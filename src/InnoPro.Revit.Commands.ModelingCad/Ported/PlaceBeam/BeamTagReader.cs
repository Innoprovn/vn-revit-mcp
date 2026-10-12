using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceBeam;

/// <summary>Gán tên+kích thước dầm đọc từ Text Note thật gần đó cho từng RawBeam (hoặc 1 đoạn Line đơn lẻ).</summary>
internal static class BeamTagReader
{
    /// <summary>Giá trị mặc định gợi ý sẵn trong ô nhập khoảng cách của UI.</summary>
    internal const double DefaultSearchExpandMm = 1000.0;

    /// <summary>Nhận diện "số x số" (b x h) — vd "220x600", không đòi hỏi bọc trong ngoặc.</summary>
    private static readonly Regex SizePattern = new(@"(?<b>\d+)\s*[xX×\*]\s*(?<h>\d+)", RegexOptions.Compiled);

    /// <summary>Trọng số phạt khoảng cách vuông góc khi chấm điểm Text ứng viên gần 1 dầm.</summary>
    private const double PerpDistanceWeight = 3.0;

    /// <summary>Đơn vị làm tròn (mm) khi so khớp b đọc từ Text với b đo hình học CAD — giống SnapUnitMm của
    /// BeamPlacer, đảm bảo "220" (Text) khớp "219.7" (đo CAD) mà không cần đúng tuyệt đối từng mm.</summary>
    private const double WidthSnapUnitMm = 5.0;

    /// <summary>Gán TagBMm/TagHMm/TagName cho từng RawBeam bằng Text Note gần nhất có b khớp bề rộng đo hình học — TagName chỉ gán khi Text có tên, TagBMm/TagHMm vẫn gán dù Text không tên (vd "220x600" trần).</summary>
    public static void AttachTags(IEnumerable<RawBeam> beams, IReadOnlyList<TextNote> textNotes, double expandFeet)
    {
        var candidates = ParseCandidates(textNotes);
        if (candidates.Count == 0)
            return;

        foreach (var beam in beams)
        {
            var geometricBMm = UnitUtils.ConvertFromInternalUnits(beam.WidthFeet, UnitTypeId.Millimeters);
            var (name, bMm, hMm) = FindNearestTag(beam.Start, beam.End, candidates, expandFeet, geometricBMm);
            if (bMm is null)
                continue;

            beam.TagBMm = bMm;
            beam.TagHMm = hMm;
            if (!string.IsNullOrEmpty(name))
                beam.TagName = name;
        }
    }

    private static double RoundToWidthSnap(double valueMm) =>
        Math.Round(valueMm / WidthSnapUnitMm, MidpointRounding.AwayFromZero) * WidthSnapUnitMm;

    /// <summary>Tìm Text gần 1 đoạn Start-End đơn lẻ — dùng cho Line mồ côi chưa ghép thành RawBeam
    /// (BeamOrphanRepairer). Parse lại toàn bộ Text mỗi lần gọi, chấp nhận vì chỉ chạy cho số ít Line mồ côi.</summary>
    public static (string? Name, double? BMm, double? HMm) FindNearestTag(
        XYZ start, XYZ end, IReadOnlyList<TextNote> textNotes, double expandFeet) =>
        FindNearestTag(start, end, ParseCandidates(textNotes), expandFeet);

    /// <summary>Đọc + parse toàn bộ TextNote có dạng "TÊN...bxh" thành danh sách ứng viên (tâm BoundingBox + tên + b + h).</summary>
    internal static List<(XYZ Center, string Name, double BMm, double HMm)> ParseCandidates(IReadOnlyList<TextNote> textNotes)
    {
        var candidates = new List<(XYZ Center, string Name, double BMm, double HMm)>();
        foreach (var note in textNotes)
        {
            var text = note.Text;
            if (string.IsNullOrEmpty(text))
                continue;

            // Gop nhieu dong + doi non-breaking space ve space thuong truoc khi so khop.
            text = text.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Replace(' ', ' ').Trim();
            if (text.Length == 0)
                continue;

            var matches = SizePattern.Matches(text);
            if (matches.Count == 0)
                continue;

            // Lay ket qua CUOI CUNG (dau x gan cuoi chuoi nhat, doc ngươc phai sang trai) - tranh bat nham chu
            // "X" nam trong ten (vd "D6-X7A") lam dau phan cach b/h.
            var sizeMatch = matches[^1];
            var name = text[..sizeMatch.Index].Trim(' ', '(', '[', '-', ':', '\t'); // co the rong (Text chi co "bxh" tran) - van nhan, chi khong du de gan TagName sau nay.

            if (!double.TryParse(sizeMatch.Groups["b"].Value, out var bMm) ||
                !double.TryParse(sizeMatch.Groups["h"].Value, out var hMm))
                continue;

            var box = note.get_BoundingBox(null);
            var center = box is null
                ? note.Coord
                : new XYZ((box.Min.X + box.Max.X) / 2.0, (box.Min.Y + box.Max.Y) / 2.0, 0);

            candidates.Add((new XYZ(center.X, center.Y, 0), name, bMm, hMm));
        }

        return candidates;
    }

