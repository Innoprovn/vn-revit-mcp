namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

/// <summary>Một thanh/set cần dịch. BarIndex null = dịch cả set (MoveElement), có giá trị = chỉ thanh đó (MoveBarInSet).</summary>
internal sealed record MoveTarget(ElementId RebarId, int? BarIndex, XYZ Translation);

internal sealed class MoveOutcome
{
    public int Applied { get; set; }
    public List<long> Failed { get; } = [];
    public string FirstProblem { get; set; } = "";

    /// <summary>Lệch giữa vị trí thực sau khi dịch và vị trí yêu cầu (mm). Lớn = Revit kéo thép về do còn bám host.</summary>
    public double MaxDriftMm { get; set; }

    public List<long> Drifted { get; } = [];
    public List<long> Unverified { get; } = [];
}

/// <summary>Dịch thép trong Transaction đang mở và tự kiểm chứng: MoveElement/MoveBarInSet không báo lỗi khi Revit kéo thép về.</summary>
internal static class MoveOps
{
    public static MoveOutcome Apply(Document doc, IReadOnlyList<MoveTarget> targets)
    {
        var outcome = new MoveOutcome();
        foreach (var target in targets)
        {
            if (target.Translation.GetLength() < 1e-12) continue;
            if (doc.GetElement(target.RebarId) is not Rebar rebar)
            {
                Fail(outcome, target, "không còn tồn tại");
                continue;
            }

            var before = Capture(rebar, target.BarIndex);
            try
            {
                if (target.BarIndex is { } index)
                    rebar.MoveBarInSet(index, Transform.CreateTranslation(target.Translation));
                else
                    ElementTransformUtils.MoveElement(doc, rebar.Id, target.Translation);
            }
            catch (Exception ex)
            {
                Fail(outcome, target, ex.Message);
                continue;
            }

            // Regenerate sau từng thanh để thanh sau đọc đúng trạng thái thật.
            doc.Regenerate();
            var after = doc.GetElement(target.RebarId) is Rebar refreshed ? Capture(refreshed, target.BarIndex) : null;
            var drift = RebarGeometry.MaxDeviationMm(RebarGeometry.Shift(before, target.Translation), after);
            if (drift < 0) outcome.Unverified.Add(target.RebarId.Value);
            else
            {
                outcome.MaxDriftMm = Math.Max(outcome.MaxDriftMm, drift);
                if (drift > RebarConstraintOps.MoveToleranceMm) outcome.Drifted.Add(target.RebarId.Value);
            }
            outcome.Applied++;
        }
        return outcome;
    }

    private static List<XYZ>? Capture(Rebar rebar, int? index)
    {
        var points = RebarGeometry.Points(rebar, index ?? RebarGeometry.ExistingPositions(rebar).FirstOrDefault());
        return points.Count == 0 ? null : points;
    }

    private static void Fail(MoveOutcome outcome, MoveTarget target, string problem)
    {
        outcome.Failed.Add(target.RebarId.Value);
        if (outcome.FirstProblem.Length == 0)
            outcome.FirstProblem = $"#{target.RebarId.Value}{(target.BarIndex is { } i ? $" vị trí {i}" : "")}: {problem}";
    }

    /// <summary>Ngắt host (an toàn) cho các thanh còn bám, trước khi dịch; trả số thanh đã ngắt.</summary>
    public static DetachOutcome? DetachAttached(Document doc, IEnumerable<ElementId> rebarIds)
    {
        var attached = rebarIds.Distinct()
            .Select(doc.GetElement).OfType<Rebar>()
            .Where(r => r.IsRebarShapeDriven() && RebarConstraintOps.ReadState(r) == RebarLinkState.Attached)
            .Select(r => r.Id).ToList();
        return attached.Count == 0 ? null : RebarConstraintOps.DetachSafely(doc, attached);
    }
}

/// <summary>Tham chiếu để canh khoảng hở: một mặt phẳng hoặc một thanh thép khác.</summary>
internal sealed class GapReference
{
    public required string Label { get; init; }
    public XYZ? PlaneOrigin { get; init; }
    public XYZ? PlaneNormal { get; init; }
    public IReadOnlyList<XYZ>? BarPoints { get; init; }
    public double BarRadius { get; init; }
    public bool IsFace => PlaneNormal != null && PlaneOrigin != null;
}

internal readonly record struct GapSolution(bool Ok, string Message, double CurrentMm, XYZ Translation)
{
    public static GapSolution Fail(string message) => new(false, message, 0, XYZ.Zero);
}

/// <summary>
/// Đo khoảng hở và tính vector dịch để đạt khoảng hở mong muốn.
/// Với mặt: đo VUÔNG GÓC mặt (đúng nghĩa lớp bảo vệ), thép vẫn dịch dọc trục đã chọn: t = Δ/(a·n).
/// Với thanh thép: đo DỌC THEO trục đã chọn.
/// </summary>
internal static class GapSolver
{
    private const double MinAxisDotNormal = 0.05;

    public static GapSolution Solve(GapReference target, IReadOnlyList<XYZ> moving, double movingRadius, XYZ axis, bool fromEdge, double? targetMm)
    {
        if (moving.Count == 0) return GapSolution.Fail("Không đọc được hình học thép cần dịch.");
        if (axis.GetLength() < 1e-9) return GapSolution.Fail("Trục dịch không hợp lệ.");
        axis = axis.Normalize();
        return target.IsFace
            ? AgainstFace(target, moving, movingRadius, axis, fromEdge, targetMm)
            : AgainstBar(target, moving, movingRadius, axis, fromEdge, targetMm);
    }

    private static GapSolution AgainstFace(GapReference target, IReadOnlyList<XYZ> moving, double radius, XYZ axis, bool fromEdge, double? targetMm)
    {
        var origin = target.PlaneOrigin!;
        var normal = target.PlaneNormal!.Normalize();
        if (moving.Average(p => (p - origin).DotProduct(normal)) < 0) normal = normal.Negate();

        var nearest = moving.Min(p => (p - origin).DotProduct(normal));
        var current = nearest - (fromEdge ? radius : 0);
        var oriented = axis.DotProduct(normal) < 0 ? axis.Negate() : axis;
        var k = oriented.DotProduct(normal);
        if (targetMm == null) return new GapSolution(true, "", current * 304.8, XYZ.Zero);
        if (k < MinAxisDotNormal)
            return GapSolution.Fail("Trục dịch gần song song mặt tham chiếu nên không đổi được khoảng hở. Chọn trục khác hoặc reference_normal.");

        var delta = targetMm.Value / 304.8 - current;
        return new GapSolution(true, "", current * 304.8, oriented * (delta / k));
    }

    private static GapSolution AgainstBar(GapReference target, IReadOnlyList<XYZ> moving, double radius, XYZ axis, bool fromEdge, double? targetMm)
    {
        var reference = target.BarPoints;
        if (reference == null || reference.Count == 0) return GapSolution.Fail("Không đọc được hình học thanh tham chiếu.");

        var oriented = moving.Average(p => p.DotProduct(axis)) < reference.Average(p => p.DotProduct(axis)) ? axis.Negate() : axis;
        var movingNear = moving.Min(p => p.DotProduct(oriented));
        var referenceFar = reference.Max(p => p.DotProduct(oriented));
        var current = movingNear - referenceFar - (fromEdge ? radius + target.BarRadius : 0);
        if (targetMm == null) return new GapSolution(true, "", current * 304.8, XYZ.Zero);

        var delta = targetMm.Value / 304.8 - current;
        return new GapSolution(true, "", current * 304.8, oriented * delta);
    }
}
