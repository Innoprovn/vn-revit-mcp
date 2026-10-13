using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using InnoPro.Bridge;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Điểm vào của lõi, bộ nạp (InnoPro.Revit.Addin.dll) gọi qua reflection: Start khi Revit mở hoặc khi có bản mới
/// (nạp nóng), Stop trước khi đổi sang lõi mới, Shutdown khi tắt Revit. Luôn gọi trong ngữ cảnh API của Revit.
/// </summary>
public static class CoreEntry
{
    public static void Start(UIApplication app, IDictionary<string, object?> host) => App.Start(app, host);

    public static void Stop(UIApplication app) => App.Instance?.Stop(app);

    /// <summary>Không đổi lõi khi còn lệnh đang chờ chạy.</summary>
    public static bool CanStop() => App.Instance?.Busy != true;

    public static void Shutdown() => App.Instance?.Bridge.Stop();

    /// <summary>
    /// Bộ nạp giữ sự kiện Idling và gọi vào đây mỗi lượt rảnh: lõi được chạy ngay trong Idling (lúc nạp nóng),
    /// mà Revit cấm đăng ký Idling trong chính sự kiện Idling.
    /// </summary>
    public static void Idle(UIApplication app) => App.Instance?.OnIdle(app);
}

internal sealed class App
{
    internal static App? Instance { get; private set; }

    private volatile CommandRegistry _registry = null!;
    private readonly object _reloadGate = new();
    private RevitDispatcher _dispatcher = null!;

    /// <summary>Danh sách lệnh hiện hành; được thay nguyên khối khi nạp lại.</summary>
    internal CommandRegistry Registry => _registry;
    internal PipeBridge Bridge { get; private set; } = null!;
    internal ChatPane ChatPane { get; private set; } = null!;
    internal ChatHost ChatHost { get; private set; } = null!;
    internal string? ActiveDocumentTitle => _activeDocument;
    internal bool Busy => _dispatcher?.HasPending == true;

    internal static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private string _revitVersion = "";
    private volatile string? _activeDocument;
    private int _openDocuments;

    /// <summary>Revit chỉ nhận lệnh API (ExternalEvent) khi đã mở ít nhất một dự án.</summary>
    internal bool HasOpenDocument => Volatile.Read(ref _openDocuments) > 0;

    // Dự phòng: có lúc Revit chậm xử lý ExternalEvent (đang nạp mô hình...); lượt rảnh chạy hàng đợi.
    internal void OnIdle(UIApplication uiApp)
    {
        if (_dispatcher.HasPending) _dispatcher.Execute(uiApp);
    }

    private void OnDocumentOpened(object? sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e) => Interlocked.Increment(ref _openDocuments);
    private void OnDocumentCreated(object? sender, Autodesk.Revit.DB.Events.DocumentCreatedEventArgs e) => Interlocked.Increment(ref _openDocuments);
    private void OnDocumentClosed(object? sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e) => Interlocked.Decrement(ref _openDocuments);

    internal static void Start(UIApplication application, IDictionary<string, object?> host)
    {
        if (host.TryGetValue("source_dir", out var dir) && dir is string source) Paths.AddinDir = source;
        var reason = host.TryGetValue("reason", out var r) ? r as string : "startup";
        var app = new App();
        Instance = app;
        app._revitVersion = $"{application.Application.VersionName} ({application.Application.VersionBuild})";
        Log.Info($"Khởi động lõi vn-revit-mcp {Version} trên {app._revitVersion} ({reason})");

        // File .cs được biên dịch có đệm theo nội dung nên lần mở sau gần như tức thì.
        app._registry = CommandRegistry.Load(Paths.CommandSetRoots, Paths.ScriptRoots);
        app.WriteManifest();

        app._dispatcher = new RevitDispatcher();
        app.Bridge = new PipeBridge(() => app._registry, app.ReloadCommands, app._dispatcher, app.Status, () => app.HasOpenDocument);

        app.ChatHost = new ChatHost(app, app._dispatcher);
        app.ChatPane = new ChatPane((System.Windows.Controls.ContentControl)host["pane_root"]!);
        // Nạp nóng: khung chat đang mở thì nạp lại module chat của bản mới.
        if (reason != "startup") app.ChatPane.ReloadIfShown(app.ChatHost);

        ButtonRunner.Register();
        RibbonManager.Initialize(application, app._registry);

        app._openDocuments = application.Application.Documents.Cast<Autodesk.Revit.DB.Document>().Count(d => !d.IsLinked);
        app._activeDocument = application.ActiveUIDocument?.Document.Title;
        application.ViewActivated += app.OnViewActivated;
        application.Application.FailuresProcessing += CommandRunner.OnFailuresProcessing;
        application.Application.DocumentOpened += app.OnDocumentOpened;
        application.Application.DocumentCreated += app.OnDocumentCreated;
        application.Application.DocumentClosed += app.OnDocumentClosed;

        // Cầu nối bật sau cùng: lõi khởi động lỗi giữa chừng thì không để lại cầu nối chạy song song với lõi cũ.
        app.Bridge.Start();
        if (reason == "startup") StartAutoUpdate();
    }

