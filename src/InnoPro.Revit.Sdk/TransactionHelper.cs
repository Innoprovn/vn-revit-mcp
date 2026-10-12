using Autodesk.Revit.DB;

namespace InnoPro.Revit.Sdk;

public static class TransactionHelper
{
    /// <summary>
    /// Bỏ qua cảnh báo để Revit không bật hộp thoại chặn luồng; gặp lỗi thật thì hủy Transaction.
    /// </summary>
    public static void SuppressWarnings(Transaction transaction)
    {
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SilentWarnings());
        transaction.SetFailureHandlingOptions(options);
    }

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
