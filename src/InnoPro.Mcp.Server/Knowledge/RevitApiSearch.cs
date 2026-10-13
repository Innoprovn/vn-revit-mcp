using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace InnoPro.Mcp.Server.Knowledge;

/// <summary>
/// Tra cứu chỉ mục Revit API (tool revit_api). Lần đầu tự lập chỉ mục từ Revit trên máy (vài chục giây), sau đó đọc cache.
/// Kết quả gọn, mỗi mục một dòng: AI tra trước khi viết run_csharp để viết đúng ngay, đỡ vòng lỗi biên dịch tốn token.
/// </summary>
public sealed partial class RevitApiSearch
{
    private sealed record Item(char Kind, string Id, string Type, string Member, string Signature, string Years, string Doc);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Item>? _items;

    public const string ToolName = "revit_api";

    public const string Description =
        "Tra Revit API của bản Revit cài trên máy (2025/2026): lớp, hàm, thuộc tính, enum kèm mô tả và phiên bản có mặt. " +
        "Dùng TRƯỚC khi viết run_csharp để gọi đúng tên, đúng tham số. query bằng tiếng Anh: tên (\"NewDimension\", \"Rebar.GetFullGeometryForView\") " +
        "hoặc từ khóa (\"dimension reference rebar\"); id để xem đầy đủ một mục (với lớp: liệt kê thành viên). Không cần mở Revit.";

    public static readonly System.Text.Json.JsonElement Schema = System.Text.Json.JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Tên hoặc từ khóa tiếng Anh." },
            "id": { "type": "string", "description": "Id một mục (lấy từ kết quả tìm) để xem chi tiết." },
            "kind": { "type": "string", "enum": ["type", "method", "property", "enum", "event", "constructor"], "description": "Lọc loại." },
            "limit": { "type": "integer", "description": "Mặc định 15." }
          }
        }
        """).RootElement;

    public async Task<string> RunAsync(string? query, string? id, string? kind, int limit, CancellationToken token)
    {
        var items = await LoadAsync(token);
        if (items.Count == 0) return "Không thấy Revit 2025+ trên máy nên không tra được API.";
        if (!string.IsNullOrWhiteSpace(id)) return Detail(items, id.Trim());
        if (string.IsNullOrWhiteSpace(query)) return "Cần query hoặc id.";
        return Search(items, query, kind, Math.Clamp(limit <= 0 ? 15 : limit, 1, 60));
    }

    private async Task<List<Item>> LoadAsync(CancellationToken token)
    {
        if (_items != null) return _items;
        await _gate.WaitAsync(token);
        try
        {
            if (_items != null) return _items;
            var dirs = RevitApiIndex.InstalledRevitDirs();
            if (dirs.Count == 0) return _items = [];
            var file = RevitApiIndex.CacheFile(dirs);
            var stale = !File.Exists(file) || dirs.Any(d => File.GetLastWriteTimeUtc(Path.Combine(d, "RevitAPI.dll")) > File.GetLastWriteTimeUtc(file));
            if (stale) await Task.Run(() => RevitApiIndex.Build(file, dirs), token);
            return _items = Read(file);
        }
        finally { _gate.Release(); }
    }

    private static List<Item> Read(string file)
    {
        var list = new List<Item>(40000);
        using var reader = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            var id = f[1];
            var core = id.Contains('(') ? id[..id.IndexOf('(')] : id;
            string type, member;
            if (f[0] == "T") { type = core; member = ""; }
            else
            {
                var dot = core.Replace(".#ctor", "\u0001ctor").LastIndexOf('.');
                type = core[..dot];
                member = core[(dot + 1)..];
            }
            list.Add(new Item(f[0][0], id, type, member, f[2], f[3], f[4]));
        }
        return list;
    }

    private static string Search(List<Item> items, string query, string? kind, int limit)
    {
        var filter = kind switch
        {
            "type" => 'T', "method" => 'M', "property" => 'P', "enum" => 'F', "event" => 'E', "constructor" => 'C', _ => '\0',
        };
        var whole = query.Trim();
        var terms = Words().Matches(whole).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        if (terms.Count == 0) return "Query rỗng.";

        var scored = new List<(Item Item, double Score)>();
        foreach (var it in items)
        {
            if (filter != '\0' && it.Kind != filter) continue;
            var member = it.Member.ToLowerInvariant();
            var typeName = ShortType(it.Type).ToLowerInvariant();
            var full = (it.Type + "." + it.Member).ToLowerInvariant();
            double score = 0;
            var all = true;
            foreach (var t in terms)
            {
                if (member == t || (it.Kind == 'T' && typeName == t)) score += 30;
                else if (member.Contains(t)) score += 12;
                else if (typeName == t) score += 10;
                else if (typeName.Contains(t)) score += 6;
                else if (it.Signature.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 3;
                else if (it.Doc.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 1;
                else { all = false; break; }
            }
            if (!all) continue;
            if (full.EndsWith(whole.ToLowerInvariant()) || full.Contains("." + whole.ToLowerInvariant())) score += 40;
            if (it.Kind == 'F') score *= 0.6;          // giá trị enum rất nhiều, ưu tiên lớp/hàm
            if (it.Doc.Length == 0) score *= 0.8;
            scored.Add((it, score));
        }

        if (scored.Count == 0) return $"Không có mục nào khớp \"{query}\". Thử từ khóa tiếng Anh khác hoặc ít từ hơn.";
        var top = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Item.Id.Length).Take(limit).ToList();
        var sb = new StringBuilder($"{scored.Count} mục khớp, hiện {top.Count}. Dòng: loại | id | chữ ký [năm] | mô tả\n");
        foreach (var (it, _) in top) sb.Append(Line(it, 220)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string Detail(List<Item> items, string id)
    {
        var it = items.FirstOrDefault(i => i.Id == id)
                 ?? items.FirstOrDefault(i => i.Kind == 'T' && (i.Id.EndsWith("." + id) || i.Id == id))
                 ?? items.FirstOrDefault(i => (i.Type + "." + i.Member).EndsWith(id, StringComparison.Ordinal));
        if (it == null) return $"Không có id \"{id}\". Tìm bằng query trước.";
        var sb = new StringBuilder(Line(it, 1500)).Append('\n');
        if (it.Kind == 'T')
        {
            var members = items.Where(i => i.Kind != 'T' && i.Type == it.Id).ToList();
            sb.Append($"Thành viên ({members.Count}):\n");
            foreach (var m in members.OrderBy(m => "CPMEF".IndexOf(m.Kind)).ThenBy(m => m.Member).Take(150))
                sb.Append($"{m.Kind} {m.Signature}{(m.Years.Contains(',') ? "" : $" [{m.Years}]")}\n");
            if (members.Count > 150) sb.Append($"…(+{members.Count - 150})\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Line(Item it, int docMax)
    {
        var where = it.Kind == 'T' ? it.Type : ShortType(it.Type);
        var sig = it.Kind == 'T' ? it.Signature : $"{where}: {it.Signature}";
        var doc = it.Doc.Length > docMax ? it.Doc[..(docMax - 1)] + "…" : it.Doc;
        return $"{it.Kind} | {it.Id} | {sig} [{it.Years}]{(doc.Length > 0 ? " | " + doc : "")}";
    }

    // Autodesk.Revit.DB.Structure.Rebar → Rebar
    private static string ShortType(string type) => type[(type.LastIndexOf('.') + 1)..];

    [GeneratedRegex(@"[A-Za-z0-9_]+")]
    private static partial Regex Words();
}
