using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Gộp nhiều đoạn thẳng hàng, liền kề (cùng phương, cùng vị trí vuông góc, nối tiếp dọc trục) thành 1 đoạn liền duy nhất — phỏng theo CadBeamScanner.MergeCollinearFragments của PlaceBeam, nhưng tổng quát hoá theo HƯỚNG BẤT KỲ (dùng vector + tích vô hướng) thay vì khoá cứng trục X/Y, vì dầm ở tool này không giới hạn phải chạy theo lưới. Dùng khi vẽ line tim dầm ra view — tránh vẽ vụn nhiều đoạn ngắn rời rạc cho 1 đường dầm liên tục bị cột/dầm khác chia cắt, giúp người dùng sửa tay dễ hơn (ít mẩu rời hơn).</summary>
internal static class CenterlineMerger
{
    private const double AngleToleranceDegrees = 2.0;
    private static readonly double PerpendicularTolFeet = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);

    /// <summary>Khoảng hở dọc trục tối đa để còn coi là "liền kề, cùng 1 đường" — không giới hạn chồng lấn (2 đoạn trùng/chồng nhau bao nhiêu cũng gộp, vì đã cùng phương + cùng vị trí vuông góc chắc chắn là 1 đường thật, tránh còn sót line trùng nhau).</summary>
    private static readonly double AdjacentGapMaxFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

    /// <summary>Gộp tham lam: với mỗi đoạn chưa dùng, mở rộng lặp lại bằng mọi đoạn còn lại thẳng hàng+liền kề với đoạn đang mở rộng, cho tới khi không còn đoạn nào gộp thêm được — protectedPoints (rỗng nếu không cần) là các điểm KHÔNG được để lọt vào giữa thân đoạn gộp (nút thật đang được cạnh khác không cùng phương dùng tới).</summary>
    public static List<Line> MergeCollinearAdjacent(IReadOnlyList<Line> segments, IReadOnlyList<XYZ>? protectedPoints = null, double? perpendicularToleranceFeet = null)
    {
        protectedPoints ??= Array.Empty<XYZ>();
        var perpendicularTolerance = perpendicularToleranceFeet ?? PerpendicularTolFeet;
        var used = new bool[segments.Count];
        var result = new List<Line>();

        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
                continue;

            used[i] = true;
            var p0 = segments[i].GetEndPoint(0);
            var p1 = segments[i].GetEndPoint(1);
            var dir = (p1 - p0).Normalize();

            var mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (var j = 0; j < segments.Count; j++)
                {
                    if (used[j])
                        continue;

                    if (!TryExtend(p0, dir, p1, segments[j], protectedPoints, perpendicularTolerance, out var newP0, out var newP1))
                        continue;

                    p0 = newP0;
                    p1 = newP1;
                    used[j] = true;
                    mergedAny = true;
                }
            }

            try
            {
                result.Add(Line.CreateBound(p0, p1));
            }
            catch (Exception)
            {
                // Suy bien (2 diem trung nhau) - bo qua rieng doan nay.
            }
        }

        return result;
    }

    /// <summary>Thử mở rộng đoạn hiện tại [p0,p1] (hướng dir cố định, tính 1 lần từ đoạn gốc) bằng candidate — chấp nhận nếu candidate cùng phương (trong AngleToleranceDegrees), cùng vị trí vuông góc (trong PerpendicularTolFeet), không hở quá AdjacentGapMaxFeet, và việc gộp không nuốt bất kỳ protectedPoint nào vào GIỮA thân đoạn mới.</summary>
    private static bool TryExtend(XYZ p0, XYZ dir, XYZ p1, Line candidate, IReadOnlyList<XYZ> protectedPoints, double perpendicularToleranceFeet, out XYZ newP0, out XYZ newP1)
    {
        newP0 = p0;
        newP1 = p1;

        var c0 = candidate.GetEndPoint(0);
        var c1 = candidate.GetEndPoint(1);
        var candDir = c1 - c0;
        if (candDir.IsZeroLength())
            return false;
        candDir = candDir.Normalize();

        var cosAngle = Math.Abs(dir.DotProduct(candDir));
        var angleDeg = Math.Acos(Math.Clamp(cosAngle, -1.0, 1.0)) * 180.0 / Math.PI;
        if (angleDeg > AngleToleranceDegrees)
            return false;

        var offset = c0 - p0;
        var t = offset.DotProduct(dir);
        var perp = offset - t * dir;
        if (perp.GetLength() > perpendicularToleranceFeet)
            return false;

        var curMin = Math.Min(0.0, (p1 - p0).DotProduct(dir));
        var curMax = Math.Max(0.0, (p1 - p0).DotProduct(dir));

        var tC0 = (c0 - p0).DotProduct(dir);
        var tC1 = (c1 - p0).DotProduct(dir);
        var candMin = Math.Min(tC0, tC1);
        var candMax = Math.Max(tC0, tC1);

        var gap = Math.Max(curMin, candMin) - Math.Min(curMax, candMax);
        if (gap > AdjacentGapMaxFeet)
            return false;

        var newMin = Math.Min(curMin, candMin);
        var newMax = Math.Max(curMax, candMax);

        if (SwallowsProtectedPoint(p0, dir, newMin, newMax, protectedPoints, perpendicularToleranceFeet))
            return false;

        newP0 = p0 + newMin * dir;
        newP1 = p0 + newMax * dir;
        return true;
    }

    /// <summary>True nếu có protectedPoint nằm TRÊN đường thẳng này (trong PerpendicularTolFeet) và rơi HẲN vào giữa [newMin,newMax] (không phải đúng 2 đầu mút) — tức merge sẽ xoá mất 1 nút thật đang được cạnh khác dùng.</summary>
    private static bool SwallowsProtectedPoint(XYZ p0, XYZ dir, double newMin, double newMax, IReadOnlyList<XYZ> protectedPoints, double perpendicularToleranceFeet)
    {
        const double edgeMarginFeet = 0.01;

        foreach (var point in protectedPoints)
        {
            var offset = point - p0;
            var t = offset.DotProduct(dir);
            var perp = offset - t * dir;
            if (perp.GetLength() > perpendicularToleranceFeet)
                continue;

            if (t > newMin + edgeMarginFeet && t < newMax - edgeMarginFeet)
                return true;
        }

        return false;
    }
}
