using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Docs;

/// <summary>Một trục thẳng kèm tọa độ chiếu lên phương U hoặc V của hệ lưới.</summary>
internal sealed record GridOnAxis(Grid Grid, double Coordinate);

/// <summary>
/// Hệ trục U/V thật của lưới trục: tự phát hiện góc xoay chủ đạo θ (gom cụm góc mod 90°),
/// U/V trùng X/Y khi lưới không xoay. Trục cong và trục xiên quá dung sai bị bỏ qua.
/// </summary>
internal sealed class GridAxes
{
    private const double AngleToleranceDegrees = 2.0;
    private const double DirectionTolerance = 0.9; // cos(~26°)

    private GridAxes(XYZ u, XYZ v, IReadOnlyList<GridOnAxis> alongU, IReadOnlyList<GridOnAxis> alongV)
    {
        U = u;
        V = v;
        CrossU = alongU;
        CrossV = alongV;
    }

    public XYZ U { get; }
    public XYZ V { get; }

    /// <summary>Trục chạy dọc V (cắt phương U): dùng khi đo theo phương U. Coordinate = tọa độ U.</summary>
    public IReadOnlyList<GridOnAxis> CrossU { get; }

    /// <summary>Trục chạy dọc U (cắt phương V): dùng khi đo theo phương V. Coordinate = tọa độ V.</summary>
    public IReadOnlyList<GridOnAxis> CrossV { get; }

    public static GridAxes Build(IEnumerable<Grid> grids)
    {
        var lines = grids.Where(g => !g.IsCurved && g.Curve is Line).ToList();
        var u = XYZ.BasisX;
        var v = XYZ.BasisY;
        if (lines.Count > 0)
        {
            var theta = DominantAngle(lines.Select(g => Mod(Math.Atan2(Direction(g).Y, Direction(g).X) * 180 / Math.PI, 90)).ToList());
            var rad = theta * Math.PI / 180;
            u = new XYZ(Math.Cos(rad), Math.Sin(rad), 0);
            v = new XYZ(-Math.Sin(rad), Math.Cos(rad), 0);
        }

        var crossU = new List<GridOnAxis>();
        var crossV = new List<GridOnAxis>();
        foreach (var grid in lines)
        {
            var d = Direction(grid);
            var start = ((Line)grid.Curve).GetEndPoint(0);
            if (Math.Abs(d.DotProduct(v)) >= DirectionTolerance) crossU.Add(new GridOnAxis(grid, Dot2(start, u)));
            else if (Math.Abs(d.DotProduct(u)) >= DirectionTolerance) crossV.Add(new GridOnAxis(grid, Dot2(start, v)));
        }
        return new GridAxes(u, v, crossU, crossV);
    }

    /// <summary>Trục gần tọa độ cho trước nhất trong nhóm đo theo U (alongU=true) hoặc V.</summary>
    public GridOnAxis? Nearest(bool alongU, double coordinate) =>
        (alongU ? CrossU : CrossV).OrderBy(g => Math.Abs(g.Coordinate - coordinate)).FirstOrDefault();

    public static double Dot2(XYZ p, XYZ axis) => p.X * axis.X + p.Y * axis.Y;

    public static XYZ Direction(Grid grid)
    {
        var line = (Line)grid.Curve;
        return (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
    }

    private static double DominantAngle(IReadOnlyList<double> angles)
    {
        var best = angles[0];
        var bestCount = 0;
        foreach (var candidate in angles)
        {
            var count = angles.Count(a => Distance90(a, candidate) <= AngleToleranceDegrees);
            if (count > bestCount) { bestCount = count; best = candidate; }
        }
        var shift = angles.Where(a => Distance90(a, best) <= AngleToleranceDegrees).Average(a => Mod(a - best + 45, 90) - 45);
        return Mod(best + shift, 90);
    }

    private static double Mod(double x, double m) => ((x % m) + m) % m;

    private static double Distance90(double a, double b)
    {
        var diff = Mod(a - b, 90);
        return Math.Min(diff, 90 - diff);
    }
}
