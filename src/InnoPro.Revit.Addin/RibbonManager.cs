using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using InnoPro.Revit.Sdk;
using InnoPro.Ui;
using Microsoft.CodeAnalysis.CSharp;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Tab InnoPro gọn: nút nhỏ xếp chồng và menu danh sách (số lệnh sẽ tăng rất nhiều).
/// - Panel "AI": Chat AI, Cửa hàng, menu Hệ thống.
/// - Mỗi Category có lệnh khai báo Ribbon: các nút nhỏ đó + menu "Tất cả" liệt kê mọi lệnh của nhóm.
/// - Category còn lại: một menu danh sách trong panel "Khác".
/// Lệnh thêm khi nạp lại vào menu của nhóm (hoặc menu "Lệnh mới"); lệnh bị gỡ thì ẩn (Revit không cho xóa nút).
/// </summary>
internal static class RibbonManager
{
    public const string TabName = "InnoPro";
    private const string AiPanel = "AI";
    private const string OtherPanel = "Khác";

    private static readonly Color Brand = Color.FromRgb(0x0B, 0x5C, 0xAD);
    // Nút ribbon không xóa được và sống suốt phiên Revit, còn lõi có thể được nạp nóng nhiều lần: trạng thái ribbon
    // giữ trong AppDomain bằng kiểu chung của .NET và Revit API để lõi mới nhận lại được.
    private const string StateKey = "InnoPro.VnRevitMcp.Ribbon";
    private static Dictionary<string, object?> State =>
        AppDomain.CurrentDomain.GetData(StateKey) as Dictionary<string, object?> ?? throw new InvalidOperationException("Ribbon chưa dựng.");
    private static Dictionary<string, List<RibbonItem>> Items => (Dictionary<string, List<RibbonItem>>)State["items"]!;
    private static Dictionary<string, PulldownButton> Menus => (Dictionary<string, PulldownButton>)State["menus"]!;
    private static PulldownButton? NewMenu
    {
        get => State["new_menu"] as PulldownButton;
        set => State["new_menu"] = value;
    }

    /// <summary>Nút hệ thống: ButtonRunner chạy lớp IExternalCommand cùng tên trong lõi đang chạy (tên có tiền tố "@").</summary>
    private static readonly (string Id, string Text, Type Command, string Glyph, string Tooltip)[] SystemButtons =
    [
        ("InnoPro.Mcp.Chat", "Chat AI", typeof(ShowChatCommand), "chat", "Chat với AI ngay trong Revit"),
        ("InnoPro.Mcp.Store", "Cửa hàng", typeof(StoreCommand), "store", "Cài lệnh do cộng đồng đóng góp"),
        ("InnoPro.Mcp.Commands", "Danh sách lệnh", typeof(ShowCommandsCommand), "list", "Lệnh đang có và lỗi nạp lệnh"),
        ("InnoPro.Mcp.Connect", "Kết nối AI", typeof(ConnectAiCommand), "connect", "Nối Claude, Cursor, VS Code... với Revit"),
        ("InnoPro.Mcp.Reload", "Nạp lại lệnh", typeof(ReloadCommandsCommand), "reload", "Nạp lại lệnh, không cần mở lại Revit"),
        ("InnoPro.Mcp.Toggle", "Bật/Tắt MCP", typeof(ToggleBridgeCommand), "power", "Cho phép hoặc chặn AI điều khiển Revit"),
        ("InnoPro.Mcp.Update", "Cập nhật", typeof(UpdateCommand), "update", "Kiểm tra bản mới"),
    ];

    public static void Initialize(UIApplication app, CommandRegistry registry)
    {
        // Lõi được nạp nóng: ribbon đã có, chỉ bổ sung nút cho lệnh mới.
        if (AppDomain.CurrentDomain.GetData(StateKey) is Dictionary<string, object?>)
        {
            Sync(app, registry);
            return;
        }
        AppDomain.CurrentDomain.SetData(StateKey, new Dictionary<string, object?>
        {
            ["items"] = new Dictionary<string, List<RibbonItem>>(StringComparer.OrdinalIgnoreCase),
            ["menus"] = new Dictionary<string, PulldownButton>(StringComparer.OrdinalIgnoreCase),
            ["new_menu"] = null,
        });

        try { app.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { } // tab đã có (add-in InnoPro khác tạo)

        var (assembly, classes) = GenerateStubs(SystemButtons.Select(b => "@" + b.Command.Name).ToList());
        PushButtonData SystemButton(int i) => Button(SystemButtons[i].Id, SystemButtons[i].Text, assembly,
            classes["@" + SystemButtons[i].Command.Name], SystemButtons[i].Glyph, SystemButtons[i].Tooltip);

        var ai = app.CreateRibbonPanel(TabName, AiPanel);
        var system = new PulldownButtonData("InnoPro.System", "Hệ thống") { ToolTip = "Lệnh, kết nối AI, cập nhật", Image = Icon("settings", 16), LargeImage = Icon("settings", 32) };
        var stacked = ai.AddStackedItems(SystemButton(0), SystemButton(1), system);
        var menu = (PulldownButton)stacked[2];
        for (var i = 2; i < SystemButtons.Length; i++) menu.AddPushButton(SystemButton(i));

        Build(registry, name => app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == name) ?? app.CreateRibbonPanel(TabName, name));
    }

