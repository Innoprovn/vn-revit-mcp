using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis.CSharp;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Tab InnoPro: nhóm "AI" cố định, và mỗi Category lệnh một nhóm. Lệnh có khai báo Ribbon thành nút lớn,
/// lệnh còn lại nằm trong menu "Lệnh khác" của nhóm. Nút được thêm khi nạp lại lệnh (Revit không cho xóa nút,
/// nên lệnh bị gỡ thì nút được ẩn).
/// </summary>
internal static class RibbonManager
{
    public const string TabName = "InnoPro";
    private const string AiPanel = "AI";

    private static readonly Color Brand = Color.FromRgb(0x0B, 0x5C, 0xAD);
    private static readonly Dictionary<string, PushButton> Buttons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PulldownButton> MoreMenus = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(UIControlledApplication app, CommandRegistry registry)
    {
        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { } // tab đã có (add-in InnoPro khác tạo)

        var panel = app.CreateRibbonPanel(TabName, AiPanel);
        var assembly = typeof(RibbonManager).Assembly.Location;
        panel.AddItem(Button("InnoPro.Mcp.Toggle", "MCP\nBật/Tắt", assembly, typeof(ToggleBridgeCommand).FullName!, "AI",
            "Bật hoặc tắt kết nối cho AI (MCP). Khi tắt, không AI nào điều khiển được Revit."));
        panel.AddItem(Button("InnoPro.Mcp.Commands", "Lệnh\nAI", assembly, typeof(ShowCommandsCommand).FullName!, "≡",
            "Trạng thái kết nối, danh sách lệnh và lỗi nạp lệnh (nếu có)."));
        panel.AddItem(Button("InnoPro.Mcp.Reload", "Nạp lại\nlệnh", assembly, typeof(ReloadCommandsCommand).FullName!, "↻",
            "Nạp lại lệnh từ đĩa (CommandSet và file .cs trong thư mục Scripts) mà không cần khởi động lại Revit."));

        Sync(registry, name => app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == name) ?? app.CreateRibbonPanel(TabName, name));
    }

    /// <summary>Gọi trên luồng chính của Revit sau khi nạp lại lệnh.</summary>
    public static void Sync(UIApplication app, CommandRegistry registry) =>
        Sync(registry, name => app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == name) ?? app.CreateRibbonPanel(TabName, name));

    private static void Sync(CommandRegistry registry, Func<string, RibbonPanel> panel)
    {
        try
        {
            var commands = registry.Commands.Select(c => c.Command).ToList();
            var fresh = commands.Where(c => !Buttons.ContainsKey(c.Name)).ToList();
            var (stubAssembly, stubs) = fresh.Count == 0 ? ("", new Dictionary<string, string>()) : GenerateStubs(fresh);

            foreach (var command in fresh.OrderBy(c => c.Category).ThenBy(c => c.Name))
            {
                var panelName = command.Ribbon?.Panel ?? command.Category;
                var data = Button($"InnoPro.Cmd.{command.Name}",
                    command.Ribbon?.Text ?? command.Name.Replace('_', ' '),
                    stubAssembly, stubs[command.Name],
                    command.Ribbon?.Glyph ?? command.Category[..1].ToUpperInvariant(),
                    Tooltip(command));

                if (command.Ribbon != null)
                {
                    Buttons[command.Name] = (PushButton)panel(panelName).AddItem(data);
                }
                else
                {
                    if (!MoreMenus.TryGetValue(panelName, out var menu))
                    {
                        var menuData = new PulldownButtonData($"InnoPro.More.{panelName}", "Lệnh\nkhác")
                        {
                            ToolTip = $"Các lệnh nhóm {panelName}.",
                            LargeImage = Icon("⋯", 32),
                            Image = Icon("⋯", 16),
                        };
                        menu = (PulldownButton)panel(panelName).AddItem(menuData);
                        MoreMenus[panelName] = menu;
                    }
                    Buttons[command.Name] = menu.AddPushButton(data);
                }
            }

            var active = commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, button) in Buttons)
            {
                button.Visible = active.Contains(name);
                if (registry.TryGet(name, out var loaded)) button.ToolTip = Tooltip(loaded.Command);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Không dựng được nút ribbon", ex);
        }
    }

    /// <summary>
    /// Revit cần một lớp IExternalCommand trong một file DLL cho mỗi nút. Sinh các lớp đó bằng Roslyn,
    /// mỗi lớp chỉ gọi ButtonRunner qua delegate đặt trong AppDomain.
    /// Tên assembly khác nhau cho mỗi bộ nút: Revit nạp theo tên, trùng tên sẽ lấy nhầm DLL cũ.
    /// </summary>
    private static (string Path, Dictionary<string, string> Map) GenerateStubs(List<Sdk.IRevitCommand> commands)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = new StringBuilder("namespace InnoPro.VnRevitMcp.Buttons {\n");
        for (var i = 0; i < commands.Count; i++)
        {
            var className = $"B{i}";
            map[commands[i].Name] = $"InnoPro.VnRevitMcp.Buttons.{className}";
            source.Append($$"""
                [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
                public sealed class {{className}} : Autodesk.Revit.UI.IExternalCommand
                {
                    public Autodesk.Revit.UI.Result Execute(Autodesk.Revit.UI.ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements) =>
                        ((System.Func<string, Autodesk.Revit.UI.ExternalCommandData, Autodesk.Revit.UI.Result>)
                            System.AppDomain.CurrentDomain.GetData({{SymbolDisplay.FormatLiteral(ButtonRunner.DataKey, true)}}))
                        ({{SymbolDisplay.FormatLiteral(commands[i].Name, true)}}, data);
                }

                """);
        }
        source.Append('}');

        var text = source.ToString();
        var assemblyName = "InnoPro.VnRevitMcp.Buttons_" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        var path = CSharpCompiler.CompileToFile(assemblyName, text, "buttons.cs", out var errors)
                   ?? throw new InvalidOperationException("Không sinh được nút ribbon: " + string.Join("; ", errors));
        return (path, map);
    }

    private static string Tooltip(Sdk.IRevitCommand command)
    {
        var text = command.Description.Length > 300 ? command.Description[..300] + "…" : command.Description;
        return $"{text}\n\nAI gọi bằng: {command.Name}";
    }

    private static PushButtonData Button(string id, string text, string assembly, string className, string glyph, string tooltip) =>
        new(id, text, assembly, className)
        {
            ToolTip = tooltip,
            LargeImage = Icon(glyph, 32),
            Image = Icon(glyph, 16),
        };

    /// <summary>Icon vẽ bằng mã để repo không phụ thuộc file ảnh.</summary>
    private static BitmapSource Icon(string glyph, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                size * 0.5, Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
