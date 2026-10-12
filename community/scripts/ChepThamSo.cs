using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

// Chép giá trị từ một tham số sang tham số khác trên nhiều phần tử (ví dụ Mark -> Comments).
public sealed class CopyParameterValues : RevitCommandBase
{
    public override string Name => "copy_parameter_values";
    public override string Category => "Tham số";
    public override string Description =>
        "Chép giá trị từ tham số nguồn sang tham số đích trên các phần tử đang chọn (hoặc theo danh sách id). " +
        "Dùng giá trị hiển thị dạng chuỗi; tham số đích phải ghi được.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Phần tử cần chép." },
            "source": { "type": "string", "description": "Tên tham số nguồn, ví dụ Mark." },
            "target": { "type": "string", "description": "Tên tham số đích, ví dụ Comments." },
            "skip_empty": { "type": "boolean", "description": "Bỏ qua phần tử có tham số nguồn trống. Mặc định true." }
          },
          "required": ["element_ids", "source", "target"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton Ribbon => new("Chép\ntham số", "⇉");

    public override object Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var source = Args.GetString(arguments, "source");
        var target = Args.GetString(arguments, "target");
        var skipEmpty = Args.GetBoolOrDefault(arguments, "skip_empty", true);

        var copied = 0;
        var failed = new List<object>();
        foreach (var id in Args.GetElementIds(arguments, "element_ids"))
        {
            var e = doc.GetElement(id);
            var from = e?.LookupParameter(source);
            var to = e?.LookupParameter(target);
            if (e == null || from == null || to == null)
            {
                failed.Add(new { element_id = id.Value, error = e == null ? "Không có phần tử" : "Thiếu tham số nguồn hoặc đích" });
                continue;
            }
            var value = from.StorageType == StorageType.String ? from.AsString() : from.AsValueString();
            if (string.IsNullOrEmpty(value) && skipEmpty) continue;
            if (to.IsReadOnly) { failed.Add(new { element_id = id.Value, error = "Tham số đích chỉ đọc" }); continue; }

            var ok = to.StorageType == StorageType.String ? to.Set(value ?? "") : to.SetValueString(value ?? "");
            if (ok) copied++;
            else failed.Add(new { element_id = id.Value, error = $"Không gán được '{value}'" });
        }
        return new { copied, failed = Table.Of(failed, 50) };
    }
}
