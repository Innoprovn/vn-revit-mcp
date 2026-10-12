using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

public sealed class CreateSheets : RevitCommandBase
{
    public override string Name => "create_sheets";
    public override string Category => "Bản vẽ";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Tạo sheet", "▭");
    public override string Description =>
        "Tạo hàng loạt sheet với khung tên chọn trước, theo danh sách số hiệu/tên hoặc số hiệu tự tăng.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "title_block_type": { "type": "string", "description": "Trống = khung tên đầu tiên." },
            "sheets": {
              "type": "array",
              "description": "Danh sách sheet cần tạo.",
              "items": { "type": "object", "properties": { "number": { "type": "string" }, "name": { "type": "string" } }, "required": ["number"] }
            },
            "start_number": { "type": "string", "description": "Số hiệu đầu, tự tăng (A101...)." },
            "count": { "type": "integer", "description": "Số sheet, mặc định 1." },
            "name": { "type": "string", "description": "Tên sheet." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var titleBlockId = ElementId.InvalidElementId;
        if (Args.GetStringOrDefault(arguments, "title_block_type") is { } tbName && !string.IsNullOrWhiteSpace(tbName))
            titleBlockId = Lookup.Type<FamilySymbol>(doc, tbName, BuiltInCategory.OST_TitleBlocks).Id;
        else if (new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElementId() is { } first)
            titleBlockId = first;

        var wanted = new List<(string Number, string Name)>();
        if (Args.Has(arguments, "sheets"))
        {
            foreach (var item in Args.GetArray(arguments, "sheets"))
            {
                if (item.ValueKind == JsonValueKind.String) { wanted.Add((item.GetString()!, "Chưa đặt tên")); continue; }
                wanted.Add((Args.GetString(item, "number"), Args.GetStringOrDefault(item, "name") ?? "Chưa đặt tên"));
            }
        }
        else if (Args.GetStringOrDefault(arguments, "start_number") is { } start)
        {
            var count = (int)Args.GetLongOrDefault(arguments, "count", 1);
            if (count is < 1 or > 500) throw new CommandException("'count' phải từ 1 đến 500.");
            var name = Args.GetStringOrDefault(arguments, "name") ?? "Chưa đặt tên";
            wanted.AddRange(Increment(start.Trim(), count).Select(n => (n, name)));
        }
        else
        {
            throw new CommandException("Cần 'sheets' (danh sách {number, name}) hoặc 'start_number' + 'count'.");
        }

        var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = new List<object>();
        var skipped = new List<object>();
        foreach (var (number, name) in wanted)
        {
            if (string.IsNullOrWhiteSpace(number) || !existing.Add(number.Trim()))
            {
                skipped.Add(new { number, reason = "Số hiệu trống hoặc đã tồn tại" });
                continue;
            }
            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = number.Trim();
            sheet.Name = name;
            created.Add(new { element_id = sheet.Id.Value, sheet_number = sheet.SheetNumber, name = sheet.Name });
        }

        return new
        {
            title_block = titleBlockId == ElementId.InvalidElementId ? null : Lookup.FullName((ElementType)doc.GetElement(titleBlockId)),
            created = Table.Of(created),
            skipped = skipped.Count == 0 ? null : Table.Of(skipped),
        };
    }

    /// <summary>"A101", 3 → A101, A102, A103 (giữ số chữ số); không có số ở cuối thì thêm -1, -2...</summary>
    private static IEnumerable<string> Increment(string start, int count)
    {
        var match = Regex.Match(start, @"^(.*?)(\d+)(\D*)$");
        if (!match.Success)
        {
            yield return start;
            for (var i = 1; i < count; i++) yield return $"{start}-{i}";
            yield break;
        }
        var prefix = match.Groups[1].Value;
        var digits = match.Groups[2].Value;
        var suffix = match.Groups[3].Value;
        var value = long.Parse(digits);
        for (var i = 0; i < count; i++)
            yield return prefix + (value + i).ToString().PadLeft(digits.Length, '0') + suffix;
    }
}

