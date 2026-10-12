namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

internal enum RebarLinkState
{
    /// <summary>Còn ít nhất một handle bám mặt/cover của host hoặc thanh khác.</summary>
    Attached,

    /// <summary>Mọi handle bám mặt phẳng tĩnh (ToSurface): host đổi không kéo thép theo.</summary>
    Detached,

    Unknown,
}

internal sealed class DetachOutcome
{
    public List<long> Detached { get; } = [];
    public List<long> SkippedMoved { get; } = [];
    public List<long> SkippedUnmeasurable { get; } = [];
    public List<long> Failed { get; } = [];
    public List<long> Unverified { get; } = [];
    public double MaxDeviationMm { get; set; }
    public bool Careful { get; set; }
    public string FirstFailure { get; set; } = "";
}

/// <summary>
/// Ràng buộc Rebar - Host. Dùng chung cho ngắt host, nối lại host, dịch thép, né va chạm.
///
/// - Không có cách làm handle của thép shape-driven "không ràng buộc": Revit luôn tự gán lại.
///   Thứ gần nhất với "ngắt" là chuyển sang RebarConstraintType.ToSurface
///   (SetPreferredConstraintsToSurfaceForHandles): lưu bản sao tọa độ mặt phẳng vào thanh thép
///   thay vì Reference sống tới mặt host, nên sửa host về sau thép đứng im.
/// - RemovePreferredConstraintFromHandle chỉ xóa lựa chọn ưu tiên, trả handle về cơ chế tự động:
///   đó là "nối lại", không phải "hoàn tác", và thép có thể nhảy nếu host đã đổi.
/// - Thép Free Form có DisconnectFromServer nhưng là một chiều, nên mọi lệnh ở đây bỏ qua Free Form.
/// - RebarInSystem / RebarContainer không có RebarConstraintsManager: chỉ đếm và báo.
/// </summary>
internal static class RebarConstraintOps
{
    /// <summary>Ngưỡng coi là thép đã nhúc nhích (mm).</summary>
    public const double MoveToleranceMm = 0.5;

    public static RebarLinkState ReadState(Rebar rebar)
    {
        try
        {
            using var mgr = rebar.GetRebarConstraintsManager();
            if (mgr == null || !mgr.HasValidRebar()) return RebarLinkState.Unknown;
            var handles = mgr.GetAllHandles();
            if (handles == null || handles.Count == 0) return RebarLinkState.Unknown;

            int toSurface = 0, toHost = 0;
            foreach (var h in handles)
            {
                var c = Current(mgr, h);
                if (c == null) continue;
                if (c.IsToSurface()) toSurface++;
                else toHost++;
            }

            if (toHost > 0) return RebarLinkState.Attached;
            return toSurface > 0 ? RebarLinkState.Detached : RebarLinkState.Unknown;
        }
        catch (Exception)
        {
            return RebarLinkState.Unknown;
        }
    }

