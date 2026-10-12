using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceWall;

/// <summary>Tự sửa Line mồ côi thuần hình học bằng cách đo khoảng cách tới Line CAD gần nhất, rồi vẽ thêm 1 Line song song làm cạnh còn lại.</summary>
internal static class WallOrphanRepairer
{
    /// <summary>Kích thước (mm) hình chữ nhật dò tìm ứng viên đo khoảng cách cho 1 Line mồ côi.</summary>
    private const double SearchOffsetMm = 2000.0;

    private static readonly double SearchOffsetFeet = UnitUtils.ConvertToInternalUnits(SearchOffsetMm, UnitTypeId.Millimeters);

    /// <summary>Tỉ lệ chồng lấn tối thiểu khi đo ứng viên — chặt hơn MinOverlapFraction của Pass 1 vì rủi ro đo nhầm cao hơn.</summary>
    private const double CandidateMinOverlapFraction = 0.7;

    /// <summary>Sửa toàn bộ Line mồ côi thành RawWall kèm tên layer gốc — bỏ qua Line nào không tìm được ứng viên hợp lệ.</summary>
    public static List<(RawWall Wall, string LayerName)> Repair(
        IReadOnlyDictionary<string, List<(XYZ A, XYZ B)>> linesByLayer,
        IReadOnlyList<(XYZ A, XYZ B, string LayerName)> orphans,
        double minPairLengthRatio)
    {
        var repaired = new List<(RawWall, string)>();

        foreach (var (a, b, layerName) in orphans)
        {
            if (!CadWallScanner.TryClassifyAxis(a, b, out var isXDirection))
                continue;

            var wall = TryRepairOne(linesByLayer, a, b, layerName, isXDirection, minPairLengthRatio);
            if (wall is not null)
                repaired.Add((wall, layerName));
        }

        return repaired;
    }

    /// <summary>Dò toàn bộ Line trong file quanh (a, b), chọn ứng viên gần nhất theo bề rộng, rồi vẽ Line song song làm cạnh còn lại.</summary>
    private static RawWall? TryRepairOne(
        IReadOnlyDictionary<string, List<(XYZ A, XYZ B)>> linesByLayer,
        XYZ a, XYZ b, string layerName, bool isXDirection, double minPairLengthRatio)
    {
        var rect = BuildSearchRect(a, b, isXDirection, SearchOffsetFeet);
        double? bestWidthFeet = null;
        var bestSign = 1.0;
        var bestScore = double.MaxValue;

        foreach (var segments in linesByLayer.Values)
        {
            foreach (var candidate in segments)
            {
                if (candidate.A.IsAlmostEqualTo(a) && candidate.B.IsAlmostEqualTo(b))
                    continue;

                if (!CadWallScanner.TryClassifyAxis(candidate.A, candidate.B, out var candIsXDirection) || candIsXDirection != isXDirection)
                    continue;

                if (!CadWallScanner.HasCompatiblePairLengths((a, b), candidate, isXDirection, minPairLengthRatio))
                    continue; // Hai cạnh phải có chiều dài tương đương; không cho line ngắn mượn line dài để tạo tường giả.

                if (!BoxesOverlap(rect, candidate.A, candidate.B))
                    continue;

                if (!CadWallScanner.TryGetOverlapPair((a, b), candidate, isXDirection, CandidateMinOverlapFraction, out var perpFeet, out _, out _))
                    continue;

                if (perpFeet < CadWallScanner.MinPairWidthFeet || perpFeet > CadWallScanner.MaxPairWidthFeet)
                    continue;

                var score = perpFeet;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestWidthFeet = perpFeet;
                bestSign = DetermineSign(a, b, candidate.A, candidate.B, isXDirection);
            }
        }

        if (bestWidthFeet is not double widthFeet)
            return null;

        var (syntheticA, syntheticB, midA, midB) = BuildSyntheticPair(a, b, isXDirection, widthFeet, bestSign);

        return new RawWall
        {
            Start = midA,
            End = midB,
            WidthFeet = widthFeet,
            Segments = new List<(XYZ, XYZ)> { (a, b), (syntheticA, syntheticB) },
            IsCrossLayerPair = true,
            CrossLayerNote = $"Tự sửa mồ côi ({layerName}) — theo CAD gần nhất",
        };
    }

    /// <summary>Xác định ứng viên nằm về phía nào (dấu offset) so với Line mồ côi — so sánh toạ độ vuông góc trung bình (Y cho phương X, X cho phương Y), cùng quy ước với TryGetOverlapPair.</summary>
    private static double DetermineSign(XYZ a, XYZ b, XYZ candA, XYZ candB, bool isXDirection)
    {
        var orphanPerp = isXDirection ? (a.Y + b.Y) / 2.0 : (a.X + b.X) / 2.0;
        var candPerp = isXDirection ? (candA.Y + candB.Y) / 2.0 : (candA.X + candB.X) / 2.0;
        return candPerp >= orphanPerp ? 1.0 : -1.0;
    }

    /// <summary>Vẽ Line song song với (a, b), lệch widthFeet*sign theo phương vuông góc (Y cho phương X, X cho phương Y) — làm cạnh còn lại + tim tường (điểm giữa).</summary>
    private static (XYZ SyntheticA, XYZ SyntheticB, XYZ MidA, XYZ MidB) BuildSyntheticPair(
        XYZ a, XYZ b, bool isXDirection, double widthFeet, double sign)
    {
        var offset = widthFeet * sign;
        var half = offset / 2.0;

        if (isXDirection)
            return (
                new XYZ(a.X, a.Y + offset, 0), new XYZ(b.X, b.Y + offset, 0),
                new XYZ(a.X, a.Y + half, 0), new XYZ(b.X, b.Y + half, 0));

        return (
            new XYZ(a.X + offset, a.Y, 0), new XYZ(b.X + offset, b.Y, 0),
            new XYZ(a.X + half, a.Y, 0), new XYZ(b.X + half, b.Y, 0));
    }

    /// <summary>Hình chữ nhật dò ứng viên quanh (a, b) — CHỈ offset theo phương vuông góc (pháp tuyến) với Line, không mở rộng dọc trục: ứng viên phải tự chồng lấn thật với (a, b) theo trục dài (đã có TryGetOverlapPair kiểm tra riêng), mở rộng dọc trục ở đây chỉ làm lọt ứng viên không liên quan nằm xa dọc theo cùng 1 đường.</summary>
    private static (double MinX, double MaxX, double MinY, double MaxY) BuildSearchRect(XYZ a, XYZ b, bool isXDirection, double offsetFeet)
    {
        var minX = Math.Min(a.X, b.X);
        var maxX = Math.Max(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);

        if (isXDirection)
        {
            minY -= offsetFeet;
            maxY += offsetFeet;
        }
        else
        {
            minX -= offsetFeet;
            maxX += offsetFeet;
        }

        return (minX, maxX, minY, maxY);
    }

    private static bool BoxesOverlap((double MinX, double MaxX, double MinY, double MaxY) rect, XYZ a, XYZ b)
    {
        var minX = Math.Min(a.X, b.X);
        var maxX = Math.Max(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);

        return minX <= rect.MaxX && maxX >= rect.MinX && minY <= rect.MaxY && maxY >= rect.MinY;
    }
}


