namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

/// <summary>
/// Dim thép đúng chuẩn Revit: MultiReferenceAnnotation ("Multi-rebar annotation") gắn reference vào chính các thanh thép,
/// không phải vẽ line giả theo thép rồi dim vào line. Mỗi dải thép (Rebar có nhiều thanh) được một đường dim chạy theo
/// phương rải, đặt ra ngoài khung bao của dải.
/// </summary>
public sealed class DimensionRebarCommand : RevitCommandBase
{
    public override string Name => "dimension_rebar";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Dim thép", "dimension");
    public override string Description =>
        "Dim khoảng cách các thanh trong dải thép ở view hiện tại (Multi-rebar annotation: dim bám thẳng vào thép, cập nhật theo thép). " +
        "Chọn dải thép hoặc cấu kiện chứa thép; trả về id dim và khoảng cách đo được.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Dải thép hoặc cấu kiện chứa thép; trống = thép trong view." },
            "offset_mm": { "type": "number", "description": "Khoảng cách đường dim tới mép dải thép, mặc định 500." },
            "annotation_type": { "type": "string", "description": "Tên Multi-Rebar Annotation Type; mặc định type đầu tiên." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context) =>
        new { element_ids = RebarScope.SelectedIds(context) };

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = RebarScope.ActiveModelView(context);
        var scope = RebarScope.Resolve(context, arguments, "active_view");
        var rebars = scope.ShapeRebars.Where(r => r.NumberOfBarPositions > 1).ToList();
        if (rebars.Count == 0)
            throw new CommandException($"Không có dải thép (Rebar nhiều thanh) nào trong {scope.Scope}.");

        var typeName = ArgsX.String(arguments, "annotation_type");
        var types = new FilteredElementCollector(doc).OfClass(typeof(MultiReferenceAnnotationType)).Cast<MultiReferenceAnnotationType>().ToList();
        var annotationType = typeName != null
            ? types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
              ?? throw new CommandException($"Không có Multi-Rebar Annotation Type '{typeName}'. Có: {string.Join(", ", types.Select(t => t.Name))}.")
            : types.FirstOrDefault() ?? MultiReferenceAnnotationType.CreateDefault(doc);

        var offset = UnitConvert.MmToFeet(ArgsX.Double(arguments, "offset_mm") ?? 500);
        var normal = view.ViewDirection;
        var rows = new List<object>();
        var skipped = new List<string>();

        foreach (var rebar in rebars)
        {
            // Phương rải thép chiếu lên mặt phẳng view: thép rải vuông góc mặt view (vd nhìn mặt cắt dọc thanh) thì không dim được.
            var path = rebar.GetShapeDrivenAccessor().GetDistributionPath();
            var along = path.Direction - normal.Multiply(path.Direction.DotProduct(normal));
            if (along.GetLength() < 1e-6) { skipped.Add($"{rebar.Id.Value}: phương rải vuông góc với view"); continue; }
            along = along.Normalize();
            var side = normal.CrossProduct(along).Normalize();

            var box = rebar.get_BoundingBox(view) ?? rebar.get_BoundingBox(null);
            if (box == null) { skipped.Add($"{rebar.Id.Value}: không lấy được khung bao"); continue; }
            var center = (box.Min + box.Max) / 2;
            var corners = new[] { box.Min, box.Max, new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X, box.Min.Y, box.Max.Z), new XYZ(box.Max.X, box.Max.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Max.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z) };
            var reach = corners.Max(c => (c - center).DotProduct(side));

            var options = new MultiReferenceAnnotationOptions(annotationType);
            options.SetElementsToDimension(new List<ElementId> { rebar.Id });
            options.DimensionStyleType = DimensionStyleType.Linear;
            options.DimensionLineDirection = along;
            options.DimensionPlaneNormal = normal;
            options.DimensionLineOrigin = center + side * (reach + offset);
            options.TagHeadPosition = center + side * (reach + offset * 2);
            options.TagHasLeader = false;

            if (!MultiReferenceAnnotation.AreReferencesValidForLinearDimension(doc, view.Id, options))
            {
                skipped.Add($"{rebar.Id.Value}: Revit không nhận reference thép ở view này (thử view mặt bằng/mặt cắt thấy rõ các thanh)");
                continue;
            }

            var annotation = MultiReferenceAnnotation.Create(doc, view.Id, options);
            doc.Regenerate();
            var dimension = doc.GetElement(annotation.DimensionId) as Dimension;
            var spacing = dimension?.Segments.Cast<DimensionSegment>().Select(s => Math.Round(UnitConvert.FeetToMm(s.Value ?? 0))).Distinct().ToList();
            rows.Add(new
            {
                rebar_id = rebar.Id.Value,
                bars = rebar.NumberOfBarPositions,
                dimension_id = annotation.DimensionId.Value,
                spacing_mm = spacing is { Count: > 0 } ? string.Join(" / ", spacing) : (dimension?.Value is { } v ? Math.Round(UnitConvert.FeetToMm(v)).ToString() : null),
            });
        }

        if (rows.Count == 0) throw new CommandException("Không dim được dải thép nào: " + string.Join("; ", skipped));
        return new { view = view.Name, created = rows.Count, dimensions = Table.Of(rows), skipped = skipped.Count > 0 ? skipped : null };
    }
}
