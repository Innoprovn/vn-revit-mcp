using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnoPro.Bridge;

/// <summary>
/// Mỗi phiên Revit mở một named pipe "InnoPro.VnRevitMcp.{pid}", chỉ cho cùng người dùng Windows kết nối.
/// Mỗi dòng là một JSON: client gửi BridgeRequest, add-in trả BridgeResponse cùng id.
/// </summary>
public static class BridgeProtocol
{
    public const string PipePrefix = "InnoPro.VnRevitMcp.";

    public const string MethodPing = "ping";
    public const string MethodListCommands = "list_commands";
    public const string MethodInvoke = "invoke";
    public const string MethodReloadCommands = "reload_commands";

    public static readonly Encoding Encoding = new UTF8Encoding(false);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PipeName(int processId) => PipePrefix + processId;
}

public sealed record BridgeRequest(string Id, string Method, JsonElement? Params = null);

public sealed record BridgeResponse(string Id, bool Ok, JsonElement? Result = null, string? Error = null);

public sealed record InvokeParams(string Name, JsonElement? Arguments = null);

public sealed record CommandDescriptor(
    string Name,
    string Description,
    string Category,
    string CommandSet,
    string TransactionMode,
    JsonElement InputSchema,
    bool Pinned = false);

/// <summary>Lỗi khi nạp một CommandSet hoặc biên dịch một file lệnh .cs.</summary>
public sealed record LoadError(string Source, string Message);

public sealed record RevitStatus(
    int ProcessId,
    string RevitVersion,
    string? ActiveDocument,
    string AddinVersion,
    int CommandCount,
    IReadOnlyList<LoadError>? LoadErrors = null);

public sealed record ReloadResult(int CommandCount, IReadOnlyList<LoadError> Errors);
