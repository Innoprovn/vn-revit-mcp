using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Tự cập nhật: MCP server kiểm tra GitHub (tối đa 12 giờ/lần, chạy nền, không chặn AI).
/// - Bản vn-revit-mcp mới: tải zip, kiểm SHA-256 do GitHub công bố, chạy bộ cài trong gói (-NoClients; bộ cài tự xử lý
///   Revit đang mở và ứng dụng MSIX). Revit đang mở tự nạp add-in mới (bộ nạp); server mới dùng từ lần AI client mở lại.
/// - Tiện ích marketplace đã cài có bản mới: tải, kiểm SHA-256, thay thư mục, nhờ Revit đang mở nạp lại ngay.
/// Kết quả ghi update.json; lần gọi tool kế tiếp kèm một dòng thông báo cho AI. Tắt: VN_REVIT_MCP_AUTO_UPDATE=0.
/// </summary>
public static class SelfUpdater
{
    private const string Repo = "Innoprovn/vn-revit-mcp";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp");
    private static readonly string StateFile = Path.Combine(Root, "update.json");
    private static readonly HttpClient Http = CreateHttp();
    private static string? _notice;

    /// <summary>Thông báo cập nhật chưa báo cho AI (lấy một lần).</summary>
    public static string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    public static bool Disabled =>
        Environment.GetEnvironmentVariable("VN_REVIT_MCP_AUTO_UPDATE") is "0" or "false" or "off";

