using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Sdk;
using Beam = InnoPro.Revit.Commands.ModelingCad.PlaceBeam;
using Column = InnoPro.Revit.Commands.ModelingCad.PlaceColumn;
using SWall = InnoPro.Revit.Commands.ModelingCad.PlaceWall;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

internal static class StructureHelper
{
    /// <summary>Tầng ngay trên tầng cho trước (dùng làm tầng đỉnh mặc định).</summary>
    public static Level NextLevel(Document doc, Level level) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(l => l.Elevation > level.Elevation + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault()
        ?? throw new CommandException($"Không có tầng nào phía trên '{level.Name}'. Truyền top_level.");

    public static List<TextNote> TextNotes(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
}

public sealed class PlaceColumnsFromCad : RevitCommandBase
{
    public override string Name => "place_columns_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Cột CAD", "column");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Đặt cột kết cấu theo đường bao chữ nhật trên layer CAD, đúng kích thước và góc xoay. Tự tạo type thiếu nếu có column_family.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer đường bao cột." },
            "level": { "type": "string", "description": "Tầng chân cột; mặc định tầng của view." },
            "top_level": { "type": "string", "description": "Mặc định tầng ngay trên." },
            "column_family": { "type": "string", "description": "Family có tham số b, h để tạo type thiếu." },
            "type_map": { "type": "object", "description": "Kích thước \"500x300\" → type." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào đường bao cột trong CAD");
        return new { cad_id = cad.Id.Value, layers };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var level = Cad.LevelOrView(doc, arguments, "level", view);
        var top = Cad.LevelOrNull(doc, arguments, "top_level") ?? StructureHelper.NextLevel(doc, level);

        var scan = Column.CadColumnScanner.Scan(doc, cad, view);
        var layers = Cad.Layers(arguments, scan.ColumnsByLayer.Keys);
        var columns = layers.SelectMany(l => scan.ColumnsByLayer[l]).ToList();

        var choices = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Select(s => new Column.SymbolChoice(s, Lookup.FullName(s))).ToList();
        var groups = Column.ColumnPlacer.GroupColumns(columns, choices);

        var typeMap = ArgsX.StringMap(arguments, "type_map");
        foreach (var group in groups)
        {
            if (!typeMap.TryGetValue(Key(group), out var typeName)) continue;
            var symbol = Lookup.Type<FamilySymbol>(doc, typeName, BuiltInCategory.OST_StructuralColumns);
            group.SelectedType = new Column.SymbolChoice(symbol, Lookup.FullName(symbol));
            group.IsExactMatch = true;
        }

        object Groups() => Table.Of(groups.Select(g => new
        {
            size = Key(g), count = g.Columns.Count, type = g.SelectedType?.DisplayName, exact = g.IsExactMatch,
        }));

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new { cad_id = cad.Id.Value, columns = columns.Count, groups = Groups() };

        var typeErrors = new List<string>();
        if (Args.GetStringOrDefault(arguments, "column_family") is { Length: > 0 } familyName)
        {
            var family = ArgsX.Family(doc, familyName, BuiltInCategory.OST_StructuralColumns);
            var created = Column.ColumnPlacer.CreateMissingTypes(doc, groups, family);
            typeErrors.AddRange(created.Errors);
        }

        var summary = Column.ColumnPlacer.Place(doc, groups, level, top);
        return new
        {
            created = summary.Created, skipped_existing = summary.Skipped, failed = summary.Failed,
            groups = Groups(), errors = typeErrors.Concat(summary.Errors).Take(20).ToList(),
            element_ids = ArgsX.Ids(summary.CreatedIds.Take(500)),
        };
    }

    private static string Key(Column.ColumnGroup g) => $"{g.SizeBMm:0}x{g.SizeAMm:0}";
}

public sealed class PlaceBeamsFromCad : RevitCommandBase
{
    public override string Name => "place_beams_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Dầm CAD", "beam");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Đặt dầm kết cấu từ cặp nét song song trên layer CAD; tên và b×h đọc từ Text Note gần dầm (ví dụ D1(220x600)).";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer nét dầm." },
            "level": { "type": "string", "description": "Mặc định tầng của view." },
            "beam_family": { "type": "string", "description": "Family có tham số b, h để tạo type thiếu." },
            "type_map": { "type": "object", "description": "Tên dầm hoặc \"bxh\" → type." },
            "include_untagged": { "type": "boolean", "description": "Đặt cả dầm không đọc được tên. Mặc định false." },
            "extend_to_columns": { "type": "boolean", "description": "Kéo dầm tới cột. Mặc định true." },
            "tag_parameter": { "type": "string", "description": "Tham số text để ghi tên dầm." },
            "text_search_mm": { "type": "number", "description": "Bán kính tìm Text, mặc định 1000." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào nét dầm trong CAD");
        return new { cad_id = cad.Id.Value, layers };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var level = Cad.LevelOrView(doc, arguments, "level", view);

        var scan = Beam.CadBeamScanner.Scan(doc, cad, view);
        foreach (var (beam, layerName) in Beam.BeamOrphanRepairer.Repair(scan.RawSegmentsByLayer, scan.Orphans))
            if (scan.BeamsByLayer.TryGetValue(layerName, out var list)) list.Add(beam);
        Beam.BeamTagReader.AttachTags(scan.BeamsByLayer.Values.SelectMany(l => l), StructureHelper.TextNotes(doc, view),
            ArgsX.Mm(arguments, "text_search_mm", Beam.BeamTagReader.DefaultSearchExpandMm));

        var layers = Cad.Layers(arguments, scan.BeamsByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
        var beams = layers.SelectMany(l => scan.BeamsByLayer[l]).ToList();
        var extended = 0;
        if (Args.GetBoolOrDefault(arguments, "extend_to_columns", true))
        {
            var extension = Beam.BeamColumnExtender.Extend(doc, beams, level.Elevation, UnitConvert.MmToFeet(3000));
            beams = extension.Beams;
            extended = extension.ExtendedBeamCount;
        }

        var choices = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Select(s => new Beam.SymbolChoice(s, Lookup.FullName(s))).ToList();
        var groups = Beam.BeamPlacer.GroupBeams(beams, choices);
        var includeUntagged = Args.GetBoolOrDefault(arguments, "include_untagged", false);
        var typeMap = ArgsX.StringMap(arguments, "type_map");
        foreach (var group in groups)
        {
            if (includeUntagged) group.ShouldModel = true;
            if (typeMap.TryGetValue(group.TagName ?? SizeKey(group), out var typeName)
                || typeMap.TryGetValue(SizeKey(group), out typeName))
            {
                var symbol = Lookup.Type<FamilySymbol>(doc, typeName, BuiltInCategory.OST_StructuralFraming);
                group.SelectedType = new Beam.SymbolChoice(symbol, Lookup.FullName(symbol));
                group.IsExactMatch = true;
            }
        }

        object Groups() => Table.Of(groups.Select(g => new
        {
            name = g.TagName, size = SizeKey(g), count = g.Beams.Count, model = g.ShouldModel,
            type = g.SelectedType?.DisplayName, exact = g.IsExactMatch,
        }));

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new { cad_id = cad.Id.Value, beams = beams.Count, extended_to_columns = extended, groups = Groups() };

        var typeErrors = new List<string>();
        if (Args.GetStringOrDefault(arguments, "beam_family") is { Length: > 0 } familyName)
        {
            var family = ArgsX.Family(doc, familyName, BuiltInCategory.OST_StructuralFraming);
            typeErrors.AddRange(Beam.BeamPlacer.CreateMissingTypes(doc, groups, family).Errors);
        }

        var summary = Beam.BeamPlacer.Place(doc, groups, level, Args.GetStringOrDefault(arguments, "tag_parameter"));
        return new
        {
            created = summary.Created, skipped_existing = summary.Skipped, failed = summary.Failed,
            extended_to_columns = extended, groups = Groups(),
            errors = typeErrors.Concat(summary.Errors).Take(20).ToList(),
            element_ids = ArgsX.Ids(summary.CreatedIds.Take(500)),
        };
    }

    private static string SizeKey(Beam.BeamGroup g) => g.SizeHMm is { } h ? $"{g.SizeBMm:0}x{h:0}" : $"{g.SizeBMm:0}";
}

