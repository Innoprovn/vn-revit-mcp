using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Chạy một lệnh trên luồng chính của Revit, bọc Transaction theo TransactionMode.
/// Dùng chung cho AI (qua cầu nối) và người dùng (bấm nút).
/// </summary>
internal static class CommandRunner
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    /// <summary>Đường AI: serialize kết quả ngay trên luồng Revit (lệnh được phép trả LINQ lười).</summary>
    public static string RunForBridge(UIApplication app, LoadedCommand loaded, JsonElement? arguments, CancellationToken token)
    {
        var context = new RevitCommandContext(app, token, new CommandUi(app), isInteractive: false);
        return JsonSerializer.Serialize(Execute(context, loaded.Command, arguments ?? EmptyArgs), BridgeProtocol.Json);
    }

    public static object? Execute(RevitCommandContext context, IRevitCommand command, JsonElement args)
    {
        if (command.TransactionMode != Sdk.TransactionMode.Auto)
            return command.Execute(context, args);

        using var transaction = new Transaction(context.Document, $"InnoPro: {command.Name}");
        TransactionHelper.SuppressWarnings(transaction);
        transaction.Start();
        try
        {
            var result = command.Execute(context, args);
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new CommandException("Revit không chấp nhận thay đổi, đã hủy toàn bộ lệnh.");
            return result;
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
                transaction.RollBack();
            throw;
        }
    }
}
