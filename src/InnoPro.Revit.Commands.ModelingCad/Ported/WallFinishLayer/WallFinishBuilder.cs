using Autodesk.Revit.DB;

// Alias 1 tuple duong tim + 2 mat that cua 1 Wall host (P0/P1/Direction/Normal deu theo dung quy uoc
// tinh tu LocationCurve trong file nay: Normal = xoay Direction 90 do, CenterN/ExtOffsetN/IntOffsetN =
// toa do chieu theo Normal) - dung chung cho ca ban than 1 tuong dang xu ly LAN tuong hang xom (goc chu
// L/chu T), tranh phai quet lai hinh hoc (GetSideFaceOffset) nhieu lan cho cung 1 tuong.
using WallGeom = (Autodesk.Revit.DB.XYZ P0, Autodesk.Revit.DB.XYZ P1, Autodesk.Revit.DB.XYZ Direction,
    Autodesk.Revit.DB.XYZ Normal, double CenterN, double ExtOffsetN, double IntOffsetN);

namespace InnoPro.Revit.Commands.ModelingCad.WallFinishLayer;

/// <summary>Tạo 2 Wall "lớp tô" (1 mỗi mặt) cho mỗi Wall host — mép lớp tô align với mép tường vì
/// dùng ĐÚNG mặt hình học thật của tường (HostObjectUtils.GetSideFaces, không giả định Width/2 —
/// tránh sai nếu Location Line của tường host không phải Wall Centerline), không phải copy dịch từ
/// Location Curve. Cao độ chân = chân tường (bounding box thật, không phụ thuộc Base Constraint/Base
/// Offset cụ thể ra sao) + offset người dùng nhập; cao độ đỉnh theo 1 trong 2 chế độ (bằng đỉnh tường
/// + offset, hoặc CAO HƠN mặt trên Ceiling gần đó 1 khoảng — xem FindCeilingTopNear). Wall.Create luôn đặt curve truyền vào là Wall
/// Centerline — lớp tô GIỮ NGUYÊN dạng đó (không đổi Location Line). Mỗi lớp tô tự Join với tường host
/// lúc vừa tạo, và sau khi tạo hết còn tự Join CHÉO giữa các lớp tô MỚI với nhau nếu giao nhau (xem
/// JoinIntersectingCoatings — vd 2 tường vuông góc gặp góc).
///
/// TƯỜNG CHỮ T (xem FindTJunctions): khi 1 tường host khác (B) có 1 đầu chạm vào GIỮA thân tường host
/// đang xử lý (W) — không phải chạm 2 đầu (góc thường, Join thường xử lý tốt) — cạnh lớp tô của W ở
/// PHÍA B đứng (phía lớp tô của B sẽ hội tụ vào) không được vẽ LIỀN 1 đoạn xuyên qua điểm chạm đó nữa,
/// mà TÁCH THÀNH 2 đoạn Wall lớp tô riêng, mỗi đoạn dừng đúng tại điểm chạm — để Join hình thành 2 góc
/// L sạch (2 chiều) thay vì 1 điểm chữ T (3 chiều) dễ để lại vệt/khe dư khi Revit tính Join. Cạnh lớp
/// tô phía ĐỐI DIỆN của W không bị ảnh hưởng, vẫn vẽ liền 1 đoạn như bình thường.
///
/// TƯỜNG CHỮ L (2 tường chạm ĐÚNG 1 đầu với nhau, xem FindCornerMate/GetMiteredCornerPoint): đầu lớp
/// tô của W tại điểm góc đó KHÔNG dùng đầu thô của tường host nữa (đầu thô 2 bên lệch nhau — để hở góc
/// trong/đè góc ngoài, đúng lỗi trong ảnh người dùng gửi) mà TRIM/kéo dài tới đúng GIAO ĐIỂM giữa 2
/// đường tim lớp tô (của W và tường hàng xóm), y hệt kiểu "miter" khung tranh — cả 2 tường độc lập tính
/// ra CÙNG 1 điểm giao (đối xứng), nên 2 lớp tô luôn khớp khít tại góc mà không cần dựa vào Join để lấp
/// khoảng hở. Ghép PHÍA Exterior của W với Exterior của láng giềng, Interior với Interior (giả định quy
/// ước Exterior/Interior nhất quán quanh 1 công trình — đúng với cách Revit thường tự set khi vẽ chuỗi
/// tường liên tục, CHỈ sai nếu người dùng cố tình Flip lệch từng đoạn). Chỉ mitre khi CHÍNH XÁC 1 tường
/// khác chạm đúng đầu đó (góc 3-4 hướng trở lên bỏ qua, giữ đầu thô — quá phức tạp cho v1).
///
/// TƯỜNG ATTACH TOP VÀO ROOF (đỉnh tường vát theo mái, vd tường đầu hồi — xem GetHostTopZAt): khi
/// HeightMode = MatchWallHeight, KHÔNG dùng 1 cao độ đỉnh đồng nhất (hostTopZ) cho cả đoạn lớp tô nữa —
/// dò cao độ mặt trên THẬT của tường host riêng cho TỪNG đầu đoạn (bắn tia thẳng đứng qua
/// HostObjectUtils.GetTopFaces) rồi dựng lớp tô bằng Wall.Create(profile) với 4 cạnh bám đúng cao độ đó
/// nếu 2 đầu lệch nhau (đỉnh nghiêng) — tránh lỗi lớp tô dư ra hình chữ thập tại đỉnh mái do trước đây
/// dùng chiều cao đồng nhất khiến lớp tô cao vượt khỏi mái ở khắp nơi trừ đúng đỉnh, 2 lớp tô của 2
/// tường gặp nhau ở góc mái đều dư phần thừa đó đè lên nhau. Tường đỉnh phẳng bình thường (không Attach
/// Top) không bị ảnh hưởng — vẫn dùng Wall.Create(curve, height) như cũ.
///
/// TƯỜNG CONG (Arc — xem CreateArcWallCoatings): lớp tô là 1 Arc ĐỒNG TÂM, cùng góc quét, chỉ đổi bán
/// kính (bán kính mặt Exterior/Interior thật ± nửa bề rộng lớp tô — analogii với GetSideFaceOffset ở
/// tường thẳng, xem GetSideFaceRadius) — dựng bằng Arc.Create(center, radius, startAngle, endAngle,
/// xVector, yVector) giữ nguyên tâm/mặt phẳng/2 đầu góc quét của Arc gốc. GIỚI HẠN: tường cong CHƯA hỗ
/// trợ tách theo tường chữ T lẫn mitre góc chữ L (FindTJunctions/FindCornerMate CHỈ xét tường thẳng) —
/// tường cong luôn vẽ liền 1 Arc trọn vẹn theo đúng chiều dài tường, các góc nối với tường khác (thẳng
/// hay cong) đều chỉ trông cậy vào Join thường (JoinIntersectingCoatings), có thể chưa sạch bằng góc
/// chữ L giữa 2 tường thẳng.
///
/// Chỉ hỗ trợ Wall Kind=Basic, Location dạng Line hoặc Arc (curve khác — HermiteSpline... — bỏ qua,
/// hiếm gặp trên Wall thật). GIỚI HẠN ĐÃ BIẾT: lớp tô KHÔNG tự cắt theo Door/Window (các Family đó vẫn
/// host trên tường gốc, không host trên lớp tô mới tạo) — cần xử lý riêng nếu cần.</summary>
internal static class WallFinishBuilder
{
    /// <summary>Khoảng lùi vào từ mặt tường khi "dò" Ceiling gần đó theo phương ngang (XY) — đủ nhỏ để
    /// vẫn nằm trong phòng sát tường, đủ lớn để không rơi đúng biên Ceiling do sai số dựng hình.</summary>
    private static readonly double CeilingProbeInsetFeet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);

    /// <summary>Dung sai coi 1 đầu tường khác là "chạm" vào đường tim tường đang xử lý (khoảng cách
    /// vuông góc) — dùng cả khi loại trừ điểm chạm quá gần 2 đầu tường (đó là góc thường, không phải
    /// chữ T).</summary>
    private static readonly double TJunctionToleranceFeet = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters);

    /// <summary>Chặn an toàn cho GetMiteredCornerPoint: nếu điểm giao 2 đường tim lớp tô cách đầu tường
    /// thô quá xa (2 đường gần song song — "góc" thực ra gần 180°, không phải góc thật) thì bỏ qua,
    /// giữ đầu thô như cũ thay vì kéo dài lớp tô vô lý.</summary>
    private static readonly double MaxMiterExtensionFeet = UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters);

    internal sealed class Report
    {
        public int WallsScanned;
        public int WallsSkipped;
        public int LayersCreated;
        public int JoinFailed;
    }

    internal static Report Build(Document doc, IReadOnlyList<Wall> hostWalls, FinishLayerOptions options)
    {
        var report = new Report { WallsScanned = hostWalls.Count };

        var baseOffsetFeet = UnitUtils.ConvertToInternalUnits(options.BaseOffsetMm, UnitTypeId.Millimeters);
        var topOffsetFeet = UnitUtils.ConvertToInternalUnits(options.TopOffsetMm, UnitTypeId.Millimeters);
        var ceilingGapFeet = UnitUtils.ConvertToInternalUnits(options.CeilingGapMm, UnitTypeId.Millimeters);
        var coatingWidthFeet = options.CoatingType.Width;

        var ceilings = options.HeightMode == HeightMode.BelowCeiling
            ? new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>().ToList()
            : new List<Ceiling>();

        var createdWalls = new List<Wall>();

        // Cache hinh hoc (duong tim + 2 mat that) cho MOI tuong host co Location dang Line - dung chung
        // cho ca viec xu ly "ban than" 1 tuong LAN tra cuu hinh hoc tuong HANG XOM luc tim goc chu L
        // (GetMiteredCornerPoint) - tranh quet lai GetSideFaceOffset nhieu lan cho cung 1 tuong.
        var geometryCache = new Dictionary<ElementId, WallGeom>();
        foreach (var w in hostWalls)
        {
            if (w.Location is not LocationCurve wLocation || wLocation.Curve is not Line wLine)
                continue;

            var gp0 = wLine.GetEndPoint(0);
            var gp1 = wLine.GetEndPoint(1);
            var gDirection = (gp1 - gp0).Normalize();
            var gNormal = new XYZ(-gDirection.Y, gDirection.X, 0);
            var gCenterN = gp0.DotProduct(gNormal);
            var gExtOffsetN = GetSideFaceOffset(w, ShellLayerType.Exterior, gNormal) ?? gCenterN + w.Width / 2.0;
            var gIntOffsetN = GetSideFaceOffset(w, ShellLayerType.Interior, gNormal) ?? gCenterN - w.Width / 2.0;

            geometryCache[w.Id] = (gp0, gp1, gDirection, gNormal, gCenterN, gExtOffsetN, gIntOffsetN);
        }

        foreach (var wall in hostWalls)
        {
            if (wall.WallType.Kind != WallKind.Basic || wall.Location is not LocationCurve locationCurve)
            {
                report.WallsSkipped++;
                continue;
            }

            var bbox = wall.get_BoundingBox(null);
            if (bbox is null ||
                wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() is not { } baseLevelId ||
                doc.GetElement(baseLevelId) is not Level baseLevel)
            {
                report.WallsSkipped++;
                continue;
            }

            var hostTopZ = bbox.Max.Z;
            var coatingBottomZ = bbox.Min.Z + baseOffsetFeet;

            var created = 0;

            if (locationCurve.Curve is Line && geometryCache.TryGetValue(wall.Id, out var selfGeo))
            {
                created = CreateLineWallCoatings(
                    doc, wall, selfGeo, hostWalls, geometryCache, hostTopZ, coatingBottomZ, baseLevel,
                    options, ceilings, topOffsetFeet, ceilingGapFeet, coatingWidthFeet, report, createdWalls);
            }
            else if (locationCurve.Curve is Arc arc)
            {
                created = CreateArcWallCoatings(
                    doc, wall, arc, hostTopZ, coatingBottomZ, baseLevel,
                    options, ceilings, topOffsetFeet, ceilingGapFeet, coatingWidthFeet, report, createdWalls);
            }

            if (created > 0)
                report.LayersCreated += created;
            else
                report.WallsSkipped++;
        }

        JoinIntersectingCoatings(doc, createdWalls, report);

        return report;
    }

    /// <summary>Tạo 2 lớp tô (Exterior/Interior) cho 1 Wall host dạng Line — tách riêng khỏi Build() để
    /// dễ đọc, xử lý luôn tường chữ T (FindTJunctions) và mitre góc chữ L (FindCornerMate) — xem
    /// doc-comment class.</summary>
    private static int CreateLineWallCoatings(
        Document doc, Wall wall, WallGeom selfGeo, IReadOnlyList<Wall> hostWalls,
        Dictionary<ElementId, WallGeom> geometryCache, double hostTopZ, double coatingBottomZ, Level baseLevel,
        FinishLayerOptions options, IReadOnlyList<Ceiling> ceilings, double topOffsetFeet, double ceilingGapFeet,
        double coatingWidthFeet, Report report, List<Wall> createdWalls)
    {
        var (p0, p1, direction, normal, centerN, extOffsetN, intOffsetN) = selfGeo;
        var wallLength = p0.DistanceTo(p1);

        var tJunctions = FindTJunctions(wall, p0, p1, direction, normal, wallLength, hostWalls);
        var cornerMateAtP0 = FindCornerMate(wall, p0, geometryCache);
        var cornerMateAtP1 = FindCornerMate(wall, p1, geometryCache);

        var created = 0;
        var midpoint = (p0 + p1) / 2.0;

        for (var sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            var faceOffsetN = sideIndex == 0 ? extOffsetN : intOffsetN;
            var sign = Math.Sign(faceOffsetN - centerN);
            if (sign == 0)
                sign = 1;

            double coatingTopZ;
            if (options.HeightMode == HeightMode.MatchWallHeight)
            {
                coatingTopZ = hostTopZ + topOffsetFeet;
            }
            else
            {
                var ceilingTopZ = FindCeilingTopNear(ceilings, midpoint, normal, sign, wall.Width / 2.0);
                coatingTopZ = ceilingTopZ is { } z ? z + ceilingGapFeet : hostTopZ;
            }

            var coatingCenterN = faceOffsetN + sign * (coatingWidthFeet / 2.0);
            var offsetVec = normal.Multiply(coatingCenterN - centerN);

            // Goc chu L: trim/keo dai dau lien quan toi giao diem 2 duong tim lop to (xem
            // GetMiteredCornerPoint) thay vi dau tho cua tuong host - xem doc-comment class.
            var startPoint = p0 + offsetVec;
            if (cornerMateAtP0 is { } mateP0Id && geometryCache.TryGetValue(mateP0Id, out var mateP0Geo))
                startPoint = GetMiteredCornerPoint(startPoint, direction, sideIndex, coatingWidthFeet, mateP0Geo) ?? startPoint;

            var endPoint = p1 + offsetVec;
            if (cornerMateAtP1 is { } mateP1Id && geometryCache.TryGetValue(mateP1Id, out var mateP1Geo))
                endPoint = GetMiteredCornerPoint(endPoint, direction, sideIndex, coatingWidthFeet, mateP1Geo) ?? endPoint;

            // Tuong chu T: cac diem chu T dung PHIA nay (cung sign) tach doan lien tuc thanh nhieu
            // Wall lop to rieng, moi doan dung dung tai diem cham - xem doc-comment class. Khong co
            // diem chu T nao thi chi co dung 1 doan [0, wallLength] nhu truoc gio.
            var splitTs = tJunctions
                .Where(j => j.Sign == sign)
                .Select(j => j.T)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            var boundaries = new List<double> { 0.0 };
            boundaries.AddRange(splitTs);

            // Che do "Cao hon Ceiling gan do": tuong dai co the di qua NHIEU phong/vung tran khac cao
            // do khac nhau - tach doan tai moi diem tran "vao/ra" pham vi doc theo tuong (xem
            // FindCeilingBreakpoints), moi doan sau do tu do lai dung tran THAT cua rieng no (xem nhanh
            // else ben duoi) thay vi 1 cao do tran dong nhat cho ca tuong (gioi han cu, xem doc-comment
            // class) - cho phep 2 lop to cung 1 buc tuong cao khac nhau dung theo tran thuc te moi ben.
            if (options.HeightMode == HeightMode.BelowCeiling)
            {
                boundaries.AddRange(FindCeilingBreakpoints(ceilings, p0, direction, normal, sign, wallLength, wall.Width / 2.0));
            }

            boundaries.Add(wallLength);
            boundaries = boundaries.Distinct().OrderBy(t => t).ToList();

            // Chi dinh mai/Attach Top moi can do lai cao do THAT tung dau (xem GetHostTopZAt) - tuong
            // dinh phang (da so truong hop) khong can, cu dung coatingTopZ dong nhat nhu truoc gio.
            var probeMinZ = coatingBottomZ - UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
            var probeMaxZ = coatingTopZ + UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);

            double ResolveMatchWallTopZ(XYZ offsetPoint)
            {
                var t = (offsetPoint - p0).DotProduct(direction);
                var centerlinePoint = p0 + direction.Multiply(t);
                var probedZ = GetHostTopZAt(wall, centerlinePoint, probeMinZ, probeMaxZ);
                return probedZ is { } z ? z + topOffsetFeet : coatingTopZ;
            }

            for (var seg = 0; seg < boundaries.Count - 1; seg++)
            {
                var segStart = boundaries[seg];
                var segEnd = boundaries[seg + 1];
                if (segEnd - segStart < 1e-6)
                    continue;

                // Chi dau/cuoi cham DUNG dau tho cua tuong (segStart=0 hoac segEnd=wallLength) moi
                // dung diem da mitre - cac moc tach do diem chu T o giua than tuong khong lien quan
                // goc chu L, giu nguyen cong thuc tham so hoa binh thuong.
                var segP0 = segStart <= 1e-6 ? startPoint : p0 + direction.Multiply(segStart) + offsetVec;
                var segP1 = segEnd >= wallLength - 1e-6 ? endPoint : p0 + direction.Multiply(segEnd) + offsetVec;

                double topZAtP0, topZAtP1;
                if (options.HeightMode == HeightMode.MatchWallHeight)
                {
                    // Dinh co the nghieng lien tuc (mai doc) - do rieng tung dau, xem doc-comment class
                    // ve Attach Top.
                    topZAtP0 = ResolveMatchWallTopZ(segP0);
                    topZAtP1 = ResolveMatchWallTopZ(segP1);
                }
                else
                {
                    // Doan nay da nam TRON trong 1 vung tran duy nhat (nho FindCeilingBreakpoints tach o
                    // tren) - dinh LUON PHANG, do 1 LAN tai trung diem doan (tranh do dung ngay bien 2
                    // vung tran de sai so lam tron chon nham tran ben canh).
                    var segMidT = (segStart + segEnd) / 2.0;
                    var segMidpoint = p0 + direction.Multiply(segMidT);
                    var segCeilingTopZ = FindCeilingTopNear(ceilings, segMidpoint, normal, sign, wall.Width / 2.0);
                    var flatTopZ = segCeilingTopZ is { } z ? z + ceilingGapFeet : hostTopZ;
                    topZAtP0 = flatTopZ;
                    topZAtP1 = flatTopZ;
                }

                if (topZAtP0 - coatingBottomZ <= 1e-6 || topZAtP1 - coatingBottomZ <= 1e-6)
                    continue;

                try
                {
                    Wall newWall;

                    // Chenh lech duoi 1mm coi la dinh PHANG - giu nguyen cach dung cu (curve + height)
                    // de khong doi hanh vi/tranh regress cho da so tuong khong Attach Top vao Roof.
                    if (Math.Abs(topZAtP0 - topZAtP1) <= UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters))
                    {
                        var curve = Line.CreateBound(segP0, segP1);
                        newWall = Wall.Create(
                            doc, curve, options.CoatingType.Id, baseLevel.Id,
                            topZAtP0 - coatingBottomZ, coatingBottomZ - baseLevel.ProjectElevation, false, false);
                    }
                    else
                    {
                        // Dinh nghieng (tuong host Attach Top vao Roof, xem doc-comment class ve loi
                        // lop to du ra hinh chu thap tai dinh mai) - dung Wall.Create(profile) voi 4
                        // canh bam DUNG cao do that tung dau, thay vi 1 hinh chu nhat dinh phang cu.
                        var segBottomP0 = new XYZ(segP0.X, segP0.Y, coatingBottomZ);
                        var segBottomP1 = new XYZ(segP1.X, segP1.Y, coatingBottomZ);
                        var segTopP0 = new XYZ(segP0.X, segP0.Y, topZAtP0);
                        var segTopP1 = new XYZ(segP1.X, segP1.Y, topZAtP1);

                        var profile = new List<Curve>
                        {
                            Line.CreateBound(segBottomP0, segBottomP1),
                            Line.CreateBound(segBottomP1, segTopP1),
                            Line.CreateBound(segTopP1, segTopP0),
                            Line.CreateBound(segTopP0, segBottomP0)
                        };
                        newWall = Wall.Create(doc, profile, options.CoatingType.Id, baseLevel.Id, false);
                    }

                    doc.Regenerate();

                    try
                    {
                        JoinGeometryUtils.JoinGeometry(doc, wall, newWall);
                    }
                    catch
                    {
                        report.JoinFailed++;
                    }

                    createdWalls.Add(newWall);
                    created++;
                }
                catch
                {
                    // Bo qua rieng doan nay (vd curve suy bien do heightFeet/be rong bat thuong),
                    // van thu cac doan/mat con lai cua cung tuong.
                }
            }
        }

        return created;
    }

    /// <summary>Do cao do Z THAT cua mat tren tuong host (HostObjectUtils.GetTopFaces) tai vi tri
    /// (X,Y) cho truoc — dung tia thang dung (Face.Intersect voi 1 doan Line dung tu duoi len) de tu
    /// dong bam dung bien mat that (ke ca khi tuong Attach Top vao Roof khien mat tren nghieng/gay khuc
    /// theo tung mai). Tra ve null neu khong Attach Top/khong co Roof (mat tren luc do la 1 mat phang
    /// ngang binh thuong, hostTopZ dong nhat cu da du dung) hoac tia khong cham mat nao (vd diem dang
    /// xet nam ngoai bien mat, thuong do goc mitre keo dai qua dau tho tuong).</summary>
    private static double? GetHostTopZAt(Wall wall, XYZ xy, double probeMinZ, double probeMaxZ)
    {
        try
        {
            if (probeMaxZ - probeMinZ <= 1e-6)
                return null;

            var rayLine = Line.CreateBound(new XYZ(xy.X, xy.Y, probeMinZ), new XYZ(xy.X, xy.Y, probeMaxZ));

            foreach (var faceRef in HostObjectUtils.GetTopFaces(wall))
            {
                if (wall.GetGeometryObjectFromReference(faceRef) is not Face face)
                    continue;

                if (face.Intersect(rayLine, out var results) == SetComparisonResult.Overlap &&
                    results is { Size: > 0 })
                    return results.get_Item(0).XYZPoint.Z;
            }
        }
        catch
        {
            // fallback coatingTopZ dong nhat o noi goi
        }

        return null;
    }

    /// <summary>Tạo 2 lớp tô (Exterior/Interior) cho 1 Wall host dạng Arc — Arc ĐỒNG TÂM, cùng góc quét,
    /// chỉ đổi bán kính (xem doc-comment class). CHƯA hỗ trợ tách chữ T/mitre chữ L như tường thẳng.</summary>
    private static int CreateArcWallCoatings(
        Document doc, Wall wall, Arc arc, double hostTopZ, double coatingBottomZ, Level baseLevel,
        FinishLayerOptions options, IReadOnlyList<Ceiling> ceilings, double topOffsetFeet, double ceilingGapFeet,
        double coatingWidthFeet, Report report, List<Wall> createdWalls)
    {
        var center = arc.Center;
        var centerlineRadius = arc.Radius;
        var startAngle = arc.GetEndParameter(0);
        var endAngle = arc.GetEndParameter(1);
        var xDirection = arc.XDirection;
        var yDirection = arc.YDirection;

        var midpoint = arc.Evaluate(0.5, true);
        var radialDirection = (midpoint - center).Normalize();

        var extRadius = GetSideFaceRadius(wall, ShellLayerType.Exterior, center, midpoint) ?? centerlineRadius + wall.Width / 2.0;
        var intRadius = GetSideFaceRadius(wall, ShellLayerType.Interior, center, midpoint) ?? centerlineRadius - wall.Width / 2.0;

        var created = 0;

        foreach (var faceRadius in new[] { extRadius, intRadius })
        {
            var sign = Math.Sign(faceRadius - centerlineRadius);
            if (sign == 0)
                sign = 1;

            double coatingTopZ;
            if (options.HeightMode == HeightMode.MatchWallHeight)
            {
                coatingTopZ = hostTopZ + topOffsetFeet;
            }
            else
            {
                var ceilingTopZ = FindCeilingTopNear(ceilings, midpoint, radialDirection, sign, wall.Width / 2.0);
                coatingTopZ = ceilingTopZ is { } z ? z + ceilingGapFeet : hostTopZ;
            }

            var heightFeet = coatingTopZ - coatingBottomZ;
            if (heightFeet <= 1e-6)
                continue;

            var coatingRadius = faceRadius + sign * (coatingWidthFeet / 2.0);
            if (coatingRadius <= 1e-6)
                continue;

            try
            {
                var curve = Arc.Create(center, coatingRadius, startAngle, endAngle, xDirection, yDirection);
                var newWall = Wall.Create(
                    doc, curve, options.CoatingType.Id, baseLevel.Id,
                    heightFeet, coatingBottomZ - baseLevel.ProjectElevation, false, false);
                doc.Regenerate();

                try
                {
                    JoinGeometryUtils.JoinGeometry(doc, wall, newWall);
                }
                catch
                {
                    report.JoinFailed++;
                }

                createdWalls.Add(newWall);
                created++;
            }
            catch
            {
                // Bo qua rieng mat nay (vd ban kinh am/qua nho do coatingWidth lon hon ban kinh con lai).
            }
        }

        return created;
    }

    /// <summary>Sau khi tạo hết lớp tô, Join CHÉO giữa các lớp tô MỚI TẠO với nhau nếu giao nhau (vd 2
    /// tường vuông góc gặp góc, lớp tô 2 bên chạm/đè lên nhau tại góc đó) — chỉ thử Join cặp có bounding
    /// box (buffer nhỏ bù sai số) giao nhau, tránh gọi JoinGeometry thừa cho cặp ở xa nhau. Không Join
    /// lại lớp tô với host Wall ở đây (đã làm ngay lúc tạo, xem vòng lặp phía trên).</summary>
    private static void JoinIntersectingCoatings(Document doc, List<Wall> createdWalls, Report report)
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
                    report.JoinFailed++;
                }
            }
        }
    }

    private static bool BoundingBoxesOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b, double bufferFeet) =>
        a.Min.X - bufferFeet <= b.Max.X && b.Min.X - bufferFeet <= a.Max.X &&
        a.Min.Y - bufferFeet <= b.Max.Y && b.Min.Y - bufferFeet <= a.Max.Y &&
        a.Min.Z - bufferFeet <= b.Max.Z && b.Min.Z - bufferFeet <= a.Max.Z;

    /// <summary>Tìm các điểm "chữ T" trên đường tim tường <paramref name="wall"/>: 1 đầu của tường host
    /// KHÁC (trong cùng <paramref name="allHostWalls"/> — chỉ xét các tường đang được xử lý trong lượt
    /// chạy này) chạm vào GIỮA thân tường (không phải chạm 1 trong 2 đầu — đó là góc thường, Join bình
    /// thường xử lý tốt, KHÔNG cần tách). Trả về vị trí dọc tường (t tính từ p0 theo <paramref
    /// name="direction"/>, đơn vị feet) VÀ dấu (Sign) của phía tường bị nhánh kia "đứng vào" (so với
    /// <paramref name="normal"/>) — dùng để biết ĐÚNG mặt lớp tô nào (Exterior hay Interior của W) cần
    /// tách, mặt còn lại (đối diện nhánh) không bị ảnh hưởng.</summary>
    private static List<(double T, int Sign)> FindTJunctions(
        Wall wall, XYZ p0, XYZ p1, XYZ direction, XYZ normal, double wallLength, IReadOnlyList<Wall> allHostWalls)
    {
        var results = new List<(double T, int Sign)>();

        foreach (var other in allHostWalls)
        {
            if (other.Id == wall.Id || other.Location is not LocationCurve otherLocation ||
                otherLocation.Curve is not Line otherLine)
                continue;

            var otherP0 = otherLine.GetEndPoint(0);
            var otherP1 = otherLine.GetEndPoint(1);

            foreach (var (touchPoint, farPoint) in new[] { (otherP0, otherP1), (otherP1, otherP0) })
            {
                var t = (touchPoint - p0).DotProduct(direction);
                if (t < TJunctionToleranceFeet || t > wallLength - TJunctionToleranceFeet)
                    continue;

                var projected = p0 + direction.Multiply(t);
                if (touchPoint.DistanceTo(projected) > TJunctionToleranceFeet)
                    continue;

                var sign = Math.Sign((farPoint - touchPoint).DotProduct(normal));
                if (sign == 0)
                    continue;

                results.Add((t, sign));
            }
        }

        return results;
    }

    /// <summary>Tìm tường host KHÁC (trong <paramref name="geometryCache"/>) có 1 trong 2 đầu chạm ĐÚNG
    /// <paramref name="point"/> (1 trong 2 đầu tường đang xử lý) — tức góc chữ L 2 tường. Trả về null
    /// nếu KHÔNG có tường nào chạm, HOẶC có từ 2 tường trở lên cùng chạm (góc 3-4 hướng — quá phức tạp
    /// để mitre đúng cho v1, giữ đầu thô như cũ, an toàn hơn là đoán sai).</summary>
    private static ElementId? FindCornerMate(Wall wall, XYZ point, Dictionary<ElementId, WallGeom> geometryCache)
    {
        ElementId? found = null;

        foreach (var (id, geo) in geometryCache)
        {
            if (id == wall.Id)
                continue;

            var touches = geo.P0.DistanceTo(point) <= TJunctionToleranceFeet ||
                          geo.P1.DistanceTo(point) <= TJunctionToleranceFeet;
            if (!touches)
                continue;

            if (found is not null)
                return null;

            found = id;
        }

        return found;
    }

    /// <summary>Giao điểm giữa đường tim lớp tô của tường đang xử lý (đi qua <paramref
    /// name="selfLinePoint"/>, hướng <paramref name="selfDirection"/>) và đường tim lớp tô CÙNG PHÍA
    /// (<paramref name="sideIndex"/>: 0=Exterior, 1=Interior — xem doc-comment class về giả định ghép
    /// Exterior-Exterior/Interior-Interior) của tường hàng xóm — dùng làm điểm "miter" thay đầu thô, để
    /// 2 lớp tô khớp khít tại góc chữ L. Trả về null nếu 2 đường gần song song (không giao được) hoặc
    /// giao điểm quá xa (an toàn, xem MaxMiterExtensionFeet) — khi đó giữ nguyên đầu thô.</summary>
    private static XYZ? GetMiteredCornerPoint(
        XYZ selfLinePoint, XYZ selfDirection, int sideIndex, double coatingWidthFeet, WallGeom neighborGeo)
    {
        var neighborFaceOffsetN = sideIndex == 0 ? neighborGeo.ExtOffsetN : neighborGeo.IntOffsetN;
        var neighborSign = Math.Sign(neighborFaceOffsetN - neighborGeo.CenterN);
        if (neighborSign == 0)
            neighborSign = 1;

        var neighborCoatingCenterN = neighborFaceOffsetN + neighborSign * (coatingWidthFeet / 2.0);
        var neighborOffsetVec = neighborGeo.Normal.Multiply(neighborCoatingCenterN - neighborGeo.CenterN);
        var neighborLinePoint = neighborGeo.P0 + neighborOffsetVec;

        var intersection = IntersectLines(selfLinePoint, selfDirection, neighborLinePoint, neighborGeo.Direction);
        if (intersection is null || intersection.DistanceTo(selfLinePoint) > MaxMiterExtensionFeet)
            return null;

        return intersection;
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

    /// <summary>Toa do (chieu theo <paramref name="normal"/>) cua mat Exterior/Interior THAT cua tuong —
    /// dung Reference hinh hoc that (giong WallSnapper) thay vi gia dinh Width/2 tinh tu Location Curve,
    /// vi Location Curve co the la Finish Face chu khong phai Centerline. Tuong co lo mo (cua/cua so) cho
    /// nhieu Face roi rac cung 1 mat nhung TAT CA deu dong phang (cung 1 toa do N) nen chi can lay 1 cai
    /// bat ky.</summary>
    private static double? GetSideFaceOffset(Wall wall, ShellLayerType side, XYZ normal)
    {
        try
        {
            foreach (var faceRef in HostObjectUtils.GetSideFaces(wall, side))
            {
                if (wall.GetGeometryObjectFromReference(faceRef) is PlanarFace face)
                    return face.Origin.DotProduct(normal);
            }
        }
        catch
        {
            // fallback Width/2 o noi goi
        }

        return null;
    }

    /// <summary>Bán kính mặt Exterior/Interior THẬT của tường CONG (Arc) — analogii với GetSideFaceOffset
    /// ở tường thẳng. CylindricalFace không có property Radius trực tiếp dùng được ở đây
    /// (Face.get_Radius(int) trả về XYZ — hướng, không phải bán kính) — thay vào đó Project
    /// <paramref name="probePoint"/> (điểm chắc chắn nằm trong phạm vi góc quét của mặt, vd trung điểm
    /// Arc) lên mặt trụ để lấy 1 điểm THẬT nằm trên mặt, rồi đo khoảng cách (chỉ theo X/Y) từ
    /// <paramref name="center"/> — với mặt trụ, MỌI điểm trên mặt đều cách trục đúng 1 bán kính, nên
    /// điểm nào cũng cho kết quả đúng miễn Project thành công. Trả về null nếu không đọc được (rơi về
    /// fallback Width/2 ở nơi gọi).</summary>
    private static double? GetSideFaceRadius(Wall wall, ShellLayerType side, XYZ center, XYZ probePoint)
    {
        try
        {
            foreach (var faceRef in HostObjectUtils.GetSideFaces(wall, side))
            {
                if (wall.GetGeometryObjectFromReference(faceRef) is not CylindricalFace face)
                    continue;

                var projection = face.Project(probePoint);
                if (projection is null)
                    continue;

                var onFace = projection.XYZPoint;
                return new XYZ(center.X, center.Y, 0).DistanceTo(new XYZ(onFace.X, onFace.Y, 0));
            }
        }
        catch
        {
            // fallback Width/2 o noi goi
        }

        return null;
    }

    /// <summary>Dò gần đúng 1 điểm thăm dò ngay bên ngoài mặt tường (lùi vào <see cref="CeilingProbeInsetFeet"/>
    /// từ mặt, tại <paramref name="wallMidpoint"/> — điểm này do người gọi truyền vào, có thể là trung
    /// điểm CẢ tường hoặc trung điểm 1 ĐOẠN nhỏ hơn sau khi đã tách theo FindCeilingBreakpoints) rồi tìm
    /// Ceiling THẬT SỰ chứa điểm đó (xem IsPointInsideCeiling — kiểm tra chính xác theo biên hình học
    /// thật, KHÔNG chỉ bounding box) - nhiều Ceiling trùng thì lấy CAO ĐỘ MẶT TRÊN CAO NHẤT (an toàn nhất
    /// — lớp tô sẽ cao vượt qua khỏi MỌI Ceiling liên quan tại đó, không hụt cái nào).
    ///
    /// TẠI SAO KHÔNG CHỈ DÙNG BOUNDING BOX: 2 phòng cạnh nhau (2 Ceiling khác cao độ) mà 1 trong 2
    /// Ceiling có hình dạng không phải hình chữ nhật đơn giản (chữ L, có góc lõm...) thì bounding box của
    /// nó có thể LẤN SANG cả phần diện tích phòng bên cạnh dù hình dạng thật không chạm tới đó — probe
    /// dò ở mặt tường phía phòng thấp vẫn bị bounding box (quá khổ) của Ceiling phòng cao "nuốt" vào,
    /// rồi quy tắc "lấy cao độ cao nhất" khiến mặt lẽ ra phải thấp lại bị chọn nhầm sang cao — đúng lỗi
    /// "lớp tô bên phải chưa được hạ xuống" user báo cáo thực tế. Test chính xác theo Face thật loại bỏ
    /// hẳn trường hợp này.</summary>
    private static double? FindCeilingTopNear(
        IReadOnlyList<Ceiling> ceilings, XYZ wallMidpoint, XYZ normal, int sign, double hostHalfWidthFeet)
    {
        var probe = wallMidpoint + normal.Multiply(sign * (hostHalfWidthFeet + CeilingProbeInsetFeet));

        double? best = null;
        foreach (var ceiling in ceilings)
        {
            var bbox = ceiling.get_BoundingBox(null);
            if (bbox is null)
                continue;

            // Prefilter re bang bbox truoc (nhanh) - bbox luon BAO TRON hinh dang that nen khong bao gio
            // loai nham, chi giup bo qua som cac Ceiling ro rang khong lien quan truoc khi lam phep test
            // chinh xac (cham hon, doc Geometry that) o duoi.
            if (probe.X < bbox.Min.X || probe.X > bbox.Max.X || probe.Y < bbox.Min.Y || probe.Y > bbox.Max.Y)
                continue;

            if (!IsPointInsideCeiling(ceiling, probe))
                continue;

            var topZ = bbox.Max.Z;
            if (best is null || topZ > best.Value)
                best = topZ;
        }

        return best;
    }

    /// <summary>Kiểm tra CHÍNH XÁC <paramref name="point"/> (đã hạ về Z=0 khi so sánh) có nằm trong biên
    /// hình học thật của <paramref name="ceiling"/> hay không — lấy mặt trên (HostObjectUtils.GetTopFaces,
    /// giống Floor) rồi test điểm nằm trong biên NGOÀI của mặt đó và KHÔNG rơi vào bất kỳ lỗ khoét nào
    /// bên trong (giếng trời/thông tầng...) — xem IsPointInPolygonXY. Trả về false (KHÔNG coi là bên
    /// trong) nếu không đọc được hình học, an toàn hơn nhận bừa.</summary>
    private static bool IsPointInsideCeiling(Ceiling ceiling, XYZ point)
    {
        try
        {
            foreach (var faceRef in HostObjectUtils.GetTopFaces(ceiling))
            {
                if (ceiling.GetGeometryObjectFromReference(faceRef) is not Face face)
                    continue;

                if (IsPointInsideFaceXY(face, point))
                    return true;
            }
        }
        catch
        {
            // fallback false o cuoi ham - khong doc duoc hinh hoc thi coi nhu khong chua diem.
        }

        return false;
    }

    private static bool IsPointInsideFaceXY(Face face, XYZ point)
    {
        IList<CurveLoop> loops;
        try
        {
            loops = face.GetEdgesAsCurveLoops();
        }
        catch
        {
            return false;
        }

        if (loops.Count == 0)
            return false;

        var loopsWithArea = loops
            .Select(loop => TessellateXY(loop))
            .Where(pts => pts.Count >= 3)
            .Select(pts => (Points: pts, Area: Math.Abs(SignedAreaXY(pts))))
            .OrderByDescending(x => x.Area)
            .ToList();

        if (loopsWithArea.Count == 0)
            return false;

        if (!IsPointInPolygonXY(point, loopsWithArea[0].Points))
            return false;

        for (var i = 1; i < loopsWithArea.Count; i++)
        {
            if (IsPointInPolygonXY(point, loopsWithArea[i].Points))
                return false;
        }

        return true;
    }

    private static List<XYZ> TessellateXY(CurveLoop loop)
    {
        var points = new List<XYZ>();
        foreach (var curve in loop)
            points.AddRange(curve.Tessellate());
        return points;
    }

    private static double SignedAreaXY(List<XYZ> points)
    {
        double area = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area / 2.0;
    }

    private static bool IsPointInPolygonXY(XYZ point, List<XYZ> polygon)
    {
        var inside = false;
        var count = polygon.Count;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];

            if (pi.Y > point.Y != pj.Y > point.Y &&
                point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Tìm các mốc t (dọc theo <paramref name="direction"/>, tính từ <paramref name="p0"/>,
    /// giới hạn trong (0, wallLength)) nơi "điểm dò Ceiling" (probe — cùng công thức lùi vào
    /// <see cref="CeilingProbeInsetFeet"/> như FindCeilingTopNear, di chuyển dọc tường theo t) đi VÀO
    /// hoặc RA khỏi bounding box (XY) của TỪNG Ceiling — dùng để tách tường dài đi qua nhiều phòng/vùng
    /// trần khác cao độ thành nhiều đoạn, mỗi đoạn tự dò lại đúng Ceiling của riêng nó (xem
    /// CreateLineWallCoatings) thay vì 1 cao độ đồng nhất cho cả tường (giới hạn cũ). Với mỗi Ceiling,
    /// probe(t) di chuyển TUYẾN TÍNH theo t (toạ độ dò = điểm cố định + t·hướng tường), nên tập hợp t mà
    /// probe(t) nằm trong 1 bounding box (điều kiện X VÀ Y đều thoả) luôn là 1 KHOẢNG liên tục — tính
    /// khoảng đó bằng cách gộp giao của 2 khoảng con (theo X, theo Y), rồi cắt về (0, wallLength), lấy 2
    /// đầu mút làm mốc tách (bỏ qua Ceiling không giao đoạn tường nào cả).</summary>
    private static List<double> FindCeilingBreakpoints(
        IReadOnlyList<Ceiling> ceilings, XYZ p0, XYZ direction, XYZ normal, int sign, double wallLength,
        double hostHalfWidthFeet)
    {
        var breakpoints = new List<double>();
        var offset = normal.Multiply(sign * (hostHalfWidthFeet + CeilingProbeInsetFeet));
        var originX = p0.X + offset.X;
        var originY = p0.Y + offset.Y;
        var dx = direction.X;
        var dy = direction.Y;

        foreach (var ceiling in ceilings)
        {
            var bbox = ceiling.get_BoundingBox(null);
            if (bbox is null)
                continue;

            double tMin, tMax;
            if (Math.Abs(dx) > 1e-9)
            {
                var t1 = (bbox.Min.X - originX) / dx;
                var t2 = (bbox.Max.X - originX) / dx;
                tMin = Math.Min(t1, t2);
                tMax = Math.Max(t1, t2);
            }
            else if (originX < bbox.Min.X || originX > bbox.Max.X)
            {
                continue;
            }
            else
            {
                tMin = double.NegativeInfinity;
                tMax = double.PositiveInfinity;
            }

            if (Math.Abs(dy) > 1e-9)
            {
                var t1 = (bbox.Min.Y - originY) / dy;
                var t2 = (bbox.Max.Y - originY) / dy;
                tMin = Math.Max(tMin, Math.Min(t1, t2));
                tMax = Math.Min(tMax, Math.Max(t1, t2));
            }
            else if (originY < bbox.Min.Y || originY > bbox.Max.Y)
            {
                continue;
            }

            tMin = Math.Max(tMin, 0.0);
            tMax = Math.Min(tMax, wallLength);
            if (tMax - tMin < 1e-6)
                continue;

            breakpoints.Add(tMin);
            breakpoints.Add(tMax);
        }

        return breakpoints;
    }
}
