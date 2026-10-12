using System.Text.Json;
using InnoPro.Bridge;

namespace InnoPro.Revit.Chat;

/// <summary>
/// Vòng hội thoại có gọi tool: gửi câu hỏi → mô hình gọi lệnh Revit → trả kết quả → lặp tới khi có câu trả lời.
/// Dùng cùng chiến lược tiết kiệm token với MCP server: chỉ hiện lệnh Pinned, lệnh khác tra qua find_commands.
/// </summary>
internal sealed class ChatEngine(IRevitHost host)
{
    private const int MaxSteps = 20;
    private const int MaxResultChars = 20000;

    // Giữ cố định để lưu đệm được (prompt caching): thông tin thay đổi theo lượt đặt trong tin nhắn người dùng.
    private const string SystemPrompt = """
        Bạn là trợ lý AI chạy bên trong Autodesk Revit (add-in vn-revit-mcp của InnoPro), giúp kỹ sư/kiến trúc sư làm việc với mô hình đang mở.
        - Dùng các tool để đọc và thao tác mô hình; đừng đoán dữ liệu. Ngoài các tool đang thấy còn nhiều lệnh khác: tra bằng find_commands rồi chạy bằng run_command.
          Chỉ dùng run_csharp khi thật sự không có lệnh phù hợp.
        - Tiết kiệm: dùng find_elements với output=ids rồi truyền id sang lệnh khác, không đọc từng phần tử. Kết quả dạng {columns, rows} là bảng.
        - Chiều dài dùng milimét.
        - Trước khi SỬA mô hình (tạo, xóa, đổi tham số) với số lượng lớn hoặc khi người dùng chưa nói rõ, tóm tắt việc sẽ làm và hỏi xác nhận.
        - Trả lời bằng tiếng Việt, ngắn gọn, văn bản thuần (khung chat không hiển thị markdown); bảng nhỏ thì trình bày từng dòng.
        """;

    private IChatProvider? _provider;
    private bool _newConversation = true;

    public void Use(IChatProvider provider)
    {
        _provider = provider;
        _newConversation = true;
    }

    public void Reset()
    {
        _provider?.Reset();
        _newConversation = true;
    }

    /// <summary>onEvent(kind, text): kind là "assistant", "tool", "tool_error".</summary>
    public async Task RunAsync(string userText, Action<string, string> onEvent, CancellationToken token)
    {
        var provider = _provider ?? throw new ChatException("Chưa chọn nhà cung cấp AI.");
        var commands = host.ListCommandsJson();
        var tools = BuildTools(commands);

        var context = _newConversation ? $"[Revit: tài liệu đang mở là \"{host.ActiveDocument ?? "chưa mở"}\"]\n" : "";
        _newConversation = false;

        var turn = await provider.SendAsync(context + userText, null, tools, SystemPrompt, token);
        for (var step = 0; turn.ToolCalls.Count > 0; step++)
        {
            if (step >= MaxSteps)
            {
                onEvent("assistant", $"Đã dừng sau {MaxSteps} bước gọi lệnh. Hãy chia nhỏ yêu cầu.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(turn.Text)) onEvent("assistant", turn.Text.Trim());

            var results = new List<ToolResult>();
            foreach (var call in turn.ToolCalls)
            {
                token.ThrowIfCancellationRequested();
                onEvent("tool", $"{call.Name} {Compact(call.Arguments)}");
                var result = await ExecuteAsync(call, commands, token);
                if (result.IsError) onEvent("tool_error", result.Content);
                results.Add(result);
            }
            turn = await provider.SendAsync(null, results, tools, SystemPrompt, token);
        }

        onEvent("assistant", string.IsNullOrWhiteSpace(turn.Text) ? "(xong)" : turn.Text.Trim());
    }

    // Host trả chuỗi JSON; parse bằng System.Text.Json của module (khác phiên bản với host).
    private static List<ToolDef> BuildTools(string commandsJson) =>
        JsonDocument.Parse(CommandCatalog.ToolsJson(commandsJson)).RootElement.EnumerateArray()
            .Select(t => new ToolDef(t.GetProperty("name").GetString()!, t.GetProperty("description").GetString()!, t.GetProperty("input_schema").Clone()))
            .ToList();

    private async Task<ToolResult> ExecuteAsync(ToolCall call, string commands, CancellationToken token)
    {
        try
        {
            string content;
            if (call.Name == CommandCatalog.FindTool)
            {
                content = CommandCatalog.FindText(commands, Str(call.Arguments, "query"), Str(call.Arguments, "name"));
            }
            else if (call.Name == CommandCatalog.RunTool)
            {
                var name = Str(call.Arguments, "name") ?? throw new ChatException("run_command thiếu 'name'.");
                var args = call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("arguments", out var a) ? a.GetRawText() : null;
                content = await host.InvokeAsync(name, args, token);
            }
            else
            {
                content = await host.InvokeAsync(call.Name, call.Arguments.ValueKind == JsonValueKind.Undefined ? null : call.Arguments.GetRawText(), token);
            }

            if (content.Length > MaxResultChars)
                content = content[..MaxResultChars] + $"\n…(cắt bớt {content.Length - MaxResultChars} ký tự; hãy dùng limit nhỏ hơn hoặc lọc thêm)";
            return new ToolResult(call.Id, content, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ToolResult(call.Id, ex.Message, true);
        }
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Compact(JsonElement args)
    {
        var raw = args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText();
        return raw.Length > 160 ? raw[..160] + "…" : raw;
    }
}
