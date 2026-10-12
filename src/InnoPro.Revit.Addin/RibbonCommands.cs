using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Ui;

namespace InnoPro.Revit.Addin;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ToggleBridgeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var bridge = App.Instance?.Bridge;
        if (bridge == null) return Result.Failed;

        if (bridge.IsRunning) bridge.Stop();
        else bridge.Start();

        InnoWindow.Message("Kết nối MCP", bridge.IsRunning ? "Đã bật. AI trên máy này điều khiển được Revit." : "Đã tắt.",
            commandData.Application.MainWindowHandle);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ReloadCommandsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = App.Instance;
        if (app == null) return Result.Failed;

        var result = app.ReloadCommands();
        var text = $"{result.CommandCount} lệnh" + (result.Errors.Count > 0 ? $", {result.Errors.Count} lỗi:\n\n" + Errors(result.Errors) : ".");
        InnoWindow.Message("Nạp lại lệnh", text, commandData.Application.MainWindowHandle);
        return Result.Succeeded;
    }

    internal static string Errors(IReadOnlyList<LoadError> errors) =>
        string.Join("\n", errors.Select(e => $"• {e.Source}: {e.Message}"));
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowCommandsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = App.Instance;
        if (app == null) return Result.Failed;

        var text = new StringBuilder();
        text.AppendLine($"vn-revit-mcp {App.Version} · MCP {(app.Bridge.IsRunning ? "bật" : "tắt")} · {app.Registry.Commands.Count} lệnh");
        foreach (var group in app.Registry.Commands.GroupBy(c => c.Command.Category).OrderBy(g => g.Key))
        {
            text.AppendLine().AppendLine(group.Key);
            foreach (var c in group.OrderBy(c => c.Command.Name))
                text.AppendLine($"   {Labels.CommandTitle(c.Command)}  ·  {c.Command.Name}");
        }
        if (app.Registry.Errors.Count > 0)
            text.AppendLine().AppendLine("Lỗi nạp lệnh").AppendLine(ReloadCommandsCommand.Errors(app.Registry.Errors));

        InnoWindow.Message("Lệnh", text.ToString(), commandData.Application.MainWindowHandle);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowChatCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = App.Instance;
        if (app == null) return Result.Failed;

        app.ChatPane.EnsureLoaded(app.ChatHost);
        commandData.Application.GetDockablePane(ChatPane.Id).Show();
        return Result.Succeeded;
    }
}
