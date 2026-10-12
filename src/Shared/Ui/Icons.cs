using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

namespace InnoPro.Ui;

/// <summary>
/// Bộ icon vector của vn-revit-mcp: nét vẽ trên lưới 24×24, tự thiết kế (không dùng ký tự hay font).
/// Lệnh khai báo icon bằng tên (RibbonButton.Glyph = "wall"); tên lạ thì dùng icon theo nhóm lệnh.
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hệ thống
        ["chat"] = "M21 11.5a8.5 8 0 0 1-12.4 7.1L3.5 20.5l1.6-4.4A8.5 8 0 1 1 21 11.5z M8 11.5h.01 M12 11.5h.01 M16 11.5h.01",
        ["store"] = "M4 8h16l-1.2 12.5H5.2z M9 8V6.5a3 3 0 0 1 6 0V8",
        ["settings"] = "M12 15.5a3.5 3.5 0 1 0 0-7a3.5 3.5 0 0 0 0 7z M12 2.5v3 M12 18.5v3 M2.5 12h3 M18.5 12h3 M5.3 5.3l2.1 2.1 M16.6 16.6l2.1 2.1 M5.3 18.7l2.1-2.1 M16.6 7.4l2.1-2.1",
        ["list"] = "M9 6h12 M9 12h12 M9 18h12 M4 6h.01 M4 12h.01 M4 18h.01",
        ["connect"] = "M9 15l6-6 M10.5 6.5l1.8-1.8a4 4 0 0 1 5.7 5.7l-1.8 1.8 M13.5 17.5l-1.8 1.8a4 4 0 0 1-5.7-5.7l1.8-1.8",
        ["reload"] = "M20 12a8 8 0 1 1-2.4-5.7 M20 4v4.5h-4.5",
        ["power"] = "M12 3v8 M7 6.3a7.5 7.5 0 1 0 10 0",
        ["update"] = "M12 3.5v11 M7.5 10.5l4.5 4.5 4.5-4.5 M4.5 20h15",
        ["plus"] = "M12 5v14 M5 12h14",
        ["key"] = "M8 15.5a4 4 0 1 1 0-8a4 4 0 0 1 0 8z M11.5 11.5h9 M17.5 11.5v3.5 M20.5 11.5v2.5",
        ["send"] = "M4 12l16-8-6 16-2.5-6.5z M11.5 13.5L20 4",

        // Truy vấn, tham số
        ["search"] = "M10.5 17a6.5 6.5 0 1 0 0-13a6.5 6.5 0 0 0 0 13z M15.5 15.5L21 21",
        ["stats"] = "M18 4.5H6.5l6 7.5-6 7.5H18",
        ["params"] = "M4 6h16 M4 12h16 M4 18h16 M8 4v4 M15 10v4 M10 16v4",
        ["edit"] = "M4 20h4.5L19.5 9 15 4.5 4 15.5z M13 6.5l4.5 4.5",
        ["copy"] = "M9 9h11v11H9z M5 15H4V4h11v1",
        ["count"] = "M9.5 3.5l-2 17 M16.5 3.5l-2 17 M4 9h16.5 M3.5 15H20",

        // QA-QC
        ["warning"] = "M12 3.5l9.5 16.5h-19z M12 10v4.5 M12 17.5h.01",
        ["clash"] = "M4 4h10v10H4z M10 10h10v10H10z M10 10h4v4h-4z",
        ["duplicate"] = "M8 8h12v12H8z M4 16V4h12",
        ["empty"] = "M12 20.5a8.5 8.5 0 1 0 0-17a8.5 8.5 0 0 0 0 17z M6 18L18 6",
        ["health"] = "M3 12h4l2.5-6 4 12 2.5-6h5",
        ["check"] = "M4.5 12.5l5 5L20 7",
        ["room"] = "M3.5 3.5h17v17h-17z M3.5 12.5h8v-9 M12.5 20.5v-5",

        // Dựng hình
        ["cube"] = "M12 3l8.5 4.5v9L12 21l-8.5-4.5v-9z M3.5 7.5L12 12l8.5-4.5 M12 12v9",
        ["wall"] = "M3 5h18v14H3z M3 9.7h18 M3 14.3h18 M9 5v4.7 M15 9.7v4.6 M9 14.3V19",
        ["floor"] = "M2.5 13.5L12 9l9.5 4.5L12 18z M2.5 13.5V16L12 20.5l9.5-4.5v-2.5",
        ["column"] = "M8.5 4h7v16h-7z M6 4h12 M6 20h12",
        ["beam"] = "M3 6h18 M3 18h18 M12 6v12 M8 6v0 M3 6v2 M21 6v2 M3 18v-2 M21 18v-2",
        ["door"] = "M6 20.5V3.5h12v17 M3.5 20.5h17 M14.5 12h.01",
        ["pile"] = "M4.5 4h15v5h-15z M9 9v11.5 M15 9v11.5",
        ["join"] = "M4 4h9v9H4z M11 11h9v9h-9z",
        ["cad"] = "M4 4h16v16H4z M4 9h6v11 M10 9l10 11",
        ["select"] = "M5.5 3.5l13 7.5-5.5 1.8-2.2 5.7z",
        ["layers"] = "M12 3.5l9 4.8-9 4.8-9-4.8z M3 12.5l9 4.8 9-4.8",
        ["grid"] = "M7 3v18 M17 3v18 M3 7h18 M3 17h18",

        // Bản vẽ, dữ liệu
        ["sheet"] = "M6 3h9l4 4v14H6z M14.5 3v4.5H19 M9 17h7",
        ["view"] = "M3.5 6h17v12h-17z M3.5 10h17 M10 10v8",
        ["tag"] = "M3.5 12V4h8l9 9-8 8z M8 8.5h.01",
        ["dimension"] = "M3.5 7v10 M20.5 7v10 M3.5 12h17 M7.5 9.5L3.5 12l4 2.5 M16.5 9.5l4 2.5-4 2.5",
        ["dim-grid"] = "M6 3v18 M18 3v18 M6 12h12 M9 9.5L6 12l3 2.5 M15 9.5l3 2.5-3 2.5",
        ["pdf"] = "M6 3h9l4 4v14H6z M14.5 3v4.5H19 M12.5 10.5v6.5 M9.5 14l3 3 3-3",
        ["table"] = "M3.5 5h17v14h-17z M3.5 10h17 M3.5 14.5h17 M9.5 5v14",
        ["quantity"] = "M4 20h16 M6.5 20V11 M12 20V5 M17.5 20v-6",
        ["document"] = "M6 3h9l4 4v14H6z M14.5 3v4.5H19 M12 11v6 M9 14h6",
        ["eye"] = "M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12z M12 15a3 3 0 1 0 0-6a3 3 0 0 0 0 6z",
        ["code"] = "M8.5 7L3.5 12l5 5 M15.5 7l5 5-5 5 M13.5 4.5l-3 15",

        // Rebar
        ["rebar"] = "M4 7h16 M4 12h16 M4 17h16 M7.5 4v16 M16.5 4v16",
        ["detach"] = "M9 15l-1.8 1.8a3.8 3.8 0 0 1-5.4-5.4L3.6 9.6 M15 9l1.8-1.8a3.8 3.8 0 0 1 5.4 5.4l-1.8 1.8 M8.5 8.5L4 4 M15.5 15.5L20 20",
        ["move"] = "M12 3v18 M3 12h18 M9 6l3-3 3 3 M9 18l3 3 3-3 M6 9l-3 3 3 3 M18 9l3 3-3 3",
        ["filter"] = "M3.5 4.5h17L14 13v6l-4 2v-8z",
    };

    /// <summary>Icon theo nhóm lệnh khi lệnh không khai báo icon.</summary>
    private static readonly Dictionary<string, string> CategoryIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Truy vấn"] = "search", ["Tham số"] = "params", ["QA-QC"] = "check", ["Dựng hình"] = "cube",
        ["Dựng hình CAD"] = "cad", ["Rebar"] = "rebar", ["Bản vẽ"] = "sheet", ["Dữ liệu"] = "table",
        ["Chọn"] = "select", ["Giao diện"] = "eye", ["Nâng cao"] = "code", ["Tài liệu"] = "document",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Has(string? name) => name != null && Paths.ContainsKey(name);

    /// <summary>Tên icon cho một lệnh: icon khai báo nếu có trong bộ, không thì icon của nhóm.</summary>
    public static string Resolve(string? declared, string category) =>
        Has(declared) ? declared! : CategoryIcons.GetValueOrDefault(category, "cube");

    public static Geometry Geometry(string name)
    {
        if (Cache.TryGetValue(name, out var g)) return g;
        g = System.Windows.Media.Geometry.Parse(Paths.GetValueOrDefault(name, Paths["cube"]));
        g.Freeze();
        return Cache[name] = g;
    }

    /// <summary>Ảnh icon cho ribbon: nét màu thương hiệu trên nền trong suốt.</summary>
    public static BitmapSource Bitmap(string name, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var scale = size / 24.0;
            dc.PushTransform(new ScaleTransform(scale, scale));
            var pen = new Pen(Theme.Brand, size <= 16 ? 2.1 : 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, Geometry(name));
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Icon dạng phần tử WPF (cho nút trong khung chat, cửa sổ).</summary>
    public static ShapePath Shape(string name, double size = 16, Brush? brush = null) => new()
    {
        Data = Geometry(name),
        Stroke = brush ?? Theme.Muted,
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        SnapsToDevicePixels = true,
    };
}
