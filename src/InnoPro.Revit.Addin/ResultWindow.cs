using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Ui;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Kết quả lệnh cho người bấm nút: số tổng thành ô gọn, mỗi danh sách là một bảng thật (sắp xếp, chọn, Ctrl+C),
/// nút Sao chép (dán vào Excel đúng cột) và Excel; bấm đúp một dòng để chọn và zoom phần tử trong Revit.
/// </summary>
internal static class ResultWindow
{
    private const string IdsColumn = "__ids";
    private const int MaxText = 200;

    private sealed record Section(string Title, DataTable Table, Dictionary<string, string> Headers);

    public static void Show(UIApplication app, string title, JsonElement result)
    {
        var chips = new List<(string Label, string Value)>();
        var sections = new List<Section>();
        var notes = new List<string>();
        Collect(result, "", chips, sections, notes);
        var ids = new HashSet<long>();
        CollectIds(result, null, ids);

        var window = new InnoWindow(title, app.MainWindowHandle, width: 780, resizable: true) { Height = 520 };
        var body = new DockPanel { Margin = new Thickness(14, 10, 14, 0) };

        if (chips.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (label, value) in chips) wrap.Children.Add(Chip(label, value));
            DockPanel.SetDock(wrap, Dock.Top);
            body.Children.Add(wrap);
        }
        if (notes.Count > 0)
        {
            var note = new TextBlock { Text = string.Join("\n", notes), Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(note, Dock.Bottom);
            body.Children.Add(note);
        }

        var uiDoc = app.ActiveUIDocument;
        if (sections.Count == 1)
        {
            body.Children.Add(SectionView(sections[0], uiDoc, fill: true));
        }
        else if (sections.Count > 1)
        {
            var stack = new StackPanel();
            foreach (var s in sections) stack.Children.Add(SectionView(s, uiDoc, fill: false));
            body.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        else if (chips.Count == 0 && notes.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "Xong.", Foreground = Theme.Muted });
        }
        window.Body = body;
        if (sections.Count == 0) { window.SizeToContent = SizeToContent.Height; window.MaxHeight = 520; }

