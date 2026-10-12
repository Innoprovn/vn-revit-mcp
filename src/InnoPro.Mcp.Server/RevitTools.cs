using System.Globalization;
using System.Text;
using System.Text.Json;
using InnoPro.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Công bố lệnh Revit thành tool MCP. Danh sách lấy từ add-in (nếu Revit đang chạy) hoặc từ danh mục ghi lần trước,
/// nên thêm lệnh mới vào CommandSet là có tool mới, không cần sửa server.
///
/// Tiết kiệm token: danh sách tool được gửi kèm MỌI lượt chat, nên mặc định chỉ hiện các lệnh "Pinned"
/// cùng find_commands / run_command; lệnh khác AI tra cứu khi cần. Đặt VN_REVIT_MCP_TOOLS=all để hiện tất cả.
/// </summary>
public sealed class RevitTools(RevitBridgeClient bridge)
{
    private const string StatusTool = "revit_status";
    private const string ReloadTool = "reload_commands";
    private const string FindTool = "find_commands";
    private const string RunTool = "run_command";

    private static readonly bool ExposeAll =
        string.Equals(Environment.GetEnvironmentVariable("VN_REVIT_MCP_TOOLS"), "all", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonElement EmptySchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement FindSchema = Schema("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Từ khóa (không cần dấu), ví dụ: tường, rebar, schedule, trùng. Bỏ trống = liệt kê tất cả."},
          "name":{"type":"string","description":"Tên lệnh chính xác để xem đầy đủ tham số."}}}
        """);

    private static readonly JsonElement RunSchema = Schema("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"Tên lệnh lấy từ find_commands."},
          "arguments":{"type":"object","description":"Tham số của lệnh."}},"required":["name"]}
        """);

    private static readonly string ManifestFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "commands.json");

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromMinutes(2.5);

    public async Task<ListToolsResult> ListAsync(CancellationToken token)
    {
        var commands = await LoadCommandsAsync(token);
        var hidden = commands.Where(c => !ExposeAll && !c.Pinned).ToList();

        var tools = new List<Tool>
        {
            new()
            {
                Name = StatusTool,
                Description = "Kiểm tra kết nối Revit: phiên bản, tài liệu đang mở, số lệnh, lỗi nạp lệnh. Gọi khi lệnh khác báo lỗi kết nối.",
                InputSchema = EmptySchema,
            },
            new()
            {
                Name = ReloadTool,
                Description = "Nạp lại lệnh trong Revit (sau khi thêm/sửa CommandSet hoặc file lệnh .cs), không cần khởi động lại Revit. Trả về lỗi biên dịch nếu có.",
                InputSchema = EmptySchema,
            },
        };

        if (hidden.Count > 0)
        {
            var groups = string.Join(", ", hidden.GroupBy(c => c.Category).OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Count()})"));
            tools.Add(new Tool
            {
                Name = FindTool,
                Description = $"Tra cứu thêm {hidden.Count} lệnh Revit chưa hiện ở đây: {groups}. " +
                              "Luôn tra trước khi tự viết mã. Trả về tên lệnh và tham số rút gọn.",
                InputSchema = FindSchema,
            });
            tools.Add(new Tool
            {
                Name = RunTool,
                Description = "Chạy một lệnh Revit tìm được qua find_commands.",
                InputSchema = RunSchema,
            });
        }

        tools.AddRange(commands.Where(c => ExposeAll || c.Pinned).Select(c => new Tool
        {
            Name = c.Name,
            Description = Describe(c),
            InputSchema = c.InputSchema,
        }));

        return new ListToolsResult { Tools = tools };
    }

    public async Task<CallToolResult> CallAsync(McpServer server, string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        try
        {
            switch (name)
            {
                case StatusTool:
                    return Json(await bridge.SendAsync(BridgeProtocol.MethodPing, null, QuickTimeout, token));
                case ReloadTool:
                    var reloaded = await bridge.SendAsync(BridgeProtocol.MethodReloadCommands, null, InvokeTimeout, token);
                    await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, token);
                    return Json(reloaded);
                case FindTool:
                    return Text(Find(await LoadCommandsAsync(token), Arg(arguments, "query"), Arg(arguments, "name")), false);
                case RunTool:
                    var target = Arg(arguments, "name") ?? throw new RevitBridgeException("Thiếu 'name'.");
                    var args = arguments != null && arguments.TryGetValue("arguments", out var a) ? a : (JsonElement?)null;
                    return Json(await Invoke(target, args, token));
                default:
                    return Json(await Invoke(name, JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>()), token));
            }
        }
        catch (RevitBridgeException ex)
        {
            return Text(ex.Message, isError: true);
        }
    }

    private Task<JsonElement> Invoke(string name, JsonElement? arguments, CancellationToken token) =>
        bridge.SendAsync(BridgeProtocol.MethodInvoke, new InvokeParams(name, arguments), InvokeTimeout, token);

    /// <summary>Kết quả tra cứu dạng văn bản: một dòng một lệnh, rẻ hơn JSON nhiều.</summary>
    private static string Find(IReadOnlyList<CommandDescriptor> commands, string? query, string? name)
    {
        if (name != null)
        {
            var command = commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                          ?? throw new RevitBridgeException($"Không có lệnh '{name}'.");
            return $"{command.Name} [{command.Category}]\n{Describe(command)}\nTham số (JSON Schema): {command.InputSchema.GetRawText()}";
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
    private static string Signature(JsonElement schema)
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

    private static string Describe(CommandDescriptor c) =>
        c.Description + (c.TransactionMode == "Auto" ? " Sửa mô hình, Undo được." : "");

    /// <summary>Câu đầu: dừng ở ". " hoặc xuống dòng, bỏ qua dấu chấm trong "..." hay số thập phân.</summary>
    private static string FirstSentence(string text)
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
    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch is '_' or '-' ? ' ' : ch);
        return sb.ToString();
    }

    private static string? Arg(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments != null && arguments.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<IReadOnlyList<CommandDescriptor>> LoadCommandsAsync(CancellationToken token)
    {
        try
        {
            var live = await bridge.SendAsync(BridgeProtocol.MethodListCommands, null, QuickTimeout, token);
            return live.Deserialize<List<CommandDescriptor>>(BridgeProtocol.Json) ?? [];
        }
        catch (RevitBridgeException)
        {
            // Revit chưa mở: dùng danh mục ghi lần trước để AI vẫn biết có những lệnh nào.
            if (!File.Exists(ManifestFile)) return [];
            return JsonSerializer.Deserialize<List<CommandDescriptor>>(await File.ReadAllTextAsync(ManifestFile, token), BridgeProtocol.Json) ?? [];
        }
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    private static CallToolResult Json(JsonElement result) =>
        Text(result.ValueKind == JsonValueKind.Undefined ? "OK" : result.GetRawText(), isError: false);

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