    /// <summary>Chấm điểm + chọn ứng viên Text tốt nhất trong vùng mở rộng quanh 1 đoạn Start-End — điểm số phạt
    /// nặng khoảng cách vuông góc hơn độ lệch dọc trục so với trung điểm, tránh bắt nhầm Text của Line song song
    /// sát bên. <paramref name="requiredBMm"/> khác null (AttachTags gọi với b đo hình học của chính dầm đang
    /// xét) thì LOẠI HẲN candidate có b không khớp (sau khi làm tròn bội WidthSnapUnitMm) trước khi chấm điểm vị
    /// trí — null (BeamOrphanRepairer gọi cho 1 Line mồ côi chưa biết b) thì không lọc, chọn thuần theo vị trí
    /// như cũ.</summary>
    internal static (string? Name, double? BMm, double? HMm) FindNearestTag(
        XYZ start, XYZ end, List<(XYZ Center, string Name, double BMm, double HMm)> candidates, double expandFeet,
        double? requiredBMm = null)
    {
        if (candidates.Count == 0)
            return (null, null, null);

        var flatStart = new XYZ(start.X, start.Y, 0);
        var flatEnd = new XYZ(end.X, end.Y, 0);
        var direction = flatEnd - flatStart;
        var length = direction.GetLength();
        var hasAxis = length >= 1e-9;
        var dirNorm = hasAxis ? direction / length : XYZ.BasisX;
        var perpNorm = new XYZ(-dirNorm.Y, dirNorm.X, 0);

        var requiredBRounded = requiredBMm is double target ? RoundToWidthSnap(target) : (double?)null;

        string? bestName = null;
        double bestB = 0, bestH = 0, bestScore = double.MaxValue;

        foreach (var (center, name, bMm, hMm) in candidates)
        {
            if (requiredBRounded is double reqB && Math.Abs(RoundToWidthSnap(bMm) - reqB) > 1e-6)
                continue; // b Text khac b do hinh hoc (da lam tron) - khong nhan, tranh bat nham Text dam khac.

            if (!IsInExpandedRect(start, end, center, expandFeet))
                continue;

            double score;
            if (hasAxis)
            {
                var toPoint = center - flatStart;
                var alongDist = toPoint.DotProduct(dirNorm);
                var perpDist = Math.Abs(toPoint.DotProduct(perpNorm));
                score = perpDist * PerpDistanceWeight + Math.Abs(alongDist - length / 2.0);
            }
            else
            {
                score = center.DistanceTo(flatStart);
            }

            if (score >= bestScore)
                continue;

            bestScore = score;
            bestName = name;
            bestB = bMm;
            bestH = hMm;
        }

        return bestName is null ? (null, null, null) : (bestName, bestB, bestH);
    }

    /// <summary>Đệm nhỏ dọc trục (mm) bù sai số label đặt hơi lệch ra ngoài đúng 2 đầu dầm — nhỏ hơn nhiều so với expandFeet (vuông góc) để không bắt nhầm dầm khác dọc cùng trục.</summary>
    private static readonly double AlongAxisAllowanceFeet = UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    /// <summary>Kiểm tra điểm có nằm trong hình chữ nhật quanh Start→End hay không — mở rộng expandFeet theo phương vuông góc, AlongAxisAllowanceFeet (nhỏ) theo dọc trục.</summary>
    private static bool IsInExpandedRect(XYZ start, XYZ end, XYZ point, double expandFeet)
    {
        var dx = Math.Abs(end.X - start.X);
        var dy = Math.Abs(end.Y - start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);

        var minX = Math.Min(start.X, end.X);
        var maxX = Math.Max(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y);
        var maxY = Math.Max(start.Y, end.Y);

        if (length < 1e-9)
        {
            // Dam suy bien thanh 1 diem - fallback mo rong ca 2 phuong quanh Start.
            minX -= expandFeet; maxX += expandFeet;
            minY -= expandFeet; maxY += expandFeet;
        }
        else if (dx >= dy)
        {
            // phuong X - mo rong Y theo expandFeet, X theo dem nho AlongAxisAllowanceFeet.
            minY -= expandFeet;
            maxY += expandFeet;
            minX -= AlongAxisAllowanceFeet;
            maxX += AlongAxisAllowanceFeet;
        }
        else
        {
            // phuong Y - mo rong X theo expandFeet, Y theo dem nho AlongAxisAllowanceFeet.
            minX -= expandFeet;
            maxX += expandFeet;
            minY -= AlongAxisAllowanceFeet;
            maxY += AlongAxisAllowanceFeet;
        }

        return point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY;
    }
}
