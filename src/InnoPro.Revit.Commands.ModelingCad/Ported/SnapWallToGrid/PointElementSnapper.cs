using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Xử lý phần tử KIỂU ĐIỂM (Cột — ColumnSnapper, Móng đơn — FoundationSnapper) — khác hẳn
/// Wall/Beam/Floor (phần tử có 1 phương, chỉ so 1 Grid song song): 1 điểm phải so với Grid theo TỪNG
/// NHÓM PHƯƠNG khác nhau trong hệ Grid (xem GridMatcher.FindNearestPerDirection — thường ra 2 kết quả
/// cho hệ Grid vuông góc 2 chiều phổ biến), tạo/kiểm tra Dimension và làm tròn RIÊNG theo TỪNG trục,
/// rồi CỘNG DỒN vector dịch chuyển của tất cả các trục lại thành 1 lần Move DUY NHẤT cho Làm tròn.</summary>
internal static class PointElementSnapper
{
    private static readonly double DimensionLinePaddingFeet =
        UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    /// <summary>Tạo 1 Dimension THẬT cho MỖI trục Grid liên quan (thường 2 — dọc/ngang) từ tim phần tử
    /// tới Grid gần nhất trên trục đó. Family không khai báo reference tim phù hợp trục nào thì bỏ qua
    /// riêng trục đó (báo rõ lý do), không ảnh hưởng các trục còn lại.</summary>
    internal static List<string> CreateDimensions(
        Document doc, View view, FamilyInstance element, XYZ point, IReadOnlyList<Grid> grids,
        DimensionType dimensionType, string categoryLabel)
    {
        var label = Describe(categoryLabel, point);
        var matches = GridMatcher.FindNearestPerDirection(point, grids);
        if (matches.Count == 0)
            return new List<string> { $"{label}: bỏ qua — không có Grid nào." };

        var results = new List<string>();
        foreach (var (grid, gridN, normal) in matches)
        {
            var reference = TryGetAlignedCenterReference(element, normal);
            if (reference is null)
            {
                results.Add($"{label}: bỏ qua Grid \"{grid.Name}\" — Family không có reference tim thẳng hàng trục này.");
                continue;
            }

            try
            {
                var references = new ReferenceArray();
                references.Append(reference);
                references.Append(new Reference(grid));

                var pointN = point.DotProduct(normal);
                var startN = Math.Min(pointN, gridN) - DimensionLinePaddingFeet;
                var endN = Math.Max(pointN, gridN) + DimensionLinePaddingFeet;
                var dimLine = Line.CreateBound(
                    point + normal.Multiply(startN - pointN), point + normal.Multiply(endN - pointN));

                doc.Create.NewDimension(view, dimLine, references, dimensionType);
                results.Add($"{label}: đã tạo Dimension tới Grid \"{grid.Name}\".");
            }
            catch (Exception ex)
            {
                results.Add($"{label}: lỗi khi tạo Dimension tới Grid \"{grid.Name}\" — {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>Làm tròn khoảng cách tới TỪNG trục Grid liên quan, cộng dồn thành 1 vector dịch chuyển
    /// duy nhất rồi Move 1 LẦN — tránh trường hợp Move riêng từng trục làm trục đã xử lý trước bị lệch
    /// lại do phép Move sau (dù về lý thuyết 2 trục vuông góc không ảnh hưởng nhau, cộng dồn vẫn an
    /// toàn/rõ ràng hơn).</summary>
    internal static (bool Changed, string? Note) Snap(
        Document doc, Element element, XYZ point, IReadOnlyList<Grid> grids, double snapUnitMm, string categoryLabel)
    {
        var label = Describe(categoryLabel, point);
        var matches = GridMatcher.FindNearestPerDirection(point, grids);
        if (matches.Count == 0)
            return (false, null);

        var moveVector = XYZ.Zero;
        var parts = new List<string>();

        foreach (var (grid, gridN, normal) in matches)
        {
            var pointN = point.DotProduct(normal);
            var (roundedMm, deltaFeet) = GridMatcher.RoundGap(pointN - gridN, snapUnitMm);
            if (Math.Abs(deltaFeet) < 1e-6)
                continue;

            moveVector += normal.Multiply(deltaFeet);
            parts.Add($"Grid \"{grid.Name}\" -> {roundedMm}mm");
        }

        if (parts.Count == 0)
            return (false, $"{label}: đã tròn theo mọi Grid liên quan, không cần chỉnh.");

        ElementTransformUtils.MoveElement(doc, element.Id, moveVector);
        return (true, $"{label}: {string.Join("; ", parts)}.");
    }

    /// <summary>Family cột/móng thường khai báo CẢ 2 reference tim (Center Left/Right VÀ Center
    /// Front/Back) — ứng với 2 phương vuông góc nhau của tiết diện, nhưng KHÔNG biết trước cái nào
    /// khớp đúng <paramref name="desiredNormal"/> (phụ thuộc góc xoay family lúc đặt). Thử phân giải
    /// CẢ 2 thành hình học thật (PlanarFace/Line), so hướng thật với desiredNormal, chỉ nhận nếu đủ
    /// thẳng hàng (cos &gt;= 0.9, ~25°) — tránh lấy nhầm reference của trục kia.</summary>
    private static Reference? TryGetAlignedCenterReference(FamilyInstance element, XYZ desiredNormal)
    {
        try
        {
            Reference? best = null;
            var bestAlignment = 0.9;

            foreach (var type in new[] { FamilyInstanceReferenceType.CenterLeftRight, FamilyInstanceReferenceType.CenterFrontBack })
            {
                var refs = element.GetReferences(type);
                if (refs is not { Count: > 0 })
                    continue;

                var geomObj = element.GetGeometryObjectFromReference(refs[0]);
                var planeNormal = geomObj switch
                {
                    PlanarFace face => face.FaceNormal,
                    Line line => Perpendicular((line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize()),
                    _ => (XYZ?)null
                };

                if (planeNormal is null)
                    continue;

                var alignment = Math.Abs(planeNormal.Normalize().DotProduct(desiredNormal));
                if (alignment < bestAlignment)
                    continue;

                bestAlignment = alignment;
                best = refs[0];
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static XYZ Perpendicular(XYZ direction) => new(-direction.Y, direction.X, 0);

    private static string Describe(string categoryLabel, XYZ point)
    {
        var xMm = Math.Round(UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters));
        var yMm = Math.Round(UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters));
        return $"{categoryLabel} tại ({xMm}, {yMm})";
    }
}
