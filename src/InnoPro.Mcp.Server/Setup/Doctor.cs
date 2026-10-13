using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using InnoPro.Bridge;

namespace InnoPro.Mcp.Server.Setup;

/// <summary>
/// "vn-revit-mcp doctor": kiểm tra toàn bộ cài đặt và kết nối. Mỗi mục có ok + chi tiết + cách sửa (lệnh chạy được),
/// để AI đọc kết quả --json rồi tự xử lý mà người dùng không cần hiểu gì.
/// </summary>
internal static class Doctor
{
    private sealed record Check(string Name, bool Ok, string Detail, string? Fix = null);

    private const string InstallCommand = "irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex";

    public static int Run(SetupPaths paths, bool json)
    {
        var checks = new List<Check>();
        var serverVersion = typeof(Doctor).Assembly.GetName().Version ?? new Version(0, 0);
        var root = Path.Combine(paths.LocalAppData, "InnoPro", "vn-revit-mcp");
        var addinDll = Path.Combine(root, "current", "addin", "InnoPro.Revit.Addin.dll");

        // Revit 2025+ đã cài
        var years = Enumerable.Range(2025, 6)
            .Where(y => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", $"Revit {y}", "Revit.exe")))
            .ToList();
        checks.Add(years.Count > 0
            ? new Check("revit", true, "Revit " + string.Join(", ", years))
            : new Check("revit", false, "Không thấy Revit 2025 trở lên trong Program Files\\Autodesk.", "Cài Autodesk Revit 2025 hoặc mới hơn."));

        // Add-in đã cài và đúng phiên bản
        if (!File.Exists(addinDll))
            checks.Add(new Check("addin", false, "Chưa cài add-in vn-revit-mcp.", InstallCommand));
        else
        {
            var installed = Version.TryParse(FileVersionInfo.GetVersionInfo(addinDll).FileVersion, out var v) ? v : new Version(0, 0);
            checks.Add(new Check("addin", true, $"Add-in {installed.ToString(3)} tại {Path.GetDirectoryName(addinDll)}"));
        }

        // File .addin cho từng năm Revit trỏ đúng DLL
        foreach (var year in years)
        {
            var manifest = Path.Combine(paths.RoamingAppData, "Autodesk", "Revit", "Addins", year.ToString(), "InnoPro.VnRevitMcp.addin");
            if (!File.Exists(manifest))
            {
                checks.Add(new Check($"addin_manifest_{year}", false, $"Revit {year} chưa có file khai báo add-in.", InstallCommand));
                continue;
            }
            var text = File.ReadAllText(manifest);
            var start = text.IndexOf("<Assembly>", StringComparison.OrdinalIgnoreCase);
            var end = text.IndexOf("</Assembly>", StringComparison.OrdinalIgnoreCase);
            var assembly = start >= 0 && end > start ? text[(start + 10)..end].Trim() : "";
            checks.Add(File.Exists(assembly)
                ? new Check($"addin_manifest_{year}", true, $"Revit {year}: {assembly}")
                : new Check($"addin_manifest_{year}", false, $"Revit {year}: file khai báo trỏ tới DLL không tồn tại ({assembly}).", InstallCommand));
        }

        // Bản cài lọt vào vùng ảo hoá của một ứng dụng MSIX (Revit không thấy): tìm thật trong Packages\*\LocalCache.
        var packages = Path.Combine(paths.LocalAppData, "Packages");
        var trapped = !Directory.Exists(packages) ? [] : Directory.EnumerateDirectories(packages)
            .SelectMany(p => new[]
            {
                Path.Combine(p, "LocalCache", "Local", "InnoPro", "vn-revit-mcp"),
                Path.Combine(p, "LocalCache", "Roaming", "Autodesk", "Revit", "Addins"),
            })
            .Where(d => Directory.Exists(d) && (d.EndsWith("vn-revit-mcp") || Directory.EnumerateFiles(d, "InnoPro.VnRevitMcp.addin", SearchOption.AllDirectories).Any()))
            .ToList();
        checks.Add(trapped.Count == 0
            ? new Check("msix", true, "Không có bản cài nào bị kẹt trong vùng ảo hoá của ứng dụng MSIX.")
            : new Check("msix", false, "Có bản cài bị kẹt trong vùng ảo hoá của ứng dụng (Revit không thấy): " + string.Join("; ", trapped),
                "Chạy lại lệnh cài trong PowerShell mở từ menu Start (không chạy bên trong ứng dụng AI): " + InstallCommand));

        // Revit đang mở và cầu nối trả lời
        var pids = RevitBridgeClient.FindRevitProcesses();
        if (pids.Count == 0)
        {
            var revitRunning = Process.GetProcessesByName("Revit").Length > 0;
            checks.Add(revitRunning
                ? new Check("bridge", false, "Revit đang mở nhưng add-in chưa bật cầu nối.", "NGƯỜI DÙNG: đóng rồi mở lại Revit (add-in mới cài chỉ nạp khi Revit khởi động); kiểm tra nút MCP trên tab InnoPro đang bật.")
                : new Check("bridge", true, "Revit chưa mở; mở Revit để AI điều khiển được."));
        }
        foreach (var pid in pids)
        {
            try
            {
                using var doc = JsonDocument.Parse(Ping(pid));
                var r = doc.RootElement;
                var active = r.TryGetProperty("active_document", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                var errors = r.TryGetProperty("load_errors", out var le) && le.ValueKind == JsonValueKind.Array ? le.GetArrayLength() : 0;
                if (Version.TryParse(r.GetProperty("addin_version").GetString(), out var running) && File.Exists(addinDll)
                    && Version.TryParse(FileVersionInfo.GetVersionInfo(addinDll).FileVersion, out var installedNow) && running < installedNow)
                    checks.Add(new Check($"addin_outdated_{pid}", false,
                        $"Revit PID {pid} đang chạy add-in {running.ToString(3)}, bản đã cài là {installedNow.ToString(3)}.",
                        "NGƯỜI DÙNG: đóng rồi mở lại Revit để dùng bản mới (AI không tự làm)."));
                checks.Add(new Check($"bridge_{pid}", errors == 0,
                    $"{r.GetProperty("revit_version").GetString()}, add-in {r.GetProperty("addin_version").GetString()}, " +
                    $"{r.GetProperty("command_count").GetInt32()} lệnh, tài liệu: {active ?? "chưa mở"}" + (errors > 0 ? $", {errors} lỗi nạp lệnh" : ""),
                    errors > 0 ? "Xem lỗi nạp lệnh: gọi tool revit_status." : active == null ? "Mở một dự án trong Revit." : null));
            }
            catch (Exception ex)
            {
                checks.Add(new Check($"bridge_{pid}", false, $"Revit PID {pid} không trả lời: {ex.Message}",
                    "Revit có thể đang bận hoặc mở hộp thoại; đóng hộp thoại rồi thử lại."));
            }
        }

        // AI client đã kết nối
        var clients = McpClient.All.Select(c => c.Inspect(paths)).Where(c => c.Detected).ToList();
        foreach (var c in clients)
            checks.Add(c.Configured
                ? new Check($"client_{c.Id}", true, $"{c.Name}: đã kết nối")
                : new Check($"client_{c.Id}", false, $"{c.Name}: có trên máy nhưng chưa kết nối.",
                    $"\"{Path.Combine(root, "current", "server", "vn-revit-mcp.exe")}\" setup --clients {c.Id}"));
        if (clients.Count == 0)
            checks.Add(new Check("clients", true, "Chưa thấy AI client nào trên máy (Claude, Cursor, VS Code...)."));

        // Tự cập nhật
        var (lastCheck, lastUpdate) = SelfUpdater.Status();
        checks.Add(new Check("auto_update", true, SelfUpdater.Disabled
            ? "Tự cập nhật đang tắt (VN_REVIT_MCP_AUTO_UPDATE=0); cập nhật tay: vn-revit-mcp.exe update"
            : $"Tự cập nhật: bật, kiểm tra lần cuối {lastCheck ?? "chưa"}" + (lastUpdate != null ? $"; lần cập nhật gần nhất: {lastUpdate}" : "")));

        var ok = checks.All(c => c.Ok);
        if (json)
        {
            var output = new JsonObject
            {
                ["ok"] = ok,
                ["server_version"] = serverVersion.ToString(3),
                ["checks"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name, ["ok"] = c.Ok, ["detail"] = c.Detail, ["fix"] = c.Fix,
                }).ToArray()),
            };
            Console.WriteLine(output.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        else
        {
            foreach (var c in checks)
            {
                Console.WriteLine($"{(c.Ok ? "[OK] " : "[!!] ")}{c.Detail}");
                if (c.Fix != null) Console.WriteLine($"      Cách sửa: {c.Fix}");
            }
            Console.WriteLine(ok ? "Mọi thứ sẵn sàng." : "Có mục cần sửa (xem 'Cách sửa').");
        }
        return ok ? 0 : 3;
    }

    private static string Ping(int pid)
    {
        using var pipe = new NamedPipeClientStream(".", BridgeProtocol.PipeName(pid), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        pipe.Connect(3000);
        using var writer = new StreamWriter(pipe, BridgeProtocol.Encoding, 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, BridgeProtocol.Encoding, false, 4096, leaveOpen: true);
        writer.WriteLine("{\"id\":\"doctor\",\"method\":\"ping\",\"params\":null}");
        var line = reader.ReadLine() ?? throw new IOException("không có phản hồi");
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.GetProperty("ok").GetBoolean()) throw new IOException(doc.RootElement.GetProperty("error").GetString());
        return doc.RootElement.GetProperty("result").GetRawText();
    }
}
