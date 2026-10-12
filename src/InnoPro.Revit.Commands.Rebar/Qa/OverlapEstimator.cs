namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>
/// Approximate axial overlap length between two bars, by projecting both onto bar A's own
/// start-to-end direction (spec §5 step 5 "chồng lấn dài/ngắn"). Good enough for the common
/// straight-bar / near-parallel case; TODO revisit for bent shapes once tuned against a real
/// model — thresholds here are deliberately left blank until then (spec §8).
/// </summary>
internal static class OverlapEstimator
{
    public static double EstimateOverlapLengthMm(BarGeometryDto a, BarGeometryDto b)
    {
        var (origin, dir) = AxisOf(a);
        if (dir is null)
            return 0;

        var (rangeA, hasA) = ProjectRange(a, origin, dir.Value);
        var (rangeB, hasB) = ProjectRange(b, origin, dir.Value);
        if (!hasA || !hasB)
            return 0;

        var overlapStart = Math.Max(rangeA.Min, rangeB.Min);
        var overlapEnd = Math.Min(rangeA.Max, rangeB.Max);
        return Math.Max(0, overlapEnd - overlapStart);
    }

    private static (Point3d Origin, Point3d? Dir) AxisOf(BarGeometryDto bar)
    {
        if (bar.CenterlineSegments.Count == 0)
            return (default, null);

        var start = bar.CenterlineSegments[0].Start;
        var end = bar.CenterlineSegments[^1].End;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-6)
            return (start, null);

        return (start, new Point3d(dx / len, dy / len, dz / len));
    }

    private static ((double Min, double Max) Range, bool Has) ProjectRange(BarGeometryDto bar, Point3d origin, Point3d dir)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var has = false;

        foreach (var seg in bar.CenterlineSegments)
        {
            foreach (var point in new[] { seg.Start, seg.End })
            {
                var t = (point.X - origin.X) * dir.X + (point.Y - origin.Y) * dir.Y + (point.Z - origin.Z) * dir.Z;
                if (t < min) min = t;
                if (t > max) max = t;
                has = true;
            }
        }

        return ((min, max), has);
    }
}
