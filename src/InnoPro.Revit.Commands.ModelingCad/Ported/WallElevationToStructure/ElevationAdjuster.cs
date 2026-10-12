using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallElevationToStructure;

/// <summary>Chỉnh Base Offset (chân) và Top Offset/Unconnected Height (đỉnh) của các Wall Location dạng
/// Line để chân nằm trên sàn kết cấu và đỉnh nằm dưới đáy sàn/dầm kết cấu — đọc hình học Floor/Structural
/// Framing từ 1 file Link đã chọn (RevitLinkInstance), quy đổi toạ độ Link → Host qua
/// <see cref="RevitLinkInstance.GetTotalTransform"/> ngay từ đầu (mọi so sánh sau đó đều ở toạ độ Host,
/// cùng hệ với Wall).
///
/// DÒ SÀN/DẦM: dùng 1 điểm dò duy nhất — trung điểm đường tim tường (XY) — kiểm tra nằm trong bounding
/// box (XY) của từng Floor/Beam (đủ dùng cho tường thẳng thông thường, GIỚI HẠN: tường dài đi qua nhiều
/// ô sàn cao độ khác nhau sẽ không tách được, giống pattern dò Ceiling trong WallFinishLayer). Xét ứng
/// viên CẢ 2 HƯỚNG quanh chân/đỉnh tường HIỆN TẠI — khoảng cách TUYỆT ĐỐI (không quan tâm ứng viên nằm
/// trên hay dưới chân/đỉnh) nằm trong [searchMinFeet, searchMaxFeet] người dùng nhập trong cửa sổ (mm,
/// mặc định 0-800, áp dụng CHUNG cho cả chân lẫn đỉnh) — ngoài khoảng đó coi như không tìm thấy, bỏ qua
/// tường (không đoán bừa). Trong số ứng viên hợp lệ, KHÔNG lấy ứng viên gần nhất theo khoảng cách tuyệt
/// đối (đã thử — sai khi tường cắm SÂU qua khỏi sàn đúng, vào tới 1 cấu kiện khác bên dưới/bên trên gần
/// hơn về khoảng cách nhưng KHÔNG phải sàn cần tựa lên: vd chân tường cắm sâu -300 trong khi sàn đúng ở
/// 0 nhưng có 1 cấu kiện khác ở -50 gần hơn về khoảng cách tuyệt đối lại bị chọn nhầm — tương tự với đỉnh
/// tường, ứng viên SAI luôn thấp hơn đích đúng) — mà LUÔN lấy mặt trên CAO NHẤT trong phạm vi cho chân,
/// VÀ mặt dưới CAO NHẤT trong phạm vi cho đỉnh (không phải thấp nhất — đã thử và sai, xem lịch sử sửa).
/// Sau
/// khi tìm được, LUÔN đặt chân tường lên ĐÚNG mặt trên sàn tìm được (dù sàn đó vốn đang ở trên hay dưới
/// chân tường), và đỉnh tường xuống ĐÚNG mặt dưới sàn/dầm tìm được (dù đang ở trên hay dưới đỉnh tường)
/// — không còn khái niệm "khoảng hở", luôn chạm sát (gap = 0).
///
/// ĐO CHÍNH XÁC (Floor — xem GetPreciseSurfaceZ): sau khi CHỌN xong ứng viên Floor bằng bounding box
/// (bước trên, chỉ để lọc/xếp hạng), KHÔNG dùng thẳng Max.Z/Min.Z của bounding box đó làm cao độ cuối —
/// bounding box là cực trị TOÀN BỘ mặt bằng sàn, sai nếu sàn không phẳng tuyệt đối tại đúng vị trí tường
/// (mép dốc thoát nước, dầm bo mép, bậc cao độ cục bộ ở chỗ khác của cùng 1 tấm sàn...), gây lệch 1
/// khoảng CỐ ĐỊNH giữa cao độ dùng để tính và cao độ thật tại vị trí tường — mà bắn tia thẳng đứng qua
/// mặt trên/dưới THẬT của Floor đó tại ĐÚNG toạ độ XY của tường. Dầm (Structural Framing) chưa hỗ trợ đo
/// chính xác kiểu này (không phải HostObject), vẫn dùng bounding box.
///
/// DẦM ƯU TIÊN HƠN SÀN Ở ĐỈNH: chỉ dùng đáy dầm thay đáy sàn khi ĐỦ CẢ 3 điều kiện (xem
/// WallFitsUnderBeam) — (1) chiều dài tường SONG SONG với chiều dài dầm (không chỉ "phủ" theo hình
/// chiếu — 2 hướng phải gần cùng phương, sai lệch dưới ParallelAngleToleranceDeg), (2) dầm phủ HẾT
/// chiều dài tường (chiếu 2 đầu dầm lên trục dọc tường phải phủ [0, chiều dài tường]), (3) bề rộng tường
/// GIAO với PHẠM VI BỀ RỘNG THẬT của dầm ít nhất <see cref="BeamWidthOverlapRatio"/> (mặc định 50% — vd
/// tường 200mm chỉ cần tối thiểu 100mm bề rộng của nó nằm trong bề rộng dầm là đủ, KHÔNG bắt buộc nằm
/// trọn 100% như trước) — bề rộng dầm đo từ hình học Solid thật, không suy từ bounding box (dầm xoay
/// lệch trục X/Y sẽ có bounding box rộng hơn bề rộng thật rất nhiều nếu chỉ đo bbox). Không đủ 3 điều
/// kiện thì dùng đáy sàn như bình thường.</summary>
internal static class ElevationAdjuster
{
    private static readonly double BeamLengthToleranceFeet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
    private static readonly double BeamWidthToleranceFeet = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
    private static readonly double EpsilonFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

