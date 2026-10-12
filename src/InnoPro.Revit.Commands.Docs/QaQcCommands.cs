using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI.Selection;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

public sealed class FindLinkClashes : RevitCommandBase
{
    private const int MaxHostElements = 5000;

    public override string Name => "find_link_clashes";
    public override string Category => "QA-QC";
    public override RibbonButton? Ribbon => new("Va chạm link", "clash");
    public override string Description =>
        "Tìm va chạm giữa mô hình chính và model link, kèm độ xuyên (mm) và ngưỡng bỏ qua.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category_a": { "type": "string", "description": "Phía mô hình chính, ví dụ OST_DuctCurves." },
            "category_b": { "type": "string", "description": "Phía link. Trống = mọi cấu kiện." },
            "link": { "type": "string", "description": "Tên link chứa chuỗi này." },
            "level": { "type": "string", "description": "Tầng phía mô hình chính." },
            "use_section_box": { "type": "boolean", "description": "Chỉ xét trong section box view 3D." },
            "min_depth_mm": { "type": "number", "description": "Bỏ qua độ xuyên ≤ giá trị này." },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          },
          "required": ["category_a"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var categoryA = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category_a"));
        var categoryBName = Args.GetStringOrDefault(arguments, "category_b");
        var linkFilter = Args.GetStringOrDefault(arguments, "link");
        var minDepth = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "min_depth_mm", 0));
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);

        (XYZ Min, XYZ Max)? scope = null;
        if (Args.GetBoolOrDefault(arguments, "use_section_box", false))
        {
            if (doc.ActiveView is not View3D { IsSectionBoxActive: true } view3D)
                throw new CommandException("View đang mở không phải view 3D có bật Section Box.");
            var box = view3D.GetSectionBox();
            scope = Geo.Transform(box.Min, box.Max, box.Transform);
        }

        var hostCollector = new FilteredElementCollector(doc).OfCategoryId(categoryA.Id).WhereElementIsNotElementType();
        if (scope is { } s) hostCollector.WherePasses(new BoundingBoxIntersectsFilter(new Outline(s.Min, s.Max)));
        IEnumerable<Element> hostQuery = hostCollector.Where(e => !e.ViewSpecific);
        if (Args.GetStringOrDefault(arguments, "level") is { } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            hostQuery = hostQuery.Where(e => ElementInfo.LevelName(doc, e) == level.Name);
        }
        var hosts = hostQuery.Take(MaxHostElements + 1).ToList();
        if (hosts.Count > MaxHostElements)
            throw new CommandException($"Phía mô hình chính có hơn {MaxHostElements} phần tử. Hãy giới hạn theo 'level' hoặc section box.");

        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => linkFilter == null || l.Name.Contains(linkFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (links.Count == 0)
            throw new CommandException(linkFilter == null ? "Mô hình không có Revit link nào." : $"Không có link nào tên chứa '{linkFilter}'.");

        var hostSolids = new Dictionary<long, List<Solid>>();
        var clashes = new List<object>();
        var linkReport = new List<object>();
        var total = 0;
        foreach (var link in links)
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null)
            {
                linkReport.Add(new { link = link.Name, status = "chưa nạp", candidates = 0, clashes = 0 });
                continue;
            }

            var transform = link.GetTotalTransform();
            var inverse = transform.Inverse;
            var linkCollector = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType().WhereElementIsViewIndependent();
            if (categoryBName != null)
            {
                Category categoryB;
                try { categoryB = CategoryResolver.Resolve(linkDoc, categoryBName); }
                catch (CommandException)
                {
                    linkReport.Add(new { link = link.Name, status = "link không có category B", candidates = 0, clashes = 0 });
                    continue;
                }
                linkCollector.OfCategoryId(categoryB.Id);
            }
            if (scope is { } sc)
            {
                var (lmin, lmax) = Geo.Transform(sc.Min, sc.Max, inverse);
                linkCollector.WherePasses(new BoundingBoxIntersectsFilter(new Outline(lmin, lmax)));
            }
            var candidateIds = linkCollector.Where(e => e.Category is { CategoryType: CategoryType.Model }).Select(e => e.Id).ToList();
            if (candidateIds.Count == 0)
            {
                linkReport.Add(new { link = link.Name, status = "đã quét", candidates = 0, clashes = 0 });
                continue;
            }

            var linkSolids = new Dictionary<long, List<Solid>>();
            var found = 0;
            foreach (var host in hosts)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var box = host.get_BoundingBox(null);
                if (box == null) continue;
                if (!hostSolids.TryGetValue(host.Id.Value, out var solids)) hostSolids[host.Id.Value] = solids = Geo.Solids(host);
                if (solids.Count == 0) continue;

                var (min, max) = Geo.Transform(box.Min, box.Max, inverse);
                var near = new FilteredElementCollector(linkDoc, candidateIds)
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(min, max)))
                    .ToElementIds();
                if (near.Count == 0) continue;

                // Đưa khối của phần tử chính về hệ tọa độ link để so với phần tử link.
                var inLink = new List<Solid>();
                foreach (var solid in solids)
                {
                    try { inLink.Add(SolidUtils.CreateTransformed(solid, inverse)); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                }

                foreach (var linkId in near)
                {
                    var linkElement = linkDoc.GetElement(linkId);
                    if (!linkSolids.TryGetValue(linkId.Value, out var other)) linkSolids[linkId.Value] = other = Geo.Solids(linkElement);
                    if (other.Count == 0) continue;
                    var depth = Geo.PenetrationDepth(inLink, other);
                    if (depth == null || depth <= minDepth) continue;

                    found++;
                    total++;
                    if (clashes.Count < limit)
                        clashes.Add(new
                        {
                            element_a = host.Id.Value,
                            category_a = host.Category?.Name,
                            link = link.Name,
                            link_instance_id = link.Id.Value,
                            element_b = linkId.Value,
                            category_b = linkElement.Category?.Name,
                            type_b = ElementInfo.TypeName(linkDoc, linkElement),
                            depth_mm = Math.Round(UnitConvert.FeetToMm(depth.Value), 1),
                        });
                }
            }
            linkReport.Add(new { link = link.Name, status = "đã quét", candidates = candidateIds.Count, clashes = found });
        }

        return new
        {
            category_a = categoryA.Name,
            checked_a = hosts.Count,
            total_clashes = total,
            links = Table.Of(linkReport),
            clashes = WithTotal(Table.Of(clashes), total),
        };
    }

    private static Table WithTotal(Table table, int total) =>
        new() { Columns = table.Columns, Rows = table.Rows, Total = total > table.Rows.Count ? total : null };
}

