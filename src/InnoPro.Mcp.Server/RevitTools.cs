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

    private static readonly bool ExposeAll =
        string.Equals(Environment.GetEnvironmentVariable("VN_REVIT_MCP_TOOLS"), "all", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

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
            tools.Add(new Tool { Name = CommandCatalog.FindTool, Description = CommandCatalog.FindDescription(hidden), InputSchema = CommandCatalog.FindSchema });
            tools.Add(new Tool { Name = CommandCatalog.RunTool, Description = CommandCatalog.RunDescription, InputSchema = CommandCatalog.RunSchema });
        }

        tools.AddRange(commands.Where(c => ExposeAll || c.Pinned).Select(c => new Tool
        {
            Name = c.Name,
            Description = CommandCatalog.Describe(c),
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
                case CommandCatalog.FindTool:
                    return Text(CommandCatalog.Find(await LoadCommandsAsync(token), Arg(arguments, "query"), Arg(arguments, "name")), false);
                case CommandCatalog.RunTool:
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

    private static CallToolResult Json(JsonElement result) =>
        Text(result.ValueKind == JsonValueKind.Undefined ? "OK" : result.GetRawText(), isError: false);

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