public sealed class PlaceStructuralWallsFromCad : RevitCommandBase
{
    public override string Name => "place_structural_walls_from_cad";
    public override string Category => "Dựng hình CAD";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Đặt vách/tường kết cấu từ cặp nét song song trên layer CAD, chọn Wall Type theo bề dày. Bỏ qua đoạn đã có tường.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer nét vách." },
            "level": { "type": "string", "description": "Tầng chân; mặc định tầng của view." },
            "top_level": { "type": "string", "description": "Mặc định tầng ngay trên." },
            "base_offset_mm": { "type": "number" },
            "top_offset_mm": { "type": "number" },
            "template_wall_type": { "type": "string", "description": "Wall Type mẫu để tạo type thiếu." },
            "type_map": { "type": "object", "description": "Tên vách hoặc bề dày → Wall Type." },
            "include_untagged": { "type": "boolean", "description": "Đặt cả vách không có tên. Mặc định true." },
            "require_support": { "type": "boolean", "description": "Chỉ đặt vách có tường/móng đỡ bên dưới. Mặc định false." },
            "tag_parameter": { "type": "string", "description": "Tham số text để ghi tên vách." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào nét vách trong CAD");
        return new { cad_id = cad.Id.Value, layers };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var level = Cad.LevelOrView(doc, arguments, "level", view);
        var top = Cad.LevelOrNull(doc, arguments, "top_level") ?? StructureHelper.NextLevel(doc, level);
        var baseOffset = ArgsX.Mm(arguments, "base_offset_mm", 0);
        var topOffset = ArgsX.Mm(arguments, "top_offset_mm", 0);
        const double minPairLengthRatio = 0.7;

        var scan = SWall.CadWallScanner.Scan(doc, cad, view, 0.0, minPairLengthRatio);
        foreach (var (wall, layerName) in SWall.WallOrphanRepairer.Repair(scan.RawSegmentsByLayer, scan.Orphans, minPairLengthRatio))
            if (scan.WallsByLayer.TryGetValue(layerName, out var list)) list.Add(wall);
        SWall.WallTagReader.AttachTags(scan.WallsByLayer.Values.SelectMany(l => l), StructureHelper.TextNotes(doc, view),
            UnitConvert.MmToFeet(SWall.WallTagReader.DefaultSearchExpandMm));

        var layers = Cad.Layers(arguments, scan.WallsByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
        var walls = layers.SelectMany(l => scan.WallsByLayer[l]).ToList();
        var unsupported = 0;
        if (Args.GetBoolOrDefault(arguments, "require_support", false))
        {
            var before = walls.Count;
            walls = SWall.WallSupportFilter.Filter(doc, walls, level.Elevation + baseOffset, UnitConvert.MmToFeet(500));
            unsupported = before - walls.Count;
        }

        var choices = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .Where(t => t.Kind == WallKind.Basic).Select(t => new SWall.SymbolChoice(t, t.Name)).ToList();
        var groups = SWall.WallPlacer.GroupWalls(walls, choices);
        var includeUntagged = Args.GetBoolOrDefault(arguments, "include_untagged", true);
        var typeMap = ArgsX.StringMap(arguments, "type_map");
        foreach (var group in groups)
        {
            if (includeUntagged) group.ShouldModel = true;
            var size = $"{group.SizeBMm:0}";
            if ((group.TagName != null && typeMap.TryGetValue(group.TagName, out var typeName)) || typeMap.TryGetValue(size, out typeName))
            {
                var type = Lookup.Type<WallType>(doc, typeName);
                group.SelectedType = new SWall.SymbolChoice(type, type.Name);
                group.IsExactMatch = true;
            }
        }

        object Groups() => Table.Of(groups.Select(g => new
        {
            name = g.TagName, thickness_mm = g.SizeBMm, count = g.Walls.Count, model = g.ShouldModel,
            type = g.SelectedType?.DisplayName, exact = g.IsExactMatch,
        }));

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new { cad_id = cad.Id.Value, walls = walls.Count, unsupported, groups = Groups() };

        var typeErrors = new List<string>();
        if (Args.GetStringOrDefault(arguments, "template_wall_type") is { Length: > 0 } templateName)
            typeErrors.AddRange(SWall.WallPlacer.CreateMissingTypes(doc, groups, Lookup.Type<WallType>(doc, templateName)).Errors);

        var summary = SWall.WallPlacer.Place(doc, groups, level, top, baseOffset, topOffset, Args.GetStringOrDefault(arguments, "tag_parameter"));
        return new
        {
            created = summary.Created, skipped_existing = summary.Skipped, failed = summary.Failed, unsupported,
            groups = Groups(), errors = typeErrors.Concat(summary.Errors).Take(20).ToList(),
            element_ids = ArgsX.Ids(summary.CreatedIds.Take(500)),
        };
    }
}