    /// <summary>Dừng lõi trước khi bộ nạp chạy lõi mới: gỡ sự kiện, tắt cầu nối, giải phóng lệnh.</summary>
    internal void Stop(UIApplication application)
    {
        application.ViewActivated -= OnViewActivated;
        application.Application.FailuresProcessing -= CommandRunner.OnFailuresProcessing;
        application.Application.DocumentOpened -= OnDocumentOpened;
        application.Application.DocumentCreated -= OnDocumentCreated;
        application.Application.DocumentClosed -= OnDocumentClosed;
        Bridge.Stop();
        _dispatcher.Dispose();
        _registry.Unload();
        if (Instance == this) Instance = null;
        Log.Info($"Đã dừng lõi {Version}.");
    }

    /// <summary>
    /// Người dùng chỉ mở Revit (không mở AI) vẫn được cập nhật: gọi "vn-revit-mcp.exe update --auto" chạy ẩn.
    /// Server tự giãn cách 12 giờ/lần và tôn trọng VN_REVIT_MCP_AUTO_UPDATE=0; cài xong bộ nạp tự chuyển sang bản mới khi Revit rảnh.
    /// </summary>
    private static void StartAutoUpdate()
    {
        try
        {
            var exe = Path.GetFullPath(Path.Combine(Paths.AddinDir, "..", "server", "vn-revit-mcp.exe"));
            if (!File.Exists(exe)) return; // bản build của lập trình viên
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "update --auto")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
            })?.Dispose();
        }
        catch (Exception ex) { Log.Error("Không chạy được tự cập nhật", ex); }
    }

    // Cầu nối chạy ngoài luồng Revit nên không đọc ActiveUIDocument được; ghi nhận tên tài liệu khi đổi view.
    private void OnViewActivated(object? sender, ViewActivatedEventArgs e) => _activeDocument = e.Document?.Title;

    private RevitStatus Status() =>
        new(Environment.ProcessId, _revitVersion, _activeDocument, Version, Registry.Commands.Count,
            Registry.Errors.Count == 0 ? null : Registry.Errors);

    /// <summary>
    /// Nạp lại toàn bộ lệnh (CommandSet và file .cs) từ đĩa mà không cần tắt Revit,
    /// rồi bổ sung nút ribbon trên luồng chính của Revit.
    /// </summary>
    internal ReloadResult ReloadCommands()
    {
        CommandRegistry fresh;
        lock (_reloadGate)
        {
            fresh = CommandRegistry.Load(Paths.CommandSetRoots, Paths.ScriptRoots);
            var old = _registry;
            _registry = fresh;
            old.Unload();
            WriteManifest();
        }

        _dispatcher.InvokeAsync(app =>
        {
            RibbonManager.Sync(app, _registry);
            return "";
        }, CancellationToken.None);

        return new ReloadResult(fresh.Commands.Count, fresh.Errors);
    }

    /// <summary>Ghi danh mục lệnh để MCP server vẫn liệt kê được tool khi Revit chưa mở.</summary>
    private void WriteManifest()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var json = JsonSerializer.Serialize(PipeBridge.DescribeCommands(Registry),
                new JsonSerializerOptions(BridgeProtocol.Json) { WriteIndented = true });
            File.WriteAllText(Paths.ManifestFile, json, BridgeProtocol.Encoding);
        }
        catch (Exception ex)
        {
            Log.Error("Không ghi được danh mục lệnh", ex);
        }
    }
}
