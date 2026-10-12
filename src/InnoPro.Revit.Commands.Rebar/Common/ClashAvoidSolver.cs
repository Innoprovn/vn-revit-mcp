namespace InnoPro.Revit.Commands.Rebar;

/// <summary>1 vi tri thanh trong set, da quy ra mm tren truc Front. `Index` la BarPositionIndex that
/// (dung de goi MoveBarInSet), KHONG phai thu tu sau khi sap xep.</summary>
internal readonly record struct BarPosition(int Index, double PositionMm);

/// <summary>1 vung cam quanh 1 vật cản: [AnchorMm - RadiusMm, AnchorMm + RadiusMm].</summary>
internal readonly record struct AnchorZone(double AnchorMm, double RadiusMm, string Label);

internal readonly record struct BarChange(int Index, double OldMm, double NewMm)
{
    public double DeltaMm => NewMm - OldMm;
}

/// <summary>`HasEndBarWarning`/`EndBarWarning` = canh bao MEM (khong chan Apply): lien quan toi cay bar
/// dau/cuoi cung (2 thu KHONG BAO GIO bi dich - xem chu thich class). `Ok=false` van la loi CUNG, chan
/// het (vi du con thanh khac roi vung cam, thu tu bi dao...).</summary>
internal readonly record struct SolveResult(
    bool Ok, string Message, IReadOnlyList<BarChange> Changes, bool HasEndBarWarning, string EndBarWarning)
{
    public static SolveResult Fail(string message) =>
        new(false, message, Array.Empty<BarChange>(), false, string.Empty);

    public static SolveResult NoChange() =>
        new(true, string.Empty, Array.Empty<BarChange>(), false, string.Empty);
}

