using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceWall;

/// <summary>Đọc line tường từ 1 ImportInstance, gộp theo layer, tự ghép cặp 2 Line song song gần nhau theo phương X/Y thành 1 tường, lấy tim làm đường đặt tường.</summary>
internal static class CadWallScanner
{
    private const string UnknownLayerName = "(Không xác định layer)";
    private const int MaxRecursionDepth = 8;

    /// <summary>Góc lệch tối đa (độ) so với trục X/Y để còn coi 1 Line là "theo phương X" hoặc "theo phương Y" — v1 MVP không xử lý tường xiên.</summary>
    private const double AxisAngleTolDeg = 5.0;

    /// <summary>Khoảng cách vuông góc tối đa giữa 2 Line để còn coi là 1 cặp tường — tránh ghép nhầm 2 tường ở 2 nhịp khác nhau.</summary>
    internal static readonly double MaxPairWidthFeet = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);

    /// <summary>Khoảng cách vuông góc tối thiểu — 2 Line trùng nhau gần như hoàn toàn (bề rộng ~0) không phải
    /// 1 tường thật, thường là do CAD vẽ đè line.</summary>
    internal static readonly double MinPairWidthFeet = UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Millimeters);

    public static CadWallScanResult Scan(
        Document doc, ImportInstance instance, View view, double minOverlapFraction, double minPairLengthRatio)
    {
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
            Walk(doc, geometryElement, linesByLayer, 0);

        var wallsByLayer = new Dictionary<string, List<RawWall>>(StringComparer.Ordinal);
        var allOrphans = new List<(XYZ A, XYZ B, string LayerName)>();

        foreach (var (layerName, segments) in linesByLayer)
        {
            var pairResult = PairLines(segments, minOverlapFraction, minPairLengthRatio);
            wallsByLayer[layerName] = pairResult.Walls;
            foreach (var (a, b) in pairResult.Orphans)
                allOrphans.Add((a, b, layerName));
        }

        return new CadWallScanResult
        {
            WallsByLayer = wallsByLayer,
            RawSegmentsByLayer = linesByLayer,
            Orphans = allOrphans,
        };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
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
                            Walk(doc, nested, linesByLayer, depth + 1);
                        break;

                    case Line line:
                        AddLine(doc, line, linesByLayer);
                        break;

                    case PolyLine polyLine:
                        AddPolyLineSegments(doc, polyLine, linesByLayer);
                        break;
                }
            }
            catch (Exception)
            {
                // Bo qua rieng 1 doi tuong loi, khong sap ca luot quet.
            }
        }
    }

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

        AddSegment(linesByLayer, ResolveLayerName(doc, line.GraphicsStyleId), a, b);
    }

    /// <summary>1 số ít trường hợp CAD vẽ line tường thành PolyLine 2 điểm thay vì Line — nhận luôn từng đoạn.</summary>
    private static void AddPolyLineSegments(Document doc, PolyLine polyLine, Dictionary<string, List<(XYZ A, XYZ B)>> linesByLayer)
    {
        var raw = polyLine.GetCoordinates();
        if (raw.Count < 2)
            return;

        var layerName = ResolveLayerName(doc, polyLine.GraphicsStyleId);
        for (var i = 0; i < raw.Count - 1; i++)
        {
            var a = new XYZ(raw[i].X, raw[i].Y, 0);
            var b = new XYZ(raw[i + 1].X, raw[i + 1].Y, 0);
            if (a.DistanceTo(b) < 1e-9)
                continue;

            AddSegment(linesByLayer, layerName, a, b);
        }
    }

    private static void AddSegment(Dictionary<string, List<(XYZ A, XYZ B)>> linesByLayer, string layerName, XYZ a, XYZ b)
    {
        if (!linesByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<(XYZ, XYZ)>();
            linesByLayer[layerName] = list;
        }

        list.Add((a, b));
    }

    /// <summary>Kết quả ghép cặp 1 layer — tường ghép được VÀ đoạn mồ côi còn lại (không ghép được cặp cùng layer, ứng viên cho Pass 2 chéo layer).</summary>
    internal readonly record struct PairResult(List<RawWall> Walls, List<(XYZ A, XYZ B)> Orphans);

    /// <summary>Ghép cặp 2 Line song song gần nhau theo phương X/Y thành 1 tường — thuần hình học, Text chỉ dùng SAU KHI ghép xong (xem WallTagReader.AttachTags).</summary>
    internal static PairResult PairLines(
        List<(XYZ A, XYZ B)> segments, double minOverlapFraction, double minPairLengthRatio)
    {
        var xLines = new List<(XYZ A, XYZ B)>();
        var yLines = new List<(XYZ A, XYZ B)>();

        foreach (var seg in segments)
        {
            if (!TryClassifyAxis(seg.A, seg.B, out var isX))
                continue; // dam xien - v1 MVP chua xu ly, bo qua.

            (isX ? xLines : yLines).Add(seg);
        }

        var xMerged = MergeCollinearFragments(xLines, isXDirection: true);
        var yMerged = MergeCollinearFragments(yLines, isXDirection: false);

        var xResult = PairInDirection(xMerged, isXDirection: true, minOverlapFraction, minPairLengthRatio);
        var yResult = PairInDirection(yMerged, isXDirection: false, minOverlapFraction, minPairLengthRatio);

        var (xWalls, xOrphans) = ResolveJunctionOrphans(xResult.Walls, xResult.Orphans, isXDirection: true, minOverlapFraction, minPairLengthRatio);
        var (yWalls, yOrphans) = ResolveJunctionOrphans(yResult.Walls, yResult.Orphans, isXDirection: false, minOverlapFraction, minPairLengthRatio);

        var walls = new List<RawWall>();
        walls.AddRange(xWalls);
        walls.AddRange(yWalls);

        var orphans = new List<(XYZ A, XYZ B)>();
        orphans.AddRange(xOrphans);
        orphans.AddRange(yOrphans);

        return new PairResult(walls, orphans);
    }

    /// <summary>Gộp các đoạn CÙNG 1 ĐƯỜNG CAD gốc bị cắt vụn tại mỗi chỗ tường khác/cột băng qua (cùng toạ độ
    /// vuông góc, liền kề dọc trục — xem IsCollinearAdjacent) thành 1 đoạn liền duy nhất theo hình học thuần túy.
    /// Grid không tham gia và không cắt đoạn đã gộp.</summary>
    private static List<(XYZ A, XYZ B)> MergeCollinearFragments(
        List<(XYZ A, XYZ B)> segments, bool isXDirection)
    {
        var used = new bool[segments.Count];
        var result = new List<(XYZ A, XYZ B)>();

        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
                continue;

            used[i] = true;
            var perp = isXDirection ? (segments[i].A.Y + segments[i].B.Y) / 2.0 : (segments[i].A.X + segments[i].B.X) / 2.0;
            var alongMin = isXDirection ? Math.Min(segments[i].A.X, segments[i].B.X) : Math.Min(segments[i].A.Y, segments[i].B.Y);
            var alongMax = isXDirection ? Math.Max(segments[i].A.X, segments[i].B.X) : Math.Max(segments[i].A.Y, segments[i].B.Y);

            var mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (var j = 0; j < segments.Count; j++)
                {
                    if (used[j])
                        continue;

                    var current = isXDirection
                        ? (new XYZ(alongMin, perp, 0), new XYZ(alongMax, perp, 0))
                        : (new XYZ(perp, alongMin, 0), new XYZ(perp, alongMax, 0));

                    if (!IsCollinearMergeable(current, segments[j], isXDirection))
                        continue;

                    var jMin = isXDirection ? Math.Min(segments[j].A.X, segments[j].B.X) : Math.Min(segments[j].A.Y, segments[j].B.Y);
                    var jMax = isXDirection ? Math.Max(segments[j].A.X, segments[j].B.X) : Math.Max(segments[j].A.Y, segments[j].B.Y);

                    alongMin = Math.Min(alongMin, jMin);
                    alongMax = Math.Max(alongMax, jMax);
                    used[j] = true;
                    mergedAny = true;
                }
            }

            result.Add(BuildAlongSegment(alongMin, alongMax, perp, isXDirection));
        }

        return result;
    }

    /// <summary>Điều kiện union Line CAD trước khi bắt cặp: cùng trục và chồng lấn không giới hạn,
    /// hoặc bị ngắt với khoảng hở không quá giới hạn giao. Không dùng cận overlap -50 mm của xử lý giao T.</summary>
    private static bool IsCollinearMergeable(
        (XYZ A, XYZ B) seg1, (XYZ A, XYZ B) seg2, bool isXDirection)
    {
        var perp1 = isXDirection ? (seg1.A.Y + seg1.B.Y) / 2.0 : (seg1.A.X + seg1.B.X) / 2.0;
        var perp2 = isXDirection ? (seg2.A.Y + seg2.B.Y) / 2.0 : (seg2.A.X + seg2.B.X) / 2.0;
        if (Math.Abs(perp1 - perp2) > JunctionCollinearPerpToleranceFeet)
            return false;

        double min1, max1, min2, max2;
        if (isXDirection)
        {
            min1 = Math.Min(seg1.A.X, seg1.B.X); max1 = Math.Max(seg1.A.X, seg1.B.X);
            min2 = Math.Min(seg2.A.X, seg2.B.X); max2 = Math.Max(seg2.A.X, seg2.B.X);
        }
        else
        {
            min1 = Math.Min(seg1.A.Y, seg1.B.Y); max1 = Math.Max(seg1.A.Y, seg1.B.Y);
            min2 = Math.Min(seg2.A.Y, seg2.B.Y); max2 = Math.Max(seg2.A.Y, seg2.B.Y);
        }

        var gap = Math.Max(min1, min2) - Math.Min(max1, max2);
        return gap <= JunctionAdjacentGapMaxFeet;
    }

    private static (XYZ A, XYZ B) BuildAlongSegment(double from, double to, double perp, bool isXDirection) =>
        isXDirection
            ? (new XYZ(from, perp, 0), new XYZ(to, perp, 0))
            : (new XYZ(perp, from, 0), new XYZ(perp, to, 0));

    /// <summary>Sai số toạ độ vuông góc (mm) cho phép để 1 Line mồ côi coi là "cùng 1 đường bị cắt đôi" với 1 cạnh tường đã ghép — xem ResolveJunctionOrphans.</summary>
    private static readonly double JunctionCollinearPerpToleranceFeet =
        UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);

    /// <summary>Khoảng hở dọc trục cho phép giữa 2 đoạn để còn coi là "liền kề, bị cắt bởi tường băng qua" — âm nhẹ (chồng lấn chút do CAD vẽ dư) tới ~bề rộng tường băng qua.</summary>
    private static readonly double JunctionAdjacentGapMinFeet = UnitUtils.ConvertToInternalUnits(-50.0, UnitTypeId.Millimeters);

    private static readonly double JunctionAdjacentGapMaxFeet = UnitUtils.ConvertToInternalUnits(1600.0, UnitTypeId.Millimeters);

    /// <summary>Sai số (mm) coi 2 tường "cùng kích thước" khi xử lý giao điểm chữ T — dùng chung cho so b lẫn so h.</summary>
    private static readonly double JunctionSameWidthToleranceFeet =
        UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);

    /// <summary>Gộp Line mồ côi vào tường đã ghép nếu cùng đường + liền kề + cùng b (thuần hình học), ngược lại chia thành tường riêng.</summary>
    private static (List<RawWall> Walls, List<(XYZ A, XYZ B)> Orphans) ResolveJunctionOrphans(
        List<RawWall> walls, List<(XYZ A, XYZ B)> orphans, bool isXDirection,
        double minOverlapFraction, double minPairLengthRatio)
    {
        var resolvedWalls = new List<RawWall>(walls);
        var remainingOrphans = new List<(XYZ A, XYZ B)>();

        foreach (var orphan in orphans)
        {
            var matchIndex = -1;
            var longSeg = default((XYZ A, XYZ B));
            var matchedShortSeg = default((XYZ A, XYZ B));

            for (var i = 0; i < resolvedWalls.Count; i++)
            {
                if (TryFindJunctionSibling(orphan, resolvedWalls[i], isXDirection, out longSeg, out matchedShortSeg))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                remainingOrphans.Add(orphan);
                continue;
            }

            var wallToFix = resolvedWalls[matchIndex];

            if (!HasCompatiblePairLengths(longSeg, orphan, isXDirection, minPairLengthRatio) ||
                !TryGetOverlapPair(longSeg, orphan, isXDirection, minOverlapFraction, out var newWidth, out var newStart, out var newEnd) ||
                newWidth < MinPairWidthFeet || newWidth > MaxPairWidthFeet)
            {
                remainingOrphans.Add(orphan);
                continue;
            }

            var sameB = Math.Abs(newWidth - wallToFix.WidthFeet) <= JunctionSameWidthToleranceFeet;

            if (sameB)
            {
                var (mergedStart, mergedEnd) = UnionAlongAxis(wallToFix.Start, wallToFix.End, newStart, newEnd, isXDirection);
                var mergedSegments = wallToFix.Segments.Count == 2
                    ? new List<(XYZ, XYZ)> { longSeg, OtherSegment(wallToFix.Segments, longSeg), orphan }
                    : new List<(XYZ, XYZ)>(wallToFix.Segments) { orphan };

                resolvedWalls[matchIndex] = new RawWall
                {
                    Start = mergedStart,
                    End = mergedEnd,
                    WidthFeet = wallToFix.WidthFeet,
                    Segments = mergedSegments,
                };
            }
            else
            {
                resolvedWalls.Add(new RawWall
                {
                    Start = newStart,
                    End = newEnd,
                    WidthFeet = newWidth,
                    Segments = new List<(XYZ, XYZ)> { longSeg, orphan },
                });
            }
        }

        return (resolvedWalls, remainingOrphans);
    }

    /// <summary>Tìm cạnh ngắn nào của wall liền kề orphan — trả ra cạnh dài (longSeg) và đúng cạnh ngắn vừa khớp (matchedShortSeg).</summary>
    private static bool TryFindJunctionSibling(
        (XYZ A, XYZ B) orphan, RawWall wall, bool isXDirection,
        out (XYZ A, XYZ B) longSeg, out (XYZ A, XYZ B) matchedShortSeg)
    {
        longSeg = default;
        matchedShortSeg = default;

        if (wall.Segments.Count < 2)
            return false;

        if (wall.Segments.Count == 2)
        {
            if (IsCollinearAdjacent(orphan, wall.Segments[0], isXDirection))
            {
                longSeg = wall.Segments[1];
                matchedShortSeg = wall.Segments[0];
                return true;
            }

            if (IsCollinearAdjacent(orphan, wall.Segments[1], isXDirection))
            {
                longSeg = wall.Segments[0];
                matchedShortSeg = wall.Segments[1];
                return true;
            }

            return false;
        }

        for (var s = 1; s < wall.Segments.Count; s++)
        {
            if (!IsCollinearAdjacent(orphan, wall.Segments[s], isXDirection))
                continue;

            longSeg = wall.Segments[0];
            matchedShortSeg = wall.Segments[s];
            return true;
        }

        return false;
    }

    /// <summary>Trong 1 danh sách 2 đoạn, trả về đoạn KHÔNG PHẢI target (so sánh theo toạ độ 2 đầu mút).</summary>
    private static (XYZ A, XYZ B) OtherSegment(List<(XYZ A, XYZ B)> segments, (XYZ A, XYZ B) target) =>
        segments[0].A.IsAlmostEqualTo(target.A) && segments[0].B.IsAlmostEqualTo(target.B) ? segments[1] : segments[0];

    /// <summary>2 đoạn có cùng toạ độ vuông góc (trong JunctionCollinearPerpToleranceFeet) và liền kề dọc trục
    /// (khoảng hở trong [JunctionAdjacentGapMinFeet, JunctionAdjacentGapMaxFeet]) hay không — dấu hiệu 2 đoạn
    /// vốn là 1 đường bị cắt đôi bởi 1 tường khác băng qua.</summary>
    private static bool IsCollinearAdjacent((XYZ A, XYZ B) seg1, (XYZ A, XYZ B) seg2, bool isXDirection)
    {
        var perp1 = isXDirection ? (seg1.A.Y + seg1.B.Y) / 2.0 : (seg1.A.X + seg1.B.X) / 2.0;
        var perp2 = isXDirection ? (seg2.A.Y + seg2.B.Y) / 2.0 : (seg2.A.X + seg2.B.X) / 2.0;
        if (Math.Abs(perp1 - perp2) > JunctionCollinearPerpToleranceFeet)
            return false;

        double min1, max1, min2, max2;
        if (isXDirection)
        {
            min1 = Math.Min(seg1.A.X, seg1.B.X); max1 = Math.Max(seg1.A.X, seg1.B.X);
            min2 = Math.Min(seg2.A.X, seg2.B.X); max2 = Math.Max(seg2.A.X, seg2.B.X);
        }
        else
        {
            min1 = Math.Min(seg1.A.Y, seg1.B.Y); max1 = Math.Max(seg1.A.Y, seg1.B.Y);
            min2 = Math.Min(seg2.A.Y, seg2.B.Y); max2 = Math.Max(seg2.A.Y, seg2.B.Y);
        }

        var gap = Math.Max(min1, min2) - Math.Min(max1, max2);
        return gap >= JunctionAdjacentGapMinFeet && gap <= JunctionAdjacentGapMaxFeet;
    }

    /// <summary>Hợp 2 đoạn tim tường cùng phương thành 1 đoạn bao trọn cả 2 (dọc trục), giữ toạ độ vuông góc của đoạn thứ nhất.</summary>
    private static (XYZ Start, XYZ End) UnionAlongAxis(XYZ s1, XYZ e1, XYZ s2, XYZ e2, bool isXDirection)
    {
        if (isXDirection)
        {
            var minX = Math.Min(Math.Min(s1.X, e1.X), Math.Min(s2.X, e2.X));
            var maxX = Math.Max(Math.Max(s1.X, e1.X), Math.Max(s2.X, e2.X));
            return (new XYZ(minX, s1.Y, 0), new XYZ(maxX, s1.Y, 0));
        }

        var minY = Math.Min(Math.Min(s1.Y, e1.Y), Math.Min(s2.Y, e2.Y));
        var maxY = Math.Max(Math.Max(s1.Y, e1.Y), Math.Max(s2.Y, e2.Y));
        return (new XYZ(s1.X, minY, 0), new XYZ(s1.X, maxY, 0));
    }

    /// <summary>Phân loại 1 Line theo phương X hay Y dựa trên góc lệch so với trục — false nếu lệch quá (tường xiên, không xử lý).</summary>
    internal static bool TryClassifyAxis(XYZ a, XYZ b, out bool isXDirection)
    {
        var dx = Math.Abs(b.X - a.X);
        var dy = Math.Abs(b.Y - a.Y);
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        if (angle <= AxisAngleTolDeg)
        {
            isXDirection = true;
            return true;
        }

        if (angle >= 90.0 - AxisAngleTolDeg)
        {
            isXDirection = false;
            return true;
        }

        isXDirection = false;
        return false;
    }

    /// <summary>Ghép cặp tham lam trong 1 phương — thuần hình học, ưu tiên khoảng cách vuông góc nhỏ nhất (Text không tham gia bước này).</summary>
    private static PairResult PairInDirection(
        List<(XYZ A, XYZ B)> segments, bool isXDirection, double minOverlapFraction, double minPairLengthRatio)
    {
        var result = new List<RawWall>();
        var used = new bool[segments.Count];

        var candidates = new List<(int I, int J, double PerpDist)>();
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                if (!HasCompatiblePairLengths(segments[i], segments[j], isXDirection, minPairLengthRatio))
                    continue;

                if (!TryGetOverlapPair(segments[i], segments[j], isXDirection, minOverlapFraction, out var perpDist, out _, out _))
                    continue;

                if (perpDist < MinPairWidthFeet || perpDist > MaxPairWidthFeet)
                    continue;

                candidates.Add((i, j, perpDist));
            }
        }

        foreach (var (i, j, _) in candidates.OrderBy(c => c.PerpDist))
        {
            if (used[i] || used[j])
                continue;

            if (!TryGetOverlapPair(segments[i], segments[j], isXDirection, minOverlapFraction, out var width, out var start, out var end))
                continue;

            used[i] = true;
            used[j] = true;

            result.Add(new RawWall
            {
                Start = start,
                End = end,
                WidthFeet = width,
                Segments = new List<(XYZ, XYZ)> { segments[i], segments[j] },
            });
        }

        var orphans = new List<(XYZ A, XYZ B)>();
        for (var k = 0; k < segments.Count; k++)
            if (!used[k])
                orphans.Add(segments[k]);

        return new PairResult(result, orphans);
    }

    internal static bool HasCompatiblePairLengths(
        (XYZ A, XYZ B) segA, (XYZ A, XYZ B) segB, bool isXDirection, double minPairLengthRatio)
    {
        var lengthA = isXDirection ? Math.Abs(segA.B.X - segA.A.X) : Math.Abs(segA.B.Y - segA.A.Y);
        var lengthB = isXDirection ? Math.Abs(segB.B.X - segB.A.X) : Math.Abs(segB.B.Y - segB.A.Y);
        var longer = Math.Max(lengthA, lengthB);
        return longer > 1e-9 && Math.Min(lengthA, lengthB) / longer >= minPairLengthRatio;
    }

    /// <summary>Kiểm tra 2 Line có đủ chồng lấn theo phương dọc trục tường để coi là 1 cặp không — nếu đủ, trả về
    /// bề rộng (khoảng cách vuông góc giữa toạ độ trung bình 2 Line) và điểm đầu/cuối tim tường (đoạn chồng lấn,
    /// ở giữa 2 Line). Dùng lại được cho cả Pass 1 (cùng layer) lẫn WallOrphanRepairer (đo khoảng cách ứng viên cho Line mồ côi).</summary>
    internal static bool TryGetOverlapPair(
        (XYZ A, XYZ B) segA, (XYZ A, XYZ B) segB, bool isXDirection, double minOverlapFraction,
        out double widthFeet, out XYZ start, out XYZ end)
    {
        widthFeet = 0;
        start = XYZ.Zero;
        end = XYZ.Zero;

        double a0Along, a1Along, a0Perp, a1Perp, b0Along, b1Along, b0Perp, b1Perp;
        if (isXDirection)
        {
            a0Along = segA.A.X; a1Along = segA.B.X; a0Perp = segA.A.Y; a1Perp = segA.B.Y;
            b0Along = segB.A.X; b1Along = segB.B.X; b0Perp = segB.A.Y; b1Perp = segB.B.Y;
        }
        else
        {
            a0Along = segA.A.Y; a1Along = segA.B.Y; a0Perp = segA.A.X; a1Perp = segA.B.X;
            b0Along = segB.A.Y; b1Along = segB.B.Y; b0Perp = segB.A.X; b1Perp = segB.B.X;
        }

        var aMin = Math.Min(a0Along, a1Along);
        var aMax = Math.Max(a0Along, a1Along);
        var bMin = Math.Min(b0Along, b1Along);
        var bMax = Math.Max(b0Along, b1Along);

        var overlapMin = Math.Max(aMin, bMin);
        var overlapMax = Math.Min(aMax, bMax);
        var overlapLen = overlapMax - overlapMin;
        if (overlapLen <= 0)
            return false;

        var shorterLen = Math.Min(aMax - aMin, bMax - bMin);
        if (shorterLen < 1e-9 || overlapLen < shorterLen * minOverlapFraction)
            return false;

        var perpA = (a0Perp + a1Perp) / 2.0;
        var perpB = (b0Perp + b1Perp) / 2.0;
        widthFeet = Math.Abs(perpA - perpB);

        var midPerp = (perpA + perpB) / 2.0;

        start = isXDirection ? new XYZ(overlapMin, midPerp, 0) : new XYZ(midPerp, overlapMin, 0);
        end = isXDirection ? new XYZ(overlapMax, midPerp, 0) : new XYZ(midPerp, overlapMax, 0);

        return true;
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


