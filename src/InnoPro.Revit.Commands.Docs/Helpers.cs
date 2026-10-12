using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

/// <summary>Tìm view, sheet theo tên/số hiệu/id mà AI và người dùng hay dùng.</summary>
internal static class DocLookup
{
    /// <summary>
    /// View theo tên (tham số <paramref name="namesKey"/>) và/hoặc id (<paramref name="idsKey"/>).
    /// Không truyền gì thì lấy view đang mở (nếu <paramref name="defaultActive"/>).
    /// </summary>
    public static List<View> Views(Document doc, JsonElement args, string namesKey = "views", string idsKey = "view_ids", bool defaultActive = true)
    {
        var result = new List<View>();
        if (Args.Has(args, idsKey))
        {
            foreach (var id in Args.GetElementIds(args, idsKey))
                result.Add(doc.GetElement(id) as View ?? throw new CommandException($"Id {id.Value} không phải là view."));
        }

        var names = Args.GetStringsOrEmpty(args, namesKey);
        if (names.Count > 0)
        {
            var all = AllViews(doc).ToList();
            foreach (var name in names) result.Add(ViewByName(all, name));
        }

        if (result.Count == 0 && defaultActive)
            result.Add(doc.ActiveView ?? throw new CommandException("Không có view nào đang mở."));
        return result.DistinctBy(v => v.Id.Value).ToList();
    }

    /// <summary>Một view theo tên, hoặc view đang mở nếu tên trống.</summary>
    public static View ViewOrActive(Document doc, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? doc.ActiveView ?? throw new CommandException("Không có view nào đang mở.")
            : ViewByName(AllViews(doc).ToList(), name);

    /// <summary>View thường (không phải template, không phải view nội bộ của Revit).</summary>
    public static IEnumerable<View> AllViews(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType is not (ViewType.Internal or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Undefined));

    /// <summary>Tên view, chấp nhận dạng "FloorPlan: Tầng 1" khi nhiều loại view trùng tên.</summary>
    private static View ViewByName(List<View> views, string name)
    {
        var wanted = name.Trim();
        var colon = wanted.IndexOf(':');
        if (colon > 0 && Enum.TryParse<ViewType>(wanted[..colon].Trim(), true, out var type))
        {
            var exact = views.FirstOrDefault(v => v.ViewType == type && string.Equals(v.Name, wanted[(colon + 1)..].Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        var matches = views.Where(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CommandException($"Không có view '{name}'. Dùng list_views để xem tên đúng."),
            _ => throw new CommandException(
                $"Có {matches.Count} view tên '{name}' ({string.Join(", ", matches.Select(v => $"{v.ViewType} id {v.Id.Value}"))}). " +
                "Ghi rõ dạng \"FloorPlan: tên\" hoặc dùng view_ids."),
        };
    }

    /// <summary>Sheet theo số hiệu, nếu không có thì theo tên.</summary>
    public static ViewSheet Sheet(Document doc, string numberOrName)
    {
        var wanted = numberOrName.Trim();
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
        return sheets.FirstOrDefault(s => string.Equals(s.SheetNumber, wanted, StringComparison.OrdinalIgnoreCase))
               ?? sheets.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? throw new CommandException($"Không có sheet số hiệu hoặc tên '{numberOrName}'. Dùng list_sheets để xem.");
    }

    /// <summary>Khung tên (title block) đặt trên sheet, null nếu sheet trống.</summary>
    public static FamilyInstance? TitleBlock(Document doc, ViewSheet sheet) =>
        new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();

    /// <summary>Đọc enum từ chuỗi, báo lỗi kèm các giá trị hợp lệ.</summary>
    public static string Choice(JsonElement args, string name, string fallback, params string[] allowed)
    {
        var value = (Args.GetStringOrDefault(args, name) ?? fallback).Trim().ToLowerInvariant();
        return allowed.Contains(value) ? value
            : throw new CommandException($"Tham số '{name}' phải là một trong: {string.Join(", ", allowed)}.");
    }

    /// <summary>Bỏ ký tự Revit và Windows không cho phép trong tên view hoặc tên file.</summary>
    public static string SafeName(string raw, string fallback)
    {
        var cleaned = Regex.Replace(raw, @"[\\/:*?""<>|{}\[\];`~]", "_").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    public static object PointMm(XYZ p) => new
    {
        x = UnitConvert.FeetToMm(p.X),
        y = UnitConvert.FeetToMm(p.Y),
        z = UnitConvert.FeetToMm(p.Z),
    };
}

/// <summary>Hình học dùng chung: lấy solid, hộp bao sau biến đổi, độ xuyên giữa hai khối.</summary>
internal static class Geo
{
    /// <summary>Mọi Solid có thể tích của phần tử (kể cả trong GeometryInstance), bỏ qua solid hỏng.</summary>
    public static List<Solid> Solids(Element element)
    {
        var result = new List<Solid>();
        GeometryElement? geometry;
        try { geometry = element.get_Geometry(new Options { ComputeReferences = false, IncludeNonVisibleObjects = false }); }
        catch { return result; }
        if (geometry != null) Collect(geometry, result);
        return result;
    }

    private static void Collect(GeometryElement geometry, List<Solid> result)
    {
        foreach (var obj in geometry)
        {
            if (obj is Solid solid && HasVolume(solid)) result.Add(solid);
            else if (obj is GeometryInstance instance && instance.GetInstanceGeometry() is { } nested) Collect(nested, result);
        }
    }

    public static bool HasVolume(Solid? solid)
    {
        if (solid == null) return false;
        try { return solid.Volume > 1e-9; }
        catch { return false; } // một số solid lỗi ném exception ngay khi đọc Volume
    }

    /// <summary>Hộp bao sau biến đổi, tính qua cả 8 đỉnh (đúng cả khi xoay).</summary>
    public static (XYZ Min, XYZ Max) Transform(XYZ min, XYZ max, Transform transform, double padding = 0)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var p = transform.OfPoint(new XYZ((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
        }
        return (new XYZ(x0 - padding, y0 - padding, z0 - padding), new XYZ(x1 + padding, y1 + padding, z1 + padding));
    }

    /// <summary>
    /// Độ xuyên lớn nhất (feet) giữa hai tập solid cùng hệ tọa độ: cạnh nhỏ nhất của hộp bao phần giao.
    /// null nếu không có phần giao có thể tích (chạm mặt, hoặc chỉ giao theo đường tim).
    /// </summary>
    public static double? PenetrationDepth(IReadOnlyList<Solid> a, IReadOnlyList<Solid> b)
    {
        double? best = null;
        foreach (var sa in a)
        {
            foreach (var sb in b)
            {
                Solid? intersection;
                try { intersection = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect); }
                catch { continue; }
                if (!HasVolume(intersection)) continue;

                BoundingBoxXYZ box;
                try { box = intersection!.GetBoundingBox(); }
                catch { continue; }
                var (min, max) = Transform(box.Min, box.Max, box.Transform);
                var depth = Math.Min(max.X - min.X, Math.Min(max.Y - min.Y, max.Z - min.Z));
                if (best == null || depth > best) best = depth;
            }
        }
        return best;
    }
}
