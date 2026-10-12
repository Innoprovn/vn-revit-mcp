using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

public sealed class TagElementsInView : RevitCommandBase
{
    private const int MaxElements = 3000;

    public override string Name => "tag_elements_in_view";
    public override string Category => "Bản vẽ";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Tag tự động", "tag");
    public override string Description =>
        "Gắn tag hàng loạt cho một category trong view, chọn loại tag, leader, hướng; bỏ qua phần tử đã có tag.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Ví dụ OST_Doors, OST_Rooms." },
            "tag_type": { "type": "string", "description": "Trống = tag mặc định." },
            "view": { "type": "string", "description": "Tên view. Bỏ trống = view đang mở." },
            "only_element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Chỉ tag các id này." },
            "leader": { "type": "boolean", "description": "Có leader." },
            "orientation": { "type": "string", "enum": ["horizontal", "vertical"], "description": "Mặc định horizontal." },
            "offset_mm": { "type": "number", "description": "Dời đầu tag (mm giấy), mặc định 8." },
            "skip_tagged": { "type": "boolean", "description": "Bỏ qua phần tử đã có tag." }
          },
          "required": ["category"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = DocLookup.ViewOrActive(doc, Args.GetStringOrDefault(arguments, "view"));
        if (view is ViewSheet or ViewSchedule || view.ViewType is ViewType.Legend or ViewType.DraftingView)
            throw new CommandException($"View '{view.Name}' ({view.ViewType}) không chứa phần tử mô hình để tag.");
        if (view is View3D { IsLocked: false })
            throw new CommandException("View 3D phải được khóa (Lock 3D view) trước khi tag.");

        var category = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category"));
        var isRoom = category.BuiltInCategory == BuiltInCategory.OST_Rooms;
        if (isRoom && view is not ViewPlan) throw new CommandException("Room tag chỉ đặt được trên view mặt bằng.");

        ElementId? tagTypeId = null;
        if (Args.GetStringOrDefault(arguments, "tag_type") is { } tagName && !string.IsNullOrWhiteSpace(tagName))
            tagTypeId = Lookup.Type<FamilySymbol>(doc, tagName).Id;

        var leader = Args.GetBoolOrDefault(arguments, "leader", false);
        var orientation = DocLookup.Choice(arguments, "orientation", "horizontal", "horizontal", "vertical") == "vertical"
            ? TagOrientation.Vertical : TagOrientation.Horizontal;
        var offset = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "offset_mm", 8)) * view.Scale;
        var skipTagged = Args.GetBoolOrDefault(arguments, "skip_tagged", true);

        IEnumerable<Element> elements = new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType();
        if (Args.Has(arguments, "only_element_ids"))
        {
            var wanted = Args.GetElementIds(arguments, "only_element_ids").Select(id => id.Value).ToHashSet();
            elements = elements.Where(e => wanted.Contains(e.Id.Value));
        }
        var targets = elements.Take(MaxElements + 1).ToList();
        if (targets.Count > MaxElements)
            throw new CommandException($"View có hơn {MaxElements} phần tử category này. Hãy giới hạn bằng element_ids.");

        var tagged = skipTagged ? TaggedIds(doc, view) : [];
        var created = new List<long>();
        var skipped = 0;
        var failures = new Dictionary<string, int>();
        foreach (var element in targets)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (tagged.Contains(element.Id.Value)) { skipped++; continue; }
            var point = TagPoint(element, view);
            if (point == null) { Count(failures, "Không xác định được vị trí phần tử"); continue; }
            try
            {
                Element tag;
                if (isRoom)
                {
                    var room = (Room)element;
                    if (room.Area <= 0) { Count(failures, "Phòng chưa khép kín hoặc chưa đặt"); continue; }
                    tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id);
                    if (tagTypeId != null) tag.ChangeTypeId(tagTypeId);
                }
                else
                {
                    var head = leader ? point + view.UpDirection * offset : point;
                    var reference = new Reference(element);
                    tag = tagTypeId != null
                        ? IndependentTag.Create(doc, tagTypeId, view.Id, reference, leader, orientation, head)
                        : IndependentTag.Create(doc, view.Id, reference, leader, TagMode.TM_ADDBY_CATEGORY, orientation, head);
                }
                created.Add(tag.Id.Value);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Count(failures, ex.Message);
            }
        }

        if (created.Count == 0 && failures.Count > 0 && targets.Count > 0)
            throw new CommandException(
                $"Không tạo được tag nào. Lỗi: {string.Join("; ", failures.Keys.Take(3))}. " +
                "Kiểm tra đã nạp family tag cho category này chưa, hoặc truyền tag_type.");

        return new
        {
            view = view.Name,
            category = category.Name,
            elements = targets.Count,
            created = created.Count,
            skipped_already_tagged = skipped,
            failed = failures.Count == 0 ? null : Table.Of(failures.Select(f => new { reason = f.Key, count = f.Value })),
            tag_ids = created.Take(200),
        };
    }

    private static void Count(Dictionary<string, int> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;

    private static HashSet<long> TaggedIds(Document doc, View view)
    {
        var ids = new HashSet<long>();
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
        {
            try { foreach (var id in tag.GetTaggedLocalElementIds()) ids.Add(id.Value); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).OfType<RoomTag>())
        {
            if (tag.Room is { } room) ids.Add(room.Id.Value);
        }
        return ids;
    }

    /// <summary>Điểm đặt tag: điểm đặt / trung điểm đường định vị, không có thì tâm hộp bao trong view.</summary>
    private static XYZ? TagPoint(Element element, View view)
    {
        switch (element.Location)
        {
            case LocationPoint lp:
                return lp.Point;
            case LocationCurve { Curve: { } curve }:
                return curve.Evaluate(0.5, true);
        }
        var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        return box == null ? null : (box.Min + box.Max) / 2;
    }
}

