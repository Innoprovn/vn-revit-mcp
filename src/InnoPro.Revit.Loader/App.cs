using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Bộ nạp: phần duy nhất Revit nạp trực tiếp, rất nhỏ và hầu như không đổi. Toàn bộ add-in thật (cầu nối, ribbon, chat,
/// bộ chạy lệnh, SDK, Roslyn) nằm trong InnoPro.Revit.Core, được nạp từ một bản sao vào AssemblyLoadContext riêng.
///
/// Nạp nóng: khi bản cài đổi (bộ cập nhật trỏ junction "current" sang bản mới, hoặc lập trình viên build lại),
/// bộ nạp đợi lúc Revit rảnh rồi dừng lõi cũ và chạy lõi mới, không cần tắt Revit. Ribbon giữ nguyên, nút mới được thêm.
/// Lõi mới lỗi thì chạy lại lõi cũ.
/// </summary>
public sealed class App : IExternalApplication, IExternalEventHandler
{
    private const string CoreFile = "InnoPro.Revit.Core.dll";
    private const string EntryType = "InnoPro.Revit.Addin.CoreEntry";
    public const string ReloadKey = "InnoPro.VnRevitMcp.RequestCoreReload";

    private static readonly Guid PaneId = new("6E0B5F4A-3C2D-4E8B-9A71-2F5D8C4B1E63");

    private readonly ContentControl _paneRoot = new()
    {
        Background = System.Windows.Media.Brushes.White,
        Content = new TextBlock { Text = "Bấm nút Chat AI trên tab InnoPro để bắt đầu.", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap },
    };

    private readonly string _sourceDir = Path.GetDirectoryName(typeof(App).Assembly.Location)!;
    private Type? _core;
    private Action<UIApplication>? _idle;
    private string? _stamp;
    private volatile bool _requested = true;
    private bool _failed;
    private ExternalEvent? _event;
    private Timer? _watch;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.RegisterDockablePane(new DockablePaneId(PaneId), "InnoPro AI", new PaneProvider(_paneRoot));
            application.Idling += OnIdling;
            _event = ExternalEvent.Create(this);
            AppDomain.CurrentDomain.SetData(ReloadKey, new Action(() => { _requested = true; _event?.Raise(); }));
            CleanShadowCopies();

            // Chạy lõi ngay lúc khởi động để ribbon có sẵn; UIApplication lấy từ trường nội bộ (cách nhiều add-in dùng).
            if (typeof(UIControlledApplication).GetField("m_uiapplication", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(application) is UIApplication ui)
                Process(ui, "startup");

            // Theo dõi bản cài: có bản mới thì xin Revit một lượt rảnh để đổi lõi.
            _watch = new Timer(_ =>
            {
                if (_core != null && Stamp() is { } stamp && stamp != _stamp) { _requested = true; _event?.Raise(); }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log("Khởi động thất bại: " + ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _watch?.Dispose();
        application.Idling -= OnIdling;
        try { _core?.GetMethod("Shutdown")?.Invoke(null, null); } catch (Exception ex) { Log("Tắt lõi lỗi: " + ex.Message); }
        return Result.Succeeded;
    }

    // Revit chỉ chạy ExternalEvent khi đã mở dự án; Idling chạy cả ở màn hình bắt đầu.
    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication ui) return;
        if (_requested) Process(ui, _core == null ? "startup" : "update");
        // Lõi không tự đăng ký Idling (Revit cấm đăng ký trong lúc đang Idling, mà lõi mới được chạy ngay trong Idling).
        try { _idle?.Invoke(ui); }
        catch (Exception ex) { Log("Lõi lỗi khi rảnh: " + ex.Message); }
    }

    public void Execute(UIApplication app)
    {
        if (_requested) Process(app, _core == null ? "startup" : "update");
    }

    public string GetName() => "InnoPro vn-revit-mcp loader";

    private void Process(UIApplication ui, string reason)
    {
        _requested = false;
        var stamp = Stamp();
        if (stamp == null) { _requested = _core == null; return; } // chưa có hoặc đang ghi dở: thử lại lượt rảnh sau
        if (stamp == _stamp && _core != null) return;
        if (_failed && stamp == _stamp) return;
        // Đang có việc trong hàng đợi của lõi: để lượt rảnh sau.
        if (_core != null && _core.GetMethod("CanStop")?.Invoke(null, null) is false) { _requested = true; return; }

        Type entry;
        try { entry = LoadCore(); }
        catch (Exception ex)
        {
            Log("Không nạp được lõi mới: " + ex);
            _stamp = stamp;
            _failed = true;
            return;
        }

        var previous = _core;
        _idle = null;
        if (previous != null)
        {
            try { Invoke(previous, "Stop", ui); }
            catch (Exception ex) { Log("Dừng lõi cũ lỗi: " + ex); }
        }
        try
        {
            Invoke(entry, "Start", ui, Host(reason));
            _core = entry;
            _idle = entry.GetMethod("Idle")?.CreateDelegate<Action<UIApplication>>();
            _stamp = stamp;
            _failed = false;
            Log($"Đã chạy lõi {entry.Assembly.GetName().Version} ({reason}).");
        }
        catch (Exception ex)
        {
            Log("Lõi mới lỗi khi chạy, dùng lại lõi cũ: " + ex);
            _stamp = stamp;
            _failed = true;
            if (previous != null)
            {
                try
                {
                    Invoke(previous, "Start", ui, Host("rollback"));
                    _idle = previous.GetMethod("Idle")?.CreateDelegate<Action<UIApplication>>();
                }
                catch (Exception again) { Log("Không chạy lại được lõi cũ: " + again); }
            }
        }
    }

    private Dictionary<string, object?> Host(string reason) => new()
    {
        ["source_dir"] = _sourceDir,
        ["pane_root"] = _paneRoot,
        ["pane_id"] = PaneId,
        ["reason"] = reason,
        ["loader_version"] = typeof(App).Assembly.GetName().Version?.ToString(3),
    };

    private static void Invoke(Type entry, string method, params object?[] args)
    {
        try { entry.GetMethod(method)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }

    /// <summary>
    /// Chép lõi sang thư mục riêng rồi nạp từ đó: file bản cài không bị khóa, cập nhật/build lại thoải mái khi Revit đang mở.
    /// Mỗi lần đổi lõi là một ngữ cảnh mới (ngữ cảnh cũ ở lại trong bộ nhớ tới khi tắt Revit, vài chục MB).
    /// </summary>
    private Type LoadCore()
    {
        var shadow = Path.Combine(DataDir, "cache", "core", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(shadow);
        var self = Path.GetFileName(typeof(App).Assembly.Location);
        foreach (var file in Directory.EnumerateFiles(_sourceDir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(self, StringComparison.OrdinalIgnoreCase) || name.Equals(Path.ChangeExtension(self, ".pdb"), StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(shadow, name), true);
        }
        var context = new CoreLoadContext(shadow);
        return context.LoadFromAssemblyPath(Path.Combine(shadow, CoreFile)).GetType(EntryType, throwOnError: true)!;
    }

    /// <summary>Dấu phiên bản lõi: thời điểm ghi + kích thước. Junction đổi bản hoặc build lại đều làm dấu đổi.</summary>
    private string? Stamp()
    {
        var file = new FileInfo(Path.Combine(_sourceDir, CoreFile));
        if (!file.Exists) return null;
        file.Refresh();
        // Đang ghi dở (build/cài đặt): đợi ổn định.
        if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(3)) return _core == null ? null : _stamp;
        return $"{file.LastWriteTimeUtc.Ticks}:{file.Length}";
    }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp");

    /// <summary>Xóa bản sao lõi của các lần chạy trước (bản đang được Revit khác dùng thì bị khóa, bỏ qua).</summary>
    private static void CleanShadowCopies()
    {
        var root = Path.Combine(DataDir, "cache", "core");
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try { Directory.Delete(dir, true); }
            catch (Exception) { /* đang dùng */ }
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"loader-{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    private sealed class PaneProvider(FrameworkElement root) : IDockablePaneProvider
    {
        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = root;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Tabbed, TabBehind = DockablePanes.BuiltInDockablePanes.PropertiesPalette };
        }
    }

    /// <summary>Thư viện của lõi lấy từ thư mục bản sao; Revit API, WPF, .NET lấy từ Revit.</summary>
    private sealed class CoreLoadContext(string dir) : AssemblyLoadContext("InnoPro.Core", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var file = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(file) : null;
        }
    }
}
