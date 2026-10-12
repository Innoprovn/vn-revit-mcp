using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>2 việc TÁCH RIÊNG: (1) CollectCenterlineSegments — quét dầm kết cấu (Structural Framing, StructuralType.Beam) thuộc 1 Level, dựng TIM dầm (centerline), tự snap T-junction/cắt nhau rồi gộp đoạn thẳng hàng liền kề (CenterlineMerger) thành danh sách Line sạch để VẼ RA làm DetailLine cho người dùng xem/sửa tay; (2) TraceClosedLoops — thuật toán chung, nhận BẤT KỲ danh sách Curve nào (dầm hay DetailLine đã sửa tay đều được) và tìm mọi Ô KHÉP KÍN bằng "duyệt mặt" đồ thị phẳng (planar face traversal qua half-edge, xem PlanarFaceTracer), mỗi ô trả về đã trim/extend sạch qua EdgeTrimmer. Cơ chế snap theo Cột (Structural Columns) đã BỎ HẲN theo yêu cầu người dùng 2026-09-18 — bị lỗi (chưa xác định nguyên nhân), khoảng hở tại cột giờ chỉ trông chờ T-junction/cắt nhau xử lý được tới đâu hay tới đó.</summary>
internal static class BeamLoopScanner
{
    /// <summary>Ngưỡng gộp 2 đầu mút thành cùng 1 nút liên kết — chỉ bù sai số làm tròn, KHÔNG được rộng vì sẽ nuốt nhầm điểm lạ (không phải giao điểm thật) vào 1 node đã có sẵn, làm sai thứ tự cạnh quanh node đó (xem SplitAtCrossingsAndJunctions/SnapConnections đã xử lý giao điểm thật từ trước).</summary>
    private static readonly double NodeTolFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

