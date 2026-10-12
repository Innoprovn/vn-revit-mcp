using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class GetDocumentInfo : RevitCommandBase
{
    public override string Name => "get_document_info";
    public override string Category => "Truy vấn";
    public override string Description =>
        "Thông tin tài liệu Revit đang mở: tên, đường dẫn, phiên bản Revit, có worksharing không, view đang mở, số tầng.";

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
        "Thông tin view đang mở: id, tên, loại view, tỉ lệ, mức chi tiết, tầng gắn với view (nếu có).";

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
    public override string Description =>
        "Danh sách phần tử người dùng đang chọn trong Revit (id, category, tên, type, tầng). Trả tối đa 'limit' phần tử.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "description": "Số phần tử tối đa trả về, mặc định 200." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);
        var ids = context.UIDocument.Selection.GetElementIds();
        return new
        {
            total = ids.Count,
            elements = ids.Take(limit).Select(doc.GetElement).Where(e => e != null).Select(e => ElementInfo.Summary(doc, e)),
        };
    }
}

public sealed class GetElementParameters : RevitCommandBase
{
    public override string Name => "get_element_parameters";
    public override string Category => "Truy vấn";
    public override string Description =>
        "Đọc toàn bộ tham số của một phần tử theo id. Có thể kèm tham số của type. Chiều dài trả về dạng chuỗi theo đơn vị dự án.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_id": { "type": "integer", "description": "Id phần tử." },
            "include_type": { "type": "boolean", "description": "Kèm tham số của type, mặc định false." }
          },
          "required": ["element_id"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var id = new ElementId(Args.GetLong(arguments, "element_id"));
        var element = doc.GetElement(id) ?? throw new CommandException($"Không tìm thấy phần tử id {id.Value}.");
        var includeType = Args.GetBoolOrDefault(arguments, "include_type", false);
        var type = includeType ? doc.GetElement(element.GetTypeId()) : null;

        return new
        {
            element = ElementInfo.Summary(doc, element),
            instance_parameters = Read(element),
            type_parameters = type == null ? null : Read(type),
        };
    }

    private static IEnumerable<object> Read(Element e) => e.Parameters
        .Cast<Parameter>()
        .Where(p => p.Definition != null)
        .OrderBy(p => p.Definition.Name)
        .Select(p => new
        {
            name = p.Definition.Name,
            value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(),
            storage_type = p.StorageType.ToString(),
            read_only = p.IsReadOnly,
            shared = p.IsShared,
        });
}

public sealed class ListLevels : RevitCommandBase
{
    public override string Name => "list_levels";
    public override string Category => "Truy vấn";
    public override string Description => "Danh sách tầng (Level) theo cao độ tăng dần, cao độ tính bằng mm.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments) =>
        new FilteredElementCollector(context.Document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .Select(l => new { element_id = l.Id.Value, name = l.Name, elevation_mm = UnitConvert.FeetToMm(l.Elevation) });
}

public sealed class AnalyzeModelStatistics : RevitCommandBase
{
    public override string Name => "analyze_model_statistics";
    public override string Category => "Truy vấn";
    public override string Description =>
        "Thống kê mô hình: tổng số phần tử và số phần tử theo category (chỉ phần tử mô hình, bỏ qua type). Dùng để nắm nhanh quy mô dự án.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "top": { "type": "integer", "description": "Số category nhiều nhất cần liệt kê, mặc định 30." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var top = (int)Args.GetLongOrDefault(arguments, "top", 30);
        var groups = new FilteredElementCollector(context.Document)
            .WhereElementIsNotElementType()
            .Where(e => e.Category is { CategoryType: CategoryType.Model })
            .GroupBy(e => e.Category.Name)
            .Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        return new
        {
            total_model_elements = groups.Sum(g => g.count),
            category_count = groups.Count,
            categories = groups.Take(top),
        };
    }
}