/// <summary>Thuat toan thuan 1D, khong dung Revit API - kiem chung bang mo phong Python truoc khi port
/// sang day (sandbox khong co dotnet SDK de build/test C# that).
///
/// CO CHE MOVE "THEO CAP GAN ANCON" (ban 7 - TH2 CHI CHO PHEP KHI DUNG 1 ANCON/ZONE MOI LAN GOI SOLVE).
/// Gia dinh nen (nguoi dung xac nhan): moi vật cản chi clash voi DUNG 1 thanh thép.
///   - "Cay cuoi cung" cua set = thanh co BarPositionIndex LON NHAT; "cay dau" = thanh co BarPositionIndex
///     NHO NHAT, trong toan bo set (khong chi trong pham vi tim quanh vật cản). CA HAI thanh nay KHONG BAO
///     GIO bi dich boi bat ky buoc nao (TH1 khoi cung, TH2 doi guong) - gia dinh (chua kiem chung voi
///     Revit API that): day la 2 dau vat ly co dinh cua set (vi du: dau/cuoi 1 doan dam/cot), khac voi
///     "cay se moc them khi tang Quantity" tung dung de dinh nghia "cay cuoi cung" o ban truoc - neu sai,
///     doi lai o Solve().
///   - "Phia" cua 1 thanh so voi Anchor = dau cua (ViTriMm - AnchorMm): duong/0 la phia phai, am la
///     phia trai.
///   - TRUONG HOP 1 (thanh clash KHAC phia voi cay cuoi cung): coi doan tu thanh clash den "cay ke
///     cuoi" (thanh ngay ke ben cay cuoi cung, theo huong ve phia cay cuoi cung) la 1 KHOI CUNG - tinh
///     dung 1 luong day de thanh clash thoat ra sat mep vung cam ve dung phia cu cua no, roi cong DUNG
///     luong do cho MOI thanh trong khoi. Neu "cay dau" (dau con lai cua set) roi vao trong pham vi khoi
///     nay (set qua ngan) thi BO QUA rieng no (khong dich), de Verify() tu phat hien neu viec do gay
///     xung dot that su.
///   - TRUONG HOP 2 (thanh clash CUNG phia voi cay cuoi cung): CHI doi guong CA SET (moi thanh TRU ca
///     cay dau lan cay cuoi cung) khi CA HAI dieu kien: (a) lan goi Solve() nay CHI co dung 1 vật cản/zone
///     (`allowMirror`) - tu 2 vật cản tro len cung clash chung 1 set thi KHONG BAO GIO doi guong nua, du
///     dang cung phia, vi doi guong dich CA SET se dam cham/xoa ket qua cac zone khac (yeu cau moi cua
///     nguoi dung: nhieu vật cản/zone thi CHI dung Truong hop 1, luon gioi han trong doan [thanh
///     clash..cay ke cuoi]); VA (b) nguong khoang cach toi da (gapMaxMm) >= 150mm (`MirrorThresholdMm`).
///     Khi doi guong: dich moi thanh (tru dau/cuoi) 1 luong dung bang 2 lan khoang lech hien tai tu
///     thanh clash toi Anchor (doi dau), roi quay lai xu ly y het TRUONG HOP 1. Khong thoa 1 trong 2
///     dieu kien tren thi BO QUA han buoc doi guong, xu ly TRUC TIEP nhu TRUONG HOP 1 (day khoi tu
///     thanh clash den cay ke cuoi) du dang cung phia voi cay cuoi cung.
///   - CANH BAO MEM (khong chan Apply, chi bao + de UI to mau do canh bao) - kiem tra 1 lan sau khi xu
///     ly xong TAT CA vung cam, cho CA HAI dau (dau/cuoi):
///       * Neu chinh cay dau hoac cay cuoi cung roi vao 1 vung cam - khong dich duoc gi ca, chi bao
///         canh bao (bat o ngay trong ApplyZone, luc phat hien clashIdx trung dau/cuoi).
///       * Neu 1 thanh ke ben cay dau/cay cuoi cung THAT SU bi dich boi buoc nay, va sau do khoang cach
///         tu no toi cay dau/cuoi >= nguong khoang cach toi da - bao canh bao (chi kiem tra khi co thay
///         doi that, tranh bao thua cho cac set von da dung khoang cach chuan tu truoc).
///   - LOI CUNG (chan het, khong Apply duoc): bat cu thanh nao KHAC cay dau/cuoi con roi vung cam, thu
///     tu bi dao lon/trung, hoac khoang cach giua 1 cap thanh KHAC (khong dinh lieu cay dau/cuoi) vuot
///     nguong toi da.
///   - Neu 1 vung cam co >1 thanh clash cung luc thi co che nay CHUA xu ly (trai gia dinh nen) - bao
///     loi ro rang thay vi doan mo.</summary>
internal static class ClashAvoidSolver
{
    private const double ToleranceMm = 1e-6;

    /// <summary>TH2 (clash cung phia voi cay cuoi cung) chi doi guong ca set khi nguong khoang cach toi
    /// da (gapMaxMm) >= nguong nay - duoi nguong nay xu ly truc tiep nhu TH1, bo qua doi guong. So 150
    /// la yeu cau cu the cua nguoi dung (khong suy ra tu radius/gap khac), doi truc tiep o day neu can
    /// chinh sau nay.</summary>
    private const double MirrorThresholdMm = 150;

