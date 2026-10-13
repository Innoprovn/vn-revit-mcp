using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Revit.Sdk;
using AW = Autodesk.Windows;

namespace InnoPro.Revit.Commands.RevitUi;

/// <summary>
/// Bản đồ giao diện Revit cho AI: đọc cây ribbon thật đang hiện (tab → panel → nút, tooltip, mã lệnh) để AI biết
/// "bấm nút nào thì ra gì", và kích hoạt lệnh có sẵn của Revit khi không có API tương đương.
/// </summary>
internal static class RibbonMap
{
    internal sealed record Item(string Tab, string Panel, string Text, string Id, string Tooltip, bool Postable);

    private static List<Item>? _cache;
    private static DateTime _cachedAt;

    public static List<Item> Get()
    {
        if (_cache != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(2)) return _cache;
        var items = new List<Item>();
        foreach (var tab in AW.ComponentManager.Ribbon.Tabs)
        foreach (var panel in tab.Panels)
            Walk(items, Clean(tab.Title ?? tab.Name), Clean(panel.Source?.Title ?? ""), panel.Source?.Items);
        _cachedAt = DateTime.UtcNow;
        return _cache = items.GroupBy(i => i.Tab + "|" + i.Id + "|" + i.Text).Select(g => g.First()).ToList();
    }

    private static void Walk(List<Item> items, string tab, string panel, IEnumerable<AW.RibbonItem>? source)
    {
        if (source == null) return;
        foreach (var item in source)
        {
            switch (item)
            {
                case AW.RibbonRowPanel row: Walk(items, tab, panel, row.Items); continue;
                case AW.RibbonListButton list:
                    Walk(items, tab, panel, list.Items);
                    break;
            }
            var text = Clean(item.Text ?? item.AutomationName ?? "");
            if (text.Length == 0 || item.Id is not { Length: > 0 } id) continue;
            items.Add(new Item(tab, panel, text, id, Tooltip(item), CanPost(id)));
        }
    }

    private static string Tooltip(AW.RibbonItem item) => item.ToolTip switch
    {
        AW.RibbonToolTip t => Clean($"{t.Content} {t.ExpandedContent}"),
        string s => Clean(s),
        _ => Clean(item.Description ?? ""),
    };

    private static bool CanPost(string id)
    {
        try { return RevitCommandId.LookupCommandId(id) != null; }
        catch (Exception) { return false; }
    }

    private static string Clean(string s) => string.Join(" ", s.Replace('\n', ' ').Replace('\r', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Bỏ dấu, chữ thường: tìm "tuong" ra "Tường", "dim" ra "Dimension".</summary>
    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }
}

public sealed class RevitUiFind : RevitCommandBase
{
    public override string Name => "revit_ui_find";
    public override string Category => "Giao diện Revit";
    public override string Description =>
        "Tìm nút/lệnh trên ribbon Revit đang hiện (kể cả add-in khác) theo chữ trên nút, tooltip hoặc mã lệnh: trả tab, panel, mã lệnh, " +
        "có kích hoạt được không. Dùng khi không có lệnh API phù hợp, rồi chạy bằng revit_ui_post.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Chữ cần tìm, vd \"aligned dimension\", \"wall\", \"tag all\"." },
            "tab": { "type": "string", "description": "Chỉ tìm trong tab này." },
            "limit": { "type": "integer", "description": "Mặc định 15." }
          },
          "required": ["query"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var terms = RibbonMap.Fold(Args.GetString(arguments, "query")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tab = Args.GetStringOrDefault(arguments, "tab") is { } t ? RibbonMap.Fold(t) : null;
        var map = RibbonMap.Get();
        var hits = map
            .Where(i => tab == null || RibbonMap.Fold(i.Tab).Contains(tab))
            .Select(i => (Item: i, Text: RibbonMap.Fold(i.Text), All: RibbonMap.Fold($"{i.Text} {i.Tooltip} {i.Id} {i.Panel}")))
            .Where(x => terms.All(term => x.All.Contains(term)))
            .OrderByDescending(x => terms.Count(term => x.Text.Contains(term)))
            .ThenByDescending(x => x.Item.Postable)
            .Take((int)Args.GetLongOrDefault(arguments, "limit", 15))
            .Select(x => new
            {
                tab = x.Item.Tab, panel = x.Item.Panel, text = x.Item.Text, id = x.Item.Id, postable = x.Item.Postable,
                tooltip = x.Item.Tooltip.Length > 140 ? x.Item.Tooltip[..139] + "…" : x.Item.Tooltip,
            })
            .ToList();
        return new { ribbon_items = map.Count, found = hits.Count, items = Table.Of(hits) };
    }
}