public sealed class DeleteDuplicateElements : RevitCommandBase
{
    public override string Name => "delete_duplicate_elements";
    public override string Category => "QA-QC";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Xóa phần tử trùng vị trí, giữ bản có id nhỏ nhất; dry_run để xem trước.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Trống = mọi cấu kiện." },
            "tolerance_mm": { "type": "number", "description": "Sai số vị trí, mặc định 5 mm." },
            "dry_run": { "type": "boolean", "description": "Chỉ liệt kê, không xóa." },
            "limit": { "type": "integer", "description": "Mặc định 100." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var tolerance = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "tolerance_mm", 5));
        if (tolerance <= 0) throw new CommandException("'tolerance_mm' phải lớn hơn 0.");
        var dryRun = Args.GetBoolOrDefault(arguments, "dry_run", false);

        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (Args.GetStringOrDefault(arguments, "category") is { } category)
            collector.OfCategoryId(CategoryResolver.Resolve(doc, category).Id);

        var groups = collector
            .Where(e => ElementInfo.IsPhysical(e) && e.GroupId == ElementId.InvalidElementId && e is not RevitLinkInstance)
            .Select(e => (Element: e, Key: Key(e, tolerance)))
            .Where(x => x.Key != null)
            .GroupBy(x => x.Key!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.Element).OrderBy(e => e.Id.Value).ToList())
            .ToList();

        var report = groups.Select(g => new
        {
            category = g[0].Category?.Name,
            type_name = ElementInfo.TypeName(doc, g[0]),
            kept_id = g[0].Id.Value,
            removed_ids = g.Skip(1).Select(e => e.Id.Value).ToList(),
        }).ToList();

        var deleted = 0;
        if (!dryRun)
        {
            var ids = groups.SelectMany(g => g.Skip(1)).Select(e => e.Id).ToList();
            if (ids.Count > 0) deleted = doc.Delete(ids).Count;
        }

        return new
        {
            dry_run = dryRun,
            duplicate_groups = groups.Count,
            redundant_elements = groups.Sum(g => g.Count - 1),
            deleted_elements = deleted,
            groups = Table.Of(report, (int)Args.GetLongOrDefault(arguments, "limit", 100)),
        };
    }

    private static string? Key(Element e, double tolerance)
    {
        string Round(XYZ p) => $"{Math.Round(p.X / tolerance)},{Math.Round(p.Y / tolerance)},{Math.Round(p.Z / tolerance)}";
        var prefix = $"{e.Category.Id.Value}|{e.GetTypeId().Value}|";
        return e.Location switch
        {
            LocationPoint lp => prefix + Round(lp.Point) + "|" + Math.Round(lp.Rotation, 3).ToString(CultureInfo.InvariantCulture),
            LocationCurve { Curve: { IsBound: true } curve } =>
                prefix + string.Join("~", new[] { Round(curve.GetEndPoint(0)), Round(curve.GetEndPoint(1)) }.OrderBy(x => x, StringComparer.Ordinal)),
            _ => null,
        };
    }
}

