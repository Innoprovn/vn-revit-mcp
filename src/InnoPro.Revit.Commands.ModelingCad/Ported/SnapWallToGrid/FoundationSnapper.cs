using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Xử lý Category Móng (Structural Foundation) — KHÔNG đồng nhất về hình học như các Category
/// khác: gồm CẢ 3 dạng thật tế hay gặp, phải DISPATCH theo đúng loại phần tử/Location:
///   - Móng bè (mat foundation) — thực chất là 1 <see cref="Floor"/> (cùng lớp với Sàn, chỉ khác
///     Category) → tái dùng NGUYÊN logic FloorSnapper (mép sàn/sketch).
///   - Móng băng (strip footing, chạy dọc theo dầm/tường) — FamilyInstance có Location Curve, hình học
///     y hệt Dầm → tái dùng NGUYÊN logic BeamSnapper (tim móng, categoryLabel "Móng").
///   - Móng đơn (isolated footing, dưới 1 cột) — FamilyInstance có Location Point, y hệt Cột → tái
///     dùng PointElementSnapper (so theo từng trục Grid).
/// Phần tử không rơi vào 3 dạng trên (hiếm) thì bỏ qua, báo rõ lý do.</summary>
internal static class FoundationSnapper
{
    internal static List<Element> Collect(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .WhereElementIsNotElementType()
            .ToList();

    internal static List<string> CreateDimensions(
        Document doc, View view, Element foundation, IReadOnlyList<Grid> grids, DimensionType dimensionType)
    {
        switch (foundation)
        {
            case Floor floor:
                return FloorSnapper.CreateDimension(doc, view, floor, grids, dimensionType);

            case FamilyInstance fi when fi.Location is LocationPoint locationPoint:
                return PointElementSnapper.CreateDimensions(doc, view, fi, locationPoint.Point, grids, dimensionType, "Móng");

            case FamilyInstance fi when fi.Location is LocationCurve:
                return new List<string> { BeamSnapper.CreateDimension(doc, view, fi, grids, dimensionType, "Móng") };

            default:
                return new List<string> { $"Móng (Id {foundation.Id.Value}): bỏ qua — loại hình học không hỗ trợ." };
        }
    }

    internal static (bool Changed, string? Note) Snap(
        Document doc, Element foundation, IReadOnlyList<Grid> grids, double snapUnitMm)
    {
        switch (foundation)
        {
            case Floor floor:
                return FloorSnapper.Snap(doc, floor, grids, snapUnitMm);

            case FamilyInstance fi when fi.Location is LocationPoint locationPoint:
                return PointElementSnapper.Snap(doc, fi, locationPoint.Point, grids, snapUnitMm, "Móng");

            case FamilyInstance fi when fi.Location is LocationCurve:
                return BeamSnapper.Snap(doc, fi, grids, snapUnitMm, "Móng");

            default:
                return (false, null);
        }
    }
}