    public static SolveResult Solve(
        IReadOnlyList<BarPosition> positions, double gapMaxMm, IReadOnlyList<AnchorZone> zones, bool allowMirrorOption = true)
    {
        if (positions.Count == 0 || zones.Count == 0)
            return SolveResult.NoChange();

        if (gapMaxMm <= 0)
            return SolveResult.Fail("Khoảng cách tối đa cho phép phải lớn hơn 0.");

        var sorted = positions.OrderBy(p => p.PositionMm).ToList();
        var original = sorted.Select(p => p.PositionMm).ToArray();
        var cur = (double[])original.Clone();

        // Nhieu vật cản cung clash chung 1 set Ligs (nhieu zone tren cung 1 lan goi Solve) -> xu ly LAN
        // LUOT theo BarPositionIndex CUA CHINH THANH CLASH trong moi vung (thanh so thu tu nho truoc,
        // lon sau), KHONG phai theo vi tri Anchor. Xac dinh thanh clash cho MOI zone tren du lieu GOC
        // (truoc khi zone nao chay ca) roi moi sap xep - vi thu tu xu ly lam dich chuyen vi tri, khong
        // the xac dinh lai giua chung. Zone khong tim thay thanh clash nao (hiem, ApplyZone tu bo qua)
        // duoc xep xuong cuoi (fallback theo AnchorMm) de thu tu van xac dinh duoc.
        var orderedZones = zones
            .Select(z => (Zone: z, ClashIndex: FindClashBarIndex(original, sorted, z)))
            .OrderBy(t => t.ClashIndex ?? int.MaxValue)
            .ThenBy(t => t.Zone.AnchorMm)
            .Select(t => t.Zone)
            .ToList();

        // "Cay cuoi cung" / "cay dau" = 2 dau BarPositionIndex cua CA SET (khong phai chi trong pham vi
        // tim quanh vật cản) - gia dinh day la 2 dau vat ly co dinh, KHONG BAO GIO bi dich boi ApplyZone.
        var lastBarIndexValue = positions.Max(p => p.Index);
        var firstBarIndexValue = positions.Min(p => p.Index);
        var lastBarSlot = sorted.FindIndex(p => p.Index == lastBarIndexValue);
        var firstBarSlot = sorted.FindIndex(p => p.Index == firstBarIndexValue);

        var warnings = new List<string>();

        // TH2 (doi guong ca set) CHI cho phep khi ca lan goi Solve() nay chi co DUNG 1 zone (1 vật cản
        // clash voi set nay) - yeu cau moi cua nguoi dung. Tu 2 vật cản tro len cung clash chung 1 set thi
        // LUON dung TH1 (day khoi tu thanh clash den cay ke cuoi), khong bao gio doi guong ca set nua -
        // vi doi guong dich CA SET (tru dau/cuoi) se dung cham/xoa ket qua zone khac da/sap xu ly, con
        // TH1 luon gioi han dung trong doan [thanh clash..cay ke cuoi] nen cac zone khong dam vao nhau.
        var allowMirror = allowMirrorOption && zones.Count == 1;

        // Xu ly TUNG ZONE MOT (theo thu tu so thu tu thanh clash nho truoc) VA KIEM TRA LAI (Verify)
        // NGAY sau MOI zone, khong doi gop het roi moi kiem tra 1 lan cuoi cung nhu truoc - de phat hien
        // DUNG zone nao gay loi, khong de zone sau "che" mat van de zone truoc de lai. Verify() tai moi
        // buoc CHI kiem tra doi voi cac zone DA xu ly xong tinh den luc do (`processedZones`) - zone
        // chua toi luot van con thanh nam trong vung cam cua NO la binh thuong, khong duoc tinh la loi.
        var processedZones = new List<AnchorZone>(orderedZones.Count);
        foreach (var zone in orderedZones)
        {
            var error = ApplyZone(cur, zone, firstBarSlot, lastBarSlot, gapMaxMm, allowMirror, warnings);
            if (error is not null)
                return SolveResult.Fail(error);

            processedZones.Add(zone);

            var stepVerifyError = Verify(cur, processedZones, gapMaxMm, firstBarSlot, lastBarSlot);
            if (stepVerifyError is not null)
            {
                // "Con thanh trong vung cam" / "khoang cach vuot nguong" sau khi dich cung khoi TH1
                // KHONG phai bug - la gioi han hinh hoc cua "dich cung": neu 2 thanh lien ke trong doan
                // bi dich von cach nhau it hon be rong vung cam (2xRadiusMm) thi khong lieu do dich cung
                // nao thoat duoc, hoac cay dau/cuoi bi ket giua 2 thanh khac trong doan bi dich (set qua
                // ngan). Bao ro CHINH XAC zone nao gay loi (dung Verify tai buoc do, khong phai gop het
                // moi kiem tra) cho nguoi dung tu xu ly, khong phai loi solver.
                return SolveResult.Fail(
                    $"Không né được cho {zone.Label} (đã xử lý {processedZones.Count}/{orderedZones.Count} " +
                    "Vật cản trên set này, dừng lại ngay khi phát hiện lỗi) — " + stepVerifyError +
                    ". Nguyên nhân thường gặp: khoảng cách gốc giữa 2 thanh liền kề trong đoạn bị dịch " +
                    "nhỏ hơn bề rộng vùng cấm (2×bán kính né), hoặc cây đầu/cuối bị kẹt giữa đoạn bị dịch " +
                    "(set quá ngắn). Giảm bán kính né/khoảng hở an toàn, tăng khoảng cách tối đa cho phép, " +
                    "hoặc xử lý thủ công cho trường hợp này.");
            }
        }

        // Canh bao mem ve khoang cach tai 2 dau mang - chi kiem tra khi thanh ke ben THAT SU bi dich
        // trong luot nay, tranh bao thua cho cac set von da dung khoang cach chuan tu truoc.
        AddEndGapWarning(cur, original, firstBarSlot, gapMaxMm, "đầu", warnings);
        AddEndGapWarning(cur, original, lastBarSlot, gapMaxMm, "cuối", warnings);

        var changes = new List<BarChange>();
        for (var i = 0; i < cur.Length; i++)
        {
            if (i == firstBarSlot || i == lastBarSlot)
                continue; // cay dau/cuoi cung khong bao gio doi - bo qua du co lech so lam tron nao do

            if (Math.Abs(cur[i] - sorted[i].PositionMm) > ToleranceMm)
                changes.Add(new BarChange(sorted[i].Index, sorted[i].PositionMm, cur[i]));
        }

        var warningText = warnings.Count > 0 ? string.Join(" | ", warnings.Distinct()) : string.Empty;
        return new SolveResult(true, string.Empty, changes, warnings.Count > 0, warningText);
    }

