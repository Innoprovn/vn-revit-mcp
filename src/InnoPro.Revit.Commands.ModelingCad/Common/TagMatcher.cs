using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Common;

/// <summary>
/// Gán chữ (tag) cho cấu kiện theo vùng, kiểu người đọc bản vẽ:
/// - Leader chỉ vào cấu kiện là bằng chứng mạnh nhất (điểm gần như bằng 0).
/// - Không có leader: chữ phải nằm trong vùng chữ nhật xoay theo cấu kiện (dọc trục rộng, vuông góc hẹp),
///   ưu tiên chữ nằm song song với cấu kiện, gần trục và gần giữa nhịp.
/// - Mỗi chữ ưu tiên cấu kiện "hợp nhất hai chiều" (chữ chọn cấu kiện và cấu kiện chọn chữ),
///   cấu kiện còn lại mới dùng chung chữ (một tag ghi cho cả dải dầm liên tục).
/// Điểm càng nhỏ càng tốt; null là nằm ngoài vùng.
/// </summary>
public static class TagMatcher
{
    /// <summary>Điểm gán khi chân leader rơi đúng vào cấu kiện.</summary>
    private const double LeaderScore = 0.001;

    /// <summary>Cấu kiện dạng đoạn (dầm, tường, vách): trục Start→End, nửa bề rộng, vùng tìm vuông góc expand.</summary>
    public static double? Linear(TextTag tag, XYZ start, XYZ end, double halfWidth, double expand, double alongAllowance)
    {
        var s = new XYZ(start.X, start.Y, 0);
        var axis = new XYZ(end.X - start.X, end.Y - start.Y, 0);
        var length = axis.GetLength();
        if (length < 1e-9) return Point(tag, s, null, halfWidth, halfWidth, expand);
        var dir = axis / length;
        var perp = new XYZ(-dir.Y, dir.X, 0);
        var tolerance = Math.Max(halfWidth, UnitConvert.MmToFeet(150));

        foreach (var tip in tag.LeaderTips)
        {
            var v = tip - s;
            var along = v.DotProduct(dir);
            var across = Math.Abs(v.DotProduct(perp));
            if (across <= halfWidth + tolerance && along >= -tolerance && along <= length + tolerance)
                return LeaderScore * (1 + across);
        }

        var c = tag.Center - s;
        var a = c.DotProduct(dir);
        var p = Math.Abs(c.DotProduct(perp));
        // Khung chữ được phép chạm vùng: trừ nửa kích thước chữ theo từng phương.
        var parallel = Math.Abs(tag.Direction.DotProduct(dir));
        var halfAlong = parallel * tag.HalfLength + (1 - parallel) * tag.HalfHeight;
        var halfAcross = parallel * tag.HalfHeight + (1 - parallel) * tag.HalfLength;
        if (p - halfAcross > halfWidth + expand) return null;
        if (a + halfAlong < -alongAllowance || a - halfAlong > length + alongAllowance) return null;

        var score = 3 * Math.Max(0, p - halfWidth) + Math.Abs(a - length / 2) * 0.5;
        score *= parallel > 0.97 ? 0.6 : parallel < 0.3 ? 1.4 : 1.0;
        return score + 1e-6;
    }

    /// <summary>
    /// Cấu kiện dạng điểm (cửa, cột, cọc): tâm, hướng (vd hướng tường chứa cửa; null là không xoay),
    /// nửa kích thước theo hai phương, vùng tìm mở rộng expand quanh khung chữ nhật đó.
    /// </summary>
    public static double? Point(TextTag tag, XYZ center, XYZ? direction, double halfAlong, double halfAcross, double expand)
    {
        var c0 = new XYZ(center.X, center.Y, 0);
        var dir = direction == null || direction.GetLength() < 1e-9 ? XYZ.BasisX : new XYZ(direction.X, direction.Y, 0).Normalize();
        var perp = new XYZ(-dir.Y, dir.X, 0);
        var tolerance = UnitConvert.MmToFeet(150);

        foreach (var tip in tag.LeaderTips)
        {
            var v = tip - c0;
            if (Math.Abs(v.DotProduct(dir)) <= halfAlong + tolerance && Math.Abs(v.DotProduct(perp)) <= halfAcross + tolerance)
                return LeaderScore * (1 + v.GetLength());
        }

        var c = tag.Center - c0;
        var a = Math.Abs(c.DotProduct(dir));
        var p = Math.Abs(c.DotProduct(perp));
        var gapA = Math.Max(0, a - halfAlong - tag.HalfLength);
        var gapP = Math.Max(0, p - halfAcross - tag.HalfHeight);
        if (gapA > expand || gapP > expand) return null;
        return Math.Sqrt(gapA * gapA + gapP * gapP) + 0.1 * c.GetLength() + 1e-6;
    }

    /// <summary>
    /// Gán tag cho từng cấu kiện. Vòng 1: cặp hợp nhất hai chiều. Vòng 2: cấu kiện còn lại lấy chữ điểm tốt nhất
    /// (được dùng chung), trừ chữ đã có leader chỉ rõ cấu kiện khác.
    /// </summary>
    public static Dictionary<int, TextTag> Assign<T>(IReadOnlyList<T> items, IReadOnlyList<TextTag> tags, Func<T, TextTag, double?> score)
    {
        var scores = new List<(int Item, int Tag, double Score)>();
        for (var i = 0; i < items.Count; i++)
        for (var t = 0; t < tags.Count; t++)
            if (score(items[i], tags[t]) is { } s) scores.Add((i, t, s));

        var bestForItem = scores.GroupBy(x => x.Item).ToDictionary(g => g.Key, g => g.MinBy(x => x.Score));
        var bestForTag = scores.GroupBy(x => x.Tag).ToDictionary(g => g.Key, g => g.MinBy(x => x.Score));
        var result = new Dictionary<int, TextTag>();
        var leaderOwned = new HashSet<int>();

        foreach (var (item, best) in bestForItem)
        {
            if (bestForTag[best.Tag].Item != item) continue;
            result[item] = tags[best.Tag];
            if (best.Score <= LeaderScore * 1000) leaderOwned.Add(best.Tag);
        }

        foreach (var (item, _) in bestForItem)
        {
            if (result.ContainsKey(item)) continue;
            var pick = scores.Where(x => x.Item == item && (!leaderOwned.Contains(x.Tag) || x.Score <= LeaderScore * 1000))
                .OrderBy(x => x.Score).FirstOrDefault();
            if (scores.Any(x => x.Item == item && x.Tag == pick.Tag && x.Score == pick.Score))
                result[item] = tags[pick.Tag];
        }
        return result;
    }
}
