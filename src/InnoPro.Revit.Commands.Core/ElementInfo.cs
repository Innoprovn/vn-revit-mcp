using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Core;

internal static class ElementInfo
{
    public static object Summary(Document doc, Element e) => new
    {
        element_id = e.Id.Value,
        category = e.Category?.Name,
        name = e.Name,
        type_name = TypeName(doc, e),
        level = LevelName(doc, e),
    };

    public static string? TypeName(Document doc, Element e)
    {
        var typeId = e.GetTypeId();
        return typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId)?.Name;
    }

    public static string? LevelName(Document doc, Element e)
    {
        var levelId = e.LevelId;
        return levelId == ElementId.InvalidElementId ? null : doc.GetElement(levelId)?.Name;
    }
}
