namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Brute-force closest approach between two bars (spec §5 step 4) — each bar typically has
/// only a handful of tessellated segments, so segment-count-squared per pair is cheap.</summary>
internal static class NarrowPhase
{
    public static SegmentDistanceResult FindClosestApproach(BarGeometryDto a, BarGeometryDto b)
    {
        var best = new SegmentDistanceResult(double.MaxValue, default, default, 0);
        foreach (var segA in a.CenterlineSegments)
        {
            foreach (var segB in b.CenterlineSegments)
            {
                var result = SegmentDistance.Compute(segA, segB);
                if (result.DistanceMm < best.DistanceMm)
                    best = result;
            }
        }
        return best;
    }
}
