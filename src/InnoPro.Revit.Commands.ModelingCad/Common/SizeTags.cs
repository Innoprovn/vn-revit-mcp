using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Common;

/// <summary>
/// Đọc tag dạng "TÊN bxh" (vd "D1 (220x500)", "V2 250x3000") và gán cho cấu kiện dạng đoạn (dầm, vách)
/// bằng <see cref="TagMatcher"/>. Bề rộng b ghi trên tag phải khớp bề rộng đo từ hai nét CAD (làm tròn 5 mm),
/// nhờ vậy chữ của dầm song song sát bên không bị nhận nhầm.
/// </summary>
public static class SizeTags
{
    public const double DefaultSearchExpandMm = 1000.0;
    private const double WidthSnapMm = 5.0;
    private static readonly double AlongAllowance = UnitConvert.MmToFeet(300);

    /// <summary>Lấy kết quả khớp CUỐI để chữ "X" trong tên (vd "D6-X7A") không bị hiểu là dấu nhân.</summary>
    private static readonly Regex SizePattern = new(@"(?<b>\d+)\s*[xX×\*]\s*(?<h>\d+)", RegexOptions.Compiled);

    public sealed record Parsed(TextTag Tag, string Name, double BMm, double HMm);

    public static List<Parsed> Parse(IEnumerable<TextTag> tags)
    {
        var list = new List<Parsed>();
        foreach (var tag in tags)
        {
            var matches = SizePattern.Matches(tag.Text);
            if (matches.Count == 0) continue;
            var m = matches[^1];
            if (!double.TryParse(m.Groups["b"].Value, out var b) || !double.TryParse(m.Groups["h"].Value, out var h)) continue;
            list.Add(new Parsed(tag, tag.Text[..m.Index].Trim(' ', '(', '[', '-', ':', '\t'), b, h));
        }
        return list;
    }

    /// <summary>Gán tên + b + h cho từng cấu kiện; trả số cấu kiện đã có tag.</summary>
    public static int Attach<T>(IReadOnlyList<T> items, Func<T, (XYZ Start, XYZ End, double WidthFeet)> geometry,
        Action<T, string?, double, double> apply, IReadOnlyList<TextTag> tags, double expandFeet)
    {
        var parsed = Parse(tags);
        if (parsed.Count == 0) return 0;
        var assigned = TagMatcher.Assign(items, parsed.Select(p => p.Tag).ToList(), (item, tag) =>
        {
            var (s, e, width) = geometry(item);
            // Mẩu vụn (ngắn hơn 1.5 lần bề rộng hoặc 300 mm) không phải cấu kiện thật, không nhận tag.
            if (s.DistanceTo(e) < Math.Max(width * 1.5, UnitConvert.MmToFeet(300))) return null;
            var p = parsed.First(x => ReferenceEquals(x.Tag, tag));
            if (Math.Abs(Snap(p.BMm) - Snap(UnitConvert.FeetToMm(width))) > 1e-6) return null;
            return TagMatcher.Linear(tag, s, e, width / 2, expandFeet, AlongAllowance);
        });
        foreach (var (index, tag) in assigned)
        {
            var p = parsed.First(x => ReferenceEquals(x.Tag, tag));
            apply(items[index], p.Name.Length > 0 ? p.Name : null, p.BMm, p.HMm);
        }
        return assigned.Count;
    }

    /// <summary>
    /// Bỏ đoạn trùng: cùng một dầm vẽ ở nhiều layer (nét thấy + nét khuất) hoặc ghép chéo sinh nhiều đoạn chồng nhau.
    /// Hai đoạn song song có phần chồng trên 40% diện tích đoạn nhỏ thì giữ đoạn dài hơn.
    /// </summary>
    public static List<T> RemoveOverlaps<T>(IEnumerable<T> items, Func<T, (XYZ Start, XYZ End, double WidthFeet)> geometry)
    {
        var list = items.Select(i => (Item: i, Box: Footprint(geometry(i)))).OrderByDescending(x => x.Box.Length).ToList();
        var kept = new List<(T Item, (double X0, double Y0, double X1, double Y1, double Length, bool AlongX) Box)>();
        foreach (var x in list)
        {
            var area = (x.Box.X1 - x.Box.X0) * (x.Box.Y1 - x.Box.Y0);
            var duplicate = kept.Any(k =>
            {
                if (k.Box.AlongX != x.Box.AlongX) return false;
                var w = Math.Min(k.Box.X1, x.Box.X1) - Math.Max(k.Box.X0, x.Box.X0);
                var h = Math.Min(k.Box.Y1, x.Box.Y1) - Math.Max(k.Box.Y0, x.Box.Y0);
                return w > 0 && h > 0 && w * h > 0.4 * area;
            });
            if (!duplicate) kept.Add(x);
        }
        var keep = kept.Select(k => k.Item).ToHashSet();
        return items.Where(keep.Contains).ToList();
    }

    private static (double X0, double Y0, double X1, double Y1, double Length, bool AlongX) Footprint((XYZ Start, XYZ End, double WidthFeet) g)
    {
        var half = g.WidthFeet / 2;
        var alongX = Math.Abs(g.End.X - g.Start.X) >= Math.Abs(g.End.Y - g.Start.Y);
        return alongX
            ? (Math.Min(g.Start.X, g.End.X), Math.Min(g.Start.Y, g.End.Y) - half, Math.Max(g.Start.X, g.End.X), Math.Max(g.Start.Y, g.End.Y) + half, g.Start.DistanceTo(g.End), true)
            : (Math.Min(g.Start.X, g.End.X) - half, Math.Min(g.Start.Y, g.End.Y), Math.Max(g.Start.X, g.End.X) + half, Math.Max(g.Start.Y, g.End.Y), g.Start.DistanceTo(g.End), false);
    }

    private static double Snap(double mm) => Math.Round(mm / WidthSnapMm, MidpointRounding.AwayFromZero) * WidthSnapMm;
}
