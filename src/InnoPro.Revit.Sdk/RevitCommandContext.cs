using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace InnoPro.Revit.Sdk;

public sealed class RevitCommandContext
{
    public RevitCommandContext(UIApplication uiApplication, CancellationToken cancellationToken, ICommandUi ui, bool isInteractive)
    {
        UIApplication = uiApplication;
        CancellationToken = cancellationToken;
        Ui = ui;
        IsInteractive = isInteractive;
    }

    public UIApplication UIApplication { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>Hỏi người dùng (chọn phần tử, chọn điểm...). Chỉ dùng khi IsInteractive.</summary>
    public ICommandUi Ui { get; }

    /// <summary>true khi người dùng bấm nút; false khi AI gọi (không được bật hộp thoại chờ người dùng).</summary>
    public bool IsInteractive { get; }

    public UIDocument UIDocument =>
        UIApplication.ActiveUIDocument ?? throw new CommandException("Chưa có tài liệu nào đang mở trong Revit.");

    public Document Document => UIDocument.Document;
}

/// <summary>
/// Tương tác với người dùng trong Revit. Người dùng bấm Esc thì ném OperationCanceledException.
/// </summary>
public interface ICommandUi
{
    /// <summary>Phần tử đang chọn; nếu chưa chọn gì thì yêu cầu người dùng quét chọn.</summary>
    IReadOnlyList<Element> SelectionOrPick(string prompt, BuiltInCategory? category = null);

    Element PickElement(string prompt, BuiltInCategory? category = null);

    /// <summary>Điểm trên mặt phẳng làm việc của view hiện tại.</summary>
    XYZ PickPoint(string prompt);
}

/// <summary>
/// Lỗi có thông điệp dành cho người dùng/AI. Host trả nguyên thông điệp, không kèm stack trace.
/// </summary>
public class CommandException : Exception
{
    public CommandException(string message) : base(message) { }
}
