using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Tìm tường THẬT (đã có sẵn trong model — khác WallFromCad, ở đó tường còn phải tự dựng từ
/// CAD) gần nhất khớp với 1 DetectedOpening — dùng để xác định Family Instance sẽ host vào tường
/// nào.</summary>
internal static class WallHostFinder
{
    private const double AngleToleranceDegrees = 2.0;
    private static readonly double AlignmentToleranceFeet =
        UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);

    /// <summary>Tường khớp phải: (1) thẳng (Location là Line — tường cong ngoài phạm vi tool này),
    /// (2) SONG SONG với phương lỗ mở (sai số góc chuẩn AngleToleranceDegrees) — đúng chuẩn vẽ CAD: lá
    /// cửa lúc đóng nằm khít mặt phẳng tường, nên phương lỗ mở luôn song song tường thật; (3) lệch
    /// vuông góc từ tâm lỗ mở tới trục tường không vượt quá NỬA bề rộng tường + dung sai (tâm lỗ mở
    /// phải nằm trong đúng bề dày tường, không phải cách xa); (4) hình chiếu tâm lỗ mở lên trục tường
    /// nằm trong đúng đoạn tường thật (không lố ra ngoài 2 đầu tường quá AlignmentToleranceFeet). Nhiều
    /// tường cùng khớp thì lấy tường lệch vuông góc NHỎ NHẤT.</summary>
    public static HostWallMatch? FindHost(DetectedOpening opening, IReadOnlyList<Wall> walls)
    {
        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);

        HostWallMatch? best = null;
        var bestDistance = double.MaxValue;

        foreach (var wall in walls)
        {
            if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line wallLine)
                continue;

            var wallP0 = wallLine.GetEndPoint(0);
            var wallDirection = (wallLine.GetEndPoint(1) - wallP0).Normalize();
            if (Math.Abs(opening.Direction.DotProduct(wallDirection)) < cosTolerance)
                continue;

            var normal = new XYZ(-wallDirection.Y, wallDirection.X, 0);
            var perpDistance = Math.Abs((opening.Center - wallP0).DotProduct(normal));
            var halfWidthFeet = wall.Width / 2.0;
            if (perpDistance > halfWidthFeet + AlignmentToleranceFeet)
                continue;

            var t = (opening.Center - wallP0).DotProduct(wallDirection);
            if (t < -AlignmentToleranceFeet || t > wallLine.Length + AlignmentToleranceFeet)
                continue;

            if (perpDistance >= bestDistance)
                continue;

            var tClamped = Math.Max(0.0, Math.Min(wallLine.Length, t));
            bestDistance = perpDistance;
            best = new HostWallMatch
            {
                Wall = wall,
                InsertionPoint = wallP0 + wallDirection.Multiply(tClamped)
            };
        }

        return best;
    }
}
