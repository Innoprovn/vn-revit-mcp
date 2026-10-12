using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Trim/extend 2 cạnh kiểu "Pick Lines" gốc của Revit — nhận prev (canonical: GetEndPoint(0)=đầu cố định, GetEndPoint(1)=đầu hở hiện tại) và next vừa pick (raw, chưa rõ hướng, tự dò đầu gần prev để định hướng trừ khi forceNextOrientation). Collinear thì gộp làm 1 cạnh (thay prev), cắt nhau thì trả về 2 cạnh đã trim, song song/không cắt thì báo lỗi rõ ràng.</summary>
internal static class EdgeTrimmer
{
    private const double AngleToleranceDegrees = 2.0;
    private static readonly double PerpendicularTolFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);
    private static readonly double CloseTolFeet = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);

    /// <summary>forceNextOrientation=true dùng khi đóng vòng (next = edges[0]) — GetEndPoint(0) của next CHẮC CHẮN là đầu cần nối (góc khởi đầu ô sàn còn bỏ ngỏ), KHÔNG tự dò/đảo hướng.</summary>
    public static TrimResult TrimExtend(Curve prev, Curve nextRaw, bool forceNextOrientation = false)
    {
        var prevFixed = prev.GetEndPoint(0);
        var prevOpen = prev.GetEndPoint(1);

        Curve next;
        if (forceNextOrientation)
        {
            next = nextRaw;
        }
        else
        {
            var n0 = nextRaw.GetEndPoint(0);
            var n1 = nextRaw.GetEndPoint(1);
            next = n0.DistanceTo(prevOpen) <= n1.DistanceTo(prevOpen) ? nextRaw : nextRaw.CreateReversed();
        }

        var nextNear = next.GetEndPoint(0);
        var nextFar = next.GetEndPoint(1);

        // Gop lam 1 canh chi ap dung cho Line-Line - Arc "collinear" theo day cung khong co nghia la ban
        // than no thang, tranh lam mat do cong that (roi qua nhanh trim/extend thuong, se bao loi neu
        // khong the xu ly Arc).
        if (prev is Line && next is Line && IsCollinear(prevFixed, prevOpen, nextNear, nextFar))
        {
            if (prevFixed.DistanceTo(nextFar) <= CloseTolFeet)
                return TrimResult.Error("2 cạnh trùng nhau hoặc quá ngắn sau khi gộp — click lại cạnh khác.");

            Curve merged;
            try
            {
                merged = Line.CreateBound(prevFixed, nextFar);
            }
            catch (Exception)
            {
                return TrimResult.Error("Lỗi dựng cạnh gộp — click lại.");
            }

            return TrimResult.Collinear(merged);
        }

        if (!TryIntersect(prevFixed, prevOpen, nextNear, nextFar, out var p))
            return TrimResult.Error("2 cạnh song song, không cắt nhau — click lại cạnh khác.");

        var prevTrimmed = RebuildForPrev(prev, prevFixed, p);
        var nextTrimmed = RebuildForNext(next, p, nextFar);

        if (prevTrimmed is null || nextTrimmed is null)
            return TrimResult.Error("Không trim/extend được cạnh cong (Arc) tới đúng giao điểm — click lại đúng vị trí.");

        return TrimResult.Trimmed(prevTrimmed, nextTrimmed);
    }

    private static bool IsCollinear(XYZ prevFixed, XYZ prevOpen, XYZ nextNear, XYZ nextFar)
    {
        var dirPrev = prevOpen - prevFixed;
        var dirNext = nextFar - nextNear;
        if (dirPrev.IsZeroLength() || dirNext.IsZeroLength())
            return false;

        dirPrev = dirPrev.Normalize();
        dirNext = dirNext.Normalize();

        var cosAngle = Math.Abs(dirPrev.DotProduct(dirNext));
        var angleDeg = Math.Acos(Math.Clamp(cosAngle, -1.0, 1.0)) * 180.0 / Math.PI;
        if (angleDeg > AngleToleranceDegrees)
            return false;

        var offset = nextNear - prevFixed;
        var t = offset.DotProduct(dirPrev);
        var perp = offset - t * dirPrev;
        return perp.GetLength() <= PerpendicularTolFeet;
    }

    /// <summary>Giao điểm 2 đường thẳng vô hạn qua (a0,a1) và (b0,b1) trên mặt phẳng XY — false nếu song song (định thức ~0).</summary>
    private static bool TryIntersect(XYZ a0, XYZ a1, XYZ b0, XYZ b1, out XYZ point)
    {
        var dA = a1 - a0;
        var dB = b1 - b0;

        var denom = dA.X * dB.Y - dA.Y * dB.X;
        if (Math.Abs(denom) < 1e-6)
        {
            point = XYZ.Zero;
            return false;
        }

        var dx = b0.X - a0.X;
        var dy = b0.Y - a0.Y;
        var t = (dx * dB.Y - dy * dB.X) / denom;

        point = new XYZ(a0.X + t * dA.X, a0.Y + t * dA.Y, 0);
        return true;
    }

    /// <summary>Dựng lại prev với đầu cố định giữ nguyên (index0) và đầu còn lại thay bằng giao điểm P (index1). Line thì dựng thẳng lại; Arc KHÔNG hỗ trợ nối dài/rút ngắn thật ở v1 — chỉ chấp nhận nếu P đã nằm rất gần đầu hở sẵn có (coi như không cần trim), null (báo lỗi ở nơi gọi) nếu không.</summary>
    private static Curve? RebuildForPrev(Curve original, XYZ fixedPoint, XYZ newSharedPoint)
    {
        if (original is Line)
            return Line.CreateBound(fixedPoint, newSharedPoint);

        var p0 = original.GetEndPoint(0);
        var p1 = original.GetEndPoint(1);
        if (p0.DistanceTo(fixedPoint) <= CloseTolFeet && p1.DistanceTo(newSharedPoint) <= CloseTolFeet)
            return original;
        if (p1.DistanceTo(fixedPoint) <= CloseTolFeet && p0.DistanceTo(newSharedPoint) <= CloseTolFeet)
            return original.CreateReversed();

        return null;
    }

    /// <summary>Dựng lại next với giao điểm P ở index0 (đầu vừa nối) và đầu xa/hở mới ở index1 — cùng giới hạn Arc như RebuildForPrev.</summary>
    private static Curve? RebuildForNext(Curve original, XYZ newSharedPoint, XYZ fixedPoint)
    {
        if (original is Line)
            return Line.CreateBound(newSharedPoint, fixedPoint);

        var p0 = original.GetEndPoint(0);
        var p1 = original.GetEndPoint(1);
        if (p0.DistanceTo(newSharedPoint) <= CloseTolFeet && p1.DistanceTo(fixedPoint) <= CloseTolFeet)
            return original;
        if (p1.DistanceTo(newSharedPoint) <= CloseTolFeet && p0.DistanceTo(fixedPoint) <= CloseTolFeet)
            return original.CreateReversed();

        return null;
    }
}

internal enum TrimOutcomeKind
{
    Collinear,
    Trimmed,
    Error,
}

/// <summary>Kết quả 1 lượt trim/extend — Merged khi gộp (Collinear), PrevTrimmed+NextTrimmed khi cắt nhau (Trimmed), ErrorMessage khi song song/lỗi.</summary>
internal sealed record TrimResult(TrimOutcomeKind Kind, Curve? Merged, Curve? PrevTrimmed, Curve? NextTrimmed, string? ErrorMessage)
{
    public static TrimResult Collinear(Curve merged) => new(TrimOutcomeKind.Collinear, merged, null, null, null);
    public static TrimResult Trimmed(Curve prevTrimmed, Curve nextTrimmed) => new(TrimOutcomeKind.Trimmed, null, prevTrimmed, nextTrimmed, null);
    public static TrimResult Error(string message) => new(TrimOutcomeKind.Error, null, null, null, message);
}
