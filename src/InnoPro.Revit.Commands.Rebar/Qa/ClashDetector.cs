namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Ties broad-phase + narrow-phase + shape-code isolation filter together (spec §5).</summary>
internal sealed class ClashDetector
{
    public IReadOnlyList<ClashCandidate> FindCandidates(IReadOnlyList<BarGeometryDto> bars, ClashConfig config)
    {
        // Thresholds left blank until tuned against a real model (spec §8) — no candidates until then.
        if (config.CellSizeMm is not { } cellSize || config.ToleranceMm is not { } tolerance)
            return Array.Empty<ClashCandidate>();

        var pairs = BroadPhase.FindCandidatePairs(bars, cellSize, tolerance);
        var results = new List<ClashCandidate>();

        foreach (var (i, j) in pairs)
        {
            var barA = bars[i];
            var barB = bars[j];

            // Bars in the same Rebar Set share one ElementId — spacing between them is a Nhóm A
            // (parameter/spacing) concern, not an independent-rebar clash, so skip self-set pairs
            // entirely rather than reporting what looks like a bar clashing with itself.
            if (barA.ElementId == barB.ElementId)
                continue;

            if (IsIsolatedByGroup(barA.ShapeCode, barB.ShapeCode, config.ShapeCodeIsolatedGroups))
                continue;

            var closest = NarrowPhase.FindClosestApproach(barA, barB);
            var clearance = barA.DiameterMm / 2 + barB.DiameterMm / 2 + tolerance;
            if (closest.DistanceMm > clearance)
                continue;

            var overlap = OverlapEstimator.EstimateOverlapLengthMm(barA, barB);
            results.Add(new ClashCandidate(barA, barB, closest.DistanceMm, closest.AngleDeg, overlap, closest.PointOnA));
        }

        return results;
    }

    private static bool IsIsolatedByGroup(string shapeCodeA, string shapeCodeB, List<List<string>> groups)
    {
        var groupA = FindGroupIndex(shapeCodeA, groups);
        var groupB = FindGroupIndex(shapeCodeB, groups);
        return groupA >= 0 && groupB >= 0 && groupA != groupB;
    }

    private static int FindGroupIndex(string shapeCode, List<List<string>> groups)
    {
        for (var g = 0; g < groups.Count; g++)
            if (groups[g].Contains(shapeCode))
                return g;
        return -1;
    }
}
