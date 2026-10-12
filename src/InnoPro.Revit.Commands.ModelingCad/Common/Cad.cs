using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Common;

/// <summary>Tìm file CAD (ImportInstance), view để đọc hình học, tầng, layer: dùng chung cho mọi lệnh CAD.</summary>
internal static class Cad
{
    public const string UnknownLayer = "(Không xác định layer)";

    /// <summary>
    /// File CAD theo cad_id; không có thì lấy file CAD đầu tiên thấy trong view hiện tại,
    /// hoặc file CAD duy nhất trong dự án.
    /// </summary>
    public static ImportInstance Resolve(Document doc, JsonElement args)
    {
        if (Args.Has(args, "cad_id"))
        {
            var id = new ElementId(Args.GetLong(args, "cad_id"));
            return doc.GetElement(id) as ImportInstance
                   ?? throw new CommandException($"Phần tử {id.Value} không phải file CAD (Import/Link). Gọi list_cad_links để xem id đúng.");
        }

        var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        if (all.Count == 0)
            throw new CommandException("Dự án chưa có file CAD nào được Link/Import.");

        var activeView = doc.ActiveView;
        if (activeView != null)
        {
            var visible = VisibleIn(doc, activeView);
            var first = all.FirstOrDefault(i => visible.Contains(i.Id));
            if (first != null) return first;
        }

        if (all.Count == 1) return all[0];
        throw new CommandException(
            $"View hiện tại không có file CAD và dự án có {all.Count} file CAD. Truyền cad_id (một trong: " +
            string.Join(", ", all.Take(10).Select(i => $"{i.Id.Value} = {Describe(doc, i)}")) + ").");
    }

