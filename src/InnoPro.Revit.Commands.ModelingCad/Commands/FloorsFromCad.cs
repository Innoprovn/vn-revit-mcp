using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Commands.ModelingCad.FloorFromCad;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

public sealed class CreateFloorsFromCad : RevitCommandBase
{
    public override string Name => "create_floors_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Sàn/trần CAD", "▤");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Vẽ sàn hoặc trần theo vùng hatch trên layer CAD. Tên vùng đọc từ Text Note nằm trong hatch để chọn type.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer hatch." },
            "kind": { "type": "string", "enum": ["floor", "ceiling"], "description": "Mặc định floor." },
            "level": { "type": "string", "description": "Mặc định tầng của view." },
            "floor_type": { "type": "string", "description": "Type cho vùng chưa khớp tên." },
            "type_map": { "type": "object", "description": "Tên vùng → type." },
            "new_type_base": { "type": "string", "description": "Type gốc để nhân bản type mới." },
            "new_type_thickness": { "type": "object", "description": "Tên vùng → bề dày mm (tạo type mới)." },
            "offset_mm": { "type": "number", "description": "Cao độ so với tầng." },
            "offsets": { "type": "object", "description": "Tên vùng → cao độ mm." },
            "edge_offset_mm": { "type": "number", "description": "Lùi mép vào trong." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào vùng hatch sàn trong CAD");
        return new { cad_id = cad.Id.Value, layers };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var level = Cad.LevelOrView(doc, arguments, "level", view);
        var kind = ArgsX.Enum(arguments, "kind", "floor", "floor", "ceiling") == "ceiling" ? SurfaceKind.Ceiling : SurfaceKind.Floor;

        var scan = CadFloorScanner.Scan(doc, cad, view);
        var layers = Cad.Layers(arguments, scan.RegionsByLayer.Keys);
        var regions = layers.SelectMany(l => scan.RegionsByLayer[l]).ToList();
        if (regions.Count == 0) throw new CommandException("Layer đã chọn không có vùng hatch nào.");

        var textNotes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
        var types = (kind == SurfaceKind.Ceiling
                ? new FilteredElementCollector(doc).OfClass(typeof(CeilingType))
                : new FilteredElementCollector(doc).OfClass(typeof(FloorType)).OfCategory(BuiltInCategory.OST_Floors))
            .Cast<HostObjAttributes>().ToList();
        if (types.Count == 0) throw new CommandException(kind == SurfaceKind.Ceiling ? "Dự án chưa có Ceiling Type." : "Dự án chưa có Floor Type.");

        var detected = regions.Select(r => (Region: r, TagName: TextTagReader.FindTagNameInside(r, textNotes))).ToList();
        var groups = FloorNameGrouper.Build(detected, types);

        HostObjAttributes FindType(string name) =>
            types.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new CommandException($"Không có type '{name}'. Type có: {Cad.Suggest(types.Select(t => t.Name))}.");

        var typeMap = ArgsX.StringMap(arguments, "type_map");
        var thicknessMap = ArgsX.NumberMap(arguments, "new_type_thickness");
        var offsets = ArgsX.NumberMap(arguments, "offsets");
        var fallback = Args.GetStringOrDefault(arguments, "floor_type") is { Length: > 0 } f ? FindType(f) : null;
        var baseType = Args.GetStringOrDefault(arguments, "new_type_base") is { Length: > 0 } b ? FindType(b) : fallback ?? types[0];
        var offset = Args.GetDoubleOrDefault(arguments, "offset_mm", 0);
        var edge = Args.GetDoubleOrDefault(arguments, "edge_offset_mm", 0);

        foreach (var group in groups)
        {
            if (typeMap.TryGetValue(group.Name, out var mapped))
                group.SelectedChoice = new FloorTypeChoice(FindType(mapped), mapped);
            else if (thicknessMap.TryGetValue(group.Name, out var thickness))
            {
                group.SelectedChoice = null;
                group.DuplicateBaseType = baseType;
                group.CustomThicknessMm = thickness;
            }
            else if (group.SelectedChoice == null && fallback != null)
                group.SelectedChoice = new FloorTypeChoice(fallback, fallback.Name);

            group.OffsetMm = offsets.TryGetValue(group.Name, out var o) ? o : offset;
            group.EdgeOffsetMm = edge;
        }

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
        {
            return new
            {
                cad_id = cad.Id.Value, regions = regions.Count,
                groups = Table.Of(groups.Select(g => new
                {
                    name = g.Name, regions = g.Count, auto_matched = g.AutoMatched,
                    type = g.SelectedChoice?.Type.Name, new_thickness_mm = g.SelectedChoice == null && g.DuplicateBaseType != null ? g.CustomThicknessMm : (double?)null,
                })),
            };
        }

        var results = FloorBuilder.Build(doc, level, groups, kind);
        return new
        {
            created = results.Count(r => r.Outcome == FloorPlacementOutcome.Created),
            skipped_no_type = results.Count(r => r.Outcome == FloorPlacementOutcome.SkippedNoType),
            failed = results.Count(r => r.Outcome == FloorPlacementOutcome.Failed),
            groups = Table.Of(groups.Select(g => new { name = g.Name, regions = g.Count, type = g.SelectedChoice?.Type.Name })),
            errors = results.Where(r => r.ErrorMessage != null).Select(r => r.ErrorMessage).Distinct().Take(10).ToList(),
        };
    }
}
