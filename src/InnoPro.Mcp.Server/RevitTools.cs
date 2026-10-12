using System.Text.Json;
using InnoPro.Bridge;
using ModelContextProtocol.Protocol;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Danh sách tool lấy từ add-in (nếu Revit đang chạy) hoặc từ danh mục add-in đã ghi lần trước.
/// Thêm lệnh mới vào CommandSet là có tool mới, không cần sửa server.
/// </summary>
public sealed class RevitTools(RevitBridgeClient bridge)
{
    private const string StatusTool = "revit_status";

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
                InputSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement,
            },
        };

        foreach (var command in await LoadCommandsAsync(token))
        {
            tools.Add(new Tool
            {
                Name = command.Name,
                Description = $"[{command.Category}] {command.Description}" +
                              (command.TransactionMode == "None" ? "" : " (Sửa mô hình; có thể Undo trong Revit.)"),
                InputSchema = command.InputSchema,
            });
        }

        return new ListToolsResult { Tools = tools };
    }

    public async Task<CallToolResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        try
        {
            var result = name == StatusTool
                ? await bridge.SendAsync(BridgeProtocol.MethodPing, null, QuickTimeout, token)
                : await bridge.SendAsync(BridgeProtocol.MethodInvoke,
                    new InvokeParams(name, JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>())),
                    InvokeTimeout, token);

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
