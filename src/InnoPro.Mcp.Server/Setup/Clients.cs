using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InnoPro.Mcp.Server.Setup;

/// <summary>Các thư mục gốc của người dùng. <c>--home</c> thay bằng thư mục giả để thử nghiệm.</summary>
internal sealed record SetupPaths(string UserProfile, string RoamingAppData, string LocalAppData, bool IsOverridden)
{
    public static SetupPaths FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        false);

    public static SetupPaths FromHome(string home)
    {
        var root = Path.GetFullPath(home);
        return new(Path.Combine(root, "User"), Path.Combine(root, "AppData", "Roaming"), Path.Combine(root, "AppData", "Local"), true);
    }
}

/// <summary>Kết quả xử lý một client.</summary>
internal sealed class ClientResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Detected { get; set; }
    public bool Configured { get; set; }
    /// <summary>configured | updated | unchanged | removed | not_found | skipped | would_configure | would_remove | error</summary>
    public string Action { get; set; } = "skipped";
    public List<string> Paths { get; } = [];
    public List<string> Backups { get; } = [];
    public string? Restart { get; set; }
    public string? Message { get; set; }

    public bool Changed => Action is "configured" or "updated" or "removed";
}

internal sealed class SetupContext
{
    public required SetupPaths Paths { get; init; }
    public required string Command { get; init; }
    public bool DryRun { get; init; }
    public string Stamp { get; } = DateTime.Now.ToString("yyyyMMddHHmmss");
}

/// <summary>Một AI client hỗ trợ MCP.</summary>
internal abstract class McpClient(string id, string name, string restart)
{
    public const string ServerName = "vn-revit-mcp";

    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>Việc người dùng cần làm để client nhận cấu hình mới.</summary>
    public string Restart { get; } = restart;

    public abstract ClientResult Inspect(SetupPaths paths);
    public abstract ClientResult Apply(SetupContext context);
    public abstract ClientResult Remove(SetupContext context);

    protected ClientResult NewResult() => new() { Id = Id, Name = Name };

    public static IReadOnlyList<McpClient> All { get; } =
    [
        new JsonMcpClient("claude-desktop", "Claude Desktop", "Thoát hẳn Claude Desktop (cả biểu tượng ở khay hệ thống) rồi mở lại.",
            p => ClaudeDesktopFolders(p).Select(d => (d, Path.Combine(d, "claude_desktop_config.json"))), "mcpServers", false),
        new ClaudeCodeClient(),
        new JsonMcpClient("cursor", "Cursor", "Khởi động lại Cursor.",
            p => One(Path.Combine(p.UserProfile, ".cursor"), "mcp.json"), "mcpServers", false),
        new JsonMcpClient("vscode", "VS Code (GitHub Copilot)", "VS Code tự nhận; nếu chưa thấy thì chạy lệnh \"MCP: List Servers\".",
            p => One(Path.Combine(p.RoamingAppData, "Code", "User"), "mcp.json"), "servers", true),
        new JsonMcpClient("vscode-insiders", "VS Code Insiders (GitHub Copilot)", "VS Code tự nhận; nếu chưa thấy thì chạy lệnh \"MCP: List Servers\".",
            p => One(Path.Combine(p.RoamingAppData, "Code - Insiders", "User"), "mcp.json"), "servers", true),
        new JsonMcpClient("windsurf", "Windsurf", "Bấm Refresh trong bảng MCP của Windsurf hoặc khởi động lại.",
            p => One(Path.Combine(p.UserProfile, ".codeium", "windsurf"), "mcp_config.json"), "mcpServers", false),
        new JsonMcpClient("gemini", "Gemini CLI", "Mở phiên Gemini CLI mới.",
            p => One(Path.Combine(p.UserProfile, ".gemini"), "settings.json"), "mcpServers", false),
        new JsonMcpClient("lmstudio", "LM Studio", "Khởi động lại LM Studio.",
            p => One(Path.Combine(p.UserProfile, ".lmstudio"), "mcp.json"), "mcpServers", false),
        new CodexClient(),
        new JsonMcpClient("cline", "Cline", "Khởi động lại VS Code hoặc mở lại bảng MCP Servers của Cline.",
            p => ClineFolders(p), "mcpServers", false),
    ];

    private static IEnumerable<(string Folder, string File)> One(string folder, string file) => [(folder, Path.Combine(folder, file))];

