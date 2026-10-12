namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Spatial-hash broad-phase (spec §5 step 3). Each bar's expanded bounding box is inserted
/// into every grid cell it touches, so two bars are only compared once they share a cell — this is
/// the multi-cell-insertion equivalent of "own cell + 26 neighbors".</summary>
internal static class BroadPhase
{
    public static List<(int I, int J)> FindCandidatePairs(IReadOnlyList<BarGeometryDto> bars, double cellSizeMm, double toleranceMm)
    {
        if (bars.Count == 0)
            return new List<(int, int)>();

        var maxRadius = bars.Max(b => b.DiameterMm / 2);
        var expand = maxRadius + toleranceMm;

        var boxes = new BoundingBoxMm[bars.Count];
        var cells = new Dictionary<(long X, long Y, long Z), List<int>>();

        for (var i = 0; i < bars.Count; i++)
        {
            var box = BoundingBoxMm.FromBar(bars[i], expand);
            boxes[i] = box;

            foreach (var cell in CellsFor(box, cellSizeMm))
            {
                if (!cells.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    cells[cell] = list;
                }
                list.Add(i);
            }
        }

        var pairs = new HashSet<(int, int)>();
        foreach (var list in cells.Values)
        {
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    var i = Math.Min(list[a], list[b]);
                    var j = Math.Max(list[a], list[b]);
                    if (boxes[i].Intersects(boxes[j]))
                        pairs.Add((i, j));
                }
            }
        }

        return pairs.ToList();
    }

    private static IEnumerable<(long, long, long)> CellsFor(BoundingBoxMm box, double cellSizeMm)
    {
        var minX = (long)Math.Floor(box.MinX / cellSizeMm);
        var maxX = (long)Math.Floor(box.MaxX / cellSizeMm);
        var minY = (long)Math.Floor(box.MinY / cellSizeMm);
        var maxY = (long)Math.Floor(box.MaxY / cellSizeMm);
        var minZ = (long)Math.Floor(box.MinZ / cellSizeMm);
        var maxZ = (long)Math.Floor(box.MaxZ / cellSizeMm);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
            yield return (x, y, z);
    }
}
