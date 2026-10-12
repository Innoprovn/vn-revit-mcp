using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.FloorFromCad;

/// <summary>Tìm tên sàn CHO 1 vùng hatch — đọc Text Note THẬT trong Revit (KHÔNG đọc được text CAD thô
/// từ ImportInstance, giới hạn đã biết của Revit API). Người dùng phải tự explode riêng layer chứa tên
/// sàn thành Text Note thật trước khi chạy tool.
///
/// LẤY TEXT NOTE NẰM BÊN TRONG VÙNG HATCH (không còn xét theo bán kính) — kiểm tra điểm (tâm bounding
/// box Text Note, không dùng Coord vì có thể lệch nếu Text Note đã xoay) có nằm trong biên ngoài của
/// hatch VÀ không rơi vào lỗ khoét bên trong (nếu hatch có "island") hay không, bằng thuật toán ray-
/// casting chuẩn trên đa giác xấp xỉ (Tessellate mỗi Curve của loop). Nếu CÓ NHIỀU Text Note cùng nằm
/// trong 1 vùng hatch (hiếm — ký hiệu vật liệu/ghi chú khác lẫn vào) thì lấy Text Note GẦN TÂM vùng hatch
/// NHẤT.</summary>
internal static class TextTagReader
{
    public static string? FindTagNameInside(RawHatchRegion region, IReadOnlyList<TextNote> textNotes)
    {
        string? best = null;
        var bestDistance = double.MaxValue;

        foreach (var note in textNotes)
        {
            var text = note.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            var box = note.get_BoundingBox(null);
            var center = box is null
                ? note.Coord
                : new XYZ((box.Min.X + box.Max.X) / 2.0, (box.Min.Y + box.Max.Y) / 2.0, 0);
            var flatCenter = new XYZ(center.X, center.Y, 0);

            if (!IsPointInsideRegion(flatCenter, region))
                continue;

            var distance = flatCenter.DistanceTo(region.Center);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = text;
        }

        return best;
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
