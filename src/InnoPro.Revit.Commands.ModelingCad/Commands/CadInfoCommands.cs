using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

public sealed class ListCadLinks : RevitCommandBase
{
    public override string Name => "list_cad_links";
    public override string Category => "Dựng hình CAD";
    public override string Description => "Liệt kê các file CAD đã Link/Import (id, tên, view chứa). Dùng id làm cad_id cho lệnh dựng hình từ CAD.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var visible = doc.ActiveView is { } view ? Cad.VisibleIn(doc, view) : [];
        return Table.Of(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .Select(i => new
            {
                cad_id = i.Id.Value,
                name = Cad.Describe(doc, i),
                linked = i.IsLinked,
                owner_view = i.ViewSpecific ? doc.GetElement(i.OwnerViewId)?.Name : null,
                in_active_view = visible.Contains(i.Id),
            }));
    }
}

public sealed class ListCadLayers : RevitCommandBase
{
    public override string Name => "list_cad_layers";
    public override string Category => "Dựng hình CAD";
    public override string Description => "Liệt kê layer của file CAD kèm số nét thẳng, polyline, cung, đường tròn, hatch.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "level": { "type": "string", "description": "Tầng để tìm mặt bằng đọc CAD." },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var stats = Cad.ScanLayers(doc, cad, view);
        return new
        {
            cad_id = cad.Id.Value,
            cad = Cad.Describe(doc, cad),
            view = view.Name,
            layers = Table.Of(stats.Select(s => new
            {
                layer = s.Name, lines = s.Lines, polylines = s.Polylines, arcs = s.Arcs,
                circles = s.Circles, other_curves = s.OtherCurves, hatches = s.Hatches,
            }), (int)Args.GetLongOrDefault(arguments, "limit", 200)),
        };
    }
}
