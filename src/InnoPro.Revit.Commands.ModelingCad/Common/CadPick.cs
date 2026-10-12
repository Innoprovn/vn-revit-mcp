using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Common;

/// <summary>
/// Hỏi người bấm nút: click vào nét mẫu trong file CAD để lấy file CAD và tên layer
/// (không cần hộp thoại riêng). Esc để kết thúc.
/// </summary>
internal static class CadPick
{
    /// <summary>Click liên tiếp các nét mẫu (mỗi click thêm 1 layer), Esc khi xong. Phải chọn ít nhất 1 nét.</summary>
    public static (ImportInstance Cad, List<string> Layers) Layers(RevitCommandContext context, string prompt, ImportInstance? sameCad = null)
    {
        var uiDoc = context.UIDocument;
        var doc = uiDoc.Document;
        ImportInstance? cad = sameCad;
        var layers = new List<string>();

        while (true)
        {
            Reference reference;
            try
            {
                reference = uiDoc.Selection.PickObject(ObjectType.PointOnElement, new CadFilter(cad?.Id), $"{prompt} (Esc: xong)");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break;
            }

            if (doc.GetElement(reference.ElementId) is not ImportInstance instance) continue;
            cad ??= instance;

            GeometryObject? geometry;
            try { geometry = instance.GetGeometryObjectFromReference(reference); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { geometry = null; }
            if (geometry == null) continue;

            var layer = Cad.LayerName(doc, geometry.GraphicsStyleId);
            if (layer != Cad.UnknownLayer && !layers.Contains(layer)) layers.Add(layer);
        }

        if (cad == null || layers.Count == 0) throw new OperationCanceledException();
        return (cad, layers);
    }

    /// <summary>Như Layers nhưng cho phép bỏ qua (Esc ngay) và trả danh sách rỗng.</summary>
    public static List<string> OptionalLayers(RevitCommandContext context, string prompt, ImportInstance cad)
    {
        try { return Layers(context, prompt, cad).Layers; }
        catch (OperationCanceledException) { return []; }
    }

    private sealed class CadFilter(ElementId? onlyId) : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is ImportInstance && (onlyId == null || elem.Id == onlyId);
        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}

/// <summary>Đọc thêm vài dạng tham số mà SDK chưa có.</summary>
internal static class ArgsX
{
    public static string Enum(JsonElement args, string name, string fallback, params string[] allowed)
    {
        var value = Args.GetStringOrDefault(args, name, fallback)!.Trim().ToLowerInvariant();
        return allowed.Contains(value)
            ? value
            : throw new CommandException($"Tham số '{name}' phải là một trong: {string.Join(", ", allowed)}.");
    }

    /// <summary>Đọc object {"khóa": "giá trị"}; không có thì rỗng.</summary>
    public static Dictionary<string, string> StringMap(JsonElement args, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Args.Has(args, name)) return map;
        var v = args.GetProperty(name);
        if (v.ValueKind != JsonValueKind.Object) throw new CommandException($"Tham số '{name}' phải là object {{\"khóa\": \"giá trị\"}}.");
        foreach (var p in v.EnumerateObject()) map[p.Name.Trim()] = p.Value.ToString();
        return map;
    }

    /// <summary>Đọc object {"khóa": số}; không có thì rỗng.</summary>
    public static Dictionary<string, double> NumberMap(JsonElement args, string name)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!Args.Has(args, name)) return map;
        var v = args.GetProperty(name);
        if (v.ValueKind != JsonValueKind.Object) throw new CommandException($"Tham số '{name}' phải là object {{\"khóa\": số}}.");
        foreach (var p in v.EnumerateObject())
            map[p.Name.Trim()] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble()
                : throw new CommandException($"Giá trị '{name}.{p.Name}' phải là số.");
        return map;
    }

    public static IReadOnlyList<ElementId> IdsOrEmpty(JsonElement args, string name) =>
        Args.Has(args, name) ? Args.GetElementIds(args, name) : [];

    public static double Mm(JsonElement args, string name, double fallbackMm) =>
        UnitConvert.MmToFeet(Args.GetDoubleOrDefault(args, name, fallbackMm));

    public static FamilySymbol Symbol(Document doc, string name, BuiltInCategory category)
    {
        var symbol = Lookup.Type<FamilySymbol>(doc, name, category);
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        return symbol;
    }

    public static Family Family(Document doc, string name, BuiltInCategory category)
    {
        var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.FamilyCategory?.BuiltInCategory == category).ToList();
        return families.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new CommandException($"Không có family '{name}'. Family có sẵn: {Cad.Suggest(families.Select(f => f.Name))}.");
    }

    public static List<long> Ids(IEnumerable<ElementId> ids) => ids.Select(i => i.Value).ToList();
}
