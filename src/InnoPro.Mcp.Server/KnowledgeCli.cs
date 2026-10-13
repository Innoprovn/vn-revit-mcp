using System.Text;

namespace InnoPro.Mcp.Server;

/// <summary>
/// "vn-revit-mcp skill|api ...": tra kỹ năng và Revit API từ dòng lệnh. Chat bên trong Revit gọi lệnh này
/// nên dùng chung đúng một bộ kỹ năng và chỉ mục API với MCP server.
///   vn-revit-mcp.exe skill [--query q] [--name n]
///   vn-revit-mcp.exe api   [--query q] [--id id] [--kind k] [--limit n]
/// </summary>
public static class KnowledgeCli
{
    public static bool IsKnowledgeCommand(string[] args) => args.Length > 0 && args[0] is "skill" or "api";

    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i + 1 < args.Length; i += 2)
            if (args[i].StartsWith("--")) options[args[i][2..]] = args[i + 1];
        string? Get(string key) => options.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        Console.WriteLine(args[0] == "skill"
            ? Skills.Run(Get("query"), Get("name"))
            : await new Knowledge.RevitApiSearch().RunAsync(Get("query"), Get("id"), Get("kind"),
                int.TryParse(Get("limit"), out var limit) ? limit : 15, CancellationToken.None));
        return 0;
    }
}