public sealed class AuditModelHealth : RevitCommandBase
{
    public override string Name => "audit_model_health";
    public override string Category => "QA-QC";
    public override RibbonButton? Ribbon => new("Sức khỏe MH", "health");
    public override string Description =>
        "Đánh giá sức khỏe mô hình: cảnh báo, CAD import, in-place, phòng lỗi, view ngoài sheet, link.";

    private static readonly (string Label, BuiltInCategory Category)[] MainCategories =
    [
        ("Tường", BuiltInCategory.OST_Walls),
        ("Sàn", BuiltInCategory.OST_Floors),
        ("Cột kết cấu", BuiltInCategory.OST_StructuralColumns),
        ("Dầm / giằng", BuiltInCategory.OST_StructuralFraming),
        ("Móng", BuiltInCategory.OST_StructuralFoundation),
        ("Cửa đi", BuiltInCategory.OST_Doors),
        ("Cửa sổ", BuiltInCategory.OST_Windows),
        ("Phòng", BuiltInCategory.OST_Rooms),
    ];

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        double? fileMb = null;
        if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName))
            fileMb = Math.Round(new FileInfo(doc.PathName).Length / 1048576.0, 1);

        var warnings = doc.GetWarnings().Count;
        var allViews = DocLookup.AllViews(doc).ToList();
        var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => v.IsTemplate);
        var sheets = allViews.OfType<ViewSheet>().ToList();
        var placed = sheets.SelectMany(s => s.GetAllPlacedViews()).Select(id => id.Value).ToHashSet();
        foreach (var instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            placed.Add(instance.ScheduleId.Value);
        var drawingViews = allViews.Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan
            or ViewType.AreaPlan or ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.DraftingView or ViewType.ThreeD).ToList();
        var notOnSheet = drawingViews.Count(v => !placed.Contains(v.Id.Value));
        var schedules = allViews.OfType<ViewSchedule>().Count(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule);

        var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
        var groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().ToList();
        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().ToList();
        var unplacedRooms = rooms.Count(r => r.Location == null);
        var unenclosedRooms = rooms.Count(r => r.Location != null && r.Area <= 0);
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        var designOptions = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).GetElementCount();
        var worksets = doc.IsWorkshared ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Count : 0;
        var physical = new FilteredElementCollector(doc).WhereElementIsNotElementType().Count(ElementInfo.IsPhysical);

        var importedCad = imports.Count(i => !i.IsLinked);
        var inPlace = families.Count(f => f.IsInPlace);
        var modelGroups = groups.Count(g => g.Category?.BuiltInCategory == BuiltInCategory.OST_IOSModelGroups);
        var detailGroups = groups.Count(g => g.Category?.BuiltInCategory == BuiltInCategory.OST_IOSDetailGroups);
        var unloadedLinks = links.Count(l => l.GetLinkDocument() == null);

        var issues = new List<string>();
        if (warnings > 0) issues.Add($"{warnings} cảnh báo Revit (xem get_warnings).");
        if (importedCad > 0) issues.Add($"{importedCad} file CAD import thẳng vào mô hình (nên dùng Link CAD).");
        if (inPlace > 10) issues.Add($"{inPlace} family in-place (nhiều family in-place làm nặng mô hình).");
        if (unplacedRooms > 0) issues.Add($"{unplacedRooms} phòng chưa đặt (Not Placed).");
        if (unenclosedRooms > 0) issues.Add($"{unenclosedRooms} phòng chưa khép kín (Not Enclosed).");
        if (unloadedLinks > 0) issues.Add($"{unloadedLinks} link Revit chưa nạp.");
        if (drawingViews.Count > 0 && notOnSheet > drawingViews.Count / 2)
            issues.Add($"{notOnSheet}/{drawingViews.Count} view chưa nằm trên sheet (cân nhắc dọn view thừa).");
        if (fileMb > 300) issues.Add($"File nặng {fileMb} MB.");

        return new
        {
            title = doc.Title,
            file_size_mb = fileMb,
            is_workshared = doc.IsWorkshared,
            worksets,
            warnings,
            model_elements = physical,
            main_categories = Table.Of(MainCategories.Select(c => new
            {
                category = c.Label,
                count = new FilteredElementCollector(doc).OfCategory(c.Category).WhereElementIsNotElementType().GetElementCount(),
            })),
            views = allViews.Count - sheets.Count,
            views_not_on_sheets = notOnSheet,
            view_templates = templates,
            sheets = sheets.Count,
            schedules,
            families = families.Count,
            in_place_families = inPlace,
            model_groups = modelGroups,
            detail_groups = detailGroups,
            design_options = designOptions,
            imported_cad = importedCad,
            linked_cad = imports.Count - importedCad,
            revit_links = links.Count,
            unloaded_links = unloadedLinks,
            rooms = rooms.Count,
            unplaced_rooms = unplacedRooms,
            unenclosed_rooms = unenclosedRooms,
            issues,
        };
    }
}

