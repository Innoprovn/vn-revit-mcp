using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.RevitUi;

/// <summary>
/// "Bàn tay ảo": sau khi kích hoạt lệnh có sẵn của Revit (PostCommand), bấm vào đúng các điểm mô hình trên view như người dùng,
/// rồi kết thúc lệnh bằng Esc. Điểm mô hình đổi sang pixel theo khung view đang hiện (UIView.GetZoomCorners + GetWindowRectangle).
/// Mặc định gửi thông điệp chuột thẳng vào cửa sổ view (không chiếm chuột thật của người dùng); mode "input" dùng chuột thật.
/// Theo dõi DocumentChanged và DialogBoxShowing để revit_ui_result báo lại phần tử đã tạo/sửa/xóa và hộp thoại đã hiện.
/// </summary>
internal static class VirtualHand
{
    internal sealed record ScreenPoint(int X, int Y, bool Visible, int Tab = 0);

    internal sealed class Session
    {
        public required IntPtr Canvas;
        public required List<ScreenPoint> Points;
        public required bool UseInput;
        public required int StartDelay;
        public required int Interval;
        public required int FinishEscapes;
        public required int DialogAnswer;
        public readonly List<string> Dialogs = [];
        public readonly HashSet<long> Added = [];
        public readonly HashSet<long> Modified = [];
        public readonly HashSet<long> Deleted = [];
        public volatile bool Done;
        public volatile string? Error;
        public DateTime Started = DateTime.Now;
    }

    private static Session? _current;
    private static UIApplication? _subscribed;

    public static Session? Current => _current;

    public static void Start(UIApplication app, Session session)
    {
        Stop(app);
        _current = session;
        app.DialogBoxShowing += OnDialog;
        app.Application.DocumentChanged += OnChanged;
        _subscribed = app;
        new Thread(() => Run(session)) { IsBackground = true, Name = "vn-revit-mcp hand" }.Start();
    }

    /// <summary>Gỡ theo dõi sự kiện (gọi trong ngữ cảnh API).</summary>
    public static void Stop(UIApplication app)
    {
        var target = _subscribed ?? app;
        target.DialogBoxShowing -= OnDialog;
        target.Application.DocumentChanged -= OnChanged;
        _subscribed = null;
    }

    private static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
    {
        if (_current is not { } s) return;
        var text = e switch
        {
            TaskDialogShowingEventArgs t => $"{e.DialogId}: {t.Message}",
            MessageBoxShowingEventArgs m => $"{e.DialogId}: {m.Message}",
            _ => e.DialogId,
        };
        lock (s.Dialogs) s.Dialogs.Add(text.Length > 300 ? text[..299] + "…" : text);
        if (s.DialogAnswer != 0) e.OverrideResult(s.DialogAnswer);
    }

