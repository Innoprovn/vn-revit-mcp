using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using InnoPro.Bridge;

namespace InnoPro.Revit.Addin;

public sealed class App : IExternalApplication
{
    internal static App? Instance { get; private set; }

    private volatile CommandRegistry _registry = null!;
    private readonly object _reloadGate = new();
    private RevitDispatcher _dispatcher = null!;

    /// <summary>Danh sách lệnh hiện hành; được thay nguyên khối khi nạp lại.</summary>
    internal CommandRegistry Registry => _registry;
    internal PipeBridge Bridge { get; private set; } = null!;

    internal static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private string _revitVersion = "";
    private volatile string? _activeDocument;

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;
        try
        {
            _revitVersion = $"{application.ControlledApplication.VersionName} ({application.ControlledApplication.VersionBuild})";
            Log.Info($"Khởi động vn-revit-mcp {Version} trên {_revitVersion}");

            // CommandSet đã build nạp ngay (nhanh). File .cs cần biên dịch thì nạp nền để không làm chậm Revit khởi động.
            _registry = CommandRegistry.Load(Paths.CommandSetRoots, []);
            WriteManifest();

            _dispatcher = new RevitDispatcher();
            Bridge = new PipeBridge(() => _registry, ReloadCommands, _dispatcher, Status);
            Bridge.Start();

            ButtonRunner.Register();
            RibbonManager.Initialize(application, _registry);
            application.ViewActivated += OnViewActivated;

            if (CommandRegistry.HasScripts(Paths.ScriptRoots))
                Task.Run(ReloadCommands);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error("Khởi động thất bại", ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ViewActivated -= OnViewActivated;
        Bridge?.Stop();
        return Result.Succeeded;
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
