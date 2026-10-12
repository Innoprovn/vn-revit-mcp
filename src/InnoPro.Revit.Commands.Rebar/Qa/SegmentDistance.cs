namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Closest distance and crossing angle between two 3D segments (spec §5 step 4).</summary>
internal readonly record struct SegmentDistanceResult(double DistanceMm, Point3d PointOnA, Point3d PointOnB, double AngleDeg);

internal static class SegmentDistance
{
    private const double Eps = 1e-9;

    /// <summary>Closed-form closest distance between two line segments (Lumelsky's algorithm).</summary>
    public static SegmentDistanceResult Compute(BarSegment a, BarSegment b)
    {
        var p1 = a.Start;
        var p2 = a.End;
        var p3 = b.Start;
        var p4 = b.End;

        var d1 = Sub(p2, p1);
        var d2 = Sub(p4, p3);
        var r = Sub(p1, p3);

        var aCoef = Dot(d1, d1);
        var eCoef = Dot(d2, d2);
        var fCoef = Dot(d2, r);

        double s, t;

        if (aCoef <= Eps && eCoef <= Eps)
        {
            s = 0;
            t = 0;
        }
        else if (aCoef <= Eps)
        {
            s = 0;
            t = Clamp01(fCoef / eCoef);
        }
        else
        {
            var c = Dot(d1, r);
            if (eCoef <= Eps)
            {
                t = 0;
                s = Clamp01(-c / aCoef);
            }
            else
            {
                var bCoef = Dot(d1, d2);
                var denom = aCoef * eCoef - bCoef * bCoef;
                s = Math.Abs(denom) > Eps ? Clamp01((bCoef * fCoef - c * eCoef) / denom) : 0;

                t = (bCoef * s + fCoef) / eCoef;

                if (t < 0)
                {
                    t = 0;
                    s = Clamp01(-c / aCoef);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Clamp01((bCoef - c) / aCoef);
                }
            }
        }

        var closestA = Lerp(p1, p2, s);
        var closestB = Lerp(p3, p4, t);
        var distance = closestA.DistanceTo(closestB);
        var angle = AngleBetweenDeg(d1, d2);

        return new SegmentDistanceResult(distance, closestA, closestB, angle);
    }

    private static Point3d Sub(Point3d a, Point3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Dot(Point3d a, Point3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    private static Point3d Lerp(Point3d a, Point3d b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>0-90deg, direction-agnostic (a bar and its reverse are the same line).</summary>
    private static double AngleBetweenDeg(Point3d d1, Point3d d2)
    {
        var len1 = Math.Sqrt(Dot(d1, d1));
        var len2 = Math.Sqrt(Dot(d2, d2));
        if (len1 < Eps || len2 < Eps)
            return 0;

        var cos = Math.Clamp(Dot(d1, d2) / (len1 * len2), -1.0, 1.0);
        return Math.Acos(Math.Abs(cos)) * 180.0 / Math.PI;
    }
}