    private static void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_current is not { } s) return;
        lock (s.Added)
        {
            foreach (var id in e.GetAddedElementIds()) s.Added.Add(id.Value);
            foreach (var id in e.GetModifiedElementIds()) s.Modified.Add(id.Value);
            foreach (var id in e.GetDeletedElementIds()) { s.Deleted.Add(id.Value); s.Added.Remove(id.Value); }
        }
    }

    // ---- Đổi điểm mô hình sang pixel màn hình ----

    public static (UIView View, Autodesk.Revit.DB.Rectangle Rect) ActiveUiView(UIDocument uidoc)
    {
        var view = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id)
                   ?? throw new CommandException("Không thấy cửa sổ của view đang mở.");
        return (view, view.GetWindowRectangle());
    }

    /// <summary>Phóng view để các điểm bấm nằm giữa màn hình, chừa lề; không phóng quá mức 1 pixel ≈ 10 mm.</summary>
    public static void ZoomTo(UIDocument uidoc, UIView uiView, View view, IReadOnlyList<XYZ> points)
    {
        var right = view.RightDirection;
        var up = view.UpDirection;
        var us = points.Select(p => p.DotProduct(right)).ToList();
        var vs = points.Select(p => p.DotProduct(up)).ToList();
        var rect = uiView.GetWindowRectangle();
        var pixels = Math.Max(200, Math.Min(rect.Right - rect.Left, rect.Bottom - rect.Top));
        var span = Math.Max(Math.Max(us.Max() - us.Min(), vs.Max() - vs.Min()) * 1.6, UnitConvert.MmToFeet(10) * pixels);
        double cu = (us.Max() + us.Min()) / 2, cv = (vs.Max() + vs.Min()) / 2;
        // Giữ thành phần theo hướng nhìn của điểm đầu để góc nằm trên mặt phẳng view.
        var depth = view.ViewDirection * points[0].DotProduct(view.ViewDirection);
        var min = depth + right * (cu - span / 2) + up * (cv - span / 2);
        var max = depth + right * (cu + span / 2) + up * (cv + span / 2);
        uiView.ZoomAndCenterRectangle(min, max);
        uidoc.RefreshActiveView();
    }

    public static ScreenPoint ToScreen(View view, UIView uiView, Autodesk.Revit.DB.Rectangle rect, XYZ point)
    {
        var corners = uiView.GetZoomCorners();
        var right = view.RightDirection;
        var up = view.UpDirection;
        double u0 = corners[0].DotProduct(right), u1 = corners[1].DotProduct(right);
        double v0 = corners[0].DotProduct(up), v1 = corners[1].DotProduct(up);
        var fx = (point.DotProduct(right) - u0) / (u1 - u0);
        var fy = (point.DotProduct(up) - v0) / (v1 - v0);
        var x = (int)Math.Round(rect.Left + fx * (rect.Right - rect.Left));
        var y = (int)Math.Round(rect.Bottom - fy * (rect.Bottom - rect.Top));
        return new ScreenPoint(x, y, fx is > 0.01 and < 0.99 && fy is > 0.01 and < 0.99);
    }

    /// <summary>Điểm {x,y,z} mm hoặc {element_id, at: clear|center|start|end}.</summary>
    public static XYZ ModelPoint(Document doc, JsonElement spec)
    {
        if (spec.TryGetProperty("element_id", out var idValue))
        {
            var element = doc.GetElement(new ElementId(idValue.GetInt64())) ?? throw new CommandException($"Không có phần tử {idValue}.");
            var at = spec.TryGetProperty("at", out var a) ? a.GetString() : "clear";
            if (element.Location is LocationCurve lc)
                return at switch
                {
                    "start" => lc.Curve.GetEndPoint(0),
                    "end" => lc.Curve.GetEndPoint(1),
                    "center" => lc.Curve.Evaluate(0.5, true),
                    _ => ClearPoint(doc, element, lc.Curve),
                };
            if (element.Location is LocationPoint lp) return lp.Point;
            var box = element.get_BoundingBox(doc.ActiveView) ?? element.get_BoundingBox(null)
                      ?? throw new CommandException($"Phần tử {idValue} không có vị trí để bấm.");
            return (box.Min + box.Max) / 2;
        }
        return new XYZ(
            UnitConvert.MmToFeet(spec.GetProperty("x").GetDouble()),
            UnitConvert.MmToFeet(spec.GetProperty("y").GetDouble()),
            UnitConvert.MmToFeet(spec.TryGetProperty("z", out var z) ? z.GetDouble() : 0));
    }

    /// <summary>
    /// Điểm "thoáng" trên phần tử dạng đường (tường, dầm...): dò dọc đường, chọn chỗ xa nhất các phần tử khác
    /// (cửa, lỗ mở, tường giao, thanh đố, trục cắt ngang...) để Revit bắt đúng phần tử này, như người dùng chọn chỗ dễ bấm.
    /// Bỏ qua phần tử phủ rộng (sàn, phòng, mái) và phần tử chạy dọc suốt phần tử này (trục trùng tường): không tránh được.
    /// </summary>
    private static XYZ ClearPoint(Document doc, Element target, Curve curve)
    {
        var view = doc.ActiveView;
        var wide = UnitConvert.MmToFeet(3000);
        var tolerance = UnitConvert.MmToFeet(50);
        XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
        bool Covers(BoundingBoxXYZ box, XYZ p) =>
            p.X > box.Min.X - tolerance && p.X < box.Max.X + tolerance && p.Y > box.Min.Y - tolerance && p.Y < box.Max.Y + tolerance;
        var boxes = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
            .Where(e => e.Id != target.Id && e.Category != null)
            .Select(e => e.get_BoundingBox(view) ?? e.get_BoundingBox(null)) // trục, lỗ mở: không có khung theo view
            .ToList();
        // Cửa, cửa sổ, lỗ mở gắn vào chính phần tử (lỗ mở thường không có trong danh sách phần tử của view).
        if (target is HostObject host)
            boxes.AddRange(host.FindInserts(true, true, true, true).Select(doc.GetElement).Select(e => e?.get_BoundingBox(null)));
        var obstacles = boxes
            .Where(box => box != null && !(box.Max.X - box.Min.X > wide && box.Max.Y - box.Min.Y > wide) && !(Covers(box, a) && Covers(box, b)))
            .Select(box => box!)
            .ToList();

        // Khoảng cách (mặt bằng) từ điểm tới khung; âm khi nằm trong khung.
        static double Distance(BoundingBoxXYZ box, XYZ p)
        {
            var dx = Math.Max(box.Min.X - p.X, p.X - box.Max.X);
            var dy = Math.Max(box.Min.Y - p.Y, p.Y - box.Max.Y);
            return dx > 0 && dy > 0 ? Math.Sqrt(dx * dx + dy * dy) : Math.Max(dx, dy);
        }

        var best = curve.Evaluate(0.5, true);
        var bestScore = double.MinValue;
        var cap = UnitConvert.MmToFeet(1000); // thoáng hơn 1 m là đủ, khi đó ưu tiên gần giữa
        for (var i = 1; i < 40; i++)
        {
            var t = i / 40.0;
            var p = curve.Evaluate(t, true);
            var clearance = obstacles.Count == 0 ? cap : Math.Min(cap, obstacles.Min(box => Distance(box, p)));
            var score = clearance - Math.Abs(t - 0.5) * UnitConvert.MmToFeet(100);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>Cửa sổ vẽ của view: cửa sổ con của Revit trùng khung với UIView (nhỏ nhất nếu lồng nhau).</summary>
    public static IntPtr FindCanvas(Autodesk.Revit.DB.Rectangle rect)
    {
        var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        IntPtr best = IntPtr.Zero;
        long bestArea = long.MaxValue;
        EnumChildWindows(main, (h, _) =>
        {
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
            var same = Math.Abs(r.Left - rect.Left) <= 4 && Math.Abs(r.Top - rect.Top) <= 4
                       && Math.Abs(r.Right - rect.Right) <= 4 && Math.Abs(r.Bottom - rect.Bottom) <= 4;
            var area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (same && area < bestArea) { best = h; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ---- Bấm ----

    private static void Run(Session s)
    {
        try
        {
            Thread.Sleep(s.StartDelay); // Revit chạy lệnh đã post sau khi lượt API hiện tại kết thúc
            POINT saved = default;
            if (s.UseInput)
            {
                GetCursorPos(out saved);
                SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            }
            foreach (var p in s.Points)
            {
                if (s.UseInput)
                {
                    SetCursorPos(p.X, p.Y);
                    Thread.Sleep(80);
                    for (var t = 0; t < p.Tab; t++) { keybd_event(0x09, 0, 0, IntPtr.Zero); keybd_event(0x09, 0, 2, IntPtr.Zero); Thread.Sleep(120); }
                    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // LEFTDOWN
                    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // LEFTUP
                }
                else
                {
                    var client = new POINT { X = p.X, Y = p.Y };
                    ScreenToClient(s.Canvas, ref client);
                    var lParam = (IntPtr)((client.Y << 16) | (client.X & 0xFFFF));
                    PostMessage(s.Canvas, 0x0200, IntPtr.Zero, lParam); // MOUSEMOVE: cho Revit bắt điểm
                    Thread.Sleep(80);
                    PostMessage(s.Canvas, 0x0200, IntPtr.Zero, lParam);
                    Thread.Sleep(40);
                    // Tab: đổi đối tượng được bắt (vd mặt tường thay vì mặt lỗ mở trùng chỗ), như người dùng bấm Tab.
                    for (var t = 0; t < p.Tab; t++)
                    {
                        PostMessage(s.Canvas, 0x0100, (IntPtr)0x09, (IntPtr)0x000F0001);
                        PostMessage(s.Canvas, 0x0101, (IntPtr)0x09, unchecked((IntPtr)0xC00F0001));
                        Thread.Sleep(120);
                    }
                    PostMessage(s.Canvas, 0x0201, (IntPtr)1, lParam);   // LBUTTONDOWN
                    PostMessage(s.Canvas, 0x0202, IntPtr.Zero, lParam); // LBUTTONUP
                }
                Thread.Sleep(s.Interval);
            }
            for (var i = 0; i < s.FinishEscapes; i++)
            {
                if (s.UseInput) { keybd_event(0x1B, 0, 0, IntPtr.Zero); keybd_event(0x1B, 0, 2, IntPtr.Zero); }
                else { PostMessage(s.Canvas, 0x0100, (IntPtr)0x1B, (IntPtr)0x00010001); PostMessage(s.Canvas, 0x0101, (IntPtr)0x1B, unchecked((IntPtr)0xC0010001)); }
                Thread.Sleep(150);
            }
            if (s.UseInput) SetCursorPos(saved.X, saved.Y);
        }
        catch (Exception ex) { s.Error = ex.Message; }
        finally { s.Done = true; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static System.Drawing.Rectangle WindowRect(IntPtr h)
    {
        GetWindowRect(h, out var r);
        return new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static string Describe(IntPtr h)
    {
        GetWindowRect(h, out var r);
        var p = new POINT();
        ScreenToClient(h, ref p);
        return $"{r.Left},{r.Top},{r.Right},{r.Bottom} client0={-p.X},{-p.Y}";
    }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}

public sealed class RevitUiResult : RevitCommandBase
{
    public override string Name => "revit_ui_result";
    public override string Category => "Giao diện Revit";
    public override string Description =>
        "Kết quả lần revit_ui_post có clicks gần nhất: các điểm đã bấm, phần tử Revit đã tạo/sửa/xóa, hộp thoại đã hiện. " +
        "Gọi sau revit_ui_post (lệnh này tự chờ Revit chạy xong lệnh đã kích hoạt).";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var s = VirtualHand.Current ?? throw new CommandException("Chưa có lần bấm nào (gọi revit_ui_post với clicks trước).");
        for (var i = 0; i < 40 && !s.Done; i++) Thread.Sleep(100);
        if (!s.Done) return new { done = false, note = "Bàn tay ảo vẫn đang bấm; gọi lại sau ít giây." };
        VirtualHand.Stop(context.UIApplication);

        var doc = context.Document;
        List<long> added, modified, deleted;
        lock (s.Added) { added = [.. s.Added]; modified = [.. s.Modified.Except(s.Added)]; deleted = [.. s.Deleted]; }
        List<string> dialogs;
        lock (s.Dialogs) dialogs = [.. s.Dialogs];
        var created = added.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null && e.Category != null)
            .Select(e => new
            {
                id = e!.Id.Value, category = e.Category!.Name, name = e.Name,
                // Dim: bám vào phần tử nào, giá trị bao nhiêu, để AI tự kiểm tra (bấm sai thì xóa, thêm tab rồi làm lại).
                detail = e is Dimension d
                    ? $"{(d.Value is { } v ? Math.Round(UnitConvert.FeetToMm(v)) + " mm, " : "")}bám: " + string.Join(", ",
                        d.References.Cast<Reference>().Select(r => $"{r.ElementId.Value} {doc.GetElement(r.ElementId)?.Name}"))
                    : null,
            }).ToList();
        return new
        {
            done = true,
            clicks = s.Points.Count,
            created = created.Count == 0 ? null : Table.Of(created.Take(50).ToList()),
            modified = modified.Count,
            deleted = deleted.Count,
            dialogs = dialogs.Count == 0 ? null : dialogs,
            error = s.Error,
            note = created.Count == 0 && deleted.Count == 0
                ? "Không có phần tử mới. Kiểm tra bằng capture_view; nếu bấm không ăn thử mode \"input\" (dùng chuột thật) hoặc chỉnh điểm."
                : null,
        };
    }
}

public sealed class RevitUiLook : RevitCommandBase
{
    public override string Name => "revit_ui_look";
    public override string Category => "Giao diện Revit";
    public override string Description =>
        "Ảnh view Revit đúng như người dùng đang thấy (mức zoom, chế độ đang vẽ, xem trước), chụp riêng cửa sổ Revit kể cả khi bị che. " +
        "Có marks thì đánh dấu đỏ các điểm mô hình (mm hoặc element_id) để kiểm tra bàn tay ảo sẽ bấm vào đâu. Ảnh gọn ~1000 px.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "marks": { "type": "array", "items": { "type": "object" }, "description": "Điểm cần đánh dấu: {x,y,z} mm hoặc {element_id, at}." },
            "max_px": { "type": "integer", "description": "Cạnh dài tối đa. Mặc định 1000." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var uidoc = context.UIDocument;
        var view = uidoc.ActiveView;
        var (uiView, rect) = VirtualHand.ActiveUiView(uidoc);
        var marks = (Args.Has(arguments, "marks") ? Args.GetArray(arguments, "marks") : [])
            .Select(m => VirtualHand.ToScreen(view, uiView, rect, VirtualHand.ModelPoint(uidoc.Document, m))).ToList();
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) throw new CommandException("Cửa sổ view đang thu nhỏ.");

        var main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        var mainRect = VirtualHand.WindowRect(main);
        using var full = new System.Drawing.Bitmap(mainRect.Width, mainRect.Height);
        using (var g = System.Drawing.Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); }
        }
        var scale = Math.Min(1.0, Args.GetLongOrDefault(arguments, "max_px", 1000) / (double)Math.Max(w, h));
        using var image = new System.Drawing.Bitmap((int)(w * scale), (int)(h * scale));
        using (var g = System.Drawing.Graphics.FromImage(image))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, new System.Drawing.Rectangle(0, 0, image.Width, image.Height),
                new System.Drawing.Rectangle(rect.Left - mainRect.Left, rect.Top - mainRect.Top, w, h), System.Drawing.GraphicsUnit.Pixel);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 2);
            using var font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
            for (var i = 0; i < marks.Count; i++)
            {
                float x = (float)((marks[i].X - rect.Left) * scale), y = (float)((marks[i].Y - rect.Top) * scale);
                g.DrawEllipse(pen, x - 7, y - 7, 14, 14);
                g.DrawString((i + 1).ToString(), font, System.Drawing.Brushes.Red, x + 7, y - 18);
            }
        }
        var folder = Path.Combine(Path.GetTempPath(), "InnoPro", "capture");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"look-{Guid.NewGuid():N}.png");
        image.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        return new { image_file = file, view = view.Name, width = image.Width, height = image.Height, marks = marks.Count };
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
