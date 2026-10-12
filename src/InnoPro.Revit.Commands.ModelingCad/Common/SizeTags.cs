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

    private static double Snap(double mm) => Math.Round(mm / WidthSnapMm, MidpointRounding.AwayFromZero) * WidthSnapMm;
}
