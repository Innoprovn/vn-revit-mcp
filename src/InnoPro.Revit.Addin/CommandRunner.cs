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
        var args = arguments ?? EmptyArgs;
        RejectUnknownArguments(loaded.Command, args);
        var context = new RevitCommandContext(app, token, new CommandUi(app), isInteractive: false);
        return JsonSerializer.Serialize(Execute(context, loaded.Command, args), BridgeProtocol.Json);
    }

    /// <summary>
    /// AI truyền tham số không có trong schema (ví dụ dry_run cho lệnh không hỗ trợ) thì báo lỗi thay vì lặng lẽ bỏ qua:
    /// tránh trường hợp AI tưởng chỉ chạy thử nhưng lệnh đã sửa mô hình thật.
    /// </summary>
    private static void RejectUnknownArguments(IRevitCommand command, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return;
        using var schema = JsonDocument.Parse(command.InputSchema);
        var root = schema.RootElement;
        if (root.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind != JsonValueKind.False) return;
        var known = root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        var unknown = args.EnumerateObject().Select(p => p.Name).Where(n => !known.Contains(n)).ToList();
        if (unknown.Count == 0) return;
        throw new CommandException($"Lệnh {command.Name} không có tham số: {string.Join(", ", unknown)}. " +
                                   (known.Count == 0 ? "Lệnh không nhận tham số." : $"Tham số hợp lệ: {string.Join(", ", known)}."));
    }

    /// <summary>Mốc id phần tử khi lệnh InnoPro bắt đầu; null là không có lệnh nào đang chạy.</summary>
    private static long? _runningAfter;

    /// <summary>
    /// Chốt chặn cuối (gắn vào ControlledApplication.FailuresProcessing): trong lúc lệnh InnoPro chạy, lỗi nào lọt qua
    /// preprocessor (vd phát sinh ở lượt xử lý thứ hai) cũng được xử lý hoặc rollback, không để Revit bật hộp thoại chặn.
    /// </summary>
    public static void OnFailuresProcessing(object? sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
    {
        if (_runningAfter is not { } after) return;
        var accessor = e.GetFailuresAccessor();
        if (accessor.GetFailureMessages().Count == 0) return;
        e.SetProcessingResult(TransactionHelper.Resolve(accessor, after));
    }

    public static object? Execute(RevitCommandContext context, IRevitCommand command, JsonElement args)
    {
        var outer = _runningAfter;
        // Phần tử do lệnh tạo ra có id lớn hơn mốc này: chỉ những phần tử đó mới được tự xóa khi gây lỗi.
        _runningAfter ??= TransactionHelper.MaxElementId(context.Document);
        try
        {
            return ExecuteInTransaction(context, command, args, _runningAfter.Value);
        }
        finally
        {
            _runningAfter = outer;
        }
    }

    private static object? ExecuteInTransaction(RevitCommandContext context, IRevitCommand command, JsonElement args, long newElementsAfter)
    {
        if (command.TransactionMode != Sdk.TransactionMode.Auto)
            return command.Execute(context, args);

        using var transaction = new Transaction(context.Document, $"InnoPro: {command.Name}");
        TransactionHelper.SuppressWarnings(transaction, newElementsAfter);
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
