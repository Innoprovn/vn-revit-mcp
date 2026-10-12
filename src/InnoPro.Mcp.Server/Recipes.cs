using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Công thức: quy trình nhiều bước dạng file Markdown, công bố thành MCP prompt.
/// Người dùng chọn một công thức (ví dụ "/kiem-tra-chat-luong" trong Claude) là AI làm trọn quy trình.
/// Ai cũng viết được công thức, không cần biết lập trình:
///
///   ---
///   name: kiem-tra-chat-luong
///   description: Kiểm tra chất lượng mô hình và đề xuất cách sửa.
///   arguments:
///     - level: Tầng cần kiểm tra (bỏ trống = cả mô hình)
///   ---
///   Nội dung hướng dẫn cho AI, dùng {{level}} để chèn tham số.
/// </summary>
public static partial class Recipes
{
    public sealed record Recipe(string Name, string Description, IReadOnlyList<(string Name, string Description, bool Required)> Arguments, string Body);

    private static IEnumerable<string> Folders =>
    [
        Path.Combine(AppContext.BaseDirectory, "recipes"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "Recipes"),
    ];

    public static IReadOnlyList<Recipe> Load()
    {
        var recipes = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(folder, "*.md").OrderBy(f => f))
        {
            // Công thức của người dùng (thư mục sau) ghi đè công thức đi kèm cùng tên.
            if (Parse(File.ReadAllText(file)) is { } recipe) recipes[recipe.Name] = recipe;
        }
        return recipes.Values.OrderBy(r => r.Name).ToList();
    }

    public static Prompt ToPrompt(Recipe recipe) => new()
    {
        Name = recipe.Name,
        Description = recipe.Description,
        Arguments = recipe.Arguments.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required }).ToList(),
    };

    public static GetPromptResult Render(Recipe recipe, IReadOnlyDictionary<string, string>? arguments)
    {
        var text = Placeholder().Replace(recipe.Body, m =>
            arguments != null && arguments.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "(không chỉ định)");
        return new GetPromptResult
        {
            Description = recipe.Description,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        };
    }

    private static Recipe? Parse(string content)
    {
        var match = FrontMatter().Match(content.Replace("\r\n", "\n"));
        if (!match.Success) return null;

        string? name = null, description = null;
        var arguments = new List<(string, string, bool)>();
        foreach (var line in match.Groups[1].Value.Split('\n'))
        {
            if (line.StartsWith("name:")) name = line[5..].Trim();
            else if (line.StartsWith("description:")) description = line[12..].Trim();
            else if (ArgumentLine().Match(line) is { Success: true } arg)
                arguments.Add((arg.Groups[1].Value, arg.Groups[3].Value.Trim(), arg.Groups[2].Value == "*"));
        }
        return name == null ? null : new Recipe(name, description ?? name, arguments, match.Groups[2].Value.Trim());
    }

    [GeneratedRegex(@"^---\n(.*?)\n---\n(.*)$", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    // "  - level: mô tả" (tùy chọn) hoặc "  - level*: mô tả" (bắt buộc)
    [GeneratedRegex(@"^\s*-\s*([a-z_]+)(\*?)\s*:\s*(.*)$")]
    private static partial Regex ArgumentLine();

    [GeneratedRegex(@"\{\{\s*([a-z_]+)\s*\}\}")]
    private static partial Regex Placeholder();
}
