namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

public sealed class RebarLocalMove : RevitCommandBase
{
    public override string Name => "rebar_local_move";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Dịch thép", "move");
    public override string Description =>
        "Dịch thép theo hệ trục local của thanh (lên/xuống/trái/phải/trước/sau) hoặc tới khoảng hở mong muốn so với một mặt hay thanh khác.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép cần dịch." },
            "direction": { "type": "string", "enum": ["up", "down", "left", "right", "front", "back"], "description": "Phương dịch local." },
            "distance_mm": { "type": "number", "description": "Khoảng dịch (mm)." },
            "bar_index": { "type": "integer", "description": "Chỉ dịch thanh này trong set." },
            "reference_rebar_id": { "type": "integer", "description": "Thanh tham chiếu để canh khoảng hở." },
            "reference_face": { "type": "string", "description": "Mặt tham chiếu (stable reference)." },
            "reference_plane": { "type": "object", "description": "Mặt tham chiếu {origin:{x,y,z}, normal:{x,y,z}} (mm)." },
            "target_gap_mm": { "type": "number", "description": "Khoảng hở mong muốn (mm)." },
            "axis": { "type": "string", "enum": ["front_back", "up_down", "left_right", "reference_normal"], "description": "Trục dịch khi canh khoảng hở." },
            "measure_from": { "type": "string", "enum": ["edge", "center"], "description": "Đo từ mép hay tâm, mặc định edge." },
            "detach_host_first": { "type": "boolean", "description": "Ngắt host trước khi dịch." },
            "measure_only": { "type": "boolean", "description": "Chỉ đo, không dịch." }
          },
          "required": ["element_ids"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var rebars = Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).OfType<Rebar>().DistinctBy(r => r.Id).ToList();
        if (rebars.Count == 0) throw new CommandException("Không có thanh Rebar nào trong element_ids (thép Area/Path Reinforcement không dịch riêng được).");

        var frame = RebarFrame.From(rebars[0]) ?? throw new CommandException($"Không đọc được mặt phẳng thép #{rebars[0].Id.Value}.");
        int? barIndex = ArgsX.Long(arguments, "bar_index") is { } bi ? (int)bi : null;
        if (barIndex != null && rebars.Count > 1) throw new CommandException("bar_index chỉ dùng với một thanh Rebar.");
        var measureOnly = Args.GetBoolOrDefault(arguments, "measure_only", false);

        // Điểm của thép cần dịch: một thanh, hoặc mọi vị trí trong set (khoảng hở đo trên thanh gần nhất).
        var movingPoints = rebars.SelectMany(r => RebarGeometry.Points(r, barIndex)).ToList();
        var radius = RebarInfo.ModelRadiusFeet(doc, rebars[0]);

        XYZ translation;
        double? currentGap = null;
        var reference = ReadReference(context, arguments);
        if (ArgsX.String(arguments, "direction") is { } directionName)
        {
            if (!Enum.TryParse<MoveDirection>(directionName, ignoreCase: true, out var direction))
                throw new CommandException("'direction' phải là up, down, left, right, front hoặc back.");
            var distance = ArgsX.Double(arguments, "distance_mm") ?? double.NaN;
            if (double.IsNaN(distance) && !measureOnly) throw new CommandException("Cần 'distance_mm' khi dịch theo 'direction'.");
            translation = double.IsNaN(distance) ? XYZ.Zero : frame.Of(direction) * UnitConvert.MmToFeet(distance);
        }
        else if (reference != null)
        {
            var axisName = ArgsX.String(arguments, "axis") ?? (reference.IsFace ? "reference_normal" : "front_back");
            var axis = axisName switch
            {
                "up_down" => frame.Up,
                "left_right" => frame.Right,
                "reference_normal" => reference.PlaneNormal ?? throw new CommandException("axis = reference_normal chỉ dùng với mặt tham chiếu."),
                _ => frame.Front,
            };
            var target = ArgsX.Double(arguments, "target_gap_mm");
            if (target == null && !measureOnly) throw new CommandException("Cần 'target_gap_mm' khi canh theo tham chiếu (hoặc measure_only = true).");
            var fromEdge = ArgsX.String(arguments, "measure_from") != "center";
            var solution = GapSolver.Solve(reference, movingPoints, radius, axis, fromEdge, target);
            if (!solution.Ok) throw new CommandException(solution.Message);
            currentGap = Math.Round(solution.CurrentMm, 2);
            translation = solution.Translation;
        }
        else if (measureOnly)
        {
            translation = XYZ.Zero;
        }
        else
        {
            throw new CommandException("Cần 'direction' + 'distance_mm', hoặc tham chiếu (reference_rebar_id / reference_face / reference_plane) + 'target_gap_mm'.");
        }

        if (measureOnly || translation.GetLength() < 1e-9)
            return new { moved = 0, frame = frame.Describe(), current_gap_mm = currentGap, reference = reference?.Label };

        DetachOutcome? detach = null;
        if (Args.GetBoolOrDefault(arguments, "detach_host_first", false))
            detach = MoveOps.DetachAttached(doc, rebars.Select(r => r.Id));

        var outcome = MoveOps.Apply(doc, rebars.Select(r => new MoveTarget(r.Id, barIndex, translation)).ToList());
        if (outcome.Applied == 0) throw new CommandException("Không dịch được thanh nào. " + outcome.FirstProblem);

        return new
        {
            moved = outcome.Applied,
            translation_mm = new[] { translation.X, translation.Y, translation.Z }.Select(v => Math.Round(v * 304.8, 2)),
            distance_mm = Math.Round(translation.GetLength() * 304.8, 2),
            gap_before_mm = currentGap,
            max_drift_mm = Math.Round(outcome.MaxDriftMm, 2),
            drifted_ids = outcome.Drifted,
            warning = outcome.Drifted.Count > 0 ? "Có thanh bị Revit kéo về (còn bám host). Chạy lại với detach_host_first = true." : null,
            unverified_ids = outcome.Unverified,
            failed_ids = outcome.Failed,
            first_problem = outcome.FirstProblem,
            detached = detach?.Detached.Count ?? 0,
            frame = frame.Describe(),
        };
    }

    private static GapReference? ReadReference(RevitCommandContext context, JsonElement args)
    {
        var doc = context.Document;
        if (ArgsX.Long(args, "reference_rebar_id") is { } refId)
        {
            var id = new ElementId(refId);
            if (doc.GetElement(id) is not Rebar bar) throw new CommandException($"#{id.Value} không phải Rebar.");
            return new GapReference
            {
                Label = $"Rebar #{id.Value}",
                BarPoints = RebarGeometry.Points(bar, null),
                BarRadius = RebarInfo.ModelRadiusFeet(doc, bar),
            };
        }

        if (ArgsX.Has(args, "reference_face"))
        {
            Reference reference;
            try { reference = Reference.ParseFromStableRepresentation(doc, Args.GetString(args, "reference_face")); }
            catch (Exception) { throw new CommandException("'reference_face' không phải tham chiếu mặt hợp lệ."); }
            var element = doc.GetElement(reference.ElementId);
            if (element?.GetGeometryObjectFromReference(reference) is not PlanarFace face)
                throw new CommandException("Mặt tham chiếu phải là mặt phẳng.");
            XYZ origin = face.Origin, normal = face.FaceNormal;
            if (element is FamilyInstance fi)
            {
                var t = fi.GetTotalTransform();
                origin = t.OfPoint(origin);
                normal = t.OfVector(normal);
            }
            return new GapReference { Label = $"Mặt của #{element.Id.Value}", PlaneOrigin = origin, PlaneNormal = normal.Normalize() };
        }

        if (ArgsX.Has(args, "reference_plane"))
        {
            var plane = args.GetProperty("reference_plane");
            var origin = Args.GetPointMm(plane, "origin");
            var n = Args.GetPointMm(plane, "normal");
            if (n.GetLength() < 1e-12) throw new CommandException("reference_plane.normal phải khác 0.");
            return new GapReference { Label = "Mặt phẳng chỉ định", PlaneOrigin = origin, PlaneNormal = n.Normalize() };
        }

        return null;
    }
}
