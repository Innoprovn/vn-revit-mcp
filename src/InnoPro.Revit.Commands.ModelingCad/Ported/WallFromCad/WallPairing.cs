using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFromCad;

/// <summary>Nhận diện tường từ nét CAD thô trong 1 layer — theo đúng thứ tự ưu tiên:
///   1. Bắt cặp 2 nét THẲNG song song (DetectLinePairs) — trường hợp phổ biến nhất (tường thẳng).
///   2. Bắt cặp 2 CUNG TRÒN đồng tâm (DetectArcPairs) — đoạn tường cong vẽ đủ 2 nét biên trong CAD.
///
/// Nét/cung KHÔNG bắt được cặp song song/đồng tâm hợp lệ ở bước 1/2 (ví dụ nét biên bị cắt ngắn/gãy
/// khúc cạnh cửa đi/cửa sổ, hoặc chỉ vẽ 1 bên tường) sẽ BỊ BỎ QUA HOÀN TOÀN — không vẽ Line, không tạo
/// Wall — chỉ đếm lại (UnresolvedLineCount/UnresolvedArcCount) để báo cho người dùng biết. KHÔNG còn
/// suy luận/"mượn" bề rộng từ đoạn tường gần nhất cho nét lẻ như bản trước — mỗi tường vẽ ra bắt buộc
/// phải đo được TRỰC TIẾP từ 2 nét CAD thật.
///
/// LOẠI MẶT ĐẦU TƯỜNG (end-cap, chỉ trong DetectLinePairs): 1 nét CAD ngắn nối vuông góc 2 đầu của 1
/// cặp bề rộng vừa xác định (bịt kín mặt đầu tường, kiểu vẽ 4 cạnh đủ cho 1 đoạn tường) KHÔNG được coi
/// là bề rộng của 1 tường khác — xem MarkEndCapConnectors. Nhận diện 1 nét là "mặt đầu" cần ĐỦ 3 điều
/// kiện CÙNG LÚC: (1) vuông góc với cặp vừa chấp nhận, (2) nối đúng khít 2 góc LIỀN KỀ của cặp đó (cả 2
/// đầu của nét đều khớp đúng 2 điểm góc, không chỉ chạm 1 điểm rồi chạy tiếp), (3) độ dài của nét đó
/// xấp xỉ ĐÚNG BẰNG bề rộng vừa đo được của cặp đó (sai số AlignmentToleranceFeet) — vì 1 mặt đầu thật
/// sự chỉ có thể dài đúng bằng khoảng cách giữa 2 biên nó bịt kín. Điều kiện (3) là mấu chốt để KHÔNG
/// loại nhầm 1 nét vuông góc là biên của 1 tường KHÁC ở xa: 1 tường độc lập thật sự gần như không bao
/// giờ có độ dài tình cờ khớp đúng bề rộng 1 tường khác VÀ đồng thời đầu kia cũng khớp đúng góc liền kề
/// còn lại — chỉ mặt đầu bịt kín thật mới thoả cả 3 điều kiện. Nét đã bị xác định là mặt đầu bị loại
/// HẲN khỏi vai trò bề rộng (không còn được dùng làm I/J của bất kỳ cặp nào nữa, kể cả ở chỗ khác).
///
/// CHIỀU DÀI ĐƯỜNG TIM (chỉ trong DetectLinePairs, xem chi tiết ở doc-comment DetectLinePairs):
///   - 2 nét phải chồng lấn (overlap) ÍT NHẤT 5% chiều dài nét ngắn hơn mới được coi là 1 cặp hợp lệ
///     (MinLineOverlapRatio — hạ từ 50% xuống 5% so với bản gốc, nhưng KHÔNG bỏ hẳn yêu cầu này).
///   - Đường tim của 1 cặp hợp lệ lấy đúng theo ĐỘ DÀI CỦA NÉT NGẮN HƠN trong cặp — KHÔNG còn kéo dài
///     theo "hợp" (union) của 2 nét như bản gốc.
///   - Sau khi có toàn bộ đường tim của layer, chạy thêm 1 bước hậu xử lý (ExtendWallsPastDoorLines,
///     xem doc-comment riêng của hàm): đo TRỰC TIẾP chiều dài của từng ký hiệu cửa trên layer cửa/cửa sổ
///     do người dùng chọn (BuildDoorLines gom nét/cung thành từng cụm 1 cửa; cụm CÓ cung xoay thì dùng
///     TÂM+BÁN KÍNH cung làm chiều dài/phương cửa — đáng tin cậy nhất, không phụ thuộc lá cửa vẽ đóng hay
///     mở; cụm KHÔNG có cung — vd cửa sổ — mới dùng cách dự phòng: cặp 2 đường thẳng song song cách xa
///     nhau nhất trong cụm), rồi với mỗi đầu Start/End của mọi WallCandidate, nếu tìm
///     thấy 1 "door line" song song với tường mà khoảng cách vuông góc tới trục tường &lt;=
///     DoorPerpendicularToleranceFeet (200mm, HẰNG SỐ CỐ ĐỊNH — KHÔNG còn dùng chung với "Khoảng bề rộng
///     nhận diện (mm)" của dialog, 2 mục đích khác nhau) VÀ khoảng cách dọc trục tới đầu tường đó nằm
///     trong [-3mm, maxFeet] (chạm khít hoặc có khe hở tới tối đa maxFeet — mục này VẪN dùng "Khoảng bề
///     rộng nhận diện" của dialog như cũ, xem TryMatchDoorLine) thì THAY THẾ/KÉO DÀI TRỰC TIẾP đầu đó của centerline gốc tới
///     hình chiếu của cạnh xa door line lên
///     trục tường — PHƯƠNG ÁN NÀY KHÁC bản trước: không còn "vẽ thêm" 1 centerline mới tách rời giữ
///     nguyên điểm ngắt tại cửa, mà gộp liền tường + khoảng hở + cửa thành 1 đường tim duy nhất.
///
/// Đây là thuật toán heuristic hình học đơn giản (không dùng AI/nhận diện ảnh). GIỚI HẠN ĐÃ BIẾT:
///   - 1 nét/cung KHÔNG bị giới hạn chỉ dùng cho 1 cặp (trừ nét đã bị xác định là mặt đầu tường, xem
///     trên) — có thể xuất hiện trong nhiều WallCandidate cùng lúc (vd 1 nét biên dùng chung giữa 2
///     tường liền kề). Hệ quả: nét CAD trùng/vẽ đè nhau có thể khiến nhiều cặp khác nhau đo ra nhiều
///     WallCandidate cùng vị trí/bề rộng — với đường THẲNG (Line), các candidate này được GỘP LẠI thành
///     1 (xem MergeOverlappingDuplicates); riêng CUNG TRÒN (Arc) chưa xử lý gộp trùng, người dùng vẫn
///     cần tự dọn nét cung trùng trong CAD trước khi quét nếu không muốn sinh nhiều tường cong gần trùng
///     nhau.
///   - Loại mặt đầu tường + chiều dài theo nét ngắn hơn + extend-để-giao-nhau CHỈ áp dụng cho
///     DetectLinePairs (tường thẳng) — DetectArcPairs (tường cong) vẫn giữ nguyên cơ chế union + ngưỡng
///     overlap tối thiểu 50% như bản gốc.
///   - Ellipse/spline... vẫn ngoài phạm vi (xem CadWallScanner) — chỉ Line/PolyLine/Arc được xử lý.</summary>
internal static class WallPairing
{
    private const double AngleToleranceDegrees = 2.0;
    private const double MinOverlapRatio = 0.5;
    private const double MinLineOverlapRatio = 0.05;
    private static readonly double AlignmentToleranceFeet =
        UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Millimeters);
    private static readonly double DoorTouchToleranceFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    /// <summary>Khoảng cách VUÔNG GÓC tối đa từ door line tới trục tim tường để còn coi là "cùng 1
    /// tường" (xem TryMatchDoorLine) — HẰNG SỐ CỐ ĐỊNH 200mm, KHÔNG còn dùng chung với "Khoảng bề rộng
    /// nhận diện (mm)" người dùng nhập ở dialog (maxFeet, vốn dùng để bắt cặp 2 nét CAD song song thành
    /// bề rộng tường — 2 mục đích khác nhau, không nên phụ thuộc lẫn nhau: đổi khoảng nhận diện bề rộng
    /// tường không nên vô tình ảnh hưởng tới việc ghép cửa). Khoảng cách DỌC TRỤC (khe hở giữa đầu tường
    /// và cạnh gần door line) vẫn dùng maxFeet như cũ — không đổi, chỉ đổi riêng điều kiện vuông góc
    /// này.</summary>
    private static readonly double DoorPerpendicularToleranceFeet =
        UnitUtils.ConvertToInternalUnits(200.0, UnitTypeId.Millimeters);
    private static readonly double MinSegmentLengthFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    public static WallDetectionResult Detect(
        IReadOnlyList<RawSegment> lines, IReadOnlyList<RawArc> arcs,
        double minThicknessMm, double maxThicknessMm,
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs)
    {
        var minFeet = UnitUtils.ConvertToInternalUnits(minThicknessMm, UnitTypeId.Millimeters);
        var maxFeet = UnitUtils.ConvertToInternalUnits(maxThicknessMm, UnitTypeId.Millimeters);
        var (lineCandidates, usedLines) = DetectLinePairs(
            lines, minFeet, maxFeet, doorSegments, doorArcs);
        var (arcCandidates, usedArcs) = DetectArcPairs(arcs, minFeet, maxFeet);
        var candidates = new List<WallCandidate>(lineCandidates.Count + arcCandidates.Count);
        candidates.AddRange(lineCandidates);
        candidates.AddRange(arcCandidates);
        return new WallDetectionResult(
            candidates, usedLines.Count(u => !u), usedArcs.Count(u => !u));
    }

    /// <summary>Lọc thô O(n) (thay vì so MỌI cặp O(n²)) ra danh sách cặp (I&lt;J) có Bounding Box giao
    /// nhau — dùng chung cho DetectLinePairs/MergeOverlappingDuplicates, 2 chỗ vốn phải so mọi cặp
    /// nét/candidate trong 1 layer, với layer vài nghìn nét sẽ rất chậm nếu không lọc trước bằng vị trí.
    /// Gom từng phần tử vào MỌI ô lưới (kích thước cellSizeFeet) mà Bounding Box của nó (đã tự bao thêm
    /// biên an toàn qua <paramref name="boundsOf"/>) phủ tới, rồi chỉ ghép cặp các phần tử CÙNG Ở CHUNG
    /// ít nhất 1 ô — không bỏ sót cặp hợp lệ nào vì mọi cặp hợp lệ thực sự bắt buộc phải có Bounding Box
    /// (đã bao biên đúng bằng khoảng cách tối đa cho phép) giao nhau ở đâu đó.</summary>
    private static List<(int I, int J)> BuildCandidatePairsByBoundingBox(
        int count, Func<int, (double MinX, double MinY, double MaxX, double MaxY)> boundsOf,
        double cellSizeFeet)
    {
        var grid = new Dictionary<(int, int), List<int>>();

        for (var index = 0; index < count; index++)
        {
            var (minX, minY, maxX, maxY) = boundsOf(index);
            var cellMinX = (int)Math.Floor(minX / cellSizeFeet);
            var cellMaxX = (int)Math.Floor(maxX / cellSizeFeet);
            var cellMinY = (int)Math.Floor(minY / cellSizeFeet);
            var cellMaxY = (int)Math.Floor(maxY / cellSizeFeet);

            for (var cx = cellMinX; cx <= cellMaxX; cx++)
            for (var cy = cellMinY; cy <= cellMaxY; cy++)
            {
                var key = (cx, cy);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }
                list.Add(index);
            }
        }

        var pairs = new HashSet<(int, int)>();
        foreach (var cellItems in grid.Values)
        {
            for (var a = 0; a < cellItems.Count; a++)
            for (var b = a + 1; b < cellItems.Count; b++)
            {
                var i = Math.Min(cellItems[a], cellItems[b]);
                var j = Math.Max(cellItems[a], cellItems[b]);
                pairs.Add((i, j));
            }
        }

        return pairs.ToList();
    }

    private static (List<WallCandidate> Candidates, bool[] Used) DetectLinePairs(
        IReadOnlyList<RawSegment> segments, double minFeet, double maxFeet,
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs)
    {
        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);
        var pairs = new List<(int I, int J, double Distance, double BStart, double BEnd)>();

        // Chi so sanh cac cap net co Bounding Box (da bao them maxFeet moi phia) giao nhau, thay vi MOI
        // cap net trong layer (O(n^2) that su cham voi layer vai nghin net - dung chinh nguyen nhan
        // "chay hoi lau") - 2 net chi co the tao thanh 1 cap hop le neu khoang cach vuong goc <= maxFeet,
        // nen 2 net cach nhau qua xa chac chan khong bao gio la 1 cap, khong can tinh dot product/overlap
        // lam gi cho chung. Khong bo sot cap nao: bat ky cap hop le nao cung chac chan co bounding box
        // (da bao maxFeet) giao nhau, vi khoang cach vuong goc giua chung <= maxFeet.
        var candidatePairs = BuildCandidatePairsByBoundingBox(
            segments.Count,
            index =>
            {
                var s = segments[index];
                return (
                    Math.Min(s.P0.X, s.P1.X) - maxFeet, Math.Min(s.P0.Y, s.P1.Y) - maxFeet,
                    Math.Max(s.P0.X, s.P1.X) + maxFeet, Math.Max(s.P0.Y, s.P1.Y) + maxFeet);
            },
            Math.Max(maxFeet, MinSegmentLengthFeet));

        foreach (var (i, j) in candidatePairs)
        {
            var a = segments[i];
            var b = segments[j];
            if (Math.Abs(a.Direction.DotProduct(b.Direction)) < cosTolerance)
                continue;
            var normal = new XYZ(-a.Direction.Y, a.Direction.X, 0);
            var distanceStart = Math.Abs((b.P0 - a.P0).DotProduct(normal));
            var distanceEnd = Math.Abs((b.P1 - a.P0).DotProduct(normal));
            var distance = (distanceStart + distanceEnd) / 2.0;
            if (distance < minFeet || distance > maxFeet ||
                Math.Abs(distanceStart - distanceEnd) > AlignmentToleranceFeet)
                continue;
            var bP0Param = (b.P0 - a.P0).DotProduct(a.Direction);
            var bP1Param = (b.P1 - a.P0).DotProduct(a.Direction);
            var bStart = Math.Min(bP0Param, bP1Param);
            var bEnd = Math.Max(bP0Param, bP1Param);
            var overlap = Math.Min(a.Length, bEnd) - Math.Max(0.0, bStart);
            if (overlap <= 0 || overlap < Math.Min(a.Length, b.Length) * MinLineOverlapRatio)
                continue;
            pairs.Add((i, j, distance, bStart, bEnd));
        }

        var used = new bool[segments.Count];
        var excludedAsWidth = new bool[segments.Count];
        var candidates = new List<WallCandidate>();
        foreach (var pair in pairs.OrderBy(p => p.Distance))
        {
            if (excludedAsWidth[pair.I] || excludedAsWidth[pair.J])
                continue;
            var a = segments[pair.I];
            var b = segments[pair.J];
            var spanStart = a.Length <= b.Length ? 0.0 : pair.BStart;
            var spanEnd = a.Length <= b.Length ? a.Length : pair.BEnd;
            var aP0 = a.P0 + a.Direction.Multiply(spanStart);
            var aP1 = a.P0 + a.Direction.Multiply(spanEnd);
            var normal = new XYZ(-a.Direction.Y, a.Direction.X, 0);
            var side = (b.P0 - a.P0).DotProduct(normal) >= 0 ? 1.0 : -1.0;
            var offset = normal.Multiply(side * pair.Distance / 2.0);
            var cP0 = aP0 + offset;
            var cP1 = aP1 + offset;
            if (cP0.DistanceTo(cP1) < MinSegmentLengthFeet)
                continue;
            used[pair.I] = true;
            used[pair.J] = true;
            candidates.Add(new WallCandidate
            {
                Centerline = Line.CreateBound(cP0, cP1),
                ThicknessMm = Math.Round(UnitUtils.ConvertFromInternalUnits(
                    pair.Distance, UnitTypeId.Millimeters), MidpointRounding.AwayFromZero),
                LengthFeet = spanEnd - spanStart
            });
            MarkEndCapConnectors(segments, pair.I, pair.J, a.Direction, pair.Distance,
                aP0, aP1, aP0 + offset.Multiply(2.0), aP1 + offset.Multiply(2.0), excludedAsWidth);
        }

        var deduped = MergeOverlappingDuplicates(candidates, doorSegments, doorArcs);
        var extended = ExtendWallsPastDoorLines(deduped, doorSegments, doorArcs, maxFeet);
        // Chay gop lan 2: ExtendWallsPastDoorLines co the doi dau mut cua nhieu WallCandidate tien sat/
        // chong len nhau (vd 2 tuong 2 phia 1 cua cung duoc keo dai qua cua) - lan gop dau tien (truoc
        // khi keo dai) khong biet truoc dieu nay, phai gop lai lan nua tren tap da keo dai xong.
        var merged = MergeOverlappingDuplicates(extended, doorSegments, doorArcs);
        return (merged, used);
    }

    /// <summary>PHƯƠNG ÁN THAY THẾ cho việc xử lý tường qua cửa — không còn "vẽ thêm" 1 centerline MỚI
    /// tách rời, mà THAY THẾ/KÉO DÀI TRỰC TIẾP centerline gốc: (1) gom nét/cung của layer cửa thành từng
    /// "cụm" 1 cửa (BuildDoorLines — các nét/cung chạm nhau trong DoorTouchToleranceFeet coi là cùng 1
    /// cửa), quy mỗi cụm về 1 "door line" ảo = đoạn nối 2 điểm XA NHAU NHẤT trong cụm (độ dài = khoảng
    /// cách dài nhất của cụm trên mặt bằng); (2) với mỗi đầu Start/End của mọi WallCandidate, tìm 1 door
    /// line SONG SONG với tường mà ĐỒNG THỜI thoả 2 điều kiện (xem TryMatchDoorLine) — MỖI điều kiện dùng
    /// 1 ngưỡng RIÊNG: (a) khoảng cách vuông góc từ door line tới trục tường &lt;=
    /// DoorPerpendicularToleranceFeet (200mm CỐ ĐỊNH, không phụ thuộc "Khoảng bề rộng nhận diện" của
    /// dialog — KHÔNG có cận dưới, door thường nằm sát/ngay trên trục tường), (b) khoảng cách dọc trục từ
    /// đầu tường tới cạnh GẦN của door line nằm trong khoảng [-AlignmentToleranceFeet,
    /// <paramref name="maxFeet"/>] (vẫn dùng đúng "Khoảng bề rộng nhận diện (mm)" của dialog như cũ) —
    /// cho phép chạm khít (khoảng cách ~0, trường hợp phổ biến nhất) hoặc có khe hở tới tối đa maxFeet,
    /// KHÔNG bắt buộc khe hở tối thiểu; (3) khớp được thì ĐỔI LUÔN đầu đó của centerline gốc tới vị trí
    /// hình chiếu của cạnh XA của door line lên đúng trục tường — tức tường được kéo dài/thay thế để phủ
    /// hết khoảng hở (nếu có) + cả bề rộng cửa, không còn giữ nguyên điểm ngắt tại cửa như phương án "vẽ
    /// thêm" trước đó.
    /// Ví dụ: Line tim (0,100)-(300,100), Line cửa (350,150)-(500,150) — khoảng cách vuông góc
    /// |150-100|=50 (&lt;=200mm CỐ ĐỊNH), khoảng cách dọc trục |350-300|=50 (trong [-3mm, maxFeet]) →
    /// khớp, tường mới thay thế tường cũ thành (0,100)-(500,100).</summary>
    private static List<WallCandidate> ExtendWallsPastDoorLines(
        List<WallCandidate> candidates,
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs,
        double maxFeet)
    {
        var doorLines = BuildDoorLines(doorSegments, doorArcs);
        if (doorLines.Count == 0)
            return candidates;

        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);
        var result = new List<WallCandidate>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (candidate.Centerline is not Line line)
            {
                result.Add(candidate);
                continue;
            }

            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var direction = (p1 - p0).Normalize();
            var changed = false;

            var newP0 = p0;
            if (TryMatchDoorLine(
                    line, direction, p0, direction.Multiply(-1.0), doorLines, maxFeet, cosTolerance,
                    out var extendedP0))
            {
                newP0 = extendedP0;
                changed = true;
            }

            var newP1 = p1;
            if (TryMatchDoorLine(
                    line, direction, p1, direction, doorLines, maxFeet, cosTolerance,
                    out var extendedP1))
            {
                newP1 = extendedP1;
                changed = true;
            }

            if (!changed)
            {
                result.Add(candidate);
                continue;
            }

            result.Add(new WallCandidate
            {
                Centerline = Line.CreateBound(newP0, newP1),
                ThicknessMm = candidate.ThicknessMm,
                LengthFeet = newP0.DistanceTo(newP1)
            });
        }

        return result;
    }

    /// <summary>Gom nét/cung của layer cửa (0 hoặc nhiều layer người dùng chọn) thành từng cụm 1 cửa —
    /// 2 nét/cung "cùng 1 cửa" nếu có ÍT NHẤT 1 cặp điểm định nghĩa (đầu mút nét, hoặc P0/P1/Mid của
    /// cung) cách nhau &lt;= DoorTouchToleranceFeet (gộp bắc cầu qua Union-Find, giống MergeOverlappingDuplicates).
    /// MỖI CỤM đo theo 1 trong 2 cách, ƯU TIÊN cách (1):
    ///   (1) Cụm CÓ ít nhất 1 CUNG XOAY (trường hợp phổ biến nhất — cửa đi luôn có cung thể hiện đường đi
    ///       của lá cửa): với MỖI cung trong cụm, tạo 2 "door line" ảo — từ TÂM cung tới P0, và từ TÂM
    ///       cung tới P1 — cả 2 dài đúng bằng BÁN KÍNH cung (chính là bề rộng cửa theo đúng chuẩn vẽ CAD,
    ///       KHÔNG phụ thuộc lá cửa đang vẽ ở trạng thái đóng hay mở). Đưa CẢ 2 vào danh sách: đúng chuẩn
    ///       vẽ, lá cửa lúc ĐÓNG luôn nằm khít trong mặt phẳng tường thật, nên 1 trong 2 đoạn này chắc
    ///       chắn thẳng hàng với tường — bước kiểm tra song song sẵn có ở TryMatchDoorLine sẽ tự loại
    ///       đoạn còn lại (chân cung ứng với lá cửa lúc MỞ, vuông góc với tường), không cần biết trước
    ///       đoạn nào đúng. ĐÁNG TIN CẬY HƠN cách (2) vì không phụ thuộc việc đoán "2 nét nào là đúng cặp".
    ///   (2) Cụm KHÔNG có cung nào (vd cửa sổ/cửa lùa — không vẽ cung xoay): dùng lại cách cũ — quy về 1
    ///       "door line" ảo theo ĐÚNG 2 ĐƯỜNG THẲNG song song với nhau và CÁCH NHAU XA NHẤT trong cụm
    ///       (lọc song song trong AngleToleranceDegrees, lấy cặp có khoảng cách vuông góc lớn nhất) — độ
    ///       dài = khoảng cách vuông góc đó, vị trí/Center = trung điểm nét NGẮN HƠN trong cặp, phương =
    ///       vector vuông góc với cặp nét đó. Cụm không đủ 2 nét thẳng song song hợp lệ thì bỏ qua.
    /// Khai internal (không phải private) để WallDebugDrawer gọi lại được, vẽ debug các door line đã
    /// nhận diện lên view — giúp kiểm tra bằng mắt xem tool có đo đúng cửa hay không trước khi tin vào
    /// kết quả kéo dài tường.</summary>
    internal static List<(XYZ P0, XYZ P1, XYZ Center, XYZ Direction, double Length)> BuildDoorLines(
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs)
    {
        var items = new List<XYZ[]>();
        var segmentIndexOfItem = new List<int>();
        var arcIndexOfItem = new List<int>();
        foreach (var index in Enumerable.Range(0, doorSegments.Count))
        {
            items.Add(new[] { doorSegments[index].P0, doorSegments[index].P1 });
            segmentIndexOfItem.Add(index);
            arcIndexOfItem.Add(-1);
        }
        foreach (var index in Enumerable.Range(0, doorArcs.Count))
        {
            items.Add(new[] { doorArcs[index].P0, doorArcs[index].P1, doorArcs[index].Mid });
            segmentIndexOfItem.Add(-1);
            arcIndexOfItem.Add(index);
        }

        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);

        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
        {
            var touching = false;
            foreach (var pointA in items[i])
            foreach (var pointB in items[j])
            {
                if (pointA.DistanceTo(pointB) <= DoorTouchToleranceFeet)
                {
                    touching = true;
                    break;
                }
            }

            if (!touching)
                continue;

            var rootI = Find(i);
            var rootJ = Find(j);
            if (rootI != rootJ)
                parent[rootI] = rootJ;
        }

        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);
        var doorLines = new List<(XYZ P0, XYZ P1, XYZ Center, XYZ Direction, double Length)>();

        foreach (var group in Enumerable.Range(0, items.Count).GroupBy(Find))
        {
            var groupIndices = group.ToList();
            var arcs = groupIndices
                .Select(index => arcIndexOfItem[index])
                .Where(arcIndex => arcIndex >= 0)
                .Select(arcIndex => doorArcs[arcIndex])
                .ToList();

            if (arcs.Count > 0)
            {
                foreach (var arc in arcs)
                {
                    if (arc.Radius < MinSegmentLengthFeet)
                        continue;

                    foreach (var endpoint in new[] { arc.P0, arc.P1 })
                    {
                        var legDirection = (endpoint - arc.Center).Normalize();
                        doorLines.Add((arc.Center, endpoint, arc.Center, legDirection, arc.Radius));
                    }
                }

                continue;
            }

            var segments = groupIndices
                .Select(index => segmentIndexOfItem[index])
                .Where(segmentIndex => segmentIndex >= 0)
                .Select(segmentIndex => doorSegments[segmentIndex])
                .ToList();
            if (segments.Count < 2)
                continue;

            var bestDistance = -1.0;
            RawSegment bestA = segments[0];
            RawSegment bestB = segments[0];
            for (var i = 0; i < segments.Count; i++)
            for (var j = i + 1; j < segments.Count; j++)
            {
                var a = segments[i];
                var b = segments[j];
                if (Math.Abs(a.Direction.DotProduct(b.Direction)) < cosTolerance)
                    continue;

                var normal = new XYZ(-a.Direction.Y, a.Direction.X, 0);
                var distance = Math.Abs((b.P0 - a.P0).DotProduct(normal));
                if (distance <= bestDistance)
                    continue;

                bestDistance = distance;
                bestA = a;
                bestB = b;
            }

            if (bestDistance < MinSegmentLengthFeet)
                continue;

            var shorter = bestA.Length <= bestB.Length ? bestA : bestB;
            var longer = bestA.Length <= bestB.Length ? bestB : bestA;
            var anchor = (shorter.P0 + shorter.P1) / 2.0;
            var otherCenter = (longer.P0 + longer.P1) / 2.0;
            var normalDirection = new XYZ(-shorter.Direction.Y, shorter.Direction.X, 0);
            var signedOffset = (otherCenter - anchor).DotProduct(normalDirection);
            var direction = signedOffset >= 0 ? normalDirection : normalDirection.Multiply(-1.0);
            var length = Math.Abs(signedOffset);
            if (length < MinSegmentLengthFeet)
                continue;

            doorLines.Add((anchor, anchor + direction.Multiply(length), anchor, direction, length));
        }

        return doorLines;
    }

    /// <summary>Tìm door line SONG SONG với tường (<paramref name="wallDirection"/>, sai số góc chuẩn
    /// AngleToleranceDegrees) VÀ ĐỒNG THỜI thoả cả 2 điều kiện — 2 điều kiện dùng 2 ngưỡng KHÁC NHAU,
    /// không còn dùng chung 1 khoảng như bản trước: (a) khoảng cách VUÔNG GÓC từ tâm door line tới trục
    /// tường phải &lt;= DoorPerpendicularToleranceFeet (200mm cố định, KHÔNG phụ thuộc "Khoảng bề rộng
    /// nhận diện" của dialog — đổi khoảng nhận diện bề rộng tường không nên ảnh hưởng tới việc ghép cửa);
    /// (b) khoảng cách DỌC TRỤC (chiếu theo <paramref name="outwardDirection"/>) từ
    /// <paramref name="wallPoint"/> tới cạnh GẦN của door line phải nằm trong [-AlignmentToleranceFeet
    /// (3mm), <paramref name="maxFeet"/>] — mục này VẪN dùng đúng "Khoảng bề rộng nhận diện (mm)" của
    /// dialog như cũ (cho phép có khe hở giữa tường và cửa tới tối đa maxFeet, hoặc chồng lấn nhẹ tới
    /// 3mm). Nhiều door line cùng khớp thì lấy door line có khoảng cách dọc trục tới cạnh gần NHỎ NHẤT.
    /// Khớp được thì <paramref name="newEndpoint"/> = hình chiếu của cạnh XA door line lên đúng trục
    /// tường (giữ nguyên trục/Y của tường, chỉ đổi toạ độ dọc trục) — đây là điểm THAY THẾ cho đầu
    /// <paramref name="wallPoint"/> của centerline gốc.</summary>
    private static bool TryMatchDoorLine(
        Line wallLine, XYZ wallDirection, XYZ wallPoint, XYZ outwardDirection,
        List<(XYZ P0, XYZ P1, XYZ Center, XYZ Direction, double Length)> doorLines,
        double maxFeet, double cosTolerance, out XYZ newEndpoint)
    {
        var wallOrigin = wallLine.GetEndPoint(0);
        var normal = new XYZ(-wallDirection.Y, wallDirection.X, 0);

        var bestNearGap = double.MaxValue;
        newEndpoint = wallPoint;
        var found = false;

        foreach (var door in doorLines)
        {
            if (Math.Abs(door.Direction.DotProduct(wallDirection)) < cosTolerance)
                continue;

            var perpDistance = Math.Abs((door.Center - wallOrigin).DotProduct(normal));
            if (perpDistance > DoorPerpendicularToleranceFeet)
                continue;

            var tP0 = (door.P0 - wallPoint).DotProduct(outwardDirection);
            var tP1 = (door.P1 - wallPoint).DotProduct(outwardDirection);
            var nearT = Math.Min(tP0, tP1);
            var farT = Math.Max(tP0, tP1);

            if (nearT < -AlignmentToleranceFeet || nearT > maxFeet)
                continue;
            if (nearT >= bestNearGap)
                continue;

            bestNearGap = nearT;
            newEndpoint = wallOrigin + wallDirection.Multiply(
                (wallPoint + outwardDirection.Multiply(farT) - wallOrigin).DotProduct(wallDirection));
            found = true;
        }

        return found;
    }

    /// <summary>Ngay sau khi 1 cặp được chấp nhận (đã "chốt" là bề rộng), đánh dấu MỌI nét khác trong
    /// layer là "mặt đầu tường" — loại hẳn khỏi vai trò bề rộng (excludedAsWidth) — nếu ĐỒNG THỜI thoả
    /// cả 3 điều kiện: (1) vuông góc với <paramref name="direction"/>, (2) nối đúng khít 1 trong 2 cặp
    /// góc liền kề của cặp vừa chấp nhận (<paramref name="cornerA0"/>/<paramref name="cornerB0"/> ở 1
    /// đầu, <paramref name="cornerA1"/>/<paramref name="cornerB1"/> ở đầu còn lại — xem ConnectsCorners),
    /// (3) độ dài xấp xỉ ĐÚNG BẰNG <paramref name="thicknessFeet"/> (bề rộng vừa đo được của cặp đó, sai
    /// số AlignmentToleranceFeet). Điều kiện (3) là mấu chốt: khác bản trước (chỉ yêu cầu độ dài nằm
    /// trong khoảng min/max CHUNG của người dùng — quá lỏng, loại nhầm cả biên thật của 1 tường khác ở
    /// xa), yêu cầu khớp ĐÚNG bề rộng CỤ THỂ của cặp đang xét gần như chỉ đúng với 1 mặt đầu bịt kín
    /// thật — 1 tường độc lập thật sự không bao giờ tình cờ vừa dài đúng bằng vừa khớp đúng góc.</summary>
    private static void MarkEndCapConnectors(
        IReadOnlyList<RawSegment> segments, int indexA, int indexB, XYZ direction, double thicknessFeet,
        XYZ cornerA0, XYZ cornerA1, XYZ cornerB0, XYZ cornerB1, bool[] excludedAsWidth)
    {
        for (var k = 0; k < segments.Count; k++)
        {
            if (k == indexA || k == indexB || excludedAsWidth[k])
                continue;

            var candidate = segments[k];

            if (Math.Abs(candidate.Length - thicknessFeet) > AlignmentToleranceFeet)
                continue;

            if (!IsPerpendicular(candidate.Direction, direction))
                continue;

            if (ConnectsCorners(candidate, cornerA0, cornerB0) || ConnectsCorners(candidate, cornerA1, cornerB1))
                excludedAsWidth[k] = true;
        }
    }

    /// <summary>Gộp các candidate "cùng 1 đoạn tường" (đường tim trùng phương — cùng hướng, lệch vuông
    /// góc &lt;= AlignmentToleranceFeet — CÙNG bề rộng ThicknessMm) thành 1 candidate DUY NHẤT có đường
    /// tim PHỦ TOÀN BỘ chiều dài của cả nhóm (từ điểm xa nhất bên này tới điểm xa nhất bên kia) — áp
    /// dụng cho CẢ 2 trường hợp (xem IsSameWallRun): (1) 2 candidate CHỒNG LẤN lên nhau (nét biên CAD vẽ
    /// ngắt quãng khiến nhiều cặp khác nhau cùng đo ra gần đúng 1 đường tim — mục 7 ở DetectLinePairs);
    /// (2) 2 candidate CHẠM/CÁCH NHAU 1 khoảng rất nhỏ (&lt;= AlignmentToleranceFeet, vd polyline tường bị
    /// tách thành nhiều đoạn rời nhưng vẫn nối liền nhau) — KHÔNG áp dụng nếu điểm nối đó lại là 1 điểm
    /// tường bị CẮT BỞI CỬA (xem TouchesDoorLayer): trường hợp đó phải giữ 2 centerline tách rời để còn
    /// cắt tường/chèn cửa đúng chỗ, đừng gộp nhầm trở lại thành 1 đường tim liền mạch. KHÔNG còn bỏ các
    /// candidate ngắn hơn như bản trước, vì mỗi candidate ngắn có thể phủ 1 đoạn mà candidate dài nhất
    /// trong nhóm không phủ tới. Gộp bắc cầu qua Union-Find: A nối B, B nối C (dù A không trực tiếp nối
    /// C) vẫn được coi là 1 nhóm.</summary>
    private static List<WallCandidate> MergeOverlappingDuplicates(
        List<WallCandidate> candidates,
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs)
    {
        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);

        var lines = candidates.Where(c => c.Centerline is Line).ToList();
        var others = candidates.Where(c => c.Centerline is not Line).ToList();

        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);

        // Loc tho bang Bounding Box (bao them AlignmentToleranceFeet moi phia) truoc khi goi
        // IsSameWallRun - tuong tu DetectLinePairs, tranh O(n^2) that su tren toan bo candidate (co the
        // len toi ca nghin) khi da qua vong loc pairing dau tien.
        var candidatePairs = BuildCandidatePairsByBoundingBox(
            lines.Count,
            index =>
            {
                var line = (Line)lines[index].Centerline;
                var p0 = line.GetEndPoint(0);
                var p1 = line.GetEndPoint(1);
                return (
                    Math.Min(p0.X, p1.X) - AlignmentToleranceFeet, Math.Min(p0.Y, p1.Y) - AlignmentToleranceFeet,
                    Math.Max(p0.X, p1.X) + AlignmentToleranceFeet, Math.Max(p0.Y, p1.Y) + AlignmentToleranceFeet);
            },
            Math.Max(AlignmentToleranceFeet * 10.0, 1.0));

        foreach (var (i, j) in candidatePairs)
        {
            if (!IsSameWallRun(lines[i], lines[j], cosTolerance, doorSegments, doorArcs))
                continue;

            var rootI = Find(i);
            var rootJ = Find(j);
            if (rootI != rootJ)
                parent[rootI] = rootJ;
        }

        var merged = Enumerable.Range(0, lines.Count)
            .GroupBy(Find)
            .Select(group =>
            {
                var members = group.Select(index => lines[index]).ToList();
                return members.Count == 1 ? members[0] : MergeLineGroup(members);
            })
            .ToList();

        merged.AddRange(others);
        return merged;
    }

    /// <summary>Xem doc-comment MergeOverlappingDuplicates. Trả về TRUE nếu 2 candidate cùng bề rộng,
    /// cùng phương, và HOẶC chồng lấn nhau (overlap &gt; 0 — chắc chắn không phải chỗ cửa, vì cửa tạo ra
    /// khoảng CÁCH chứ không tạo ra phần chồng lấn), HOẶC chạm/cách nhau trong khoảng
    /// AlignmentToleranceFeet VÀ điểm nối đó không chạm layer cửa.</summary>
    private static bool IsSameWallRun(
        WallCandidate candidateA, WallCandidate candidateB, double cosTolerance,
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs)
    {
        if (Math.Abs(candidateA.ThicknessMm - candidateB.ThicknessMm) > 0.5)
            return false;

        var a = (Line)candidateA.Centerline;
        var b = (Line)candidateB.Centerline;
        var dirA = (a.GetEndPoint(1) - a.GetEndPoint(0)).Normalize();
        var dirB = (b.GetEndPoint(1) - b.GetEndPoint(0)).Normalize();
        if (Math.Abs(dirA.DotProduct(dirB)) < cosTolerance)
            return false;

        var origin = a.GetEndPoint(0);
        var normal = new XYZ(-dirA.Y, dirA.X, 0);
        var bP0 = b.GetEndPoint(0);
        var bP1 = b.GetEndPoint(1);
        if (Math.Abs((bP0 - origin).DotProduct(normal)) > AlignmentToleranceFeet)
            return false;

        var bStart = (bP0 - origin).DotProduct(dirA);
        var bEnd = (bP1 - origin).DotProduct(dirA);
        var lo = Math.Min(bStart, bEnd);
        var hi = Math.Max(bStart, bEnd);
        var overlap = Math.Min(a.Length, hi) - Math.Max(0.0, lo);

        if (overlap > 0)
            return true;
        if (overlap < -AlignmentToleranceFeet)
            return false;

        return !TouchesDoorLayer(a.GetEndPoint(0), doorSegments, doorArcs) &&
               !TouchesDoorLayer(a.GetEndPoint(1), doorSegments, doorArcs) &&
               !TouchesDoorLayer(bP0, doorSegments, doorArcs) &&
               !TouchesDoorLayer(bP1, doorSegments, doorArcs);
    }

    /// <summary>Gộp 1 nhóm candidate Line đã xác nhận cùng vị trí/bề rộng thành 1 Line DUY NHẤT chạy từ
    /// điểm xa nhất bên này tới điểm xa nhất bên kia — chiếu toàn bộ đầu mút của cả nhóm lên trục hướng
    /// của candidate đầu tiên trong nhóm rồi lấy khoảng [min, max] của các tham số chiếu đó.</summary>
    private static WallCandidate MergeLineGroup(List<WallCandidate> members)
    {
        var reference = (Line)members[0].Centerline;
        var origin = reference.GetEndPoint(0);
        var direction = (reference.GetEndPoint(1) - origin).Normalize();

        var minParam = double.MaxValue;
        var maxParam = double.MinValue;
        foreach (var member in members)
        {
            var line = (Line)member.Centerline;
            foreach (var point in new[] { line.GetEndPoint(0), line.GetEndPoint(1) })
            {
                var param = (point - origin).DotProduct(direction);
                minParam = Math.Min(minParam, param);
                maxParam = Math.Max(maxParam, param);
            }
        }

        return new WallCandidate
        {
            Centerline = Line.CreateBound(origin + direction.Multiply(minParam), origin + direction.Multiply(maxParam)),
            ThicknessMm = members[0].ThicknessMm,
            LengthFeet = maxParam - minParam
        };
    }

    private static bool TouchesDoorLayer(
        XYZ point,
        IReadOnlyList<RawSegment> doorSegments, IReadOnlyList<RawArc> doorArcs)
    {
        foreach (var segment in doorSegments)
        {
            var segmentVector = segment.P1 - segment.P0;
            var segmentLengthSquared = segmentVector.DotProduct(segmentVector);
            var segmentParameter = segmentLengthSquared > 1e-12
                ? (point - segment.P0).DotProduct(segmentVector) / segmentLengthSquared
                : 0.0;
            segmentParameter = Math.Max(0.0, Math.Min(1.0, segmentParameter));
            var closestPoint = segment.P0 + segmentVector.Multiply(segmentParameter);
            if (point.DistanceTo(closestPoint) <= DoorTouchToleranceFeet)
                return true;
        }

        foreach (var arc in doorArcs)
        {
            if (IsPointNearArc(point, arc, DoorTouchToleranceFeet))
                return true;
        }

        return false;
    }

    private static bool IsPointNearArc(XYZ point, RawArc arc, double tolerance)
    {
        var pointFromCenter = point - arc.Center;
        var distance = Math.Sqrt(pointFromCenter.X * pointFromCenter.X + pointFromCenter.Y * pointFromCenter.Y);
        if (Math.Abs(distance - arc.Radius) > tolerance)
            return false;

        var angle = NormalizeAngle(Math.Atan2(pointFromCenter.Y, pointFromCenter.X));
        var sweep = ComputeAbsoluteSweep(arc, arc.Center);
        var relativeAngle = NormalizeAngle(angle - sweep.Start);
        return relativeAngle <= sweep.End - sweep.Start + 1e-6;
    }

    private static bool IsPerpendicular(XYZ d1, XYZ d2) =>
        Math.Abs(d1.DotProduct(d2)) <= Math.Sin(AngleToleranceDegrees * Math.PI / 180.0);

    /// <summary>1 nét được coi là "nối đúng" 2 điểm p/q nếu 2 đầu của nó gần trùng (trong dung sai
    /// AlignmentToleranceFeet) 2 điểm đó, không kể thứ tự.</summary>
    private static bool ConnectsCorners(RawSegment s, XYZ p, XYZ q) =>
        (s.P0.DistanceTo(p) <= AlignmentToleranceFeet && s.P1.DistanceTo(q) <= AlignmentToleranceFeet) ||
        (s.P0.DistanceTo(q) <= AlignmentToleranceFeet && s.P1.DistanceTo(p) <= AlignmentToleranceFeet);

    /// <summary>Bắt cặp 2 cung tròn ĐỒNG TÂM thành 1 đoạn tường cong — tương đương DetectLinePairs
    /// nhưng cho nét cong: 2 cung có cùng tâm (sai số &lt;= 3mm), hiệu bán kính nằm trong khoảng bề rộng
    /// hợp lý, và chồng lấn góc quét đủ nhiều (&gt;=50% cung ngắn hơn) để coi là 1 cặp hợp lệ. Giống
    /// DetectLinePairs: TẠO 1 WallCandidate CHO MỌI CẶP HỢP LỆ, 1 cung có thể tham gia nhiều cặp cùng
    /// lúc, không giới hạn "mỗi cung chỉ 1 cặp". Đường tim dựng theo phần HỢP (union) góc quét của 2
    /// cung (không phải phần chồng lấn) — cùng lý do xử lý góc/T-junction như DetectLinePairs — giới
    /// hạn phần kéo dài thêm mỗi đầu (quy đổi ra chiều dài cung qua bán kính trung bình) tối đa bằng
    /// maxFeet.</summary>
    private static (List<WallCandidate> Candidates, bool[] Used) DetectArcPairs(
        IReadOnlyList<RawArc> arcs, double minFeet, double maxFeet)
    {
        // "used" = "co tham gia >= 1 cap hop le" (dung de dem UnresolvedArcCount ben Detect), khong
        // gioi han 1 cung chi duoc dung cho 1 cap - xem doc-comment tren.
        var used = new bool[arcs.Count];
        var candidates = new List<WallCandidate>();

        var pairs = new List<(int I, int J, double OverlapSweep, XYZ Center, double Radius,
            double OverlapStart, double OverlapEnd, (double Start, double End) RangeA, (double Start, double End) RangeB)>();

        for (var i = 0; i < arcs.Count; i++)
        {
            var a = arcs[i];

            for (var j = i + 1; j < arcs.Count; j++)
            {
                var b = arcs[j];

                if (a.Center.DistanceTo(b.Center) > AlignmentToleranceFeet)
                    continue;

                var radiusDiff = Math.Abs(a.Radius - b.Radius);
                if (radiusDiff < minFeet || radiusDiff > maxFeet)
                    continue;

                var sharedCenter = new XYZ(
                    (a.Center.X + b.Center.X) / 2.0,
                    (a.Center.Y + b.Center.Y) / 2.0, 0);

                var rangeA = ComputeAbsoluteSweep(a, sharedCenter);
                var rangeB = ComputeAbsoluteSweep(b, sharedCenter);
                var overlap = IntersectCircularRanges(rangeA, rangeB);
                if (overlap is null)
                    continue;

                var (overlapStart, overlapEnd, shiftedBStart, shiftedBEnd) = overlap.Value;
                var overlapSweep = overlapEnd - overlapStart;
                var minSweep = Math.Min(rangeA.End - rangeA.Start, rangeB.End - rangeB.Start);
                if (overlapSweep <= 0 || overlapSweep < minSweep * MinOverlapRatio)
                    continue;

                var avgRadius = (a.Radius + b.Radius) / 2.0;
                // Dung rangeB DA DICH (shiftedBStart/End) chu khong phai rangeB goc - de tinh union o
                // buoc sau dung cung 1 "vong" tuyet doi voi overlapStart/End (xem ghi chu IntersectCircularRanges).
                pairs.Add((i, j, overlapSweep, sharedCenter, avgRadius, overlapStart, overlapEnd,
                    rangeA, (shiftedBStart, shiftedBEnd)));
            }
        }

        foreach (var pair in pairs)
        {
            // Bat dau tu phan chong lan (overlap - da xac nhan hop le o buoc tim cap phia tren), roi
            // keo dai ra 2 phia toi phan HOP (union) cua RangeA/RangeB - nhung gioi han moi phia toi da
            // maxSweepExtend (quy doi maxFeet ra radian qua ban kinh trung binh) de tranh keo dai qua
            // da neu chi tinh co gan nhau (cung ly do nhu DetectLinePairs).
            var maxSweepExtend = pair.Radius > 1e-6 ? maxFeet / pair.Radius : double.MaxValue;
            var rawUnionStart = Math.Min(pair.RangeA.Start, pair.RangeB.Start);
            var rawUnionEnd = Math.Max(pair.RangeA.End, pair.RangeB.End);

            var extendBefore = Math.Min(pair.OverlapStart - rawUnionStart, maxSweepExtend);
            var extendAfter = Math.Min(rawUnionEnd - pair.OverlapEnd, maxSweepExtend);

            var unionStart = pair.OverlapStart - extendBefore;
            var unionEnd = pair.OverlapEnd + extendAfter;

            Arc centerlineArc;
            try
            {
                centerlineArc = Arc.Create(pair.Center, pair.Radius, unionStart, unionEnd, XYZ.BasisX, XYZ.BasisY);
            }
            catch
            {
                // Goc quet suy bien (qua nho/qua sat 2*PI) - bo qua cap nay, khong lam hong ca qua trinh.
                continue;
            }

            used[pair.I] = true;
            used[pair.J] = true;

            var a = arcs[pair.I];
            var b = arcs[pair.J];
            var thicknessMm = Math.Round(
                UnitUtils.ConvertFromInternalUnits(Math.Abs(a.Radius - b.Radius), UnitTypeId.Millimeters),
                MidpointRounding.AwayFromZero);

            candidates.Add(new WallCandidate
            {
                Centerline = centerlineArc,
                ThicknessMm = thicknessMm,
                LengthFeet = centerlineArc.Length
            });
        }

        return (candidates, used);
    }

    private static double NormalizeAngle(double angle)
    {
        const double twoPi = 2 * Math.PI;
        var result = angle % twoPi;
        if (result < 0)
            result += twoPi;
        return result;
    }

    /// <summary>Tính góc quét TUYỆT ĐỐI của 1 cung theo 1 tâm chung cho trước (không nhất thiết đúng
    /// tâm gốc của cung — 2 cung đồng tâm có thể lệch tâm vài mm do sai số CAD, dùng lại 1 tâm chung để
    /// so sánh góc nhất quán). Trả về (Start, End) với Start thuộc [0, 2π) và End &gt; Start (có thể vượt
    /// 2π) — dùng điểm giữa cung (Mid) để xác định đúng chiều quét (CCW từ P0 hay từ P1).</summary>
    private static (double Start, double End) ComputeAbsoluteSweep(RawArc arc, XYZ center)
    {
        var a0 = NormalizeAngle(Math.Atan2(arc.P0.Y - center.Y, arc.P0.X - center.X));
        var a1 = NormalizeAngle(Math.Atan2(arc.P1.Y - center.Y, arc.P1.X - center.X));
        var aMid = NormalizeAngle(Math.Atan2(arc.Mid.Y - center.Y, arc.Mid.X - center.X));

        var sweepCcwFromA0 = NormalizeAngle(a1 - a0);
        if (sweepCcwFromA0 < 1e-9)
            sweepCcwFromA0 = 2 * Math.PI;

        var midOffsetFromA0 = NormalizeAngle(aMid - a0);

        if (midOffsetFromA0 <= sweepCcwFromA0 + 1e-6)
            return (a0, a0 + sweepCcwFromA0);

        var sweepCcwFromA1 = NormalizeAngle(a0 - a1);
        if (sweepCcwFromA1 < 1e-9)
            sweepCcwFromA1 = 2 * Math.PI;

        return (a1, a1 + sweepCcwFromA1);
    }

    /// <summary>Giao 2 khoảng góc tuyệt đối (dạng Start/End, End có thể &gt; 2π) — thử giao với 3 bản
    /// sao dịch ±2π của khoảng b để xử lý trường hợp 1 trong 2 khoảng vòng qua mốc 0/2π. Trả thêm
    /// ShiftedB (= b đã dịch đúng bằng lượng dùng để ra kết quả giao) để bên gọi tính phần HỢP (union)
    /// đúng cùng 1 "vòng" tuyệt đối với phần giao — nếu chỉ dùng lại b gốc (chưa dịch) để tính union thì
    /// sẽ lệch hẳn 1 vòng 2π so với phần giao trong đúng trường hợp cần dịch (vòng qua mốc 0/2π).</summary>
    private static (double Start, double End, double ShiftedBStart, double ShiftedBEnd)? IntersectCircularRanges(
        (double Start, double End) a, (double Start, double End) b)
    {
        const double twoPi = 2 * Math.PI;

        foreach (var shift in new[] { -twoPi, 0.0, twoPi })
        {
            var shiftedBStart = b.Start + shift;
            var shiftedBEnd = b.End + shift;
            var start = Math.Max(a.Start, shiftedBStart);
            var end = Math.Min(a.End, shiftedBEnd);
            if (end - start > 1e-6)
                return (start, end, shiftedBStart, shiftedBEnd);
        }

        return null;
    }
}
