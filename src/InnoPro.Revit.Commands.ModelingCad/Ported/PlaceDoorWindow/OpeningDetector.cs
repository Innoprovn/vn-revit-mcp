using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Nhận diện từng vị trí cửa đi/cửa sổ từ layer CAD người dùng chọn, khớp luôn với tường
/// THẬT gần nhất trong model (khác WallFromCad: ở đây tường đã có sẵn trong model, không phải tự
/// dựng từ CAD — xem WallHostFinder), và gán tên bằng Common.TagMatcher (chữ đọc thẳng từ file CAD và Text Note).
///
/// Đo bề rộng/phương lỗ mở dùng lại ĐÚNG thuật toán đã kiểm chứng ở WallFromCad\WallPairing.BuildDoorLines
/// (2 project là 2 assembly tách biệt trong kiến trúc hot-reload nên phải chép lại, không tham chiếu
/// chéo được):
///   - Cụm nét/cung CHẠM NHAU (trong DoorTouchToleranceFeet) coi là 1 ký hiệu cửa.
///   - Cụm CÓ cung xoay (đa số cửa đi, KỂ CẢ cửa nhiều cánh — nhiều cung trong CÙNG 1 cụm): mỗi chân
///     cung (tâm→P0, tâm→P1) cho 1 PHƯƠNG ứng viên; với MỖI phương khác nhau xuất hiện, lấy 2 ĐIỂM XA
///     NHAU NHẤT trong TOÀN BỘ điểm của cả cụm (tâm + 2 đầu mọi cung, VÀ 2 đầu mọi nét thẳng — khung/nẹp
///     cửa) khi chiếu theo đúng phương đó — đây mới là bề rộng THẬT của cả lỗ mở. KHÔNG tính bề rộng
///     riêng cho từng cung (hinge→điểm 2 cung gặp nhau ở giữa, kiểu cửa đôi 2 cánh) vì sẽ SAI — 1 lỗ mở
///     duy nhất phải tính từ bản lề NGOÀI CÙNG bên này tới bản lề NGOÀI CÙNG bên kia. CHỈ 1 trong các
///     phương ứng viên thẳng hàng với tường thật (đúng chuẩn vẽ CAD: lá cửa lúc ĐÓNG nằm khít mặt
///     tường) — WallHostFinder tự loại phương sai vì không tìm được tường song song đủ gần.
///   - Cụm KHÔNG có cung (cửa sổ, cửa lùa...): dùng lại cách dự phòng — cặp 2 đường thẳng song song
///     cách xa nhau nhất trong cụm.</summary>
internal static class OpeningDetector
{
    private const double AngleToleranceDegrees = 2.0;
    private static readonly double MinOpeningFeet = UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);
    private static readonly double DoorTouchToleranceFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);
    private static readonly double MinSegmentLengthFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    public static List<OpeningInstance> Detect(
        IReadOnlyList<RawSegment> segments, IReadOnlyList<RawArc> arcs, IReadOnlyList<Wall> walls,
        IReadOnlyList<Common.TextTag> tags, double expandFeet)
    {
        var chosenAll = new List<(DetectedOpening Candidate, HostWallMatch? Host)>();
        foreach (var group in BuildCandidates(segments, arcs))
        {
            var resolved = group
                .Select(candidate => (Candidate: candidate, Host: WallHostFinder.FindHost(candidate, walls)))
                .ToList();
            var withHost = resolved.Where(r => r.Host is not null).ToList();
            chosenAll.AddRange(withHost.Count > 0 ? withHost : resolved.Take(1));
        }

        // Lỗ mở hẹp hơn 300 mm không phải cửa (nét vụn, ký hiệu khác trên layer cửa).
        chosenAll.RemoveAll(x => x.Candidate.LengthFeet < MinOpeningFeet);

        // Ký hiệu cửa có thể sinh thêm ứng viên không host (nét cánh mở, cụm tách rời); bỏ ứng viên không host
        // nằm chồng lên một lỗ mở đã khớp tường.
        var hosted = chosenAll.Where(x => x.Host != null).ToList();
        chosenAll.RemoveAll(x => x.Host == null && hosted.Any(h =>
            h.Candidate.Center.DistanceTo(x.Candidate.Center) <= Math.Max(h.Candidate.LengthFeet, x.Candidate.LengthFeet)));

        // Tên cửa: vùng chữ nhật dọc theo lỗ mở (theo hướng tường), leader chỉ vào lỗ là chắc chắn;
        // bỏ chữ kích thước "2000x2700" thường đặt sát tên.
        var names = tags.Where(t => !TextTagReader.IsDimension(t.Text) && TextTagReader.IsMark(t.Text)).ToList();
        var assigned = Common.TagMatcher.Assign(chosenAll, names, (item, tag) =>
        {
            var halfAcross = item.Host?.Wall.Width / 2 ?? UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Millimeters);
            return Common.TagMatcher.Point(tag, item.Candidate.Center, item.Candidate.Direction, item.Candidate.LengthFeet / 2, halfAcross, expandFeet);
        }, share: false);

        return chosenAll.Select((item, i) => new OpeningInstance
        {
            Opening = item.Candidate,
            WidthMm = Math.Round(UnitUtils.ConvertFromInternalUnits(item.Candidate.LengthFeet, UnitTypeId.Millimeters), MidpointRounding.AwayFromZero),
            Host = item.Host,
            TagName = assigned.TryGetValue(i, out var tag) ? tag.Text : null,
        }).ToList();
    }

    /// <summary>Trả về danh sách NHÓM candidate — mỗi nhóm là 1-2 candidate cùng xuất phát từ 1 cung/1
    /// cụm (2 candidate cho 1 cung — xem doc-comment lớp), để Detect biết candidate nào "cùng phe" khi
    /// quyết định giữ 1 hay giữ cả 2 (trường hợp cửa góc, cả 2 chân cùng khớp 2 tường vuông góc).</summary>
    private static List<List<DetectedOpening>> BuildCandidates(
        IReadOnlyList<RawSegment> segments, IReadOnlyList<RawArc> arcs)
    {
        var items = new List<XYZ[]>();
        var segmentIndexOfItem = new List<int>();
        var arcIndexOfItem = new List<int>();
        foreach (var index in Enumerable.Range(0, segments.Count))
        {
            items.Add(new[] { segments[index].P0, segments[index].P1 });
            segmentIndexOfItem.Add(index);
            arcIndexOfItem.Add(-1);
        }
        foreach (var index in Enumerable.Range(0, arcs.Count))
        {
            items.Add(new[] { arcs[index].P0, arcs[index].P1, arcs[index].Mid });
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
        var groups = new List<List<DetectedOpening>>();

        foreach (var group in Enumerable.Range(0, items.Count).GroupBy(Find))
        {
            var groupIndices = group.ToList();
            var clusterArcs = groupIndices
                .Select(index => arcIndexOfItem[index])
                .Where(arcIndex => arcIndex >= 0)
                .Select(arcIndex => arcs[arcIndex])
                .Where(arc => arc.Radius >= MinSegmentLengthFeet)
                .ToList();
            var clusterSegments = groupIndices
                .Select(index => segmentIndexOfItem[index])
                .Where(segmentIndex => segmentIndex >= 0)
                .Select(segmentIndex => segments[segmentIndex])
                .ToList();

            if (clusterArcs.Count > 0)
            {
                // Cua NHIEU canh (vd cua doi 2 canh khong deu) co THE co nhieu cung trong CUNG 1 cum -
                // KHONG tinh be rong rieng cho tung cung (se ra be rong tu ban le toi diem 2 la gap
                // nhau o giua, SAI khi day thuc ra la 1 lo mo DUY NHAT tu ban le NGOAI CUNG nay toi ban
                // le NGOAI CUNG kia). Thay vao do: gom diem cua ca cum (tam + 2 dau moi cung, VA 2 dau
                // moi net thang NGAN - khung/nep cua) lai, roi voi MOI phuong xuat hien (tu chan cac
                // cung), lay 2 DIEM XA NHAU NHAT trong so cac diem do khi chieu theo dung phuong ay -
                // day moi la be rong THAT cua ca lo mo.
                //
                // CHU Y: CHI lay net thang NGAN HON ban kinh cung lon nhat trong cum - loai bo net dai
                // kieu LANH TO/header phia tren khung cua (thuong ve DAI HON ca lo mo that, de vuon ra
                // do len tuong 2 ben) - neu tinh lan net nay vao se lam lech han diem xa nhat tinh duoc,
                // sai ca be rong lan tam thuc te (dung bai hoc tu truong hop D6 lech 152mm).
                var maxRadius = clusterArcs.Max(arc => arc.Radius);
                var allPoints = new List<XYZ>();
                foreach (var arc in clusterArcs)
                {
                    allPoints.Add(arc.Center);
                    allPoints.Add(arc.P0);
                    allPoints.Add(arc.P1);
                }
                foreach (var segment in clusterSegments)
                {
                    if (segment.Length >= maxRadius)
                        continue;

                    allPoints.Add(segment.P0);
                    allPoints.Add(segment.P1);
                }

                var candidateDirections = new List<XYZ>();
                foreach (var arc in clusterArcs)
                {
                    candidateDirections.Add((arc.P0 - arc.Center).Normalize());
                    candidateDirections.Add((arc.P1 - arc.Center).Normalize());
                }

                var seenDirections = new List<XYZ>();
                foreach (var rawDirection in candidateDirections)
                {
                    // Gop cac phuong gan song song (ke ca nguoc dau) thanh 1 - moi phuong khac nhau chi
                    // sinh candidate 1 lan, tranh trung lap khi nhieu chan cung cung phuong.
                    if (seenDirections.Any(seen => Math.Abs(seen.DotProduct(rawDirection)) >= cosTolerance))
                        continue;
                    seenDirections.Add(rawDirection);

                    var origin = allPoints[0];
                    var minT = double.MaxValue;
                    var maxT = double.MinValue;
                    var minPoint = origin;
                    var maxPoint = origin;
                    foreach (var point in allPoints)
                    {
                        var t = (point - origin).DotProduct(rawDirection);
                        if (t < minT)
                        {
                            minT = t;
                            minPoint = point;
                        }

                        if (t > maxT)
                        {
                            maxT = t;
                            maxPoint = point;
                        }
                    }

                    var spanLength = maxT - minT;
                    if (spanLength < MinSegmentLengthFeet)
                        continue;

                    groups.Add(new List<DetectedOpening>
                    {
                        new()
                        {
                            // Center = diem GIUA lo mo (giua 2 diem xa nhau nhat) - dung chuan family
                            // Door/Window cua Revit, xem ghi chu o nhanh 2-duong-song-song ben duoi.
                            Center = (minPoint + maxPoint) / 2.0,
                            Direction = rawDirection,
                            LengthFeet = spanLength,
                            SwingDirection = FindSwingDirection(clusterArcs, rawDirection, cosTolerance)
                        }
                    });
                }

                continue;
            }

            if (clusterSegments.Count < 2)
                continue;

            var bestDistance = -1.0;
            RawSegment bestA = clusterSegments[0];
            RawSegment bestB = clusterSegments[0];
            for (var i = 0; i < clusterSegments.Count; i++)
            for (var j = i + 1; j < clusterSegments.Count; j++)
            {
                var a = clusterSegments[i];
                var b = clusterSegments[j];
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

            // Center = diem GIUA lo mo, tinh tu anchor (tam net ngan hon) di 1 nua "length" doc theo
            // direction - dung chuan family Door/Window, xem ghi chu nhanh cung xoay o tren.
            groups.Add(new List<DetectedOpening>
            {
                new()
                {
                    Center = anchor + direction.Multiply(length / 2.0),
                    Direction = direction,
                    LengthFeet = length
                }
            });
        }

        return groups;
    }

    /// <summary>Xác định hướng mở cửa (phía có cung xoay, xem yêu cầu Command.cs) — tìm trong
    /// <paramref name="clusterArcs"/> cung nào có 1 chân (tâm→P0 hoặc tâm→P1) thẳng hàng đúng
    /// <paramref name="wallDirection"/> (đúng chuẩn CAD: chân đó là lá cửa lúc ĐÓNG, nằm khít tường —
    /// đây chính là cung ứng với lỗ mở này, không phải cung của 1 cửa khác trong cùng cụm), rồi xem
    /// điểm GIỮA cung (Mid) lệch về phía nào so với trục tường (dấu của tích vô hướng với pháp tuyến) —
    /// đó chính là phía cửa MỞ RA (chứa phần cong của cung). Trả về null nếu không có cung nào khớp
    /// phương (không nên xảy ra vì phương này vốn sinh ra từ chân 1 cung) hoặc Mid trùng đúng trục
    /// (cung suy biến).</summary>
    private static XYZ? FindSwingDirection(
        List<RawArc> clusterArcs, XYZ wallDirection, double cosTolerance)
    {
        var normal = new XYZ(-wallDirection.Y, wallDirection.X, 0);

        foreach (var arc in clusterArcs)
        {
            var legToP0 = (arc.P0 - arc.Center).Normalize();
            var legToP1 = (arc.P1 - arc.Center).Normalize();
            var matches =
                Math.Abs(legToP0.DotProduct(wallDirection)) >= cosTolerance ||
                Math.Abs(legToP1.DotProduct(wallDirection)) >= cosTolerance;
            if (!matches)
                continue;

            var signedOffset = (arc.Mid - arc.Center).DotProduct(normal);
            if (Math.Abs(signedOffset) < 1e-6)
                continue;

            return normal.Multiply(Math.Sign(signedOffset));
        }

        return null;
    }
}