    /// <summary>Xu ly 1 vung cam tren mang `cur` (sua tai cho, cur da sap tang dan). Tra ve null = ok
    /// (co the da them canh bao mem vao `warnings`), khac null = thong diep loi CUNG be tac. Xem chu
    /// thich class de biet co che TH1/TH2 va quy tac "khong bao gio dich cay dau/cuoi".</summary>
    private static string? ApplyZone(
        double[] cur, AnchorZone zone, int firstBarSlot, int lastBarSlot, double gapMaxMm,
        bool allowMirror, List<string> warnings)
    {
        var lo = zone.AnchorMm - zone.RadiusMm;
        var hi = zone.AnchorMm + zone.RadiusMm;

        var affected = new List<int>();
        for (var i = 0; i < cur.Length; i++)
            if (cur[i] > lo && cur[i] < hi)
                affected.Add(i);

        if (affected.Count == 0)
            return null; // khong thanh nao dung vao vung cam nay

        if (affected.Count > 1)
        {
            return $"Vùng cấm quanh {zone.Label} đang có {affected.Count} thanh thép cùng lúc rơi vào " +
                   "trong — cơ chế \"move theo cặp gần vật cản\" hiện chỉ xử lý đúng 1 thanh clash mỗi " +
                   "Vật cản (giả định nền đã thống nhất). Kiểm tra lại bán kính né/khoảng hở, hoặc xử lý " +
                   "thủ công cho trường hợp này.";
        }

        var clashIdx = affected[0];

        if (clashIdx == lastBarSlot || clashIdx == firstBarSlot)
        {
            // Chinh cay dau/cuoi cung dang clash - KHONG duoc phep dich, nen khong lam gi ca, chi canh
            // bao mem de nguoi dung tu xu ly (UI se to mau do canh bao).
            var which = clashIdx == lastBarSlot ? "cuối cùng" : "đầu";
            warnings.Add($"{zone.Label}: chính cây bar {which} của set đang nằm trong vùng cấm — " +
                         "không được tự động dịch, cần kiểm tra/xử lý thủ công.");
            return null;
        }

        var clashSide = Side(cur[clashIdx], zone.AnchorMm);
        var lastSide = Side(cur[lastBarSlot], zone.AnchorMm);

        // TH2 (cung phia) CHI doi guong khi: (a) allowMirror = true (dung 1 vật cản clash 1 set trong lan
        // goi Solve() nay - tu 2 vật cản/zone tro len LUON bo qua, xem chu thich o Solve()), VA (b) nguong
        // khoang cach toi da (gapMaxMm) >= MirrorThresholdMm (150mm, theo yeu cau nguoi dung). Neu khong
        // thoa 1 trong 2 dieu kien, xu ly TRUC TIEP nhu TH1 (bo qua han buoc doi guong) DU clash cung
        // phia voi cay cuoi cung - logic day khoi ben duoi chi dung `clashSide` (khong dung `lastSide`)
        // nen van chay dung huong binh thuong.
        if (clashSide == lastSide && allowMirror && gapMaxMm >= MirrorThresholdMm - ToleranceMm)
        {
            // doi guong CA SET (TRU ca cay dau lan cay cuoi cung) qua phia doi dien cua Anchor (dung
            // khoang lech hien tai tu thanh clash toi Anchor, nhan doi, doi dau) roi xu ly tiep nhu TH1.
            var offsetFromAnchor = cur[clashIdx] - zone.AnchorMm; // dau = clashSide
            var mirrorDelta = -2 * offsetFromAnchor;
            for (var i = 0; i < cur.Length; i++)
                if (i != firstBarSlot && i != lastBarSlot)
                    cur[i] += mirrorDelta;

            clashSide = Side(cur[clashIdx], zone.AnchorMm);
            lastSide = Side(cur[lastBarSlot], zone.AnchorMm);

            if (clashSide == lastSide)
            {
                return $"Đã đổi gương cả set (trừ cây đầu/cuối) quanh {zone.Label} nhưng thanh clash " +
                       "vẫn cùng phía với cây cuối cùng — báo lại cho dev (dữ liệu bất thường so với " +
                       "giả định nền).";
            }
        }

        // TH1 - khac phia (truc tiep, hoac sau khi da doi guong o TH2): day khoi lien tuc tu thanh clash
        // den CAY KE CUOI (khong phai chinh cay cuoi cung) THANH 1 KHOI CUNG - moi thanh trong khoi cong
        // CUNG 1 luong dich, TRU rieng cay dau neu no vo tinh roi vao pham vi khoi nay (set qua ngan).
        var dirToLast = lastBarSlot > clashIdx ? 1 : -1;
        var segEndSlot = lastBarSlot - dirToLast; // cay ke cuoi - khong bao gio dung toi lastBarSlot

        var target = clashSide < 0 ? lo : hi;
        var pushDelta = target - cur[clashIdx];

        var from = Math.Min(clashIdx, segEndSlot);
        var to = Math.Max(clashIdx, segEndSlot);
        for (var i = from; i <= to; i++)
            if (i != firstBarSlot)
                cur[i] += pushDelta;

        return null;
    }

