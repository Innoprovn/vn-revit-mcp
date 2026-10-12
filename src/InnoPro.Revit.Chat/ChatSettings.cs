using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InnoPro.Revit.Chat;

/// <summary>
/// Nhà cung cấp AI. Kind: "anthropic" (SDK Anthropic), "openai" (API OpenAI-compatible), "desktop" (gói thuê bao
/// qua ứng dụng ngoài nối MCP). Thêm nhà cung cấp OpenAI-compatible mới chỉ cần thêm một dòng.
/// </summary>
internal sealed record ProviderPreset(string Id, string Name, string Kind, string BaseUrl, bool NeedsKey, string DefaultModel, string Hint)
{
    public bool IsAnthropic => Kind == "anthropic";
    public bool IsDesktop => Kind == "desktop";
    public bool Local => BaseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
}

internal static class ProviderPresets
{
    // Hint: một dòng ngắn, chỉ nói lấy key ở đâu.
    public static readonly IReadOnlyList<ProviderPreset> All =
    [
        new("claude-desktop", "Claude (gói Pro/Max)", "desktop", "", false, "", "Dùng gói Claude qua ứng dụng Claude Desktop"),
        new("anthropic", "Claude API", "anthropic", "", true, "claude-opus-5-5", "Key: console.anthropic.com"),
        new("openai", "OpenAI", "openai", "https://api.openai.com/v1", true, "", "Key: platform.openai.com"),
        new("gemini", "Google Gemini", "openai", "https://generativelanguage.googleapis.com/v1beta/openai", true, "", "Key: aistudio.google.com"),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com/v1", true, "deepseek-chat", "Key: platform.deepseek.com"),
        new("qwen", "Qwen (Alibaba)", "openai", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", true, "qwen-plus", "Key: Alibaba Cloud Model Studio"),
        new("kimi", "Kimi (Moonshot)", "openai", "https://api.moonshot.ai/v1", true, "", "Key: platform.moonshot.ai"),
        new("glm", "GLM (Zhipu)", "openai", "https://api.z.ai/api/paas/v4", true, "", "Key: z.ai"),
        new("minimax", "MiniMax", "openai", "https://api.minimax.io/v1", true, "", "Key: minimax.io"),
        new("openrouter", "OpenRouter", "openai", "https://openrouter.ai/api/v1", true, "", "Một key, nhiều model · openrouter.ai"),
        new("ollama", "Ollama (trên máy)", "openai", "http://localhost:11434/v1", false, "", "Chạy trên máy, dữ liệu không ra ngoài"),
        new("lmstudio", "LM Studio (trên máy)", "openai", "http://localhost:1234/v1", false, "", "Chạy trên máy, dữ liệu không ra ngoài"),
        new("custom", "Tùy chỉnh", "openai", "http://localhost:8000/v1", false, "", "Máy chủ API dạng OpenAI"),
    ];

    public static ProviderPreset Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}
/// <summary>Cài đặt chat (không chứa bí mật) lưu ở chat.json; API key lưu trong Windows Credential Manager.</summary>
internal sealed class ChatSettings
{
    public string Provider { get; set; } = "claude-desktop";
    public Dictionary<string, string> Models { get; set; } = [];
    public Dictionary<string, string> BaseUrls { get; set; } = [];
    public bool DataNoticeAccepted { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ChatSettings Load(string dataDir)
    {
        try
        {
            var file = Path.Combine(dataDir, "chat.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<ChatSettings>(File.ReadAllText(file), Json) ?? new() : new();
        }
        catch (Exception) { return new(); }
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "chat.json"), JsonSerializer.Serialize(this, Json));
    }

    public string ModelFor(ProviderPreset p) => Models.TryGetValue(p.Id, out var m) && m.Length > 0 ? m : p.DefaultModel;

    public string BaseUrlFor(ProviderPreset p) => BaseUrls.TryGetValue(p.Id, out var u) && u.Length > 0 ? u : p.BaseUrl;
}

/// <summary>API key lưu trong Windows Credential Manager (mã hóa theo tài khoản Windows), không ghi ra file.</summary>
internal static class CredentialStore
{
    private const string Prefix = "InnoPro.VnRevitMcp/";

    public static string? Read(string providerId)
    {
        if (!CredRead(Prefix + providerId, 1, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            return cred.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public static void Write(string providerId, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new Credential
            {
                Type = 1, // CRED_TYPE_GENERIC
                TargetName = Prefix + providerId,
                CredentialBlob = ptr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref cred, 0)) throw new ChatException("Không lưu được API key vào Windows Credential Manager.");
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void Delete(string providerId) => CredDelete(Prefix + providerId, 1, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
