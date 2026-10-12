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

    /// <summary>Chữ làm tag: đọc thẳng file CAD + Text Note trong view; text_layers lọc layer chữ trong CAD.</summary>
    public static List<TextTag> Tags(Document doc, ImportInstance cad, View view, JsonElement args, out string status) =>
        CadText.Collect(doc, cad, view, out status, Args.GetStringsOrEmpty(args, "text_layers").ToList());
}

public sealed class PlaceColumnsFromCad : RevitCommandBase
{
    public override string Name => "place_columns_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Cột CAD", "column");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Đặt cột kết cấu theo đường bao chữ nhật trên layer CAD, đúng kích thước và góc xoay; tự tạo type thiếu. Tên cột (C1, K2...) đọc từ chữ CAD quanh cột, ghi vào Mark.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer đường bao cột." },
            "level": { "type": "string", "description": "Tầng chân cột; mặc định tầng của view." },
            "top_level": { "type": "string", "description": "Mặc định tầng ngay trên." },
            "column_family": { "type": "string", "description": "Family có tham số b, h để tạo type thiếu; mặc định tự chọn." },
            "type_map": { "type": "object", "description": "Kích thước \"500x300\" → type." },
            "name_parameter": { "type": "string", "description": "Tham số ghi tên cột, mặc định Mark; \"\" để bỏ qua." },
            "text_search_mm": { "type": "number", "description": "Vùng tìm tên quanh cột, mặc định 1000." },
            "text_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer chữ tên cột trong CAD; mặc định mọi layer." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          }
        }
        """;

    /// <summary>Tên cột: chữ cái + số (C1, C-1, K12, CT2a).</summary>
    private static readonly System.Text.RegularExpressions.Regex ColumnMark = new(@"^\p{L}{1,3}[-.]?\d{1,3}\p{L}?$");

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

        var columnFamily = ArgsX.FamilyWithBh(doc, arguments, "column_family", BuiltInCategory.OST_StructuralColumns);
        var choices = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => columnFamily == null || s.Family.Id == columnFamily.Id)
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

        // Tên cột: chữ dạng C1 quanh khung cột (leader chỉ vào cột là chắc chắn).
        var tags = StructureHelper.Tags(doc, cad, view, arguments, out var tagSource).Where(t => ColumnMark.IsMatch(t.Text)).ToList();
        var expand = ArgsX.Mm(arguments, "text_search_mm", 1000);
        var names = TagMatcher.Assign(columns, tags, (c, tag) =>
            TagMatcher.Point(tag, c.Center, new XYZ(Math.Cos(c.AngleRadians), Math.Sin(c.AngleRadians), 0), c.LengthFeet / 2, c.WidthFeet / 2, expand), share: false);
        string? NameOf(Column.RawColumn c) => names.TryGetValue(columns.IndexOf(c), out var t) ? t.Text : null;

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new
            {
                cad_id = cad.Id.Value, columns = columns.Count, named = names.Count, tag_source = tagSource, groups = Groups(),
                names = string.Join(", ", columns.Select(NameOf).Where(n => n != null).Distinct().OrderBy(n => n)),
            };

        var typeErrors = new List<string>();
        if (groups.Any(g => !g.IsExactMatch))
        {
            if (columnFamily != null)
                typeErrors.AddRange(Column.ColumnPlacer.CreateMissingTypes(doc, groups, columnFamily).Errors);
            else if (groups.Any(g => g.SelectedType == null))
                throw new CommandException("Dự án chưa có family cột kết cấu có tham số b, h để tạo type theo CAD. " +
                                           "Load một family cột chữ nhật (ví dụ M_Concrete-Rectangular-Column) rồi chạy lại, hoặc truyền type_map.");
        }

        var summary = Column.ColumnPlacer.Place(doc, groups, level, top);

        // Ghi tên: cột vừa tạo khớp với khung CAD có tâm gần nhất.
        var named = 0;
        var nameParameter = Args.GetStringOrDefault(arguments, "name_parameter", "Mark");
        if (!string.IsNullOrEmpty(nameParameter) && names.Count > 0)
        {
            var tolerance = UnitConvert.MmToFeet(50);
            foreach (var id in summary.CreatedIds)
            {
                if (doc.GetElement(id) is not FamilyInstance fi || fi.Location is not LocationPoint lp) continue;
                var at = new XYZ(lp.Point.X, lp.Point.Y, 0);
                var source = columns.FirstOrDefault(c => new XYZ(c.Center.X, c.Center.Y, 0).DistanceTo(at) <= tolerance);
                if (source == null || NameOf(source) is not { } name) continue;
                var parameter = nameParameter == "Mark" ? fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK) : fi.LookupParameter(nameParameter);
                if (parameter is { IsReadOnly: false, StorageType: StorageType.String } && parameter.Set(name)) named++;
            }
        }

        return new
        {
            created = summary.Created, named, skipped_existing = summary.Skipped, failed = summary.Failed,
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
        "Đặt dầm kết cấu từ cặp nét song song trên layer CAD; tên và b×h đọc thẳng từ chữ CAD dọc dầm hoặc leader chỉ vào (ví dụ D1(220x600)); chia nhịp tại Grid và cột.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer nét dầm." },
            "level": { "type": "string", "description": "Mặc định tầng của view." },
            "beam_family": { "type": "string", "description": "Family có tham số b, h để tạo type thiếu; mặc định tự chọn." },
            "type_map": { "type": "object", "description": "Tên dầm hoặc \"bxh\" → type." },
            "include_untagged": { "type": "boolean", "description": "Đặt cả dầm không đọc được tên. Mặc định false." },
            "extend_to_columns": { "type": "boolean", "description": "Kéo dầm tới cột. Mặc định true." },
            "tag_parameter": { "type": "string", "description": "Tham số text để ghi tên dầm." },
            "text_search_mm": { "type": "number", "description": "Vùng tìm tên hai bên dầm, mặc định 1000." },
            "text_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer chữ tên cấu kiện trong CAD; mặc định mọi layer." },
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
        var layers = Cad.Layers(arguments, scan.BeamsByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
        var beams = SizeTags.RemoveOverlaps(layers.SelectMany(l => scan.BeamsByLayer[l]), x => (x.Start, x.End, x.WidthFeet));
        var tags = StructureHelper.Tags(doc, cad, view, arguments, out var tagSource);
        var tagged = SizeTags.Attach(beams, b => (b.Start, b.End, b.WidthFeet),
            (b, name, bMm, hMm) => { b.TagName = name; b.TagBMm = bMm; b.TagHMm = hMm; },
            tags, ArgsX.Mm(arguments, "text_search_mm", SizeTags.DefaultSearchExpandMm));

        var extended = 0;
        if (Args.GetBoolOrDefault(arguments, "extend_to_columns", true))
        {
            var extension = Beam.BeamColumnExtender.Extend(doc, beams, level.Elevation, UnitConvert.MmToFeet(3000));
            beams = extension.Beams;
            extended = extension.ExtendedBeamCount;
        }

        // Có family dầm (chỉ định hoặc tự chọn family có b, h) thì chỉ chọn type trong family đó, tránh khớp nhầm
        // sang family khác cùng b×h (dầm sườn, dầm thép...); type thiếu sẽ được tạo.
        var beamFamily = ArgsX.FamilyWithBh(doc, arguments, "beam_family", BuiltInCategory.OST_StructuralFraming);
        var choices = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => beamFamily == null || s.Family.Id == beamFamily.Id)
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
            return new
            {
                cad_id = cad.Id.Value, beams = beams.Count, tagged, tag_source = tagSource, extended_to_columns = extended, groups = Groups(),
                detail = Table.Of(beams.Select(b => new
                {
                    name = b.TagName, b_mm = Math.Round(UnitConvert.FeetToMm(b.WidthFeet)), tag_h_mm = b.TagHMm,
                    x1 = Math.Round(UnitConvert.FeetToMm(b.Start.X)), y1 = Math.Round(UnitConvert.FeetToMm(b.Start.Y)),
                    x2 = Math.Round(UnitConvert.FeetToMm(b.End.X)), y2 = Math.Round(UnitConvert.FeetToMm(b.End.Y)),
                }), 200),
            };

        var typeErrors = new List<string>();
        // Tự tạo type thiếu khi biết đủ b x h (đọc từ Text); family lấy theo beam_family hoặc family đầu tiên có b, h.
        if (groups.Any(g => !g.IsExactMatch && g.ShouldModel && g.SizeHMm != null) && beamFamily != null)
            typeErrors.AddRange(Beam.BeamPlacer.CreateMissingTypes(doc, groups, beamFamily).Errors);

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
            "text_search_mm": { "type": "number", "description": "Vùng tìm tên quanh vách, mặc định 1000." },
            "text_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer chữ tên cấu kiện trong CAD; mặc định mọi layer." },
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
        var layers = Cad.Layers(arguments, scan.WallsByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
        var walls = SizeTags.RemoveOverlaps(layers.SelectMany(l => scan.WallsByLayer[l]), x => (x.Start, x.End, x.WidthFeet));
        var tags = StructureHelper.Tags(doc, cad, view, arguments, out var tagSource);
        var tagged = SizeTags.Attach(walls, w => (w.Start, w.End, w.WidthFeet),
            (w, name, bMm, hMm) => { w.TagName = name; w.TagBMm = bMm; w.TagHMm = hMm; },
            tags, ArgsX.Mm(arguments, "text_search_mm", SizeTags.DefaultSearchExpandMm));

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
            return new { cad_id = cad.Id.Value, walls = walls.Count, tagged, tag_source = tagSource, unsupported, groups = Groups() };

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
