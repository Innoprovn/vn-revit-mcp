using Autodesk.Revit.DB;

namespace InnoPro.Revit.Sdk;

public static class TransactionHelper
{
    /// <summary>
    /// Bỏ qua cảnh báo để Revit không bật hộp thoại chặn luồng.
    /// Gặp lỗi thật: nếu Revit cho phép xóa phần tử gây lỗi VÀ mọi phần tử đó do chính lệnh vừa tạo
    /// (id lớn hơn newElementsAfter) thì xóa chúng và giữ phần còn lại của lô; ngược lại hủy cả Transaction
    /// (không bao giờ tự xóa phần tử có sẵn của người dùng).
    /// </summary>
    public static void SuppressWarnings(Transaction transaction, long? newElementsAfter = null)
    {
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SilentWarnings(newElementsAfter));
        transaction.SetFailureHandlingOptions(options);
    }

    /// <summary>Id lớn nhất hiện có trong tài liệu; phần tử tạo sau có id lớn hơn.</summary>
    public static long MaxElementId(Document doc)
    {
        long max = 0;
        foreach (var id in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()) max = Math.Max(max, id.Value);
        foreach (var id in new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds()) max = Math.Max(max, id.Value);
        return max;
    }

    private sealed class SilentWarnings(long? newElementsAfter) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            var resolved = false;
            foreach (var message in failures.GetFailureMessages())
            {
                if (message.GetSeverity() == FailureSeverity.Warning)
                {
                    failures.DeleteWarning(message);
                    continue;
                }

                if (newElementsAfter is { } after
                    && message.HasResolutionOfType(FailureResolutionType.DeleteElements)
                    && message.GetFailingElementIds().Count > 0
                    && message.GetFailingElementIds().All(id => id.Value > after))
                {
                    message.SetCurrentResolutionType(FailureResolutionType.DeleteElements);
                    failures.ResolveFailure(message);
                    resolved = true;
                    continue;
                }

                return FailureProcessingResult.ProceedWithRollBack;
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }
}
