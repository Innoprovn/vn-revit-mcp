namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Commands.Rebar.Qa;
using InnoPro.Revit.Sdk;

public sealed class RebarQaCheck : RevitCommandBase
{
    public override string Name => "rebar_qa_check";
    public override string Category => "Rebar";
    public override RibbonButton? Ribbon => new("QA thép", "check");
    public override string Description =>
        "Kiểm tra thép: thanh chồng lên nhau (khoảng cách tâm nhỏ hơn tổng bán kính) và thanh nằm ngoài bê tông, theo từng thanh trong set.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép hoặc host; trống = toàn mô hình." },
            "checks": { "type": "array", "items": { "type": "string", "enum": ["clash", "outside_concrete"] }, "description": "Mặc định cả hai." },
            "clash_tolerance_mm": { "type": "number", "description": "Dung sai va chạm, mặc định 0." },
            "min_inside_percent": { "type": "number", "description": "% chiều dài tối thiểu trong bê tông, mặc định 100." },
            "isolated_shape_groups": { "type": "array", "items": { "type": "array", "items": { "type": "string" } }, "description": "Nhóm shape không xét va chạm với nhau." },
            "min_severity": { "type": "string", "enum": ["info", "warning", "fail"], "description": "Mặc định warning." },
            "select_result": { "type": "boolean", "description": "Chọn thép lỗi trong Revit." },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context) =>
        new { element_ids = RebarScope.SelectedIds(context), select_result = true };

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var checks = Args.GetStringsOrEmpty(arguments, "checks").Select(c => c.ToLowerInvariant()).ToHashSet();
        if (checks.Count == 0) checks = ["clash", "outside_concrete"];

        var scopeIds = ArgsX.Has(arguments, "element_ids") ? Args.GetElementIds(arguments, "element_ids").ToList() : [];
        var config = new RuleConfig();
        config.ClashRebarRebar.ToleranceMm = ArgsX.Double(arguments, "clash_tolerance_mm") ?? 0;
        config.BarOutsideHost.MinInsidePercent = ArgsX.Double(arguments, "min_inside_percent") ?? 100;
        if (ArgsX.Has(arguments, "isolated_shape_groups"))
            config.ClashRebarRebar.ShapeCodeIsolatedGroups = arguments.GetProperty("isolated_shape_groups").EnumerateArray()
                .Select(g => g.EnumerateArray().Select(x => x.ToString()).ToList()).ToList();

        var bars = new RebarExtractor().ExtractFromDocument(doc, scopeIds, checks.Contains("outside_concrete"));
        var violations = new List<RuleViolation>();
        if (checks.Contains("clash")) violations.AddRange(new ClashRebarRebarCheck().Run(bars, config));
        if (checks.Contains("outside_concrete")) violations.AddRange(new BarOutsideHostCheck().Run(bars, config));

        var minSeverity = (ArgsX.String(arguments, "min_severity") ?? "warning").ToLowerInvariant() switch
        {
            "info" => Severity.Info,
            "fail" => Severity.Fail,
            _ => Severity.Warning,
        };
        var shown = violations.Where(v => v.Severity >= minSeverity)
            .OrderByDescending(v => v.Severity).ThenBy(v => v.RuleName).ThenBy(v => v.ElementId).ToList();

        var ids = shown.SelectMany(v => v.OtherElementId is { } o ? new[] { v.ElementId, o } : [v.ElementId]).Distinct().ToList();
        if (ids.Count > 0 && Args.GetBoolOrDefault(arguments, "select_result", false))
            context.UIDocument.Selection.SetElementIds(ids.Select(i => new ElementId(i)).ToList());

        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);
        return new
        {
            scope = scopeIds.Count > 0 ? $"{scopeIds.Count} phần tử chỉ định" : "toàn mô hình",
            bars_checked = bars.Count,
            issues = shown.Count,
            hidden_below_severity = violations.Count - shown.Count,
            by_rule = shown.GroupBy(v => (v.RuleName, v.Severity)).Select(g => new { rule = g.Key.RuleName, severity = g.Key.Severity.ToString(), count = g.Count() }),
            element_ids = ids.Take(500),
            violations = Table.Of(shown.Select(v => new
            {
                element_id = v.ElementId,
                bar_index = v.BarPositionIndex,
                mark = v.Mark,
                rule = v.RuleName,
                value = v.ModelValue,
                required = v.RequiredValue,
                severity = v.Severity.ToString(),
                other_id = v.OtherElementId,
                location_mm = v.Location is { } p ? new[] { Math.Round(p.X), Math.Round(p.Y), Math.Round(p.Z) } : null,
            }), limit),
        };
    }
}

