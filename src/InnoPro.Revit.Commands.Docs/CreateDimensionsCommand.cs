using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

/// <summary>
/// Dim một chuỗi qua nhiều phần tử trên view đang mở: trục, mặt phẳng tham chiếu, tường (mặt tường), cột/family (đường tâm),
/// sàn/dầm (mặt bên). Đường dim do AI cho bằng hai điểm; chỉ lấy reference vuông góc với đường dim.
/// </summary>
public sealed class CreateDimensions : RevitCommandBase
{
    public override string Name => "create_dimensions";
    public override string Category => "Bản vẽ";
    public override string Description =>
        "Dim chuỗi qua các phần tử trên view đang mở (trục, tường, cột, family, mặt phẳng tham chiếu...) dọc đường dim từ start tới end (mm). " +
        "Tường: wall_faces = both (hai mặt, mặc định) | exterior | interior | center (mặt gần đường tâm nhất). Trả giá trị từng đoạn.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Phần tử cần dim, ít nhất 2 reference." },
            "start": { "type": "object", "description": "Điểm đầu đường dim {x,y,z} mm, nằm ở chỗ trống cạnh các phần tử." },
            "end": { "type": "object", "description": "Điểm cuối đường dim {x,y,z} mm. Hướng start→end là hướng đo." },
            "wall_faces": { "type": "string", "enum": ["both", "exterior", "interior", "center"] }
          },
          "required": ["element_ids", "start", "end"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = doc.ActiveView;
        var start = Args.GetPointMm(arguments, "start");
        var end = Args.GetPointMm(arguments, "end");
        if (start.DistanceTo(end) < 1e-3) throw new CommandException("start và end trùng nhau.");
        var direction = (end - start).Normalize();
        var line = Line.CreateBound(start, end);
        var mode = Args.GetStringOrDefault(arguments, "wall_faces", "both")!;

        var references = new List<(Reference Ref, double At, string From)>();
        var skipped = new List<long>();
        foreach (var id in Args.GetElementIds(arguments, "element_ids"))
        {
            var element = doc.GetElement(id);
            var found = element == null ? [] : ReferencesOf(element, view, direction, mode);
            if (found.Count == 0) { skipped.Add(id.Value); continue; }
            references.AddRange(found.Select(f => (f.Ref, f.Origin.DotProduct(direction), $"{id.Value} {element!.Name}")));
        }
        if (references.Count < 2)
            throw new CommandException("Cần ít nhất 2 reference vuông góc với đường dim. Kiểm tra hướng start→end và phần tử" +
                                       (skipped.Count > 0 ? $" (không lấy được: {string.Join(", ", skipped)})." : "."));

        // Bỏ reference trùng vị trí (vd mặt tường trùng mặt cột), xếp theo hướng đo.
        var ordered = references.OrderBy(r => r.At).Aggregate(new List<(Reference Ref, double At, string From)>(), (list, r) =>
        {
            if (list.Count == 0 || r.At - list[^1].At > 1e-4) list.Add(r);
            return list;
        });
        var array = new ReferenceArray();
        foreach (var r in ordered) array.Append(r.Ref);
        var dimension = doc.Create.NewDimension(view, line, array)
                        ?? throw new CommandException("Revit không tạo được dim với các reference này.");

        var segments = dimension.NumberOfSegments == 0
            ? [Math.Round(UnitConvert.FeetToMm(dimension.Value ?? 0))]
            : dimension.Segments.Cast<DimensionSegment>().Select(s => Math.Round(UnitConvert.FeetToMm(s.Value ?? 0))).ToList();
        return new
        {
            dimension_id = dimension.Id.Value,
            references = ordered.Count,
            segments_mm = segments,
            total_mm = Math.Round(UnitConvert.FeetToMm(ordered[^1].At - ordered[0].At)),
            skipped = skipped.Count == 0 ? null : skipped,
        };
    }

    private static List<(Reference Ref, XYZ Origin)> ReferencesOf(Element element, View view, XYZ direction, string wallMode)
    {
        bool Perpendicular(XYZ normal) => Math.Abs(Math.Abs(normal.Normalize().DotProduct(direction)) - 1) < 1e-3;

        switch (element)
        {
            case Grid grid when grid.Curve is Line g && Perpendicular(g.Direction.CrossProduct(view.ViewDirection)):
                return [(new Reference(grid), g.Origin)];
            case ReferencePlane plane when Perpendicular(plane.Normal):
                return [(plane.GetReference(), plane.BubbleEnd)];
            case FamilyInstance fi:
                // Cột, cửa...: đường tâm theo hướng phù hợp.
                foreach (var type in new[] { FamilyInstanceReferenceType.CenterLeftRight, FamilyInstanceReferenceType.CenterFrontBack })
                foreach (var r in fi.GetReferences(type))
                    if (fi.GetTransform() is { } t && Perpendicular(type == FamilyInstanceReferenceType.CenterLeftRight ? t.BasisX : t.BasisY))
                        return [(r, t.Origin)];
                break;
        }

        // Mặt phẳng vuông góc với đường dim trong hình học của phần tử (tường, sàn, dầm...).
        var faces = new List<(Reference Ref, XYZ Origin)>();
        var options = new Options { ComputeReferences = true, View = view, IncludeNonVisibleObjects = false };
        foreach (var solid in Solids(element.get_Geometry(options)))
        foreach (Face face in solid.Faces)
            if (face is PlanarFace pf && pf.Reference != null && Perpendicular(pf.FaceNormal))
                faces.Add((pf.Reference, pf.Origin));
        if (faces.Count == 0 || element is not Wall wall || wallMode == "both") return faces;

        var orientation = wall.Orientation; // hướng ra mặt ngoài
        var centre = (wall.Location as LocationCurve)?.Curve.Evaluate(0.5, true) ?? faces[0].Origin;
        return wallMode switch
        {
            "exterior" => [faces.MaxBy(f => (f.Origin - centre).DotProduct(orientation))],
            "interior" => [faces.MinBy(f => (f.Origin - centre).DotProduct(orientation))],
            _ => [faces.MinBy(f => Math.Abs((f.Origin - centre).DotProduct(direction)))],
        };
    }

    private static IEnumerable<Solid> Solids(GeometryElement? geometry)
    {
        if (geometry == null) yield break;
        foreach (var obj in geometry)
        {
            if (obj is Solid { Volume: > 0 } solid) yield return solid;
            else if (obj is GeometryInstance instance)
                foreach (var s in Solids(instance.GetInstanceGeometry())) yield return s;
        }
    }
}
