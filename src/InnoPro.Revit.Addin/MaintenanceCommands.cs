using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace InnoPro.Revit.Addin;

/// <summary>Xem AI nào trên máy đã kết nối với Revit, và kết nối lại bằng một nút (không cần mở PowerShell).</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ConnectAiCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var exe = Path.GetFullPath(Path.Combine(Paths.AddinDir, "..", "server", "vn-revit-mcp.exe"));
        if (!File.Exists(exe))
        {
            TaskDialog.Show("InnoPro: Kết nối AI", $"Không thấy MCP server tại:\n{exe}\n\nHãy cài lại vn-revit-mcp.");
            return Result.Cancelled;
        }

        var clients = Run(exe, "clients --json");
        var dialog = new TaskDialog("InnoPro: Kết nối AI")
        {
            MainInstruction = "AI trên máy này",
            MainContent = Describe(clients) +
                          "\nAI khác (hỗ trợ MCP): khai báo server kiểu stdio với lệnh:\n" + exe,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Kết nối tất cả AI đã cài",
            "Tự thêm vn-revit-mcp vào cấu hình của từng AI (có sao lưu file cấu hình cũ).");
        if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Succeeded;

        var result = Run(exe, "setup --json");
        TaskDialog.Show("InnoPro: Kết nối AI", Describe(result) + "\nKhởi động lại các AI vừa kết nối để chúng nhận Revit.");
        return Result.Succeeded;
    }

    private static string Describe(JsonElement? report)
    {
        if (report is not { } json || !json.TryGetProperty("clients", out var clients))
            return "Không đọc được danh sách AI.\n";

        var text = new StringBuilder();
        foreach (var c in clients.EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var detected = c.GetProperty("detected").GetBoolean();
            var configured = c.GetProperty("configured").GetBoolean();
            text.AppendLine(!detected ? $"○ {name}: chưa cài"
                : configured ? $"✓ {name}: đã kết nối"
                : $"✗ {name}: chưa kết nối");
        }
        return text.ToString();
    }

    private static JsonElement? Run(string exe, string arguments)
    {
        var start = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(120_000);
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        catch (Exception ex)
        {
            Log.Error($"Chạy {exe} {arguments} lỗi", ex);
            return null;
        }
    }
}

/// <summary>
/// Kiểm tra bản mới trên GitHub khi người dùng bấm nút (không tự gọi mạng ngầm) và cài bằng bộ cài một dòng.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class UpdateCommand : IExternalCommand
{
    private const string Repo = "Innoprovn/vn-revit-mcp";
    private const string Installer = "irm https://raw.githubusercontent.com/" + Repo + "/main/install.ps1 | iex";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        string? latestTag;
        string notes;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("vn-revit-mcp/" + App.Version);
            var json = Task.Run(() => http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest")).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            latestTag = doc.RootElement.GetProperty("tag_name").GetString();
            notes = doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            TaskDialog.Show("InnoPro: Cập nhật", $"Không kiểm tra được bản mới (cần Internet): {ex.Message}");
            return Result.Cancelled;
        }

        var current = ParseVersion(App.Version);
        var latest = ParseVersion(latestTag);
        if (latest == null || current != null && latest <= current)
        {
            TaskDialog.Show("InnoPro: Cập nhật", $"Đang dùng bản mới nhất ({App.Version}).");
            return Result.Succeeded;
        }

        var dialog = new TaskDialog("InnoPro: Cập nhật")
        {
            MainInstruction = $"Có bản mới {latestTag} (đang dùng {App.Version})",
            MainContent = notes.Length > 1200 ? notes[..1200] + "…" : notes,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Cài bản mới",
            "Mở cửa sổ cài đặt. Bản mới có hiệu lực từ lần mở Revit sau.");
        if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Succeeded;

        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -NoExit -Command \"{Installer}\"")
        {
            UseShellExecute = true,
        });
        return Result.Succeeded;
    }

    private static Version? ParseVersion(string? text)
    {
        var clean = (text ?? "").TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(clean, out var v) ? v : null;
    }
}
