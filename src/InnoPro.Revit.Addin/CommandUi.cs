using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>Cài đặt ICommandUi bằng Selection API của Revit. Esc thì ném OperationCanceledException.</summary>
internal sealed class CommandUi(UIApplication app) : ICommandUi
{
    private UIDocument UiDoc => app.ActiveUIDocument ?? throw new CommandException("Chưa có tài liệu nào đang mở trong Revit.");

    public IReadOnlyList<Element> SelectionOrPick(string prompt, BuiltInCategory? category = null)
    {
        var doc = UiDoc.Document;
        var selected = UiDoc.Selection.GetElementIds().Select(doc.GetElement)
            .Where(e => e != null && (category == null || e.Category?.BuiltInCategory == category))
            .ToList();
        if (selected.Count > 0) return selected;

        return Guard(() => UiDoc.Selection.PickObjects(ObjectType.Element, new CategoryFilter(category), prompt)
            .Select(r => doc.GetElement(r)).ToList());
    }

    public Element PickElement(string prompt, BuiltInCategory? category = null) =>
        Guard(() => UiDoc.Document.GetElement(UiDoc.Selection.PickObject(ObjectType.Element, new CategoryFilter(category), prompt)));

    public XYZ PickPoint(string prompt) => Guard(() => UiDoc.Selection.PickPoint(prompt));

    private static T Guard<T>(Func<T> pick)
    {
        try { return pick(); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw new OperationCanceledException(); }
    }

    private sealed class CategoryFilter(BuiltInCategory? category) : ISelectionFilter
    {
        public bool AllowElement(Element elem) => category == null || elem.Category?.BuiltInCategory == category;
        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}
