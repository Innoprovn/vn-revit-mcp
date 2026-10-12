using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Quản lý 1 Line Style riêng ("InnoPro - Tim dầm (Vẽ sàn)", subcategory của category Lines) để đánh dấu DetailLine do tool này vẽ ra ở Bước 1 — nhờ vậy Bước 2 (Tạo sàn từ line) đọc lại ĐÚNG các line này, không lẫn với CAD/DetailLine khác có sẵn trong view.</summary>
internal static class OwnedLineStyle
{
    private const string StyleName = "InnoPro - Tim dầm (Vẽ sàn)";

    /// <summary>Lấy GraphicsStyle đã có, hoặc tạo mới nếu chưa có — PHẢI gọi trong Transaction.</summary>
    public static GraphicsStyle EnsureStyle(Document doc)
    {
        var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

        var category = FindSubCategory(linesCategory) ?? doc.Settings.Categories.NewSubcategory(linesCategory, StyleName);

        try
        {
            category.LineColor = new Color(23, 112, 138);
        }
        catch (Exception)
        {
            // Best-effort - mau chi de de nhin, khong chan luong neu API tu choi doi mau.
        }

        try
        {
            category.SetLineWeight(6, GraphicsStyleType.Projection);
        }
        catch (Exception)
        {
            // Best-effort - do day chi de de nhin ro hon, khong chan luong neu API tu choi.
        }

        var style = category.GetGraphicsStyle(GraphicsStyleType.Projection);
        if (style is null)
            throw new InvalidOperationException("Không tạo được Line Style riêng cho tool.");

        return style;
    }

    /// <summary>Tìm GraphicsStyle đã có (không tạo mới) — null nếu Bước 1 chưa từng chạy trong file này.</summary>
    public static GraphicsStyle? FindExisting(Document doc)
    {
        var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        return FindSubCategory(linesCategory)?.GetGraphicsStyle(GraphicsStyleType.Projection);
    }

    private static Category? FindSubCategory(Category linesCategory)
    {
        foreach (Category sub in linesCategory.SubCategories)
        {
            if (string.Equals(sub.Name, StyleName, StringComparison.Ordinal))
                return sub;
        }

        return null;
    }

    /// <summary>Toàn bộ DetailLine mang đúng Line Style này, trong đúng 1 view cho trước.</summary>
    public static List<CurveElement> FindOwned(Document doc, View view, ElementId styleId)
    {
        return new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(CurveElement))
            .Cast<CurveElement>()
            .Where(ce => ce.LineStyle is GraphicsStyle gs && gs.Id == styleId)
            .ToList();
    }
}
