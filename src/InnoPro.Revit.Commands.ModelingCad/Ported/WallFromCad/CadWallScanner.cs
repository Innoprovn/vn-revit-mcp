using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFromCad;

/// <summary>Đọc hình học từ 1 ImportInstance (CAD import hoặc CAD link đều là ImportInstance trong
/// Revit API), gộp theo tên layer CAD (đọc qua GraphicsStyle của từng đối tượng hình học — đây là
/// cách chuẩn để lấy tên layer gốc của DWG/DXF sau khi import vào Revit).
///
/// Đọc cả nét thẳng (Line/PolyLine) lẫn nét cong tròn (Arc) — cả 2 loại đều dùng để nhận diện tường
/// (xem WallPairing: bắt cặp 2 nét song song hoặc 2 cung đồng tâm để đo bề rộng trực tiếp). Chỉ những
/// nét cong KHÁC (Ellipse/NurbSpline/
/// HermiteSpline...) mới thật sự bị bỏ qua, vì không có cách bắt cặp/đo bề rộng hợp lý cho các dạng
/// này trong phạm vi tool — chỉ đếm lại để báo cho người dùng biết.
///
/// Chỉ đọc, không mở transaction — an toàn để gọi trước khi mở cửa sổ WPF.</summary>
internal static class CadWallScanner
{
    /// <summary>Bỏ qua đoạn/cung ngắn hơn mức này (nhiễu làm tròn/điểm neo) — 5mm.</summary>
    private static readonly double MinSegmentLengthFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    public static CadScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var byLayer = new Dictionary<string, List<RawSegment>>(StringComparer.Ordinal);
        var arcsByLayer = new Dictionary<string, List<RawArc>>(StringComparer.Ordinal);
        var skippedCurved = new Dictionary<string, int>(StringComparer.Ordinal);

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
            Walk(doc, geometryElement, byLayer, arcsByLayer, skippedCurved, 0);

        return new CadScanResult
        {
            SegmentsByLayer = byLayer,
            ArcsByLayer = arcsByLayer,
            SkippedCurvedByLayer = skippedCurved
        };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawSegment>> byLayer, Dictionary<string, List<RawArc>> arcsByLayer,
        Dictionary<string, int> skippedCurved, int depth)
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
                        // GetInstanceGeometry() trả hình học đã áp transform của instance — không cần
                        // tự nhân transform tay.
                        var nested = instance.GetInstanceGeometry();
                        if (nested is not null)
                            Walk(doc, nested, byLayer, arcsByLayer, skippedCurved, depth + 1);
                        break;

                    case PolyLine polyLine:
                        AddPolyLine(doc, polyLine, byLayer);
                        break;

                    case Line line:
                        AddSegment(doc, line.GetEndPoint(0), line.GetEndPoint(1), line.GraphicsStyleId, byLayer);
                        break;

                    case Arc arc:
                        // Phải xét TRƯỚC case Curve chung bên dưới (Arc cũng là 1 loại Curve, pattern
                        // matching theo thứ tự khai báo).
                        if (arc.IsBound)
                        {
                            // Cung tròn thật (có điểm đầu/cuối) - giữ lại để bắt cặp/đo bề rộng (xem
                            // WallPairing), không còn bị đếm vào "bỏ qua" như bản trước.
                            AddArc(doc, arc, arcsByLayer);
                        }
                        else
                        {
                            // Vòng tròn KÍN (circle) trong CAD - Revit đọc thành Arc KHÔNG bound (không
                            // có điểm đầu/cuối, GetEndPoint sẽ ném lỗi nếu gọi) - không phải nét tường,
                            // chỉ đếm lại như nét cong bị bỏ qua.
                            CountSkippedCurved(doc, arc.GraphicsStyleId, skippedCurved);
                        }
                        break;

                    case Curve curve:
                        // Ellipse/NurbSpline/HermiteSpline... - không có cách bắt cặp/đo bề rộng hợp
                        // lý, chỉ đếm lại để báo cho người dùng biết.
                        CountSkippedCurved(doc, curve.GraphicsStyleId, skippedCurved);
                        break;

                    // Solid/Mesh/Point/... không liên quan tới nét vẽ mặt bằng 2D — bỏ qua.
                }
            }
            catch (Exception)
            {
                // 1 đối tượng hình học bất thường (CAD lỗi/không chuẩn, hình học suy biến...) không
                // được làm sập toàn bộ quá trình quét - bỏ qua đối tượng đó, quét tiếp các đối tượng
                // còn lại (bài học từ lỗi "the input curve is not bound" gặp phải với circle kín).
            }
        }
    }

    private static void CountSkippedCurved(Document doc, ElementId styleId, Dictionary<string, int> skippedCurved)
    {
        var layerName = ResolveLayerName(doc, styleId);
        skippedCurved[layerName] = skippedCurved.GetValueOrDefault(layerName) + 1;
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

    private static void AddSegment(
        Document doc, XYZ p0, XYZ p1, ElementId styleId, Dictionary<string, List<RawSegment>> byLayer)
    {
        var layerName = ResolveLayerName(doc, styleId);
        AddRaw(byLayer, layerName, p0, p1);
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

    /// <summary>Đọc 1 cung Arc, làm phẳng về Z=0 (giữ nguyên tâm + bán kính + 3 điểm đầu/giữa/cuối) —
    /// dùng để bắt cặp 2 cung đồng tâm, đo bề rộng trực tiếp. Xem WallPairing.DetectArcPairs.</summary>
    private static void AddArc(Document doc, Arc arc, Dictionary<string, List<RawArc>> arcsByLayer)
    {
        var length = arc.Length;
        if (length < MinSegmentLengthFeet)
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
            Mid = new XYZ(mid.X, mid.Y, 0),
            ArcLengthFeet = length
        });
    }

    /// <summary>Tên layer CAD gốc = tên subcategory (GraphicsStyleCategory) của GraphicsStyle. Đây là
    /// cách đọc layer DWG/DXF chuẩn sau khi Revit import — không có API nào đọc thẳng "layer name"
    /// khác cách này.</summary>
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
