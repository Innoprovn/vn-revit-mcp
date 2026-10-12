using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFromCad;

/// <summary>Dựng Wall từ WallBuildPlan đã người dùng xác nhận trong cửa sổ. Toàn bộ trong 1
/// transaction — lỗi từng đoạn không làm hỏng các đoạn khác (bắt riêng từng lỗi, gộp báo cáo).
///
/// SAU KHI dựng xong, NẾU người dùng có chọn Dimension Type (không bắt buộc — xem
/// WallFromCadWindow), TỰ TẠO Dimension THẬT (annotation, không di chuyển tường) từ MẶT tường gần
/// nhất tới Grid THẬT của Revit song song với tường (xem CreateGridDimension) — mục đích để người
/// dùng TỰ NHÌN THẤY VÀ TỰ QUYẾT ĐỊNH khoảng cách nào cần chỉnh (toạ độ CAD gốc thường lẻ
/// 0.1mm/0.3mm/0.001mm nên khoảng cách đo được có thể ra số lẻ kiểu "360.583"). KHÔNG còn tự động
/// DI CHUYỂN tường ở đây nữa (bản trước có làm, đã bỏ theo yêu cầu) — việc "chỉnh số lẻ" (di chuyển
/// tường cho khớp Grid) nay là 1 tool RIÊNG (xem Tools/Modeling/SnapWallToGrid), tách khỏi tool vẽ
/// tường để "Vẽ tường từ CAD" chạy nhanh hơn và không gộp 2 việc khác nhau vào 1 nút. CHỈ tạo được
/// Dimension khi tường THẲNG (Location Curve là Line) VÀ có ít nhất 1 Grid thẳng ĐỦ SONG SONG (trong
/// dung sai GridParallelToleranceDegrees) — tường XIÊN (không song song Grid nào) không có khái niệm
/// "DIM tới Grid" kiểu này, bỏ qua, không tạo Dimension cho tường đó.</summary>
internal static class WallBuilder
{
    /// <summary>Chiều cao khởi tạo tạm khi Top Constraint = "Đến cốt" — sẽ bị ghi đè ngay bằng
    /// WALL_HEIGHT_TYPE/WALL_TOP_OFFSET nên giá trị này không ảnh hưởng kết quả cuối.</summary>
    private const double PlaceholderHeightMm = 3000.0;

    /// <summary>Dung sai góc (độ) để coi 1 Grid là "song song" với tường — dùng để lọc Grid hợp lệ khi
    /// chọn Grid để tạo Dimension, đủ chặt để loại tường xiên thật sự, đủ rộng để chấp nhận sai số
    /// dựng hình nhỏ.</summary>
    private const double GridParallelToleranceDegrees = 2.0;

    /// <summary>Khoảng kéo dài thêm mỗi đầu đường Dimension (ngoài khoảng Grid↔tường thật) để witness
    /// line không nằm sát mép, dễ nhìn hơn trên bản vẽ.</summary>
    private static readonly double DimensionLinePaddingFeet =
        UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    internal sealed class Report
    {
        public int Created { get; set; }
        public int SkippedNoType { get; set; }
        public int Failed { get; set; }
        public int GridDimensionsCreated { get; set; }
        public List<string> Errors { get; } = new();

        /// <summary>1 dòng chẩn đoán cho MỖI tường (khi có chọn Dimension Type) — Grid nào dùng để so,
        /// đã tạo Dimension được hay không, hoặc lý do bỏ qua CỤ THỂ (kèm góc lệch gần nhất nếu không
        /// tìm được Grid đủ song song) — để biết CHÍNH XÁC nguyên nhân khi 1 tường không có Dimension
        /// như mong đợi, thay vì phải đoán qua ảnh chụp màn hình.</summary>
        public List<string> GridDimensionNotes { get; } = new();
    }

