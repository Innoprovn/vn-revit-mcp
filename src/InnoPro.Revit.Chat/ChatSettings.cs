using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InnoPro.Revit.Chat;

/// <summary>Nhà cung cấp AI có sẵn. Thêm nhà cung cấp OpenAI-compatible mới chỉ cần thêm một dòng.</summary>
internal sealed record ProviderPreset(string Id, string Name, bool IsAnthropic, string BaseUrl, bool NeedsKey, string DefaultModel, bool Local, string Hint);

internal static class ProviderPresets
{
    public static readonly IReadOnlyList<ProviderPreset> All =
    [
        new("anthropic", "Claude (Anthropic)", true, "", true, "claude-opus-5-5", false,
            "Lấy API key tại console.anthropic.com. Model gợi ý: claude-opus-5-5, claude-sonnet-5-5 (rẻ hơn), claude-haiku-5-5 (rẻ nhất)."),
        new("openai", "OpenAI", false, "https://api.openai.com/v1", true, "", false,
            "Lấy API key tại platform.openai.com, rồi bấm ↻ để chọn model."),
        new("gemini", "Google Gemini", false, "https://generativelanguage.googleapis.com/v1beta/openai", true, "", false,
            "Lấy API key tại aistudio.google.com, rồi bấm ↻ để chọn model."),
        new("openrouter", "OpenRouter (nhiều model)", false, "https://openrouter.ai/api/v1", true, "", false,
            "Một API key dùng được nhiều model của nhiều hãng. Lấy key tại openrouter.ai."),
        new("ollama", "Ollama (chạy trên máy)", false, "http://localhost:11434/v1", false, "", true,
            "Chạy hoàn toàn trên máy, không gửi dữ liệu ra ngoài. Cài Ollama, tải một model hỗ trợ tool (ví dụ qwen), rồi bấm ↻."),
        new("lmstudio", "LM Studio (chạy trên máy)", false, "http://localhost:1234/v1", false, "", true,
            "Chạy hoàn toàn trên máy. Mở LM Studio, bật Local Server, nạp một model hỗ trợ tool, rồi bấm ↻."),
        new("custom", "Tùy chỉnh (OpenAI-compatible)", false, "http://localhost:8000/v1", false, "", false,
            "Mọi máy chủ có API dạng OpenAI Chat Completions. Nhập địa chỉ trong ô Base URL."),
    ];

    public static ProviderPreset Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

/// <summary>Cài đặt chat (không chứa bí mật) lưu ở chat.json; API key lưu trong Windows Credential Manager.</summary>
internal sealed class ChatSettings
{
    public string Provider { get; set; } = "anthropic";
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
