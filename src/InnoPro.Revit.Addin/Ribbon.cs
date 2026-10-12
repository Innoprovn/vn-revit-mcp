using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace InnoPro.Revit.Addin;

internal static class Ribbon
{
    public const string TabName = "InnoPro";

    private static readonly Color Brand = Color.FromRgb(0x0B, 0x5C, 0xAD);

    public static void Build(UIControlledApplication application)
    {
        try { application.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { } // tab đã có (add-in InnoPro khác tạo)

        var panel = application.CreateRibbonPanel(TabName, "AI");
        var assembly = typeof(Ribbon).Assembly.Location;

        AddButton(panel, assembly, "InnoPro.Mcp.Toggle", "MCP\nBật/Tắt", typeof(ToggleBridgeCommand), "AI",
            "Bật hoặc tắt kết nối cho AI (MCP). Khi tắt, không AI nào điều khiển được Revit.");
        AddButton(panel, assembly, "InnoPro.Mcp.Commands", "Lệnh\nAI", typeof(ShowCommandsCommand), "≡",
            "Xem trạng thái kết nối và danh sách lệnh AI đang có.");
    }

    private static void AddButton(RibbonPanel panel, string assembly, string id, string text, Type command, string glyph, string tooltip)
    {
        var data = new PushButtonData(id, text, assembly, command.FullName)
        {
            ToolTip = tooltip,
            LargeImage = Icon(glyph, 32),
            Image = Icon(glyph, 16),
        };
        panel.AddItem(data);
    }

    /// <summary>Icon vẽ bằng mã để repo không phụ thuộc file ảnh.</summary>
    private static BitmapSource Icon(string glyph, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.5, Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
