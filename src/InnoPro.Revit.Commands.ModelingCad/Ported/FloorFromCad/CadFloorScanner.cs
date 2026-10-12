using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.FloorFromCad;

/// <summary>Đọc hình học HATCH từ 1 ImportInstance, gộp theo tên layer CAD — khác hẳn cách đọc
/// Line/PolyLine/Arc của WallFromCad\CadWallScanner.cs / PlaceDoorWindow\CadOpeningScanner.cs (những
/// tool đó chỉ cần cặp nét thẳng/cung để suy ra tường/lỗ mở); HATCH là 1 vùng tô kín — khi Revit import
/// DWG, mỗi HATCH xuất hiện trong luồng hình học dưới dạng 1 <see cref="Solid"/> mỏng (gần như phẳng)
/// mang đúng 1 <see cref="Face"/> có biên trùng đường bao hatch (loop diện tích lớn nhất) và các loop lỗ
/// khoét bên trong (nếu hatch có "island") — quét CẢ Solid.Faces LẪN Face xuất hiện trực tiếp (một vài
/// trường hợp/phiên bản Revit trả thẳng Face không bọc Solid) để chắc ăn.</summary>
internal static class CadFloorScanner
{
    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    /// <summary>Bỏ qua vùng hatch quá nhỏ (100mm x 100mm) — rác hình học/ký hiệu vật liệu nhỏ lẫn trong
    /// layer, không phải 1 ô sàn thật.</summary>
    private static readonly double MinAreaSqFeet =
        UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters) *
        UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters);

    public static CadFloorScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var byLayer = new Dictionary<string, List<RawHatchRegion>>(StringComparer.Ordinal);

        var options = new Options { View = view, IncludeNonVisibleObjects = false };
        GeometryElement? geometryElement;
        try
        {
            geometryElement = instance.get_Geometry(options);
        }
        catch (Exception)
        {
            geometryElement = null;
        }

        if (geometryElement is not null)
            Walk(doc, geometryElement, byLayer, 0);

        return new CadFloorScanResult { RegionsByLayer = byLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement, Dictionary<string, List<RawHatchRegion>> byLayer, int depth)
    {
        if (depth > MaxRecursionDepth)
            return;

        foreach (var obj in geometryElement)
        {
            try
            {
                switch (obj)
                {
                    case GeometryInstance instance:
                        var nested = instance.GetInstanceGeometry();
                        if (nested is not null)
                            Walk(doc, nested, byLayer, depth + 1);
                        break;

                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face face in solid.Faces)
                        {
                            var styleId = face.GraphicsStyleId != ElementId.InvalidElementId
                                ? face.GraphicsStyleId
                                : solid.GraphicsStyleId;
                            AddFace(doc, face, styleId, byLayer);
                        }

                        break;

                    case Face face:
                        AddFace(doc, face, face.GraphicsStyleId, byLayer);
                        break;
                }
            }
            catch (Exception)
            {
                // 1 doi tuong hinh hoc bat thuong khong duoc lam sap toan bo qua trinh quet.
            }
        }
    }

    private static void AddFace(Document doc, Face face, ElementId styleId, Dictionary<string, List<RawHatchRegion>> byLayer)
    {
        if (face is not PlanarFace)
            return;

        IList<CurveLoop> loops;
        try
        {
            loops = face.GetEdgesAsCurveLoops();
        }
        catch (Exception)
        {
            return;
        }

        if (loops.Count == 0)
            return;

        var flattened = loops.Select(FlattenLoop).Where(l => l is not null).Select(l => l!).ToList();
        if (flattened.Count == 0)
            return;

        var withArea = flattened
            .Select(loop => (Loop: loop, Area: Math.Abs(ComputeSignedAreaXY(loop))))
            .OrderByDescending(x => x.Area)
            .ToList();

        if (withArea[0].Area < MinAreaSqFeet)
            return;

        var outer = withArea[0].Loop;
        var inner = withArea.Skip(1).Select(x => x.Loop).ToList();
        var layerName = ResolveLayerName(doc, styleId);
        var center = ComputeBoundingCenterXY(outer);

        if (!byLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawHatchRegion>();
            byLayer[layerName] = list;
        }

        list.Add(new RawHatchRegion
        {
            OuterLoop = outer,
            InnerLoops = inner,
            Center = center,
            AreaSqFeet = withArea[0].Area
        });
    }

    /// <summary>Chiếu 1 CurveLoop về mặt phẳng Z=0, giữ nguyên dạng Line/Arc — trả về null nếu có curve
    /// khác (Spline, Ellipse... hiếm gặp trong biên hatch, không dùng làm sketch sàn đơn giản được) hoặc
    /// loop suy biến (dưới 3 cạnh sau khi lọc đoạn dài 0).</summary>
    private static CurveLoop? FlattenLoop(CurveLoop loop)
    {
        var flatCurves = new List<Curve>();

        foreach (var curve in loop)
        {
            switch (curve)
            {
                case Line line:
                    var p0 = Flatten(line.GetEndPoint(0));
                    var p1 = Flatten(line.GetEndPoint(1));
                    if (p0.DistanceTo(p1) < 1e-6)
                        continue;
                    flatCurves.Add(Line.CreateBound(p0, p1));
                    break;

                case Arc arc:
                    var a0 = Flatten(arc.GetEndPoint(0));
                    var a1 = Flatten(arc.GetEndPoint(1));
                    var mid = Flatten(arc.Evaluate(0.5, true));
                    if (a0.DistanceTo(a1) < 1e-6)
                        continue;
                    flatCurves.Add(Arc.Create(a0, a1, mid));
                    break;

                default:
                    return null;
            }
        }

        if (flatCurves.Count < 3)
            return null;

        try
        {
            var result = new CurveLoop();
            foreach (var c in flatCurves)
                result.Append(c);
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static XYZ Flatten(XYZ p) => new(p.X, p.Y, 0);

    private static double ComputeSignedAreaXY(CurveLoop loop)
    {
        var points = new List<XYZ>();
        foreach (var curve in loop)
            points.AddRange(curve.Tessellate());

        if (points.Count < 3)
            return 0.0;

        double area = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }

    /// <summary>Tâm bounding box XY của loop — dùng làm điểm tra Text Note gần nhất (ổn định hơn centroid
    /// hình học thật với biên cong/lõm bất thường).</summary>
    private static XYZ ComputeBoundingCenterXY(CurveLoop loop)
    {
        var points = new List<XYZ>();
        foreach (var curve in loop)
            points.AddRange(curve.Tessellate());

        if (points.Count == 0)
            return XYZ.Zero;

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0, 0);
    }

    private static string ResolveLayerName(Document doc, ElementId styleId)
    {
        if (styleId == ElementId.InvalidElementId)
            return UnknownLayerName;

        if (doc.GetElement(styleId) is not GraphicsStyle style)
            return UnknownLayerName;

        var name = style.GraphicsStyleCategory?.Name;
        return string.IsNullOrWhiteSpace(name) ? UnknownLayerName : name;
    }
}