    /// <summary>Bản cài thường dùng %APPDATA%\Claude; bản Microsoft Store (MSIX) bị chuyển hướng vào Packages\Claude_*.</summary>
    private static IEnumerable<string> ClaudeDesktopFolders(SetupPaths p)
    {
        yield return Path.Combine(p.RoamingAppData, "Claude");
        var packages = Path.Combine(p.LocalAppData, "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
            yield return Path.Combine(package, "LocalCache", "Roaming", "Claude");
    }

    /// <summary>Cline là extension, có thể nằm trong VS Code, VS Code Insiders hoặc Cursor.</summary>
    private static IEnumerable<(string, string)> ClineFolders(SetupPaths p)
    {
        foreach (var editor in new[] { "Code", "Code - Insiders", "Cursor" })
        {
            var extension = Path.Combine(p.RoamingAppData, editor, "User", "globalStorage", "saoudrizwan.claude-dev");
            yield return (extension, Path.Combine(extension, "settings", "cline_mcp_settings.json"));
        }
    }

    protected static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Ghi file qua file tạm rồi thay thế, tránh để file cấu hình hỏng nửa chừng.</summary>
    protected static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-vnrevitmcp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Sao lưu file gốc một lần mỗi lần chạy.</summary>
    protected static string? Backup(string path, SetupContext context)
    {
        if (!File.Exists(path)) return null;
        var backup = $"{path}.bak-vnrevitmcp-{context.Stamp}";
        if (!File.Exists(backup)) File.Copy(path, backup);
        return backup;
    }
}

/// <summary>Client lưu cấu hình MCP trong file JSON dạng { "&lt;rootKey&gt;": { "vn-revit-mcp": {...} } }.</summary>
internal sealed class JsonMcpClient(
    string id, string name, string restart,
    Func<SetupPaths, IEnumerable<(string Folder, string File)>> targets,
    string rootKey, bool vsCodeStyle) : McpClient(id, name, restart)
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Chỉ các đích có thư mục cấu hình (tức là client đã cài).</summary>
    private List<(string Folder, string File)> Detected(SetupPaths paths) => targets(paths).Where(t => Directory.Exists(t.Folder)).ToList();

    public override ClientResult Inspect(SetupPaths paths)
    {
        var result = NewResult();
        var all = targets(paths).ToList();
        var detected = Detected(paths);
        result.Detected = detected.Count > 0;
        result.Paths.AddRange((detected.Count > 0 ? detected : all.Take(1)).Select(t => t.File));
        foreach (var (_, file) in detected)
        {
            try
            {
                if (Load(file)?[rootKey]?[ServerName] is JsonObject) result.Configured = true;
            }
            catch (Exception ex) { result.Message = $"Không đọc được {file}: {ex.Message}"; }
        }
        return result;
    }

