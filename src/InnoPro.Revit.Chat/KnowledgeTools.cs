using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace InnoPro.Revit.Chat;

/// <summary>
/// revit_skill và revit_api cho chat trong Revit: gọi "vn-revit-mcp.exe skill|api" của bản đã cài,
/// dùng chung bộ kỹ năng và chỉ mục API với MCP server. Chưa cài (bản build lập trình viên) thì không có hai tool này.
/// </summary>
internal static class KnowledgeTools
{
    public const string SkillTool = "revit_skill";
    public const string ApiTool = "revit_api";

    private static readonly string Exe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "current", "server", "vn-revit-mcp.exe");

    public static bool Available => File.Exists(Exe);

    public static IEnumerable<ToolDef> Definitions() =>
    [
        new(SkillTool,
            "Cách làm Revit đã kiểm chứng (bước, bẫy, cách kiểm tra). Gọi TRƯỚC khi làm việc khó hoặc nhiều bước: query để tìm, name để đọc.",
            JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"},"name":{"type":"string"}}}""").RootElement.Clone()),
        new(ApiTool,
            "Tra Revit API (lớp, hàm, thuộc tính) trước khi viết run_csharp. query tiếng Anh, id để xem chi tiết.",
            JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"},"id":{"type":"string"},"kind":{"type":"string"},"limit":{"type":"integer"}}}""").RootElement.Clone()),
    ];

    public static bool Handles(string name) => name is SkillTool or ApiTool;

    public static async Task<string> RunAsync(string name, JsonElement args, CancellationToken token)
    {
        var start = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(name == SkillTool ? "skill" : "api");
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var p in args.EnumerateObject())
            {
                start.ArgumentList.Add("--" + p.Name);
                start.ArgumentList.Add(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
            }

        using var process = Process.Start(start) ?? throw new ChatException("Không chạy được vn-revit-mcp.exe.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2)); // lần đầu tra API phải lập chỉ mục (vài chục giây)
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            if (token.IsCancellationRequested) throw;
            throw new ChatException("Tra cứu quá lâu, đã dừng.");
        }
    }
}
