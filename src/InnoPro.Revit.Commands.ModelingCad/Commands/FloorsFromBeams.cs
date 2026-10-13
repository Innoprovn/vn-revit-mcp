using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Commands.ModelingCad.PlaceFloor;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

internal static class FloorBeamHelper
{
    public static readonly double MinFloorAreaSqFeet = 1.0 / 0.09290304;

    /// <summary>Mặt bằng của tầng: view đang mở nếu đúng tầng, không thì mặt bằng đầu tiên của tầng.</summary>
    public static (ViewPlan View, Level Level) PlanAndLevel(Document doc, JsonElement args)
    {
        var level = Cad.LevelOrNull(doc, args, "level");
        if (doc.ActiveView is ViewPlan { IsTemplate: false, GenLevel: { } activeLevel } active && (level == null || activeLevel.Id == level.Id))
            return (active, activeLevel);
        if (level == null)
            throw new CommandException("Mở một mặt bằng gắn với tầng, hoặc truyền level.");
        return (Cad.PlanOf(doc, level) ?? throw new CommandException($"Tầng '{level.Name}' chưa có mặt bằng."), level);
    }

    public static Line Flat(Line line) => Line.CreateBound(
        new XYZ(line.GetEndPoint(0).X, line.GetEndPoint(0).Y, 0), new XYZ(line.GetEndPoint(1).X, line.GetEndPoint(1).Y, 0));

    /// <summary>
    /// Dầm dựng từ CAD (hoặc vẽ tay) thường chỉ chạy từ mặt cột tới mặt cột nên tim dầm hở tại cột, không khép thành ô.
    /// Đầu dầm nằm trong tầm một cột (≤ 700 mm tới tâm) được kéo về tâm cột trước khi dò ô.
    /// </summary>
    public static List<Curve> SnapToColumns(Document doc, List<Curve> curves)
    {
        var centers = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType()
            .Select(e => (e.Location as LocationPoint)?.Point).OfType<XYZ>().Select(p => new XYZ(p.X, p.Y, 0)).ToList();
        if (centers.Count == 0) return curves;
        var reach = UnitConvert.MmToFeet(700);
        XYZ Snap(XYZ p)
        {
            var flat = new XYZ(p.X, p.Y, 0);
            var best = centers.MinBy(c => c.DistanceTo(flat))!;
            return best.DistanceTo(flat) <= reach ? new XYZ(best.X, best.Y, p.Z) : p;
        }
        var result = new List<Curve>();
        foreach (var curve in curves)
        {
            if (curve is not Line line) { result.Add(curve); continue; }
            XYZ a = Snap(line.GetEndPoint(0)), b = Snap(line.GetEndPoint(1));
            if (a.DistanceTo(b) > doc.Application.ShortCurveTolerance) result.Add(Line.CreateBound(a, b));
        }
        return result;
    }
}