public sealed class RevitUiPost : RevitCommandBase
{
    public override string Name => "revit_ui_post";
    public override string Category => "Giao diện Revit";
    public override string Description =>
        "Kích hoạt một lệnh có sẵn của Revit như người bấm nút (vd Aligned Dimension, Tag by Category): id lấy từ revit_ui_find, " +
        "hoặc tên PostableCommand (vd \"AlignedDimension\"). Có clicks thì \"bàn tay ảo\" bấm tiếp vào các điểm đó trên view đang mở như người dùng " +
        "(điểm mm hoặc element_id), kết thúc bằng Esc; xem kết quả bằng revit_ui_result. Không có clicks thì người dùng thao tác tiếp.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "description": "Mã lệnh ribbon (ID_...) hoặc tên PostableCommand." },
            "clicks": { "type": "array", "description": "Điểm bấm lần lượt trên view đang mở: {x,y,z} mm, hoặc {element_id, at: center|start|end}; thêm tab: n để bấm Tab n lần trước khi bấm (đổi đối tượng được bắt).",
              "items": { "type": "object" } },
            "finish": { "type": "integer", "description": "Số lần Esc sau khi bấm xong để kết thúc lệnh. Mặc định 2." },
            "dialog": { "type": "string", "enum": ["none", "ok", "cancel", "yes", "no"], "description": "Tự trả lời hộp thoại hiện ra. Mặc định none (để người dùng thấy)." },
            "mode": { "type": "string", "enum": ["message", "input"], "description": "message (mặc định): gửi thẳng vào view, không chiếm chuột. input: dùng chuột thật." },
            "interval_ms": { "type": "integer", "description": "Nghỉ giữa các lần bấm. Mặc định 350." },
            "zoom": { "type": "boolean", "description": "Tự phóng to vùng có các điểm bấm trước khi bấm. Mặc định true." }
          },
          "required": ["id"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var id = Args.GetString(arguments, "id").Trim();
        var command = Enum.TryParse<PostableCommand>(id, ignoreCase: true, out var postable)
            ? RevitCommandId.LookupPostableCommandId(postable)
            : RevitCommandId.LookupCommandId(id);
        if (command == null) throw new CommandException($"Không có lệnh '{id}'. Tìm mã đúng bằng revit_ui_find.");
        var app = context.UIApplication;
        if (!app.CanPostCommand(command)) throw new CommandException($"Revit không cho kích hoạt '{id}' lúc này (có thể cần mở view phù hợp hoặc chọn phần tử trước).");
        var clicks = Args.Has(arguments, "clicks") ? Args.GetArray(arguments, "clicks") : [];
        if (clicks.Count == 0)
        {
            app.PostCommand(command);
            return new { posted = command.Name, note = "Revit sẽ chạy lệnh ngay sau lượt này." };
        }

        var uidoc = context.UIDocument;
        var view = uidoc.ActiveView;
        var (uiView, rect) = VirtualHand.ActiveUiView(uidoc);
        var model = clicks.Select(c => VirtualHand.ModelPoint(uidoc.Document, c)).ToList();
        // Như người dùng: phóng to vùng có các điểm bấm cho dễ bắt đúng đối tượng (mỗi pixel vài chục mm thì hay bắt nhầm).
        if (Args.GetBoolOrDefault(arguments, "zoom", true)) VirtualHand.ZoomTo(uidoc, uiView, view, model);
        var points = model.Zip(clicks, (m, c) => VirtualHand.ToScreen(view, uiView, rect, m)
            with { Tab = c.TryGetProperty("tab", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Number ? t.GetInt32() : 0 }).ToList();
        var hidden = points.Select((p, i) => (p, i)).Where(x => !x.p.Visible).Select(x => x.i).ToList();
        if (hidden.Count > 0)
            throw new CommandException($"Điểm số {string.Join(", ", hidden)} nằm ngoài khung view đang hiện. Phóng to/thu nhỏ view (zoom_to) cho thấy hết rồi thử lại.");
        var mode = Args.GetStringOrDefault(arguments, "mode", "message");
        var canvas = VirtualHand.FindCanvas(rect);
        if (canvas == IntPtr.Zero && mode != "input") throw new CommandException("Không tìm thấy cửa sổ vẽ của view; thử mode \"input\".");

        var session = new VirtualHand.Session
        {
            Canvas = canvas,
            Points = points,
            UseInput = mode == "input",
            StartDelay = 700,
            Interval = (int)Args.GetLongOrDefault(arguments, "interval_ms", 350),
            FinishEscapes = (int)Args.GetLongOrDefault(arguments, "finish", 2),
            DialogAnswer = Args.GetStringOrDefault(arguments, "dialog", "none") switch { "ok" => 1, "cancel" => 2, "yes" => 6, "no" => 7, _ => 0 },
        };
        app.PostCommand(command);
        VirtualHand.Start(app, session);
        return new
        {
            posted = command.Name, view = view.Name, clicks = points.Count, mode,
            note = "Đang bấm sau khi Revit chạy lệnh. Gọi revit_ui_result để xem phần tử đã tạo; capture_view để nhìn lại.",
        };
    }
}

public sealed class RevitUiState : RevitCommandBase
{
    public override string Name => "revit_ui_state";
    public override string Category => "Giao diện Revit";
    public override string Description =>
        "Trạng thái giao diện Revit gọn (vài chục token, thay cho chụp màn hình): tài liệu, view đang mở và các view đang mở, " +
        "số phần tử đang chọn theo category, hộp thoại đang hiện.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var uidoc = context.UIDocument;
        var doc = uidoc.Document;
        var selected = uidoc.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null).ToList();
        return new
        {
            document = doc.Title,
            active_view = new { name = doc.ActiveView.Name, type = doc.ActiveView.ViewType.ToString(), scale = doc.ActiveView.Scale },
            open_views = uidoc.GetOpenUIViews().Select(v => (doc.GetElement(v.ViewId) as View)?.Name).Where(n => n != null).ToList(),
            selection = selected.Count == 0 ? null : selected.GroupBy(e => e!.Category?.Name ?? "?").ToDictionary(g => g.Key, g => g.Count()),
            dialogs = Dialogs(),
        };
    }

    // Cửa sổ con đang hiện của Revit (hộp thoại, bảng nổi) ngoài cửa sổ chính.
    private static List<string>? Dialogs()
    {
        var pid = (uint)Environment.ProcessId;
        var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        var titles = new List<string>();
        EnumWindows((h, _) =>
        {
            if (h == main || !IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            var title = sb.ToString().Trim();
            if (title.Length > 0 && !title.EndsWith("Monitor", StringComparison.Ordinal)) titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return titles.Count == 0 ? null : titles;
    }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
}
