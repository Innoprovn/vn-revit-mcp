using System.Text.Json;

namespace InnoPro.Revit.Chat;

internal sealed record ToolDef(string Name, string Description, JsonElement Schema);

internal sealed record ToolCall(string Id, string Name, JsonElement Arguments);

internal sealed record ToolResult(string Id, string Content, bool IsError);

/// <summary>Một lượt trả lời của mô hình: văn bản và/hoặc các lời gọi tool.</summary>
internal sealed record TurnResult(string Text, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>
/// Một nhà cung cấp AI. Mỗi provider tự giữ lịch sử hội thoại theo định dạng gốc của API,
/// nên không mất thông tin khi quay vòng gọi tool (ví dụ khối thinking có chữ ký của Claude).
/// </summary>
internal interface IChatProvider
{
    void Reset();

    /// <summary>Gửi tin nhắn người dùng (userText) hoặc kết quả tool (toolResults) và nhận lượt trả lời kế tiếp.</summary>
    Task<TurnResult> SendAsync(string? userText, IReadOnlyList<ToolResult>? toolResults, IReadOnlyList<ToolDef> tools,
        string system, CancellationToken token);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token);
}

/// <summary>Lỗi có thông điệp hiển thị cho người dùng.</summary>
internal sealed class ChatException(string message) : Exception(message);
