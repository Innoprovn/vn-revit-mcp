using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Sdk;
using BeamType = InnoPro.Revit.Commands.ModelingCad.CreateBeamType;
using Door = InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;
using Pile = InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

public sealed class PlacePilesFromCad : RevitCommandBase
{
    public override string Name => "place_piles_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Cọc + đài", "pile");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Đặt cọc theo đường tròn và đài móng (Foundation Slab) theo đường bao kín trên layer CAD, kèm lớp lót và join cọc vào đài.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "pile_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer đường tròn cọc." },
            "cap_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer đường bao đài." },
            "level": { "type": "string", "description": "Tầng đỉnh đài; mặc định tầng của view." },
            "pile_type": { "type": "string", "description": "Type cọc mặc định (Structural Foundation)." },
            "cap_type": { "type": "string", "description": "Foundation Slab type của đài." },
            "lean_type": { "type": "string", "description": "Foundation Slab type lớp lót; bỏ trống thì không tạo." },
            "lean_offset_mm": { "type": "number", "description": "Mép lót rộng hơn đài, mặc định 50." },
            "embedment_mm": { "type": "number", "description": "Cọc ăn vào đài, mặc định 100." },
            "create_missing_types": { "type": "boolean", "description": "Tạo type cọc theo đường kính. Mặc định true." },
            "snap_to_grid": { "type": "boolean", "description": "Làm tròn vị trí theo Grid. Mặc định true." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          },
          "required": ["cap_type"]
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, piles) = CadPick.Layers(context, "Click vào đường tròn cọc trong CAD");
        var caps = CadPick.OptionalLayers(context, "Click vào đường bao đài móng", cad);
        var cap = new FilteredElementCollector(context.Document).OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FloorType)).Cast<FloorType>().OrderBy(t => t.Name).FirstOrDefault()
            ?? throw new CommandException("Dự án chưa có Foundation Slab type cho đài móng.");
        return new { cad_id = cad.Id.Value, pile_layers = piles, cap_layers = caps, cap_type = cap.Name };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        var level = Cad.LevelOrView(doc, arguments, "level", view);

        var scan = Pile.CadPileScanner.Scan(doc, cad, view);
        var pileLayers = Cad.Layers(arguments, scan.CirclesByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key), "pile_layers", "pile_layer");
        var capLayers = Cad.Layers(arguments, scan.BoundariesByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key), "cap_layers", "cap_layer", required: false);
        var circles = pileLayers.SelectMany(l => scan.CirclesByLayer[l]).ToList();
        var boundaries = Pile.PileFoundationPlacer.DeduplicateOverlappingBoundaries(capLayers.SelectMany(l => scan.BoundariesByLayer[l]));
        var rows = boundaries.Select((b, i) => new Pile.BoundaryRow { Index = i + 1, Boundary = b }).ToList();
        Pile.PileOwnershipResolver.AssignOwners(circles, rows);

        var choices = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Select(s => new Pile.SymbolChoice(s, Lookup.FullName(s))).ToList();
        var groups = Pile.PileFoundationPlacer.GroupCircles(circles, choices);
        if (Args.GetStringOrDefault(arguments, "pile_type") is { Length: > 0 } pileTypeName)
        {
            var symbol = Lookup.Type<FamilySymbol>(doc, pileTypeName, BuiltInCategory.OST_StructuralFoundation);
            var choice = new Pile.SymbolChoice(symbol, Lookup.FullName(symbol));
            foreach (var group in groups)
            {
                group.SelectedType = choice;
                group.IsExactMatch = Pile.PileFoundationPlacer.IsMatch(choice, group.DiameterMm);
            }
        }

        object Groups() => Table.Of(groups.Select(g => new
        {
            diameter_mm = g.DiameterMm, count = g.Circles.Count, type = g.SelectedType?.DisplayName, exact = g.IsExactMatch,
        }));

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
        {
            return new
            {
                cad_id = cad.Id.Value, piles = circles.Count, caps = rows.Count,
                piles_without_cap = circles.Count(c => c.OwningBoundary == null), groups = Groups(),
            };
        }

        var capType = FoundationSlab(doc, Args.GetString(arguments, "cap_type"));
        var leanType = Args.GetStringOrDefault(arguments, "lean_type") is { Length: > 0 } lean ? FoundationSlab(doc, lean) : null;

        var errors = new List<string>();
        if (Args.GetBoolOrDefault(arguments, "create_missing_types", true))
            errors.AddRange(Pile.PileFoundationPlacer.CreateMissingTypes(doc, groups).Errors);

        var planView = view as ViewPlan ?? doc.ActiveView;
        var theta = Pile.PileGridSnapper.DetectThetaDeg(doc, planView);
        var summary = Pile.PileFoundationPlacer.Place(doc, groups, rows, level, capType,
            Args.GetDoubleOrDefault(arguments, "embedment_mm", 100), theta);
        errors.AddRange(summary.Errors);

        Pile.LotFloorSummary? leanSummary = null;
        if (leanType != null && summary.ResolvedCapIds.Count > 0)
            leanSummary = Pile.PileLotFloorPlacer.Place(doc, summary.ResolvedCapIds, leanType,
                Args.GetDoubleOrDefault(arguments, "lean_offset_mm", 50), level, summary.CreatedCapProfiles);

        Pile.SnapSummary? snap = null;
        if (Args.GetBoolOrDefault(arguments, "snap_to_grid", true))
            snap = Pile.PileGridSnapper.Snap(doc, planView, summary.CreatedPileIds.Concat(summary.CreatedCapIds).ToList());

        var join = Pile.PileCapJoiner.JoinPilesToFoundationSlabs(doc, planView,
            Pile.PileFoundationPlacer.FindPileFamilyNames(groups.Where(g => g.SelectedType != null).Select(g => g.SelectedType!)),
            capType.Id, leanType?.Id);

        return new
        {
            piles_created = summary.PilesCreated, piles_skipped = summary.PilesSkipped, piles_failed = summary.PilesFailed,
            caps_created = summary.CapsCreated, caps_skipped = summary.CapsSkipped, caps_failed = summary.CapsFailed,
            lean_created = leanSummary?.Created, snapped = snap?.Snapped, joined = join.Joined,
            groups = Groups(), errors = errors.Take(20).ToList(),
        };
    }

    private static FloorType FoundationSlab(Document doc, string name)
    {
        var types = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
        return types.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new CommandException($"Không có Foundation Slab type '{name}'. Có: {Cad.Suggest(types.Select(t => t.Name))}.");
    }
}