public sealed class CreateFloorsFromBeams : RevitCommandBase
{
    public override string Name => "create_floors_from_beams";
    public override string Category => "Dựng hình";
    public override RibbonButton? Ribbon => new("Sàn theo dầm", "floor");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Tạo sàn cho mọi ô khép kín giữa tim dầm và vách của một tầng (hoặc theo line đường bao đã vẽ). Bỏ qua ô dưới 1 m² và ô đã có sàn.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "level": { "type": "string", "description": "Mặc định tầng của view." },
            "floor_type": { "type": "string", "description": "Mặc định Floor Type đầu tiên." },
            "offset_mm": { "type": "number", "description": "Cao độ so với tầng." },
            "source": { "type": "string", "enum": ["beams", "boundary_lines"], "description": "beams: tự dò tim dầm; boundary_lines: line đã vẽ bằng floor_boundary_lines." },
            "dry_run": { "type": "boolean", "description": "Chỉ đếm ô sàn." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var (view, level) = FloorBeamHelper.PlanAndLevel(doc, arguments);
        var floorType = Args.GetStringOrDefault(arguments, "floor_type") is { Length: > 0 } floorName
            ? Lookup.Type<FloorType>(doc, floorName)
            : new FilteredElementCollector(doc).OfClass(typeof(FloorType)).OfCategory(BuiltInCategory.OST_Floors).Cast<FloorType>()
                  .OrderBy(t => t.Name).FirstOrDefault() ?? throw new CommandException("Dự án chưa có Floor Type.");

        List<Curve> curves;
        if (ArgsX.Enum(arguments, "source", "beams", "beams", "boundary_lines") == "beams")
        {
            curves = BeamLoopScanner.CollectCenterlineSegments(doc, view, level).Cast<Curve>().ToList();
        }
        else
        {
            var style = OwnedLineStyle.FindExisting(doc)
                        ?? throw new CommandException("Chưa có line đường bao. Chạy floor_boundary_lines trước.");
            curves = OwnedLineStyle.FindOwned(doc, view, style.Id)
                .Select(e => e.GeometryCurve).OfType<Line>().Select(l => (Curve)FloorBeamHelper.Flat(l)).ToList();
        }
        if (curves.Count == 0) throw new CommandException($"Không có dầm/vách/line đường bao nào ở tầng '{level.Name}'.");

        var loops = BeamLoopScanner.TraceClosedLoops(FloorBeamHelper.SnapToColumns(doc, curves));
        var valid = loops.Where(l => PolygonMath.AreaAbs(PolygonMath.Tessellate(l)) >= FloorBeamHelper.MinFloorAreaSqFeet).ToList();
        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new { level = level.Name, loops = loops.Count, floors_to_create = valid.Count };

        var offset = Args.GetDoubleOrDefault(arguments, "offset_mm", 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rows = valid.Select((loop, i) => new FloorRow
        {
            RowKey = i + 1, OuterLoop = loop, LevelChoices = [level], SelectedLevel = level,
            SelectedFloorType = floorType, OffsetMmText = offset,
        }).ToList();
        var summary = FloorPlacer.Place(doc, rows);
        return new
        {
            loops = loops.Count, created = summary.Created, skipped_existing = summary.Skipped,
            skipped_small = loops.Count - valid.Count, failed = summary.Failed,
            errors = summary.Errors.Take(10).ToList(), element_ids = ArgsX.Ids(summary.CreatedIds),
        };
    }
}

public sealed class FloorBoundaryLines : RevitCommandBase
{
    public override string Name => "floor_boundary_lines";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Vẽ line đường bao sàn theo tim dầm/vách trên mặt bằng để sửa tay trước khi tạo sàn, hoặc làm sạch các line đó.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "level": { "type": "string", "description": "Mặc định tầng của view." },
            "action": { "type": "string", "enum": ["draw", "clean"], "description": "Mặc định draw." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var (view, level) = FloorBeamHelper.PlanAndLevel(doc, arguments);

        if (ArgsX.Enum(arguments, "action", "draw", "draw", "clean") == "clean")
        {
            if (OwnedLineStyle.FindExisting(doc) == null) throw new CommandException("Chưa có line đường bao để làm sạch.");
            var clean = LineCleaner.Clean(doc, view);
            if (!clean.Succeeded) throw new CommandException(clean.Errors.FirstOrDefault() ?? "Không làm sạch được line.");
            return new { gaps_joined = clean.GapsJoined, branches_removed = clean.BranchesRemoved, lines = clean.LinesCreated, errors = clean.Errors };
        }

        if (OwnedLineStyle.FindExisting(doc) is { } existing && OwnedLineStyle.FindOwned(doc, view, existing.Id).Count is > 0 and var count)
            throw new CommandException($"View '{view.Name}' đã có {count} line đường bao. Xóa chúng trước nếu muốn vẽ lại.");

        var segments = BeamLoopScanner.CollectCenterlineSegments(doc, view, level);
        if (segments.Count == 0) throw new CommandException($"Không có dầm hoặc vách thẳng ở tầng '{level.Name}'.");
        var style = OwnedLineStyle.EnsureStyle(doc);
        var ids = new List<long>();
        foreach (var line in segments)
        {
            try
            {
                var curve = doc.Create.NewDetailCurve(view, line);
                curve.LineStyle = style;
                ids.Add(curve.Id.Value);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Đoạn nằm ngoài mặt phẳng view: bỏ qua.
            }
        }
        return new { view = view.Name, lines = ids.Count, line_style = style.Name };
    }
}

public sealed class MergeFloors : RevitCommandBase
{
    public override string Name => "merge_floors";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description => "Gộp các sàn cùng type và cùng cao độ trên một tầng thành một sàn.";
    public override string InputSchema => """
        { "type": "object", "properties": { "level": { "type": "string", "description": "Mặc định tầng của view." } } }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var (_, level) = FloorBeamHelper.PlanAndLevel(context.Document, arguments);
        var summary = FloorMerger.Merge(context.Document, level);
        return new { groups_merged = summary.GroupsMerged, floors_merged = summary.FloorsMerged, failed = summary.Failed, errors = summary.Errors };
    }
}

public sealed class SplitFloorByLines : RevitCommandBase
{
    public override string Name => "split_floor_by_lines";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description => "Chia một sàn thành nhiều mảnh theo line đường bao (floor_boundary_lines) đi qua sàn.";
    public override string InputSchema => """
        { "type": "object", "properties": { "element_id": { "type": "integer", "description": "Id sàn." } }, "required": ["element_id"] }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var floor = doc.GetElement(new ElementId(Args.GetLong(arguments, "element_id"))) as Floor
                    ?? throw new CommandException("element_id không phải sàn.");
        var level = doc.GetElement(floor.LevelId) as Level ?? throw new CommandException("Sàn không gắn tầng.");
        var view = doc.ActiveView is ViewPlan { GenLevel: { } l } active && l.Id == level.Id ? active
            : Cad.PlanOf(doc, level) ?? throw new CommandException($"Tầng '{level.Name}' chưa có mặt bằng.");
        var summary = FloorSplitter.Split(doc, view, level, floor);
        if (!summary.Success) throw new CommandException(summary.Errors.FirstOrDefault() ?? "Không chia được sàn.");
        return new { pieces = summary.PiecesCreated, errors = summary.Errors };
    }
}
