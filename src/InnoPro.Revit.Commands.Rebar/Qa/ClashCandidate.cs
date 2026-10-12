namespace InnoPro.Revit.Commands.Rebar.Qa;

internal sealed record ClashCandidate(
    BarGeometryDto BarA,
    BarGeometryDto BarB,
    double DistanceMm,
    double AngleDeg,
    double OverlapLengthMm,
    Point3d LocationMm);