public sealed class PlaceViewsOnSheet : RevitCommandBase
{
    private const double GapFeet = 10 / 304.8;

    public override string Name => "place_views_on_sheet";
    public override string Category => "Bản vẽ";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Đặt view", "⊞");
    public override string Description =>
        "Đặt view hoặc schedule lên sheet, tự xếp trong khung tên hoặc theo tọa độ.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "sheet": { "type": "string", "description": "Số hiệu hoặc tên sheet." },
            "views": { "type": "array", "items": { "type": "string" }, "description": "Tên view. Trống = view đang mở." },
            "view_ids": { "type": "array", "items": { "type": "integer" }, "description": "Id view (thay cho tên)." },
            "points": {
              "type": "array",
              "description": "Tâm từng view (mm). Trống = tự xếp.",
              "items": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } }
            }
          },
          "required": ["sheet"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var sheet = DocLookup.Sheet(doc, Args.GetString(arguments, "sheet"));
        var views = DocLookup.Views(doc, arguments);
        var points = Args.Has(arguments, "points")
            ? Args.GetArray(arguments, "points").Select(p => (XYZ?)new XYZ(
                UnitConvert.MmToFeet(Args.GetDoubleOrDefault(p, "x", 0)),
                UnitConvert.MmToFeet(Args.GetDoubleOrDefault(p, "y", 0)), 0)).ToList()
            : [];

        var placed = new List<(View View, Element Instance)>();
        var failed = new List<object>();
        foreach (var view in views)
        {
            if (view.Id == sheet.Id || view is ViewSheet)
            {
                failed.Add(new { view = view.Name, reason = "Không đặt sheet lên sheet." });
                continue;
            }
            try
            {
                Element instance;
                if (view is ViewSchedule schedule)
                {
                    if (schedule.IsTitleblockRevisionSchedule || schedule.IsInternalKeynoteSchedule)
                        throw new CommandException("Schedule nội bộ của Revit, không đặt được.");
                    instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, XYZ.Zero);
                }
                else
                {
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                        throw new CommandException("Revit không cho đặt (view đã nằm trên sheet khác, là template, hoặc view trống).");
                    instance = Viewport.Create(doc, sheet.Id, view.Id, XYZ.Zero);
                }
                placed.Add((view, instance));
            }
            catch (Exception ex) when (ex is CommandException or Autodesk.Revit.Exceptions.ApplicationException)
            {
                failed.Add(new { view = view.Name, reason = ex.Message });
            }
        }

        if (placed.Count > 0)
        {
            doc.Regenerate(); // cần kích thước thật của viewport trước khi xếp
            if (points.Count > 0) PlaceAtPoints(placed, points);
            else AutoLayout(doc, sheet, placed);
        }

        return new
        {
            sheet = $"{sheet.SheetNumber} - {sheet.Name}",
            placed = Table.Of(placed.Select(p => new
            {
                view = p.View.Name,
                view_type = p.View.ViewType.ToString(),
                element_id = p.Instance.Id.Value,
            }).ToList()),
            failed = failed.Count == 0 ? null : Table.Of(failed),
        };
    }

    private static void PlaceAtPoints(List<(View View, Element Instance)> placed, List<XYZ?> points)
    {
        for (var i = 0; i < placed.Count && i < points.Count; i++)
        {
            if (points[i] is not { } p) continue;
            Move(placed[i].Instance, new XYZ(p.X, p.Y, 0), placed[i].View);
        }
    }

    /// <summary>Xếp theo hàng từ góc trên trái vùng vẽ, xuống hàng khi hết chiều ngang.</summary>
    private static void AutoLayout(Document doc, ViewSheet sheet, List<(View View, Element Instance)> placed)
    {
        double minX, minY, maxX, maxY;
        if (DocLookup.TitleBlock(doc, sheet)?.get_BoundingBox(sheet) is { } box)
        {
            (minX, minY, maxX, maxY) = (box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
        }
        else
        {
            var outline = sheet.Outline;
            (minX, minY, maxX, maxY) = (outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);
        }
        // Chừa lề và dải khung tên bên phải (khoảng 20% bề ngang) cho khung tên đứng phổ biến.
        var margin = 15 / 304.8;
        var left = minX + margin;
        var right = maxX - Math.Max(margin, (maxX - minX) * 0.2);
        var top = maxY - margin;

        var x = left;
        var y = top;
        var rowHeight = 0.0;
        foreach (var (view, instance) in placed)
        {
            var (w, h) = Size(view, instance);
            if (x > left && x + w > right)
            {
                x = left;
                y -= rowHeight + GapFeet;
                rowHeight = 0;
            }
            Move(instance, new XYZ(x + w / 2, y - h / 2, 0), view);
            x += w + GapFeet;
            rowHeight = Math.Max(rowHeight, h);
        }
    }

    private static (double W, double H) Size(View view, Element instance)
    {
        if (instance is Viewport vp)
        {
            var o = vp.GetBoxOutline();
            return (o.MaximumPoint.X - o.MinimumPoint.X, o.MaximumPoint.Y - o.MinimumPoint.Y);
        }
        var box = instance.get_BoundingBox(instance.Document.GetElement(instance.OwnerViewId) as View);
        return box == null ? (0.3, 0.3) : (box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
    }

    private static void Move(Element instance, XYZ center, View view)
    {
        if (instance is Viewport vp)
        {
            vp.SetBoxCenter(center);
            return;
        }
        // Schedule: Point là góc trên trái, đổi từ tâm sang góc.
        var (w, h) = Size(view, instance);
        var schedule = (ScheduleSheetInstance)instance;
        schedule.Point = new XYZ(center.X - w / 2, center.Y + h / 2, 0);
    }
}

public sealed class DuplicateViews : RevitCommandBase
{
    public override string Name => "duplicate_views";
    public override string Category => "Bản vẽ";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Nhân bản view: chỉ hình, kèm ghi chú hoặc view phụ thuộc, đặt được tiền tố/hậu tố tên.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "views": { "type": "array", "items": { "type": "string" }, "description": "Trống = view đang mở." },
            "view_ids": { "type": "array", "items": { "type": "integer" } },
            "mode": { "type": "string", "enum": ["duplicate", "with_detailing", "as_dependent"], "description": "Mặc định with_detailing." },
            "copies": { "type": "integer", "description": "Số bản mỗi view, mặc định 1." },
            "name_prefix": { "type": "string" },
            "name_suffix": { "type": "string", "description": "Ví dụ \" - THÉP\"." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var views = DocLookup.Views(doc, arguments);
        var option = DocLookup.Choice(arguments, "mode", "with_detailing", "duplicate", "with_detailing", "as_dependent") switch
        {
            "duplicate" => ViewDuplicateOption.Duplicate,
            "as_dependent" => ViewDuplicateOption.AsDependent,
            _ => ViewDuplicateOption.WithDetailing,
        };
        var copies = (int)Args.GetLongOrDefault(arguments, "copies", 1);
        if (copies is < 1 or > 50) throw new CommandException("'copies' phải từ 1 đến 50.");
        var prefix = Args.GetStringOrDefault(arguments, "name_prefix") ?? "";
        var suffix = Args.GetStringOrDefault(arguments, "name_suffix") ?? "";

        var usedNames = DocLookup.AllViews(doc).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<object>();
        var failed = new List<object>();
        foreach (var view in views)
        {
            if (!view.CanViewBeDuplicated(option))
            {
                failed.Add(new { view = view.Name, reason = $"Loại view {view.ViewType} không nhân bản được theo kiểu này." });
                continue;
            }
            for (var i = 1; i <= copies; i++)
            {
                var copy = (View)doc.GetElement(view.Duplicate(option));
                if (prefix.Length > 0 || suffix.Length > 0)
                {
                    var name = prefix + view.Name + suffix + (copies > 1 ? $" {i}" : "");
                    var unique = name;
                    for (var n = 2; usedNames.Contains(unique); n++) unique = $"{name} ({n})";
                    copy.Name = unique;
                }
                usedNames.Add(copy.Name);
                created.Add(new { source = view.Name, element_id = copy.Id.Value, name = copy.Name });
            }
        }

        return new { created = Table.Of(created), failed = failed.Count == 0 ? null : Table.Of(failed) };
    }
}

public sealed class ApplyViewTemplate : RevitCommandBase
{
    public override string Name => "apply_view_template";
    public override string Category => "Bản vẽ";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Gán, áp một lần hoặc gỡ view template cho các view.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "view_template": { "type": "string", "description": "Tên view template." },
            "views": { "type": "array", "items": { "type": "string" }, "description": "Trống = view đang mở." },
            "view_ids": { "type": "array", "items": { "type": "integer" } },
            "view_type": { "type": "string", "description": "Mọi view loại này (FloorPlan...)." },
            "name_contains": { "type": "string", "description": "Lọc tên, dùng với view_type." },
            "apply_once": { "type": "boolean", "description": "Chỉ áp một lần, không gán." },
            "remove": { "type": "boolean", "description": "Gỡ template." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var remove = Args.GetBoolOrDefault(arguments, "remove", false);
        var applyOnce = Args.GetBoolOrDefault(arguments, "apply_once", false);

        View? template = null;
        if (!remove)
        {
            var name = Args.GetStringOrDefault(arguments, "view_template")
                       ?? throw new CommandException("Thiếu 'view_template' (hoặc dùng remove=true để gỡ template).");
            var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
            template = templates.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new CommandException($"Không có view template '{name}'. Các template: {string.Join(", ", templates.Select(t => t.Name).OrderBy(n => n).Take(30))}.");
        }

        List<View> views;
        if (Args.GetStringOrDefault(arguments, "view_type") is { } typeName)
        {
            if (!Enum.TryParse<ViewType>(typeName, true, out var viewType))
                throw new CommandException($"Loại view '{typeName}' không hợp lệ.");
            var contains = Args.GetStringOrDefault(arguments, "name_contains");
            views = DocLookup.AllViews(doc).Where(v => v.ViewType == viewType && (contains == null || v.Name.Contains(contains, StringComparison.OrdinalIgnoreCase))).ToList();
            views.AddRange(DocLookup.Views(doc, arguments, defaultActive: false));
            views = views.DistinctBy(v => v.Id.Value).ToList();
        }
        else
        {
            views = DocLookup.Views(doc, arguments);
        }

        var done = new List<object>();
        var failed = new List<object>();
        foreach (var view in views)
        {
            try
            {
                if (remove)
                {
                    view.ViewTemplateId = ElementId.InvalidElementId;
                }
                else if (!view.IsValidViewTemplate(template!.Id))
                {
                    failed.Add(new { view = view.Name, reason = $"Template không hợp với loại view {view.ViewType}." });
                    continue;
                }
                else if (applyOnce)
                {
                    view.ApplyViewTemplateParameters(template);
                }
                else
                {
                    view.ViewTemplateId = template.Id;
                }
                done.Add(new { element_id = view.Id.Value, view = view.Name, view_type = view.ViewType.ToString() });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                failed.Add(new { view = view.Name, reason = ex.Message });
            }
        }

        return new
        {
            template = template?.Name,
            action = remove ? "remove" : applyOnce ? "apply_once" : "assign",
            views = Table.Of(done),
            failed = failed.Count == 0 ? null : Table.Of(failed),
        };
    }
}
