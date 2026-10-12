using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Tìm Grid THẲNG SONG SONG gần nhất với 1 phương cho trước — dùng chung cho Wall/Beam/Floor,
/// cả khi tạo Dimension kiểm tra lẫn khi tự làm tròn (di chuyển/sửa sketch). "Song song" nghĩa là
/// phần tử (tường/dầm/mép sàn) chạy DỌC theo hướng gần giống 1 Grid — đúng kiểu Dimension kiến trúc
/// hay ghi: khoảng cách NGANG từ Grid tới phần tử, không phải đo DỌC qua Grid như cửa/PlaceDoorWindow
/// (ở đó cửa cắt NGANG qua tường nên đo dọc tường từ Grid vuông góc — khác hẳn trường hợp ở đây).</summary>
internal static class GridMatcher
{
    internal const double AngleToleranceDegrees = 2.0;

    /// <summary>Tìm Grid thẳng song song với <paramref name="direction"/> (trong dung sai
    /// AngleToleranceDegrees) mà toạ độ vuông góc (chiếu lên <paramref name="normal"/>) GẦN
    /// <paramref name="centerN"/> nhất. Trả về null nếu không Grid nào đủ song song (phần tử xiên,
    /// không thẳng hàng với hệ Grid).</summary>
    internal static Grid? FindNearestParallel(
        XYZ direction, XYZ normal, double centerN, IReadOnlyList<Grid> grids, out double gridN)
    {
        var parallelCosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);

        Grid? best = null;
        var bestDistance = double.MaxValue;
        gridN = 0.0;

        foreach (var grid in grids)
        {
            if (grid.Curve is not Line gridLine)
                continue;

            var gridP0 = gridLine.GetEndPoint(0);
            var gridDirection = (gridLine.GetEndPoint(1) - gridP0).Normalize();

            if (Math.Abs(direction.DotProduct(gridDirection)) < parallelCosTolerance)
                continue;

            var candidateGridN = gridP0.DotProduct(normal);
            var distance = Math.Abs(candidateGridN - centerN);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = grid;
            gridN = candidateGridN;
        }

        return best;
    }

    /// <summary>Dùng cho phần tử KIỂU ĐIỂM (Cột, Móng đơn) — khác Wall/Beam/Floor (phần tử có 1
    /// PHƯƠNG rõ ràng, chỉ cần so 1 Grid song song), 1 điểm không có "phương" nên phải so với Grid theo
    /// TỪNG NHÓM PHƯƠNG khác nhau trong hệ Grid (thường 2 nhóm cho hệ Grid vuông góc 2 chiều kiểu Việt
    /// Nam: nhóm Grid số 1,2,3... và nhóm Grid chữ A,B,C...) — mỗi nhóm phương tìm ra ĐÚNG 1 Grid gần
    /// điểm nhất trong nhóm đó, trả về cả 2 (hoặc nhiều hơn nếu hệ Grid có nhiều hơn 2 phương) để bên
    /// gọi tạo/kiểm tra Dimension riêng theo từng trục.</summary>
    internal static List<(Grid Grid, double GridN, XYZ Normal)> FindNearestPerDirection(
        XYZ point, IReadOnlyList<Grid> grids)
    {
        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);
        var clusters = new List<(XYZ Direction, XYZ Normal, List<Grid> Grids)>();

        foreach (var grid in grids)
        {
            if (grid.Curve is not Line gridLine)
                continue;

            var direction = (gridLine.GetEndPoint(1) - gridLine.GetEndPoint(0)).Normalize();

            var clusterIndex = clusters.FindIndex(c => Math.Abs(c.Direction.DotProduct(direction)) >= cosTolerance);
            if (clusterIndex >= 0)
            {
                clusters[clusterIndex].Grids.Add(grid);
            }
            else
            {
                var normal = new XYZ(-direction.Y, direction.X, 0);
                clusters.Add((direction, normal, new List<Grid> { grid }));
            }
        }

        var results = new List<(Grid, double, XYZ)>();
        foreach (var (_, normal, gridsInCluster) in clusters)
        {
            var pointN = point.DotProduct(normal);

            Grid? best = null;
            var bestDistance = double.MaxValue;
            var bestGridN = 0.0;

            foreach (var grid in gridsInCluster)
            {
                var gridN = ((Line)grid.Curve).GetEndPoint(0).DotProduct(normal);
                var distance = Math.Abs(gridN - pointN);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = grid;
                bestGridN = gridN;
            }

            if (best is not null)
                results.Add((best, bestGridN, normal));
        }

        return results;
    }

    /// <summary>Làm tròn <paramref name="gapFeet"/> (khoảng cách Grid→phần tử, feet) về BỘI SỐ của
    /// <paramref name="snapUnitMm"/> (mm) — số liệu thiết kế kiến trúc luôn là bội của 10 hoặc ít nhất
    /// bội của 5, không bao giờ là số mm lẻ. Trả về (giá trị mm đã làm tròn, phần chênh lệch cần dịch
    /// thêm — feet, dương nghĩa là cần tăng gapFeet).</summary>
    internal static (double RoundedMm, double DeltaFeet) RoundGap(double gapFeet, double snapUnitMm)
    {
        var gapMm = UnitUtils.ConvertFromInternalUnits(gapFeet, UnitTypeId.Millimeters);
        var roundedMm = Math.Round(gapMm / snapUnitMm, MidpointRounding.AwayFromZero) * snapUnitMm;
        var deltaMm = roundedMm - gapMm;
        var deltaFeet = UnitUtils.ConvertToInternalUnits(deltaMm, UnitTypeId.Millimeters);
        return (roundedMm, deltaFeet);
    }
}
