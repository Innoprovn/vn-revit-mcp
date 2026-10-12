using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class SelectElements : RevitCommandBase
{
    public override string Name => "select_elements";
    public override string Category => "Giao diện";
    public override bool Pinned => true;
    public override string Description =>
        "Chọn các phần tử theo id trong Revit (thay cho vùng chọn hiện tại) và tùy chọn zoom tới chúng. Không sửa mô hình.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Danh sách id phần tử." },
            "zoom": { "type": "boolean", "description": "Zoom tới các phần tử đã chọn, mặc định true." }
          },
          "required": ["element_ids"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var requested = Args.GetElementIds(arguments, "element_ids");
        var found = requested.Where(id => doc.GetElement(id) != null).ToList();
        var missing = requested.Except(found).Select(id => id.Value).ToList();

        var uiDoc = context.UIDocument;
        uiDoc.Selection.SetElementIds(found);
        if (found.Count > 0 && Args.GetBoolOrDefault(arguments, "zoom", true))
            uiDoc.ShowElements(found);

        return new { selected = found.Count, missing_ids = missing };
    }
}
