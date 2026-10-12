using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace InnoPro.Revit.Chat;

/// <summary>Claude qua SDK chính thức của Anthropic.</summary>
internal sealed class AnthropicProvider(string apiKey, string model) : IChatProvider
{
    private readonly AnthropicClient _client = new() { ApiKey = apiKey };
    private readonly List<MessageParam> _messages = [];

    public void Reset() => _messages.Clear();

    public async Task<TurnResult> SendAsync(string? userText, IReadOnlyList<ToolResult>? toolResults, IReadOnlyList<ToolDef> tools,
        string system, CancellationToken token)
    {
        if (toolResults is { Count: > 0 })
        {
            // Mọi tool_result của một lượt phải nằm trong CÙNG một tin nhắn user.
            List<ContentBlockParam> results = toolResults
                .Select(r => (ContentBlockParam)new ToolResultBlockParam { ToolUseID = r.Id, Content = r.Content, IsError = r.IsError })
                .ToList();
            _messages.Add(new() { Role = Role.User, Content = results });
        }
        else
        {
            _messages.Add(new() { Role = Role.User, Content = userText ?? "" });
        }

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = system,
            Tools = tools.Select(t => (ToolUnion)new Tool { Name = t.Name, Description = t.Description, InputSchema = Schema(t.Schema) }).ToList(),
            Messages = _messages.ToList(),
            // Lưu đệm tự động: tools + system + lịch sử được tính rẻ hơn ở các lượt sau.
            CacheControl = new CacheControlEphemeral(),
        };

        Message response;
        try
        {
            response = await _client.Messages.Create(parameters, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _messages.RemoveAt(_messages.Count - 1); // để gửi lại được
            throw new ChatException($"Claude báo lỗi: {ex.Message}");
        }

        if (response.StopReason == "refusal")
        {
            _messages.RemoveAt(_messages.Count - 1);
            return new TurnResult("Claude từ chối yêu cầu này. Hãy diễn đạt lại hoặc thử mô hình khác.", []);
        }

        // Không có .ToParam(): dựng lại từng khối để gửi lại ở lượt sau (giữ nguyên chữ ký của khối thinking).
        List<ContentBlockParam> assistant = [];
        var text = new List<string>();
        var calls = new List<ToolCall>();
        foreach (ContentBlock block in response.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                assistant.Add(new TextBlockParam { Text = t.Text });
                text.Add(t.Text);
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? use))
            {
                assistant.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                calls.Add(new ToolCall(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
            }
        }
        _messages.Add(new() { Role = Role.Assistant, Content = assistant });
        return new TurnResult(string.Join("\n", text), calls);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
    {
        var page = await _client.Models.List();
        return page.Items.Select(m => m.ID).ToList();
    }

    private static InputSchema Schema(JsonElement schema)
    {
        var properties = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.Clone();

        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];
        return new InputSchema { Properties = properties, Required = required };
    }
}
