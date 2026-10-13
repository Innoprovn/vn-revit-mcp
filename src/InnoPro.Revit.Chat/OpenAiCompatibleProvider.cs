using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InnoPro.Revit.Chat;

/// <summary>
/// API dạng Chat Completions (OpenAI-compatible): OpenAI, Google Gemini, OpenRouter, và AI chạy trên máy
/// như Ollama, LM Studio. Một bộ chuyển đổi cho rất nhiều nhà cung cấp.
/// </summary>
internal sealed class OpenAiCompatibleProvider : IChatProvider
{
    // Model local có thể chậm, nên cho thời gian chờ dài.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly JsonArray _messages = [];

    public OpenAiCompatibleProvider(string baseUrl, string? apiKey, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _model = model;
        _isLocal = Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
    }

    private readonly bool _isLocal;

    public void Reset() => _messages.Clear();

    /// <summary>Máy chủ trên máy (Ollama, LM Studio, llama.cpp) thường chạy mô hình nhỏ, ngữ cảnh 8K–32K.</summary>
    public int MaxToolResultChars => _isLocal ? 4000 : 20000;

    public async Task<TurnResult> SendAsync(string? userText, IReadOnlyList<ToolResult>? toolResults, IReadOnlyList<ToolDef> tools,
        string system, CancellationToken token)
    {
        var added = 0;
        if (toolResults is { Count: > 0 })
        {
            foreach (var r in toolResults)
            {
                _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.Id, ["content"] = r.IsError ? "LỖI: " + r.Content : r.Content });
                added++;
            }
        }
        else
        {
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = userText ?? "" });
            added++;
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray([new JsonObject { ["role"] = "system", ["content"] = system }, .. _messages.Select(m => m!.DeepClone())]),
        };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Schema.GetRawText()),
                },
            }).ToArray());
        }

        JsonNode response;
        try
        {
            response = await PostAsync("/chat/completions", body, token);
        }
        catch
        {
            for (var i = 0; i < added; i++) _messages.RemoveAt(_messages.Count - 1); // để gửi lại được
            throw;
        }

        var message = response["choices"]?[0]?["message"]?.AsObject()
                      ?? throw new ChatException("Phản hồi không có nội dung (choices rỗng).");

        var calls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls)
            {
                var id = call?["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                var name = call?["function"]?["name"]?.ToString() ?? "";
                var raw = call?["function"]?["arguments"];
                // Đa số API trả arguments là chuỗi JSON; vài model local trả thẳng object.
                var args = raw is JsonValue v && v.TryGetValue<string>(out var s)
                    ? ParseOrEmpty(s)
                    : JsonSerializer.SerializeToElement(raw ?? new JsonObject());
                calls.Add(new ToolCall(id, name, args));
            }
        }

        // Lưu lại đúng tin nhắn assistant (kèm tool_calls) để lượt sau API ghép được kết quả tool.
        var echo = new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.DeepClone() };
        if (message["tool_calls"] is JsonArray tc) echo["tool_calls"] = tc.DeepClone();
        _messages.Add(echo);

        return new TurnResult(message["content"]?.ToString() ?? "", calls);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
        Authorize(request);
        using var response = await Http.SendAsync(request, token);
        var json = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new ChatException($"Không lấy được danh sách model ({(int)response.StatusCode}): {Shorten(json)}");
        return JsonNode.Parse(json)?["data"]?.AsArray().Select(m => m?["id"]?.ToString() ?? "").Where(s => s.Length > 0).OrderBy(s => s).ToList()
               ?? [];
    }

    private async Task<JsonNode> PostAsync(string path, JsonObject body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, token);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatException($"Không kết nối được {_baseUrl}: {ex.Message}. Nếu dùng AI trên máy (Ollama, LM Studio), kiểm tra chương trình đã chạy.");
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new ChatException($"Lỗi {(int)response.StatusCode} từ {_baseUrl}: {Shorten(json)}");
            return JsonNode.Parse(json) ?? throw new ChatException("Phản hồi rỗng.");
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (_apiKey != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private static JsonElement ParseOrEmpty(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static string Shorten(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
