using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class SetParameters : RevitCommandBase
{
    public override string Name => "set_parameters";
    public override string Category => "Tham số";
    public override string Description =>
        "Gán cùng một giá trị cho một tham số trên nhiều phần tử. Giá trị số có đơn vị (chiều dài, diện tích...) được hiểu " +
        "theo đơn vị hiển thị của dự án, ví dụ \"3000\" với dự án dùng mm. Phần tử nào lỗi thì bỏ qua và báo lại, phần còn lại vẫn được sửa.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" } },
            "parameter": { "type": "string", "description": "Tên tham số (instance hoặc type)." },
            "value": { "type": ["string", "number", "boolean"], "description": "Giá trị mới." },
            "on_type": { "type": "boolean", "description": "Sửa tham số trên type của phần tử thay vì instance. Mặc định false." }
          },
          "required": ["element_ids", "parameter", "value"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var name = Args.GetString(arguments, "parameter");
        var value = arguments.GetProperty("value");
        var onType = Args.GetBoolOrDefault(arguments, "on_type", false);

        var updated = new List<long>();
        var failed = new List<object>();
        var touchedTypes = new HashSet<long>();

        foreach (var id in Args.GetElementIds(arguments, "element_ids"))
        {
            var target = doc.GetElement(id);
            if (target != null && onType) target = doc.GetElement(target.GetTypeId());
            if (target == null)
            {
                failed.Add(new { element_id = id.Value, error = "Không tìm thấy phần tử." });
                continue;
            }
            if (onType && !touchedTypes.Add(target.Id.Value))
            {
                updated.Add(id.Value);
                continue;
            }

            var error = TrySet(target.LookupParameter(name), value);
            if (error == null) updated.Add(id.Value);
            else failed.Add(new { element_id = id.Value, error });
        }

        return new { updated = updated.Count, failed };
    }

    private static string? TrySet(Parameter? parameter, JsonElement value)
    {
        if (parameter == null) return "Không có tham số này.";
        if (parameter.IsReadOnly) return "Tham số chỉ đọc.";

        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        var ok = parameter.StorageType switch
        {
            StorageType.String => parameter.Set(text),
            StorageType.Integer => value.ValueKind switch
            {
                JsonValueKind.True => parameter.Set(1),
                JsonValueKind.False => parameter.Set(0),
                _ => int.TryParse(text, out var n) ? parameter.Set(n) : parameter.SetValueString(text),
            },
            StorageType.Double => parameter.SetValueString(text),
            StorageType.ElementId => long.TryParse(text, out var eid) && parameter.Set(new ElementId(eid)),
            _ => false,
        };
        return ok ? null : $"Không gán được giá trị '{text}' (kiểu {parameter.StorageType}).";
    }
}
