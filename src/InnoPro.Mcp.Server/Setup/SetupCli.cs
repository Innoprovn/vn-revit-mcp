using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InnoPro.Mcp.Server.Setup;

/// <summary>
/// Lệnh phụ của vn-revit-mcp.exe để tự khai báo MCP server với các AI client trên máy:
/// <c>setup</c>, <c>unsetup</c>, <c>clients</c>. Không có lệnh phụ thì exe chạy như MCP server bình thường.
/// </summary>
public static class SetupCli
{
    private static readonly string[] Commands = ["setup", "unsetup", "clients"];

    public static bool IsSetupCommand(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        var previousEncoding = Console.OutputEncoding;
        var changeEncoding = !Console.IsOutputRedirected;
        try
        {
            // Console Windows mặc định dùng code page OEM, không hiện được tiếng Việt có dấu.
            // Khi bị chuyển hướng (script đọc kết quả) thì luôn xuất UTF-8, không đụng tới code page của console.
            if (changeEncoding) Console.OutputEncoding = new UTF8Encoding(false);
            else Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            return RunCore(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Lỗi: " + ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }
        finally
        {
            if (changeEncoding)
            {
                try { Console.OutputEncoding = previousEncoding; } catch (IOException) { }
            }
        }
    }

    private const string Usage = """
        Cách dùng:
          vn-revit-mcp.exe setup   [--clients a,b|all] [--dry-run] [--home <thư mục>] [--command <exe>] [--json]
          vn-revit-mcp.exe unsetup [--clients a,b|all] [--dry-run] [--home <thư mục>] [--json]
          vn-revit-mcp.exe clients [--home <thư mục>] [--json]
        Mã client: claude-desktop, claude-code, cursor, vscode, vscode-insiders, windsurf, gemini, lmstudio, codex, cline
        """;

    private sealed class Options
    {
        public string Command = "";
        public HashSet<string>? Clients;
        public bool DryRun;
        public bool Json;
        public bool Help;
        public string? Home;
        public string? ServerCommand;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options { Command = args[0].ToLowerInvariant() };
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Thiếu giá trị sau {args[i]}.");
            switch (args[i].ToLowerInvariant())
            {
                case "--clients":
                    var list = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (list.Any(c => c.Equals("all", StringComparison.OrdinalIgnoreCase))) { options.Clients = null; break; }
                    var unknown = list.Where(c => McpClient.All.All(k => !k.Id.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (unknown.Count > 0) throw new ArgumentException($"Không biết client: {string.Join(", ", unknown)}.");
                    options.Clients = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                    break;
                case "--dry-run": options.DryRun = true; break;
                case "--json": options.Json = true; break;
                case "--home": options.Home = Next(); break;
                case "--command": options.ServerCommand = Path.GetFullPath(Next()); break;
                case "-h" or "--help" or "/?": options.Help = true; break;
                default: throw new ArgumentException($"Không hiểu tham số '{args[i]}'.");
            }
        }
        return options;
    }

    private static int RunCore(string[] args)
    {
        var options = Parse(args);
        if (options.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var paths = options.Home != null ? SetupPaths.FromHome(options.Home) : SetupPaths.FromEnvironment();
        var clients = McpClient.All.Where(c => options.Clients == null || options.Clients.Contains(c.Id)).ToList();

        if (options.Command == "clients")
        {
            var found = clients.Select(c => c.Inspect(paths)).ToList();
            if (options.Json) WriteJson(found, null, false);
            else PrintClients(found);
            return 0;
        }

        var (command, warning) = ResolveServerCommand(paths, options.ServerCommand);
        var context = new SetupContext { Paths = paths, Command = command, DryRun = options.DryRun };
        var results = clients
            .Select(c => options.Command == "setup" ? c.Apply(context) : c.Remove(context))
            .ToList();

        if (options.Json) WriteJson(results, options.Command == "setup" ? command : null, options.DryRun, warning);
        else PrintResults(options.Command, results, command, options.DryRun, warning);
        return results.Any(r => r.Action == "error") ? 2 : 0;
    }

    /// <summary>
    /// Đường dẫn khai báo cho client: luôn trỏ qua junction "current" để cập nhật phiên bản không phải cấu hình lại.
    /// Chạy từ thư mục build (chưa cài) thì dùng chính exe đang chạy.
    /// </summary>
    private static (string Command, string? Warning) ResolveServerCommand(SetupPaths paths, string? explicitCommand)
    {
        if (explicitCommand != null) return (explicitCommand, null);
        var installRoot = Path.Combine(paths.LocalAppData, "InnoPro", "vn-revit-mcp");
        var stable = Path.Combine(installRoot, "current", "server", "vn-revit-mcp.exe");
        var realRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp");
        var self = Environment.ProcessPath;
        if (File.Exists(stable) || self == null || self.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (stable, File.Exists(stable) ? null : $"Chưa có {stable}; client sẽ báo lỗi cho tới khi cài đặt xong.");
        return (self, $"vn-revit-mcp chưa được cài vào {installRoot}; tạm khai báo exe đang chạy: {self}");
    }

    private static string ActionText(string action) => action switch
    {
        "configured" => "đã kết nối",
        "updated" => "đã cập nhật",
        "unchanged" => "đã có sẵn",
        "removed" => "đã gỡ",
        "would_configure" => "sẽ kết nối",
        "would_remove" => "sẽ gỡ",
        "not_found" => "chưa cài",
        "error" => "LỖI",
        _ => "bỏ qua",
    };

    private static void PrintResults(string command, List<ClientResult> results, string serverCommand, bool dryRun, string? warning)
    {
        var setup = command == "setup";
        Console.WriteLine(setup ? "vn-revit-mcp: kết nối AI client" : "vn-revit-mcp: gỡ khỏi AI client");
        if (dryRun) Console.WriteLine("(chạy thử --dry-run: không ghi file nào)");
        if (setup) Console.WriteLine($"Lệnh MCP: {serverCommand}");
        if (warning != null) Console.WriteLine($"Cảnh báo: {warning}");
        Console.WriteLine();

        var width = results.Max(r => r.Name.Length) + 2;
        foreach (var r in results)
        {
            var line = $"  {r.Name.PadRight(width)}{ActionText(r.Action)}";
            if (r.Paths.Count > 0 && r.Action is not ("not_found" or "skipped" or "error")) line += $"  ({string.Join("; ", r.Paths)})";
            if (r.Message != null) line += $"  {r.Message}";
            Console.WriteLine(line);
        }

        var connected = results.Where(r => r.Configured && setup).Select(r => r.Name).ToList();
        var restart = results.Where(r => r.Restart != null).ToList();
        Console.WriteLine();
        if (setup)
            Console.WriteLine(connected.Count > 0 ? $"{(dryRun ? "AI sẽ được kết nối" : "AI đã kết nối")}: {string.Join(", ", connected)}" : "Không tìm thấy AI client nào trên máy để kết nối.");
        if (restart.Count > 0 && !dryRun)
        {
            Console.WriteLine("Việc cần làm để AI nhận thay đổi:");
            foreach (var r in restart) Console.WriteLine($"  - {r.Name}: {r.Restart}");
        }
        var backups = results.SelectMany(r => r.Backups).ToList();
        if (backups.Count > 0)
        {
            Console.WriteLine("Bản sao lưu cấu hình cũ:");
            foreach (var b in backups) Console.WriteLine($"  {b}");
        }
    }

    private static void PrintClients(List<ClientResult> results)
    {
        var width = results.Max(r => r.Name.Length) + 2;
        Console.WriteLine($"  {"Client".PadRight(width)}{"Đã cài",-9}{"Đã kết nối",-12}Cấu hình");
        foreach (var r in results)
        {
            var line = $"  {r.Name.PadRight(width)}{(r.Detected ? "có" : "không"),-9}{(r.Configured ? "có" : "không"),-12}{string.Join("; ", r.Paths)}";
            if (r.Message != null) line += $"  ({r.Message})";
            Console.WriteLine(line);
        }
    }

    private static void WriteJson(List<ClientResult> results, string? serverCommand, bool dryRun, string? warning = null)
    {
        var root = new JsonObject();
        if (serverCommand != null) root["command"] = serverCommand;
        root["dry_run"] = dryRun;
        if (warning != null) root["warning"] = warning;
        root["clients"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id,
            ["name"] = r.Name,
            ["detected"] = r.Detected,
            ["configured"] = r.Configured,
            ["action"] = r.Action,
            ["paths"] = new JsonArray(r.Paths.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["backups"] = new JsonArray(r.Backups.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["restart"] = r.Restart,
            ["message"] = r.Message,
        }).ToArray());
        Console.WriteLine(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
