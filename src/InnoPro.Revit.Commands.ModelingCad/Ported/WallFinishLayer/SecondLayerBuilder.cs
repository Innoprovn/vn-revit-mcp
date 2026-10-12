using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFinishLayer;

/// <summary>Tạo Wall "lớp vật liệu tiếp theo" cho MỌI mặt tường người dùng đã PickObjects — khác Lớp tô
/// (WallFinishBuilder) ở chỗ CHỈ vẽ trên ĐÚNG 1 mặt mỗi tường (không quét hàng loạt, không vẽ cả 2
/// mặt). Mép lớp vật liệu align với ĐÚNG mặt đã chọn — dùng thẳng hình học mặt đó. Hỗ trợ CẢ mặt phẳng
/// (PlanarFace, tường thẳng — xem BuildOnLine) LẪN mặt trụ (CylindricalFace, tường cong — xem
/// BuildOnArc, analogii WallFinishBuilder.CreateArcWallCoatings: 1 Arc đồng tâm cùng góc quét, chỉ đổi
/// bán kính). Sau khi tạo, MỖI lớp vật liệu tự Join với ĐÚNG phần tử (Wall hoặc lớp tô — bất kỳ Wall
/// nào) mà mặt được chọn thuộc về (xem CreateLayerWall) — nếu người dùng chọn mặt trên 1 Wall lớp tô đã
/// tồn tại thì lớp vật liệu Join với lớp tô đó, không phải tường gốc.
///
/// GÓC GIAO NHAU (2 tường thẳng có mặt được chọn CHẠM đúng 1 đầu với nhau — xem MitreCorners): thay vì
/// để đầu thô 2 lớp vật liệu lệch nhau (hở góc trong/đè góc ngoài), TRIM/kéo dài cả 2 tới đúng GIAO ĐIỂM
/// giữa 2 đường tim lớp vật liệu — y hệt kiểu "miter" khung tranh WallFinishBuilder đã làm cho Lớp tô,
/// nhưng áp dụng cho ĐÚNG 1 đường mỗi tường (không phải 2 mặt Exterior/Interior như Lớp tô). Chỉ mitre
/// khi CHÍNH XÁC 1 target khác chạm đúng điểm đó (góc 3+ hướng bỏ qua, giữ đầu thô). Sau khi tạo hết,
/// còn tự Join CHÉO giữa các lớp vật liệu MỚI TẠO với nhau nếu bounding box giao nhau (phòng trường hợp
/// mitre không khít tuyệt đối do sai số dựng hình).
///
/// Cao độ chân = chân tường (bounding box, CÙNG công thức với Lớp tô) + offset người dùng nhập. Cao độ
/// đỉnh theo 1 trong 3 chế độ (xem SecondLayerTopMode): chiều cao tự do, offset theo đáy Ceiling gần đó
/// (dò 1 điểm ngay ngoài mặt đã chọn), hoặc offset theo 1 bề mặt khác người dùng PickObject thêm
/// (Command.RunSecondLayer đọc cao độ bề mặt đó qua GetFaceElevation TRƯỚC khi mở Transaction). GIỚI
/// HẠN: nếu mặt đã chọn là mặt ĐẦU tường cong (end cap, phẳng) thay vì mặt trụ bên hông thì bỏ qua.
/// </summary>
internal static class SecondLayerBuilder
{
    private static readonly double CeilingProbeInsetFeet = UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    /// <summary>Dung sai coi 2 đầu tường (khác nhau) là "chạm" nhau — góc giao nhau cần mitre.</summary>
    private static readonly double CornerToleranceFeet = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);

    /// <summary>Chặn an toàn: nếu giao điểm 2 đường tim lớp vật liệu cách đầu tường thô quá xa (2 đường
    /// gần song song — không phải góc thật) thì bỏ qua, giữ đầu thô.</summary>
    private static readonly double MaxMiterExtensionFeet = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);

    /// <summary>Hình học đường tim lớp vật liệu (trước khi mitre) của 1 target tường thẳng — WallP0/P1
    /// dùng để PHÁT HIỆN góc giao nhau (endpoint thật của tường), MaterialStart/End là đường thật sẽ
    /// dựng Wall (bị TryMitreEndpoint chỉnh lại nếu có góc).</summary>
    private sealed class LineTargetGeo
    {
        public required int TargetIndex { get; init; }
        public required XYZ WallP0 { get; init; }
        public required XYZ WallP1 { get; init; }
        public required XYZ Direction { get; init; }
        public required XYZ MaterialStart { get; set; }
        public required XYZ MaterialEnd { get; set; }
    }

    /// <summary>Tạo lớp vật liệu cho TOÀN BỘ mặt tường đã chọn trong 1 lượt — cần gom hết ở đây (thay vì
    /// tạo từng cái độc lập) để mitre góc giao nhau TRƯỚC khi dựng Wall thật (xem MitreCorners), và Join
    /// chéo các lớp mới tạo với nhau SAU khi tạo hết (xem JoinIntersectingLayers).</summary>
    internal static (int CreatedCount, List<string> Notes) BuildAll(
        Document doc, IReadOnlyList<(Wall Wall, Face Face)> targets, SecondLayerOptions options, double? referenceFaceZ)
    {
        var notes = new string[targets.Count];
        var createdWalls = new List<Wall>();
        var createdCount = 0;

        var lineGeos = new Dictionary<int, LineTargetGeo>();
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Face is PlanarFace planarFace)
            {
                var geo = ComputeLineGeometry(i, targets[i].Wall, planarFace, options);
                if (geo is not null)
                    lineGeos[i] = geo;
            }
        }

        MitreCorners(lineGeos.Values.ToList());

        for (var i = 0; i < targets.Count; i++)
        {
            var (wall, face) = targets[i];
            var (created, note, newWall) = BuildOne(doc, wall, face, options, referenceFaceZ, lineGeos.GetValueOrDefault(i));
            notes[i] = note;
            if (created)
            {
                createdCount++;
                if (newWall is not null)
                    createdWalls.Add(newWall);
            }
        }

        JoinIntersectingLayers(doc, createdWalls);

        return (createdCount, notes.ToList());
    }

    private static LineTargetGeo? ComputeLineGeometry(int index, Wall wall, PlanarFace face, SecondLayerOptions options)
    {
        if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line line)
            return null;

        var p0 = line.GetEndPoint(0);
        var p1 = line.GetEndPoint(1);
        var direction = (p1 - p0).Normalize();
        var normal = new XYZ(-direction.Y, direction.X, 0);
        var centerN = p0.DotProduct(normal);

        var faceOffsetN = face.Origin.DotProduct(normal);
        var sign = Math.Sign(faceOffsetN - centerN);
        if (sign == 0)
            sign = 1;

        var materialWidthFeet = options.MaterialType.Width;
        var layerCenterN = faceOffsetN + sign * (materialWidthFeet / 2.0);
        var offsetVec = normal.Multiply(layerCenterN - centerN);

        return new LineTargetGeo
        {
            TargetIndex = index,
            WallP0 = p0,
            WallP1 = p1,
            Direction = direction,
            MaterialStart = p0 + offsetVec,
            MaterialEnd = p1 + offsetVec
        };
    }

    /// <summary>Với MỖI target, thử mitre CẢ 2 đầu (WallP0 và WallP1) — xem TryMitreEndpoint. Sửa trực
    /// tiếp MaterialStart/MaterialEnd của từng LineTargetGeo trong danh sách.</summary>
    private static void MitreCorners(List<LineTargetGeo> geos)
    {
        foreach (var geo in geos)
        {
            TryMitreEndpoint(geo, geos, atStart: true);
            TryMitreEndpoint(geo, geos, atStart: false);
        }
    }

    /// <summary>Nếu CHÍNH XÁC 1 target khác trong <paramref name="all"/> có 1 đầu tường chạm đúng đầu
    /// <paramref name="geo"/> đang xét (WallP0 nếu atStart, ngược lại WallP1) — góc giao nhau 2 tường —
    /// thì TRIM đầu đó tới giao điểm giữa 2 đường tim lớp vật liệu (MaterialStart/End, đã offset ra mặt
    /// đã chọn) thay vì giữ đầu thô của tường. Từ 2 tường khác chạm cùng điểm trở lên (góc phức tạp) thì
    /// bỏ qua, giữ nguyên đầu thô.</summary>
    private static void TryMitreEndpoint(LineTargetGeo geo, List<LineTargetGeo> all, bool atStart)
    {
        var point = atStart ? geo.WallP0 : geo.WallP1;

        LineTargetGeo? mate = null;
        foreach (var other in all)
        {
            if (other.TargetIndex == geo.TargetIndex)
                continue;

            var touches = other.WallP0.DistanceTo(point) <= CornerToleranceFeet ||
                          other.WallP1.DistanceTo(point) <= CornerToleranceFeet;
            if (!touches)
                continue;

            if (mate is not null)
                return;

            mate = other;
        }

        if (mate is null)
            return;

        var selfAnchor = atStart ? geo.MaterialStart : geo.MaterialEnd;
        var intersection = IntersectLines(selfAnchor, geo.Direction, mate.MaterialStart, mate.Direction);
        if (intersection is null || intersection.DistanceTo(point) > MaxMiterExtensionFeet)
            return;

        if (atStart)
            geo.MaterialStart = intersection;
        else
            geo.MaterialEnd = intersection;
    }

    private static XYZ? IntersectLines(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
    {
        var denom = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denom) < 1e-9)
            return null;

        var diff = p2 - p1;
        var t1 = (diff.X * d2.Y - diff.Y * d2.X) / denom;
        return new XYZ(p1.X + d1.X * t1, p1.Y + d1.Y * t1, p1.Z);
    }

    private static (bool Created, string Note, Wall? NewWall) BuildOne(
        Document doc, Wall wall, Face face, SecondLayerOptions options, double? referenceFaceZ, LineTargetGeo? lineGeo)
    {
        if (wall.Location is not LocationCurve locationCurve)
            return (false, "Không đọc được Location Curve của tường.", null);

        var bbox = wall.get_BoundingBox(null);
        if (bbox is null ||
            wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() is not { } baseLevelId ||
            doc.GetElement(baseLevelId) is not Level baseLevel)
            return (false, "Không đọc được Base Level của tường.", null);

        var baseOffsetFeet = UnitUtils.ConvertToInternalUnits(options.BaseOffsetMm, UnitTypeId.Millimeters);
        var layerBottomZ = bbox.Min.Z + baseOffsetFeet;

        // Diem tham chieu + huong "ra ngoai mat" dung chung cho ca mat phang (Line wall) lan mat tru
        // (Arc wall) - dung de do Ceiling gan mat da chon khi can (xem FindNearestCeilingBottomZ).
        XYZ probeOrigin;
        XYZ probeDirection;
        switch (face)
        {
            case PlanarFace planar:
                probeOrigin = planar.Origin;
                probeDirection = planar.FaceNormal;
                break;

            case CylindricalFace cylindrical:
                var faceMid = FaceMidpoint(cylindrical);
                if (faceMid is null)
                    return (false, "Không đọc được hình học mặt tường cong đã chọn.", null);

                var alongAxis = (faceMid - cylindrical.Origin).DotProduct(cylindrical.Axis);
                var onAxis = cylindrical.Origin + cylindrical.Axis.Multiply(alongAxis);
                probeOrigin = faceMid;
                probeDirection = (faceMid - onAxis).Normalize();
                break;

            default:
                return (false, "Chỉ hỗ trợ mặt tường phẳng (tường thẳng) hoặc mặt trụ (tường cong).", null);
        }

        double topZ;
        switch (options.TopMode)
        {
            case SecondLayerTopMode.FreeHeight:
                topZ = layerBottomZ + UnitUtils.ConvertToInternalUnits(options.FreeHeightMm, UnitTypeId.Millimeters);
                break;

            case SecondLayerTopMode.OffsetFromCeilingBottom:
                var ceilingBottomZ = FindNearestCeilingBottomZ(doc, probeOrigin, probeDirection);
                if (ceilingBottomZ is null)
                    return (false, "Không tìm thấy Ceiling gần mặt tường đã chọn.", null);
                topZ = ceilingBottomZ.Value + UnitUtils.ConvertToInternalUnits(options.CeilingOffsetMm, UnitTypeId.Millimeters);
                break;

            case SecondLayerTopMode.OffsetFromSelectedFace:
                if (referenceFaceZ is null)
                    return (false, "Chưa có cao độ bề mặt tham chiếu.", null);
                topZ = referenceFaceZ.Value + UnitUtils.ConvertToInternalUnits(options.SelectedFaceOffsetMm, UnitTypeId.Millimeters);
                break;

            default:
                return (false, "Chế độ cao độ đỉnh không hợp lệ.", null);
        }

        var heightFeet = topZ - layerBottomZ;
        if (heightFeet <= 1e-6)
            return (false, "Cao độ đỉnh không lớn hơn cao độ chân — kiểm tra lại offset/chiều cao đã nhập.", null);

        if (locationCurve.Curve is Line)
        {
            if (lineGeo is null)
                return (false, "Mặt tường đã chọn không phải mặt phẳng hợp lệ.", null);

            try
            {
                var curve = Line.CreateBound(lineGeo.MaterialStart, lineGeo.MaterialEnd);
                return CreateLayerWall(doc, wall, curve, layerBottomZ, heightFeet, baseLevel, options.MaterialType);
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi tạo lớp vật liệu — {ex.Message}", null);
            }
        }

        if (locationCurve.Curve is Arc wallArc)
            return BuildOnArc(doc, wall, wallArc, face as CylindricalFace, layerBottomZ, heightFeet, baseLevel, options);

        return (false, "Location Curve của tường không phải Line/Arc — chưa hỗ trợ.", null);
    }

    /// <summary>Lớp vật liệu cho tường CONG — 1 Arc ĐỒNG TÂM, cùng góc quét với tường host, chỉ đổi bán
    /// kính = bán kính mặt trụ đã chọn (đo bằng cách lấy 1 điểm thật trên mặt rồi đo khoảng cách XY tới
    /// tâm — CylindricalFace không có sẵn thuộc tính bán kính vô hướng) ± nửa bề rộng Type vật liệu, cùng
    /// hướng ra ngoài/vào trong với mặt đã chọn. CHƯA mitre góc cho tường cong (giống giới hạn của Lớp tô
    /// với Arc).</summary>
    private static (bool, string, Wall?) BuildOnArc(
        Document doc, Wall wall, Arc wallArc, CylindricalFace? face, double layerBottomZ, double heightFeet,
        Level baseLevel, SecondLayerOptions options)
    {
        if (face is null)
            return (false, "Mặt tường cong đã chọn không phải mặt trụ bên hông (có thể là mặt đầu tường) — chưa hỗ trợ.", null);

        var faceMid = FaceMidpoint(face);
        if (faceMid is null)
            return (false, "Không đọc được hình học mặt tường cong đã chọn.", null);

        var center = wallArc.Center;
        var centerlineRadius = wallArc.Radius;
        var faceRadius = new XYZ(center.X, center.Y, 0).DistanceTo(new XYZ(faceMid.X, faceMid.Y, 0));

        var sign = Math.Sign(faceRadius - centerlineRadius);
        if (sign == 0)
            sign = 1;

        var materialWidthFeet = options.MaterialType.Width;
        var layerRadius = faceRadius + sign * (materialWidthFeet / 2.0);
        if (layerRadius <= 1e-6)
            return (false, "Bán kính lớp vật liệu không hợp lệ (âm hoặc bằng 0) — kiểm tra lại bề rộng Type vật liệu.", null);

        try
        {
            var curve = Arc.Create(
                center, layerRadius, wallArc.GetEndParameter(0), wallArc.GetEndParameter(1),
                wallArc.XDirection, wallArc.YDirection);
            return CreateLayerWall(doc, wall, curve, layerBottomZ, heightFeet, baseLevel, options.MaterialType);
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi khi tạo lớp vật liệu — {ex.Message}", null);
        }
    }

    /// <summary>Dựng Wall thật rồi Join NGAY với <paramref name="hostWall"/> — CHÍNH LÀ phần tử mà mặt
    /// được chọn thuộc về (có thể là tường gốc, cũng có thể là 1 Wall lớp tô nếu người dùng chọn mặt
    /// trên đó), không phải lúc nào cũng là tường xây thô.</summary>
    private static (bool, string, Wall?) CreateLayerWall(
        Document doc, Wall hostWall, Curve curve, double layerBottomZ, double heightFeet, Level baseLevel, WallType materialType)
    {
        var newWall = Wall.Create(
            doc, curve, materialType.Id, baseLevel.Id,
            heightFeet, layerBottomZ - baseLevel.ProjectElevation, false, false);
        doc.Regenerate();

        try
        {
            JoinGeometryUtils.JoinGeometry(doc, hostWall, newWall);
        }
        catch
        {
            return (true, "Đã tạo lớp vật liệu (Join với tường/lớp tô đã chọn thất bại, tự Join tay nếu cần).", newWall);
        }

        return (true, "Đã tạo lớp vật liệu.", newWall);
    }

    /// <summary>Sau khi tạo hết lớp vật liệu, Join CHÉO giữa các lớp MỚI TẠO với nhau nếu bounding box
    /// giao nhau — bù thêm cho trường hợp mitre không khít tuyệt đối do sai số dựng hình, hoặc góc 3+
    /// hướng không được mitre. Không Join lại với host (đã làm ngay lúc tạo, xem CreateLayerWall).</summary>
    private static void JoinIntersectingLayers(Document doc, List<Wall> createdWalls)
    {
        const double bufferFeet = 0.01;
        var boxes = createdWalls.Select(w => w.get_BoundingBox(null)).ToList();

        for (var i = 0; i < createdWalls.Count; i++)
        {
            var boxI = boxes[i];
            if (boxI is null)
                continue;

            for (var j = i + 1; j < createdWalls.Count; j++)
            {
                var boxJ = boxes[j];
                if (boxJ is null || !BoundingBoxesOverlap(boxI, boxJ, bufferFeet))
                    continue;

                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, createdWalls[i], createdWalls[j]))
                        JoinGeometryUtils.JoinGeometry(doc, createdWalls[i], createdWalls[j]);
                }
                catch
                {
                    // Bo qua rieng cap nay, khong lam gian doan cac cap con lai.
                }
            }
        }
    }

    private static bool BoundingBoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b, double bufferFeet) =>
        a.Min.X - bufferFeet <= b.Max.X && b.Min.X - bufferFeet <= a.Max.X &&
        a.Min.Y - bufferFeet <= b.Max.Y && b.Min.Y - bufferFeet <= a.Max.Y &&
        a.Min.Z - bufferFeet <= b.Max.Z && b.Min.Z - bufferFeet <= a.Max.Z;

    /// <summary>1 điểm thật bất kỳ nằm trên Face (không nhất thiết là tâm hình học) — lấy qua Evaluate
    /// tại UV giữa bounding box tham số của mặt. Dùng cho cả việc đo bán kính mặt trụ lẫn dò điểm thăm dò
    /// Ceiling.</summary>
    private static XYZ? FaceMidpoint(Face face)
    {
        try
        {
            var box = face.GetBoundingBox();
            var mid = new UV((box.Min.U + box.Max.U) / 2.0, (box.Min.V + box.Max.V) / 2.0);
            return face.Evaluate(mid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dò gần đúng 1 điểm ngay ngoài mặt tường đã chọn (lùi vào CeilingProbeInsetFeet theo
    /// hướng ra ngoài mặt — pháp tuyến với mặt phẳng, hoặc phương bán kính với mặt trụ) rồi tìm Ceiling
    /// có bounding box (XY) chứa điểm đó — nhiều Ceiling trùng thì lấy CAO ĐỘ MẶT DƯỚI THẤP NHẤT (an
    /// toàn nhất — lớp vật liệu dừng dưới MỌI Ceiling liên quan, không đâm xuyên cái nào).</summary>
    private static double? FindNearestCeilingBottomZ(Document doc, XYZ probeOrigin, XYZ probeDirection)
    {
        var probe = probeOrigin + probeDirection.Multiply(CeilingProbeInsetFeet);
        var ceilings = new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>();

        double? best = null;
        foreach (var ceiling in ceilings)
        {
            var bbox = ceiling.get_BoundingBox(null);
            if (bbox is null)
                continue;

            if (probe.X < bbox.Min.X || probe.X > bbox.Max.X || probe.Y < bbox.Min.Y || probe.Y > bbox.Max.Y)
                continue;

            var bottomZ = bbox.Min.Z;
            if (best is null || bottomZ < best.Value)
                best = bottomZ;
        }

        return best;
    }

    /// <summary>Đọc cao độ Z của 1 Face bất kỳ (không nhất thiết là mặt tường) — dùng cho chế độ Offset
    /// theo bề mặt chọn. PlanarFace đọc thẳng Origin.Z (đa số trường hợp thật: đáy dầm/sàn phẳng ngang);
    /// Face khác (hiếm, mặt cong) thử Evaluate tại điểm giữa UV — trả về null nếu không đọc được.</summary>
    internal static double? GetFaceElevation(Face? face)
    {
        if (face is null)
            return null;

        if (face is PlanarFace planar)
            return planar.Origin.Z;

        return FaceMidpoint(face)?.Z;
    }
}