public sealed class ListRevitLinks : RevitCommandBase
{
    public override string Name => "list_revit_links";
    public override string Category => "QA-QC";
    public override string Description =>
        "Danh sách model link (cả IFC): id, trạng thái nạp, đường dẫn, gốc đặt, số phần tử.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "count_elements": { "type": "boolean", "description": "Đếm phần tử, mặc định true." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var count = Args.GetBoolOrDefault(arguments, "count_elements", true);
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Table.Of(links.Select(l =>
        {
            var linkDoc = l.GetLinkDocument();
            var path = PathOf(doc, l.GetTypeId());
            return new
            {
                link_instance_id = l.Id.Value,
                link_type_id = l.GetTypeId().Value,
                name = l.Name,
                loaded = linkDoc != null,
                is_ifc = path?.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase) == true
                         || path?.Contains(".ifc.", StringComparison.OrdinalIgnoreCase) == true,
                path,
                pinned = l.Pinned,
                origin_mm = DocLookup.PointMm(l.GetTotalTransform().Origin),
                elements = count && linkDoc != null ? new FilteredElementCollector(linkDoc).WhereElementIsNotElementType().GetElementCount() : (int?)null,
            };
        }).ToList());
    }

    internal static string? PathOf(Document doc, ElementId typeId)
    {
        try
        {
            if (doc.GetElement(typeId) is not RevitLinkType type || !type.IsExternalFileReference()) return null;
            var reference = type.GetExternalFileReference();
            return ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }
}

