using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Đọc hình học từ 1 ImportInstance, gộp theo tên layer CAD — rút gọn từ
/// WallFromCad\CadWallScanner.cs (2 project là 2 assembly tách biệt trong kiến trúc hot-reload, không
/// tham chiếu chéo được nên phải chép lại phần dùng chung). Chỉ cần Line/PolyLine/Arc, không cần đếm
/// nét cong bị bỏ qua (Ellipse/Spline...) vì tool này không báo cáo mục đó.</summary>
internal static class CadOpeningScanner
{
    private static readonly double MinSegmentLengthFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    public static CadScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var byLayer = new Dictionary<string, List<RawSegment>>(StringComparer.Ordinal);
        var arcsByLayer = new Dictionary<string, List<RawArc>>(StringComparer.Ordinal);

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
            Walk(doc, geometryElement, byLayer, arcsByLayer, 0);

        return new CadScanResult { SegmentsByLayer = byLayer, ArcsByLayer = arcsByLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawSegment>> byLayer, Dictionary<string, List<RawArc>> arcsByLayer,
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
                            Walk(doc, nested, byLayer, arcsByLayer, depth + 1);
                        break;

                    case PolyLine polyLine:
                        AddPolyLine(doc, polyLine, byLayer);
                        break;

                    case Line line:
                        AddRaw(byLayer, ResolveLayerName(doc, line.GraphicsStyleId),
                            line.GetEndPoint(0), line.GetEndPoint(1));
                        break;

                    case Arc arc:
                        if (arc.IsBound)
                            AddArc(doc, arc, arcsByLayer);
                        break;
                }
            }
            catch (Exception)
            {
                // 1 doi tuong hinh hoc bat thuong khong duoc lam sap toan bo qua trinh quet.
            }
        }
    }

    private static void AddPolyLine(Document doc, PolyLine polyLine, Dictionary<string, List<RawSegment>> byLayer)
    {
        var points = polyLine.GetCoordinates();
        if (points.Count < 2)
            return;

        var layerName = ResolveLayerName(doc, polyLine.GraphicsStyleId);
        for (var i = 0; i < points.Count - 1; i++)
            AddRaw(byLayer, layerName, points[i], points[i + 1]);
    }

    private static void AddRaw(Dictionary<string, List<RawSegment>> byLayer, string layerName, XYZ p0, XYZ p1)
    {
        var flatP0 = new XYZ(p0.X, p0.Y, 0);
        var flatP1 = new XYZ(p1.X, p1.Y, 0);
        var length = flatP0.DistanceTo(flatP1);
        if (length < MinSegmentLengthFeet)
            return;

        if (!byLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawSegment>();
            byLayer[layerName] = list;
        }

        list.Add(new RawSegment
        {
            P0 = flatP0,
            P1 = flatP1,
            Direction = (flatP1 - flatP0).Normalize(),
            Length = length
        });
    }

    private static void AddArc(Document doc, Arc arc, Dictionary<string, List<RawArc>> arcsByLayer)
    {
        if (arc.Length < MinSegmentLengthFeet)
            return;

        var layerName = ResolveLayerName(doc, arc.GraphicsStyleId);
        var p0 = arc.GetEndPoint(0);
        var p1 = arc.GetEndPoint(1);
        var mid = arc.Evaluate(0.5, true);
        var center = arc.Center;

        if (!arcsByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawArc>();
            arcsByLayer[layerName] = list;
        }

        list.Add(new RawArc
        {
            Center = new XYZ(center.X, center.Y, 0),
            Radius = arc.Radius,
            P0 = new XYZ(p0.X, p0.Y, 0),
            P1 = new XYZ(p1.X, p1.Y, 0),
            Mid = new XYZ(mid.X, mid.Y, 0)
        });
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