    /// <summary>Biên an toàn cộng thêm vào nửa bề rộng dầm host khi chấp nhận 1 T-junction — bù sai số mô hình (dầm phụ không neo khít tuyệt đối vào mặt dầm chính).</summary>
    private static readonly double TJunctionMarginFeet = UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Millimeters);

    /// <summary>Nửa bề rộng mặc định khi không đọc được BoundingBox của dầm host (hiếm) — ước lượng an toàn cho dầm cỡ trung bình.</summary>
    private static readonly double DefaultHalfWidthFeet = UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Millimeters);

    /// <summary>Sàn tối thiểu 1 đoạn dầm/1 cạnh sau khi tách T-junction — loại bỏ mẩu thừa gần như trùng điểm do sai số.</summary>
    private static readonly double MinSegmentLenFeet = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);

    /// <summary>Dung sai T-junction dùng cho curve tự do (không có "bề rộng" như dầm).</summary>
    private static readonly double GenericJunctionTolFeet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);

    /// <summary>Khoảng cách TUYỆT ĐỐI tới 1 trong 2 đầu mút host — điểm rơi gần hơn khoảng này không tính là T-junction/cắt nhau vì coi như đầu-đối-đầu bình thường (ClusterNode xử lý); PHẢI tuyệt đối chứ không theo tỉ lệ % chiều dài, vì host dài (vài mét) sẽ khiến % lớn hơn cả khoảng cách thật tới 1 nút thật gần đó, nuốt mất T-junction hợp lệ.</summary>
    private static readonly double EndpointExclusionFeet = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);

    /// <summary>True nếu điểm ở tham số t (0..1) dọc host dài hostLengthFeet rơi trong khoảng marginFeet tính từ 1 trong 2 đầu — dùng khoảng cách thật thay vì % để không phụ thuộc chiều dài host.</summary>
    private static bool IsNearEitherEnd(double t, double hostLengthFeet, double marginFeet)
    {
        if (hostLengthFeet < 1e-9)
            return true;

        var distFromStart = t * hostLengthFeet;
        var distFromEnd = (1 - t) * hostLengthFeet;
        return distFromStart <= marginFeet || distFromEnd <= marginFeet;
    }

    /// <summary>1 cạnh trong đồ thị phẳng — Curve bất kỳ (dầm hay DetailLine đều dùng chung kiểu này) nối 2 nút đã cluster.</summary>
    private sealed record GraphEdge(Curve Curve, int NodeA, int NodeB);

    private sealed record RawCenterline(Curve Curve, double HalfWidthFeet);

    /// <summary>Level thật của 1 dầm — ưu tiên tham số "Reference Level" (INSTANCE_REFERENCE_LEVEL_PARAM), KHÔNG dùng thẳng Element.LevelId vì dầm (curve-driven, không host theo mặt phẳng) thường không đồng bộ đúng field này, khiến lọc theo LevelId trả về sai/rỗng dù dầm rõ ràng thuộc đúng tầng trong view. Fallback Element.LevelId nếu param không đọc được.</summary>
    public static ElementId GetBeamLevelId(FamilyInstance beam)
    {
        var refLevelParam = beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
        if (refLevelParam is { StorageType: StorageType.ElementId } && refLevelParam.AsElementId() != ElementId.InvalidElementId)
            return refLevelParam.AsElementId();

        return beam.LevelId;
    }

    /// <summary>Quét dầm thuộc 1 Level, tự snap (T-junction, cắt nhau) rồi gộp đoạn thẳng hàng liền kề — trả về danh sách Line sạch, sẵn sàng vẽ thành DetailLine cho người dùng xem/sửa tay trước khi tạo sàn (xem CenterlineMerger).</summary>
    public static List<Line> CollectCenterlineSegments(Document doc, ViewPlan view, Level level)
    {
        var rawCenterlines = new List<RawCenterline>();

        var beams = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .Cast<FamilyInstance>()
            .Where(fi => fi.StructuralType == StructuralType.Beam && GetBeamLevelId(fi) == level.Id);

        foreach (var beam in beams)
        {
            if (beam.Location is not LocationCurve locationCurve)
                continue;

            var flattened = FlattenZ0(locationCurve.Curve);
            if (flattened is null)
                continue;

            if (flattened.GetEndPoint(0).DistanceTo(flattened.GetEndPoint(1)) < MinSegmentLenFeet)
                continue;

            rawCenterlines.Add(new RawCenterline(flattened, EstimateHalfWidthFeet(beam, flattened)));
        }

        var walls = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Wall))
            .OfCategory(BuiltInCategory.OST_Walls)
            .Cast<Wall>()
            .Where(wall => IsWallAtLevel(wall, level));

        foreach (var wall in walls)
        {
            if (wall.Location is not LocationCurve { Curve: Line wallLine })
                continue;

            var flattened = FlattenZ0(wallLine);
            if (flattened is null || flattened.GetEndPoint(0).DistanceTo(flattened.GetEndPoint(1)) < MinSegmentLenFeet)
                continue;

            rawCenterlines.Add(new RawCenterline(flattened, Math.Max(wall.Width / 2.0, NodeTolFeet)));
        }

        var segments = SnapConnections(rawCenterlines);

        return CenterlineMerger.MergeCollinearAdjacent(segments);
    }

    /// <summary>Kiểm tra view có vách thẳng cắt qua cao độ Level đang mở.</summary>
    public static bool HasStraightWallCenterline(Document doc, ViewPlan view, Level level)
    {
        return new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Wall))
            .OfCategory(BuiltInCategory.OST_Walls)
            .Cast<Wall>()
            .Any(wall => wall.Location is LocationCurve { Curve: Line } && IsWallAtLevel(wall, level));
    }

    /// <summary>Kiểm tra thân vách có cắt qua mặt phẳng cao độ của Level.</summary>
    private static bool IsWallAtLevel(Wall wall, Level level)
    {
        var box = wall.get_BoundingBox(null);
        if (box is not null)
            return level.Elevation >= box.Min.Z - NodeTolFeet && level.Elevation <= box.Max.Z + NodeTolFeet;

        return wall.LevelId == level.Id;
    }

    /// <summary>Thuật toán chung: tách cắt-nhau/T-junction, gộp nút, rồi duyệt mặt đồ thị phẳng tìm mọi ô khép kín.</summary>
    public static List<List<Curve>> TraceClosedLoops(IReadOnlyList<Curve> segments)
    {
        var dedupedSegments = MergeDuplicateLines(segments);
        var splitSegments = SplitAtCrossingsAndJunctions(dedupedSegments);

        var edges = new List<GraphEdge>();
        var points = new List<XYZ>();

        int ClusterNode(XYZ point)
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].DistanceTo(point) <= NodeTolFeet)
                    return i;
            }

            points.Add(point);
            return points.Count - 1;
        }

        foreach (var segment in splitSegments)
        {
            var nodeA = ClusterNode(segment.GetEndPoint(0));
            var nodeB = ClusterNode(segment.GetEndPoint(1));
            if (nodeA == nodeB)
                continue;

            edges.Add(new GraphEdge(segment, nodeA, nodeB));
        }

        if (edges.Count == 0)
            return new List<List<Curve>>();

        var traces = PlanarFaceTracer.TraceBoundedFaces(edges, points);

        var results = new List<List<Curve>>();
        foreach (var trace in traces)
        {
            var loop = BuildLoop(trace);
            if (loop is not null)
                results.Add(loop);
        }

        return results;
    }

    /// <summary>Gộp các Line thẳng hàng trùng/chồng lấn/liền kề nhau (CenterlineMerger) trước khi dựng đồ thị — tránh cạnh trùng lặp làm hỏng thuật toán duyệt mặt; Arc giữ nguyên (không hỗ trợ gộp); truyền kèm các nút thật (FindProtectedPoints) để không gộp nuốt mất góc/T-junction đang được cạnh khác dùng.</summary>
    private static List<Curve> MergeDuplicateLines(IReadOnlyList<Curve> segments)
    {
        var lines = new List<Line>();
        var others = new List<Curve>();
        foreach (var segment in segments)
        {
            if (segment is Line line)
                lines.Add(line);
            else
                others.Add(segment);
        }

        var protectedPoints = FindProtectedPoints(segments);

        var result = new List<Curve>(others);
        result.AddRange(CenterlineMerger.MergeCollinearAdjacent(lines, protectedPoints));
        return result;
    }

    /// <summary>Điểm là nút THẬT cần bảo vệ — có ít nhất 2 curve KHÔNG cùng phương chạm đúng vào đó (góc/T-junction), tránh bị gộp collinear nuốt mất khi 1 curve khác chồng lấn xuyên qua điểm này.</summary>
    private static List<XYZ> FindProtectedPoints(IReadOnlyList<Curve> segments)
    {
        var directions = segments.Select(GetLineDirection).ToList();
        var protectedPoints = new List<XYZ>();

        for (var i = 0; i < segments.Count; i++)
        {
            if (directions[i] is not { } dirI)
                continue;

            for (var side = 0; side < 2; side++)
            {
                var point = segments[i].GetEndPoint(side);

                for (var j = 0; j < segments.Count; j++)
                {
                    if (j == i || directions[j] is not { } dirJ)
                        continue;

                    var cosAngle = Math.Abs(dirI.DotProduct(dirJ));
                    var angleDeg = Math.Acos(Math.Clamp(cosAngle, -1.0, 1.0)) * 180.0 / Math.PI;
                    if (angleDeg <= 2.0)
                        continue; // Cung phuong - khong phai goc/T-junction that, bo qua.

                    if (segments[j].GetEndPoint(0).DistanceTo(point) <= NodeTolFeet ||
                        segments[j].GetEndPoint(1).DistanceTo(point) <= NodeTolFeet)
                    {
                        protectedPoints.Add(point);
                        break;
                    }
                }
            }
        }

        return protectedPoints;
    }

    private static XYZ? GetLineDirection(Curve curve)
    {
        if (curve is not Line)
            return null;

        var dir = curve.GetEndPoint(1) - curve.GetEndPoint(0);
        return dir.IsZeroLength() ? null : dir.Normalize();
    }

    /// <summary>Tách cắt-nhau thật (kiểu dấu +) và T-junction giữa các curve chưa có nút chung.</summary>
    private static List<Curve> SplitAtCrossingsAndJunctions(IReadOnlyList<Curve> segments)
    {
        var n = segments.Count;
        if (n == 0)
            return new List<Curve>();

        var interiorSplits = new List<List<XYZ>>();
        for (var i = 0; i < n; i++)
            interiorSplits.Add(new List<XYZ>());

        // Cat nhau that su - tach ca hai curve.
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (!TryIntersectBounded(segments[i], segments[j], out var point, out var ti, out var tj))
                    continue;

                var lenI = segments[i].GetEndPoint(0).DistanceTo(segments[i].GetEndPoint(1));
                var lenJ = segments[j].GetEndPoint(0).DistanceTo(segments[j].GetEndPoint(1));
                if (IsNearEitherEnd(ti, lenI, EndpointExclusionFeet) || IsNearEitherEnd(tj, lenJ, EndpointExclusionFeet))
                    continue;

                interiorSplits[i].Add(point);
                interiorSplits[j].Add(point);
            }
        }

        // T-junction: 1 dau cham giua than curve khac - chi tach ben host.
        for (var i = 0; i < n; i++)
        {
            for (var side = 0; side < 2; side++)
            {
                var point = segments[i].GetEndPoint(side);
                if (HasCoincidentEndpointGeneric(segments, i, point))
                    continue;

                var best = FindBestJunctionHostGeneric(segments, i, point);
                if (best is not null)
                    interiorSplits[best.Value.HostIndex].Add(best.Value.Point);
            }
        }

        var result = new List<Curve>();
        for (var i = 0; i < n; i++)
        {
            if (interiorSplits[i].Count == 0 || segments[i] is not Line)
            {
                result.Add(segments[i]);
                continue;
            }

            var p0 = segments[i].GetEndPoint(0);
            var p1 = segments[i].GetEndPoint(1);
            var dir = p1 - p0;
            if (dir.GetLength() < MinSegmentLenFeet)
            {
                result.Add(segments[i]);
                continue;
            }
            var dirNormalized = dir.Normalize();

            var orderedPoints = new List<XYZ> { p0 };
            orderedPoints.AddRange(interiorSplits[i]);
            orderedPoints.Add(p1);
            orderedPoints.Sort((a, b) => (a - p0).DotProduct(dirNormalized).CompareTo((b - p0).DotProduct(dirNormalized)));

            for (var k = 0; k < orderedPoints.Count - 1; k++)
            {
                var a = orderedPoints[k];
                var b = orderedPoints[k + 1];
                if (a.DistanceTo(b) < MinSegmentLenFeet)
                    continue;

                try
                {
                    result.Add(Line.CreateBound(a, b));
                }
                catch (Exception)
                {
                    // Doan suy bien do sai so lam tron - bo qua rieng doan nay.
                }
            }
        }

        return result;
    }

    private static bool HasCoincidentEndpointGeneric(IReadOnlyList<Curve> segments, int selfIndex, XYZ point)
    {
        for (var j = 0; j < segments.Count; j++)
        {
            if (j == selfIndex)
                continue;

            if (segments[j].GetEndPoint(0).DistanceTo(point) <= NodeTolFeet ||
                segments[j].GetEndPoint(1).DistanceTo(point) <= NodeTolFeet)
                return true;
        }

        return false;
    }

    /// <summary>FindBestTJunctionHost tổng quát, dùng dung sai cố định thay vì bề rộng dầm.</summary>
    private static (int HostIndex, XYZ Point)? FindBestJunctionHostGeneric(IReadOnlyList<Curve> segments, int selfIndex, XYZ point)
    {
        var bestDist = double.MaxValue;
        (int HostIndex, XYZ Point)? best = null;

        for (var j = 0; j < segments.Count; j++)
        {
            if (j == selfIndex)
                continue;

            var (closest, t) = ClosestPointOnSegment(segments[j], point);
            var hostLen = segments[j].GetEndPoint(0).DistanceTo(segments[j].GetEndPoint(1));
            if (IsNearEitherEnd(t, hostLen, EndpointExclusionFeet))
                continue;

            var dist = closest.DistanceTo(point);
            if (dist > GenericJunctionTolFeet || dist >= bestDist)
                continue;

            bestDist = dist;
            best = (j, closest);
        }

        return best;
    }

    /// <summary>Dựng vòng khép kín từ 1 chuỗi Curve ĐÃ ĐỊNH HƯỚNG đúng thứ tự đi (do PlanarFaceTracer trả về, không cần dò/bootstrap hướng như luồng pick tay) — trim/extend nối tiếp qua EdgeTrimmer, đóng vòng cuối cùng giữa cạnh cuối và cạnh đầu.</summary>
    private static List<Curve>? BuildLoop(List<Curve> orientedCurves)
    {
        if (orientedCurves.Count < 3)
            return null;

        var edges = new List<Curve> { orientedCurves[0] };

        for (var i = 1; i < orientedCurves.Count; i++)
        {
            if (!AppendTrimmed(edges, orientedCurves[i]))
                return null;
        }

        if (edges.Count < 3)
            return null;

        var closeResult = EdgeTrimmer.TrimExtend(edges[^1], edges[0], forceNextOrientation: true);
        switch (closeResult.Kind)
        {
            case TrimOutcomeKind.Collinear:
                edges[^1] = closeResult.Merged!;
                edges.RemoveAt(0);
                return edges.Count >= 3 ? edges : null;
            case TrimOutcomeKind.Trimmed:
                edges[^1] = closeResult.PrevTrimmed!;
                edges[0] = closeResult.NextTrimmed!;
                return edges;
            default:
                return null;
        }
    }

    private static bool AppendTrimmed(List<Curve> edges, Curve next)
    {
        var result = EdgeTrimmer.TrimExtend(edges[^1], next);
        switch (result.Kind)
        {
            case TrimOutcomeKind.Collinear:
                edges[^1] = result.Merged!;
                return true;
            case TrimOutcomeKind.Trimmed:
                edges[^1] = result.PrevTrimmed!;
                edges.Add(result.NextTrimmed!);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Xử lý theo đúng thứ tự ưu tiên: (1) 2 dầm CẮT NHAU thật sự (mỗi dầm chạy xuyên qua điểm giao, không dầm nào có đầu mút gần đó — vd 2 dầm vẽ tràn qua nhau kiểu dấu +) → tách CẢ HAI tại đúng giao điểm; (2) T-junction dầm-dầm bình thường (dầm phụ neo vào MẶT dầm chính) cho đầu chưa có ai xử lý. Không có các bước này, khoảng hở/điểm giao không có nút sẽ làm ô sàn không khép kín được. Cơ chế snap theo Cột đã BỎ HẲN (xem doc-comment đầu file) — khoảng hở tại cột (dầm dừng ở mặt cột thay vì tim cột) hiện KHÔNG được xử lý.</summary>
    private static List<Line> SnapConnections(List<RawCenterline> rawCenterlines)
    {
        var n = rawCenterlines.Count;
        var endpointOverride = new XYZ?[n, 2];
        var interiorSplits = new List<List<XYZ>>();
        for (var i = 0; i < n; i++)
            interiorSplits.Add(new List<XYZ>());

        // (1) 2 dam cat nhau that su (giao diem nam SAU trong ca 2 than, khong dam nao co dau mut gan do) -
        // tach CA HAI dam tai dung giao diem, khac voi T-junction (buoc 2) chi tach 1 ben.
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (!TryIntersectBounded(rawCenterlines[i].Curve, rawCenterlines[j].Curve, out var point, out var ti, out var tj))
                    continue;

                var lenI = rawCenterlines[i].Curve.GetEndPoint(0).DistanceTo(rawCenterlines[i].Curve.GetEndPoint(1));
                var lenJ = rawCenterlines[j].Curve.GetEndPoint(0).DistanceTo(rawCenterlines[j].Curve.GetEndPoint(1));
                if (IsNearEitherEnd(ti, lenI, EndpointExclusionFeet) || IsNearEitherEnd(tj, lenJ, EndpointExclusionFeet))
                    continue;

                interiorSplits[i].Add(point);
                interiorSplits[j].Add(point);
            }
        }

        // (2) T-junction dam-dam binh thuong.
        for (var i = 0; i < n; i++)
        {
            for (var side = 0; side < 2; side++)
            {
                var point = rawCenterlines[i].Curve.GetEndPoint(side);

                if (HasCoincidentEndpoint(rawCenterlines, i, point))
                    continue; // Da co dam khac cham dung dau-doi-dau roi - de ClusterNode xu ly binh thuong.

                var best = FindBestTJunctionHost(rawCenterlines, i, point);
                if (best is null)
                    continue;

                endpointOverride[i, side] = best.Value.Point;
                interiorSplits[best.Value.HostIndex].Add(best.Value.Point);
            }
        }

        var segments = new List<Line>();

        for (var i = 0; i < n; i++)
        {
            var p0 = endpointOverride[i, 0] ?? rawCenterlines[i].Curve.GetEndPoint(0);
            var p1 = endpointOverride[i, 1] ?? rawCenterlines[i].Curve.GetEndPoint(1);

            var dir = p1 - p0;
            if (dir.GetLength() < MinSegmentLenFeet)
                continue;
            var dirNormalized = dir.Normalize();

            var orderedPoints = new List<XYZ> { p0 };
            orderedPoints.AddRange(interiorSplits[i]);
            orderedPoints.Add(p1);
            orderedPoints.Sort((a, b) => (a - p0).DotProduct(dirNormalized).CompareTo((b - p0).DotProduct(dirNormalized)));

            for (var k = 0; k < orderedPoints.Count - 1; k++)
            {
                var a = orderedPoints[k];
                var b = orderedPoints[k + 1];
                if (a.DistanceTo(b) < MinSegmentLenFeet)
                    continue;

                try
                {
                    segments.Add(Line.CreateBound(a, b));
                }
                catch (Exception)
                {
                    // Doan suy bien do sai so lam tron - bo qua rieng doan nay.
                }
            }
        }

        return segments;
    }

    private static bool HasCoincidentEndpoint(List<RawCenterline> rawCenterlines, int selfIndex, XYZ point)
    {
        for (var j = 0; j < rawCenterlines.Count; j++)
        {
            if (j == selfIndex)
                continue;

            if (rawCenterlines[j].Curve.GetEndPoint(0).DistanceTo(point) <= NodeTolFeet ||
                rawCenterlines[j].Curve.GetEndPoint(1).DistanceTo(point) <= NodeTolFeet)
                return true;
        }

        return false;
    }

    /// <summary>Tìm dầm host gần nhất mà điểm cho trước có thể "cham vào giữa" (T-junction) — điểm rơi phía trong thân host (không quá gần 1 trong 2 đầu, xem EndpointExclusionFeet) và trong khoảng cách ≤ nửa bề rộng host + biên an toàn.</summary>
    private static (int HostIndex, XYZ Point)? FindBestTJunctionHost(List<RawCenterline> rawCenterlines, int selfIndex, XYZ point)
    {
        var bestDist = double.MaxValue;
        (int HostIndex, XYZ Point)? best = null;

        for (var j = 0; j < rawCenterlines.Count; j++)
        {
            if (j == selfIndex)
                continue;

            var (closest, t) = ClosestPointOnSegment(rawCenterlines[j].Curve, point);
            var hostLen = rawCenterlines[j].Curve.GetEndPoint(0).DistanceTo(rawCenterlines[j].Curve.GetEndPoint(1));
            if (IsNearEitherEnd(t, hostLen, EndpointExclusionFeet))
                continue;

            var dist = closest.DistanceTo(point);
            var tol = rawCenterlines[j].HalfWidthFeet + TJunctionMarginFeet;
            if (dist > tol || dist >= bestDist)
                continue;

            bestDist = dist;
            best = (j, closest);
        }

        return best;
    }

    private static (XYZ Point, double T) ClosestPointOnSegment(Curve segment, XYZ point)
    {
        var a = segment.GetEndPoint(0);
        var b = segment.GetEndPoint(1);
        var ab = b - a;
        var lenSq = ab.DotProduct(ab);
        if (lenSq < 1e-12)
            return (a, 0);

        var t = Math.Clamp((point - a).DotProduct(ab) / lenSq, 0.0, 1.0);
        return (a + t * ab, t);
    }

    /// <summary>Giao điểm 2 đường thẳng vô hạn chứa 2 curve, kèm tham số t dọc theo MỖI curve (0=đầu, 1=cuối, có thể &lt;0 hoặc &gt;1 nếu giao điểm nằm ngoài đoạn thật) — dùng để phân biệt "cắt nhau thật trong thân" (t trong (0,1) cho CẢ HAI) với giao điểm ảo ngoài đoạn. False nếu song song (định thức ~0).</summary>
    private static bool TryIntersectBounded(Curve a, Curve b, out XYZ point, out double tA, out double tB)
    {
        var a0 = a.GetEndPoint(0);
        var a1 = a.GetEndPoint(1);
        var b0 = b.GetEndPoint(0);
        var b1 = b.GetEndPoint(1);

        var dA = a1 - a0;
        var dB = b1 - b0;

        var denom = dA.X * dB.Y - dA.Y * dB.X;
        if (Math.Abs(denom) < 1e-9)
        {
            point = XYZ.Zero;
            tA = 0;
            tB = 0;
            return false;
        }

        var e = b0 - a0;
        tA = (e.X * dB.Y - e.Y * dB.X) / denom;
        tB = (e.X * dA.Y - e.Y * dA.X) / denom;

        point = new XYZ(a0.X + tA * dA.X, a0.Y + tA * dA.Y, 0);
        return true;
    }

    /// <summary>Ước lượng nửa bề rộng thật của 1 dầm (vuông góc với chiều dài) từ BoundingBox — chính xác cho dầm chạy dọc trục X/Y (phổ biến trong lưới khung thật); dầm xoay chéo có thể bị ước lượng rộng hơn thật (bbox world-aligned), chấp nhận được vì chỉ làm ngưỡng T-junction RỘNG hơn, không hẹp hơn.</summary>
    private static double EstimateHalfWidthFeet(FamilyInstance beam, Curve centerline)
    {
        try
        {
            var bb = beam.get_BoundingBox(null);
            if (bb is null)
                return DefaultHalfWidthFeet;

            var dir = centerline.GetEndPoint(1) - centerline.GetEndPoint(0);
            var horizDir = new XYZ(dir.X, dir.Y, 0);
            if (horizDir.IsZeroLength())
                return DefaultHalfWidthFeet;
            horizDir = horizDir.Normalize();
            var perp = new XYZ(-horizDir.Y, horizDir.X, 0);

            var corners = new[]
            {
                new XYZ(bb.Min.X, bb.Min.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0),
                new XYZ(bb.Max.X, bb.Max.Y, 0), new XYZ(bb.Min.X, bb.Max.Y, 0),
            };
            var projections = corners.Select(p => p.DotProduct(perp)).ToList();
            var widthFeet = projections.Max() - projections.Min();

            return Math.Max(widthFeet / 2.0, UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters));
        }
        catch (Exception)
        {
            return DefaultHalfWidthFeet;
        }
    }

    /// <summary>Chỉ hỗ trợ dầm thẳng (Line) ở v1 — dầm cong (Arc) bỏ qua khỏi đồ thị, giống giới hạn đã áp dụng ở luồng pick tay.</summary>
    private static Line? FlattenZ0(Curve curve)
    {
        try
        {
            return curve switch
            {
                Line line => Line.CreateBound(WithZ0(line.GetEndPoint(0)), WithZ0(line.GetEndPoint(1))),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static XYZ WithZ0(XYZ p) => new(p.X, p.Y, 0);

    /// <summary>Duyệt mặt (face traversal) chuẩn của đồ thị phẳng — biến mạng lưới cạnh (nút + cạnh, không quan tâm nguồn gốc dầm hay line) thành từng "half-edge" có hướng, sắp xếp các half-edge quanh mỗi nút theo góc, rồi đi theo quy tắc "cạnh tiếp theo = cạnh đứng ngay TRƯỚC cạnh đối xứng (twin) trong danh sách đã sắp xếp tại nút đích" (tương đương "đi men tường phải", rẽ vào cạnh góc nhỏ nhất bên phải). Quy tắc này khiến MỌI mặt bị chặn (bounded face — tức ô sàn thật) luôn có diện tích DƯƠNG khi tính theo shoelace, còn đúng 1 mặt ngoài cùng (unbounded) của MỖI cụm liên thông luôn ra diện tích ÂM — cho phép loại bỏ mặt ngoài chỉ bằng dấu diện tích, không cần suy đoán theo kích thước.</summary>
    private static class PlanarFaceTracer
    {
        private sealed record HalfEdge(int From, int To, Curve OrientedCurve);

        public static List<List<Curve>> TraceBoundedFaces(List<GraphEdge> graphEdges, List<XYZ> nodePoints)
        {
            var halfEdges = new List<HalfEdge>();
            var twin = new List<int>();

            foreach (var graphEdge in graphEdges)
            {
                var idxForward = halfEdges.Count;
                var idxBackward = idxForward + 1;
                halfEdges.Add(new HalfEdge(graphEdge.NodeA, graphEdge.NodeB, OrientStartAt(graphEdge.Curve, nodePoints[graphEdge.NodeA])));
                halfEdges.Add(new HalfEdge(graphEdge.NodeB, graphEdge.NodeA, OrientStartAt(graphEdge.Curve, nodePoints[graphEdge.NodeB])));
                twin.Add(idxBackward);
                twin.Add(idxForward);
            }

            var outgoingByNode = new Dictionary<int, List<int>>();
            for (var i = 0; i < halfEdges.Count; i++)
            {
                if (!outgoingByNode.TryGetValue(halfEdges[i].From, out var list))
                {
                    list = new List<int>();
                    outgoingByNode[halfEdges[i].From] = list;
                }

                list.Add(i);
            }

            double AngleOf(int halfEdgeIndex)
            {
                var he = halfEdges[halfEdgeIndex];
                var dir = nodePoints[he.To] - nodePoints[he.From];
                return Math.Atan2(dir.Y, dir.X);
            }

            foreach (var list in outgoingByNode.Values)
                list.Sort((x, y) => AngleOf(x).CompareTo(AngleOf(y)));

            var positionInList = new int[halfEdges.Count];
            foreach (var list in outgoingByNode.Values)
            {
                for (var pos = 0; pos < list.Count; pos++)
                    positionInList[list[pos]] = pos;
            }

            int Next(int h)
            {
                var t = twin[h];
                var atNode = halfEdges[t].From;
                var list = outgoingByNode[atNode];
                var pos = positionInList[t];
                var prevPos = (pos - 1 + list.Count) % list.Count;
                return list[prevPos];
            }

            var used = new bool[halfEdges.Count];
            var results = new List<List<Curve>>();

            for (var start = 0; start < halfEdges.Count; start++)
            {
                if (used[start])
                    continue;

                var trace = new List<int>();
                var current = start;
                var guard = 0;
                var maxSteps = halfEdges.Count + 4;

                do
                {
                    used[current] = true;
                    trace.Add(current);
                    current = Next(current);
                    guard++;
                } while (current != start && guard <= maxSteps);

                if (current != start)
                    continue; // Du lieu suy bien (goc trung, canh chong khit...) - bo qua rieng mat nay, khong sap ca luot quet.

                if (trace.Count < 3)
                    continue;

                var points = trace.Select(h => nodePoints[halfEdges[h].From]).ToList();
                var signedArea = PolygonMath.SignedArea(points);

                // Mat ngoai cung (unbounded) cua moi cum lien thong luon ra dien tich AM voi quy uoc Next()
                // o tren - chi giu mat co dien tich DUONG (bounded, tuc o san that).
                if (signedArea <= 1e-6)
                    continue;

                results.Add(trace.Select(h => halfEdges[h].OrientedCurve).ToList());
            }

            return results;
        }

        private static Curve OrientStartAt(Curve curve, XYZ desiredStart)
        {
            var d0 = curve.GetEndPoint(0).DistanceTo(desiredStart);
            var d1 = curve.GetEndPoint(1).DistanceTo(desiredStart);
            return d0 <= d1 ? curve : curve.CreateReversed();
        }
    }
}
