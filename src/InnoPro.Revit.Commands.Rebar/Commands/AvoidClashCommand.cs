namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

public sealed class RebarAvoidClash : RevitCommandBase
{
    private const string DefaultCenterReference = "Center (Front/Back)";

    public override string Name => "rebar_avoid_clash";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Dịch riêng các thanh trong rebar set đang va vào vật cản (anchor, coupler, ống...) ra ngoài bán kính né, giữ khoảng cách thanh không vượt ngưỡng.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Vật cản cần né." },
            "rebar_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép cần xét; trống = tự tìm quanh vật cản." },
            "search_radius_mm": { "type": "number", "description": "Bán kính tìm thép, mặc định 150." },
            "filter_parameter": { "type": "string", "description": "Chỉ xét thép có tham số này..." },
            "filter_value": { "type": "string", "description": "...bằng giá trị này (ví dụ Ligs)." },
            "radius_mm": { "type": "number", "description": "Bán kính né, mặc định 75." },
            "clearance_mm": { "type": "number", "description": "Khoảng hở thêm, mặc định 0." },
            "max_gap_mm": { "type": "number", "description": "Khoảng cách thanh tối đa; trống = spacing hiện có." },
            "center_mode": { "type": "string", "enum": ["auto", "reference", "location", "bounding_box"], "description": "Cách lấy tâm vật cản, mặc định auto." },
            "center_reference": { "type": "string", "description": "Tên reference plane tâm, mặc định Center (Front/Back)." },
            "allow_mirror": { "type": "boolean", "description": "Cho đổi gương cả set, mặc định true." },
            "detach_host_first": { "type": "boolean", "description": "Ngắt host trước khi dịch." },
            "dry_run": { "type": "boolean", "description": "Chỉ tính, không dịch." }
          },
          "required": ["element_ids"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var obstacles = Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).Where(e => e != null).ToList();
        if (obstacles.Count == 0) throw new CommandException("Không tìm thấy vật cản nào trong element_ids.");

        var searchRadius = UnitConvert.MmToFeet(ArgsX.Double(arguments, "search_radius_mm") ?? 150);
        var zoneRadiusMm = (ArgsX.Double(arguments, "radius_mm") ?? 75) + (ArgsX.Double(arguments, "clearance_mm") ?? 0);
        var maxGapOverride = ArgsX.Double(arguments, "max_gap_mm");
        var centerMode = ArgsX.String(arguments, "center_mode") ?? "auto";
        var centerReference = ArgsX.String(arguments, "center_reference") ?? DefaultCenterReference;
        var filterParameter = ArgsX.String(arguments, "filter_parameter");
        var filterValue = ArgsX.String(arguments, "filter_value");
        var dryRun = Args.GetBoolOrDefault(arguments, "dry_run", false);

        // Tâm vật cản.
        var centers = new List<(Element Obstacle, XYZ Center, string Source)>();
        var problems = new List<string>();
        foreach (var obstacle in obstacles)
        {
            var center = ReadCenter(doc, obstacle!, centerMode, centerReference, out var source);
            if (center == null) problems.Add($"#{obstacle!.Id.Value}: {source}");
            else centers.Add((obstacle!, center, source));
        }
        if (centers.Count == 0) throw new CommandException("Không đọc được tâm vật cản nào. " + string.Join(" ", problems));

        // Thép ứng viên.
        IEnumerable<Rebar> candidates = ArgsX.Has(arguments, "rebar_ids")
            ? Args.GetElementIds(arguments, "rebar_ids").Select(doc.GetElement).OfType<Rebar>()
            : new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>();
        if (filterParameter != null)
            candidates = candidates.Where(r => string.Equals(RebarInfo.ParameterText(doc, r, filterParameter), filterValue ?? "", StringComparison.OrdinalIgnoreCase));
        var rebars = candidates.Where(r => centers.Any(c => IsNear(r, c.Center, searchRadius))).DistinctBy(r => r.Id).ToList();
        if (rebars.Count == 0) throw new CommandException("Không có thanh thép nào trong bán kính tìm quanh vật cản.");

        var plan = new List<MoveTarget>();
        var planRows = new List<object>();
        var reports = new List<object>();
        foreach (var rebar in rebars)
        {
            var frame = RebarFrame.From(rebar);
            if (frame == null)
            {
                reports.Add(new { rebar_id = rebar.Id.Value, status = "bỏ qua", message = "Không đọc được hình học." });
                continue;
            }

            var positions = RebarGeometry.ExistingPositions(rebar)
                .Select(i => (Index: i, Points: RebarGeometry.Points(rebar, i)))
                .Where(p => p.Points.Count > 0)
                .Select(p => new BarPosition(p.Index, p.Points.Average(x => x.DotProduct(frame.Front)) * 304.8))
                .ToList();
            var zones = centers.Where(c => IsNear(rebar, c.Center, searchRadius))
                .Select(c => new AnchorZone(c.Center.DotProduct(frame.Front) * 304.8, zoneRadiusMm, $"vật cản #{c.Obstacle.Id.Value}"))
                .ToList();

            double gapMax;
            if (maxGapOverride is { } overrideGap) gapMax = overrideGap;
            else if (!TryMedianGap(positions, out gapMax))
            {
                reports.Add(new { rebar_id = rebar.Id.Value, status = "bỏ qua", message = "Set chỉ có một thanh, cần max_gap_mm." });
                continue;
            }

            var result = ClashAvoidSolver.Solve(positions, gapMax, zones, Args.GetBoolOrDefault(arguments, "allow_mirror", true));
            if (!result.Ok)
            {
                reports.Add(new { rebar_id = rebar.Id.Value, status = "bế tắc", message = result.Message });
                continue;
            }

            foreach (var change in result.Changes)
            {
                plan.Add(new MoveTarget(rebar.Id, change.Index, frame.Front * UnitConvert.MmToFeet(change.DeltaMm)));
                planRows.Add(new { rebar_id = rebar.Id.Value, bar_index = change.Index, old_mm = Math.Round(change.OldMm, 1), new_mm = Math.Round(change.NewMm, 1), delta_mm = Math.Round(change.DeltaMm, 1) });
            }
            reports.Add(new
            {
                rebar_id = rebar.Id.Value,
                status = result.Changes.Count == 0 ? "không cần dịch" : "sẽ dịch",
                message = result.HasEndBarWarning ? result.EndBarWarning : $"{result.Changes.Count} thanh, khoảng cách tối đa {gapMax:F0} mm",
            });
        }

        DetachOutcome? detach = null;
        MoveOutcome? outcome = null;
        if (!dryRun && plan.Count > 0)
        {
            if (Args.GetBoolOrDefault(arguments, "detach_host_first", false))
                detach = MoveOps.DetachAttached(doc, plan.Select(p => p.RebarId));
            outcome = MoveOps.Apply(doc, plan);
        }

        return new
        {
            dry_run = dryRun,
            obstacles = centers.Select(c => new { id = c.Obstacle.Id.Value, center_source = c.Source }),
            obstacle_problems = problems,
            planned_moves = plan.Count,
            moved = outcome?.Applied ?? 0,
            max_drift_mm = outcome == null ? 0 : Math.Round(outcome.MaxDriftMm, 2),
            drifted_ids = outcome?.Drifted,
            failed = outcome?.FirstProblem,
            detached = detach?.Detached.Count ?? 0,
            rebars = Table.Of(reports),
            moves = Table.Of(planRows, 300),
        };
    }

    private static XYZ? ReadCenter(Document doc, Element obstacle, string mode, string referenceName, out string source)
    {
        if (mode is "auto" or "reference" && obstacle is FamilyInstance fi)
        {
            var center = CenterFromReference(doc, fi, referenceName);
            if (center != null)
            {
                source = $"reference '{referenceName}'";
                return center;
            }
        }
        if (mode == "reference")
        {
            source = $"không có reference plane '{referenceName}'";
            return null;
        }
        if (mode is "auto" or "location" && obstacle.Location is LocationPoint lp)
        {
            source = "điểm đặt";
            return lp.Point;
        }
        if (mode == "location")
        {
            source = "không có điểm đặt";
            return null;
        }
        var box = obstacle.get_BoundingBox(null);
        source = box == null ? "không có bounding box" : "tâm bounding box";
        return box == null ? null : (box.Min + box.Max) / 2;
    }

    /// <summary>Lấy gốc của reference plane đặt tên trong family: tạo SketchPlane tạm rồi hoàn tác.</summary>
    private static XYZ? CenterFromReference(Document doc, FamilyInstance instance, string name)
    {
        Reference? reference;
        try { reference = instance.GetReferenceByName(name); }
        catch (Exception) { return null; }
        if (reference == null) return null;

        using var sub = new SubTransaction(doc);
        sub.Start();
        try
        {
            return SketchPlane.Create(doc, reference).GetPlane().Origin;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            sub.RollBack();
        }
    }

    private static bool IsNear(Rebar rebar, XYZ point, double radius)
    {
        var box = rebar.get_BoundingBox(null);
        if (box == null) return false;
        var closest = new XYZ(Math.Clamp(point.X, box.Min.X, box.Max.X), Math.Clamp(point.Y, box.Min.Y, box.Max.Y), Math.Clamp(point.Z, box.Min.Z, box.Max.Z));
        return closest.DistanceTo(point) <= radius;
    }

    private static bool TryMedianGap(IReadOnlyList<BarPosition> positions, out double gap)
    {
        gap = 0;
        if (positions.Count < 2) return false;
        var sorted = positions.Select(p => p.PositionMm).OrderBy(v => v).ToList();
        var gaps = sorted.Skip(1).Select((v, i) => v - sorted[i]).OrderBy(v => v).ToList();
        gap = gaps[gaps.Count / 2];
        return gap > 0;
    }
}