    private static RebarConstraint? Current(RebarConstraintsManager mgr, RebarConstrainedHandle h)
    {
        try
        {
            var c = mgr.GetCurrentConstraintOnHandle(h);
            return c != null && c.IsValid() ? c : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static RebarConstraint? Preferred(RebarConstraintsManager mgr, RebarConstrainedHandle h)
    {
        try
        {
            var c = mgr.GetPreferredConstraintOnHandle(h);
            return c != null && c.IsValid() ? c : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Chuyển mọi handle đang bám host sang ToSurface; cả cụm trước, từng handle nếu bị từ chối.</summary>
    public static bool TryDetach(Rebar rebar, out string problem)
    {
        problem = "";
        try
        {
            using var mgr = rebar.GetRebarConstraintsManager();
            if (mgr == null || !mgr.HasValidRebar())
            {
                problem = "không lấy được RebarConstraintsManager";
                return false;
            }

            var candidates = new List<RebarConstrainedHandle>();
            foreach (var h in mgr.GetAllHandles())
            {
                var c = Current(mgr, h);
                if (c != null && !c.IsToSurface()) candidates.Add(h);
            }

            if (candidates.Count == 0)
            {
                problem = "không có handle nào đang bám host";
                return false;
            }

            try
            {
                mgr.SetPreferredConstraintsToSurfaceForHandles(candidates);
                return true;
            }
            catch (Exception)
            {
                // Thử từng handle bên dưới.
            }

            var done = 0;
            foreach (var h in candidates)
            {
                try
                {
                    mgr.SetPreferredConstraintsToSurfaceForHandles([h]);
                    done++;
                }
                catch (Exception)
                {
                    // Handle này Revit không cho chuyển.
                }
            }

            if (done == 0) problem = "Revit từ chối chuyển sang ràng buộc ToSurface cho mọi handle";
            return done > 0;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>Xóa ưu tiên ToSurface để Revit tự gán lại ràng buộc sống tới host.</summary>
    public static bool TryReattach(Rebar rebar, out string problem)
    {
        problem = "";
        try
        {
            using var mgr = rebar.GetRebarConstraintsManager();
            if (mgr == null || !mgr.HasValidRebar())
            {
                problem = "không lấy được RebarConstraintsManager";
                return false;
            }

            var removed = 0;
            foreach (var h in mgr.GetAllHandles())
            {
                var isSurface = Preferred(mgr, h)?.IsToSurface() == true || Current(mgr, h)?.IsToSurface() == true;
                if (!isSurface) continue;
                try
                {
                    mgr.RemovePreferredConstraintFromHandle(h);
                    removed++;
                }
                catch (Exception)
                {
                    // Không gỡ được handle này.
                }
            }

            if (removed == 0) problem = "không có handle nào đang ở trạng thái đã ngắt";
            return removed > 0;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Ngắt host cho cả lô trong Transaction đang mở mà KHÔNG để thanh nào xê dịch:
    /// lượt nhanh (một SubTransaction cho cả lô, Regenerate một lần, đo lại); có thanh bị dịch thì
    /// hủy lượt nhanh và làm lại từng thanh trong SubTransaction riêng, thanh nào dịch thì hoàn tác riêng.
    /// </summary>
    public static DetachOutcome DetachSafely(Document doc, IReadOnlyList<ElementId> targets)
    {
        return TryFastPass(doc, targets) ?? CarefulPass(doc, targets);
    }

    private static DetachOutcome? TryFastPass(Document doc, IReadOnlyList<ElementId> targets)
    {
        var outcome = new DetachOutcome();
        var before = new Dictionary<ElementId, List<XYZ>?>();
        var touched = new List<ElementId>();

        using var sub = new SubTransaction(doc);
        sub.Start();

        foreach (var id in targets)
            if (doc.GetElement(id) is Rebar r) before[id] = RebarGeometry.Capture(r);

        foreach (var id in targets)
        {
            if (doc.GetElement(id) is not Rebar r) continue;
            if (TryDetach(r, out var problem)) touched.Add(id);
            else
            {
                outcome.Failed.Add(id.Value);
                if (outcome.FirstFailure.Length == 0) outcome.FirstFailure = $"#{id.Value}: {problem}";
            }
        }

        if (touched.Count == 0)
        {
            sub.RollBack();
            return null;
        }

        doc.Regenerate();

        foreach (var id in touched)
        {
            if (doc.GetElement(id) is not Rebar r)
            {
                sub.RollBack();
                return null;
            }

            var deviation = RebarGeometry.MaxDeviationMm(before.GetValueOrDefault(id), RebarGeometry.Capture(r));
            if (deviation < 0 || deviation > MoveToleranceMm)
            {
                sub.RollBack();
                return null;
            }

            outcome.MaxDeviationMm = Math.Max(outcome.MaxDeviationMm, deviation);
            if (ReadState(r) != RebarLinkState.Detached) outcome.Unverified.Add(id.Value);
            outcome.Detached.Add(id.Value);
        }

        sub.Commit();
        return outcome;
    }

    private static DetachOutcome CarefulPass(Document doc, IReadOnlyList<ElementId> targets)
    {
        var outcome = new DetachOutcome { Careful = true };
        foreach (var id in targets)
        {
            if (doc.GetElement(id) is not Rebar rebar)
            {
                outcome.Failed.Add(id.Value);
                continue;
            }

            var before = RebarGeometry.Capture(rebar);
            using var sub = new SubTransaction(doc);
            sub.Start();

            if (!TryDetach(rebar, out var problem))
            {
                sub.RollBack();
                outcome.Failed.Add(id.Value);
                if (outcome.FirstFailure.Length == 0) outcome.FirstFailure = $"#{id.Value}: {problem}";
                continue;
            }

            doc.Regenerate();
            var after = doc.GetElement(id) is Rebar refreshed ? RebarGeometry.Capture(refreshed) : null;
            var deviation = RebarGeometry.MaxDeviationMm(before, after);

            if (deviation < 0)
            {
                sub.RollBack();
                outcome.SkippedUnmeasurable.Add(id.Value);
                continue;
            }

            if (deviation > MoveToleranceMm)
            {
                sub.RollBack();
                outcome.SkippedMoved.Add(id.Value);
                continue;
            }

            outcome.MaxDeviationMm = Math.Max(outcome.MaxDeviationMm, deviation);
            if (doc.GetElement(id) is Rebar check && ReadState(check) != RebarLinkState.Detached)
                outcome.Unverified.Add(id.Value);

            sub.Commit();
            outcome.Detached.Add(id.Value);
        }
        return outcome;
    }
}
