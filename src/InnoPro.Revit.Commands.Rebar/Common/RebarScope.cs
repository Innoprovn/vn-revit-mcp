namespace InnoPro.Revit.Commands.Rebar;

// Đặt using bên trong namespace: nếu để ngoài, tên "Rebar" sẽ khớp với chính namespace
// InnoPro.Revit.Commands.Rebar trước khi khớp với lớp Autodesk.Revit.DB.Structure.Rebar.
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

/// <summary>Kết quả gom phạm vi thép cho một lệnh.</summary>
internal sealed class RebarScopeResult
{
    /// <summary>Phần tử thép (Rebar, RebarInSystem, RebarContainer), không trùng.</summary>
    public List<Element> Elements { get; } = [];

    /// <summary>Mô tả phạm vi cho thông báo: "phần tử chỉ định", "view hiện tại", "toàn mô hình".</summary>
    public string Scope { get; set; } = "";

    /// <summary>Id truyền vào nhưng không phải thép, không phải host có thép.</summary>
    public List<long> IgnoredIds { get; } = [];

    public IEnumerable<Rebar> ShapeRebars => Elements.OfType<Rebar>();
}

/// <summary>Gom thép theo id chỉ định, theo view, hoặc toàn mô hình; kiểm tra view hợp lệ.</summary>
internal static class RebarScope
{
    /// <summary>
    /// Đọc "element_ids": thép lấy thẳng; Area/Path Reinforcement bung ra RebarInSystem; phần tử khác
    /// được coi là host và lấy mọi thép nằm trong nó. Không có id thì lấy theo "scope"
    /// (active_view hoặc model, mặc định do lệnh quyết định).
    /// </summary>
    public static RebarScopeResult Resolve(RevitCommandContext context, JsonElement args, string defaultScope)
    {
        var doc = context.Document;
        var result = new RebarScopeResult();
        var seen = new HashSet<ElementId>();

        void Add(Element? e)
        {
            if (e != null && IsRebarElement(e) && seen.Add(e.Id)) result.Elements.Add(e);
        }

        var ids = Args.Has(args, "element_ids") ? Args.GetElementIds(args, "element_ids") : [];
        if (ids.Count > 0)
        {
            result.Scope = "phần tử chỉ định";
            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                switch (e)
                {
                    case null:
                        result.IgnoredIds.Add(id.Value);
                        break;
                    case Rebar or RebarInSystem or RebarContainer:
                        Add(e);
                        break;
                    case AreaReinforcement area:
                        foreach (var rid in area.GetRebarInSystemIds()) Add(doc.GetElement(rid));
                        break;
                    case PathReinforcement path:
                        foreach (var rid in path.GetRebarInSystemIds()) Add(doc.GetElement(rid));
                        break;
                    default:
                        var before = result.Elements.Count;
                        foreach (var hosted in RebarsInHost(e)) Add(hosted);
                        if (result.Elements.Count == before) result.IgnoredIds.Add(id.Value);
                        break;
                }
            }
            return result;
        }

        var scope = Args.GetStringOrDefault(args, "scope", defaultScope)!.Trim().ToLowerInvariant();
        if (scope is "active_view" or "view")
        {
            var view = ActiveModelView(context);
            result.Scope = $"view '{view.Name}'";
            foreach (var e in CollectInView(doc, view)) Add(e);
        }
        else if (scope is "model" or "all")
        {
            result.Scope = "toàn mô hình";
            foreach (var e in CollectInModel(doc)) Add(e);
        }
        else
        {
            throw new CommandException($"'scope' chỉ nhận 'active_view' hoặc 'model' (đang là '{scope}').");
        }

        return result;
    }

    public static bool IsRebarElement(Element e) => e is Rebar or RebarInSystem or RebarContainer;

    public static IEnumerable<Element> CollectInModel(Document doc)
    {
        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType())
            if (IsRebarElement(e)) yield return e;

        // RebarContainer chưa chắc nằm trong OST_Rebar: quét thêm theo class, trùng thì nơi gọi tự lọc.
        foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(RebarContainer)).WhereElementIsNotElementType())
            yield return e;
    }

    /// <summary>Collector theo view chỉ trả phần tử thật sự nhìn thấy (crop, ẩn, filter, workset).</summary>
    public static IEnumerable<Element> CollectInView(Document doc, View view)
    {
        foreach (var e in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType())
            if (IsRebarElement(e)) yield return e;

        foreach (var e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RebarContainer)).WhereElementIsNotElementType())
            yield return e;
    }

    public static IEnumerable<Element> RebarsInHost(Element host)
    {
        RebarHostData? data;
        try { data = RebarHostData.GetRebarHostData(host); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { data = null; }
        if (data == null) yield break;

        foreach (var r in data.GetRebarsInHost()) yield return r;
        foreach (var c in data.GetRebarContainersInHost()) yield return c;
        foreach (var area in data.GetAreaReinforcementsInHost())
            foreach (var id in area.GetRebarInSystemIds())
                if (host.Document.GetElement(id) is { } e) yield return e;
        foreach (var path in data.GetPathReinforcementsInHost())
            foreach (var id in path.GetRebarInSystemIds())
                if (host.Document.GetElement(id) is { } e) yield return e;
    }

    /// <summary>View đồ họa đang mở, báo lỗi rõ nếu là sheet, schedule, legend, template...</summary>
    public static View ActiveModelView(RevitCommandContext context)
    {
        var view = context.UIDocument.ActiveGraphicalView ?? context.Document.ActiveView;
        if (!IsModelView(view, out var problem)) throw new CommandException(problem);
        return view!;
    }

    public static bool IsModelView(View? view, out string problem)
    {
        problem = "";
        if (view == null)
            problem = "Không xác định được view đang mở.";
        else if (view.IsTemplate)
            problem = "View đang mở là view template. Mở một view thật rồi chạy lại.";
        else if (view is ViewSheet or ViewSchedule || view.ViewType is ViewType.Legend or ViewType.DraftingView
                 or ViewType.Schedule or ViewType.DrawingSheet or ViewType.ProjectBrowser or ViewType.SystemBrowser
                 or ViewType.Internal or ViewType.Undefined)
            problem = "Lệnh này cần view mô hình (mặt bằng, mặt cắt, 3D...), không chạy trên sheet, bảng thống kê hay legend.";
        return problem.Length == 0;
    }

    /// <summary>Id thép trong vùng chọn hiện tại (dùng trong PromptArguments: không chọn gì thì trả rỗng).</summary>
    public static List<long> SelectedIds(RevitCommandContext context) =>
        context.UIDocument.Selection.GetElementIds().Select(id => id.Value).ToList();
}
