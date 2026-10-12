using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Toán học đa giác 2D (mặt phẳng XY, Z bỏ qua) dùng chung cho phân loại outer/hole, tính diện tích, tâm và zoom.</summary>
internal static class PolygonMath
{
    /// <summary>Tessellate 1 chuỗi Curve đã nối thành 1 dãy điểm liên tục (giữ cong của Arc), bỏ điểm trùng liền kề.</summary>
    public static List<XYZ> Tessellate(IEnumerable<Curve> loop)
    {
        var points = new List<XYZ>();

        foreach (var curve in loop)
        {
            List<XYZ> pts;
            try
            {
                pts = curve.Tessellate().ToList();
            }
            catch (Exception)
            {
                pts = new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };
            }

            foreach (var p in pts)
            {
                if (points.Count == 0 || points[^1].DistanceTo(p) > 1e-9)
                    points.Add(p);
            }
        }

        return points;
    }

    /// <summary>Diện tích có dấu (shoelace) — dấu cho biết chiều winding (dương = ngược chiều kim đồng hồ). points không cần khép kín (điểm cuối ≠ điểm đầu vẫn đúng).</summary>
    public static double SignedArea(IReadOnlyList<XYZ> points)
    {
        if (points.Count < 3)
            return 0;

        double area = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var p0 = points[i];
            var p1 = points[(i + 1) % points.Count];
            area += p0.X * p1.Y - p1.X * p0.Y;
        }

        return area * 0.5;
    }

    /// <summary>Diện tích tuyệt đối (shoelace) — points không cần khép kín (điểm cuối ≠ điểm đầu vẫn đúng).</summary>
    public static double AreaAbs(IReadOnlyList<XYZ> points) => Math.Abs(SignedArea(points));

    /// <summary>Centroid qua công thức shoelace, fallback trung bình cộng điểm nếu diện tích ~0.</summary>
    public static XYZ Centroid(IReadOnlyList<XYZ> points)
    {
        if (points.Count == 0)
            return XYZ.Zero;

        double area = 0, cx = 0, cy = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var p0 = points[i];
            var p1 = points[(i + 1) % points.Count];
            var cross = p0.X * p1.Y - p1.X * p0.Y;
            area += cross;
            cx += (p0.X + p1.X) * cross;
            cy += (p0.Y + p1.Y) * cross;
        }

        area *= 0.5;
        if (Math.Abs(area) < 1e-9)
        {
            var avgX = points.Average(p => p.X);
            var avgY = points.Average(p => p.Y);
            return new XYZ(avgX, avgY, 0);
        }

        return new XYZ(cx / (6 * area), cy / (6 * area), 0);
    }

    /// <summary>Ray-casting chẵn-lẻ tiêu chuẩn — polygon không cần khép kín.</summary>
    public static bool IsPointInPolygon(XYZ point, IReadOnlyList<XYZ> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (pi.Y > point.Y != pj.Y > point.Y &&
                point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }

        return inside;
    }
}