    /// <summary>Phia cua 1 vi tri so voi Anchor: +1 = phai (hoac dung ngay tam, hiem khi xay ra), -1 = trai.</summary>
    private static int Side(double positionMm, double anchorMm)
    {
        var s = Math.Sign(positionMm - anchorMm);
        return s == 0 ? 1 : s;
    }

    /// <summary>Tim BarPositionIndex (so thu tu THAT, khong phai slot sau sap xep) cua thanh dang roi
    /// vao 1 zone, tren mang vi tri GOC (chua bi dich boi zone nao) - dung de sap xep thu tu xu ly cac
    /// zone khi nhieu vật cản cung clash chung 1 set. Tra null neu khong co thanh nao roi vao (hoac >1,
    /// truong hop nay de nguyen cho ApplyZone tu bao loi ro khi thuc su xu ly zone do).</summary>
    private static int? FindClashBarIndex(double[] original, List<BarPosition> sorted, AnchorZone zone)
    {
        var lo = zone.AnchorMm - zone.RadiusMm;
        var hi = zone.AnchorMm + zone.RadiusMm;

        for (var i = 0; i < original.Length; i++)
            if (original[i] > lo && original[i] < hi)
                return sorted[i].Index;

        return null;
    }

    /// <summary>Canh bao mem neu thanh ke ben `endSlot` (dau hoac cuoi mang) THAT SU bi dich trong luot
    /// nay VA sau do khoang cach toi `endSlot` (bat dong) >= nguong toi da. Chi kiem tra khi co thay doi
    /// that (so voi `original`), tranh bao thua cho set von da dung khoang cach chuan.</summary>
    private static void AddEndGapWarning(
        double[] cur, double[] original, int endSlot, double gapMaxMm, string endLabel, List<string> warnings)
    {
        if (cur.Length < 2)
            return;

        var neighbor = endSlot == 0 ? 1 : endSlot == cur.Length - 1 ? cur.Length - 2 : -1;
        if (neighbor < 0)
            return; // endSlot khong nam o 2 dau mang (trai gia dinh nen) - khong doan mo, bo qua

        if (Math.Abs(cur[neighbor] - original[neighbor]) <= ToleranceMm)
            return; // thanh ke ben khong bi dich gi trong luot nay - khong co gi moi de canh bao

        var gap = Math.Abs(cur[endSlot] - cur[neighbor]);
        if (gap >= gapMaxMm - ToleranceMm)
        {
            warnings.Add($"Cây {endLabel} bất động: sau khi dịch, khoảng cách tới thanh kế bên = " +
                         $"{gap:F1}mm ≥ ngưỡng {gapMaxMm:F0}mm — cây {endLabel} không được tự động dịch " +
                         "nên không thể giữ đúng khoảng cách, cần kiểm tra/xử lý thủ công.");
        }
    }