public sealed class PlaceDoorsWindowsFromCad : RevitCommandBase
{
    public override string Name => "place_doors_windows_from_cad";
    public override string Category => "Dựng hình CAD";
    public override RibbonButton? Ribbon => new("Cửa CAD", "door");
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Đặt cửa đi/cửa sổ vào tường theo ký hiệu cửa trên layer CAD; tên cửa đọc thẳng từ chữ CAD quanh lỗ mở (ưu tiên leader), bỏ qua chữ kích thước.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "cad_id": { "type": "integer", "description": "Id file CAD; mặc định CAD trong view." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer ký hiệu cửa." },
            "level": { "type": "string", "description": "Tầng đặt cửa; mặc định mặt bằng đang mở." },
            "type_map": { "type": "object", "description": "Tên cửa → 'Family: Type'." },
            "default_type": { "type": "string", "description": "Type cho cửa chưa khớp tên." },
            "new_type_family": { "type": "string", "description": "Family để tạo type thiếu theo kích thước." },
            "new_type_heights": { "type": "object", "description": "Tên cửa → chiều cao mm (tạo type mới)." },
            "sill_height_mm": { "type": "number", "description": "Cao độ bậu mặc định, mặc định 0." },
            "sill_heights": { "type": "object", "description": "Tên cửa → cao độ bậu mm." },
            "sill_from": { "type": "string", "enum": ["level", "finished_floor"], "description": "Mốc cao độ bậu." },
            "text_search_mm": { "type": "number", "description": "Vùng tìm tên quanh lỗ cửa, mặc định 2000." },
            "text_layers": { "type": "array", "items": { "type": "string" }, "description": "Layer chữ tên cửa trong CAD; mặc định mọi layer." },
            "width_parameter": { "type": "string", "description": "Mặc định Width." },
            "height_parameter": { "type": "string", "description": "Mặc định Height." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào ký hiệu cửa trong CAD");
        return new { cad_id = cad.Id.Value, layers };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var cad = Cad.Resolve(doc, arguments);
        var view = Cad.ScanView(doc, cad, Cad.LevelOrNull(doc, arguments, "level"));
        if (view is not ViewPlan) throw new CommandException("Cần mặt bằng chứa file CAD để đặt cửa.");

        var scan = Door.CadOpeningScanner.Scan(doc, cad, view);
        var layers = Cad.Layers(arguments, scan.SegmentsByLayer.Keys.Concat(scan.ArcsByLayer.Keys));
        var segments = layers.SelectMany(l => scan.SegmentsByLayer.TryGetValue(l, out var s) ? s : []).ToList();
        var arcs = layers.SelectMany(l => scan.ArcsByLayer.TryGetValue(l, out var a) ? a : []).ToList();

        // Mọi tường cắt qua cao độ tầng của mặt bằng (không lọc theo view: crop view có thể che mất tường).
        var elevation = (view as ViewPlan)?.GenLevel?.Elevation ?? 0;
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .Where(w => w.get_BoundingBox(null) is { } box && box.Min.Z <= elevation + 0.5 && box.Max.Z >= elevation + 0.5)
            .ToList();
        if (walls.Count == 0) throw new CommandException("Mặt bằng chưa có tường để đặt cửa.");
        var tags = StructureHelper.Tags(doc, cad, view, arguments, out var tagSource);
        var instances = Door.OpeningDetector.Detect(segments, arcs, walls, tags,
            ArgsX.Mm(arguments, "text_search_mm", Door.TextTagReader.DefaultSearchRadiusMm));

        List<FamilySymbol> Symbols(BuiltInCategory c) =>
            new FilteredElementCollector(doc).OfCategory(c).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var groups = Door.DoorNameGrouper.Build(instances, Symbols(BuiltInCategory.OST_Doors), Symbols(BuiltInCategory.OST_Windows));

        FamilySymbol Find(string name)
        {
            var all = Symbols(BuiltInCategory.OST_Doors).Concat(Symbols(BuiltInCategory.OST_Windows)).ToList();
            return all.FirstOrDefault(s => string.Equals(Lookup.FullName(s), name.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new CommandException($"Không có type cửa '{name}'. Có: {Cad.Suggest(all.Select(Lookup.FullName))}.");
        }

        var typeMap = ArgsX.StringMap(arguments, "type_map");
        var heights = ArgsX.NumberMap(arguments, "new_type_heights");
        var sills = ArgsX.NumberMap(arguments, "sill_heights");
        var defaultType = Args.GetStringOrDefault(arguments, "default_type") is { Length: > 0 } d ? Find(d) : null;
        var newFamily = Args.GetStringOrDefault(arguments, "new_type_family") is { Length: > 0 } nf
            ? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                  .FirstOrDefault(f => string.Equals(f.Name, nf.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new CommandException($"Không có family '{nf}'.")
            : null;
        var sill = Args.GetDoubleOrDefault(arguments, "sill_height_mm", 0);

        foreach (var group in groups)
        {
            if (typeMap.TryGetValue(group.Name, out var mapped))
            {
                var s = Find(mapped);
                group.SelectedChoice = new Door.SymbolChoice(s, Lookup.FullName(s));
            }
            else if (group.SelectedChoice == null && newFamily != null && heights.TryGetValue(group.Name, out var h))
            {
                group.SelectedFamily = newFamily;
                group.CustomHeightMm = h;
            }
            else if (group.SelectedChoice == null && defaultType != null)
                group.SelectedChoice = new Door.SymbolChoice(defaultType, Lookup.FullName(defaultType));

            group.SillHeightMm = sills.TryGetValue(group.Name, out var gs) ? gs : sill;
        }

        object Groups() => Table.Of(groups.Select(g => new
        {
            name = g.Name, count = g.Count, no_host = g.NoHostCount, type = g.SelectedChoice?.DisplayName,
            width_mm = Math.Round(g.CustomWidthMm), sill_mm = g.SillHeightMm,
        }));

        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new { cad_id = cad.Id.Value, openings = instances.Count, named = instances.Count(i => i.TagName != null), tag_source = tagSource, groups = Groups() };

        var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        var mode = ArgsX.Enum(arguments, "sill_from", "level", "level", "finished_floor") == "finished_floor"
            ? Door.SillHeightMode.FromFinishedFloor : Door.SillHeightMode.FromLevel;
        var results = Door.DoorWindowPlacer.Place(doc, view, groups, mode, grids,
            Args.GetStringOrDefault(arguments, "width_parameter", "Width")!,
            Args.GetStringOrDefault(arguments, "height_parameter", "Height")!);

        return new
        {
            created = results.Count(r => r.Outcome == Door.PlacementOutcome.Created),
            skipped_no_type = results.Count(r => r.Outcome == Door.PlacementOutcome.SkippedNoType),
            skipped_no_wall = results.Count(r => r.Outcome == Door.PlacementOutcome.SkippedNoHostWall),
            failed = results.Count(r => r.Outcome == Door.PlacementOutcome.Failed),
            groups = Groups(),
            errors = results.Where(r => r.ErrorMessage != null).Select(r => r.ErrorMessage).Distinct().Take(10).ToList(),
        };
    }
}

public sealed class CreateBeamTypes : RevitCommandBase
{
    public override string Name => "create_beam_types";
    public override string Category => "Dựng hình CAD";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Tạo type dầm b×h trong một family dầm, từ mặt cắt dầm trên layer CAD hoặc từ danh sách kích thước. Bỏ qua kích thước đã có.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "beam_family": { "type": "string", "description": "Family dầm có tham số b, h." },
            "sizes": { "type": "array", "items": { "type": "object", "properties": { "b_mm": { "type": "number" }, "h_mm": { "type": "number" } } }, "description": "Kích thước cần tạo (thay cho CAD)." },
            "cad_id": { "type": "integer", "description": "Id file CAD mặt cắt." },
            "layers": { "type": "array", "items": { "type": "string" }, "description": "Layer khung mặt cắt dầm." },
            "dry_run": { "type": "boolean", "description": "Chỉ phân tích." }
          },
          "required": ["beam_family"]
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var (cad, layers) = CadPick.Layers(context, "Click vào khung mặt cắt dầm trong CAD");
        var family = new FilteredElementCollector(context.Document).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.FamilyCategory?.BuiltInCategory == BuiltInCategory.OST_StructuralFraming)
            .OrderBy(f => f.Name).FirstOrDefault()
            ?? throw new CommandException("Dự án chưa có family dầm (Structural Framing).");
        return new { cad_id = cad.Id.Value, layers, beam_family = family.Name };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var family = ArgsX.Family(doc, Args.GetString(arguments, "beam_family"), BuiltInCategory.OST_StructuralFraming);
        var familyChoice = new BeamType.FamilyChoice(family, family.Name);

        List<BeamType.RawBeamSection> sections;
        if (Args.Has(arguments, "sizes"))
        {
            sections = Args.GetArray(arguments, "sizes").Select(s => new BeamType.RawBeamSection
            {
                Center = XYZ.Zero,
                BFeet = ArgsX.Mm(s, "b_mm", double.NaN) is var b && !double.IsNaN(b) ? b : throw new CommandException("Thiếu b_mm."),
                HFeet = ArgsX.Mm(s, "h_mm", double.NaN) is var h && !double.IsNaN(h) ? h : throw new CommandException("Thiếu h_mm."),
                Segments = [],
            }).ToList();
        }
        else
        {
            var cad = Cad.Resolve(doc, arguments);
            var view = doc.ActiveView is { IsTemplate: false } active && cad.ViewSpecific == false ? active : Cad.ScanView(doc, cad, null);
            var scan = BeamType.CadBeamScanner.Scan(doc, cad, view);
            var layers = Cad.Layers(arguments, scan.ColumnsByLayer.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
            sections = layers.SelectMany(l => scan.ColumnsByLayer[l]).ToList();
        }

        var groups = BeamType.BeamTypeCreator.GroupColumns(sections, familyChoice);
        object Groups() => Table.Of(groups.Select(g => new
        {
            size = g.SizeLabel, count = g.Columns.Count, existing = g.Status == BeamType.MatchStatus.Existing, error = g.ErrorMessage,
        }));
        if (Args.GetBoolOrDefault(arguments, "dry_run", false))
            return new { sections = sections.Count, groups = Groups() };

        var summary = BeamType.BeamTypeCreator.CreateTypes(doc, groups, familyChoice);
        return new { created = summary.Created, skipped_existing = summary.Skipped, failed = summary.Failed, groups = Groups(), errors = summary.Errors };
    }
}
