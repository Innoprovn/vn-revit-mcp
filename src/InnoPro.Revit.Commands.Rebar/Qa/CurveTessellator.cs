namespace InnoPro.Revit.Commands.Rebar.Qa;

using Autodesk.Revit.DB;

/// <summary>Arc → chuỗi đoạn thẳng ngắn (spec §5 step 2), using Revit's own adaptive tessellation
/// so segment density follows curvature instead of a fixed chord count.</summary>
internal static class CurveTessellator
{
    public static List<BarSegment> TessellateAll(IEnumerable<Curve> curves)
    {
        var segments = new List<BarSegment>();
        foreach (var curve in curves)
        {
            segments.AddRange(Tessellate(curve));
        }
        return segments;
    }

    private static IEnumerable<BarSegment> Tessellate(Curve curve)
    {
        IList<XYZ> points = curve.Tessellate();
        for (int i = 0; i < points.Count - 1; i++)
        {
            yield return new BarSegment(points[i].ToPoint3d(), points[i + 1].ToPoint3d());
        }
    }
}