    /// <summary>Tỉ lệ tối thiểu bề rộng tường phải giao với bề rộng dầm để còn coi là "tường nằm dưới
    /// dầm" (dùng đáy dầm thay đáy sàn) — 0.5 = tường chỉ cần giao 50% bề rộng của nó với dầm.</summary>
    private const double BeamWidthOverlapRatio = 0.5;

    /// <summary>Dung sai kiểm tra lại hình học SAU khi Set() param + Regenerate — lớn hơn EpsilonFeet
    /// (chỉ dùng để quyết định có cần Set() hay không) vì đây là ngưỡng phát hiện LỖI THẬT (param đã Set
    /// nhưng hình học cuối cùng không khớp — nghi Attach Base/Top hoặc Join/Cut ghi đè), không phải sai
    /// số làm tròn thông thường.</summary>
    private static readonly double MismatchToleranceFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    /// <summary>Sai lệch góc tối đa (độ) giữa hướng tường và hướng dầm để còn coi là "song song".</summary>
    private const double ParallelAngleToleranceDeg = 5.0;

    internal sealed class Report
    {
        public int WallsScanned;

        /// <summary>Số tường có ÍT NHẤT 1 trong 2 đầu (chân/đỉnh) được chỉnh thành công, không dính lỗi
        /// ở đầu đó — số hiển thị chính trong TaskDialog (gộp từ BaseAdjusted/TopAdjusted, khử trùng lặp
        /// theo tường).</summary>
        public int WallsFixed;

        /// <summary>Số tường có ÍT NHẤT 1 vấn đề (không tìm thấy sàn/dầm, Attach, hình học không khớp,
        /// hoặc không đọc được hình học) — số hiển thị chính trong TaskDialog (gộp mọi loại lỗi bên
        /// dưới, khử trùng lặp theo tường).</summary>
        public int WallsFailed;

        public int BaseAdjusted;
        public int TopAdjusted;
        public int SkippedNoBase;
        public int SkippedNoTop;
        public int SkippedNotLine;

        /// <summary>Đã Set() param chân/đỉnh (không lỗi), NHƯNG bounding box đọc lại sau Regenerate
        /// KHÔNG khớp giá trị vừa đặt (lệch quá EpsilonFeet) — dấu hiệu tường có Attach Base/Top vào 1
        /// reference khác (roof/floor) đang ghi đè offset, hoặc bị Join/Cut với phần tử khác làm sai
        /// lệch hình học hiển thị so với tham số. Đã CỘNG vào BaseAdjusted/TopAdjusted ở trên (param đã
        /// đổi thật) nhưng cần liệt kê riêng vì kết quả hình học cuối cùng không đáng tin.</summary>
        public int BaseMismatch;
        public int TopMismatch;

