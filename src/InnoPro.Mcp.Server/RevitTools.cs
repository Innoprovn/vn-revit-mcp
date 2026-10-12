using System.Text.Json;
using InnoPro.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Danh sách tool lấy từ add-in (nếu Revit đang chạy) hoặc từ danh mục add-in đã ghi lần trước.
/// Thêm lệnh mới vào CommandSet là có tool mới, không cần sửa server.
/// </summary>
public sealed class RevitTools(RevitBridgeClient bridge)
{
    private const string StatusTool = "revit_status";
    private const string ReloadTool = "reload_commands";

    private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

    private static readonly string ManifestFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "commands.json");

    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromMinutes(2.5);

    public async Task<ListToolsResult> ListAsync(CancellationToken token)
    {
        var tools = new List<Tool>
        {
            new()
            {
                Name = StatusTool,
                Description = "Kiểm tra kết nối tới Revit: phiên bản Revit, tài liệu đang mở, số lệnh có sẵn. Gọi đầu tiên khi lệnh khác báo lỗi kết nối.",
                InputSchema = EmptySchema,
            },
            new()
            {
                Name = ReloadTool,
                Description = "Nạp lại các CommandSet trong Revit sau khi cập nhật DLL lệnh, không cần khởi động lại Revit. Danh sách tool được làm mới sau đó.",
                InputSchema = EmptySchema,
            },
        };

        foreach (var command in await LoadCommandsAsync(token))
        {
            tools.Add(new Tool
            {
                Name = command.Name,
                Description = $"[{command.Category}] {command.Description}" +
                              (command.TransactionMode == "Auto" ? " (Sửa mô hình; có thể Undo trong Revit.)" : ""),
                InputSchema = command.InputSchema,
            });
        }

        return new ListToolsResult { Tools = tools };
    }

    public async Task<CallToolResult> CallAsync(McpServer server, string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        try
        {
            var result = name switch
            {
                StatusTool => await bridge.SendAsync(BridgeProtocol.MethodPing, null, QuickTimeout, token),
                ReloadTool => await bridge.SendAsync(BridgeProtocol.MethodReloadCommands, null, InvokeTimeout, token),
                _ => await bridge.SendAsync(BridgeProtocol.MethodInvoke,
                    new InvokeParams(name, JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>())),
                    InvokeTimeout, token),
            };

            if (name == ReloadTool)
                await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, token);

            return Text(result.ValueKind == JsonValueKind.Undefined ? "OK" : result.GetRawText(), isError: false);
        }
        catch (RevitBridgeException ex)
        {
            return Text(ex.Message, isError: true);
        }
    }

    private async Task<IReadOnlyList<CommandDescriptor>> LoadCommandsAsync(CancellationToken token)
    {
        try
        {
            var live = await bridge.SendAsync(BridgeProtocol.MethodListCommands, null, QuickTimeout, token);
            return live.Deserialize<List<CommandDescriptor>>(BridgeProtocol.Json) ?? [];
        }
        catch (RevitBridgeException)
        {
            // Revit chưa mở: dùng danh mục ghi lần trước để AI vẫn biết có những tool nào.
            if (!File.Exists(ManifestFile)) return [];
            return JsonSerializer.Deserialize<List<CommandDescriptor>>(await File.ReadAllTextAsync(ManifestFile, token), BridgeProtocol.Json) ?? [];
        }
    }

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
