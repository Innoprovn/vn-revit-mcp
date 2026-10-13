using System.Text.RegularExpressions;

namespace InnoPro.Revit.Chat;

/// <summary>
/// Khung chat hiển thị văn bản thuần. Mô hình (nhất là mô hình nhỏ chạy trên máy) vẫn hay trả markdown:
/// bỏ dấu nhấn, tiêu đề, dòng kẻ bảng; hàng bảng thành "ô · ô · ô".
/// </summary>
internal static partial class PlainText
{
    public static string From(string markdown)
    {
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (TableRule().IsMatch(line)) continue;
            if (line.TrimStart().StartsWith('|'))
                line = string.Join(" · ", line.Trim().Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0));
            line = Heading().Replace(line, "");
            line = Emphasis().Replace(line, "$2");
            line = Code().Replace(line, "$1");
            lines.Add(line);
        }
        return Regex.Replace(string.Join("\n", lines), "\n{3,}", "\n\n").Trim();
    }

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"(\*\*|__)(.+?)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Code();
}
