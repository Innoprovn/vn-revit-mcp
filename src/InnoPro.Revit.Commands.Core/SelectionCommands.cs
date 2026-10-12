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

public sealed class OpenView : RevitCommandBase
{
    public override string Name => "open_view";
    public override string Category => "Giao diện";
    public override string Description =>
        "Mở (kích hoạt) một view theo tên hoặc id; hoặc mặt bằng của một tầng. Dùng trước các lệnh cần mặt bằng đang mở.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "view": { "type": "string", "description": "Tên view." },
            "view_id": { "type": "integer" },
            "level": { "type": "string", "description": "Tên tầng: mở mặt bằng đầu tiên của tầng đó." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType is not (ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal or ViewType.Undefined))
            .ToList();
        View? target = null;
        if (Args.Has(arguments, "view_id"))
            target = doc.GetElement(new ElementId(Args.GetLong(arguments, "view_id"))) as View;
        else if (Args.GetStringOrDefault(arguments, "view") is { Length: > 0 } name)
            target = views.FirstOrDefault(v => string.Equals(v.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? views.FirstOrDefault(v => v.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));
        else if (Args.GetStringOrDefault(arguments, "level") is { Length: > 0 } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            target = views.OfType<ViewPlan>().Where(v => v.GenLevel?.Id == level.Id)
                .OrderBy(v => v.ViewType switch { ViewType.FloorPlan => 0, ViewType.EngineeringPlan => 1, _ => 2 })
                .FirstOrDefault();
        }
        else throw new CommandException("Truyền view, view_id hoặc level.");

        if (target == null) throw new CommandException("Không tìm thấy view. Gọi list_views để xem tên đúng.");
        context.UIDocument.ActiveView = target;
        return new { view_id = target.Id.Value, name = target.Name, view_type = target.ViewType.ToString() };
    }
}
