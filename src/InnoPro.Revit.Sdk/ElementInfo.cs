using Autodesk.Revit.DB;

namespace InnoPro.Revit.Sdk;

/// <summary>Tóm tắt phần tử theo một dạng thống nhất cho mọi CommandSet.</summary>
public static class ElementInfo
{
    public static object Summary(Document doc, Element e) => new
    {
        element_id = e.Id.Value,
        category = e.Category?.Name,
        name = e.Name,
        type_name = TypeName(doc, e),
        level = LevelName(doc, e),
    };

    /// <summary>
    /// Cấu kiện thật trong mô hình: category kiểu Model, không thuộc riêng một view,
    /// bỏ qua sketch, vật liệu, sun path... (Revit cũng xếp chúng vào category Model).
    /// </summary>
    public static bool IsPhysical(Element e) =>
        e.Category is { CategoryType: CategoryType.Model } category
        && !category.Name.StartsWith('<')
        && !e.ViewSpecific
        && e is not (Material or PropertySetElement or AppearanceAssetElement or SunAndShadowSettings or ElementType);

    public static string? TypeName(Document doc, Element e)
    {
        var typeId = e.GetTypeId();
        return typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId)?.Name;
    }

    public static string? LevelName(Document doc, Element e)
    {
        var levelId = e.LevelId;
        if (levelId == ElementId.InvalidElementId && e is FamilyInstance fi)
            levelId = fi.Host is Level ? fi.Host.Id : (e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId() ?? levelId);
        return levelId == ElementId.InvalidElementId ? null : doc.GetElement(levelId)?.Name;
    }

    /// <summary>Giá trị tham số dạng chuỗi hiển thị; null nếu không có hoặc trống.</summary>
    public static string? ParameterText(Element e, string name)
    {
        var p = e.LookupParameter(name);
        if (p == null || !p.HasValue) return null;
        var text = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