    /// <summary>Gọi trên luồng chính của Revit sau khi nạp lại lệnh.</summary>
    public static void Sync(UIApplication app, CommandRegistry registry)
    {
        try
        {
            var commands = registry.Commands.Select(c => c.Command).ToList();
            var fresh = commands.Where(c => !Items.ContainsKey(c.Name)).ToList();
            if (fresh.Count > 0)
            {
                var (path, stubs) = GenerateStubs(fresh.Select(c => c.Name).ToList());
                // Nhóm mới có lệnh nổi bật (ví dụ tiện ích vừa cài từ Cửa hàng): dựng panel riêng ngay, không chờ mở lại Revit.
                foreach (var group in fresh.GroupBy(c => c.Ribbon?.Panel ?? c.Category)
                             .Where(g => g.Any(c => c.Ribbon != null) && !Menus.ContainsKey(g.Key)))
                {
                    var panel = app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == group.Key) ?? app.CreateRibbonPanel(TabName, group.Key);
                    panel.Visible = true;
                    AddFeatured(panel, group.Key, group.ToList(), path, stubs);
                    fresh.RemoveAll(c => group.Contains(c));
                }
                foreach (var command in fresh)
                {
                    var target = Menus.GetValueOrDefault(command.Category) ?? NewMenu;
                    if (target == null) continue;
                    target.Visible = true;
                    Track(command.Name, target.AddPushButton(MenuEntry(command, path, stubs[command.Name])));
                }
            }

            var active = commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, items) in Items)
            foreach (var item in items)
                item.Visible = active.Contains(name);
        }
        catch (Exception ex)
        {
            Log.Error("Không cập nhật được ribbon", ex);
        }
    }

    private static void Build(CommandRegistry registry, Func<string, RibbonPanel> panel)
    {
        try
        {
            var commands = registry.Commands.Select(c => c.Command).ToList();
            var (path, stubs) = commands.Count == 0 ? ("", new Dictionary<string, string>()) : GenerateStubs(commands.Select(c => c.Name).ToList());
            var groups = commands.GroupBy(c => c.Ribbon?.Panel ?? c.Category).OrderBy(g => g.Key).ToList();

            // Nhóm có lệnh nổi bật: panel riêng gồm các nút nhỏ + menu "Tất cả".
            foreach (var group in groups.Where(g => g.Any(c => c.Ribbon != null)))
                AddFeatured(panel(group.Key), group.Key, group.ToList(), path, stubs);

            // Nhóm còn lại: mỗi nhóm một menu danh sách trong panel "Khác", kèm menu "Lệnh mới" cho lệnh thêm về sau.
            var rest = groups.Where(g => g.All(c => c.Ribbon == null)).ToList();
            var menuData = rest.Select(g => MenuData(g.Key, Short(g.Key))).ToList();
            menuData.Add(new PulldownButtonData("InnoPro.More.New", "Lệnh mới") { Image = Icon("plus", 16), LargeImage = Icon("plus", 32), ToolTip = "Lệnh vừa thêm" });
            var menus = AddCompact(panel(OtherPanel), menuData.Cast<RibbonItemData>().ToList());
            for (var i = 0; i < rest.Count; i++)
            {
                var m = (PulldownButton)menus[i];
                Menus[rest[i].Key] = m;
                foreach (var c in rest[i].OrderBy(Labels.MenuTitle)) Track(c.Name, m.AddPushButton(MenuEntry(c, path, stubs[c.Name])));
            }
            NewMenu = (PulldownButton)menus[^1];
            NewMenu.Visible = false;
        }
        catch (Exception ex)
        {
            Log.Error("Không dựng được ribbon", ex);
        }
    }

    /// <summary>Panel của một nhóm có lệnh nổi bật: các nút nhỏ + menu "Tất cả" liệt kê mọi lệnh của nhóm.</summary>
    private static void AddFeatured(RibbonPanel panel, string key, List<IRevitCommand> group, string path, Dictionary<string, string> stubs)
    {
        var featured = group.Where(c => c.Ribbon != null).OrderBy(c => c.Name).ToList();
        var items = featured.Select(c => (RibbonItemData)Button($"InnoPro.Cmd.{c.Name}", Short(c.Ribbon!.Text), path, stubs[c.Name],
            Glyph(c), Tooltip(c))).ToList();
        items.Add(MenuData(key, "Tất cả"));
        var created = AddCompact(panel, items);

        for (var i = 0; i < featured.Count; i++) Track(featured[i].Name, created[i]);
        var menu = (PulldownButton)created[^1];
        Menus[key] = menu;
        foreach (var c in group.OrderBy(Labels.MenuTitle)) Track(c.Name, menu.AddPushButton(MenuEntry(c, path, stubs[c.Name])));
    }

    /// <summary>Thêm item thành cột 3 nút nhỏ xếp chồng; lẻ 1 thì thêm đơn.</summary>
    private static List<RibbonItem> AddCompact(RibbonPanel panel, List<RibbonItemData> items)
    {
        var created = new List<RibbonItem>();
        for (var i = 0; i < items.Count;)
        {
            // Cột 3 nút; tránh để lẻ 1 nút (sẽ thành nút to): còn 4 thì chia 2 + 2.
            var remaining = items.Count - i;
            var take = remaining == 4 ? 2 : Math.Min(3, remaining);
            var chunk = items.Skip(i).Take(take).ToList();
            i += take;
            created.AddRange(chunk.Count switch
            {
                3 => panel.AddStackedItems(chunk[0], chunk[1], chunk[2]),
                2 => panel.AddStackedItems(chunk[0], chunk[1]),
                _ => [panel.AddItem(chunk[0])],
            });
        }
        return created;
    }

    private static PulldownButtonData MenuData(string category, string label) =>
        new($"InnoPro.Menu.{category}", label)
        {
            ToolTip = $"Lệnh nhóm {category}",
            Image = Icon(Icons.Resolve(null, category), 16),
            LargeImage = Icon(Icons.Resolve(null, category), 32),
        };

    // Mục trong menu chỉ có icon nhỏ (Revit dùng LargeImage nếu có, làm menu to và nặng).
    private static PushButtonData MenuEntry(IRevitCommand c, string path, string className) =>
        new($"InnoPro.List.{c.Name}", Labels.MenuTitle(c), path, className)
        {
            ToolTip = Tooltip(c),
            Image = Icon(Glyph(c), 16),
        };

    private static void Track(string name, RibbonItem item)
    {
        if (!Items.TryGetValue(name, out var list)) Items[name] = list = [];
        list.Add(item);
    }

    private static string Short(string text) => text.Replace('\n', ' ').Trim();

    /// <summary>Tên icon vector của lệnh: icon khai báo nếu có trong bộ, không thì icon của nhóm.</summary>
    private static string Glyph(IRevitCommand c) => Icons.Resolve(c.Ribbon?.Glyph, c.Category);

    private static string Tooltip(IRevitCommand c) => Labels.Short(c.Description, 160);

    /// <summary>
    /// Revit cần một lớp IExternalCommand trong một file DLL cho mỗi nút. Sinh các lớp đó bằng Roslyn,
    /// mỗi lớp chỉ gọi ButtonRunner qua delegate đặt trong AppDomain.
    /// Tên assembly khác nhau cho mỗi bộ nút: Revit nạp theo tên, trùng tên sẽ lấy nhầm DLL cũ.
    /// </summary>
    private static (string Path, Dictionary<string, string> Map) GenerateStubs(List<string> names)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Namespace riêng cho mỗi bộ nút: Revit tìm lớp nút theo tên đầy đủ, trùng "Buttons.B1" giữa hai bộ
        // (ví dụ sau khi cài tiện ích hoặc nạp nóng) thì nút sẽ chạy nhầm lệnh của bộ kia.
        var space = "InnoPro.VnRevitMcp.Buttons.S" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", names))))[..12];
        var source = new StringBuilder($"namespace {space} {{\n");
        for (var i = 0; i < names.Count; i++)
        {
            var className = $"B{i}";
            map[names[i]] = $"{space}.{className}";
            source.Append($$"""
                [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
                public sealed class {{className}} : Autodesk.Revit.UI.IExternalCommand
                {
                    public Autodesk.Revit.UI.Result Execute(Autodesk.Revit.UI.ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements) =>
                        ((System.Func<string, Autodesk.Revit.UI.ExternalCommandData, Autodesk.Revit.UI.Result>)
                            System.AppDomain.CurrentDomain.GetData({{SymbolDisplay.FormatLiteral(ButtonRunner.DataKey, true)}}))
                        ({{SymbolDisplay.FormatLiteral(names[i], true)}}, data);
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

    private static PushButtonData Button(string id, string text, string assembly, string className, string glyph, string tooltip) =>
        new(id, text, assembly, className)
        {
            ToolTip = tooltip,
            LargeImage = Icon(glyph, 32),
            Image = Icon(glyph, 16),
        };

    /// <summary>Icon vector của bộ InnoPro (src/Shared/Ui/Icons.cs).</summary>
    private static BitmapSource Icon(string name, int size) => Icons.Bitmap(Icons.Has(name) ? name : "cube", size);
}
