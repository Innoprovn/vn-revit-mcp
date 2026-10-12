namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Point in millimeters. Extraction converts from Revit's internal feet at the boundary
/// so every layer above it (Rules, RuleEngine) works in mm, matching rebar_rules.yaml.</summary>
internal readonly record struct Point3d(double X, double Y, double Z)
{
    public double DistanceTo(Point3d other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
