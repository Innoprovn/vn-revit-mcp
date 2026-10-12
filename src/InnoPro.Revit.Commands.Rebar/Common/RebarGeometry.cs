namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

/// <summary>
/// Đọc đúng centerline của TỪNG VỊ TRÍ trong một Rebar (kể cả rebar set nhiều thanh).
///
/// Với thép shape-driven, GetCenterlineCurves(barPositionIndex: i) luôn trả hình học của vị trí 0:
/// vị trí thật chỉ có được bằng RebarShapeDrivenAccessor.GetBarPositionTransform(i) áp lên hình của
/// vị trí 0, cộng thêm GetMovedBarTransform(i) nếu thanh đã bị "Edit Bars" kéo riêng (kể cả vị trí 0).
/// Thép Free Form thì GetCenterlineCurves(barPositionIndex) đúng như tên gọi.
/// Mọi hàm trả null / danh sách rỗng thay vì ném lỗi: một thanh hỏng không được làm chết cả lô.
/// </summary>
internal static class RebarGeometry
{
    /// <summary>Trần số vị trí quét trong một set, tránh treo khi set bất thường hàng nghìn vị trí.</summary>
    public const int MaxPositions = 400;

    public static IList<Curve>? PositionCurves(Rebar rebar, int index)
    {
        try
        {
            if (index < 0 || index >= rebar.NumberOfBarPositions || !rebar.DoesBarExistAtPosition(index))
                return null;

            var accessor = rebar.IsRebarShapeDriven() ? rebar.GetShapeDrivenAccessor() : null;
            if (accessor == null)
                return rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, index);

            var baseCurves = rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
            if (baseCurves == null || baseCurves.Count == 0) return null;

            var layout = accessor.GetBarPositionTransform(index);
            Transform? moved = null;
            try
            {
                var t = rebar.GetMovedBarTransform(index);
                if (t != null && !t.IsIdentity) moved = t;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Không hỗ trợ dịch riêng thanh: coi như chưa bị dịch.
            }

            var result = new List<Curve>(baseCurves.Count);
            foreach (var c in baseCurves)
            {
                var positioned = c.CreateTransformed(layout);
                result.Add(moved == null ? positioned : positioned.CreateTransformed(moved));
            }
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Các vị trí thanh đang tồn tại trong set (bỏ vị trí bị loại bằng IncludeFirstBar/SetBarIncluded).</summary>
    public static List<int> ExistingPositions(Rebar rebar)
    {
        var result = new List<int>();
        int count;
        try { count = rebar.NumberOfBarPositions; }
        catch (Exception) { return result; }

        for (var i = 0; i < Math.Min(count, MaxPositions); i++)
        {
            try { if (rebar.DoesBarExistAtPosition(i)) result.Add(i); }
            catch (Exception) { /* bỏ qua vị trí lỗi */ }
        }
        return result;
    }

    /// <summary>Điểm centerline. index null = quét mọi vị trí trong set.</summary>
    public static List<XYZ> Points(Rebar rebar, int? index)
    {
        if (index is { } i) return Tessellate(PositionCurves(rebar, i));

        var points = new List<XYZ>();
        foreach (var p in ExistingPositions(rebar))
            points.AddRange(Tessellate(PositionCurves(rebar, p)));
        if (points.Count == 0)
            points.AddRange(Tessellate(PositionCurves(rebar, 0)));
        return points;
    }

    /// <summary>Băm curve ra điểm (giữ cung uốn của móc), curve hỏng thì lấy hai đầu mút.</summary>
    public static List<XYZ> Tessellate(IList<Curve>? curves)
    {
        var points = new List<XYZ>();
        if (curves == null) return points;

        foreach (var curve in curves)
        {
            try
            {
                var t = curve.Tessellate();
                if (t is { Count: > 0 })
                {
                    points.AddRange(t);
                    continue;
                }
            }
            catch (Exception) { /* rơi xuống hai đầu mút */ }

            try
            {
                points.Add(curve.GetEndPoint(0));
                points.Add(curve.GetEndPoint(1));
            }
            catch (Exception) { /* curve hỏng */ }
        }
        return points;
    }

    /// <summary>
    /// Chụp hình học để so trước/sau: đầu, giữa, cuối của từng đoạn centerline ở vị trí 0, hoặc
    /// bounding box nếu không đọc được. Null nghĩa là không kiểm chứng được (không phải "đứng yên").
    /// </summary>
    public static List<XYZ>? Capture(Rebar rebar)
    {
        try
        {
            var curves = rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
            if (curves is { Count: > 0 })
            {
                var points = new List<XYZ>(curves.Count * 3);
                foreach (var c in curves)
                {
                    points.Add(c.GetEndPoint(0));
                    points.Add(c.GetEndPoint(1));
                    try { points.Add(c.Evaluate(0.5, true)); }
                    catch (Exception) { /* hai đầu mút là đủ */ }
                }
                if (points.Count > 0) return points;
            }
        }
        catch (Exception) { /* thử bounding box */ }

        try
        {
            var box = rebar.get_BoundingBox(null);
            if (box != null) return [box.Min, box.Max];
        }
        catch (Exception) { /* không đọc được */ }

        return null;
    }

    /// <summary>Độ lệch lớn nhất (mm) giữa hai lần chụp; -1 nếu không so được.</summary>
    public static double MaxDeviationMm(List<XYZ>? before, List<XYZ>? after)
    {
        if (before == null || after == null || before.Count == 0 || before.Count != after.Count) return -1;
        var max = 0.0;
        for (var i = 0; i < before.Count; i++) max = Math.Max(max, before[i].DistanceTo(after[i]));
        return max * 304.8;
    }

    public static List<XYZ>? Shift(List<XYZ>? points, XYZ translation) =>
        points?.Select(p => p + translation).ToList();

    /// <summary>Vị trí thanh gần một điểm nhất (dùng khi người dùng click vào một thanh trong set).</summary>
    public static int ClosestPosition(Rebar rebar, XYZ? point)
    {
        if (point == null || rebar.NumberOfBarPositions <= 1) return 0;
        var best = 0;
        var bestDistance = double.MaxValue;
        foreach (var i in ExistingPositions(rebar))
        {
            var curves = PositionCurves(rebar, i);
            if (curves == null) continue;
            foreach (var c in curves)
            {
                double d;
                try { d = c.Project(point)?.Distance ?? double.MaxValue; }
                catch (Exception) { d = double.MaxValue; }
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
        }
        return best;
    }

    public static (XYZ Min, XYZ Max)? Bounds(IEnumerable<XYZ> points)
    {
        XYZ? min = null, max = null;
        foreach (var p in points)
        {
            min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }
        return min == null ? null : (min, max!);
    }
}