    internal static Report Build(
        Document doc, View view, WallBuildPlan plan, IReadOnlyList<Grid> grids, DimensionType? dimensionType)
    {
        var report = new Report();
        var createdWalls = new List<Wall>();


        foreach (var bucket in plan.Buckets)
        {
            if (bucket.SelectedWallType is null)
            {
                report.SkippedNoType += bucket.Candidates.Count;
                continue;
            }

            foreach (var candidate in bucket.Candidates)
            {
                try
                {
                    var wall = CreateWall(doc, candidate, bucket.SelectedWallType, plan.Options);
                    report.Created++;
                    createdWalls.Add(wall);
                }
                catch (Exception ex)
                {
                    report.Failed++;
                    if (report.Errors.Count < 8 && !report.Errors.Contains(ex.Message))
                        report.Errors.Add(ex.Message);
                }
            }
        }

        if (dimensionType is not null && createdWalls.Count > 0)
        {
            // Regenerate truoc de Wall Join tu dong giua cac tuong cham goc nhau on dinh - Dimension
            // tao ra dua tren vi tri THAT sau cung cua tuong, khong phai vi tri ngay luc vua Wall.Create.
            doc.Regenerate();
            foreach (var wall in createdWalls)
            {
                var (created, note) = CreateGridDimension(doc, view, wall, grids, dimensionType);
                report.GridDimensionNotes.Add(note);
                if (created)
                    report.GridDimensionsCreated++;
            }
        }

        return report;
    }

    private static Wall CreateWall(Document doc, WallCandidate candidate, WallType wallType, WallBuildOptions options)
    {
        var centerline = FlattenToElevation(candidate.Centerline, options.BaseLevel.Elevation);

        var initialHeight = options.TopMode == TopConstraintMode.Unconnected
            ? MmToFeet(options.UnconnectedHeightMm)
            : MmToFeet(PlaceholderHeightMm);

        var wall = Wall.Create(
            doc, centerline, wallType.Id, options.BaseLevel.Id,
            initialHeight, MmToFeet(options.BaseOffsetMm), false, false);

        doc.Regenerate();

        if (options.TopMode == TopConstraintMode.UpToLevel && options.TopLevel is not null)
        {
            wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(options.TopLevel.Id);
            wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(MmToFeet(options.TopOffsetMm));
        }

        ApplyLocationLine(doc, wall, wallType, options.LocationLine);
        return wall;
    }

