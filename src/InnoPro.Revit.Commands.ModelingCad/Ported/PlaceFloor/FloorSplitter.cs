using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Chia 1 Floor đang được chọn bằng Boolean Intersect tuần tự (kiểu BSP) — lấy bounding box của sàn, quét TẤT CẢ line tim dầm (OwnedLineStyle) trong view có bounding box giao với sàn, sắp xếp dài→ngắn rồi cắt lần lượt từng line, xoá sàn gốc và tạo lại từng mảnh — chỉ giữ Type của sàn gốc, Level/Offset lấy theo view đang mở + 0.</summary>
internal static class FloorSplitter
{
    /// <summary>Thể tích tối thiểu 1 mảnh sau Boolean để coi là thật (loại mảnh sinh do sai số hình học gần như phẳng/0).</summary>
    private static readonly double MinPieceVolumeFeet3 =
        UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters) *
        UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters) * 0.5;

    private static readonly double MarginFeet = UnitUtils.ConvertToInternalUnits(5000.0, UnitTypeId.Millimeters);

    public static SplitSummary Split(Document doc, ViewPlan view, Level level, Floor floor)
    {
        var top = FloorSolidHelper.GetTopLoops(floor);
        if (top is null)
            return new SplitSummary(false, 0, new List<string> { $"Floor #{floor.Id.Value}: không đọc được biên hình học." });

        BoundingBoxXYZ? floorBb;
        try
        {
            floorBb = floor.get_BoundingBox(null);
        }
        catch (Exception)
        {
            floorBb = null;
        }

        if (floorBb is null)
            return new SplitSummary(false, 0, new List<string> { $"Floor #{floor.Id.Value}: không đọc được bounding box." });

        var style = OwnedLineStyle.FindExisting(doc);
        if (style is null)
            return new SplitSummary(false, 0, new List<string> { "Chưa vẽ line tim dầm/vách nào — bấm \"Tạo đường bao sàn\" trước." });

        // Quet TAT CA line tim dam co bounding box giao voi bounding box san, sap xep dai->ngan de cat truoc.
        var cutters = OwnedLineStyle.FindOwned(doc, view, style.Id)
            .Select(ce => ce.GeometryCurve as Line)
            .Where(l => l is not null)
            .Select(l => l!)
            .Where(l => BoundingBoxesOverlap(floorBb, l.GetEndPoint(0), l.GetEndPoint(1)))
            .OrderByDescending(l => l.GetEndPoint(0).DistanceTo(l.GetEndPoint(1)))
            .ToList();

        if (cutters.Count == 0)
            return new SplitSummary(false, 0, new List<string> { "Không có line tim dầm/vách nào giao với bounding box của sàn." });

        var baseSolid = FloorSolidHelper.BuildHelperSolid(top.Value.Loops);
        if (baseSolid is null)
            return new SplitSummary(false, 0, new List<string> { $"Floor #{floor.Id.Value}: lỗi dựng solid tạm." });

        var pieces = SplitBySequentialCutters(baseSolid, cutters, top.Value.TopZ);
        if (pieces is null || pieces.Count < 2)
            return new SplitSummary(false, 0, new List<string> { $"Floor #{floor.Id.Value}: không tách được bằng line hiện có." });

        var floorTypeId = floor.FloorType.Id;
        var levelElevation = level.Elevation;
        var piecesCreated = 0;
        var errors = new List<string>();


        doc.Delete(floor.Id);

        foreach (var piece in pieces)
        {
            var loops = FloorSolidHelper.ExtractTopLoops(piece);
            if (loops.Count == 0)
                continue;

            try
            {
                var loopsAtLevel = loops.Select(l => TranslateLoopToZ(l, levelElevation)).ToList();
                Floor.Create(doc, loopsAtLevel, floorTypeId, level.Id, true, null, 0.0);
                piecesCreated++;
            }
            catch (Exception ex)
            {
                errors.Add($"1 mảnh bị lỗi khi tạo lại: {ex.Message}");
            }
        }


        return new SplitSummary(piecesCreated > 0, piecesCreated, errors);
    }

    private static bool BoundingBoxesOverlap(BoundingBoxXYZ floorBb, XYZ p0, XYZ p1)
    {
        var lineMinX = Math.Min(p0.X, p1.X);
        var lineMaxX = Math.Max(p0.X, p1.X);
        var lineMinY = Math.Min(p0.Y, p1.Y);
        var lineMaxY = Math.Max(p0.Y, p1.Y);

        return lineMaxX >= floorBb.Min.X && lineMinX <= floorBb.Max.X &&
               lineMaxY >= floorBb.Min.Y && lineMinY <= floorBb.Max.Y;
    }

    /// <summary>Áp dụng tuần tự từng cutter theo thứ tự đã sắp (dài→ngắn, kiểu BSP) — mỗi bước tách MỌI mảnh hiện có thành 2 nửa qua Boolean Intersect, dừng và trả về null nếu 1 bước không tách được mảnh nào thành 2.</summary>
    private static List<Solid>? SplitBySequentialCutters(Solid baseSolid, List<Line> cutters, double topZ)
    {
        var pieces = new List<Solid> { baseSolid };

        foreach (var cutter in cutters)
        {
            var (left, right) = BuildHalfPlaneSolids(cutter, topZ);
            if (left is null || right is null)
                return null;

            var nextPieces = new List<Solid>();
            foreach (var piece in pieces)
            {
                TryIntersect(piece, left, nextPieces);
                TryIntersect(piece, right, nextPieces);
            }

            if (nextPieces.Count < 2)
                return null;

            pieces = nextPieces;
        }

        return pieces;
    }

    private static void TryIntersect(Solid a, Solid b, List<Solid> output)
    {
        try
        {
            var result = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
            if (result is not null && result.Volume > MinPieceVolumeFeet3)
                output.Add(result);
        }
        catch (Exception)
        {
            // Mieng qua nho/suy bien sau Boolean - bo qua rieng mieng nay.
        }
    }

    /// <summary>Dựng 2 solid mỏng đại diện 2 nửa mặt phẳng đối xứng qua đường cutter, đủ lớn phủ hết mọi mảnh hiện có (margin cố định 5m quanh đường cắt).</summary>
    private static (Solid? Left, Solid? Right) BuildHalfPlaneSolids(Line cutter, double topZ)
    {
        var p0 = new XYZ(cutter.GetEndPoint(0).X, cutter.GetEndPoint(0).Y, topZ);
        var p1 = new XYZ(cutter.GetEndPoint(1).X, cutter.GetEndPoint(1).Y, topZ);
        var dirRaw = p1 - p0;
        if (dirRaw.IsZeroLength())
            return (null, null);

        var dir = dirRaw.Normalize();
        var perp = new XYZ(-dir.Y, dir.X, 0);
        var extStart = p0 - dir * MarginFeet;
        var extEnd = p1 + dir * MarginFeet;

        return (BuildHalfPlaneSolid(extStart, extEnd, perp, 1.0), BuildHalfPlaneSolid(extStart, extEnd, perp, -1.0));
    }

    private static Solid? BuildHalfPlaneSolid(XYZ extStart, XYZ extEnd, XYZ perp, double sign)
    {
        try
        {
            var c = extEnd + perp * MarginFeet * sign;
            var d = extStart + perp * MarginFeet * sign;

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(extStart, extEnd));
            loop.Append(Line.CreateBound(extEnd, c));
            loop.Append(Line.CreateBound(c, d));
            loop.Append(Line.CreateBound(d, extStart));

            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, 1.0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Dịch cả loop về đúng cao độ Z (elevation của Level đang mở) — loop đọc từ solid kết quả Boolean nằm ở Z thật của sàn gốc.</summary>
    private static CurveLoop TranslateLoopToZ(CurveLoop loop, double z)
    {
        var first = loop.First();
        var translate = Transform.CreateTranslation(new XYZ(0, 0, z - first.GetEndPoint(0).Z));

        var result = new CurveLoop();
        foreach (var curve in loop)
            result.Append(curve.CreateTransformed(translate));

        return result;
    }
}

/// <summary>Kết quả 1 lượt "Chia sàn".</summary>
internal sealed record SplitSummary(bool Success, int PiecesCreated, List<string> Errors);