    public override ClientResult Apply(SetupContext context)
    {
        var result = NewResult();
        var detected = Detected(context.Paths);
        result.Detected = detected.Count > 0;
        if (!result.Detected)
        {
            result.Action = "not_found";
            result.Paths.Add(targets(context.Paths).First().File);
            return result;
        }

        var anyChange = false;
        foreach (var (_, file) in detected)
        {
            result.Paths.Add(file);
            try
            {
                var root = Load(file) ?? new JsonObject();
                if (root[rootKey] is not JsonObject servers)
                {
                    if (root[rootKey] != null) throw new InvalidDataException($"Mục \"{rootKey}\" không phải object JSON.");
                    servers = new JsonObject();
                    root[rootKey] = servers;
                }

                var existed = servers[ServerName] is JsonObject;
                if (existed && Matches((JsonObject)servers[ServerName]!, context.Command)) continue;

                anyChange = true;
                if (servers[ServerName] is not JsonObject entry)
                {
                    entry = new JsonObject();
                    servers[ServerName] = entry;
                }
                // Giữ các khóa người dùng tự thêm (env...), chỉ cập nhật phần của mình.
                if (vsCodeStyle) entry["type"] = "stdio";
                entry["command"] = context.Command;
                entry["args"] = new JsonArray();
                if (result.Action != "configured") result.Action = existed ? "updated" : "configured";

                if (context.DryRun) continue;
                var backup = Backup(file, context);
                if (backup != null) result.Backups.Add(backup);
                WriteAtomic(file, root.ToJsonString(WriteOptions) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                result.Action = "error";
                result.Message = $"{file}: {ex.Message}";
                return result;
            }
        }

        result.Configured = true;
        if (!anyChange) result.Action = "unchanged";
        else if (context.DryRun) result.Action = "would_configure";
        if (result.Changed) result.Restart = Restart;
        return result;
    }

    public override ClientResult Remove(SetupContext context)
    {
        var result = NewResult();
        var detected = Detected(context.Paths);
        result.Detected = detected.Count > 0;
        result.Action = "not_found";
        foreach (var (_, file) in detected)
        {
            result.Paths.Add(file);
            try
            {
                var root = Load(file);
                if (root?[rootKey] is not JsonObject servers || servers[ServerName] == null) continue;
                servers.Remove(ServerName);
                result.Action = context.DryRun ? "would_remove" : "removed";
                if (context.DryRun) continue;
                var backup = Backup(file, context);
                if (backup != null) result.Backups.Add(backup);
                WriteAtomic(file, root.ToJsonString(WriteOptions) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                result.Action = "error";
                result.Message = $"{file}: {ex.Message}";
                return result;
            }
        }
        if (result.Changed) result.Restart = Restart;
        return result;
    }

    private bool Matches(JsonObject entry, string command) =>
        SamePath(entry["command"]?.GetValueKind() == JsonValueKind.String ? entry["command"]!.GetValue<string>() : null, command)
        && entry["args"] is JsonArray { Count: 0 }
        && (!vsCodeStyle || entry["type"]?.ToString() == "stdio");

    /// <summary>Đọc file JSON (cho phép chú thích, dấu phẩy thừa). File chưa có hoặc rỗng trả về null.</summary>
    private static JsonObject? Load(string file)
    {
        if (!File.Exists(file)) return null;
        var text = File.ReadAllText(file);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
            ?? throw new InvalidDataException("File không phải object JSON.");
    }
}

/// <summary>Codex CLI: bảng [mcp_servers.vn-revit-mcp] trong ~/.codex/config.toml. Sửa dạng văn bản để giữ nguyên phần còn lại.</summary>
internal sealed class CodexClient() : McpClient("codex", "Codex CLI", "Mở phiên Codex mới.")
{
    private static readonly Regex OurHeader = new(@"^\s*\[\s*mcp_servers\s*\.\s*(vn-revit-mcp|""vn-revit-mcp"")\s*\]\s*(#.*)?$");
    private static readonly Regex OurSubHeader = new(@"^\s*\[\s*mcp_servers\s*\.\s*(vn-revit-mcp|""vn-revit-mcp"")\s*\.");
    private static readonly Regex AnyHeader = new(@"^\s*\[");
    private static readonly Regex CommandLine = new(@"^\s*command\s*=");
    private static readonly Regex ArgsLine = new(@"^\s*args\s*=");

    private static (string Folder, string File) Target(SetupPaths p)
    {
        var folder = Path.Combine(p.UserProfile, ".codex");
        return (folder, Path.Combine(folder, "config.toml"));
    }

    public override ClientResult Inspect(SetupPaths paths)
    {
        var result = NewResult();
        var (folder, file) = Target(paths);
        result.Paths.Add(file);
        result.Detected = Directory.Exists(folder);
        if (result.Detected && File.Exists(file))
            result.Configured = File.ReadAllLines(file).Any(l => OurHeader.IsMatch(l));
        return result;
    }

    public override ClientResult Apply(SetupContext context)
    {
        var result = NewResult();
        var (folder, file) = Target(context.Paths);
        result.Paths.Add(file);
        result.Detected = Directory.Exists(folder);
        if (!result.Detected) { result.Action = "not_found"; return result; }

        try
        {
            var original = File.Exists(file) ? File.ReadAllText(file) : "";
            var newline = original.Contains("\r\n") || original.Length == 0 ? "\r\n" : "\n";
            var lines = SplitLines(original);
            var commandValue = $"command = {TomlString(context.Command)}";
            const string argsValue = "args = []";

            var start = lines.FindIndex(l => OurHeader.IsMatch(l));
            if (start < 0)
            {
                while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                lines.Add($"[mcp_servers.{ServerName}]");
                lines.Add(commandValue);
                lines.Add(argsValue);
                result.Action = "configured";
            }
            else
            {
                var end = start + 1;
                while (end < lines.Count && !AnyHeader.IsMatch(lines[end])) end++;
                var updated = false;
                var cmdIndex = lines.FindIndex(start + 1, end - start - 1, l => CommandLine.IsMatch(l));
                if (cmdIndex < 0) { lines.Insert(start + 1, commandValue); end++; updated = true; }
                else if (lines[cmdIndex].Trim() != commandValue) { lines[cmdIndex] = commandValue; updated = true; }
                cmdIndex = lines.FindIndex(start + 1, end - start - 1, l => CommandLine.IsMatch(l));
                var argsIndex = lines.FindIndex(start + 1, end - start - 1, l => ArgsLine.IsMatch(l));
                if (argsIndex < 0) { lines.Insert(cmdIndex + 1, argsValue); updated = true; }
                else if (lines[argsIndex].Trim() != argsValue)
                {
                    // args có thể viết nhiều dòng: xóa tới dấu ] đóng.
                    var last = argsIndex;
                    while (!lines[last].Contains(']') && last + 1 < lines.Count) last++;
                    lines.RemoveRange(argsIndex, last - argsIndex + 1);
                    lines.Insert(argsIndex, argsValue);
                    updated = true;
                }
                result.Action = updated ? "updated" : "unchanged";
            }

            result.Configured = true;
            if (result.Action == "unchanged") return result;
            if (context.DryRun) { result.Action = "would_configure"; return result; }
            var backup = Backup(file, context);
            if (backup != null) result.Backups.Add(backup);
            WriteAtomic(file, string.Join(newline, lines) + newline);
            result.Restart = Restart;
        }
        catch (Exception ex)
        {
            result.Action = "error";
            result.Message = $"{file}: {ex.Message}";
        }
        return result;
    }

    public override ClientResult Remove(SetupContext context)
    {
        var result = NewResult();
        var (folder, file) = Target(context.Paths);
        result.Paths.Add(file);
        result.Detected = Directory.Exists(folder);
        result.Action = "not_found";
        if (!File.Exists(file)) return result;
        try
        {
            var original = File.ReadAllText(file);
            var newline = original.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitLines(original);
            var removed = false;
            for (var i = 0; i < lines.Count;)
            {
                if (!OurHeader.IsMatch(lines[i]) && !OurSubHeader.IsMatch(lines[i])) { i++; continue; }
                var end = i + 1;
                while (end < lines.Count && !AnyHeader.IsMatch(lines[end])) end++;
                lines.RemoveRange(i, end - i);
                removed = true;
            }
            if (!removed) return result;
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            result.Action = context.DryRun ? "would_remove" : "removed";
            if (context.DryRun) return result;
            var backup = Backup(file, context);
            if (backup != null) result.Backups.Add(backup);
            WriteAtomic(file, lines.Count == 0 ? "" : string.Join(newline, lines) + newline);
            result.Restart = Restart;
        }
        catch (Exception ex)
        {
            result.Action = "error";
            result.Message = $"{file}: {ex.Message}";
        }
        return result;
    }

    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Chuỗi literal TOML ('...') giữ nguyên dấu \ trong đường dẫn Windows.</summary>
    private static string TomlString(string value) =>
        value.Contains('\'')
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : "'" + value + "'";
}

/// <summary>Claude Code: chỉ dùng CLI chính thức (claude mcp add/remove/get), không đọc file cấu hình của nó.</summary>
internal sealed class ClaudeCodeClient() : McpClient("claude-code", "Claude Code", "Mở phiên Claude Code mới (hoặc gõ /mcp).")
{
    private static string? FindCli()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var dir in dirs)
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude.bat" })
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static (int ExitCode, string Output) RunCli(string cli, params string[] args)
    {
        var info = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Không chạy được claude.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(true); } catch { }
            return (-1, "Hết thời gian chờ claude CLI.");
        }
        return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    private ClientResult Start(SetupPaths paths, out string? cli)
    {
        var result = NewResult();
        cli = null;
        if (paths.IsOverridden)
        {
            result.Message = "Bỏ qua khi dùng --home.";
            return result;
        }
        cli = FindCli();
        result.Detected = cli != null;
        if (cli != null) result.Paths.Add(cli);
        return result;
    }