        /// <summary>Chân/đỉnh tường đang Attach vào 1 reference khác (Attach Base/Top — thường là
        /// Roof/Floor) — KHÔNG thử Set() Base Offset/Top Offset vì attachment thường ghi đè offset đó,
        /// dễ gây ra đúng lỗi "Set() không báo lỗi nhưng hình học không đổi đúng hướng mong muốn". Cần
        /// Detach tay trong Revit (Modify Wall &gt; Detach Base/Top) rồi chạy lại tool.</summary>
        public int SkippedBaseAttached;
        public int SkippedTopAttached;

        /// <summary>Chi tiết số liệu từng tường (mm, làm tròn) — chỉ điền khi tổng số tường đang xét
        /// <= 20 (tránh hộp thoại quá dài khi chạy hàng loạt cả dự án) — dùng để chẩn đoán khi kết quả
        /// vẫn sai dù đã qua nhiều lần sửa: cho thấy ĐÚNG phần tử sàn/dầm nào được chọn và cao độ CHÍNH
        /// XÁC (GetPreciseSurfaceZ) hay CHỈ bounding box (fallback) đã dùng.</summary>
        public List<string> Details { get; } = new();
    }

    private sealed class StructureGeo
    {
        public required Element Element { get; init; }
        public required BoundingBoxXYZ HostBox { get; init; }
        public XYZ? LineP0 { get; init; }
        public XYZ? LineP1 { get; init; }

        /// <summary>Bề rộng THẬT của dầm (đo từ Solid, vuông góc với chiều dài dầm) — chỉ tính cho
        /// Structural Framing, null với Floor (không cần).</summary>
        public double? WidthFeet { get; init; }
    }

