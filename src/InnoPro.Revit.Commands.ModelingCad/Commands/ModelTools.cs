using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Sdk;
using Join = InnoPro.Revit.Commands.ModelingCad.AutoJoin;
using Snap = InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;
using Finish = InnoPro.Revit.Commands.ModelingCad.WallFinishLayer;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

internal static class WallScope
{
    /// <summary>Tường theo element_ids; không có thì theo scope (view hoặc project).</summary>
    public static List<Wall> Collect(Document doc, JsonElement args)
    {
        var ids = ArgsX.IdsOrEmpty(args, "element_ids");
        if (ids.Count > 0)
            return ids.Select(doc.GetElement).OfType<Wall>().ToList();
        var collector = ArgsX.Enum(args, "scope", "view", "view", "project") == "view"
            ? new FilteredElementCollector(doc, doc.ActiveView.Id)
            : new FilteredElementCollector(doc);
        return collector.OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>().ToList();
    }
}

public sealed class SnapElementsToGrid : RevitCommandBase
{
    public override string Name => "snap_elements_to_grid";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Manual;
    public override string Description =>
        "Làm tròn khoảng cách từ Grid tới tường, dầm, sàn, móng, cột trong mặt bằng hiện tại (snap), hoặc tạo Dimension để kiểm tra (verify).";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "mode": { "type": "string", "enum": ["snap", "verify"], "description": "Mặc định snap." },
            "categories": { "type": "array", "items": { "type": "string", "enum": ["walls", "beams", "floors", "foundations", "columns"] }, "description": "Mặc định tất cả." },
            "snap_unit_mm": { "type": "number", "description": "Bội số làm tròn, mặc định 5." },
            "dimension_type": { "type": "string", "description": "Dùng cho verify." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        if (doc.ActiveView is not ViewPlan view) throw new CommandException("Cần mở một mặt bằng (gọi open_view với level).");
        var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        if (grids.Count == 0) throw new CommandException("Mặt bằng chưa có Grid.");

        var cats = Args.GetStringsOrEmpty(arguments, "categories").Select(c => c.ToLowerInvariant()).ToHashSet();
        bool Use(string c) => cats.Count == 0 || cats.Contains(c);
        var walls = Use("walls") ? Snap.WallSnapper.Collect(doc, view) : [];
        var beams = Use("beams") ? Snap.BeamSnapper.Collect(doc, view) : [];
        var floors = Use("floors") ? Snap.FloorSnapper.Collect(doc, view) : [];
        var foundations = Use("foundations") ? Snap.FoundationSnapper.Collect(doc, view) : [];
        var columns = Use("columns") ? Snap.ColumnSnapper.Collect(doc, view) : [];
        var scanned = new { walls = walls.Count, beams = beams.Count, floors = floors.Count, foundations = foundations.Count, columns = columns.Count };

        if (ArgsX.Enum(arguments, "mode", "snap", "snap", "verify") == "verify")
        {
            var dimType = Args.GetStringOrDefault(arguments, "dimension_type") is { Length: > 0 } dimName
                ? Lookup.Type<DimensionType>(doc, dimName)
                : new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                      .FirstOrDefault(t => t.StyleType == DimensionStyleType.Linear)
                  ?? throw new CommandException("Dự án chưa có Dimension Type dạng Linear.");
            var notes = new List<string>();
            using var tx = new Transaction(doc, "InnoPro: kiểm tra khoảng cách tới Grid");
            TransactionHelper.SuppressWarnings(tx);
            tx.Start();
            notes.AddRange(walls.Select(w => Snap.WallSnapper.CreateDimension(doc, view, w, grids, dimType)));
            notes.AddRange(beams.Select(b => Snap.BeamSnapper.CreateDimension(doc, view, b, grids, dimType)));
            notes.AddRange(floors.SelectMany(f => Snap.FloorSnapper.CreateDimension(doc, view, f, grids, dimType)));
            notes.AddRange(foundations.SelectMany(f => Snap.FoundationSnapper.CreateDimensions(doc, view, f, grids, dimType)));
            notes.AddRange(columns.SelectMany(c => Snap.ColumnSnapper.CreateDimensions(doc, view, c, grids, dimType)));
            tx.Commit();
            return new { scanned, dimensions = notes.Count(n => n.Contains("đã tạo Dimension")), notes = notes.Take(50).ToList() };
        }

        var unit = Args.GetDoubleOrDefault(arguments, "snap_unit_mm", 5);
        var changed = 0;
        var details = new List<string>();
        void Count((bool Changed, string? Note) r)
        {
            if (r.Changed) changed++;
            if (r.Note != null && details.Count < 50) details.Add(r.Note);
        }

        // Sàn/móng bè sửa Sketch bằng SketchEditScope: phải chạy khi không có Transaction nào đang mở.
        foreach (var floor in floors) Count(Snap.FloorSnapper.Snap(doc, floor, grids, unit));
        foreach (var f in foundations.Where(f => f is Floor)) Count(Snap.FoundationSnapper.Snap(doc, f, grids, unit));

        using (var tx = new Transaction(doc, "InnoPro: làm tròn theo Grid"))
        {
            TransactionHelper.SuppressWarnings(tx);
            tx.Start();
            foreach (var w in walls) Count(Snap.WallSnapper.Snap(doc, w, grids, unit));
            foreach (var b in beams) Count(Snap.BeamSnapper.Snap(doc, b, grids, unit));
            foreach (var f in foundations.Where(f => f is not Floor)) Count(Snap.FoundationSnapper.Snap(doc, f, grids, unit));
            foreach (var c in columns) Count(Snap.ColumnSnapper.Snap(doc, c, grids, unit));
            tx.Commit();
        }
        return new { scanned, snapped = changed, notes = details };
    }
}

