using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class FindElements : RevitCommandBase
{
    public override string Name => "find_elements";
    public override string Category => "Truy vấn";
    public override bool Pinned => true;
    public override string Description =>
        "Tìm phần tử theo category, tầng, view đang mở và/hoặc giá trị tham số. " +
        "Dùng trước khi sửa/chọn phần tử. output=ids chỉ trả id (rẻ nhất, dùng để chuyển sang lệnh khác), count chỉ trả số lượng.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "BuiltInCategory (OST_Walls, OST_StructuralColumns, OST_Rebar...) hoặc tên category hiển thị." },
            "level": { "type": "string", "description": "Tên tầng (Level) mà phần tử thuộc về." },
            "in_active_view": { "type": "boolean", "description": "Chỉ lấy phần tử thấy trong view đang mở. Mặc định false." },
            "parameter": { "type": "string", "description": "Tên tham số để lọc." },
            "equals": { "type": "string", "description": "Giá trị tham số phải bằng (so theo chuỗi hiển thị, không phân biệt hoa thường)." },
            "contains": { "type": "string", "description": "Giá trị tham số phải chứa chuỗi này." },
            "output": { "type": "string", "enum": ["table", "ids", "count"], "description": "Mặc định table." },
            "limit": { "type": "integer", "description": "Số phần tử tối đa trả về, mặc định 200." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var collector = Args.GetBoolOrDefault(arguments, "in_active_view", false)
            ? new FilteredElementCollector(doc, doc.ActiveView.Id)
            : new FilteredElementCollector(doc);
        collector.WhereElementIsNotElementType();

        if (Args.GetStringOrDefault(arguments, "category") is { } categoryName)
            collector.OfCategoryId(CategoryResolver.Resolve(doc, categoryName).Id);

        IEnumerable<Element> elements = collector;

        if (Args.GetStringOrDefault(arguments, "level") is { } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            elements = elements.Where(e => ElementInfo.LevelName(doc, e) == level.Name);
        }

        if (Args.GetStringOrDefault(arguments, "parameter") is { } parameterName)
        {
            var equals = Args.GetStringOrDefault(arguments, "equals");
            var contains = Args.GetStringOrDefault(arguments, "contains");
            elements = elements.Where(e => Matches(e.LookupParameter(parameterName), equals, contains));
        }

        if (!Args.Has(arguments, "category") && !Args.Has(arguments, "in_active_view") && !Args.Has(arguments, "level"))
            elements = elements.Where(ElementInfo.IsPhysical);

        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);
        var list = elements.ToList();
        return Args.GetStringOrDefault(arguments, "output", "table") switch
        {
            "count" => new { total = list.Count },
            "ids" => new { total = list.Count, element_ids = list.Take(limit).Select(e => e.Id.Value) },
            _ => Table.Of(list.Select(e => ElementInfo.Summary(doc, e)), limit),
        };
    }

    private static bool Matches(Parameter? parameter, string? equals, string? contains)
    {
        if (parameter == null) return false;
        var value = (parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString()) ?? "";
        if (equals != null && !string.Equals(value, equals, StringComparison.OrdinalIgnoreCase)) return false;
        if (contains != null && value.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }
}

public sealed class ListViews : RevitCommandBase
{
    public override string Name => "list_views";
    public override string Category => "Truy vấn";
    public override string Description =>
        "Danh sách view trong dự án (bỏ qua view template). Lọc được theo loại view: FloorPlan, CeilingPlan, Section, Elevation, ThreeD, DraftingView, Schedule, Legend...";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "view_type": { "type": "string", "description": "Loại view (tên enum ViewType), ví dụ FloorPlan." },
            "name_contains": { "type": "string", "description": "Tên view chứa chuỗi này." },
            "limit": { "type": "integer", "description": "Mặc định 300." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var views = new FilteredElementCollector(context.Document).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType != ViewType.Internal && v.ViewType != ViewType.ProjectBrowser
                        && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.DrawingSheet);

        if (Args.GetStringOrDefault(arguments, "view_type") is { } typeName)
        {
            if (!Enum.TryParse<ViewType>(typeName, true, out var viewType))
                throw new CommandException($"Loại view '{typeName}' không hợp lệ.");
            views = views.Where(v => v.ViewType == viewType);
        }
        if (Args.GetStringOrDefault(arguments, "name_contains") is { } part)
            views = views.Where(v => v.Name.Contains(part, StringComparison.OrdinalIgnoreCase));

        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 300);
        return Table.Of(views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).Select(v => new
        {
            element_id = v.Id.Value,
            name = v.Name,
            view_type = v.ViewType.ToString(),
            level = v.GenLevel?.Name,
            scale = v.ViewType is ViewType.Schedule or ViewType.ThreeD ? (int?)null : v.Scale,
        }), limit);
    }
}

public sealed class ListSheets : RevitCommandBase
{
    public override string Name => "list_sheets";
    public override string Category => "Truy vấn";
    public override string Description => "Danh sách sheet: số hiệu, tên, và các view đặt trên sheet.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        return Table.Of(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber)
            .Select(s => new
            {
                element_id = s.Id.Value,
                sheet_number = s.SheetNumber,
                name = s.Name,
                views = s.GetAllPlacedViews().Select(id => doc.GetElement(id)?.Name),
            }));
    }
}
