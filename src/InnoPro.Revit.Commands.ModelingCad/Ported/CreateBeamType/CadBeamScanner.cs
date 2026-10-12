using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.CreateBeamType;

/// <summary>Đọc mặt cắt dầm (luôn vẽ thẳng trục, không xoay) từ 1 ImportInstance, gộp theo layer — nhận cả PolyLine
/// (khép kín hoặc hở) lẫn dầm được người vẽ CAD tách thành nhiều Line rời rạc từng cạnh một. B/H đọc trực tiếp
/// theo cạnh đứng/ngang, bỏ qua đoạn xiên/gãy khúc (ký hiệu ngắt của cánh sàn giao với dầm).</summary>
internal static class CadBeamScanner
{
    private static readonly double MinSizeFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    /// <summary>Ngưỡng phân loại cạnh "đứng"/"ngang": lệch trục vuông góc tối đa 5mm mới coi là thẳng trục.</summary>
    private static readonly double AxisTolFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    /// <summary>Ngưỡng coi 2 đầu Line là "nối nhau" khi gộp cụm Line rời rạc thành 1 đường bao.</summary>
    private static readonly double ConnectTolFeet = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);

    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    public static CadBeamScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var columnsByLayer = new Dictionary<string, List<RawBeamSection>>(StringComparer.Ordinal);
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

        return new CadBeamScanResult { ColumnsByLayer = columnsByLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawBeamSection>> columnsByLayer,
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

    /// <summary>Nhận mọi PolyLine (khép kín hoặc hở, tối thiểu 3 điểm) — đọc b/h trực tiếp theo cạnh đứng/ngang.</summary>
    private static void AddPolyLine(Document doc, PolyLine polyLine, Dictionary<string, List<RawBeamSection>> columnsByLayer)
    {
        var raw = polyLine.GetCoordinates();
        if (raw.Count < 3)
            return;

        var flat = raw.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
        var segments = new List<(XYZ, XYZ)>();
        for (var i = 0; i < flat.Count - 1; i++)
            segments.Add((flat[i], flat[i + 1]));

        var layerName = ResolveLayerName(doc, polyLine.GraphicsStyleId);
        AddSection(layerName, flat, segments, columnsByLayer);
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
    /// coi là nối) — mỗi cụm đủ điểm thì đọc b/h y hệt đường PolyLine. Độ phức tạp O(n²) chấp nhận được vì mỗi
    /// layer dầm chỉ vài trăm đến vài nghìn Line.</summary>
    private static void AddLineClusters(string layerName, List<(XYZ A, XYZ B)> segments, Dictionary<string, List<RawBeamSection>> columnsByLayer)
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
                continue; // 1 Line don le (2 diem) - khong du xac dinh mat cat, bo qua.

            AddSection(layerName, points, clusterSegments, columnsByLayer);
        }
    }

    /// <summary>Đọc b/h theo cạnh đứng/ngang rồi thêm 1 mặt cắt vào danh sách theo layer — bỏ qua âm thầm nếu không có cạnh thẳng nào (nhiễu CAD).</summary>
    private static void AddSection(string layerName, List<XYZ> points, List<(XYZ A, XYZ B)> segments, Dictionary<string, List<RawBeamSection>> columnsByLayer)
    {
        var outcome = ComputeBeamSize(segments);
        if (outcome is null)
            return;

        var (bFeet, hFeet, error) = outcome.Value;

        if (!columnsByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawBeamSection>();
            columnsByLayer[layerName] = list;
        }

        list.Add(new RawBeamSection
        {
            Center = ComputeCenter(points),
            BFeet = error is null ? bFeet : null,
            HFeet = error is null ? hFeet : null,
            ErrorMessage = error,
            Segments = segments,
        });
    }

    private static XYZ ComputeCenter(List<XYZ> points)
    {
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0);
    }

    /// <summary>Đọc b (khoảng cách X giữa 2 cạnh đứng dài nhất) và h (Y lớn nhất trừ nhỏ nhất trên cạnh đứng/ngang), bỏ qua đoạn
    /// xiên/gãy khúc (cánh sàn) — null nếu không có cạnh thẳng trục nào (nhiễu CAD), lỗi nếu thiếu cạnh đứng, hoặc kích thước quá nhỏ.</summary>
    private static (double BFeet, double HFeet, string? Error)? ComputeBeamSize(List<(XYZ A, XYZ B)> segments)
    {
        var verticals = new List<(double Length, double X)>();
        var keptPoints = new List<XYZ>();

        foreach (var (a, b) in segments)
        {
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            var isVertical = dx <= AxisTolFeet && dy > dx;
            var isHorizontal = dy <= AxisTolFeet && dx > dy;

            if (!isVertical && !isHorizontal)
                continue; // doan xien/gay khuc - ky hieu ngat cua canh san, bo qua hoan toan.

            keptPoints.Add(a);
            keptPoints.Add(b);
            if (isVertical)
                verticals.Add((dy, (a.X + b.X) / 2));
        }

        if (keptPoints.Count == 0)
            return null;

        if (verticals.Count < 2)
            return (0, 0, "Không xác định được mặt cắt — thiếu cạnh đứng");

        var hFeet = keptPoints.Max(p => p.Y) - keptPoints.Min(p => p.Y);
        var top2 = verticals.OrderByDescending(v => v.Length).Take(2).ToList();
        var bFeet = Math.Abs(top2[0].X - top2[1].X);

        if (bFeet < MinSizeFeet || hFeet < MinSizeFeet)
            return null;

        return (bFeet, hFeet, null);
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