    internal static Report Adjust(
        Document doc, IReadOnlyList<Wall> walls, IReadOnlyList<Element> floors, IReadOnlyList<Element> beams,
        Transform linkTransform, double searchMinFeet, double searchMaxFeet)
    {
        var report = new Report { WallsScanned = walls.Count };

        var floorGeos = floors.Select(f => BuildGeo(f, linkTransform, computeWidth: false)).Where(g => g is not null).Select(g => g!).ToList();
        var beamGeos = beams.Select(b => BuildGeo(b, linkTransform, computeWidth: true)).Where(g => g is not null).Select(g => g!).ToList();

        foreach (var wall in walls)
        {
            if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line line)
            {
                report.SkippedNotLine++;
                report.WallsFailed++;
                continue;
            }

            var bbox = wall.get_BoundingBox(null);
            if (bbox is null)
            {
                report.SkippedNotLine++;
                report.WallsFailed++;
                continue;
            }

            var wallP0 = line.GetEndPoint(0);
            var wallP1 = line.GetEndPoint(1);
            var wallLength = wallP0.DistanceTo(wallP1);
            if (wallLength <= 1e-6)
            {
                report.SkippedNotLine++;
                report.WallsFailed++;
                continue;
            }

            var wallDirection = (wallP1 - wallP0).Normalize();
            var midpoint = (wallP0 + wallP1) / 2.0;
            var currentBaseZ = bbox.Min.Z;
            var currentTopZ = bbox.Max.Z;
            var wallWidthFeet = wall.Width;

            // Trong so cac ung vien trong pham vi tim kiem, LUON uu tien mat tren CAO NHAT (khong phai
            // gan nhat theo khoang cach tuyet doi) - neu chan tuong dang cam sau qua 1 san (vd -300)
            // thi co the co 1 cau kien khac (mong, dam, san day hon...) o gan hon ve khoang cach tuyet
            // doi (vd -50) nhung KHONG phai san dung nguoi dung muon chan tuong tua len - san THAT luon
            // la mat cao nhat trong pham vi, xem doc-comment class.
            var floorBelow = floorGeos
                .Where(g => XyContains(g.HostBox, midpoint) &&
                            IsWithinSearchRange(g.HostBox.Max.Z - currentBaseZ, searchMinFeet, searchMaxFeet))
                .OrderByDescending(g => g.HostBox.Max.Z)
                .FirstOrDefault();

            // Prefilter theo bounding box GIAO NHAU (khong bat buoc midpoint tuong nam trong bbox dam -
            // dam thuong hep hon phong, tuong co the lech tam vai cm so voi dam ma van con nam trong be
            // rong that cua dam) - loc chinh xac hon dua vao WallFitsUnderBeam ngay sau.
            var wallHostBox = new BoundingBoxXYZ { Min = bbox.Min, Max = bbox.Max };
            var beamAbove = beamGeos
                .Where(g => BoundingBoxesOverlapXY(g.HostBox, wallHostBox, BeamLengthToleranceFeet) &&
                            IsWithinSearchRange(g.HostBox.Min.Z - currentTopZ, searchMinFeet, searchMaxFeet) &&
                            WallFitsUnderBeam(g, wallP0, wallP1, wallDirection, wallLength, wallWidthFeet, midpoint))
                .OrderBy(g => g.HostBox.Min.Z)
                .FirstOrDefault();

            // Doi tu "thap nhat" sang "CAO NHAT" - thuc te kiem chung cho thay ung vien SAI (khong phai
            // san dung nguoi dung can) luon la ung vien THAP HON nam xen giua vi tri hien tai va dich
            // dung (vd dinh tuong 2000/2500 hien tai, dich dung 2600, nhung co 1 be mat sai o 2550 -
            // THAP hon 2600 - bi chon nham khi uu tien "thap nhat"). Doi sang uu tien CAO NHAT trong pham
            // vi giong het floorBelow o tren.
            var floorAbove = beamAbove is not null
                ? null
                : floorGeos
                    .Where(g => XyContains(g.HostBox, midpoint) &&
                                IsWithinSearchRange(g.HostBox.Min.Z - currentTopZ, searchMinFeet, searchMaxFeet))
                    .OrderByDescending(g => g.HostBox.Min.Z)
                    .FirstOrDefault();

            // Sau khi CHON xong ung vien bang bounding box (o tren), do lai CHINH XAC cao do that TAI
            // DUNG vi tri tuong (khong dung cuc tri toan cuc cua ca tam san) - xem GetPreciseSurfaceZ.
            // Dam giu nguyen bbox-based (chua ho tro do mat chinh xac nhu Floor).
            var basePreciseZ = floorBelow is not null ? GetPreciseSurfaceZ(floorBelow.Element, midpoint, linkTransform, top: true) : null;
            double? targetBaseZ = floorBelow is not null ? basePreciseZ ?? floorBelow.HostBox.Max.Z : null;

            var topPreciseZ = floorAbove is not null ? GetPreciseSurfaceZ(floorAbove.Element, midpoint, linkTransform, top: false) : null;
            double? targetTopZ = beamAbove is not null
                ? beamAbove.HostBox.Min.Z
                : floorAbove is not null ? topPreciseZ ?? floorAbove.HostBox.Min.Z : null;

            if (walls.Count <= 20)
            {
                string Mm(double? feet) => feet is { } f ? Math.Round(UnitUtils.ConvertFromInternalUnits(f, UnitTypeId.Millimeters)).ToString() : "-";
                report.Details.Add(
                    $"Tường {wall.Id}: chân hiện tại={Mm(currentBaseZ)} | sàn dưới Id={(floorBelow is null ? "không có" : floorBelow.Element.Id.ToString())} bbox_đỉnh={Mm(floorBelow?.HostBox.Max.Z)} chính_xác={Mm(basePreciseZ)} → target={Mm(targetBaseZ)} || " +
                    $"đỉnh hiện tại={Mm(currentTopZ)} | {(beamAbove is not null ? $"dầm trên Id={beamAbove.Element.Id} bbox_đáy={Mm(beamAbove.HostBox.Min.Z)}" : $"sàn trên Id={(floorAbove is null ? "không có" : floorAbove.Element.Id.ToString())} bbox_đáy={Mm(floorAbove?.HostBox.Min.Z)} chính_xác={Mm(topPreciseZ)}")} → target={Mm(targetTopZ)}");
            }

            if (targetBaseZ is null)
                report.SkippedNoBase++;
            if (targetTopZ is null)
                report.SkippedNoTop++;

            if (targetBaseZ is null && targetTopZ is null)
            {
                report.WallsFailed++;
                continue;
            }

            // Snapshot truoc/sau de biet DUNG tuong nay co duoc chinh thanh cong / co dinh loi hay khong
            // (WallsFixed/WallsFailed la so tuong khu trung lap, khac voi cac counter theo LUOT chinh
            // chan/dinh rieng le o tren).
            var baseAdjustedBefore = report.BaseAdjusted;
            var topAdjustedBefore = report.TopAdjusted;
            var issuesBefore = report.SkippedBaseAttached + report.SkippedTopAttached + report.BaseMismatch + report.TopMismatch;

            ApplyElevation(doc, wall, targetBaseZ, targetTopZ, currentBaseZ, currentTopZ, report);

            var wasAdjusted = report.BaseAdjusted > baseAdjustedBefore || report.TopAdjusted > topAdjustedBefore;
            var hasIssue = targetBaseZ is null || targetTopZ is null ||
                           (report.SkippedBaseAttached + report.SkippedTopAttached + report.BaseMismatch + report.TopMismatch) > issuesBefore;

            if (wasAdjusted)
                report.WallsFixed++;
            if (hasIssue)
                report.WallsFailed++;
        }

