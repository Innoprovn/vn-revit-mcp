namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

internal static class HostLinkSchema
{
    public const string Common = """
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép hoặc host; trống = theo scope." },
            "scope": { "type": "string", "enum": ["active_view", "model"], "description": "Mặc định active_view." }
        """;

    /// <summary>Tách thép shape-driven xử lý được và đếm loại bỏ qua.</summary>
    public static (List<Rebar> ShapeDriven, int FreeForm, int SystemBars) Split(RebarScopeResult scope)
    {
        var rebars = scope.ShapeRebars.ToList();
        return (rebars.Where(r => r.IsRebarShapeDriven()).ToList(),
                rebars.Count(r => !r.IsRebarShapeDriven()),
                scope.Elements.Count - rebars.Count);
    }
}

public sealed class RebarDetachHost : RevitCommandBase
{
    public override string Name => "rebar_detach_host";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Ngắt host", "detach");
    public override string Description =>
        "Ngắt liên kết thép với host (chuyển ràng buộc sang ToSurface) mà thép đứng yên; thanh nào bị dịch thì tự hoàn tác.";
    public override string InputSchema => "{ \"type\": \"object\", \"properties\": {" + HostLinkSchema.Common + "} }";

    // Một cú bấm: dùng vùng chọn hiện tại, không chọn gì thì xử lý thép trong view.
    public override object? PromptArguments(RevitCommandContext context) =>
        new { element_ids = RebarScope.SelectedIds(context), scope = "active_view" };

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var scope = RebarScope.Resolve(context, arguments, "active_view");
        var (shapeDriven, freeForm, systemBars) = HostLinkSchema.Split(scope);

        var already = shapeDriven.Where(r => RebarConstraintOps.ReadState(r) == RebarLinkState.Detached).Select(r => r.Id).ToHashSet();
        var targets = shapeDriven.Where(r => !already.Contains(r.Id)).Select(r => r.Id).ToList();
        if (targets.Count == 0)
            throw new CommandException(scope.Elements.Count == 0
                ? $"Không có thép nào trong {scope.Scope}."
                : $"Không có thanh nào cần ngắt trong {scope.Scope} (đã ngắt: {already.Count}, Free Form: {freeForm}, thép hệ thống: {systemBars}).");

        var outcome = RebarConstraintOps.DetachSafely(doc, targets);
        if (outcome.Detached.Count == 0)
            throw new CommandException("Không ngắt được thanh nào, mô hình giữ nguyên. " +
                                       (outcome.FirstFailure.Length > 0 ? outcome.FirstFailure : "Thanh bị dịch hoặc không đo được hình học."));

        return new
        {
            scope = scope.Scope,
            detached = outcome.Detached.Count,
            max_deviation_mm = Math.Round(outcome.MaxDeviationMm, 3),
            careful_mode = outcome.Careful,
            skipped_moved_ids = outcome.SkippedMoved,
            skipped_unmeasurable_ids = outcome.SkippedUnmeasurable,
            failed_ids = outcome.Failed,
            first_failure = outcome.FirstFailure,
            unverified_ids = outcome.Unverified,
            already_detached = already.Count,
            skipped_free_form = freeForm,
            skipped_system_bars = systemBars,
        };
    }
}

public sealed class RebarReattachHost : RevitCommandBase
{
    public override string Name => "rebar_reattach_host";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Nối lại liên kết host cho thép đã ngắt (Revit tự gán lại ràng buộc, thép có thể dịch nếu host đã đổi); tùy chọn đổi sang host khác.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
        """ + HostLinkSchema.Common + """
        ,
            "host_id": { "type": "integer", "description": "Host mới (tùy chọn)." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        UiHelpers.ConfirmOrCancel("Nối lại host", "Nối lại liên kết host cho thép?",
            "Phạm vi: thép đang chọn, không chọn thì cả view. Revit tự quét lại mặt host: nếu host đã đổi, thép sẽ dịch theo.");
        return new { element_ids = RebarScope.SelectedIds(context), scope = "active_view" };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var scope = RebarScope.Resolve(context, arguments, "active_view");
        var (shapeDriven, freeForm, systemBars) = HostLinkSchema.Split(scope);

        Element? newHost = null;
        if (Args.Has(arguments, "host_id"))
        {
            newHost = doc.GetElement(new ElementId(Args.GetLong(arguments, "host_id")))
                      ?? throw new CommandException("Không tìm thấy host_id.");
            if (!RebarHostData.IsValidHost(newHost))
                throw new CommandException($"Phần tử #{newHost.Id.Value} ({newHost.Category?.Name}) không làm host cho thép được.");
        }

        var before = new Dictionary<ElementId, List<XYZ>?>();
        var touched = new List<Rebar>();
        var failed = new List<long>();
        var firstFailure = "";
        var rehosted = 0;
        var stillAttached = 0;

        foreach (var rebar in shapeDriven)
        {
            var detached = RebarConstraintOps.ReadState(rebar) == RebarLinkState.Detached;
            if (!detached && newHost == null)
            {
                stillAttached++;
                continue;
            }

            before[rebar.Id] = RebarGeometry.Capture(rebar);
            if (newHost != null && rebar.GetHostId() != newHost.Id)
            {
                try
                {
                    rebar.SetHostId(doc, newHost.Id);
                    rehosted++;
                }
                catch (Exception ex)
                {
                    failed.Add(rebar.Id.Value);
                    if (firstFailure.Length == 0) firstFailure = $"#{rebar.Id.Value}: {ex.Message}";
                    continue;
                }
            }

            if (detached && !RebarConstraintOps.TryReattach(rebar, out var problem))
            {
                failed.Add(rebar.Id.Value);
                if (firstFailure.Length == 0) firstFailure = $"#{rebar.Id.Value}: {problem}";
                continue;
            }
            touched.Add(rebar);
        }

        if (touched.Count == 0)
            throw new CommandException(firstFailure.Length > 0
                ? "Không nối lại được thanh nào. " + firstFailure
                : $"Không có thanh nào đang ở trạng thái đã ngắt trong {scope.Scope} (đang bám host: {stillAttached}).");

        doc.Regenerate();

        var moved = new List<long>();
        var unverified = new List<long>();
        var maxDeviation = 0.0;
        foreach (var rebar in touched)
        {
            var deviation = RebarGeometry.MaxDeviationMm(before[rebar.Id], RebarGeometry.Capture(rebar));
            if (deviation < 0) unverified.Add(rebar.Id.Value);
            else
            {
                if (deviation > RebarConstraintOps.MoveToleranceMm) moved.Add(rebar.Id.Value);
                maxDeviation = Math.Max(maxDeviation, deviation);
            }
        }

        return new
        {
            scope = scope.Scope,
            reattached = touched.Count,
            rehosted,
            moved_ids = moved,
            max_deviation_mm = Math.Round(maxDeviation, 2),
            unverified_ids = unverified,
            failed_ids = failed,
            first_failure = firstFailure,
            still_attached = stillAttached,
            skipped_free_form = freeForm,
            skipped_system_bars = systemBars,
        };
    }
}
