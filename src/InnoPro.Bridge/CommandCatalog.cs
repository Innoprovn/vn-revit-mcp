using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InnoPro.Bridge;

/// <summary>
/// Tra cứu lệnh cho AI, dùng chung cho MCP server và khung chat trong Revit.
/// Kết quả là văn bản gọn (một dòng một lệnh) để tốn ít token.
/// </summary>
public static class CommandCatalog
{
    public const string FindTool = "find_commands";
    public const string RunTool = "run_command";

    public static readonly JsonElement FindSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Từ khóa (không cần dấu), ví dụ: tường, rebar, schedule, trùng. Bỏ trống = liệt kê tất cả."},
          "name":{"type":"string","description":"Tên lệnh chính xác để xem đầy đủ tham số."}}}
        """).RootElement;

    public static readonly JsonElement RunSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Tên lệnh lấy từ find_commands."},
          "arguments":{"type":"object","description":"Tham số của lệnh."}},"required":["name"]}
        """).RootElement;

    /// <summary>Mô tả tool find_commands, kèm các nhóm lệnh đang ẩn.</summary>
    public static string FindDescription(IEnumerable<CommandDescriptor> hidden)
    {
        var list = hidden.ToList();
        var groups = string.Join(", ", list.GroupBy(c => c.Category).OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Count()})"));
        return $"Tra cứu thêm {list.Count} lệnh Revit chưa hiện ở đây: {groups}. Luôn tra trước khi tự viết mã. Trả về tên lệnh và tham số rút gọn.";
    }

    public const string RunDescription = "Chạy một lệnh Revit tìm được qua find_commands.";

    public static string Describe(CommandDescriptor c) =>
        c.Description + (c.TransactionMode == "Auto" ? " Sửa mô hình, Undo được." : "");

    // Bản nhận/trả chuỗi JSON cho module chạy trong AssemblyLoadContext riêng (xem IRevitHost).

    /// <summary>Tool cho AI theo chiến lược tiết kiệm token: [{name, description, input_schema}].</summary>
    public static string ToolsJson(string commandsJson)
    {
        var commands = Parse(commandsJson);
        var tools = commands.Where(c => c.Pinned)
            .Select(c => new { name = c.Name, description = Describe(c), input_schema = c.InputSchema })
            .ToList();
        var hidden = commands.Where(c => !c.Pinned).ToList();
        if (hidden.Count > 0)
        {
            tools.Add(new { name = FindTool, description = FindDescription(hidden), input_schema = FindSchema });
            tools.Add(new { name = RunTool, description = RunDescription, input_schema = RunSchema });
        }
        return JsonSerializer.Serialize(tools);
    }

    public static string FindText(string commandsJson, string? query, string? name) => Find(Parse(commandsJson), query, name);

    private static List<CommandDescriptor> Parse(string commandsJson) =>
        JsonSerializer.Deserialize<List<CommandDescriptor>>(commandsJson, BridgeProtocol.Json) ?? [];

    /// <summary>Kết quả tra cứu: theo tên chính xác (đủ schema) hoặc theo từ khóa (danh sách gọn).</summary>
    public static string Find(IReadOnlyList<CommandDescriptor> commands, string? query, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var command = commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            return command == null
                ? $"Không có lệnh '{name}'."
                : $"{command.Name} [{command.Category}]\n{Describe(command)}\nTham số (JSON Schema): {command.InputSchema.GetRawText()}";
        }

        var words = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = commands
            .Where(c => words.All(w => Normalize($"{c.Name} {c.Category} {c.CommandSet} {c.Description}").Contains(w)))
            .OrderBy(c => c.Category).ThenBy(c => c.Name)
            .ToList();
        if (matches.Count == 0) return "Không có lệnh phù hợp. Thử từ khóa khác, hoặc dùng run_csharp nếu thật sự không có lệnh.";

        var text = new StringBuilder();
        foreach (var group in matches.GroupBy(c => c.Category))
        {
            text.Append('[').Append(group.Key).AppendLine("]");
            foreach (var c in group)
                text.Append(c.Name).Append('(').Append(Signature(c.InputSchema)).Append(") ").AppendLine(FirstSentence(c.Description));
        }
        text.Append("Gọi bằng run_command. * = bắt buộc. Xem chi tiết: find_commands name=<tên>.");
        return text.ToString();
    }

    /// <summary>Chữ ký tham số rút gọn: "level*, output:table|ids|count, limit:int".</summary>
    public static string Signature(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return "";
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet()
            : [];

        return string.Join(", ", properties.EnumerateObject().Select(p =>
        {
            var type = p.Value.TryGetProperty("enum", out var e) ? string.Join("|", e.EnumerateArray().Select(x => x.ToString()))
                : p.Value.TryGetProperty("type", out var t) ? (t.ValueKind == JsonValueKind.String ? t.GetString() : "any") : "any";
            var shortType = type switch { "string" => "", "integer" => ":int", "number" => ":num", "boolean" => ":bool", _ => ":" + type };
            return p.Name + (required.Contains(p.Name) ? "*" : "") + shortType;
        }));
    }

    /// <summary>Câu đầu: dừng ở ". " hoặc xuống dòng, bỏ qua dấu chấm trong "..." hay số thập phân.</summary>
    public static string FirstSentence(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') return text[..i].TrimEnd();
            if (text[i] == '.' && (i + 1 == text.Length || text[i + 1] == ' ') && (i == 0 || text[i - 1] != '.'))
                return text[..(i + 1)];
        }
        return text;
    }

    /// <summary>Bỏ dấu tiếng Việt, chữ thường, để tìm "tuong" ra "tường".</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch is '_' or '-' ? ' ' : ch);
        return sb.ToString();
    }
}

/// <summary>
/// Những gì Revit cung cấp cho một module chạy trong add-in (ví dụ khung chat).
/// Chỉ dùng kiểu cơ bản (chuỗi JSON) ở ranh giới: module có thể nạp System.Text.Json phiên bản khác
/// trong AssemblyLoadContext riêng, nên không được truyền JsonElement qua lại.
/// </summary>
public interface IRevitHost
{
    /// <summary>Mảng CommandDescriptor dạng JSON (snake_case).</summary>
    string ListCommandsJson();

    /// <summary>Chạy lệnh trên luồng Revit, trả JSON kết quả. Lỗi lệnh ném InvalidOperationException kèm thông điệp.</summary>
    Task<string> InvokeAsync(string name, string? argumentsJson, CancellationToken token);

    /// <summary>Tên tài liệu đang mở (null nếu chưa mở).</summary>
    string? ActiveDocument { get; }

    /// <summary>%LOCALAPPDATA%\InnoPro\vn-revit-mcp</summary>
    string DataDir { get; }

    string Version { get; }
}
