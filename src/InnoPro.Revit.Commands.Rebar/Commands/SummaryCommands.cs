namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

public sealed class RebarSummary : RevitCommandBase
{
    public override string Name => "rebar_summary";
    public override string Category => "Rebar";
    public override bool Pinned => true;
    public override RibbonButton? Ribbon => new("Thống kê", "Σ");
    public override string Description =>
        "Thống kê cốt thép theo host, đường kính, bar type, shape, partition hoặc tham số: số thanh, chiều dài, khối lượng. Chỉ đọc.";
    public override string InputSchema => $$"""
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép hoặc host; trống = theo scope." },
            "scope": { "type": "string", "enum": ["model", "active_view"], "description": "Phạm vi khi không chọn, mặc định model." },
            "group_by": { "type": "array", "items": { "type": "string", "enum": [{{RebarGrouping.ModesJson}}] }, "description": "Tiêu chí gom, mặc định diameter." },
            "parameter_name": { "type": "string", "description": "Tên tham số khi gom theo parameter." },
            "density_kg_m3": { "type": "number", "description": "Mặc định 7850." },
            "ids_per_group": { "type": "integer", "description": "Số id mẫu mỗi nhóm, mặc định 10." },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var scope = RebarScope.Resolve(context, arguments, "model");
        var groupBy = (ArgsX.Has(arguments, "group_by") ? Args.GetStringsOrEmpty(arguments, "group_by") : ["diameter"])
            .Select(RebarGrouping.Normalize).Distinct().ToList();
        var parameterName = Args.GetStringOrDefault(arguments, "parameter_name");
        var density = Args.GetDoubleOrDefault(arguments, "density_kg_m3", RebarInfo.SteelDensity);
        var idsPerGroup = (int)Args.GetLongOrDefault(arguments, "ids_per_group", 10);
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);

        var bars = scope.Elements.Where(e => e is not RebarContainer).Select(e => new
        {
            Element = e,
            Keys = groupBy.Select(g => RebarGrouping.Key(doc, e, g, parameterName)).ToArray(),
            Quantity = RebarInfo.Quantity(e),
            LengthFeet = RebarInfo.TotalLengthFeet(e),
            WeightKg = RebarInfo.WeightKg(e, density),
            Host = RebarInfo.HostId(e),
        }).ToList();

        var groups = bars
            .GroupBy(b => string.Join("\u001F", b.Keys))
            .Select(g =>
            {
                var perBar = g.Where(b => b.Quantity > 0).Select(b => UnitConvert.FeetToMm(b.LengthFeet / b.Quantity)).ToList();
                return new
                {
                    Keys = g.First().Keys,
                    Elements = g.Count(),
                    Bars = g.Sum(b => b.Quantity),
                    Hosts = g.Select(b => b.Host).Where(h => h != ElementId.InvalidElementId).Distinct().Count(),
                    LengthM = Math.Round(g.Sum(b => b.LengthFeet) * 0.3048, 2),
                    WeightKg = Math.Round(g.Sum(b => b.WeightKg), 2),
                    BarLength = perBar.Count == 0 ? "" : perBar.Max() - perBar.Min() < 1 ? $"{perBar.Min():F0}" : $"{perBar.Min():F0}-{perBar.Max():F0}",
                    Ids = g.Select(b => b.Element.Id.Value).Take(Math.Max(0, idsPerGroup)).ToList(),
                };
            })
            .ToList();

        var ordered = groupBy.Count == 0 ? groups : RebarGrouping.Order(groups, g => g.Keys[0]).ToList();
        for (var i = 1; i < groupBy.Count; i++)
        {
            var index = i;
            ordered = ordered.GroupBy(g => string.Join("\u001F", g.Keys.Take(index)))
                .SelectMany(block => RebarGrouping.Order(block, g => g.Keys[index]))
                .ToList();
        }

        var columns = groupBy.Select(g => g == "parameter" ? $"parameter:{parameterName}" : g).ToList();
        columns.AddRange(["rebar_elements", "bars", "hosts", "total_length_m", "weight_kg", "bar_length_mm"]);
        if (idsPerGroup > 0) columns.Add("element_ids");

        var rows = ordered.Take(limit).Select(g =>
        {
            var row = new List<object?>(g.Keys);
            row.AddRange([g.Elements, g.Bars, g.Hosts, g.LengthM, g.WeightKg, g.BarLength]);
            if (idsPerGroup > 0) row.Add(g.Ids);
            return row.ToArray();
        }).ToList();

        return new
        {
            scope = scope.Scope,
            rebar_elements = bars.Count,
            rebar_containers = scope.Elements.Count(e => e is RebarContainer),
            bars = bars.Sum(b => b.Quantity),
            total_length_m = Math.Round(bars.Sum(b => b.LengthFeet) * 0.3048, 2),
            total_weight_kg = Math.Round(bars.Sum(b => b.WeightKg), 2),
            ignored_ids = scope.IgnoredIds,
            groups = new Table { Columns = columns, Rows = rows, Total = ordered.Count > limit ? ordered.Count : null },
            note = "Khối lượng = Volume của Revit x khối lượng riêng; chiều dài thanh = tổng chiều dài / số thanh.",
        };
    }
}

public sealed class RebarQuickSelect : RevitCommandBase
{
    public override string Name => "rebar_quick_select";
    public override string Category => "Rebar";
    public override RibbonButton? Ribbon => new("Chọn nhanh", "⊙");
    public override string Description =>
        "Chọn mọi thanh thép cùng nhóm (partition, tham số, bar type, shape, host...); không truyền giá trị thì liệt kê các nhóm.";
    public override string InputSchema => $$"""
        {
          "type": "object",
          "properties": {
            "group_by": { "type": "string", "enum": [{{RebarGrouping.ModesJson}}], "description": "Mặc định partition." },
            "parameter_name": { "type": "string", "description": "Tên tham số khi gom theo parameter." },
            "values": { "type": "array", "items": { "type": "string" }, "description": "Giá trị nhóm cần chọn." },
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thanh mẫu: chọn thanh cùng nhóm." },
            "scope": { "type": "string", "enum": ["model", "active_view"], "description": "Mặc định model." },
            "select": { "type": "boolean", "description": "Đặt vùng chọn, mặc định true." },
            "limit": { "type": "integer", "description": "Mặc định 500." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var mode = RebarGrouping.Normalize(Args.GetStringOrDefault(arguments, "group_by", "partition")!);
        var parameterName = Args.GetStringOrDefault(arguments, "parameter_name");
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 500);

        var scopeName = Args.GetStringOrDefault(arguments, "scope", "model")!.Trim().ToLowerInvariant();
        var pool = (scopeName is "active_view" or "view"
                ? RebarScope.CollectInView(doc, RebarScope.ActiveModelView(context))
                : RebarScope.CollectInModel(doc))
            .DistinctBy(e => e.Id)
            .Select(e => (Element: e, Key: RebarGrouping.Key(doc, e, mode, parameterName)))
            .ToList();

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (Args.Has(arguments, "values"))
            foreach (var v in Args.GetStringsOrEmpty(arguments, "values")) wanted.Add(v.Trim());
        if (Args.Has(arguments, "element_ids"))
            foreach (var id in Args.GetElementIds(arguments, "element_ids"))
                if (doc.GetElement(id) is { } e && RebarScope.IsRebarElement(e))
                    wanted.Add(RebarGrouping.Key(doc, e, mode, parameterName));

        var groups = RebarGrouping.Order(pool.GroupBy(p => p.Key, StringComparer.Ordinal), g => g.Key)
            .Select(g => new { value = g.Key, count = g.Count(), picked = wanted.Contains(g.Key) })
            .ToList();

        if (wanted.Count == 0)
        {
            return new
            {
                group_by = mode,
                scope = scopeName,
                selected = 0,
                groups = Table.Of(groups.Select(g => new { g.value, g.count }), 300),
                hint = "Truyền 'values' (giá trị nhóm) hoặc 'element_ids' (thanh mẫu) để chọn.",
            };
        }

        var ids = pool.Where(p => wanted.Contains(p.Key)).Select(p => p.Element.Id).ToList();
        if (ids.Count > 0 && Args.GetBoolOrDefault(arguments, "select", true))
            context.UIDocument.Selection.SetElementIds(ids);

        return new
        {
            group_by = mode,
            scope = scopeName,
            selected = ids.Count,
            values = wanted.ToList(),
            missing_values = wanted.Where(w => groups.All(g => g.value != w)).ToList(),
            element_ids = ids.Take(limit).Select(id => id.Value),
            groups = Table.Of(groups.Where(g => g.picked).Select(g => new { g.value, g.count })),
        };
    }
}
