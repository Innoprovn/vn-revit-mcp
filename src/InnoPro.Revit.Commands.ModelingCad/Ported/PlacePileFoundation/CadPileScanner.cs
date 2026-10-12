using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Đọc circle (tim cọc) + PolyLine khép kín (đường bao đài) từ 1 ImportInstance, gộp theo layer.</summary>
internal static class CadPileScanner
{
    private static readonly double MinRadiusFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);
    private static readonly double CloseTolFeet = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);

    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    public static CadPileScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var circlesByLayer = new Dictionary<string, List<RawCircle>>(StringComparer.Ordinal);
        var boundariesByLayer = new Dictionary<string, List<RawBoundary>>(StringComparer.Ordinal);

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
            Walk(doc, geometryElement, circlesByLayer, boundariesByLayer, 0);

        return new CadPileScanResult { CirclesByLayer = circlesByLayer, BoundariesByLayer = boundariesByLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawCircle>> circlesByLayer, Dictionary<string, List<RawBoundary>> boundariesByLayer,
        int depth)
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
                            Walk(doc, nested, circlesByLayer, boundariesByLayer, depth + 1);
                        break;

                    case PolyLine polyLine:
                        AddPolyLineIfClosed(doc, polyLine, boundariesByLayer);
                        break;

                    case Arc arc:
                        if (!arc.IsBound)
                            AddCircle(doc, arc, circlesByLayer);
                        break;
                }
            }
            catch (Exception)
            {
                // Bo qua rieng 1 doi tuong loi, khong sap ca luot quet.
            }
        }
    }

    private static void AddCircle(Document doc, Arc arc, Dictionary<string, List<RawCircle>> circlesByLayer)
    {
        if (arc.Radius < MinRadiusFeet)
            return;

        var layerName = ResolveLayerName(doc, arc.GraphicsStyleId);
        var center = arc.Center;

        if (!circlesByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawCircle>();
            circlesByLayer[layerName] = list;
        }

        list.Add(new RawCircle { Center = new XYZ(center.X, center.Y, 0), RadiusFeet = arc.Radius });
    }

    /// <summary>Chỉ nhận PolyLine khép kín (điểm đầu ≈ điểm cuối, tối thiểu 3 cạnh) — bỏ qua polyline hở.</summary>
    private static void AddPolyLineIfClosed(Document doc, PolyLine polyLine, Dictionary<string, List<RawBoundary>> boundariesByLayer)
    {
        var raw = polyLine.GetCoordinates();
        if (raw.Count < 4)
            return;

        var flat = raw.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
        if (flat[0].DistanceTo(flat[^1]) > CloseTolFeet)
            return;

        var center = PolygonCentroid(flat);
        var (lengthFeet, widthFeet) = BoundingBoxExtents(flat);
        var layerName = ResolveLayerName(doc, polyLine.GraphicsStyleId);

        if (!boundariesByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawBoundary>();
            boundariesByLayer[layerName] = list;
        }

        list.Add(new RawBoundary { Center = center, LengthFeet = lengthFeet, WidthFeet = widthFeet, Points = flat });
    }

    /// <summary>Centroid đa giác qua công thức shoelace, fallback tâm bounding box nếu diện tích ~0.</summary>
    private static XYZ PolygonCentroid(List<XYZ> pts)
    {
        double area = 0, cx = 0, cy = 0;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[i];
            var p1 = pts[i + 1];
            var cross = p0.X * p1.Y - p1.X * p0.Y;
            area += cross;
            cx += (p0.X + p1.X) * cross;
            cy += (p0.Y + p1.Y) * cross;
        }

        area *= 0.5;
        if (Math.Abs(area) < 1e-9)
            return BoundingBoxCenter(pts);

        return new XYZ(cx / (6 * area), cy / (6 * area), 0);
    }

    /// <summary>Bounding box TRỤC X/Y THẾ GIỚI (chưa xử lý rectangle xoay lệch — đài xoay sẽ ra Length/Width lớn hơn thật).</summary>
    private static (double LengthFeet, double WidthFeet) BoundingBoxExtents(List<XYZ> pts)
    {
        var dx = pts.Max(p => p.X) - pts.Min(p => p.X);
        var dy = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        return dx >= dy ? (dx, dy) : (dy, dx);
    }

    private static XYZ BoundingBoxCenter(List<XYZ> pts)
    {
        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        return new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0);
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
