using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceColumn;

/// <summary>Đọc đường bao cột chữ nhật (có thể xoay) từ 1 ImportInstance, gộp theo layer — nhận cả PolyLine
/// (khép kín hoặc hở) lẫn cột được người vẽ CAD tách thành nhiều Line rời rạc từng cạnh một.</summary>
internal static class CadColumnScanner
{
    private static readonly double MinSizeFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    /// <summary>Ngưỡng coi 2 đầu Line là "nối nhau" khi gộp cụm Line rời rạc thành 1 đường bao.</summary>
    private static readonly double ConnectTolFeet = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);

    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    public static CadColumnScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var columnsByLayer = new Dictionary<string, List<RawColumn>>(StringComparer.Ordinal);
        var linesByLayer = new Dictionary<string, List<(XYZ A, XYZ B)>>(StringComparer.Ordinal);

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
            Walk(doc, geometryElement, columnsByLayer, linesByLayer, 0);

        // Cot duoc ve bang nhieu Line roi rac tung canh (khong noi thanh 1 PolyLine) - gop cac Line
        // cung layer co dau noi gan nhau thanh 1 cum, moi cum du diem thi quy ve hinh chu nhat y het
        // duong PolyLine ben tren.
        foreach (var (layerName, segments) in linesByLayer)
            AddLineClusters(layerName, segments, columnsByLayer);

        return new CadColumnScanResult { ColumnsByLayer = columnsByLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawColumn>> columnsByLayer,
        Dictionary<string, List<(XYZ A, XYZ B)>> linesByLayer, int depth)
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
                            Walk(doc, nested, columnsByLayer, linesByLayer, depth + 1);
                        break;

                    case PolyLine polyLine:
                        AddPolyLine(doc, polyLine, columnsByLayer);
                        break;

                    case Line line:
                        AddLine(doc, line, linesByLayer);
                        break;
                }
            }
            catch (Exception)
            {
                // Bo qua rieng 1 doi tuong loi, khong sap ca luot quet.
            }
        }
    }

    /// <summary>Nhận mọi PolyLine (khép kín hoặc hở, tối thiểu 3 điểm) — quy về hình chữ nhật xoay nhỏ nhất bao quanh.</summary>
    private static void AddPolyLine(Document doc, PolyLine polyLine, Dictionary<string, List<RawColumn>> columnsByLayer)
    {
        var raw = polyLine.GetCoordinates();
        if (raw.Count < 3)
            return;

        var flat = raw.Select(p => new XYZ(p.X, p.Y, 0)).ToList();

        var rect = MinAreaRectangle(flat);
        if (rect is null)
            return;

        var (center, lengthFeet, widthFeet, angleRadians) = rect.Value;
        if (lengthFeet < MinSizeFeet || widthFeet < MinSizeFeet)
            return;

        var layerName = ResolveLayerName(doc, polyLine.GraphicsStyleId);
        var segments = new List<(XYZ, XYZ)>();
        for (var i = 0; i < flat.Count - 1; i++)
            segments.Add((flat[i], flat[i + 1]));

        AddColumn(columnsByLayer, layerName, center, lengthFeet, widthFeet, angleRadians, segments);
    }

    /// <summary>Gom 1 Line CAD thô (Z=0) vào danh sách theo layer — xử lý gộp cụm dồn lại sau khi Walk xong toàn bộ hình học.</summary>
    private static void AddLine(Document doc, Line line, Dictionary<string, List<(XYZ A, XYZ B)>> linesByLayer)
    {
        XYZ p0, p1;
        try
        {
            p0 = line.GetEndPoint(0);
            p1 = line.GetEndPoint(1);
        }
        catch (Exception)
        {
            return;
        }

        var a = new XYZ(p0.X, p0.Y, 0);
        var b = new XYZ(p1.X, p1.Y, 0);
        if (a.DistanceTo(b) < 1e-9)
            return;

        var layerName = ResolveLayerName(doc, line.GraphicsStyleId);
        if (!linesByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<(XYZ, XYZ)>();
            linesByLayer[layerName] = list;
        }

        list.Add((a, b));
    }

    /// <summary>Gộp các Line rời rạc cùng layer thành từng cụm nối liền nhau (2 đầu gần nhau trong ConnectTolFeet
    /// coi là nối) — mỗi cụm đủ điểm thì quy về hình chữ nhật xoay nhỏ nhất y hệt đường PolyLine. Độ phức tạp
    /// O(n²) chấp nhận được vì mỗi layer cột chỉ vài trăm đến vài nghìn Line.</summary>
    private static void AddLineClusters(string layerName, List<(XYZ A, XYZ B)> segments, Dictionary<string, List<RawColumn>> columnsByLayer)
    {
        var used = new bool[segments.Count];

        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
                continue;

            var clusterIdx = new List<int> { i };
            used[i] = true;

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var j = 0; j < segments.Count; j++)
                {
                    if (used[j])
                        continue;

                    var (a, b) = segments[j];
                    var connects = clusterIdx.Exists(k =>
                    {
                        var (ka, kb) = segments[k];
                        return a.DistanceTo(ka) <= ConnectTolFeet || a.DistanceTo(kb) <= ConnectTolFeet ||
                               b.DistanceTo(ka) <= ConnectTolFeet || b.DistanceTo(kb) <= ConnectTolFeet;
                    });

                    if (!connects)
                        continue;

                    clusterIdx.Add(j);
                    used[j] = true;
                    changed = true;
                }
            }

            var clusterSegments = clusterIdx.Select(k => segments[k]).ToList();
            var points = clusterSegments.SelectMany(s => new[] { s.A, s.B }).ToList();
            if (points.Count < 3)
                continue; // 1 Line don le (2 diem) - khong du xac dinh ca 2 canh hinh chu nhat, bo qua.

            var rect = MinAreaRectangle(points);
            if (rect is null)
                continue;

            var (center, lengthFeet, widthFeet, angleRadians) = rect.Value;
            if (lengthFeet < MinSizeFeet || widthFeet < MinSizeFeet)
                continue;

            AddColumn(columnsByLayer, layerName, center, lengthFeet, widthFeet, angleRadians, clusterSegments);
        }
    }

    private static void AddColumn(
        Dictionary<string, List<RawColumn>> columnsByLayer, string layerName,
        XYZ center, double lengthFeet, double widthFeet, double angleRadians, List<(XYZ A, XYZ B)> segments)
    {
        if (!columnsByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawColumn>();
            columnsByLayer[layerName] = list;
        }

        list.Add(new RawColumn
        {
            Center = center,
            LengthFeet = lengthFeet,
            WidthFeet = widthFeet,
            AngleRadians = angleRadians,
            Segments = segments,
        });
    }

    /// <summary>Hình chữ nhật xoay nhỏ nhất bao quanh đa giác (rotating calipers trên convex hull) — null nếu đa giác suy biến.
    /// Trả về tâm, kích thước theo trục chính, kích thước theo trục vuông góc, góc trục chính trong [0, π/2).</summary>
    internal static (XYZ Center, double LengthFeet, double WidthFeet, double AngleRadians)? MinAreaRectangle(List<XYZ> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3)
            return null;

        var bestArea = double.MaxValue;
        XYZ bestD = XYZ.BasisX;
        XYZ bestP = XYZ.BasisY;
        double bestMinD = 0, bestMaxD = 0, bestMinP = 0, bestMaxP = 0;

        for (var i = 0; i < hull.Count; i++)
        {
            var p0 = hull[i];
            var p1 = hull[(i + 1) % hull.Count];
            var edge = p1 - p0;
            var len = edge.GetLength();
            if (len < 1e-9)
                continue;

            var d = edge / len;
            var p = new XYZ(-d.Y, d.X, 0);

            double minD = double.MaxValue, maxD = double.MinValue, minP = double.MaxValue, maxP = double.MinValue;
            foreach (var pt in hull)
            {
                var projD = pt.X * d.X + pt.Y * d.Y;
                var projP = pt.X * p.X + pt.Y * p.Y;
                if (projD < minD) minD = projD;
                if (projD > maxD) maxD = projD;
                if (projP < minP) minP = projP;
                if (projP > maxP) maxP = projP;
            }

            var area = (maxD - minD) * (maxP - minP);
            if (area < bestArea)
            {
                bestArea = area;
                bestD = d;
                bestP = p;
                bestMinD = minD;
                bestMaxD = maxD;
                bestMinP = minP;
                bestMaxP = maxP;
            }
        }

        if (bestArea >= double.MaxValue)
            return null;

        var centerXY = bestD * ((bestMinD + bestMaxD) / 2) + bestP * ((bestMinP + bestMaxP) / 2);
        var center = new XYZ(centerXY.X, centerXY.Y, 0);
        var lengthAlongD = bestMaxD - bestMinD;
        var widthAlongP = bestMaxP - bestMinP;

        var angle = Math.Atan2(bestD.Y, bestD.X);
        angle = ((angle % Math.PI) + Math.PI) % Math.PI;

        double canonicalAngle, canonicalLength, canonicalWidth;
        if (angle < Math.PI / 2)
        {
            canonicalAngle = angle;
            canonicalLength = lengthAlongD;
            canonicalWidth = widthAlongP;
        }
        else
        {
            canonicalAngle = angle - Math.PI / 2;
            canonicalLength = widthAlongP;
            canonicalWidth = lengthAlongD;
        }

        return (center, canonicalLength, canonicalWidth, canonicalAngle);
    }

    /// <summary>Convex hull qua thuật toán monotone chain — trả về các đỉnh theo chiều ngược kim đồng hồ, không lặp điểm đầu/cuối.
    /// Gộp điểm trùng toạ độ qua GroupBy (đủ để loại điểm đóng lặp lại của PolyLine khép kín, không cần biết trước polyline hở hay kín).</summary>
    private static List<XYZ> ConvexHull(List<XYZ> points)
    {
        var pts = points
            .GroupBy(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6)))
            .Select(g => g.First())
            .OrderBy(p => p.X).ThenBy(p => p.Y)
            .ToList();

        if (pts.Count < 3)
            return pts;

        var lower = new List<XYZ>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<XYZ>();
        for (var i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(XYZ o, XYZ a, XYZ b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

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
