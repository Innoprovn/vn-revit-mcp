using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace InnoPro.Revit.Sdk;

public sealed class RevitCommandContext
{
    public RevitCommandContext(UIApplication uiApplication, CancellationToken cancellationToken)
    {
        UIApplication = uiApplication;
        CancellationToken = cancellationToken;
    }

    public UIApplication UIApplication { get; }

    public CancellationToken CancellationToken { get; }

    public UIDocument UIDocument =>
        UIApplication.ActiveUIDocument ?? throw new CommandException("Chưa có tài liệu nào đang mở trong Revit.");

    public Document Document => UIDocument.Document;
}

/// <summary>
/// Lỗi có thông điệp dành cho người dùng/AI. Host trả nguyên thông điệp, không kèm stack trace.
/// </summary>
public class CommandException : Exception
{
    public CommandException(string message) : base(message) { }
}
