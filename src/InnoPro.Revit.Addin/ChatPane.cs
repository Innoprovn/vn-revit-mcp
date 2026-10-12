using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Khung "InnoPro AI" gắn cạnh Revit. Nội dung là module chat (Modules\Chat) nạp trong AssemblyLoadContext riêng
/// khi người dùng mở lần đầu, nên Revit khởi động không chậm và thư viện của module không xung đột add-in khác.
/// </summary>
internal sealed class ChatPane : IDockablePaneProvider
{
    public static readonly DockablePaneId Id = new(new Guid("6E0B5F4A-3C2D-4E8B-9A71-2F5D8C4B1E63"));

    private readonly ContentControl _root = new()
    {
        Content = new TextBlock { Text = "Bấm nút Chat AI trên tab InnoPro để bắt đầu.", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap },
    };
    private bool _loaded;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _root;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Tabbed, TabBehind = DockablePanes.BuiltInDockablePanes.PropertiesPalette };
    }

    public void EnsureLoaded(IRevitHost host)
    {
        if (_loaded) return;
        var dir = Path.Combine(Paths.AddinDir, "Modules", "Chat");
        var dll = Path.Combine(dir, "InnoPro.Revit.Chat.dll");
        try
        {
            if (!File.Exists(dll)) throw new FileNotFoundException("Chưa cài module chat.", dll);
            var context = new PluginLoadContext("Chat", dir, collectible: false);
            var create = context.LoadFromFileBytes(dll).GetType("InnoPro.Revit.Chat.ChatModule")!.GetMethod("CreateView")!;
            _root.Content = (FrameworkElement)create.Invoke(null, [host])!;
            _loaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("Không nạp được module chat", ex);
            _root.Content = new TextBlock { Text = $"Không nạp được module chat: {(ex.InnerException ?? ex).Message}", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        }
    }
}

/// <summary>Cài đặt IRevitHost cho module: danh mục lệnh và chạy lệnh qua cùng đường với AI (MCP).</summary>
internal sealed class ChatHost(App app, RevitDispatcher dispatcher) : IRevitHost
{
    public string ListCommandsJson() => JsonSerializer.Serialize(PipeBridge.DescribeCommands(app.Registry), BridgeProtocol.Json);

    public async Task<string> InvokeAsync(string name, string? argumentsJson, CancellationToken token)
    {
        if (!app.Registry.TryGet(name, out var loaded))
            throw new InvalidOperationException($"Không có lệnh '{name}'. Dùng find_commands để tra tên đúng.");
        JsonElement? args = string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonDocument.Parse(argumentsJson).RootElement;

        try
        {
            return await dispatcher.InvokeAsync(uiApp => CommandRunner.RunForBridge(uiApp, loaded, args, token), token);
        }
        catch (CommandException ex) { throw new InvalidOperationException(ex.Message); }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { throw new InvalidOperationException($"Revit báo lỗi: {ex.Message}"); }
    }

    public string? ActiveDocument => app.ActiveDocumentTitle;

    public string DataDir => Paths.DataDir;

    public string Version => App.Version;
}
