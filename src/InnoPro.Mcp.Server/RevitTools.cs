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

    private static readonly Knowledge.RevitApiSearch ApiSearch = new();

    private static readonly bool ExposeAll =
        string.Equals(Environment.GetEnvironmentVariable("VN_REVIT_MCP_TOOLS"), "all", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonElement StatusSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"pid":{"type":"integer","description":"Chuyển sang phiên Revit này (lấy từ danh sách sessions)."}}}""").RootElement;

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
                Description = "Kiểm tra kết nối Revit: phiên bản, tài liệu đang mở, số lệnh, lỗi nạp lệnh. Gọi khi lệnh khác báo lỗi kết nối. " +
                              "Mở nhiều Revit: liệt kê các phiên; truyền pid để làm việc với phiên khác.",
                InputSchema = StatusSchema,
            },
            new()
            {
                Name = ReloadTool,
                Description = "Nạp lại lệnh trong Revit (sau khi thêm/sửa CommandSet hoặc file lệnh .cs), không cần khởi động lại Revit. Trả về lỗi biên dịch nếu có.",
                InputSchema = EmptySchema,
            },
        };

        tools.Add(new Tool { Name = Skills.ToolName, Description = Skills.Description, InputSchema = Skills.Schema });
        tools.Add(new Tool { Name = TokenMeter.ToolName, Description = TokenMeter.Description, InputSchema = TokenMeter.Schema });
        tools.Add(new Tool { Name = Knowledge.RevitApiSearch.ToolName, Description = Knowledge.RevitApiSearch.Description, InputSchema = Knowledge.RevitApiSearch.Schema });

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

        var result = new ListToolsResult { Tools = tools };
        TokenMeter.RecordList(JsonSerializer.Serialize(tools.Select(t => new { t.Name, t.Description, t.InputSchema })), tools.Count);
        return result;
    }

    public async Task<CallToolResult> CallAsync(McpServer server, string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await CallCoreAsync(server, name, arguments, token);
        if (SelfUpdater.TakeNotice() is { } notice)
            result.Content.Add(new TextContentBlock { Text = notice + " Hãy báo người dùng." });
        TokenMeter.RecordCall(name, arguments == null ? null : JsonSerializer.Serialize(arguments),
            string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)), result.IsError == true, watch.ElapsedMilliseconds,
            result.Content.OfType<ImageContentBlock>().Sum(i => TokenMeter.ImageTokens(i.DecodedData.Span)));
        return result;
    }

    private async Task<CallToolResult> CallCoreAsync(McpServer server, string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        try
        {
            switch (name)
            {
                case StatusTool:
                    return await StatusAsync(arguments, token);
                case ReloadTool:
                    var reloaded = await bridge.SendAsync(BridgeProtocol.MethodReloadCommands, null, InvokeTimeout, token);
                    await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, token);
                    return Json(reloaded);
                case Skills.ToolName:
                    return Text(Skills.Run(Arg(arguments, "query"), Arg(arguments, "name")), false);
                case TokenMeter.ToolName:
                    var days = arguments != null && arguments.TryGetValue("days", out var dv) && dv.ValueKind == JsonValueKind.Number ? dv.GetInt32() : 1;
                    return Text(TokenMeter.Report(days), false);
                case Knowledge.RevitApiSearch.ToolName:
                    var limit = arguments != null && arguments.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 15;
                    return Text(await ApiSearch.RunAsync(Arg(arguments, "query"), Arg(arguments, "id"), Arg(arguments, "kind"), limit, token), false);
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

    /// <summary>Trạng thái phiên đang làm việc; nhiều phiên thì kèm danh sách (pid, phiên bản, tài liệu) để AI chọn.</summary>
    private async Task<CallToolResult> StatusAsync(IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var running = RevitBridgeClient.FindRevitProcesses();
        if (arguments != null && arguments.TryGetValue("pid", out var p) && p.ValueKind == JsonValueKind.Number)
        {
            if (!running.Contains(p.GetInt32())) return Text($"Không có phiên Revit PID {p.GetInt32()}. Đang mở: {string.Join(", ", running)}.", true);
            RevitBridgeClient.Selected = p.GetInt32();
        }
        var current = await bridge.SendAsync(BridgeProtocol.MethodPing, null, QuickTimeout, token);
        if (running.Count < 2) return Json(current);

        var sessions = new List<object>();
        foreach (var pid in running)
        {
            try
            {
                var r = await bridge.SendAsync(BridgeProtocol.MethodPing, null, QuickTimeout, token, pid);
                sessions.Add(new
                {
                    pid,
                    revit = r.GetProperty("revit_version").GetString(),
                    document = r.TryGetProperty("active_document", out var d) ? d.GetString() : null,
                    addin = r.GetProperty("addin_version").GetString(),
                    selected = pid == RevitBridgeClient.Selected,
                });
            }
            catch (RevitBridgeException ex) { sessions.Add(new { pid, error = ex.Message, selected = pid == RevitBridgeClient.Selected }); }
        }
        return Text(JsonSerializer.Serialize(new { current, sessions, note = "Đổi phiên: revit_status pid=..." }, BridgeProtocol.Json), false);
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

    private static CallToolResult Json(JsonElement result)
    {
        // Lệnh trả "image_file" (vd capture_view): gửi ảnh thẳng cho AI xem, kèm phần chữ không có đường dẫn.
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("image_file", out var f) && f.ValueKind == JsonValueKind.String
            && File.Exists(f.GetString()) && new FileInfo(f.GetString()!).Length < 5_000_000)
        {
            var path = f.GetString()!;
            var bytes = File.ReadAllBytes(path);
            try { File.Delete(path); } catch (IOException) { }
            var rest = new Dictionary<string, JsonElement>();
            foreach (var p in result.EnumerateObject().Where(p => p.Name != "image_file")) rest[p.Name] = p.Value;
            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(bytes, "image/png"),
                    new TextContentBlock { Text = JsonSerializer.Serialize(rest, BridgeProtocol.Json) },
                ],
            };
        }
        return Text(result.ValueKind == JsonValueKind.Undefined ? "OK" : result.GetRawText(), isError: false);
    }

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