public sealed class AutoJoinGeometry : RevitCommandBase
{
    public override string Name => "auto_join_geometry";
    public override string Category => "Dựng hình";
    public override RibbonButton? Ribbon => new("Tự Join", "join");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Join hình học hàng loạt giữa các nhóm cấu kiện chạm nhau theo thứ tự ưu tiên (nhóm trước cắt nhóm sau), hoặc đảo/bỏ join.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "categories": { "type": "array", "items": { "type": "string", "enum": ["floors", "mass", "columns", "foundations", "framing", "walls", "roofs"] }, "description": "Thứ tự ưu tiên; mặc định floors, mass, columns, foundations, framing, walls." },
            "scope": { "type": "string", "enum": ["view", "project"], "description": "Mặc định view." },
            "operation": { "type": "string", "enum": ["join", "switch", "unjoin"], "description": "Mặc định join." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var map = new Dictionary<string, Join.CategoryOption>
        {
            ["floors"] = Join.CategoryCatalog.Floor, ["mass"] = Join.CategoryCatalog.Mass,
            ["columns"] = Join.CategoryCatalog.Column, ["foundations"] = Join.CategoryCatalog.Foundation,
            ["framing"] = Join.CategoryCatalog.Framing, ["walls"] = Join.CategoryCatalog.Wall, ["roofs"] = Join.CategoryCatalog.Roof,
        };
        var names = Args.GetStringsOrEmpty(arguments, "categories");
        var ordered = names.Count == 0
            ? Join.CategoryCatalog.Defaults.ToList()
            : names.Select(n => map.TryGetValue(n.Trim().ToLowerInvariant(), out var o) ? o
                : throw new CommandException($"Category '{n}' không hợp lệ. Dùng: {string.Join(", ", map.Keys)}.")).Distinct().ToList();
        if (ordered.Count < 2) throw new CommandException("Cần ít nhất 2 category.");

        var scope = ArgsX.Enum(arguments, "scope", "view", "view", "project") == "view" ? Join.JoinScope.ActiveView : Join.JoinScope.Project;
        var operation = ArgsX.Enum(arguments, "operation", "join", "join", "switch", "unjoin") switch
        {
            "switch" => Join.JoinOperation.Switch,
            "unjoin" => Join.JoinOperation.Unjoin,
            _ => Join.JoinOperation.Join,
        };
        var r = Join.AutoJoinEngine.Run(context.Document, context.Document.ActiveView, ordered, scope, operation);
        return new { pairs = r.CandidatePairs, changed = r.Changed, already_correct = r.AlreadyCorrect, skipped = r.Skipped, failed = r.Failed };
    }
}

