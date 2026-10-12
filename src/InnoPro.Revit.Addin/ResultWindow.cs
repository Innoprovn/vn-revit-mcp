using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Hiển thị kết quả lệnh cho người bấm nút: bảng căn cột, danh sách gọn,
/// nút chọn trong Revit các phần tử có trong kết quả.
/// </summary>
internal static class ResultWindow
{
    private const int MaxRows = 500;
    private const int MaxCell = 48;

    public static void Show(UIApplication app, string title, JsonElement result)
    {
        var text = new StringBuilder();
        Format(result, text, 0);
        var ids = new HashSet<long>();
        CollectIds(result, null, ids);

        var window = new Window
        {
            Title = $"InnoPro: {title}",
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        new WindowInteropHelper(window) { Owner = app.MainWindowHandle };

        var body = new TextBox
        {
            Text = text.Length == 0 ? "Xong." : text.ToString(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        if (ids.Count > 0 && app.ActiveUIDocument is { } uiDoc)
        {
            var select = new Button { Content = $"Chọn {ids.Count} phần tử trong Revit", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            select.Click += (_, _) =>
            {
                var existing = ids.Select(id => new ElementId(id)).Where(id => uiDoc.Document.GetElement(id) != null).ToList();
                uiDoc.Selection.SetElementIds(existing);
                if (existing.Count > 0) uiDoc.ShowElements(existing);
                window.Close();
            };
            buttons.Children.Add(select);
        }
        var copy = new Button { Content = "Sao chép", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(body.Text);
        var close = new Button { Content = "Đóng", Padding = new Thickness(10, 3, 10, 3), IsCancel = true };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(body);
        window.Content = layout;
        window.ShowDialog();
    }

    private static void Format(JsonElement e, StringBuilder sb, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (e.ValueKind)
        {
            case JsonValueKind.Object when IsTable(e):
                FormatTable(e, sb, pad);
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind is JsonValueKind.Null) continue;
                    if (IsScalar(p.Value) || IsScalarArray(p.Value))
                        sb.Append(pad).Append(p.Name).Append(": ").AppendLine(Scalar(p.Value));
                    else
                    {
                        sb.Append(pad).Append(p.Name).AppendLine(":");
                        Format(p.Value, sb, indent + 1);
                    }
                }
                break;
            case JsonValueKind.Array when IsScalarArray(e):
                sb.Append(pad).AppendLine(Scalar(e));
                break;
            case JsonValueKind.Array when e.GetArrayLength() > 0 && e.EnumerateArray().All(i => i.ValueKind == JsonValueKind.Object):
                // Mảng object cùng dạng thì vẽ như bảng.
                var columns = e.EnumerateArray().First().EnumerateObject().Select(p => p.Name).ToList();
                var rows = e.EnumerateArray().Select(o => columns.Select(c => o.TryGetProperty(c, out var v) ? Scalar(v) : "").ToArray()).ToList();
                WriteTable(columns, rows, sb, pad, null);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray().Take(MaxRows))
                {
                    sb.Append(pad).AppendLine("-");
                    Format(item, sb, indent + 1);
                }
                break;
            default:
                sb.Append(pad).AppendLine(Scalar(e));
                break;
        }
    }

    private static bool IsTable(JsonElement e) =>
        e.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Array
        && e.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array;

    private static void FormatTable(JsonElement e, StringBuilder sb, string pad)
    {
        var columns = e.GetProperty("columns").EnumerateArray().Select(c => c.ToString()).ToList();
        var rows = e.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(Scalar).ToArray()).ToList();
        int? total = e.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null;
        WriteTable(columns, rows, sb, pad, total);
    }

    private static void WriteTable(List<string> columns, List<string[]> rows, StringBuilder sb, string pad, int? total)
    {
        var shown = rows.Take(MaxRows).ToList();
        var widths = columns.Select((c, i) => Math.Min(MaxCell, Math.Max(c.Length, shown.Count == 0 ? 0 : shown.Max(r => i < r.Length ? r[i].Length : 0)))).ToArray();
        string Cell(string s, int w) => (s.Length > w ? s[..(w - 1)] + "…" : s).PadRight(w);

        sb.Append(pad).AppendLine(string.Join("  ", columns.Select((c, i) => Cell(c, widths[i]))));
        sb.Append(pad).AppendLine(string.Join("  ", widths.Select(w => new string('─', w))));
        foreach (var row in shown)
            sb.Append(pad).AppendLine(string.Join("  ", widths.Select((w, i) => Cell(i < row.Length ? row[i] : "", w))));
        var count = total ?? rows.Count;
        if (count > shown.Count) sb.Append(pad).AppendLine($"… còn {count - shown.Count} dòng");
        sb.Append(pad).AppendLine($"({count} dòng)");
    }

    private static bool IsScalar(JsonElement e) => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);

    private static bool IsScalarArray(JsonElement e) => e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(IsScalar);

    private static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        JsonValueKind.True => "có",
        JsonValueKind.False => "không",
        JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Take(50).Select(Scalar)) + (e.GetArrayLength() > 50 ? $", … (+{e.GetArrayLength() - 50})" : ""),
        JsonValueKind.Object => e.GetRawText(),
        _ => e.GetRawText(),
    };

    /// <summary>Gom id phần tử: các trường tên element_id(s), element_a/b, hoặc cột element_id của bảng.</summary>
    private static void CollectIds(JsonElement e, string? name, HashSet<long> ids)
    {
        if (ids.Count > 5000) return;
        var isIdField = name is "element_id" or "element_ids" or "element_a" or "element_b";
        switch (e.ValueKind)
        {
            case JsonValueKind.Number when isIdField && e.TryGetInt64(out var id):
                ids.Add(id);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) CollectIds(item, isIdField ? name : null, ids);
                break;
            case JsonValueKind.Object when IsTable(e):
                var columns = e.GetProperty("columns").EnumerateArray().Select(c => c.ToString()).ToList();
                foreach (var row in e.GetProperty("rows").EnumerateArray())
                {
                    var cells = row.EnumerateArray().ToList();
                    for (var i = 0; i < columns.Count && i < cells.Count; i++)
                        CollectIds(cells[i], columns[i], ids);
                }
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) CollectIds(p.Value, p.Name, ids);
                break;
        }
    }
}
