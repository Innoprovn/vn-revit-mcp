namespace InnoPro.Revit.Sdk;

/// <summary>
/// Revit lưu chiều dài bằng feet. Mọi tool công bố ra ngoài dùng milimét.
/// </summary>
public static class UnitConvert
{
    private const double MmPerFoot = 304.8;

    public static double FeetToMm(double feet) => Math.Round(feet * MmPerFoot, 3);

    public static double MmToFeet(double mm) => mm / MmPerFoot;

    public static double SquareFeetToSquareMeters(double sqft) => Math.Round(sqft * 0.09290304, 4);

    public static double CubicFeetToCubicMeters(double cuft) => Math.Round(cuft * 0.028316846592, 4);
}
