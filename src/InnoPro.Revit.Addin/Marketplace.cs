using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Marketplace tiện ích: bộ lệnh đóng gói (CommandSet .zip) liệt kê trong community/index.json của repo, mục "extensions".
/// Cài = tải zip (kiểm SHA-256), giải nén vào %LOCALAPPDATA%\InnoPro\vn-revit-mcp\Commands\&lt;id&gt;, nạp nóng, không cần tắt Revit.
/// Mỗi tiện ích vừa có nút trên ribbon cho người dùng vừa có tool cho AI. AI tự cài được qua marketplace_install.
/// </summary>
internal static class Marketplace
{
    private const string IndexUrl = "https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/community/index.json";
    private const string MarkerFile = ".marketplace.json";

    internal sealed record Extension(string Id, string Name, string Description, string Category, string Author,
        string Version, string Url, string Sha256, int SizeKb, string[] Commands)
    {
        public string Dir => Path.Combine(Paths.UserCommandsDir, Id);
        public string? InstalledVersion => Marketplace.InstalledVersion(Id);
        public bool UpdateAvailable => InstalledVersion is { } v && Compare(Version, v) > 0;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    static Marketplace() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("vn-revit-mcp");

    /// <summary>Lệnh dựng sẵn cho AI (đăng ký trong mọi bản CommandRegistry).</summary>
    public static IReadOnlyList<IRevitCommand> Commands { get; } = [new ListCommand(), new InstallCommand(), new RemoveCommand()];

    public static List<Extension> Fetch()
    {
        var json = Http.GetStringAsync(IndexUrl + "?t=" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("extensions", out var list)) return [];
        string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return list.EnumerateArray().Select(e => new Extension(
            S(e, "id"), S(e, "name"), S(e, "description"), S(e, "category"), S(e, "author"), S(e, "version"), S(e, "url"), S(e, "sha256"),
            e.TryGetProperty("size_kb", out var k) && k.TryGetInt32(out var kb) ? kb : 0,
            e.TryGetProperty("commands", out var c) ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : []))
            .Where(e => e.Id.Length > 0 && e.Url.Length > 0)
            .ToList();
    }

    public static string? InstalledVersion(string id)
    {
        var marker = Path.Combine(Paths.UserCommandsDir, id, MarkerFile);
        if (!File.Exists(marker)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (Exception) { return "?"; }
    }

    /// <summary>Tải, kiểm SHA-256, giải nén thay bản cũ. Không nạp lại: người gọi tự nạp (ReloadAfter).</summary>
    public static void Install(Extension ext)
    {
        if (!ext.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new CommandException($"Link tải của '{ext.Id}' không phải https.");
        var bytes = Http.GetByteArrayAsync(ext.Url).GetAwaiter().GetResult();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, ext.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CommandException($"Gói '{ext.Id}' tải về không khớp mã kiểm tra (SHA-256), đã hủy cài.");

        var staging = ext.Dir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
            zip.ExtractToDirectory(staging);
        // Zip có thể bọc một thư mục gốc: lấy thư mục chứa commandset.json.
        var root = Directory.EnumerateFiles(staging, "commandset.json", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                   ?? throw new CommandException($"Gói '{ext.Id}' không có commandset.json.");
        File.WriteAllText(Path.Combine(root!, MarkerFile),
            JsonSerializer.Serialize(new { id = ext.Id, version = ext.Version, installed = DateTime.Now.ToString("s") }));

        // CommandSet nạp từ byte nên file không bị khóa: thay thư mục cũ được ngay cả khi Revit đang chạy.
        if (Directory.Exists(ext.Dir)) Directory.Delete(ext.Dir, true);
        Directory.CreateDirectory(Paths.UserCommandsDir);
        Directory.Move(root!, ext.Dir);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Log.Info($"Marketplace: đã cài {ext.Id} {ext.Version}");
    }

    public static void Remove(string id)
    {
        var dir = Path.Combine(Paths.UserCommandsDir, id);
        if (!File.Exists(Path.Combine(dir, MarkerFile)))
            throw new CommandException($"'{id}' không phải tiện ích cài từ marketplace.");
        Directory.Delete(dir, true);
        Log.Info($"Marketplace: đã gỡ {id}");
    }

    public static int Compare(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    private static Extension Find(string id) =>
        Fetch().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new CommandException($"Marketplace không có tiện ích '{id}'. Gọi marketplace_list để xem danh sách.");

    private sealed class ListCommand : RevitCommandBase
    {
        public override string Name => "marketplace_list";
        public override string Category => "Hệ thống";
        public override string Description =>
            "Danh sách tiện ích trong marketplace (bộ lệnh cài thêm như mô phỏng 4D): tên, phiên bản, đã cài chưa, có bản mới không.";

        public override object? Execute(RevitCommandContext context, JsonElement arguments) =>
            Table.Of(Fetch().Select(e => new
            {
                id = e.Id, name = e.Name, version = e.Version, installed = e.InstalledVersion,
                update = e.UpdateAvailable ? true : (bool?)null, category = e.Category, description = e.Description,
            }));
    }

    private sealed class InstallCommand : RevitCommandBase
    {
        public override string Name => "marketplace_install";
        public override string Category => "Hệ thống";
        public override string Description =>
            "Cài hoặc cập nhật một tiện ích từ marketplace theo id; nạp ngay, không cần tắt Revit. Trả về các lệnh mới có.";
        public override string InputSchema => """
            {"type":"object","properties":{"id":{"type":"string","description":"Id tiện ích (từ marketplace_list)."}},"required":["id"]}
            """;

        public override object? Execute(RevitCommandContext context, JsonElement arguments)
        {
            var ext = Find(Args.GetString(arguments, "id"));
            var before = ext.InstalledVersion;
            Install(ext);
            var reload = App.Instance?.ReloadCommands();
            return new
            {
                id = ext.Id, version = ext.Version, previous = before, commands = ext.Commands,
                command_count = reload?.CommandCount, errors = reload?.Errors.Count > 0 ? reload.Errors : null,
            };
        }
    }

    private sealed class RemoveCommand : RevitCommandBase
    {
        public override string Name => "marketplace_remove";
        public override string Category => "Hệ thống";
        public override string Description => "Gỡ một tiện ích đã cài từ marketplace.";
        public override string InputSchema => """
            {"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}
            """;

        public override object? Execute(RevitCommandContext context, JsonElement arguments)
        {
            var id = Args.GetString(arguments, "id");
            Remove(id);
            var reload = App.Instance?.ReloadCommands();
            return new { removed = id, command_count = reload?.CommandCount };
        }
    }
}