    public static HashSet<ElementId> VisibleIn(Document doc, View view)
    {
        try
        {
            return new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).ToElementIds().ToHashSet();
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            return [];
        }
    }

    public static string Describe(Document doc, ImportInstance instance)
    {
        var name = doc.GetElement(instance.GetTypeId())?.Name;
        var kind = instance.IsLinked ? "CAD Link" : "CAD Import";
        return string.IsNullOrWhiteSpace(name) ? $"{kind} #{instance.Id.Value}" : $"{kind}: {name}";
    }

    /// <summary>
    /// View dùng để đọc hình học CAD (layer đang tắt trong view sẽ bị bỏ qua): view sở hữu nếu CAD chỉ
    /// thuộc 1 view, mặt bằng đang mở, hoặc mặt bằng đầu tiên của tầng.
    /// </summary>
    public static View ScanView(Document doc, ImportInstance cad, Level? level)
    {
        if (cad.ViewSpecific && doc.GetElement(cad.OwnerViewId) is View owner)
            return owner;
        if (doc.ActiveView is ViewPlan { IsTemplate: false } active && (level == null || active.GenLevel?.Id == level.Id))
            return active;
        if (level != null && PlanOf(doc, level) is { } plan)
            return plan;
        if (doc.ActiveView is ViewPlan { IsTemplate: false } anyActive)
            return anyActive;
        throw new CommandException("Cần mở một mặt bằng (Floor/Structural Plan) chứa file CAD, hoặc truyền level để lệnh tự tìm mặt bằng.");
    }

    public static ViewPlan? PlanOf(Document doc, Level level) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.GenLevel?.Id == level.Id)
            .OrderBy(v => v.ViewType switch { ViewType.FloorPlan => 0, ViewType.EngineeringPlan => 1, _ => 2 })
            .FirstOrDefault();

    /// <summary>Tầng theo tham số (tên), mặc định tầng của view.</summary>
    public static Level LevelOrView(Document doc, JsonElement args, string name, View? view)
    {
        if (Args.GetStringOrDefault(args, name) is { Length: > 0 } levelName)
            return Lookup.Level(doc, levelName);
        if (view is ViewPlan { GenLevel: { } genLevel })
            return genLevel;
        if (doc.ActiveView is ViewPlan { GenLevel: { } activeLevel })
            return activeLevel;
        throw new CommandException($"Thiếu tham số '{name}' (tên tầng) và view hiện tại không gắn với tầng nào.");
    }

    /// <summary>Tầng theo tham số trước khi biết view (dùng để chọn view đọc CAD).</summary>
    public static Level? LevelOrNull(Document doc, JsonElement args, string name) =>
        Args.GetStringOrDefault(args, name) is { Length: > 0 } levelName ? Lookup.Level(doc, levelName) : null;

    public static string LayerName(Document doc, ElementId styleId)
    {
        if (styleId == ElementId.InvalidElementId || doc.GetElement(styleId) is not GraphicsStyle style)
            return UnknownLayer;
        var name = style.GraphicsStyleCategory?.Name;
        return string.IsNullOrWhiteSpace(name) ? UnknownLayer : name;
    }

    /// <summary>
    /// Đọc danh sách layer từ tham số (mảng "layers" hoặc chuỗi "layer"), so khớp không phân biệt hoa thường
    /// với layer có thật. Báo lỗi kèm danh sách layer khi không khớp.
    /// </summary>
    public static List<string> Layers(JsonElement args, IEnumerable<string> available, string arrayName = "layers", string singleName = "layer", bool required = true)
    {
        var wanted = Args.GetStringsOrEmpty(args, arrayName).ToList();
        if (Args.GetStringOrDefault(args, singleName) is { Length: > 0 } single) wanted.Add(single);
        var availableList = available.Distinct().ToList();
        if (wanted.Count == 0)
        {
            if (!required) return [];
            throw new CommandException($"Thiếu tham số '{arrayName}'. Layer có trong CAD: {Suggest(availableList)}.");
        }

        var result = new List<string>();
        foreach (var name in wanted)
        {
            var match = availableList.FirstOrDefault(a => string.Equals(a, name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new CommandException($"CAD không có layer '{name}' (hoặc layer đó không có đối tượng phù hợp). Layer có: {Suggest(availableList)}.");
            if (!result.Contains(match)) result.Add(match);
        }
        return result;
    }

    public static string Suggest(IEnumerable<string> names)
    {
        var list = names.Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        return list.Count == 0 ? "(không có)" : string.Join(", ", list.Take(30)) + (list.Count > 30 ? $"... (+{list.Count - 30})" : "");
    }

    /// <summary>Thống kê đối tượng theo layer để người dùng/AI biết layer nào chứa gì.</summary>
    public static List<LayerStat> ScanLayers(Document doc, ImportInstance cad, View view)
    {
        var stats = new Dictionary<string, LayerStat>(StringComparer.Ordinal);
        GeometryElement? geometry;
        try { geometry = cad.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false }); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { geometry = null; }
        if (geometry != null) Walk(doc, geometry, stats, 0);
        return stats.Values.OrderByDescending(s => s.Total).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Walk(Document doc, GeometryElement geometry, Dictionary<string, LayerStat> stats, int depth)
    {
        if (depth > 8) return;
        foreach (var obj in geometry)
        {
            try
            {
                switch (obj)
                {
                    case GeometryInstance instance:
                        if (instance.GetInstanceGeometry() is { } nested) Walk(doc, nested, stats, depth + 1);
                        break;
                    case PolyLine polyLine:
                        Stat(doc, stats, polyLine.GraphicsStyleId).Polylines++;
                        break;
                    case Line line:
                        Stat(doc, stats, line.GraphicsStyleId).Lines++;
                        break;
                    case Arc arc:
                        if (arc.IsBound) Stat(doc, stats, arc.GraphicsStyleId).Arcs++;
                        else Stat(doc, stats, arc.GraphicsStyleId).Circles++;
                        break;
                    case Curve curve:
                        Stat(doc, stats, curve.GraphicsStyleId).OtherCurves++;
                        break;
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face face in solid.Faces)
                        {
                            if (face is not PlanarFace) continue;
                            var styleId = face.GraphicsStyleId != ElementId.InvalidElementId ? face.GraphicsStyleId : solid.GraphicsStyleId;
                            Stat(doc, stats, styleId).Hatches++;
                        }
                        break;
                    case PlanarFace face:
                        Stat(doc, stats, face.GraphicsStyleId).Hatches++;
                        break;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                // Một đối tượng hình học lỗi không làm hỏng cả lượt quét.
            }
        }
    }

    private static LayerStat Stat(Document doc, Dictionary<string, LayerStat> stats, ElementId styleId)
    {
        var name = LayerName(doc, styleId);
        if (!stats.TryGetValue(name, out var stat)) stats[name] = stat = new LayerStat(name);
        return stat;
    }

    internal sealed class LayerStat(string name)
    {
        public string Name { get; } = name;
        public int Lines { get; set; }
        public int Polylines { get; set; }
        public int Arcs { get; set; }
        public int Circles { get; set; }
        public int OtherCurves { get; set; }
        public int Hatches { get; set; }
        public int Total => Lines + Polylines + Arcs + Circles + OtherCurves + Hatches;
    }
}
