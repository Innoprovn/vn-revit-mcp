namespace InnoPro.Revit.Commands.Rebar.Qa;

using Autodesk.Revit.DB;

/// <summary>Revit's internal units are feet; everything above this layer works in millimeters.</summary>
internal static class RevitUnits
{
    public static double FeetToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    public static Point3d ToPoint3d(this XYZ xyz) => new(FeetToMm(xyz.X), FeetToMm(xyz.Y), FeetToMm(xyz.Z));
}
