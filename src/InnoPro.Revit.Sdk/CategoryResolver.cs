using Autodesk.Revit.DB;

namespace InnoPro.Revit.Sdk;

public static class CategoryResolver
{
    /// <summary>
    /// Tìm category theo tên BuiltInCategory ("OST_Walls" hoặc "Walls") hoặc theo tên hiển thị
    /// trong dự án ("Walls", "Tường" nếu Revit tiếng Việt).
    /// </summary>
    public static Category Resolve(Document doc, string name)
    {
        var trimmed = name.Trim();
        var enumName = trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? trimmed : "OST_" + trimmed;
        if (Enum.TryParse<BuiltInCategory>(enumName, ignoreCase: true, out var builtIn))
        {
            var category = Category.GetCategory(doc, builtIn);
            if (category != null) return category;
        }

        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        throw new CommandException(
            $"Không tìm thấy category '{name}'. Dùng tên BuiltInCategory (ví dụ OST_Walls, OST_StructuralColumns) hoặc tên hiển thị trong dự án.");
    }
}
