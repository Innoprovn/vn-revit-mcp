using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class GetDocumentInfo : RevitCommandBase
{
    public override string Name => "get_document_info";
    public override string Category => "Truy vấn";
    public override bool Pinned => true;
    public override string Description =>
        "Thông tin tài liệu Revit đang mở: tên, đường dẫn, phiên bản Revit, worksharing, view đang mở, số tầng.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var app = context.UIApplication.Application;
        return new
        {
            title = doc.Title,
            path = doc.PathName,
            is_family_document = doc.IsFamilyDocument,
            is_workshared = doc.IsWorkshared,
            revit_version = $"{app.VersionName} ({app.VersionBuild})",
            active_view = doc.ActiveView?.Name,
            level_count = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount(),
        };
    }
}

public sealed class GetCurrentViewInfo : RevitCommandBase
{
    public override string Name => "get_current_view_info";
    public override string Category => "Truy vấn";
    public override string Description =>
        "Thông tin view đang mở: id, tên, loại view, tỉ lệ, mức chi tiết, tầng gắn với view.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var view = context.Document.ActiveView ?? throw new CommandException("Không có view nào đang mở.");
        return new
        {
            element_id = view.Id.Value,
            name = view.Name,
            view_type = view.ViewType.ToString(),
            scale = view.Scale,
            detail_level = view.DetailLevel.ToString(),
            level = view.GenLevel?.Name,
            is_template = view.IsTemplate,
        };
    }
}

public sealed class GetSelectedElements : RevitCommandBase
{
    public override string Name => "get_selected_elements";
    public override string Category => "Truy vấn";
    public override bool Pinned => true;
    public override string Description => "Phần tử người dùng đang chọn trong Revit (id, category, tên, type, tầng).";
    public override string InputSchema => """
        {"type":"object","properties":{"limit":{"type":"integer","description":"Mặc định 200."}}}
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var elements = context.UIDocument.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null);
        return Table.Of(elements.Select(e => ElementInfo.Summary(doc, e)), (int)Args.GetLongOrDefault(arguments, "limit", 200));
    }
}

public sealed class GetElementParameters : RevitCommandBase
{
    public override string Name => "get_element_parameters";
    public override string Category => "Truy vấn";
    public override bool Pinned => true;
    public override string Description =>
        "Tham số của một phần tử (giá trị theo đơn vị hiển thị của dự án). Mặc định bỏ tham số trống; có thể kèm tham số type.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_id": { "type": "integer" },
            "include_type": { "type": "boolean", "description": "Kèm tham số của type. Mặc định false." },
            "include_empty": { "type": "boolean", "description": "Kèm tham số chưa có giá trị. Mặc định false." },
            "name_contains": { "type": "string", "description": "Chỉ lấy tham số có tên chứa chuỗi này." }
          },
          "required": ["element_id"]
        }
        """;
    public override RibbonButton? Ribbon => new("Tham số\nphần tử", "≣");

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var id = new ElementId(Args.GetLong(arguments, "element_id"));
        var element = doc.GetElement(id) ?? throw new CommandException($"Không tìm thấy phần tử id {id.Value}.");
        var includeEmpty = Args.GetBoolOrDefault(arguments, "include_empty", false);
        var part = Args.GetStringOrDefault(arguments, "name_contains");
        var type = Args.GetBoolOrDefault(arguments, "include_type", false) ? doc.GetElement(element.GetTypeId()) : null;

        Table Read(Element e) => Table.Of(e.Parameters.Cast<Parameter>()
            .Where(p => p.Definition != null && (part == null || p.Definition.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new { name = p.Definition.Name, value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(), read_only = p.IsReadOnly })
            .Where(p => includeEmpty || !string.IsNullOrEmpty(p.value))
            .OrderBy(p => p.name));

        return new
        {
            element = ElementInfo.Summary(doc, element),
            instance = Read(element),
            type = type == null ? null : Read(type),
        };
    }
}

public sealed class ListLevels : RevitCommandBase
{
    public override string Name => "list_levels";
    public override string Category => "Truy vấn";
    public override bool Pinned => true;
    public override string Description => "Danh sách tầng theo cao độ tăng dần (mm).";

    public override object? Execute(RevitCommandContext context, JsonElement arguments) =>
        Table.Of(new FilteredElementCollector(context.Document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => new { element_id = l.Id.Value, name = l.Name, elevation_mm = UnitConvert.FeetToMm(l.Elevation) }));
}

public sealed class AnalyzeModelStatistics : RevitCommandBase
{
    public override string Name => "analyze_model_statistics";
    public override string Category => "Truy vấn";
    public override string Description =>
        "Thống kê mô hình: tổng số cấu kiện và số lượng theo category. Dùng để nắm nhanh quy mô dự án.";
    public override string InputSchema => """
        {"type":"object","properties":{"top":{"type":"integer","description":"Số category nhiều nhất cần liệt kê, mặc định 30."}}}
        """;
    public override RibbonButton? Ribbon => new("Thống kê\nmô hình", "Σ");

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var groups = new FilteredElementCollector(context.Document)
            .WhereElementIsNotElementType()
            .Where(ElementInfo.IsPhysical)
            .GroupBy(e => e.Category.Name)
            .Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        return new
        {
            total_model_elements = groups.Sum(g => g.count),
            category_count = groups.Count,
            categories = Table.Of(groups, (int)Args.GetLongOrDefault(arguments, "top", 30)),
        };
    }
}
