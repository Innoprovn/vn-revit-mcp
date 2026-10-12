using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Chạy một lệnh trên luồng chính của Revit, bọc Transaction theo TransactionMode,
/// rồi serialize kết quả ngay trên luồng đó.
/// </summary>
internal static class CommandRunner
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    public static string Run(UIApplication app, LoadedCommand loaded, JsonElement? arguments, CancellationToken token)
    {
        var command = loaded.Command;
        var context = new RevitCommandContext(app, token);
        var args = arguments ?? EmptyArgs;

        object? result;
        if (command.TransactionMode == Sdk.TransactionMode.Auto)
        {
            using var transaction = new Transaction(context.Document, $"InnoPro: {command.Name}");
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new SilentWarnings());
            transaction.SetFailureHandlingOptions(options);

            transaction.Start();
            try
            {
                result = command.Execute(context, args);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new CommandException("Revit không chấp nhận thay đổi, đã hủy toàn bộ lệnh.");
            }
            catch
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
                throw;
            }
        }
        else
        {
            result = command.Execute(context, args);
        }

        return JsonSerializer.Serialize(result, BridgeProtocol.Json);
    }

    /// <summary>Bỏ qua cảnh báo (không bật hộp thoại chặn Revit); lỗi thật thì hủy Transaction.</summary>
    private sealed class SilentWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            foreach (var message in failures.GetFailureMessages())
            {
                if (message.GetSeverity() == FailureSeverity.Warning)
                    failures.DeleteWarning(message);
                else
                    return FailureProcessingResult.ProceedWithRollBack;
            }
            return FailureProcessingResult.Continue;
        }
    }
}
