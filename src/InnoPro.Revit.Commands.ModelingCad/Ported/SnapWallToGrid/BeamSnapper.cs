using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Xử lý Category Dầm (Structural Framing): ƯU TIÊN dùng đúng TIM dầm (Location Curve) làm
/// mốc so với Grid — đúng quy ước ghi Dimension kết cấu phổ biến nhất (Grid tới tim dầm/cột) — Reference
/// lấy qua FamilyInstanceReferenceType.CenterLeftRight NẾU family có khai báo (không phải family nào
/// cũng có reference plane này). Family KHÔNG khai báo thì KHÔNG còn bỏ qua như bản trước — tự tìm
/// MẶT BÊN của dầm qua hình học thật (quét Solid.Faces tìm PlanarFace có pháp tuyến vuông góc phương
/// dầm — giống hệt cách Wall dùng HostObjectUtils, chỉ khác là dầm không phải HostObject nên phải tự
/// quét geometry), tương tự Wall: Dimension khi đó là Grid→MẶT dầm (không phải tim) — ghi rõ "Nguồn"
/// trong kết quả để biết đang đo tới đâu. LÀM TRÒN (Snap) không bị ảnh hưởng bởi vụ Reference này —
/// luôn hoạt động vì chỉ cần toạ độ hình học (tim dầm), không cần Reference.
///
/// TÁI SỬ DỤNG cho Móng dạng đường (móng băng — FamilyInstance có Location Curve, hình học giống dầm
/// hệt) qua tham số <c>categoryLabel</c> (mặc định "Dầm", FoundationSnapper truyền "Móng") — xem
/// FoundationSnapper.</summary>
internal static class BeamSnapper
{
    private static readonly double DimensionLinePaddingFeet =
        UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    internal static List<FamilyInstance> Collect(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .ToList();

    internal static string CreateDimension(
        Document doc, View view, FamilyInstance beam, IReadOnlyList<Grid> grids, DimensionType dimensionType,
        string categoryLabel = "Dầm")
    {
        var label = Describe(beam, categoryLabel);

        if (beam.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line beamLine)
            return $"{label}: bỏ qua — không phải Location Line (Arc?).";

        var p0 = beamLine.GetEndPoint(0);
        var p1 = beamLine.GetEndPoint(1);
        var direction = (p1 - p0).Normalize();
        var normal = new XYZ(-direction.Y, direction.X, 0);
        var centerN = p0.DotProduct(normal);

        var grid = GridMatcher.FindNearestParallel(direction, normal, centerN, grids, out var gridN);
        if (grid is null)
            return $"{label}: bỏ qua — không Grid nào đủ song song (dầm xiên).";

        var centerRef = TryGetCenterReference(beam);
        var referenceN = centerN;
        var source = "tim (Center Left/Right)";

        if (centerRef is null)
        {
            var sideFace = FindNearSideFaceReference(beam, normal, gridN);
            if (sideFace is null)
                return $"{label}: bỏ qua — không tìm được reference nào (cả tim family lẫn mặt bên hình học).";

            centerRef = sideFace.Value.Reference;
            referenceN = sideFace.Value.FaceN;
            source = "mặt bên (dự phòng hình học, family không khai báo tim)";
        }

        try
        {
            var references = new ReferenceArray();
            references.Append(centerRef);
            references.Append(new Reference(grid));

            var midpoint = (p0 + p1) / 2.0;
            var startN = Math.Min(referenceN, gridN) - DimensionLinePaddingFeet;
            var endN = Math.Max(referenceN, gridN) + DimensionLinePaddingFeet;
            var dimLine = Line.CreateBound(
                midpoint + normal.Multiply(startN - centerN), midpoint + normal.Multiply(endN - centerN));

            doc.Create.NewDimension(view, dimLine, references, dimensionType);
            return $"{label}: đã tạo Dimension ({source}) tới Grid \"{grid.Name}\".";
        }
        catch (Exception ex)
        {
            return $"{label}: lỗi khi tạo Dimension tới Grid \"{grid.Name}\" — {ex.Message}";
        }
    }

    internal static (bool Changed, string? Note) Snap(
        Document doc, FamilyInstance beam, IReadOnlyList<Grid> grids, double snapUnitMm, string categoryLabel = "Dầm")
    {
        if (beam.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line beamLine)
            return (false, null);

        var p0 = beamLine.GetEndPoint(0);
        var direction = (beamLine.GetEndPoint(1) - p0).Normalize();
        var normal = new XYZ(-direction.Y, direction.X, 0);
        var centerN = p0.DotProduct(normal);

        var grid = GridMatcher.FindNearestParallel(direction, normal, centerN, grids, out var gridN);
        if (grid is null)
            return (false, null);

        var sideSign = Math.Sign(gridN - centerN);
        if (sideSign == 0)
            sideSign = 1;

        var gapFeet = sideSign * (gridN - centerN);
        var (roundedMm, deltaFeet) = GridMatcher.RoundGap(gapFeet, snapUnitMm);

        var label = Describe(beam, categoryLabel);
        if (Math.Abs(deltaFeet) < 1e-6)
            return (false, $"{label}: Grid \"{grid.Name}\" đã tròn ({roundedMm}mm), không cần chỉnh.");

        ElementTransformUtils.MoveElement(doc, beam.Id, normal.Multiply(-sideSign * deltaFeet));
        return (true, $"{label}: Grid \"{grid.Name}\" -> làm tròn {roundedMm}mm.");
    }

    private static Reference? TryGetCenterReference(FamilyInstance beam)
    {
        try
        {
            var refs = beam.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
            return refs is { Count: > 0 } ? refs[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dự phòng khi family không khai báo reference tim: tự quét hình học thật (Solid.Faces)
    /// tìm MẶT BÊN — PlanarFace có pháp tuyến gần trùng <paramref name="normal"/> (vuông góc phương
    /// dầm, đúng hướng "trái/phải" khi nhìn dọc dầm — loại được mặt đầu dầm, pháp tuyến dọc theo phương
    /// dầm, và mặt trên/dưới, pháp tuyến gần thẳng đứng) — GẦN Grid nhất trong 2 mặt bên. Trả về null
    /// nếu không đọc được hình học (rất hiếm, family lỗi) — khi đó mới thật sự bỏ qua Dimension.</summary>
    private static (Reference Reference, double FaceN)? FindNearSideFaceReference(
        FamilyInstance beam, XYZ normal, double gridN)
    {
        try
        {
            // ComputeReferences = true BAT BUOC de Face.Reference tra ve Reference THAT su dung duoc
            // cho NewDimension - mac dinh (false) khien Reference vo hieu, gay loi "Invalid number of
            // references" luc tao Dimension du code bien dich va chay khong nem exception o day.
            GeometryElement? geometryElement = beam.get_Geometry(new Options { ComputeReferences = true });
            if (geometryElement is null)
                return null;

            (Reference Reference, double FaceN)? best = null;
            var bestDistance = double.MaxValue;

            void Walk(GeometryElement element)
            {
                foreach (var geomObj in element)
                {
                    switch (geomObj)
                    {
                        case GeometryInstance geomInstance:
                            var nested = geomInstance.GetInstanceGeometry();
                            if (nested is not null)
                                Walk(nested);
                            break;

                        case Solid solid when !solid.Faces.IsEmpty:
                            foreach (Face face in solid.Faces)
                            {
                                if (face is not PlanarFace planar)
                                    continue;

                                // Chi nhan mat co phap tuyen GAN TRUNG voi normal (mat ben that su) -
                                // loai mat dau dam (phap tuyen doc phuong dam) va mat tren/duoi (phap
                                // tuyen thang dung).
                                if (Math.Abs(planar.FaceNormal.Normalize().DotProduct(normal)) < 0.9)
                                    continue;

                                var faceN = planar.Origin.DotProduct(normal);
                                var distance = Math.Abs(faceN - gridN);
                                if (distance >= bestDistance)
                                    continue;

                                bestDistance = distance;
                                best = (planar.Reference, faceN);
                            }

                            break;
                    }
                }
            }

            Walk(geometryElement);
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(FamilyInstance beam, string categoryLabel)
    {
        if (beam.Location is not LocationCurve locationCurve)
            return categoryLabel;

        var p = locationCurve.Curve.GetEndPoint(0);
        var xMm = Math.Round(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters));
        var yMm = Math.Round(UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters));
        return $"{categoryLabel} tại ({xMm}, {yMm})";
    }
}
