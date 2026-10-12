using Autodesk.Revit.DB;

namespace InnoPro.Revit.Sdk;

/// <summary>
/// Tìm phần tử theo tên mà AI hay dùng (tên tầng, tên type...), báo lỗi kèm gợi ý khi không thấy.
/// </summary>
public static class Lookup
{
    public static Level Level(Document doc, string name)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        return levels.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new CommandException($"Không có tầng '{name}'. Các tầng: {Suggest(levels.Select(l => l.Name))}.");
    }

    /// <summary>
    /// Tìm ElementType (WallType, FloorType, FamilySymbol...) theo "Tên type" hoặc "Tên family: Tên type".
    /// </summary>
    public static T Type<T>(Document doc, string name, BuiltInCategory? category = null) where T : ElementType
    {
        var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
        if (category is { } c) collector.OfCategory(c);
        var types = collector.Cast<T>().ToList();

        var wanted = name.Trim();
        var match = types.FirstOrDefault(t => string.Equals(FullName(t), wanted, StringComparison.OrdinalIgnoreCase))
                    ?? types.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new CommandException(
            $"Không có type '{name}'. Một số type có sẵn: {Suggest(types.Select(FullName))}.");
    }

    /// <summary>"Tên family: Tên type" nếu là FamilySymbol, ngược lại tên type.</summary>
    public static string FullName(ElementType type) =>
        string.IsNullOrEmpty(type.FamilyName) ? type.Name : $"{type.FamilyName}: {type.Name}";

    private static string Suggest(IEnumerable<string> names)
    {
        var list = names.Distinct().OrderBy(n => n).ToList();
        return list.Count == 0 ? "(không có)" : string.Join(", ", list.Take(15)) + (list.Count > 15 ? $"... (+{list.Count - 15})" : "");
    }
}
