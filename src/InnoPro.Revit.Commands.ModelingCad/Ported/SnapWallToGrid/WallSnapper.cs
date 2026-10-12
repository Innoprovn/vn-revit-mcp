using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Xử lý Category Tường (Wall): tạo Dimension kiểm tra tới Grid song song gần nhất (đo tới
/// MẶT tường gần Grid hơn — đúng kiểu Dimension kiến trúc thật), và tự làm tròn (di chuyển cả Wall)
/// khi khoảng cách đó lẻ. 2 việc TÁCH RIÊNG (người dùng tick chọn độc lập ở UI) nhưng dùng chung cách
/// xác định Grid + mặt gần nhất (xem FindNearFace).</summary>
internal static class WallSnapper
{
    private static readonly double DimensionLinePaddingFeet =
        UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    internal static List<Wall> Collect(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();

    /// <summary>Tạo 1 Dimension THẬT từ mặt tường gần Grid nhất tới Grid đó — không sửa gì tường.</summary>
    internal static string CreateDimension(
        Document doc, View view, Wall wall, IReadOnlyList<Grid> grids, DimensionType dimensionType)
    {
        var label = Describe(wall);

        if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line wallLine)
            return $"{label}: bỏ qua — tường không phải Location Line (Arc?).";

        var wallP0 = wallLine.GetEndPoint(0);
        var wallP1 = wallLine.GetEndPoint(1);
        var direction = (wallP1 - wallP0).Normalize();
        var normal = new XYZ(-direction.Y, direction.X, 0);
        var centerN = wallP0.DotProduct(normal);

        var grid = GridMatcher.FindNearestParallel(direction, normal, centerN, grids, out var gridN);
        if (grid is null)
            return $"{label}: bỏ qua — không Grid nào đủ song song (tường xiên).";

        var faceRef = FindNearFaceReference(wall, normal, gridN);
        if (faceRef is null)
            return $"{label}: bỏ qua — không đọc được mặt tường (Side Face).";

        try
        {
            var references = new ReferenceArray();
            references.Append(faceRef);
            references.Append(new Reference(grid));

            var midpoint = (wallP0 + wallP1) / 2.0;
            var startN = Math.Min(centerN, gridN) - DimensionLinePaddingFeet;
            var endN = Math.Max(centerN, gridN) + DimensionLinePaddingFeet;
            var dimLine = Line.CreateBound(
                midpoint + normal.Multiply(startN - centerN), midpoint + normal.Multiply(endN - centerN));

            doc.Create.NewDimension(view, dimLine, references, dimensionType);
            return $"{label}: đã tạo Dimension tới Grid \"{grid.Name}\".";
        }
        catch (Exception ex)
        {
            return $"{label}: lỗi khi tạo Dimension tới Grid \"{grid.Name}\" — {ex.Message}";
        }
    }

    /// <summary>Làm tròn khoảng cách Grid→mặt tường gần nhất về bội số <paramref name="snapUnitMm"/>
    /// bằng cách dịch cả Wall vuông góc với chính nó. Trả về null nếu bỏ qua (tường xiên/không phải
    /// Line) — khác biệt với "đã tròn sẵn" (vẫn trả về note, chỉ không có hành động di chuyển).</summary>
    internal static (bool Changed, string? Note) Snap(Document doc, Wall wall, IReadOnlyList<Grid> grids, double snapUnitMm)
    {
        if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line wallLine)
            return (false, null);

        var wallP0 = wallLine.GetEndPoint(0);
        var direction = (wallLine.GetEndPoint(1) - wallP0).Normalize();
        var normal = new XYZ(-direction.Y, direction.X, 0);
        var centerN = wallP0.DotProduct(normal);

        var grid = GridMatcher.FindNearestParallel(direction, normal, centerN, grids, out var gridN);
        if (grid is null)
            return (false, null);

        var halfWidthFeet = wall.Width / 2.0;
        var sideSign = Math.Sign(gridN - centerN);
        if (sideSign == 0)
            sideSign = 1;

        var nearFaceN = centerN + sideSign * halfWidthFeet;
        var gapFeet = sideSign * (gridN - nearFaceN);
        var (roundedMm, deltaFeet) = GridMatcher.RoundGap(gapFeet, snapUnitMm);

        var label = Describe(wall);
        if (Math.Abs(deltaFeet) < 1e-6)
            return (false, $"{label}: Grid \"{grid.Name}\" đã tròn ({roundedMm}mm), không cần chỉnh.");

        ElementTransformUtils.MoveElement(doc, wall.Id, normal.Multiply(-sideSign * deltaFeet));
        return (true, $"{label}: Grid \"{grid.Name}\" -> làm tròn {roundedMm}mm.");
    }

    /// <summary>Trong 2 mặt tường (Exterior/Interior), chọn mặt GẦN Grid hơn — đúng mặt người dùng sẽ
    /// ghi Dimension thật trên bản vẽ.</summary>
    private static Reference? FindNearFaceReference(Wall wall, XYZ normal, double gridN)
    {
        try
        {
            var faceRefs = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior)
                .Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior))
                .ToList();

            Reference? best = null;
            var bestDistance = double.MaxValue;

            foreach (var faceRef in faceRefs)
            {
                if (wall.GetGeometryObjectFromReference(faceRef) is not PlanarFace face)
                    continue;

                var distance = Math.Abs(face.Origin.DotProduct(normal) - gridN);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = faceRef;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(Wall wall)
    {
        if (wall.Location is not LocationCurve locationCurve)
            return "Tường";

        var p = locationCurve.Curve.GetEndPoint(0);
        var xMm = Math.Round(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters));
        var yMm = Math.Round(UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters));
        return $"Tường tại ({xMm}, {yMm})";
    }
}