public sealed class RebarChangeCheck : RevitCommandBase
{
    // Mốc so sánh sống suốt phiên Revit, theo từng tài liệu.
    private static readonly List<(Document Doc, Dictionary<long, RebarShapeSnapshotEntry> Snapshot)> Baselines = [];

    public override string Name => "rebar_change_check";
    public override string Category => "Rebar";
    public override string Description =>
        "Phát hiện kích thước đoạn (A, B, C...) của thép bị đổi ngoài ý muốn: lần đầu lưu mốc, các lần sau so với mốc và báo thanh thêm/sửa/xóa.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép hoặc host; trống = theo scope." },
            "scope": { "type": "string", "enum": ["active_view", "model"], "description": "Mặc định active_view." },
            "reset": { "type": "boolean", "description": "Xóa mốc cũ, lưu mốc mới." },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context) =>
        new { element_ids = RebarScope.SelectedIds(context), scope = "active_view" };

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var index = Baselines.FindIndex(b => b.Doc.IsValidObject && b.Doc.Equals(doc));
        if (Args.GetBoolOrDefault(arguments, "reset", false) && index >= 0)
        {
            Baselines.RemoveAt(index);
            index = -1;
        }
        Baselines.RemoveAll(b => !b.Doc.IsValidObject);
        index = Baselines.FindIndex(b => b.Doc.Equals(doc));

        var scope = RebarScope.Resolve(context, arguments, "active_view");
        var tracked = scope.ShapeRebars.Select(r => r.Id).ToHashSet();
        // Đọc lại cả các thanh đã có trong mốc: sửa một thanh có thể làm đổi thanh khác cùng shape.
        if (index >= 0)
            foreach (var id in Baselines[index].Snapshot.Keys) tracked.Add(new ElementId(id));

        var snapshot = new Dictionary<long, RebarShapeSnapshotEntry>();
        foreach (var id in tracked)
            if (doc.GetElement(id) is Rebar rebar && RebarShapeSnapshotReader.Read(rebar, doc) is { } entry)
                snapshot[entry.ElementId] = entry;

        if (index < 0)
        {
            Baselines.Add((doc, snapshot));
            return new { baseline_saved = snapshot.Count, scope = scope.Scope, message = "Đã lưu mốc. Gọi lại sau khi sửa thép để so sánh." };
        }

        var diff = RebarChangeDiff.Compute(Baselines[index].Snapshot, snapshot);
        Baselines[index] = (doc, snapshot);
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);

        return new
        {
            changed = !diff.IsEmpty,
            added = diff.Added.Count,
            modified = diff.Modified.Count,
            deleted = diff.Deleted.Count,
            changes = Table.Of(
                diff.Modified.SelectMany(m => m.Changes.Select(c => new
                {
                    change = "sửa",
                    element_id = m.Entry.ElementId,
                    mark = m.Entry.Mark,
                    host = m.Entry.HostElementName,
                    shape = m.Entry.RebarShapeTypeName,
                    segment = c.Label,
                    old_mm = c.OldValueMm is { } o ? Math.Round(o, 1) : (double?)null,
                    new_mm = c.NewValueMm is { } n ? Math.Round(n, 1) : (double?)null,
                    note = c.Note,
                }))
                .Concat(diff.Added.Select(e => new { change = "thêm", element_id = e.ElementId, mark = e.Mark, host = e.HostElementName, shape = e.RebarShapeTypeName, segment = "", old_mm = (double?)null, new_mm = (double?)null, note = (string?)null }))
                .Concat(diff.Deleted.Select(e => new { change = "xóa", element_id = e.ElementId, mark = e.Mark, host = e.HostElementName, shape = e.RebarShapeTypeName, segment = "", old_mm = (double?)null, new_mm = (double?)null, note = (string?)null })),
                limit),
            note = "Mốc đã cập nhật thành trạng thái hiện tại.",
        };
    }
}