        return report;
    }

    /// <summary>Chỉnh Base Offset trước (nếu cần) rồi Regenerate để đọc lại chân THẬT trước khi tính Top
    /// (Unconnected Height phụ thuộc chân — chỉnh chân trước mà không Regenerate sẽ tính sai chiều cao).
    ///
    /// QUAN TRỌNG: tính giá trị param MỚI bằng CỘNG DỒN delta (= target − Z thật hiện tại, cả 2 đều đọc
    /// từ bounding box/Transform Link, CÙNG 1 hệ toạ độ) vào giá trị param HIỆN TẠI — KHÔNG tính lại từ
    /// đầu kiểu "target − Level.ProjectElevation". Trước đây dùng cách tính lại từ đầu, nhưng
    /// ProjectElevation của Level có thể không khớp tuyệt đối với hệ toạ độ Z dùng cho bounding box (tuỳ
    /// cấu hình Project Base Point/Survey Point của từng file) — sinh ra sai lệch CỐ ĐỊNH (Set() không
    /// lỗi nhưng đích thật lệch 1 khoảng hằng số so với mục tiêu, đúng loại lỗi user báo cáo: gap không
    /// về 0 mà dừng lại 1 khoảng dư cố định). Cộng delta vào giá trị hiện tại không phụ thuộc
    /// ProjectElevation có khớp tuyệt đối hay không — chỉ cần property luôn TRẢ VỀ CÙNG 1 giá trị giữa 2
    /// lần đọc (chắc chắn đúng), nên luôn cho kết quả chính xác.
    ///
    /// Bỏ qua hẳn (không Set) nếu chân/đỉnh đang Attach vào reference khác (xem
    /// Report.SkippedBaseAttached/SkippedTopAttached) — attachment thường ghi đè offset, Set() vẫn
    /// "thành công" (không lỗi) nhưng hình học thật không đổi theo ý muốn. Sau khi Set() còn ĐỌC LẠI
    /// bounding box để xác nhận kết quả THẬT khớp mục tiêu (xem Report.BaseMismatch/TopMismatch) — phòng
    /// trường hợp khác (Join/Cut với phần tử lân cận) cũng làm hình học hiển thị lệch so với tham số vừa
    /// đặt dù không có Attach.</summary>
    private static void ApplyElevation(
        Document doc, Wall wall, double? targetBaseZ, double? targetTopZ, double currentBaseZ, double currentTopZ,
        Report report)
    {
        if (targetBaseZ is { } newBaseZ && Math.Abs(newBaseZ - currentBaseZ) > EpsilonFeet)
        {
            if (wall.get_Parameter(BuiltInParameter.WALL_BOTTOM_IS_ATTACHED)?.AsInteger() == 1)
            {
                report.SkippedBaseAttached++;
            }
            else if (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET) is { IsReadOnly: false } baseOffsetParam)
            {
                var delta = newBaseZ - currentBaseZ;
                baseOffsetParam.Set(baseOffsetParam.AsDouble() + delta);
                doc.Regenerate();
                report.BaseAdjusted++;

                var verifyBaseZ = wall.get_BoundingBox(null)?.Min.Z;
                if (verifyBaseZ is null || Math.Abs(verifyBaseZ.Value - newBaseZ) > MismatchToleranceFeet)
                    report.BaseMismatch++;
            }
        }

        if (targetTopZ is { } newTopZ)
        {
            var freshTopZ = wall.get_BoundingBox(null)?.Max.Z ?? currentTopZ;
            if (Math.Abs(newTopZ - freshTopZ) <= EpsilonFeet)
                return;

            if (wall.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() == 1)
            {
                report.SkippedTopAttached++;
                return;
            }

            var delta = newTopZ - freshTopZ;
            var topLevelId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
            if (topLevelId is { } tid && tid != ElementId.InvalidElementId)
            {
                if (wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET) is { IsReadOnly: false } topOffsetParam)
                {
                    topOffsetParam.Set(topOffsetParam.AsDouble() + delta);
                    report.TopAdjusted++;

                    var verifyTopZ = wall.get_BoundingBox(null)?.Max.Z;
                    if (verifyTopZ is null || Math.Abs(verifyTopZ.Value - newTopZ) > MismatchToleranceFeet)
                        report.TopMismatch++;
                }
            }
            else
            {
                if (wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM) is { IsReadOnly: false } heightParam)
                {
                    heightParam.Set(heightParam.AsDouble() + delta);
                    report.TopAdjusted++;

                    var verifyTopZ = wall.get_BoundingBox(null)?.Max.Z;
                    if (verifyTopZ is null || Math.Abs(verifyTopZ.Value - newTopZ) > MismatchToleranceFeet)
                        report.TopMismatch++;
                }
            }
        }
    }

    /// <summary>3 điều kiện để dùng đáy DẦM thay đáy sàn cho đỉnh tường — xem doc-comment class:
    /// (1) hướng tường gần song song hướng dầm, (2) dầm phủ hết chiều dài tường, (3) bề rộng tường GIAO
    /// với bề rộng THẬT của dầm ít nhất <see cref="BeamWidthOverlapRatio"/> (mặc định 50%) — không bắt
    /// buộc nằm trọn 100% như trước nữa.</summary>
    private static bool WallFitsUnderBeam(
        StructureGeo beam, XYZ wallP0, XYZ wallP1, XYZ wallDirection, double wallLength, double wallWidthFeet, XYZ wallMidpoint)
    {
        if (beam.LineP0 is not { } b0 || beam.LineP1 is not { } b1 || beam.WidthFeet is not { } beamWidthFeet)
            return false;

        var beamDirection = (b1 - b0).Normalize();

        // (1) Song song - so |cos goc| voi nguong, khong quan tam chieu (2 huong nguoc nhau van la
        // "song song" ve mat hinh hoc tuong/dam).
        var cosAngle = Math.Abs(wallDirection.DotProduct(beamDirection));
        if (cosAngle < Math.Cos(ParallelAngleToleranceDeg * Math.PI / 180.0))
            return false;

        // (2) Dam phu het chieu dai tuong - chieu 2 dau dam len truc doc tuong.
        var t0 = (b0 - wallP0).DotProduct(wallDirection);
        var t1 = (b1 - wallP0).DotProduct(wallDirection);
        var beamMin = Math.Min(t0, t1);
        var beamMax = Math.Max(t0, t1);
        if (beamMin > BeamLengthToleranceFeet || beamMax < wallLength - BeamLengthToleranceFeet)
            return false;

        // (3) Be rong tuong GIAO voi be rong dam >= BeamWidthOverlapRatio (mac dinh 50%) - do khoang giao
        // giua 2 doan [wallMin, wallMax] va [beamMin, beamMax] theo phuong vuong goc voi truc dam/tuong,
        // KHONG bat buoc tuong nam TRON trong dam nhu truoc.
        var perpendicular = new XYZ(-beamDirection.Y, beamDirection.X, 0);
        var beamCenterOffset = b0.DotProduct(perpendicular);
        var wallCenterOffset = wallMidpoint.DotProduct(perpendicular);

        var wallMinOffset = wallCenterOffset - wallWidthFeet / 2.0;
        var wallMaxOffset = wallCenterOffset + wallWidthFeet / 2.0;
        var beamMinOffset = beamCenterOffset - beamWidthFeet / 2.0;
        var beamMaxOffset = beamCenterOffset + beamWidthFeet / 2.0;

        var overlapFeet = Math.Min(wallMaxOffset, beamMaxOffset) - Math.Max(wallMinOffset, beamMinOffset);
        return overlapFeet + BeamWidthToleranceFeet >= BeamWidthOverlapRatio * wallWidthFeet;
    }

    /// <summary>true nếu khoảng cách TUYỆT ĐỐI (không quan tâm ứng viên nằm trên hay dưới chân/đỉnh
    /// tường hiện tại — xem doc-comment class) nằm trong [searchMinFeet, searchMaxFeet] người dùng
    /// nhập.</summary>
    private static bool IsWithinSearchRange(double distanceFeet, double searchMinFeet, double searchMaxFeet)
    {
        var abs = Math.Abs(distanceFeet);
        return abs >= searchMinFeet && abs <= searchMaxFeet;
    }

    private static bool XyContains(BoundingBoxXYZ box, XYZ point) =>
        point.X >= box.Min.X && point.X <= box.Max.X && point.Y >= box.Min.Y && point.Y <= box.Max.Y;

    private static bool BoundingBoxesOverlapXY(BoundingBoxXYZ a, BoundingBoxXYZ b, double bufferFeet) =>
        a.Min.X - bufferFeet <= b.Max.X && b.Min.X - bufferFeet <= a.Max.X &&
        a.Min.Y - bufferFeet <= b.Max.Y && b.Min.Y - bufferFeet <= a.Max.Y;

    /// <summary>Đọc bounding box của phần tử Link (toạ độ Link) rồi quy đổi 8 đỉnh sang toạ độ Host qua
    /// <paramref name="transform"/>, lấy min/max mới — an toàn cả khi Link bị xoay (bbox Host có thể hơi
    /// rộng hơn hình thật, đủ dùng cho phép thử "giao nhau XY" sơ bộ ở đây, lọc chính xác hơn dựa vào
    /// WallFitsUnderBeam). Kèm theo 2 đầu Location Line (nếu có) cũng quy đổi sang Host, và bề rộng thật
    /// (nếu <paramref name="computeWidth"/>, chỉ cần cho dầm) đo trực tiếp trên Solid — xem
    /// ComputeWidthFeet.</summary>
    private static StructureGeo? BuildGeo(Element element, Transform transform, bool computeWidth)
    {
        var bbox = element.get_BoundingBox(null);
        if (bbox is null)
            return null;

        var corners = new List<XYZ>(8);
        foreach (var x in new[] { bbox.Min.X, bbox.Max.X })
        foreach (var y in new[] { bbox.Min.Y, bbox.Max.Y })
        foreach (var z in new[] { bbox.Min.Z, bbox.Max.Z })
            corners.Add(transform.OfPoint(new XYZ(x, y, z)));

        var hostBox = new BoundingBoxXYZ
        {
            Min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
            Max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z))
        };

        XYZ? lineP0 = null;
        XYZ? lineP1 = null;
        double? widthFeet = null;
        if (element.Location is LocationCurve { Curve: Line beamLine })
        {
            var localP0 = beamLine.GetEndPoint(0);
            var localP1 = beamLine.GetEndPoint(1);
            lineP0 = transform.OfPoint(localP0);
            lineP1 = transform.OfPoint(localP1);

            if (computeWidth)
                widthFeet = ComputeWidthFeet(element, (localP1 - localP0).Normalize());
        }

        return new StructureGeo { Element = element, HostBox = hostBox, LineP0 = lineP0, LineP1 = lineP1, WidthFeet = widthFeet };
    }

    /// <summary>Đo cao độ THẬT của mặt trên/dưới 1 Floor (HostObject) TẠI ĐÚNG vị trí XY đã cho, thay vì
    /// dùng bounding box toàn cục (Min/Max Z) của cả tấm sàn — bounding box là cực trị TOÀN BỘ mặt bằng
    /// sàn, sai nếu sàn không phẳng tuyệt đối tại đúng vị trí tường (mép dốc thoát nước, dầm bo mép, bậc
    /// cao độ cục bộ...) dẫn tới lệch 1 khoảng cố định — đúng loại lỗi user báo cáo (Set() đúng theo tính
    /// toán nhưng bbox-based target đã sai ngay từ đầu). Bắn 1 tia thẳng đứng (bọc quanh bbox local +
    /// biên an toàn) qua mặt trên/dưới thật của Floor (HostObjectUtils.GetTopFaces/GetBottomFaces) tại
    /// toạ độ XY đã quy đổi NGƯỢC về hệ Link cục bộ (Face.Intersect chỉ hoạt động đúng trong hệ toạ độ
    /// hình học gốc của phần tử) — điểm giao tìm được rồi quy đổi XUÔI lại Host để lấy đúng Z cuối. Trả
    /// về null nếu không đọc được (rơi về bounding box ở nơi gọi, không tệ hơn hành vi cũ).</summary>
    private static double? GetPreciseSurfaceZ(Element element, XYZ hostProbePoint, Transform linkTransform, bool top)
    {
        if (element is not HostObject hostObject)
            return null;

        try
        {
            var localProbe = linkTransform.Inverse.OfPoint(hostProbePoint);
            var bbox = element.get_BoundingBox(null);
            if (bbox is null)
                return null;

            var marginFeet = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);
            var rayLine = Line.CreateBound(
                new XYZ(localProbe.X, localProbe.Y, bbox.Min.Z - marginFeet),
                new XYZ(localProbe.X, localProbe.Y, bbox.Max.Z + marginFeet));

            var faceRefs = top ? HostObjectUtils.GetTopFaces(hostObject) : HostObjectUtils.GetBottomFaces(hostObject);
            foreach (var faceRef in faceRefs)
            {
                if (element.GetGeometryObjectFromReference(faceRef) is not Face face)
                    continue;

                if (face.Intersect(rayLine, out var results) == SetComparisonResult.Overlap &&
                    results is { Size: > 0 })
                    return linkTransform.OfPoint(results.get_Item(0).XYZPoint).Z;
            }
        }
        catch
        {
            // fallback bounding box o noi goi
        }

        return null;
    }

    /// <summary>Bề rộng THẬT của dầm — chiếu MỌI đỉnh của Solid hình học lên phương vuông góc với
    /// <paramref name="localDirection"/> (chiều dài dầm, toạ độ LOCAL của Link — khoảng cách vô hướng
    /// không đổi khi quy đổi sang Host vì Link transform chỉ xoay/tịnh tiến, không co giãn, nên không
    /// cần quy đổi kết quả). Đo trên đỉnh các Edge (đủ chính xác cho tiết diện đa giác thẳng cạnh — phổ
    /// biến với dầm chữ nhật/chữ T thông thường); trả về null nếu không đọc được hình học (dầm rỗng/lỗi
    /// dựng hình) — dầm đó bị loại khỏi ứng viên ưu tiên, tự động rơi về dùng đáy sàn.</summary>
    private static double? ComputeWidthFeet(Element element, XYZ localDirection)
    {
        try
        {
            var perpendicular = new XYZ(-localDirection.Y, localDirection.X, 0);
            double? minT = null;
            double? maxT = null;

            var geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geometry is null)
                return null;

            foreach (var obj in geometry)
            {
                if (obj is not Solid solid || solid.Volume <= 1e-9)
                    continue;

                foreach (Edge edge in solid.Edges)
                {
                    var curve = edge.AsCurve();
                    foreach (var point in new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) })
                    {
                        var t = point.DotProduct(perpendicular);
                        if (minT is null || t < minT)
                            minT = t;
                        if (maxT is null || t > maxT)
                            maxT = t;
                    }
                }
            }

            return minT is not null && maxT is not null ? maxT.Value - minT.Value : null;
        }
        catch
        {
            return null;
        }
    }
}