public sealed class CreateWallFinishLayers : RevitCommandBase
{
    public override string Name => "create_wall_finish_layers";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Tạo tường lớp tô hai mặt cho các tường đã chọn (hoặc cả view/dự án), tự join và nối góc.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Tường cần tô; bỏ trống thì theo scope." },
            "scope": { "type": "string", "enum": ["view", "project"], "description": "Khi không có element_ids." },
            "host_wall_types": { "type": "array", "items": { "type": "string" }, "description": "Chỉ tô tường thuộc các type này." },
            "wall_type": { "type": "string", "description": "Wall Type lớp tô." },
            "base_offset_mm": { "type": "number" },
            "height_mode": { "type": "string", "enum": ["wall", "ceiling"], "description": "Đỉnh theo tường hoặc theo trần. Mặc định wall." },
            "top_offset_mm": { "type": "number", "description": "Khi height_mode=wall." },
            "ceiling_gap_mm": { "type": "number", "description": "Khi height_mode=ceiling: cao hơn mặt trần." }
          },
          "required": ["wall_type"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var coating = Lookup.Type<WallType>(doc, Args.GetString(arguments, "wall_type"));
        if (coating.Kind != WallKind.Basic) throw new CommandException("Wall Type lớp tô phải là Basic Wall.");
        var walls = WallScope.Collect(doc, arguments).Where(w => w.GetTypeId() != coating.Id).ToList();
        var hostTypes = Args.GetStringsOrEmpty(arguments, "host_wall_types");
        if (hostTypes.Count > 0)
        {
            var allowed = hostTypes.Select(t => Lookup.Type<WallType>(doc, t).Id).ToHashSet();
            walls = walls.Where(w => allowed.Contains(w.GetTypeId())).ToList();
        }
        if (walls.Count == 0) throw new CommandException("Không có tường nào trong phạm vi đã chọn.");

        var options = new Finish.FinishLayerOptions
        {
            CoatingType = coating,
            BaseOffsetMm = Args.GetDoubleOrDefault(arguments, "base_offset_mm", 0),
            HeightMode = ArgsX.Enum(arguments, "height_mode", "wall", "wall", "ceiling") == "ceiling" ? Finish.HeightMode.BelowCeiling : Finish.HeightMode.MatchWallHeight,
            TopOffsetMm = Args.GetDoubleOrDefault(arguments, "top_offset_mm", 0),
            CeilingGapMm = Args.GetDoubleOrDefault(arguments, "ceiling_gap_mm", 0),
        };
        var report = Finish.WallFinishBuilder.Build(doc, walls, options);
        return new { walls = report.WallsScanned, skipped = report.WallsSkipped, layers_created = report.LayersCreated, join_failed = report.JoinFailed };
    }
}