    public override ClientResult Inspect(SetupPaths paths)
    {
        var result = Start(paths, out var cli);
        if (cli == null) return result;
        try { result.Configured = RunCli(cli, "mcp", "get", ServerName).ExitCode == 0; }
        catch (Exception ex) { result.Message = ex.Message; }
        return result;
    }

    public override ClientResult Apply(SetupContext context)
    {
        var result = Start(context.Paths, out var cli);
        if (cli == null) { result.Action = context.Paths.IsOverridden ? "skipped" : "not_found"; return result; }
        if (context.DryRun) { result.Action = "would_configure"; return result; }
        try
        {
            // Xóa bản cũ (nếu có) rồi thêm lại để cập nhật đường dẫn.
            RunCli(cli, "mcp", "remove", ServerName, "--scope", "user");
            var (code, output) = RunCli(cli, "mcp", "add", ServerName, "--scope", "user", "--", context.Command);
            if (code != 0) throw new InvalidOperationException(output);
            result.Configured = true;
            result.Action = "configured";
            result.Restart = Restart;
        }
        catch (Exception ex)
        {
            result.Action = "error";
            result.Message = ex.Message;
        }
        return result;
    }

    public override ClientResult Remove(SetupContext context)
    {
        var result = Start(context.Paths, out var cli);
        if (cli == null) { result.Action = context.Paths.IsOverridden ? "skipped" : "not_found"; return result; }
        if (context.DryRun) { result.Action = "would_remove"; return result; }
        try
        {
            var (code, _) = RunCli(cli, "mcp", "remove", ServerName, "--scope", "user");
            result.Action = code == 0 ? "removed" : "not_found";
            if (code == 0) result.Restart = Restart;
        }
        catch (Exception ex)
        {
            result.Action = "error";
            result.Message = ex.Message;
        }
        return result;
    }
}
