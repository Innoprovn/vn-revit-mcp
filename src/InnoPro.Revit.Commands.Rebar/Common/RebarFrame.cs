namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

internal enum MoveDirection { Up, Down, Left, Right, Front, Back }

/// <summary>
/// Hệ trục ĐỊA PHƯƠNG của một thanh thép, dựng từ mặt phẳng uốn của chính nó (dự án đặt theo tọa độ
/// thực nên cấu kiện nghiêng, dịch theo trục X/Y/Z project sẽ làm thép rời khỏi mặt phẳng của nó).
///
/// Quy ước (tam thuận tay phải, Right x Up = Front):
///   Front = pháp tuyến mặt phẳng uốn (với shape-driven là RebarShapeDrivenAccessor.Normal, cũng là
///           phương phân bố của rebar set).
///   Up    = một trong bốn phương {±L, ±M} nằm trong mặt phẳng thép, chọn phương đứng nhất;
///           L = hướng đoạn thẳng dài nhất, M = Front x L.
///   Right = Up x Front.
/// Mặt phẳng gần nằm ngang (|Front.Z| &gt; 0.95) thì không có "lên" thật: Up rơi về L và
/// PlaneNearlyHorizontal = true để báo cho người dùng.
/// </summary>
internal sealed class RebarFrame
{
    public required XYZ Right { get; init; }
    public required XYZ Up { get; init; }
    public required XYZ Front { get; init; }
    public required string Source { get; init; }
    public required bool PlaneNearlyHorizontal { get; init; }

    public XYZ Of(MoveDirection direction) => direction switch
    {
        MoveDirection.Up => Up,
        MoveDirection.Down => Up.Negate(),
        MoveDirection.Right => Right,
        MoveDirection.Left => Right.Negate(),
        MoveDirection.Front => Front,
        MoveDirection.Back => Front.Negate(),
        _ => XYZ.Zero,
    };

    public object Describe() => new
    {
        front = Vec(Front),
        up = Vec(Up),
        right = Vec(Right),
        up_tilt_deg = Math.Round(Math.Acos(Math.Clamp(Math.Abs(Up.DotProduct(XYZ.BasisZ)), 0.0, 1.0)) * 180.0 / Math.PI, 2),
        normal_source = Source,
        plane_nearly_horizontal = PlaneNearlyHorizontal,
    };

    private static double[] Vec(XYZ v) => [Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4)];

    /// <summary>Null nếu không đọc được hình học thanh.</summary>
    public static RebarFrame? From(Rebar rebar)
    {
        IList<Curve>? curves = null;
        foreach (var i in RebarGeometry.ExistingPositions(rebar).Take(32))
        {
            curves = RebarGeometry.PositionCurves(rebar, i);
            if (curves is { Count: > 0 }) break;
        }

        var points = RebarGeometry.Tessellate(curves);
        var (normal, source) = ResolveNormal(rebar, curves, points);
        if (normal == null) return null;

        // Cùng một hình học luôn cho cùng một dấu pháp tuyến: thành phần lớn nhất dương.
        var front = OrientDeterministic(normal);
        var l = LongestSegmentDirection(curves, front) ?? ProjectIntoPlane(XYZ.BasisZ, front) ?? AnyPerpendicular(front);
        var m = front.CrossProduct(l).Normalize();

        var up = l;
        var best = l.DotProduct(XYZ.BasisZ);
        foreach (var candidate in new[] { l.Negate(), m, m.Negate() })
        {
            var v = candidate.DotProduct(XYZ.BasisZ);
            if (v > best)
            {
                best = v;
                up = candidate;
            }
        }

        return new RebarFrame
        {
            Front = front,
            Up = up,
            Right = up.CrossProduct(front).Normalize(),
            Source = source,
            PlaneNearlyHorizontal = Math.Abs(front.DotProduct(XYZ.BasisZ)) > 0.95,
        };
    }

    private static (XYZ? Normal, string Source) ResolveNormal(Rebar rebar, IList<Curve>? curves, IReadOnlyList<XYZ> points)
    {
        if (rebar.IsRebarShapeDriven())
        {
            try
            {
                var n = rebar.GetShapeDrivenAccessor()?.Normal;
                if (n != null && n.GetLength() > 1e-9) return (n.Normalize(), "RebarShapeDrivenAccessor.Normal");
            }
            catch (Exception)
            {
                // Suy từ hình học bên dưới.
            }
        }

        var fitted = BestFitNormal(points);
        if (fitted != null) return (fitted, "khớp mặt phẳng từ centerline (Free Form)");

        // Thanh thẳng: chọn mặt phẳng đứng chứa trục thanh.
        var axis = LongestSegmentDirection(curves, null) ?? SpanDirection(points);
        if (axis == null) return (null, "");
        var normal = XYZ.BasisZ.CrossProduct(axis);
        if (normal.GetLength() < 1e-6) normal = XYZ.BasisX.CrossProduct(axis);
        return normal.GetLength() < 1e-9 ? (null, "") : (normal.Normalize(), "thanh thẳng: mặt phẳng đứng chứa trục thanh");
    }

    /// <summary>Pháp tuyến Newell; loại thanh gần thẳng (diện tích chiếu &lt; 1% span²).</summary>
    private static XYZ? BestFitNormal(IReadOnlyList<XYZ> points)
    {
        if (points.Count < 3) return null;
        double nx = 0, ny = 0, nz = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        var normal = new XYZ(nx, ny, nz);
        var bounds = RebarGeometry.Bounds(points);
        var span = bounds == null ? 0 : (bounds.Value.Max - bounds.Value.Min).GetLength();
        if (span < 1e-9 || normal.GetLength() < 0.01 * span * span) return null;
        return normal.Normalize();
    }

    private static XYZ? LongestSegmentDirection(IList<Curve>? curves, XYZ? plane)
    {
        if (curves == null) return null;
        XYZ? best = null;
        var bestLength = 0.0;
        foreach (var curve in curves)
        {
            if (curve is not Line line) continue;
            double length;
            try { length = line.Length; }
            catch (Exception) { continue; }
            if (length <= bestLength) continue;

            var direction = plane == null ? line.Direction : ProjectIntoPlane(line.Direction, plane);
            if (direction == null) continue;
            bestLength = length;
            best = direction;
        }
        return best;
    }

    private static XYZ? SpanDirection(IReadOnlyList<XYZ> points)
    {
        if (points.Count < 2) return null;
        var v = points[^1] - points[0];
        return v.GetLength() > 1e-9 ? v.Normalize() : null;
    }

    private static XYZ? ProjectIntoPlane(XYZ direction, XYZ normal)
    {
        var projected = direction - normal * direction.DotProduct(normal);
        return projected.GetLength() < 1e-9 ? null : projected.Normalize();
    }

    private static XYZ AnyPerpendicular(XYZ normal)
    {
        var v = XYZ.BasisZ.CrossProduct(normal);
        if (v.GetLength() < 1e-6) v = XYZ.BasisX.CrossProduct(normal);
        return v.Normalize();
    }

    private static XYZ OrientDeterministic(XYZ v)
    {
        double ax = Math.Abs(v.X), ay = Math.Abs(v.Y), az = Math.Abs(v.Z);
        var dominant = ax >= ay && ax >= az ? v.X : ay >= az ? v.Y : v.Z;
        return dominant < 0 ? v.Negate() : v;
    }
}
