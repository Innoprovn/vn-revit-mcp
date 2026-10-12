using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using InnoPro.Revit.Sdk;
using InnoPro.Ui;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Chữ hiển thị ngắn gọn cho người dùng: lấy từ mô tả tiếng Việt của lệnh/tham số thay vì tên kỹ thuật snake_case.
/// </summary>
internal static partial class Labels
{
    /// <summary>Tiêu đề lệnh: nhãn nút ribbon nếu có, không thì tên lệnh viết thường có khoảng trắng.</summary>
    public static string CommandTitle(IRevitCommand command) =>
        command.Ribbon?.Text.Replace('\n', ' ') ?? Humanize(command.Name);

    /// <summary>Tên trong menu danh sách: nhãn nút nếu có, không thì cụm đầu của mô tả tiếng Việt.</summary>
    public static string MenuTitle(IRevitCommand command)
    {
        if (command.Ribbon != null) return command.Ribbon.Text.Replace('\n', ' ');
        var head = command.Description.Split([':', ',', '.', '(', ';'], 2)[0].Trim();
        // Cắt ở từ nối cho gọn: "Danh sách tầng theo cao độ" -> "Danh sách tầng".
        foreach (var joint in new[] { " theo ", " trong ", " của ", " từ ", " cho ", " bằng " })
        {
            var at = head.IndexOf(joint, StringComparison.OrdinalIgnoreCase);
            if (at >= 8) { head = head[..at]; break; }
        }
        return head.Length is > 0 and <= 38 ? head : Humanize(command.Name);
    }

    /// <summary>Nhãn tham số: cụm đầu của mô tả ("Sai số vị trí, mặc định 5 mm." → "Sai số vị trí").</summary>
    public static string Field(string name, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            var head = description.Split([',', '.', '(', ';', ':'], 2)[0].Trim();
            if (head.Length is > 0 and <= 40 && !head.Contains("mặc định", StringComparison.OrdinalIgnoreCase)) return head;
        }
        return Humanize(name);
    }

    /// <summary>Giá trị mặc định ghi trong mô tả ("mặc định 30" → "30"), dùng làm chữ mờ trong ô nhập.</summary>
    public static string? Default(string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var m = DefaultPattern().Match(description);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Câu đầu, cắt gọn.</summary>
    public static string Short(string text, int max)
    {
        var first = InnoPro.Bridge.CommandCatalog.FirstSentence(text ?? "").Trim();
        return first.Length <= max ? first : first[..(max - 1)].TrimEnd() + "…";
    }

    private static readonly Dictionary<string, string> Units = new()
    {
        ["mm"] = "mm", ["m"] = "m", ["m2"] = "m²", ["m3"] = "m³", ["mm2"] = "mm²", ["kg"] = "kg", ["mb"] = "MB", ["deg"] = "°",
    };

    // Tên hiển thị tiếng Việt cho các khóa kết quả hay gặp (chỉ đổi chữ trên giao diện, dữ liệu cho AI giữ nguyên).
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "Tên", ["count"] = "Số lượng", ["total"] = "Tổng", ["category"] = "Category", ["categories"] = "Category",
        ["type"] = "Loại", ["types"] = "Loại", ["family"] = "Family", ["level"] = "Tầng", ["levels"] = "Tầng",
        ["id"] = "Id", ["element_id"] = "Id", ["element_ids"] = "Id", ["ids"] = "Id", ["elements"] = "Phần tử",
        ["view"] = "View", ["views"] = "View", ["sheet"] = "Sheet", ["sheets"] = "Sheet", ["mark"] = "Mark",
        ["length"] = "Chiều dài", ["area"] = "Diện tích", ["volume"] = "Thể tích", ["weight"] = "Khối lượng",
        ["diameter"] = "Đường kính", ["quantity"] = "Số lượng", ["value"] = "Giá trị", ["status"] = "Trạng thái",
        ["reason"] = "Lý do", ["problem"] = "Vấn đề", ["error"] = "Lỗi", ["errors"] = "Lỗi", ["message"] = "Thông báo",
        ["notes"] = "Ghi chú", ["created"] = "Đã tạo", ["changed"] = "Đã đổi", ["skipped"] = "Bỏ qua", ["failed"] = "Lỗi",
        ["found"] = "Tìm thấy", ["warnings"] = "Cảnh báo", ["warning"] = "Cảnh báo", ["description"] = "Mô tả",
        ["parameter"] = "Tham số", ["parameters"] = "Tham số", ["file"] = "File", ["path"] = "Đường dẫn",
        ["total_model_elements"] = "Tổng phần tử", ["category_count"] = "Số category", ["host"] = "Host",
        ["severity"] = "Mức độ", ["layer"] = "Layer", ["layers"] = "Layer", ["width"] = "Bề rộng", ["height"] = "Chiều cao",
        ["elevation"] = "Cao độ", ["number"] = "Số", ["room"] = "Phòng", ["rooms"] = "Phòng", ["walls"] = "Tường",
    };

    /// <summary>"total_length_m" → "Total length (m)"; khóa quen thuộc thì dùng tên tiếng Việt.</summary>
    public static string Humanize(string name)
    {
        if (Words.TryGetValue(name, out var vn)) return vn;
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? unit = null;
        if (parts.Count > 1 && Units.TryGetValue(parts[^1].ToLowerInvariant(), out var u)) { unit = u; parts.RemoveAt(parts.Count - 1); }
        var words = string.Join(' ', parts).Trim();
        if (words.Length > 0) words = char.ToUpperInvariant(words[0]) + words[1..];
        return unit == null ? words : $"{words} ({unit})";
    }

    /// <summary>Ô nhập có chữ mờ gợi ý khi đang trống.</summary>
    public static FrameworkElement WithPlaceholder(TextBox box, string? placeholder)
    {
        if (string.IsNullOrEmpty(placeholder)) return box;
        var hint = new TextBlock
        {
            Text = placeholder, Foreground = Theme.Muted, Opacity = 0.7, IsHitTestVisible = false,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    [GeneratedRegex(@"mặc định\s*(?:là\s*)?([^,.;)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultPattern();
}
