using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.Tokenizers;

namespace InnoPro.Mcp.Server;

/// <summary>
/// Đo token MCP server gửi cho AI: danh sách tool (gửi kèm MỌI lượt chat) và từng lần gọi tool (tham số + kết quả).
/// Đếm bằng bộ tách o200k (GPT-4o); bộ đếm của Claude khác một chút, thường cao hơn khoảng 10–20%.
/// Ghi %LOCALAPPDATA%\InnoPro\vn-revit-mcp\logs\tokens-yyyyMMdd.jsonl; tool token_usage tổng hợp lại.
/// </summary>
public static class TokenMeter
{
    public const string ToolName = "token_usage";

    public const string Description =
        "Báo số token vn-revit-mcp đã gửi cho AI: chi phí cố định của danh sách tool mỗi lượt, tổng token theo từng lệnh, " +
        "các lần gọi nặng nhất. Dùng để biết lệnh nào tốn token và nên gọi gọn hơn (output=ids/count, group_by, fields, limit).";

    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"days":{"type":"integer","description":"Số ngày gần nhất, mặc định 1 (hôm nay)."}}}
        """).RootElement;

    private static readonly Lazy<Tokenizer> Encoder = new(() => TiktokenTokenizer.CreateForModel("gpt-4o"));
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "logs");

    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try { return Encoder.Value.CountTokens(text); }
        catch { return (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 3.5); } // dự phòng khi thiếu dữ liệu bộ tách
    }

    /// <summary>Danh sách tool vừa gửi (chi phí cố định mỗi lượt).</summary>
    public static void RecordList(string toolsJson, int toolCount) =>
        Write(new JsonObject { ["kind"] = "list", ["tools"] = toolCount, ["out"] = Count(toolsJson) });

    /// <summary>Ảnh PNG: Claude tính khoảng rộng × cao / 750 token (ảnh lớn hơn ~1568 px bị thu nhỏ trước).</summary>
    public static int ImageTokens(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24) return 0;
        double w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..20]);
        double h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..24]);
        var scale = Math.Min(1.0, 1568.0 / Math.Max(w, h));
        return (int)Math.Ceiling(w * scale * h * scale / 750);
    }

    public static void RecordCall(string tool, string? argumentsJson, string resultText, bool isError, long milliseconds, int imageTokens = 0) =>
        Write(new JsonObject
        {
            ["kind"] = "call", ["tool"] = tool, ["in"] = Count(argumentsJson), ["out"] = Count(resultText) + imageTokens,
            ["image"] = imageTokens > 0 ? imageTokens : null,
            ["ms"] = milliseconds, ["error"] = isError ? true : null,
        });

    private static void Write(JsonObject entry)
    {
        try
        {
            entry["t"] = DateTime.Now.ToString("HH:mm:ss");
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(Path.Combine(LogDir, $"tokens-{DateTime.Now:yyyyMMdd}.jsonl"), entry.ToJsonString() + "\n");
            }
        }
        catch (IOException) { } // đo đếm không được làm hỏng lệnh
    }

    public static string Report(int days)
    {
        days = Math.Clamp(days <= 0 ? 1 : days, 1, 60);
        var entries = new List<JsonNode>();
        for (var d = 0; d < days; d++)
        {
            var file = Path.Combine(LogDir, $"tokens-{DateTime.Now.AddDays(-d):yyyyMMdd}.jsonl");
            if (!File.Exists(file)) continue;
            lock (Gate)
                foreach (var line in File.ReadAllLines(file))
                    if (line.Length > 0 && JsonNode.Parse(line) is { } n) entries.Add(n);
        }

        var lists = entries.Where(e => (string?)e["kind"] == "list").ToList();
        var calls = entries.Where(e => (string?)e["kind"] == "call").ToList();
        int I(JsonNode n, string k) => n[k]?.GetValue<int>() ?? 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Token vn-revit-mcp gửi cho AI ({(days == 1 ? "hôm nay" : $"{days} ngày")}, đếm theo o200k; Claude thường cao hơn ~10–20%):");
        if (lists.Count > 0)
            sb.AppendLine($"- Danh sách tool (gửi kèm mỗi lượt): {I(lists[^1], "out")} token cho {I(lists[^1], "tools")} tool.");
        if (calls.Count == 0) { sb.AppendLine("- Chưa có lần gọi tool nào."); return sb.ToString().TrimEnd(); }

        var totalIn = calls.Sum(c => I(c, "in"));
        var totalOut = calls.Sum(c => I(c, "out"));
        sb.AppendLine($"- {calls.Count} lần gọi tool: AI gửi {totalIn} token tham số, nhận {totalOut} token kết quả (trung bình {totalOut / calls.Count}/lần).");
        sb.AppendLine("Theo lệnh (lệnh | số lần | token kết quả | trung bình | lỗi):");
        foreach (var g in calls.GroupBy(c => (string?)c["tool"] ?? "?").OrderByDescending(g => g.Sum(c => I(c, "out"))).Take(20))
            sb.AppendLine($"{g.Key} | {g.Count()} | {g.Sum(c => I(c, "out"))} | {g.Sum(c => I(c, "out")) / g.Count()} | {g.Count(c => c["error"] != null)}");
        sb.AppendLine("Nặng nhất:");
        foreach (var c in calls.OrderByDescending(c => I(c, "out")).Take(5))
            sb.AppendLine($"{c["t"]} {c["tool"]}: {I(c, "out")} token, {I(c, "ms")} ms");
        return sb.ToString().TrimEnd();
    }
}