    /// <summary>Xem ghi chú cơ chế đầy đủ trong WallCoreOffsets — tạo bằng đường tim (luôn đúng vì
    /// Wall Type luôn khớp chính xác bề rộng CAD), rồi nếu người dùng chọn Location Line khác Wall
    /// Centerline thì set tham số + dịch bù lại để tường không bị xê dịch khỏi đúng vị trí CAD.</summary>
    private static void ApplyLocationLine(Document doc, Wall wall, WallType wallType, WallLocationLine locationLine)
    {
        if (locationLine == WallLocationLine.WallCenterline)
            return;

        var orientation = wall.Orientation;

        wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)locationLine);
        doc.Regenerate();

        var offsetFeet = WallCoreOffsets.GetReferenceOffsetFeet(wallType, locationLine);
        if (Math.Abs(offsetFeet) > 1e-9)
            ElementTransformUtils.MoveElement(doc, wall.Id, orientation.Multiply(offsetFeet));
    }

    /// <summary>Nâng đường tim (Line hoặc Arc, đang ở Z=0 sau khi quét CAD) lên đúng cao độ Base Level.
    /// Với Arc: dựng lại bằng 3 điểm đầu/giữa/cuối đã nâng Z — giữ nguyên hình dạng (tâm/bán kính suy
    /// ra lại đúng như cũ vì chỉ tịnh tiến theo Z), Wall.Create nhận Curve nên tường cong tạo được y hệt
    /// tường thẳng, không cần code dựng Wall riêng.</summary>
    private static Curve FlattenToElevation(Curve curve, double elevationFeet)
    {
        switch (curve)
        {
            case Line line:
                return Line.CreateBound(
                    WithZ(line.GetEndPoint(0), elevationFeet),
                    WithZ(line.GetEndPoint(1), elevationFeet));

            case Arc arc:
                return Arc.Create(
                    WithZ(arc.GetEndPoint(0), elevationFeet),
                    WithZ(arc.GetEndPoint(1), elevationFeet),
                    WithZ(arc.Evaluate(0.5, true), elevationFeet));

            default:
                throw new InvalidOperationException(
                    $"Không hỗ trợ dựng tường từ đường tim loại '{curve.GetType().Name}'.");
        }
    }

    private static XYZ WithZ(XYZ point, double z) => new(point.X, point.Y, z);

    private static double MmToFeet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    /// <summary>Tạo 1 Dimension THẬT (annotation) từ MẶT tường gần Grid nhất tới Grid SONG SONG gần
    /// nhất — xem doc-comment lớp. Trả về (đã tạo được hay không, chuỗi chẩn đoán) — LUÔN trả note dù
    /// tạo được hay không, để biết CHÍNH XÁC lý do khi 1 tường không có Dimension như mong đợi.</summary>
    private static (bool Created, string Note) CreateGridDimension(
        Document doc, View view, Wall wall, IReadOnlyList<Grid> grids, DimensionType dimensionType)
    {
        var location = DescribeWallLocation(wall);

        if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line wallLine)
            return (false, $"{location}: bỏ qua — tường không phải Location Line (Arc?).");

        var wallP0 = wallLine.GetEndPoint(0);
        var wallP1 = wallLine.GetEndPoint(1);
        var wallDirection = (wallP1 - wallP0).Normalize();
        var normal = new XYZ(-wallDirection.Y, wallDirection.X, 0);
        var wallCenterN = wallP0.DotProduct(normal);

        var nearestGrid = FindNearestParallelGrid(
            wallDirection, normal, wallCenterN, grids,
            out var gridN, out var closestAngleDegrees, out var closestGridName);

        if (nearestGrid is null)
        {
            return grids.Count == 0
                ? (false, $"{location}: bỏ qua — model không có Grid nào.")
                : (false, $"{location}: bỏ qua — không Grid nào đủ song song (gần nhất lệch " +
                          $"{closestAngleDegrees:0.0}° so với Grid \"{closestGridName}\", vượt dung sai " +
                          $"{GridParallelToleranceDegrees}°).");
        }

        var nearFaceReference = TryGetNearFaceReference(wall, normal, gridN);
        if (nearFaceReference is null)
            return (false, $"{location}: bỏ qua — không đọc được mặt tường (Wall không có Side Face).");

        try
        {
            var references = new ReferenceArray();
            references.Append(nearFaceReference);
            references.Append(new Reference(nearestGrid));

            var wallMidpoint = (wallP0 + wallP1) / 2.0;
            var startN = Math.Min(wallCenterN, gridN) - DimensionLinePaddingFeet;
            var endN = Math.Max(wallCenterN, gridN) + DimensionLinePaddingFeet;
            var dimLine = Line.CreateBound(
                wallMidpoint + normal.Multiply(startN - wallCenterN),
                wallMidpoint + normal.Multiply(endN - wallCenterN));

            doc.Create.NewDimension(view, dimLine, references, dimensionType);
            return (true, $"{location}: đã tạo Dimension tới Grid \"{nearestGrid.Name}\".");
        }
        catch (Exception ex)
        {
            return (false, $"{location}: lỗi khi tạo Dimension tới Grid \"{nearestGrid.Name}\" — {ex.Message}");
        }
    }

    /// <summary>Trong 2 mặt tường (Exterior/Interior — xem HostObjectUtils.GetSideFaces), chọn mặt GẦN
    /// Grid hơn (so vị trí thật của từng mặt, chiếu lên <paramref name="normal"/>, với toạ độ Grid
    /// <paramref name="gridN"/>) — đây đúng mặt người dùng sẽ ghi Dimension thật trên bản vẽ. Trả về
    /// null nếu Wall không đọc được Side Face nào (family/loại tường không chuẩn).</summary>
    private static Reference? TryGetNearFaceReference(Wall wall, XYZ normal, double gridN)
    {
        try
        {
            var faceRefs = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior)
                .Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior))
                .ToList();

            Reference? best = null;
            var bestDistance = double.MaxValue;

            foreach (var faceRef in faceRefs)
            {
                if (wall.GetGeometryObjectFromReference(faceRef) is not PlanarFace face)
                    continue;

                var faceN = face.Origin.DotProduct(normal);
                var distance = Math.Abs(faceN - gridN);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = faceRef;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nhãn ngắn định vị 1 Wall theo toạ độ điểm đầu Location Curve (mm, làm tròn) — dùng để
    /// người dùng đối chiếu dòng chẩn đoán GridDimensionNotes với đúng tường nào trên mặt bằng, vì Wall
    /// không có "tên" tự nhiên như DoorNameGroup bên PlaceDoorWindow.</summary>
    private static string DescribeWallLocation(Wall wall)
    {
        if (wall.Location is not LocationCurve locationCurve)
            return "Tường";

        var p = locationCurve.Curve.GetEndPoint(0);
        var xMm = Math.Round(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Millimeters));
        var yMm = Math.Round(UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Millimeters));
        return $"Tường tại ({xMm}, {yMm})";
    }

    /// <summary>Tìm Grid THẲNG (bỏ qua Grid cung, hiếm gặp) SONG SONG với phương tường (trong dung sai
    /// GridParallelToleranceDegrees) mà toạ độ vuông góc (chiếu lên <paramref name="normal"/>) GẦN
    /// <paramref name="wallCenterN"/> nhất — đây đúng là Grid người dùng sẽ dùng để ghi Dimension
    /// khoảng cách từ Grid tới tường này trên bản vẽ thật. Trả về null nếu không Grid nào đủ song song
    /// (tường xiên, không thẳng hàng với hệ Grid) — kèm theo <paramref name="closestAngleDegrees"/>/
    /// <paramref name="closestGridName"/>: góc lệch NHỎ NHẤT tìm được (dù không đạt dung sai) và tên
    /// Grid tương ứng, để dòng chẩn đoán biết chính xác mức độ lệch thay vì chỉ báo "không tìm thấy".</summary>
    private static Grid? FindNearestParallelGrid(
        XYZ wallDirection, XYZ normal, double wallCenterN, IReadOnlyList<Grid> grids,
        out double gridN, out double closestAngleDegrees, out string closestGridName)
    {
        var parallelCosTolerance = Math.Cos(GridParallelToleranceDegrees * Math.PI / 180.0);

        Grid? best = null;
        var bestDistance = double.MaxValue;
        gridN = 0.0;

        var smallestAngleDegrees = double.MaxValue;
        closestGridName = "?";

        foreach (var grid in grids)
        {
            if (grid.Curve is not Line gridLine)
                continue;

            var gridP0 = gridLine.GetEndPoint(0);
            var gridDirection = (gridLine.GetEndPoint(1) - gridP0).Normalize();

            var cos = Math.Abs(wallDirection.DotProduct(gridDirection));
            var angleDegrees = Math.Acos(Math.Min(1.0, cos)) * 180.0 / Math.PI;
            if (angleDegrees < smallestAngleDegrees)
            {
                smallestAngleDegrees = angleDegrees;
                closestGridName = grid.Name;
            }

            if (cos < parallelCosTolerance)
                continue;

            var candidateGridN = gridP0.DotProduct(normal);
            var distance = Math.Abs(candidateGridN - wallCenterN);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = grid;
            gridN = candidateGridN;
        }

        closestAngleDegrees = smallestAngleDegrees == double.MaxValue ? 0.0 : smallestAngleDegrees;
        return best;
    }
}