public sealed class DimensionGrids : RevitCommandBase
{
    public override string Name => "dimension_grids";
    public override string Category => "Bản vẽ";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Dim trục", "dimension");
    public override string Description =>
        "Tạo chuỗi kích thước và kích thước tổng cho lưới trục trên view mặt bằng.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "view": { "type": "string", "description": "Trống = view đang mở." },
            "sides": { "type": "string", "enum": ["start", "end", "both"], "description": "Đầu trục đặt dim." },
            "overall": { "type": "boolean", "description": "Thêm dim tổng." },
            "offset_mm": { "type": "number", "description": "Cách đầu trục (mm giấy), mặc định 15." },
            "spacing_mm": { "type": "number", "description": "Cách giữa hai dòng dim (mm giấy)." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = DocLookup.ViewOrActive(doc, Args.GetStringOrDefault(arguments, "view"));
        if (view is not ViewPlan)
            throw new CommandException($"View '{view.Name}' không phải mặt bằng. Lệnh này chỉ chạy trên view mặt bằng.");

        var sides = DocLookup.Choice(arguments, "sides", "both", "start", "end", "both");
        var overall = Args.GetBoolOrDefault(arguments, "overall", true);
        var offset = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "offset_mm", 15)) * view.Scale;
        var spacing = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "spacing_mm", 8)) * view.Scale;
        var z = view.GenLevel?.Elevation ?? 0;

        var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        if (grids.Count < 2) throw new CommandException("View có ít hơn 2 trục thẳng đang hiện.");
        var axes = GridAxes.Build(grids);

        var created = new List<object>();
        var notes = new List<string>();
        foreach (var (group, measureAlongU) in new[] { (axes.CrossU, true), (axes.CrossV, false) })
        {
            if (group.Count < 2) continue;
            var measure = measureAlongU ? axes.U : axes.V; // phương đo (vuông góc với trục)
            var along = measureAlongU ? axes.V : axes.U;   // phương chạy của trục
            var sorted = group.OrderBy(g => g.Coordinate).ToList();

            // Phạm vi đầu trục theo phương chạy của trục, lấy theo hình hiển thị thật trong view.
            var ends = sorted.SelectMany(g => ExtentsInView(g.Grid, view)).Select(p => GridAxes.Dot2(p, along)).ToList();
            var startEnd = ends.Min();
            var endEnd = ends.Max();

            var positions = new List<(string Side, double Chain, double Total)>();
            if (sides is "start" or "both") positions.Add(("start", startEnd + offset, startEnd + offset - spacing));
            if (sides is "end" or "both") positions.Add(("end", endEnd - offset, endEnd - offset + spacing));

            foreach (var (side, chainAt, totalAt) in positions)
            {
                var chain = Create(doc, view, sorted, measure, along, chainAt, z);
                if (chain != null) created.Add(new { direction = measureAlongU ? "U" : "V", side, kind = "chain", element_id = chain.Id.Value, grids = sorted.Count });
                else notes.Add($"Không tạo được chuỗi kích thước phương {(measureAlongU ? "U" : "V")} phía {side}.");

                if (overall && sorted.Count > 2)
                {
                    var total = Create(doc, view, [sorted[0], sorted[^1]], measure, along, totalAt, z);
                    if (total != null) created.Add(new { direction = measureAlongU ? "U" : "V", side, kind = "overall", element_id = total.Id.Value, grids = 2 });
                }
            }
        }

        if (created.Count == 0)
            throw new CommandException("Không tạo được kích thước nào. " + (notes.Count > 0 ? string.Join(" ", notes) : "Cần ít nhất 2 trục song song."));
        return new { view = view.Name, dimensions = Table.Of(created), notes = notes.Count == 0 ? null : notes };
    }

    private static IEnumerable<XYZ> ExtentsInView(Grid grid, View view)
    {
        IList<Curve> curves;
        try { curves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { curves = []; }
        if (curves.Count == 0) curves = [grid.Curve];
        foreach (var c in curves)
        {
            yield return c.GetEndPoint(0);
            yield return c.GetEndPoint(1);
        }
    }

    private static Dimension? Create(Document doc, View view, IReadOnlyList<GridOnAxis> grids, XYZ measure, XYZ along, double alongAt, double z)
    {
        var references = new ReferenceArray();
        foreach (var g in grids) references.Append(new Reference(g.Grid));
        var p1 = measure * grids[0].Coordinate + along * alongAt;
        var p2 = measure * grids[^1].Coordinate + along * alongAt;
        if (p1.DistanceTo(p2) < doc.Application.ShortCurveTolerance) return null;
        try
        {
            return doc.Create.NewDimension(view, Line.CreateBound(new XYZ(p1.X, p1.Y, z), new XYZ(p2.X, p2.Y, z)), references);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }
}

public sealed class DimToNearestGrid : RevitCommandBase
{
    private const double SameFaceTolerance = 0.001;
    private const double NormalTolerance = 0.99;

    public override string Name => "dim_to_nearest_grid";
    public override string Category => "QA-QC";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Dim tới trục", "dim-grid");
    public override string Description =>
        "Tạo dim từ mép cột, đài móng tới trục gần nhất theo hai phương; tùy chọn dịch cấu kiện để số dim tròn.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Cột, đài móng." },
            "view": { "type": "string", "description": "Trống = view đang mở." },
            "offset_mm": { "type": "number", "description": "Đường dim cách cấu kiện (mm), mặc định 500." },
            "round_by_moving": { "type": "boolean", "description": "Dịch cấu kiện cho dim tròn số." },
            "round_unit_mm": { "type": "number", "description": "Bội làm tròn (mm), mặc định 1." }
          },
          "required": ["element_ids"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = DocLookup.ViewOrActive(doc, Args.GetStringOrDefault(arguments, "view")) as ViewPlan
                   ?? throw new CommandException("Lệnh chỉ chạy trên view mặt bằng (Floor Plan / Ceiling Plan).");
        var offset = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "offset_mm", 500));
        var round = Args.GetBoolOrDefault(arguments, "round_by_moving", false);
        var unitMm = Args.GetDoubleOrDefault(arguments, "round_unit_mm", 1);
        if (unitMm <= 0) throw new CommandException("'round_unit_mm' phải lớn hơn 0.");

        var axes = GridAxes.Build(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>());
        if (axes.CrossU.Count == 0 && axes.CrossV.Count == 0) throw new CommandException("Mô hình không có trục thẳng nào.");

        var results = new List<object>();
        var skips = new List<object>();
        var moved = 0;
        foreach (var id in Args.GetElementIds(arguments, "element_ids"))
        {
            var element = doc.GetElement(id);
            if (element == null) { skips.Add(new { element_id = id.Value, axis = "-", reason = "Không tồn tại" }); continue; }

            foreach (var alongU in new[] { true, false })
            {
                var axisLabel = alongU ? "X" : "Y";
                var axis = alongU ? axes.U : axes.V;
                var perp = alongU ? axes.V : axes.U;

                var faces = Faces(element, view, axis);
                if (faces.Count == 0) { skips.Add(new { element_id = id.Value, axis = axisLabel, reason = "Không có mặt phẳng song song trục (cấu kiện tròn hoặc xoay lệch)" }); continue; }

                if (round && BestShift(faces, alongU, axes, unitMm) is { } shift && Math.Abs(shift) > UnitConvert.MmToFeet(0.05))
                {
                    ElementTransformUtils.MoveElement(doc, element.Id, axis * shift);
                    doc.Regenerate();
                    moved++;
                    faces = Faces(element, view, axis);
                    if (faces.Count == 0) continue;
                }

                var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
                if (box == null) { skips.Add(new { element_id = id.Value, axis = axisLabel, reason = "Không có hộp bao" }); continue; }
                var (bmin, bmax) = Geo.Transform(box.Min, box.Max, box.Transform);
                // Đường dim nằm ngoài cấu kiện: nhỏ nhất trên 4 góc mặt bằng (đúng cả khi lưới xoay) trừ offset.
                var lineAt = new[] { bmin, bmax, new XYZ(bmin.X, bmax.Y, 0), new XYZ(bmax.X, bmin.Y, 0) }
                    .Min(c => GridAxes.Dot2(c, perp)) - offset;
                var z = view.GenLevel?.Elevation ?? bmin.Z;

                foreach (var (face, side) in Boundaries(faces))
                {
                    var grid = axes.Nearest(alongU, face.Coordinate);
                    if (grid == null) { skips.Add(new { element_id = id.Value, axis = axisLabel, reason = $"Không có trục theo phương {axisLabel}" }); continue; }

                    var p1 = axis * face.Coordinate + perp * lineAt;
                    var p2 = axis * grid.Coordinate + perp * lineAt;
                    if (p1.DistanceTo(p2) < doc.Application.ShortCurveTolerance)
                    {
                        skips.Add(new { element_id = id.Value, axis = axisLabel, reason = $"Mép {side} trùng trục {grid.Grid.Name}" });
                        continue;
                    }
                    var references = new ReferenceArray();
                    references.Append(face.Reference);
                    references.Append(new Reference(grid.Grid));
                    try
                    {
                        var dim = doc.Create.NewDimension(view, Line.CreateBound(new XYZ(p1.X, p1.Y, z), new XYZ(p2.X, p2.Y, z)), references);
                        if (dim == null) throw new CommandException("NewDimension trả về null");
                        results.Add(new
                        {
                            element_id = id.Value,
                            axis = axisLabel,
                            side,
                            grid = grid.Grid.Name,
                            distance_mm = UnitConvert.FeetToMm(Math.Abs(face.Coordinate - grid.Coordinate)),
                            dimension_id = dim.Id.Value,
                        });
                    }
                    catch (Exception ex) when (ex is CommandException or Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        skips.Add(new { element_id = id.Value, axis = axisLabel, reason = $"Revit từ chối tạo dim mép {side}: {ex.Message}" });
                    }
                }
            }
        }

        return new
        {
            view = view.Name,
            dimensions = Table.Of(results),
            moved_axes = moved,
            skipped = skips.Count == 0 ? null : Table.Of(skips),
        };
    }

    private sealed record BoundaryFace(Reference Reference, double Coordinate);

    /// <summary>Mặt phẳng có pháp tuyến gần song song phương đo, lấy theo view để Reference dùng được cho dim.</summary>
    private static List<BoundaryFace> Faces(Element element, View view, XYZ axis)
    {
        var result = new List<BoundaryFace>();
        var geometry = element.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view });
        if (geometry != null) Collect(geometry, axis, result);
        return result;
    }

    private static void Collect(GeometryElement geometry, XYZ axis, List<BoundaryFace> result)
    {
        foreach (var obj in geometry)
        {
            if (obj is GeometryInstance instance && instance.GetInstanceGeometry() is { } nested)
            {
                Collect(nested, axis, result);
                continue;
            }
            if (obj is not Solid solid || !Geo.HasVolume(solid)) continue;
            foreach (Face face in solid.Faces)
            {
                if (face is PlanarFace planar && planar.Reference != null && Math.Abs(planar.FaceNormal.DotProduct(axis)) > NormalTolerance)
                    result.Add(new BoundaryFace(planar.Reference, planar.Origin.DotProduct(axis)));
            }
        }
    }

    private static List<(BoundaryFace Face, string Side)> Boundaries(List<BoundaryFace> faces)
    {
        var min = faces.MinBy(f => f.Coordinate)!;
        var max = faces.MaxBy(f => f.Coordinate)!;
        var list = new List<(BoundaryFace, string)> { (min, "min") };
        if (Math.Abs(max.Coordinate - min.Coordinate) > SameFaceTolerance) list.Add((max, "max"));
        return list;
    }

    /// <summary>Khoảng dịch (feet, dọc phương đo) nhỏ nhất để một mép cách trục gần nhất đúng bội unitMm.</summary>
    private static double? BestShift(List<BoundaryFace> faces, bool alongU, GridAxes axes, double unitMm)
    {
        double? best = null;
        foreach (var (face, _) in Boundaries(faces))
        {
            if (axes.Nearest(alongU, face.Coordinate) is not { } grid) continue;
            var gapMm = (face.Coordinate - grid.Coordinate) * 304.8;
            var deltaMm = Math.Round(gapMm / unitMm, MidpointRounding.AwayFromZero) * unitMm - gapMm;
            var delta = UnitConvert.MmToFeet(deltaMm);
            if (best == null || Math.Abs(delta) < Math.Abs(best.Value)) best = delta;
        }
        return best;
    }
}
