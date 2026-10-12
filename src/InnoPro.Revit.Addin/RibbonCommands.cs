using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;

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

        TaskDialog.Show("InnoPro MCP", bridge.IsRunning
            ? "Đã BẬT kết nối AI. Các ứng dụng AI trên máy này (cùng tài khoản Windows) có thể điều khiển Revit."
            : "Đã TẮT kết nối AI.");
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
        var dialog = new TaskDialog("InnoPro MCP")
        {
            MainInstruction = $"Đã nạp lại: {result.CommandCount} lệnh" + (result.Errors.Count > 0 ? $", {result.Errors.Count} lỗi" : ""),
            MainContent = $"Thả file lệnh .cs vào:\n{Paths.UserScriptsDir}",
            ExpandedContent = Errors(result.Errors),
        };
        dialog.Show();
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
        foreach (var group in app.Registry.Commands.GroupBy(c => c.Command.Category).OrderBy(g => g.Key))
        {
            text.AppendLine($"[{group.Key}]");
            foreach (var c in group.OrderBy(c => c.Command.Name))
                text.AppendLine($"  • {c.Command.Name}  ({c.CommandSet})");
        }
        if (app.Registry.Errors.Count > 0)
            text.AppendLine().AppendLine("[Lỗi nạp lệnh]").AppendLine(ReloadCommandsCommand.Errors(app.Registry.Errors));

        var dialog = new TaskDialog("InnoPro MCP")
        {
            MainInstruction = $"vn-revit-mcp {App.Version}: {(app.Bridge.IsRunning ? "đang BẬT" : "đang TẮT")}",
            MainContent = $"{app.Registry.Commands.Count} lệnh" +
                          (app.Registry.Errors.Count > 0 ? $", {app.Registry.Errors.Count} lỗi nạp" : "") +
                          $"\nThư mục lệnh .cs: {Paths.UserScriptsDir}",
            ExpandedContent = text.ToString(),
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