    /// <summary>firstBarSlot/lastBarSlot duoc MIEN kiem tra "con trong vung cam" va "khoang cach vuot
    /// nguong toi da" (2 dieu nay da duoc bao thanh canh bao mem rieng) - nhung VAN bi kiem tra thu tu
    /// (khong duoc dao lon/trung) nhu moi cap khac, vi day la loi hinh hoc thuc su chu khong phai chuyen
    /// "khong dich duoc cay dau/cuoi".</summary>
    private static string? Verify(
        double[] cur, IReadOnlyList<AnchorZone> zones, double gapMaxMm, int firstBarSlot, int lastBarSlot)
    {
        foreach (var zone in zones)
        {
            var lo = zone.AnchorMm - zone.RadiusMm;
            var hi = zone.AnchorMm + zone.RadiusMm;
            for (var i = 0; i < cur.Length; i++)
            {
                if (i == firstBarSlot || i == lastBarSlot)
                    continue; // da xu ly rieng thanh canh bao mem trong ApplyZone

                var p = cur[i];
                if (p > lo + ToleranceMm && p < hi - ToleranceMm)
                    return $"vị trí {p:F1}mm vẫn rơi vào vùng cấm của {zone.Label} [{lo:F1}, {hi:F1}]";
            }
        }

        for (var i = 1; i < cur.Length; i++)
        {
            if (cur[i] <= cur[i - 1] + ToleranceMm)
                return $"thứ tự thanh bị đảo lộn/trùng tại chỉ số {i} ({cur[i - 1]:F1}mm, {cur[i]:F1}mm)";

            var involvesEndBar = i == firstBarSlot || i == lastBarSlot ||
                                  i - 1 == firstBarSlot || i - 1 == lastBarSlot;
            if (!involvesEndBar && cur[i] - cur[i - 1] > gapMaxMm + ToleranceMm)
                return $"khoảng cách {cur[i] - cur[i - 1]:F1}mm giữa chỉ số {i - 1} và {i} vượt ngưỡng {gapMaxMm:F0}mm";
        }

        return null;
    }
}
