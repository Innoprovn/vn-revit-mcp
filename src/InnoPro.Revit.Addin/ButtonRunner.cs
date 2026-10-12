using System.Text.Json;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;
using InnoPro.Ui;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Chạy lệnh khi người dùng bấm nút trên ribbon: hỏi tham số, chạy, hiện kết quả.
/// Các lớp nút sinh lúc chạy (RibbonStubs) gọi vào đây qua một delegate đặt trong AppDomain,
/// nên không phụ thuộc việc Revit nạp DLL nút vào context nào.
/// </summary>
internal static class ButtonRunner
{
    public const string DataKey = "InnoPro.VnRevitMcp.RunButton";

    public static void Register() =>
        AppDomain.CurrentDomain.SetData(DataKey, new Func<string, ExternalCommandData, Result>(Run));

    private static Result Run(string name, ExternalCommandData data)
    {
        var app = App.Instance;
        if (app == null || !app.Registry.TryGet(name, out var loaded))
        {
            InnoWindow.Message("InnoPro", $"Lệnh {name} không còn. Bấm Nạp lại lệnh để cập nhật.", data.Application.MainWindowHandle);
            return Result.Cancelled;
        }

        var command = loaded.Command;
        var uiApp = data.Application;
        var context = new RevitCommandContext(uiApp, CancellationToken.None, new CommandUi(uiApp), isInteractive: true);
        try
        {
            JsonElement args;
            if (command.PromptArguments(context) is { } custom)
                args = custom is JsonElement element ? element : JsonSerializer.SerializeToElement(custom, BridgeProtocol.Json);
            else if (ArgumentPrompter.Prompt(context, command) is { } prompted)
                args = prompted;
            else
                return Result.Cancelled;

            Log.Info($"Nút: chạy lệnh {name}");
            var result = CommandRunner.Execute(context, command, args);
            ResultWindow.Show(uiApp, Labels.CommandTitle(command), JsonSerializer.SerializeToElement(result, BridgeProtocol.Json));
            return Result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (CommandException ex)
        {
            InnoWindow.Message(Labels.CommandTitle(command), ex.Message, uiApp.MainWindowHandle);
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error($"Nút {name} lỗi", ex);
            InnoWindow.Message(Labels.CommandTitle(command), $"Lỗi: {ex.Message}\n\nLog: {Paths.LogDir}", uiApp.MainWindowHandle);
            return Result.Cancelled;
        }
    }
}
