using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using InnoPro.Bridge;

namespace InnoPro.Revit.Addin;

public sealed class App : IExternalApplication
{
    internal static App? Instance { get; private set; }

    internal CommandRegistry Registry { get; private set; } = null!;
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

            Registry = CommandRegistry.Load(Paths.BundledCommandsDir, Paths.UserCommandsDir);
            WriteManifest();

            Bridge = new PipeBridge(Registry, new RevitDispatcher(), Status);
            Bridge.Start();

            application.ViewActivated += OnViewActivated;
            Ribbon.Build(application);
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
        new(Environment.ProcessId, _revitVersion, _activeDocument, Version, Registry.Commands.Count);

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
