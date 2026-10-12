using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Xử lý Category Cột — GỘP CẢ Structural Columns (OST_StructuralColumns) VÀ Architectural
/// Columns (OST_Columns) làm 1 Category "Cột" theo đúng yêu cầu người dùng. Cột là phần tử KIỂU ĐIỂM
/// (Location Point, không phải Location Curve như Wall/Beam) — logic so Grid khác hẳn, xem
/// PointElementSnapper (so theo TỪNG trục Grid liên quan, thường 2 trục cho hệ Grid vuông góc).</summary>
internal static class ColumnSnapper
{
    internal static List<FamilyInstance> Collect(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .WherePasses(new LogicalOrFilter(
                new ElementCategoryFilter(BuiltInCategory.OST_StructuralColumns),
                new ElementCategoryFilter(BuiltInCategory.OST_Columns)))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .ToList();

    internal static List<string> CreateDimensions(
        Document doc, View view, FamilyInstance column, IReadOnlyList<Grid> grids, DimensionType dimensionType)
    {
        if (column.Location is not LocationPoint locationPoint)
            return new List<string> { "Cột: bỏ qua — không phải Location Point." };

        return PointElementSnapper.CreateDimensions(doc, view, column, locationPoint.Point, grids, dimensionType, "Cột");
    }

    internal static (bool Changed, string? Note) Snap(
        Document doc, FamilyInstance column, IReadOnlyList<Grid> grids, double snapUnitMm)
    {
        if (column.Location is not LocationPoint locationPoint)
            return (false, null);

        return PointElementSnapper.Snap(doc, column, locationPoint.Point, grids, snapUnitMm, "Cột");
    }
}
