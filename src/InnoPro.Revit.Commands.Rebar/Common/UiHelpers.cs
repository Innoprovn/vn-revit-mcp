namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using InnoPro.Revit.Sdk;

/// <summary>Thao tác hỏi người dùng khi bấm nút (chỉ dùng trong PromptArguments).</summary>
internal static class UiHelpers
{
    /// <summary>Bọc thao tác pick của Revit: Esc thành OperationCanceledException.</summary>
    public static T Pick<T>(Func<T> pick)
    {
        try { return pick(); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw new OperationCanceledException(); }
    }

    /// <summary>Hỏi Có/Không; Không thì hủy lệnh.</summary>
    public static void ConfirmOrCancel(string title, string instruction, string content)
    {
        var owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        var text = string.IsNullOrWhiteSpace(content) ? instruction : instruction + "\n" + content;
        if (InnoPro.Ui.InnoWindow.Choose(title, text, owner, "Có", "Không") != 0) throw new OperationCanceledException();
    }

    /// <summary>Pick nhiều mặt; trả chuỗi tham chiếu ổn định để truyền qua JSON.</summary>
    public static List<string> PickFaces(RevitCommandContext context, string prompt, bool allowEmpty)
    {
        var uiDoc = context.UIDocument;
        IList<Reference> refs;
        try
        {
            refs = uiDoc.Selection.PickObjects(ObjectType.Face, prompt);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            if (allowEmpty) return [];
            throw new OperationCanceledException();
        }
        return refs.Select(r => r.ConvertToStableRepresentation(context.Document)).ToList();
    }

    public static string PickFace(RevitCommandContext context, string prompt) =>
        Pick(() => context.UIDocument.Selection.PickObject(ObjectType.Face, prompt)).ConvertToStableRepresentation(context.Document);

    /// <summary>Pick một thanh trong set bằng điểm trên phần tử để biết vị trí thanh đã click.</summary>
    public static (ElementId Id, XYZ? Point) PickPointOnElement(RevitCommandContext context, string prompt)
    {
        var r = Pick(() => context.UIDocument.Selection.PickObject(ObjectType.PointOnElement, prompt));
        return (r.ElementId, r.GlobalPoint);
    }
}