public sealed class GetLinkedElementInfo : RevitCommandBase
{
    public override string Name => "get_linked_element_info";
    public override string Category => "QA-QC";
    public override string Description =>
        "Thông tin phần tử trong model link: id, UniqueId, category, type, vị trí (mm), tham số.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "link_instance_id": { "type": "integer", "description": "Id RevitLinkInstance (xem list_revit_links)." },
            "linked_element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Id phần tử bên trong link." },
            "include_parameters": { "type": "boolean", "description": "Kèm tham số." }
          },
          "required": ["link_instance_id", "linked_element_ids"]
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var selection = context.UIDocument.Selection;
        IList<Reference> picked;
        try
        {
            picked = selection.PickObjects(ObjectType.LinkedElement, "Chọn phần tử bên trong model link (có thể chọn nhiều, cùng một link) rồi bấm Finish");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            throw new OperationCanceledException();
        }
        var valid = picked.Where(r => r.LinkedElementId != ElementId.InvalidElementId).ToList();
        if (valid.Count == 0) throw new OperationCanceledException();
        var linkId = valid[0].ElementId;
        return new
        {
            link_instance_id = linkId.Value,
            linked_element_ids = valid.Where(r => r.ElementId == linkId).Select(r => r.LinkedElementId.Value).Distinct().ToList(),
            include_parameters = valid.Count == 1,
        };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var linkId = new ElementId(Args.GetLong(arguments, "link_instance_id"));
        var link = doc.GetElement(linkId) as RevitLinkInstance
                   ?? throw new CommandException($"Id {linkId.Value} không phải RevitLinkInstance. Dùng list_revit_links để xem id.");
        var linkDoc = link.GetLinkDocument() ?? throw new CommandException($"Link '{link.Name}' chưa được nạp (Unloaded).");
        var transform = link.GetTotalTransform();
        var withParameters = Args.GetBoolOrDefault(arguments, "include_parameters", false);

        var rows = new List<object>();
        var missing = new List<long>();
        foreach (var id in Args.GetElementIds(arguments, "linked_element_ids"))
        {
            var e = linkDoc.GetElement(id);
            if (e == null) { missing.Add(id.Value); continue; }
            XYZ? location = e.Location switch
            {
                LocationPoint lp => lp.Point,
                LocationCurve { Curve: { } c } => c.Evaluate(0.5, true),
                _ => e.get_BoundingBox(null) is { } b ? (b.Min + b.Max) / 2 : null,
            };
            rows.Add(new
            {
                linked_element_id = e.Id.Value,
                unique_id = e.UniqueId,
                category = e.Category?.Name,
                name = e.Name,
                type_name = ElementInfo.TypeName(linkDoc, e),
                level = ElementInfo.LevelName(linkDoc, e),
                location_mm = location == null ? null : DocLookup.PointMm(transform.OfPoint(location)),
                parameters = withParameters ? Parameters(e) : null,
            });
        }

        return new
        {
            link_instance_id = link.Id.Value,
            link = link.Name,
            link_file = linkDoc.Title,
            elements = rows,
            not_found = missing.Count == 0 ? null : missing,
        };
    }

    private static Dictionary<string, string> Parameters(Element e)
    {
        var result = new Dictionary<string, string>();
        foreach (var p in e.GetOrderedParameters())
        {
            if (!p.HasValue) continue;
            string? text;
            try { text = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
            if (!string.IsNullOrWhiteSpace(text)) result.TryAdd(p.Definition.Name, text);
        }
        return result;
    }
}
