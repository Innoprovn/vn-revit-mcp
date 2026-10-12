namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>One straight piece of a tessellated bar centerline (see spec §5 step 2).</summary>
internal readonly record struct BarSegment(Point3d Start, Point3d End);
