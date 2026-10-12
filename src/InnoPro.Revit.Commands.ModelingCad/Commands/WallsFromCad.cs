using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Commands.ModelingCad.WallFromCad;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

public sealed class CreateWallsFromCad : RevitCommandBase
{
    public override string Name => "create_walls_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Tường CAD", "wall");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Vẽ tường kiến trúc từ cặp nét song song (hoặc cung đồng tâm) trên layer CAD. Wall Type chọn theo đúng bề dày đo được.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer tường." },
            "door_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer cửa để nối tường qua lỗ cửa." },
            "level": { "type": "string", "description": "Tầng chân tường; mặc định tầng của view." },
            "top_level": { "type": "string", "description": "Tầng đỉnh; bỏ trống thì dùng height_mm." },
            "height_mm": { "type": "number", "description": "Mặc định 3000." },
            "base_offset_mm": { "type": "number" },
            "top_offset_mm": { "type": "number" },
            "min_thickness_mm": { "type": "number", "description": "Mặc định 80." },
            "max_thickness_mm": { "type": "number", "description": "Mặc định 300." },
            "wall_types": { "type": "object", "description": "Bề dày → Wall Type, ví dụ {\"200\": \"Tường 200\"}." },
            "location_line": { "type": "string", "enum": ["wall_centerline", "core_centerline", "finish_exterior", "finish_interior", "core_exterior", "core_interior"] },
            "dimension_type": { "type": "string", "description": "Tạo Dimension tới Grid gần nhất." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích, không vẽ." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào nét tường trong CAD");
        var doors = CadPick.OptionalLayers(context, "Click nét cửa (nếu có) để nối tường qua cửa", cad);
        return new { cad_id = cad.Id.Value, layers, door_layers = doors };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var level = Cad.LevelOrView(doc, arguments, "level", view);
        var scan = CadWallScanner.Scan(doc, cad, view);
        var available = scan.SegmentsByLayer.Keys.Concat(scan.ArcsByLayer.Keys).ToList();
        var layers = Cad.Layers(arguments, available);
        var doorLayers = Cad.Layers(arguments, available, "door_layers", "door_layer", required: false);

        List<T> Gather<T>(IReadOnlyDictionary<string, List<T>> source, IEnumerable<string> names) =>
            names.SelectMany(n => source.TryGetValue(n, out var list) ? list : []).ToList();

        var minMm = Args.GetDoubleOrDefault(arguments, "min_thickness_mm", 80);
        var maxMm = Args.GetDoubleOrDefault(arguments, "max_thickness_mm", 300);
        if (maxMm <= minMm) throw new CommandException("max_thickness_mm phải lớn hơn min_thickness_mm.");

        var detection = WallPairing.Detect(
            Gather(scan.SegmentsByLayer, layers), Gather(scan.ArcsByLayer, layers), minMm, maxMm,
            Gather(scan.SegmentsByLayer, doorLayers), Gather(scan.ArcsByLayer, doorLayers));

        var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .Where(t => t.Kind == WallKind.Basic).ToList();
        var typeMap = ArgsX.StringMap(arguments, "wall_types");
        var buckets = detection.Candidates
            .GroupBy(c => (int)c.ThicknessMm)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var matching = wallTypes.Where(t => RoundedWidthMm(t) == g.Key).OrderBy(t => t.Name).ToList();
                var bucket = new ThicknessBucket { ThicknessMm = g.Key, Candidates = g.ToList(), MatchingWallTypes = matching };
                bucket.SelectedWallType = typeMap.TryGetValue(g.Key.ToString(), out var typeName)
                    ? Lookup.Type<WallType>(doc, typeName)
                    : matching.FirstOrDefault();
                return bucket;
            })
            .ToList();

        var summary = Table.Of(buckets.Select(b => new
        {
            thickness_mm = b.ThicknessMm,
            segments = b.Candidates.Count,
            length_m = Math.Round(UnitConvert.FeetToMm(b.Candidates.Sum(c => c.LengthFeet)) / 1000, 2),
            wall_type = b.SelectedWallType?.Name,
        }));

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
        {
            return new
            {
                cad_id = cad.Id.Value, layers, detected = detection.Candidates.Count,
                unpaired_lines = detection.UnresolvedLineCount, unpaired_arcs = detection.UnresolvedArcCount, groups = summary,
            };
        }

        var topLevel = Cad.LevelOrNull(doc, arguments, "top_level");
        var options = new WallBuildOptions
        {
            LocationLine = ArgsX.Enum(arguments, "location_line", "wall_centerline",
                    "wall_centerline", "core_centerline", "finish_exterior", "finish_interior", "core_exterior", "core_interior") switch
            {
                "core_centerline" => WallLocationLine.CoreCenterline,
                "finish_exterior" => WallLocationLine.FinishFaceExterior,
                "finish_interior" => WallLocationLine.FinishFaceInterior,
                "core_exterior" => WallLocationLine.CoreExterior,
                "core_interior" => WallLocationLine.CoreInterior,
                _ => WallLocationLine.WallCenterline,
            },
            BaseLevel = level,
            BaseOffsetMm = Args.GetDoubleOrDefault(arguments, "base_offset_mm", 0),
            TopMode = topLevel != null ? TopConstraintMode.UpToLevel : TopConstraintMode.Unconnected,
            TopLevel = topLevel,
            TopOffsetMm = Args.GetDoubleOrDefault(arguments, "top_offset_mm", 0),
            UnconnectedHeightMm = Args.GetDoubleOrDefault(arguments, "height_mm", 3000),
        };

        var dimensionType = Args.GetStringOrDefault(arguments, "dimension_type") is { Length: > 0 } dimName
            ? Lookup.Type<DimensionType>(doc, dimName)
            : null;
        var dimensionView = view is ViewPlan ? view : doc.ActiveView;
        var grids = new FilteredElementCollector(doc, dimensionView.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        var plan = new WallBuildPlan { LayerName = string.Join(", ", layers), Buckets = buckets, Options = options };
        var report = WallBuilder.Build(doc, dimensionView, plan, grids, dimensionType);

        return new
        {
            created = report.Created,
            skipped_no_type = report.SkippedNoType,
            failed = report.Failed,
            dimensions = report.GridDimensionsCreated,
            unpaired_lines = detection.UnresolvedLineCount,
            groups = summary,
            errors = report.Errors,
        };
    }

    private static int RoundedWidthMm(WallType type) =>
        (int)Math.Round(UnitConvert.FeetToMm(type.Width), MidpointRounding.AwayFromZero);
}