        window.AddButton("Sao chép tất cả", onClick: () => Clipboard.SetText(AllAsText(chips, sections, notes)));
        if (ids.Count > 0 && uiDoc != null)
            window.AddButton($"Chọn {ids.Count} phần tử", onClick: () => Select(uiDoc, ids), closes: true);
        window.AddButton("Đóng", primary: true, closes: true);
        window.ShowDialog();
    }

    // ---- Hiển thị ----

    private static Border Chip(string label, string value)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Foreground = Theme.Muted, FontSize = 11 });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value });
        return new Border
        {
            Child = panel, Background = Theme.SurfaceAlt, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 12, 6), Margin = new Thickness(0, 0, 8, 6),
        };
    }

    private static FrameworkElement SectionView(Section section, UIDocument? uiDoc, bool fill)
    {
        var table = section.Table;
        var header = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(new TextBlock
        {
            Text = $"{section.Title}  ·  {table.Rows.Count}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });

        var root = new DockPanel { Margin = new Thickness(0, 0, 0, fill ? 0 : 10) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        if (table.Rows.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = "Không có dữ liệu", Foreground = Theme.Muted, Margin = new Thickness(0, 4, 0, 4) });
            return root;
        }

        actions.Children.Add(SmallButton("copy", "Sao chép (dán vào Excel)", () => Clipboard.SetText(TableAsTsv(section))));
        actions.Children.Add(SmallButton("table", "Mở bằng Excel", () => OpenInExcel(section)));

        var grid = new DataGrid { ItemsSource = table.DefaultView, AutoGenerateColumns = true, MaxHeight = fill ? double.PositiveInfinity : 320 };
        grid.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyName == IdsColumn) { e.Cancel = true; return; }
            e.Column.Header = section.Headers.GetValueOrDefault(e.PropertyName, e.PropertyName);
        };
        if (uiDoc != null && table.Columns.Contains(IdsColumn))
        {
            grid.ToolTip = "Bấm đúp một dòng để chọn phần tử trong Revit";
            grid.MouseDoubleClick += (_, _) =>
            {
                if (grid.SelectedItem is not DataRowView row) return;
                var rowIds = ParseIds(row[IdsColumn] as string);
                if (rowIds.Count > 0) Select(uiDoc, rowIds);
            };
        }
        root.Children.Add(grid);
        return root;
    }

    private static Button SmallButton(string icon, string tooltip, Action action)
    {
        var button = new Button { Content = Icons.Shape(icon, 14), ToolTip = tooltip, Padding = new Thickness(6, 2, 6, 2), MinHeight = 24, Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private static void Select(UIDocument uiDoc, IEnumerable<long> ids)
    {
        var existing = ids.Select(id => new ElementId(id)).Where(id => uiDoc.Document.GetElement(id) != null).ToList();
        uiDoc.Selection.SetElementIds(existing);
        if (existing.Count > 0) uiDoc.ShowElements(existing);
    }

    // ---- Xuất dữ liệu ----

    private static string TableAsTsv(Section section)
    {
        var columns = section.Table.Columns.Cast<DataColumn>().Where(c => c.ColumnName != IdsColumn).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", columns.Select(c => section.Headers.GetValueOrDefault(c.ColumnName, c.ColumnName))));
        foreach (DataRow row in section.Table.Rows)
            sb.AppendLine(string.Join("\t", columns.Select(c => Clean(row[c]?.ToString()))));
        return sb.ToString();
    }

    private static void OpenInExcel(Section section)
    {
        var dir = Path.Combine(Path.GetTempPath(), "InnoPro");
        Directory.CreateDirectory(dir);
        var name = string.Concat(section.Title.Split(Path.GetInvalidFileNameChars()));
        var file = Path.Combine(dir, $"{name}-{DateTime.Now:HHmmss}.csv");
        var columns = section.Table.Columns.Cast<DataColumn>().Where(c => c.ColumnName != IdsColumn).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => Csv(section.Headers.GetValueOrDefault(c.ColumnName, c.ColumnName)))));
        foreach (DataRow row in section.Table.Rows)
            sb.AppendLine(string.Join(",", columns.Select(c => Csv(row[c]?.ToString() ?? ""))));
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true)); // BOM để Excel đọc đúng tiếng Việt
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private static string AllAsText(List<(string Label, string Value)> chips, List<Section> sections, List<string> notes)
    {
        var sb = new StringBuilder();
        foreach (var (label, value) in chips) sb.Append(label).Append('\t').AppendLine(value);
        foreach (var s in sections) sb.AppendLine().AppendLine(s.Title).Append(TableAsTsv(s));
        foreach (var n in notes) sb.AppendLine().AppendLine(n);
        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string Clean(string? value) => (value ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    // ---- Chuyển JSON kết quả thành ô số liệu, bảng, ghi chú ----

    private static void Collect(JsonElement e, string prefix, List<(string, string)> chips, List<Section> sections, List<string> notes)
    {
        var label = prefix.Length == 0 ? "Kết quả" : prefix;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object when IsTable(e):
                sections.Add(FromTable(label, e));
                return;
            case JsonValueKind.Array when e.GetArrayLength() > 0 && e.EnumerateArray().All(i => i.ValueKind == JsonValueKind.Object):
                sections.Add(FromObjects(label, e));
                return;
            case JsonValueKind.Array:
                chips.Add((label, Text(e)));
                return;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Null) continue;
                    var name = Labels.Humanize(p.Name);
                    var child = prefix.Length == 0 ? name : $"{prefix} · {name}";
                    if (p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString()?.Length ?? 0) > 80) notes.Add(p.Value.GetString()!);
                    else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Collect(p.Value, child, chips, sections, notes);
                    else chips.Add((child, Text(p.Value)));
                }
                return;
            default:
                chips.Add((label, Text(e)));
                return;
        }
    }

    private static bool IsTable(JsonElement e) =>
        e.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Array
        && e.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array;

    private static Section FromTable(string title, JsonElement e)
    {
        var keys = e.GetProperty("columns").EnumerateArray().Select(c => c.ToString()).ToList();
        var rows = e.GetProperty("rows").EnumerateArray().Select(r => (IReadOnlyList<JsonElement>)r.EnumerateArray().ToList()).ToList();
        return Build(title, keys, rows);
    }

    private static Section FromObjects(string title, JsonElement e)
    {
        var keys = new List<string>();
        foreach (var o in e.EnumerateArray())
        foreach (var p in o.EnumerateObject())
            if (!keys.Contains(p.Name)) keys.Add(p.Name);
        var rows = e.EnumerateArray()
            .Select(o => (IReadOnlyList<JsonElement>)keys.Select(k => o.TryGetProperty(k, out var v) ? v : default).ToList())
            .ToList();
        return Build(title, keys, rows);
    }

    /// <summary>Cột toàn số thì kiểu số (sắp xếp đúng); cột id ẩn gom id phần tử của từng dòng.</summary>
    private static Section Build(string title, List<string> keys, List<IReadOnlyList<JsonElement>> rows)
    {
        var table = new DataTable();
        var headers = new Dictionary<string, string>();
        for (var i = 0; i < keys.Count; i++)
        {
            var index = i;
            var values = rows.Select(r => index < r.Count ? r[index] : default)
                .Where(v => v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)).ToList();
            var numeric = values.Count > 0 && values.All(v => v.ValueKind == JsonValueKind.Number);
            table.Columns.Add($"c{i}", numeric ? typeof(double) : typeof(string));
            headers[$"c{i}"] = Labels.Humanize(keys[i]);
        }
        var hasIds = keys.Any(IsIdKey);
        if (hasIds) table.Columns.Add(IdsColumn, typeof(string));

        foreach (var r in rows)
        {
            var row = table.NewRow();
            var rowIds = new HashSet<long>();
            for (var i = 0; i < keys.Count; i++)
            {
                var v = i < r.Count ? r[i] : default;
                if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                row[$"c{i}"] = table.Columns[$"c{i}"]!.DataType == typeof(double) ? v.GetDouble() : Text(v);
                if (IsIdKey(keys[i])) CollectIds(v, keys[i], rowIds);
            }
            if (hasIds) row[IdsColumn] = string.Join(",", rowIds);
            table.Rows.Add(row);
        }
        return new Section(title, table, headers);
    }

    private static bool IsIdKey(string key) =>
        key is "element_id" or "element_ids" or "element_a" or "element_b" || key.EndsWith("_element_id") || key.EndsWith("_element_ids");

    private static string Text(JsonElement e)
    {
        var text = e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.True => "có",
            JsonValueKind.False => "không",
            JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Text)),
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            _ => e.GetRawText(),
        };
        return text.Length > MaxText ? text[..MaxText] + "…" : text;
    }

    private static HashSet<long> ParseIds(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToHashSet();

    /// <summary>Gom id phần tử: các trường tên element_id(s), element_a/b, hoặc cột id của bảng.</summary>
    private static void CollectIds(JsonElement e, string? name, HashSet<long> ids)
    {
        if (ids.Count > 5000) return;
        var isIdField = name != null && IsIdKey(name);
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
                    for (var i = 0; i < columns.Count && i < cells.Count; i++) CollectIds(cells[i], columns[i], ids);
                }
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) CollectIds(p.Value, p.Name, ids);
                break;
        }
    }
}
