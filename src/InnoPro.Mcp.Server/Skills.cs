using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Bộ kỹ năng Revit đã kiểm chứng: mỗi kỹ năng một file Markdown (mục tiêu, cách làm, bẫy, dự phòng giao diện, kiểm tra).
/// AI tra bằng tool revit_skill khi gặp việc khó hoặc nhiều bước, chỉ tải đúng kỹ năng cần nên ít token.
/// Kỹ năng đi kèm ở thư mục skills cạnh exe; kỹ năng riêng của công ty đặt ở %LOCALAPPDATA%\InnoPro\vn-revit-mcp\Skills (ghi đè cùng tên).
///
///   ---
///   name: dim-thep
///   title: Dim khoảng cách thép
///   keywords: dim, dimension, rebar, thép, kích thước
///   verified: Revit 2026, 13/10/2026
///   ---
///   Nội dung.
/// </summary>
public static partial class Skills
{
    public sealed record Skill(string Name, string Title, string Keywords, string Verified, string Body);

    public const string ToolName = "revit_skill";

    public const string Description =
        "Bộ kỹ năng Revit đã kiểm chứng (cách làm, bẫy thường gặp, cách kiểm tra). Gọi TRƯỚC khi làm việc khó hoặc nhiều bước " +
        "(dim thép, dựng hình từ CAD, tiến độ 4D, việc không có lệnh sẵn...): query để tìm, name để đọc đầy đủ.";

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Việc cần làm, tiếng Việt hoặc Anh."},
          "name":{"type":"string","description":"Tên kỹ năng để đọc đầy đủ."}}}
        """).RootElement;

    private static IEnumerable<string> Folders =>
    [
        Path.Combine(AppContext.BaseDirectory, "skills"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "Skills"),
    ];

    public static IReadOnlyList<Skill> Load()
    {
        var skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").OrderBy(f => f))
            if (Parse(File.ReadAllText(file)) is { } skill) skills[skill.Name] = skill;
        return skills.Values.OrderBy(s => s.Name).ToList();
    }

    public static string Run(string? query, string? name)
    {
        var skills = Load();
        if (skills.Count == 0) return "Chưa có kỹ năng nào.";
        if (!string.IsNullOrWhiteSpace(name))
        {
            var s = skills.FirstOrDefault(k => k.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            return s == null ? $"Không có kỹ năng '{name}'. Có: {string.Join(", ", skills.Select(k => k.Name))}."
                : $"# {s.Title}\n(đã kiểm chứng: {s.Verified})\n\n{s.Body}";
        }

        var terms = Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranked = skills
            .Select(s => (Skill: s, Score: terms.Sum(t => (Fold(s.Keywords + " " + s.Title).Contains(t) ? 3 : 0) + (Fold(s.Body).Contains(t) ? 1 : 0))))
            .Where(x => terms.Length == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();
        if (ranked.Count == 0) return "Không có kỹ năng khớp. Danh sách: " + string.Join(", ", skills.Select(k => $"{k.Name} ({k.Title})"));
        var sb = new StringBuilder("Kỹ năng khớp (đọc đầy đủ bằng name):\n");
        foreach (var (s, _) in ranked.Take(6)) sb.Append($"- {s.Name}: {s.Title} [đã kiểm chứng: {s.Verified}]\n");
        return sb.ToString().TrimEnd();
    }

    private static Skill? Parse(string text)
    {
        var m = FrontMatter().Match(text);
        if (!m.Success) return null;
        string Field(string key) => Regex.Match(m.Groups[1].Value, $@"^{key}:\s*(.+)$", RegexOptions.Multiline) is { Success: true } f ? f.Groups[1].Value.Trim() : "";
        var name = Field("name");
        return name.Length == 0 ? null : new Skill(name, Field("title"), Field("keywords"), Field("verified"), text[m.Length..].Trim());
    }

    private static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    [GeneratedRegex(@"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();
}
