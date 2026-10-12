using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.FloorFromCad;

/// <summary>Tìm tên sàn cho 1 vùng hatch: chữ (đọc từ file CAD hoặc Text Note) nằm trong biên ngoài và không rơi
/// vào lỗ khoét (ray-casting trên đa giác xấp xỉ), hoặc chữ có leader chỉ vào vùng.</summary>
internal static class TextTagReader
{
    public static string? FindTagNameInside(RawHatchRegion region, IReadOnlyList<Common.TextTag> tags)
    {
        // Leader chỉ vào vùng hatch là chắc chắn; không có thì chữ nằm trong vùng, gần tâm vùng nhất.
        var byLeader = tags.FirstOrDefault(t => t.LeaderTips.Any(p => IsPointInsideRegion(p, region)));
        if (byLeader != null) return byLeader.Text;
        return tags.Where(t => t.LeaderTips.Count == 0 && IsPointInsideRegion(t.Center, region))
            .OrderBy(t => t.Center.DistanceTo(region.Center)).FirstOrDefault()?.Text;
    }

    private static bool IsPointInsideRegion(XYZ point, RawHatchRegion region)
    {
        if (!IsPointInLoop(point, region.OuterLoop))
            return false;

        foreach (var hole in region.InnerLoops)
        {
            if (IsPointInLoop(point, hole))
                return false;
        }

        return true;
    }

    private static bool IsPointInLoop(XYZ point, CurveLoop loop)
    {
        var polygon = new List<XYZ>();
        foreach (var curve in loop)
            polygon.AddRange(curve.Tessellate());

        return IsPointInPolygonXY(point, polygon);
    }

    /// <summary>Ray-casting chuẩn (even-odd rule) trên đa giác XY — đủ dùng cho biên hatch xấp xỉ bằng
    /// Tessellate (điểm dày đặc trên cả Arc), không cần chính xác tuyệt đối tới từng micromet.</summary>
    private static bool IsPointInPolygonXY(XYZ point, List<XYZ> polygon)
    {
        var inside = false;
        var count = polygon.Count;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];

            if (pi.Y > point.Y != pj.Y > point.Y &&
                point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