public sealed class CreateWallFaceLayers : RevitCommandBase
{
    public override string Name => "create_wall_face_layers";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Tạo lớp vật liệu (tường mỏng) trên một mặt của các tường đã chọn, nối góc giữa các lớp.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Tường." },
            "side": { "type": "string", "enum": ["exterior", "interior"], "description": "Mặt tường, mặc định exterior." },
            "wall_type": { "type": "string", "description": "Wall Type lớp vật liệu." },
            "base_offset_mm": { "type": "number" },
            "top_mode": { "type": "string", "enum": ["height", "ceiling", "elevation"], "description": "Mặc định height." },
            "height_mm": { "type": "number", "description": "Khi top_mode=height, mặc định 900." },
            "ceiling_offset_mm": { "type": "number", "description": "Khi top_mode=ceiling: so với đáy trần." },
            "top_elevation_mm": { "type": "number", "description": "Khi top_mode=elevation: cao độ tuyệt đối." }
          },
          "required": ["element_ids", "wall_type"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var type = Lookup.Type<WallType>(doc, Args.GetString(arguments, "wall_type"));
        var shell = ArgsX.Enum(arguments, "side", "exterior", "exterior", "interior") == "interior" ? ShellLayerType.Interior : ShellLayerType.Exterior;
        var targets = new List<(Wall, Face)>();
        foreach (var wall in Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).OfType<Wall>())
        {
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetSideFaces(wall, shell); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
            foreach (var r in refs)
                if (wall.GetGeometryObjectFromReference(r) is Face face) targets.Add((wall, face));
        }
        if (targets.Count == 0) throw new CommandException("Không đọc được mặt tường nào từ element_ids.");

        var mode = ArgsX.Enum(arguments, "top_mode", "height", "height", "ceiling", "elevation");
        var options = new Finish.SecondLayerOptions
        {
            MaterialType = type,
            BaseOffsetMm = Args.GetDoubleOrDefault(arguments, "base_offset_mm", 0),
            TopMode = mode switch
            {
                "ceiling" => Finish.SecondLayerTopMode.OffsetFromCeilingBottom,
                "elevation" => Finish.SecondLayerTopMode.OffsetFromSelectedFace,
                _ => Finish.SecondLayerTopMode.FreeHeight,
            },
            FreeHeightMm = Args.GetDoubleOrDefault(arguments, "height_mm", 900),
            CeilingOffsetMm = Args.GetDoubleOrDefault(arguments, "ceiling_offset_mm", 0),
            SelectedFaceOffsetMm = 0,
        };
        double? referenceZ = mode == "elevation"
            ? UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "top_elevation_mm", double.NaN) is var z && !double.IsNaN(z) ? z
                : throw new CommandException("Thiếu top_elevation_mm."))
            : null;
        var (created, notes) = Finish.SecondLayerBuilder.BuildAll(doc, targets, options, referenceZ);
        return new { faces = targets.Count, created, notes = notes.Where(n => !n.StartsWith("Đã tạo")).Take(20).ToList() };
    }
}

public sealed class AdjustWallsToStructure : RevitCommandBase
{
    public override string Name => "adjust_walls_to_structure";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Chỉnh chân và đỉnh tường kiến trúc bám theo sàn, dầm trong file link kết cấu.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Tường; bỏ trống thì theo scope." },
            "scope": { "type": "string", "enum": ["view", "project"] },
            "link": { "type": "string", "description": "Tên link kết cấu; mặc định link đầu tiên." },
            "search_min_mm": { "type": "number", "description": "Mặc định 0." },
            "search_max_mm": { "type": "number", "description": "Khoảng dò tối đa, mặc định 800." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null).ToList();
        if (links.Count == 0) throw new CommandException("Chưa có file link Revit nào được nạp.");
        var link = Args.GetStringOrDefault(arguments, "link") is { Length: > 0 } name
            ? links.FirstOrDefault(l => l.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
              ?? throw new CommandException($"Không có link '{name}'. Có: {Cad.Suggest(links.Select(l => l.Name))}.")
            : links[0];
        var linkDoc = link.GetLinkDocument();
        var floors = new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().ToList();
        var beams = new FilteredElementCollector(linkDoc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().ToList();
        if (floors.Count == 0 && beams.Count == 0) throw new CommandException("Link không có sàn hoặc dầm.");

        var walls = WallScope.Collect(doc, arguments);
        if (walls.Count == 0) throw new CommandException("Không có tường nào trong phạm vi.");
        var r = WallElevationToStructure.ElevationAdjuster.Adjust(doc, walls, floors, beams, link.GetTotalTransform(),
            ArgsX.Mm(arguments, "search_min_mm", 0), ArgsX.Mm(arguments, "search_max_mm", 800));
        return new
        {
            link = link.Name, walls = r.WallsScanned, fixed_walls = r.WallsFixed, failed = r.WallsFailed,
            base_adjusted = r.BaseAdjusted, top_adjusted = r.TopAdjusted, details = r.Details.Take(30).ToList(),
        };
    }
}

public sealed class SelectSameType : RevitCommandBase
{
    public override string Name => "select_same_type";
    public override string Category => "Chọn";
    public override RibbonButton? Ribbon => new("Cùng loại", "layers");
    public override string Description => "Chọn mọi phần tử trong mô hình cùng type với các phần tử đã cho.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Phần tử mẫu." },
            "scope": { "type": "string", "enum": ["project", "view"], "description": "Mặc định project." }
          },
          "required": ["element_ids"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var typeIds = Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement)
            .Where(e => e != null).Select(e => e!.GetTypeId()).Where(t => t != ElementId.InvalidElementId).ToHashSet();
        if (typeIds.Count == 0) throw new CommandException("Phần tử đã cho không có type.");
        var collector = ArgsX.Enum(arguments, "scope", "project", "project", "view") == "view"
            ? new FilteredElementCollector(doc, doc.ActiveView.Id) : new FilteredElementCollector(doc);
        var matches = collector.WhereElementIsNotElementType().Where(e => typeIds.Contains(e.GetTypeId())).Select(e => e.Id).ToList();
        context.UIDocument.Selection.SetElementIds(matches);
        return new { selected = matches.Count, types = typeIds.Count };
    }
}

