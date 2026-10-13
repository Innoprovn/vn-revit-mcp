using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace InnoPro.Revit.Chat;

/// <summary>
/// Chat bằng Claude Code đã đăng nhập trên máy (gói Claude của người dùng, không cần API key).
/// Mỗi câu hỏi chạy "claude -p" ở chế độ không giao diện: chỉ nạp vn-revit-mcp (ghim đúng phiên Revit đang mở),
/// chỉ cho phép các lệnh Revit, đọc luồng stream-json để hiện câu trả lời và các bước. Hội thoại nối tiếp bằng --resume.
/// </summary>
internal sealed class ClaudeCodeRunner
{
    private const string SystemNote =
        "Bạn đang trả lời trong khung chat bên trong Autodesk Revit. Làm việc với mô hình qua các tool vn-revit-mcp. " +
        "Trả lời bằng tiếng Việt, ngắn gọn, văn bản thuần (không markdown). Trước khi sửa mô hình số lượng lớn, nói rõ việc sẽ làm.";

    private string? _session;

    public static readonly IReadOnlyList<string> Models = ["sonnet", "opus", "haiku"];

    public void Reset() => _session = null;

    /// <summary>Đường dẫn claude.exe (bộ cài native hoặc npm); null nếu chưa cài.</summary>
    public static string? FindClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
            candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ServerExe() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "current", "server", "vn-revit-mcp.exe");

    public async Task RunAsync(string userText, string model, Action<string, string> onEvent, CancellationToken token)
    {
        var claude = FindClaude() ?? throw new ChatException("Chưa có Claude Code trên máy. Cài tại claude.com/claude-code, đăng nhập một lần rồi thử lại.");
        var server = ServerExe();
        if (!File.Exists(server)) throw new ChatException("Không thấy vn-revit-mcp.exe trong bản cài. Chạy lại bộ cài.");

        // Thư mục làm việc riêng + cấu hình MCP chỉ có vn-revit-mcp, ghim phiên Revit này.
        var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "claude-chat");
        Directory.CreateDirectory(work);
        var config = Path.Combine(work, "mcp.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["vn-revit-mcp"] = new { command = server, env = new Dictionary<string, string> { ["VN_REVIT_MCP_PID"] = Environment.ProcessId.ToString() } },
            },
        }));

        var start = new ProcessStartInfo(claude)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", userText, "--output-format", "stream-json", "--verbose", "--model", model,
                     "--strict-mcp-config", "--mcp-config", config, "--allowedTools", "mcp__vn-revit-mcp__*", "--append-system-prompt", SystemNote })
            start.ArgumentList.Add(a);
        if (_session != null) { start.ArgumentList.Add("--resume"); start.ArgumentList.Add(_session); }

        using var process = Process.Start(start) ?? throw new ChatException("Không chạy được Claude Code.");
        process.StandardInput.Close();
        using var kill = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        string? error = null;
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            JsonElement e;
            try { e = JsonDocument.Parse(line).RootElement; } catch (JsonException) { continue; }
            switch (e.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "system" when e.TryGetProperty("session_id", out var s):
                    _session = s.GetString();
                    break;
                case "assistant":
                    foreach (var c in e.GetProperty("message").GetProperty("content").EnumerateArray())
                    {
                        var kind = c.GetProperty("type").GetString();
                        if (kind == "text" && c.GetProperty("text").GetString() is { Length: > 0 } text) onEvent("assistant", text.Trim());
                        else if (kind == "tool_use" && c.GetProperty("name").GetString() is { } name && name.StartsWith("mcp__vn-revit-mcp__"))
                        {
                            var input = c.TryGetProperty("input", out var i) ? i.GetRawText() : "";
                            onEvent("tool", $"{name.Replace("mcp__vn-revit-mcp__", "")} {(input.Length > 160 ? input[..160] + "…" : input)}");
                        }
                    }
                    break;
                case "user":
                    foreach (var c in e.GetProperty("message").GetProperty("content").EnumerateArray())
                        if (c.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True)
                            onEvent("tool_error", ShortText(c.TryGetProperty("content", out var cc) ? cc : default));
                    break;
                case "result":
                    if (e.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                        error = e.TryGetProperty("result", out var r) ? r.GetString() : "Claude Code báo lỗi.";
                    break;
            }
        }
        await process.WaitForExitAsync(token);
        if (error != null) throw new ChatException(error);
        if (process.ExitCode != 0)
        {
            var detail = (await stderr).Trim();
            throw new ChatException(detail.Contains("login", StringComparison.OrdinalIgnoreCase) || detail.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? "Claude Code chưa đăng nhập: mở terminal, gõ claude rồi đăng nhập một lần."
                : $"Claude Code dừng với mã {process.ExitCode}: {detail[..Math.Min(300, detail.Length)]}");
        }
    }

    private static string ShortText(JsonElement content)
    {
        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => string.Join(" ", content.EnumerateArray().Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : "")),
            _ => "",
        };
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