    public static void StartInBackground(RevitBridgeClient bridge) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            await CheckAsync(bridge, force: false);
        }
        catch (Exception ex) { Log($"Không tự cập nhật được: {ex.Message}"); }
    });

    /// <summary>Kiểm tra và cập nhật; trả mô tả kết quả (dùng cho lệnh "vn-revit-mcp update").</summary>
    public static async Task<string> CheckAsync(RevitBridgeClient? bridge, bool force)
    {
        if (Disabled && !force) return "Tự cập nhật đang tắt (VN_REVIT_MCP_AUTO_UPDATE=0).";
        // Server và Revit có thể cùng kiểm tra: chỉ một tiến trình cập nhật một lúc.
        Directory.CreateDirectory(Root);
        FileStream gate;
        try { gate = new FileStream(Path.Combine(Root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { return "Đang có tiến trình khác cập nhật."; }
        using var _ = gate;

        var state = LoadState();
        if (!force && state["last_check"]?.GetValue<DateTime>() is { } last && DateTime.UtcNow - last < Interval)
            return "Vừa kiểm tra gần đây.";
        state["last_check"] = DateTime.UtcNow;
        SaveState(state);

        var messages = new List<string>();
        if (await UpdateCoreAsync(state) is { } core) messages.Add(core);
        var extensions = await UpdateExtensionsAsync();
        if (extensions.Count > 0)
        {
            messages.Add("Tiện ích đã cập nhật: " + string.Join(", ", extensions) + ".");
            if (bridge != null)
            {
                try { await bridge.SendAsync(InnoPro.Bridge.BridgeProtocol.MethodReloadCommands, null, TimeSpan.FromMinutes(1), CancellationToken.None); }
                catch (Exception) { /* Revit chưa mở: lần mở sau tự nạp */ }
            }
        }

        if (messages.Count == 0) return "Đang dùng bản mới nhất.";
        var text = string.Join(" ", messages);
        _notice = "vn-revit-mcp vừa tự cập nhật: " + text;
        state["last_update"] = text;
        state["last_update_at"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        SaveState(state);
        Log(text);
        return text;
    }

    private static async Task<string?> UpdateCoreAsync(JsonObject state)
    {
        if (InstalledVersion() is not { } installed) return null; // chưa cài bằng bộ cài (chạy từ thư mục build)

        using var release = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var latest) || latest <= installed) return null;
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() is { } n && n.StartsWith("vn-revit-mcp-") && n.EndsWith("-win-x64.zip"));
        if (asset.ValueKind != JsonValueKind.Object) return null;

        var bytes = await Http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString());
        VerifyDigest(asset, bytes);
        var work = Path.Combine(Root, "cache", "update", tag);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        using (var zip = new ZipArchive(new MemoryStream(bytes))) zip.ExtractToDirectory(work);

        var installer = Directory.EnumerateFiles(work, "Install-VnRevitMcp.ps1", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidDataException("Gói cập nhật thiếu bộ cài.");
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{installer}\" -Source \"{work}\" -NoClients")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Bộ cài báo lỗi: " + output.Trim().Split('\n').LastOrDefault());
        try { Directory.Delete(work, true); } catch (IOException) { }
        state["installed"] = latest.ToString(3);
        return $"bản {installed.ToString(3)} → {latest.ToString(3)} (Revit đang mở tự nạp add-in mới khi rảnh; server mới dùng từ lần mở lại ứng dụng AI).";
    }

    private static async Task<List<string>> UpdateExtensionsAsync()
    {
        var updated = new List<string>();
        var commands = Path.Combine(Root, "Commands");
        if (!Directory.Exists(commands)) return updated;
        var installed = Directory.GetDirectories(commands)
            .Select(d => (Dir: d, Marker: Path.Combine(d, ".marketplace.json")))
            .Where(x => File.Exists(x.Marker))
            .ToList();
        if (installed.Count == 0) return updated;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/contents/community/index.json?ref=main");
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var index = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!index.RootElement.TryGetProperty("extensions", out var list)) return updated;

        foreach (var (dir, marker) in installed)
        {
            using var mine = JsonDocument.Parse(File.ReadAllText(marker));
            var id = mine.RootElement.GetProperty("id").GetString();
            var current = mine.RootElement.GetProperty("version").GetString();
            var entry = list.EnumerateArray().FirstOrDefault(e => e.GetProperty("id").GetString() == id);
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var version = entry.GetProperty("version").GetString()!;
            if (!Version.TryParse(version, out var v) || !Version.TryParse(current, out var c) || v <= c) continue;

            var bytes = await Http.GetByteArrayAsync(entry.GetProperty("url").GetString());
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                Log($"Tiện ích {id}: mã SHA-256 không khớp, bỏ qua.");
                continue;
            }
            var staging = dir + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            using (var zip = new ZipArchive(new MemoryStream(bytes))) zip.ExtractToDirectory(staging);
            var root = Directory.EnumerateFiles(staging, "commandset.json", SearchOption.AllDirectories).Select(Path.GetDirectoryName).First()!;
            File.WriteAllText(Path.Combine(root, ".marketplace.json"), JsonSerializer.Serialize(new { id, version, installed = DateTime.Now.ToString("s") }));
            Directory.Delete(dir, true);
            Directory.Move(root, dir);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            updated.Add($"{id} {current} → {version}");
        }
        return updated;
    }

    /// <summary>Bản đang cài: tên thư mục app\&lt;phiên bản&gt; mà junction current trỏ tới, không có thì phiên bản file add-in.</summary>
    public static Version? InstalledVersion()
    {
        var current = new DirectoryInfo(Path.Combine(Root, "current"));
        if (!current.Exists) return null;
        var name = Path.GetFileName(current.LinkTarget?.TrimEnd('\\') ?? "");
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^v?(\d+\.\d+\.\d+)");
        if (m.Success) return Version.Parse(m.Groups[1].Value);
        var dll = Path.Combine(current.FullName, "addin", "InnoPro.Revit.Addin.dll");
        return File.Exists(dll) && Version.TryParse(FileVersionInfo.GetVersionInfo(dll).FileVersion, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>Tình trạng cập nhật cho doctor.</summary>
    public static (string? LastCheck, string? LastUpdate) Status()
    {
        var state = LoadState();
        var check = state["last_check"]?.GetValue<DateTime>().ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        return (check,state["last_update"] is { } u ? $"{u} ({state["last_update_at"]})" : null);
    }

    /// <summary>GitHub công bố "digest": "sha256:..." cho từng file phát hành; có thì phải khớp.</summary>
    private static void VerifyDigest(JsonElement asset, byte[] bytes)
    {
        if (!asset.TryGetProperty("digest", out var digest) || digest.GetString() is not { } d || !d.StartsWith("sha256:")) return;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, d[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Gói cập nhật tải về không khớp mã SHA-256, đã hủy.");
    }

    private static JsonObject LoadState()
    {
        try { return File.Exists(StateFile) ? JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? [] : []; }
        catch (Exception) { return []; }
    }

    private static void SaveState(JsonObject state)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(StateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("vn-revit-mcp-updater");
        return http;
    }

    private static void Log(string message) => Console.Error.WriteLine("[vn-revit-mcp] " + message);
}