public sealed class SelectAllRooms : RevitCommandBase
{
    public override string Name => "select_all_rooms";
    public override string Category => "Chọn";
    public override string Description => "Chọn toàn bộ Room trong mô hình (hoặc trong view hiện tại).";
    public override string InputSchema => """
        { "type": "object", "properties": { "scope": { "type": "string", "enum": ["project", "view"], "description": "Mặc định project." } } }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var collector = ArgsX.Enum(arguments, "scope", "project", "project", "view") == "view"
            ? new FilteredElementCollector(doc, doc.ActiveView.Id) : new FilteredElementCollector(doc);
        var rooms = collector.OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElementIds();
        context.UIDocument.Selection.SetElementIds(rooms);
        return new { selected = rooms.Count };
    }
}

public sealed class CutDirectShape : RevitCommandBase
{
    public override string Name => "cut_directshape";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Khoét khối (DirectShape hoặc cấu kiện) bằng khối dao: cắt sống nếu Revit cho phép, không thì trừ thẳng hình học.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Khối cần cắt." },
            "cutter_ids": { "type": "array", "items": { "type": "integer" }, "description": "Khối dao." },
            "prefer_live_cut": { "type": "boolean", "description": "Mặc định true." },
            "delete_cutter": { "type": "boolean", "description": "Xóa dao sau khi trừ hình học." }
          },
          "required": ["element_ids", "cutter_ids"]
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var targets = context.Ui.SelectionOrPick("Chọn khối cần cắt rồi bấm Finish");
        IList<Reference> cutters;
        try { cutters = context.UIDocument.Selection.PickObjects(ObjectType.Element, "Chọn khối dao rồi bấm Finish"); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw new OperationCanceledException(); }
        return new { element_ids = targets.Select(e => e.Id.Value).ToList(), cutter_ids = cutters.Select(r => r.ElementId.Value).Distinct().ToList() };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var targets = Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).OfType<Element>().ToList();
        var cutters = Args.GetElementIds(arguments, "cutter_ids").Select(doc.GetElement).OfType<Element>().ToList();
        if (targets.Count == 0 || cutters.Count == 0) throw new CommandException("Thiếu khối cần cắt hoặc khối dao.");
        var options = new DirectShapeCut.CutOptions
        {
            PreferLiveCut = Args.GetBoolOrDefault(arguments, "prefer_live_cut", true),
            DeleteCutterAfterSubtract = Args.GetBoolOrDefault(arguments, "delete_cutter", false),
        };
        var counts = new Dictionary<string, int>();
        var notes = new List<string>();
        foreach (var target in targets)
        foreach (var cutter in cutters)
        {
            if (target.Id == cutter.Id || !cutter.IsValidObject || !target.IsValidObject) continue;
            var outcome = DirectShapeCut.CutService.Cut(doc, target, cutter, options);
            var key = outcome.Method.ToString();
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (outcome.Method == DirectShapeCut.CutMethod.Failed && outcome.Note is { Length: > 0 } note && notes.Count < 8 && !notes.Contains(note))
                notes.Add(note);
        }
        return new
        {
            live_void = counts.GetValueOrDefault("InstanceVoid"), live_solid = counts.GetValueOrDefault("SolidSolid"),
            subtracted = counts.GetValueOrDefault("GeometrySubtract"), failed = counts.GetValueOrDefault("Failed"), notes,
        };
    }
}
