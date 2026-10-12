using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Làm tròn vị trí X/Y của cọc/đài vừa đặt theo Grid gần nhất, mỗi phương độc lập, bội 1mm — tự phát hiện phương thật của hệ Grid, kể cả khi cả hệ xoay nghiêng so với trục X/Y toàn cục.</summary>
internal static class PileGridSnapper
{
    private const double AngleToleranceDegrees = 2.0;
    private const double SnapUnitMm = 1.0;

    /// <summary>Snap từng element trong danh sách theo Grid của view, trong 1 Transaction riêng.</summary>
    public static SnapSummary Snap(Document doc, View view, IReadOnlyList<ElementId> elementIds)
    {
        if (elementIds.Count == 0)
            return new SnapSummary(0, 0, 0);

        var lineGrids = LineGridsOf(doc, view);
        var axes = DetectGridAxes(lineGrids);
        if (axes is null)
            return new SnapSummary(0, 0, elementIds.Count);

        var (uAxis, vAxis) = axes.Value;
        var uGroup = lineGrids.Where(g => IsParallel(GridDirection(g), uAxis)).ToList();
        var vGroup = lineGrids.Where(g => IsParallel(GridDirection(g), vAxis)).ToList();

        var snapped = 0;
        var alreadyRound = 0;
        var skippedNoGrid = 0;


        foreach (var id in elementIds)
        {
            try
            {
                switch (SnapOne(doc, id, uAxis, vAxis, uGroup, vGroup))
                {
                    case Outcome.Snapped: snapped++; break;
                    case Outcome.AlreadyRound: alreadyRound++; break;
                    case Outcome.SkippedNoGrid: skippedNoGrid++; break;
                }
            }
            catch (Exception)
            {
                // Loi rieng 1 element khong duoc chan cac element khac.
            }
        }

        return new SnapSummary(snapped, alreadyRound, skippedNoGrid);
    }

    /// <summary>Góc nghiêng θ (độ, [0,90)) của cả hệ Grid trong view — dùng lại cho Job R (regularize đường bao đài). Null nếu không có Grid Line nào.</summary>
    public static double? DetectThetaDeg(Document doc, View view)
    {
        var lineGrids = LineGridsOf(doc, view);
        if (lineGrids.Count == 0)
            return null;

        var angles = lineGrids.Select(g => AngleClusterUtils.Mod90Deg(GridDirection(g))).ToList();
        return AngleClusterUtils.DetectDominantAngle(angles, AngleToleranceDegrees)?.ThetaDeg;
    }

    /// <summary>Grid cong (Arc) bị loại hoàn toàn khỏi cả bước phát hiện θ lẫn bước snap.</summary>
    private static List<Grid> LineGridsOf(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .Where(g => g.Curve is Line)
            .ToList();

    private enum Outcome
    {
        Snapped,
        AlreadyRound,
        SkippedNoGrid
    }

    /// <summary>Snap 1 điểm theo 2 phương U/V của hệ Grid thật — bỏ qua riêng phương không có Grid đủ song song.</summary>
    private static Outcome SnapOne(Document doc, ElementId id, XYZ uAxis, XYZ vAxis, List<Grid> uGroup, List<Grid> vGroup)
    {
        if (doc.GetElement(id) is not { Location: LocationPoint lp })
            return Outcome.SkippedNoGrid;

        var point = lp.Point;
        var u = point.X * uAxis.X + point.Y * uAxis.Y;
        var v = point.X * vAxis.X + point.Y * vAxis.Y;

        // Grid song song Uaxis co toa do v khong doi doc chieu dai - dung de snap phuong V, va nguoc lai.
        var deltaV = FindAxisDelta(v, uGroup, uAxis, vAxis, useV: true);
        var deltaU = FindAxisDelta(u, vGroup, uAxis, vAxis, useV: false);

        if (deltaU is null && deltaV is null)
            return Outcome.SkippedNoGrid;

        var du = deltaU ?? 0;
        var dv = deltaV ?? 0;
        // Xoay nguoc (Δu, Δv) ve (ΔX, ΔY) toan cuc - dung vi Uaxis/Vaxis truc chuan.
        var dx = du * uAxis.X + dv * vAxis.X;
        var dy = du * uAxis.Y + dv * vAxis.Y;

        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
            return Outcome.AlreadyRound;

        ElementTransformUtils.MoveElement(doc, id, new XYZ(dx, dy, 0));
        return Outcome.Snapped;
    }

    /// <summary>Tìm Grid trong nhóm gần toạ độ u/v nhất, trả về delta (feet) làm tròn bội 1mm — null nếu nhóm rỗng.</summary>
    private static double? FindAxisDelta(double coordinate, List<Grid> group, XYZ uAxis, XYZ vAxis, bool useV)
    {
        double? bestGridCoord = null;
        var bestDistance = double.MaxValue;

        foreach (var grid in group)
        {
            var p = ((Line)grid.Curve!).GetEndPoint(0);
            var gridCoord = useV ? p.X * vAxis.X + p.Y * vAxis.Y : p.X * uAxis.X + p.Y * uAxis.Y;
            var distance = Math.Abs(coordinate - gridCoord);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestGridCoord = gridCoord;
        }

        if (bestGridCoord is null)
            return null;

        var gapMm = UnitUtils.ConvertFromInternalUnits(coordinate - bestGridCoord.Value, UnitTypeId.Millimeters);
        var roundedMm = Math.Round(gapMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;
        var deltaMm = roundedMm - gapMm;
        return UnitUtils.ConvertToInternalUnits(deltaMm, UnitTypeId.Millimeters);
    }

    /// <summary>Phát hiện θ rồi dựng hệ trục U/V vuông góc xoay theo θ — null nếu không có Grid Line nào.</summary>
    private static (XYZ UAxis, XYZ VAxis)? DetectGridAxes(List<Grid> lineGrids)
    {
        if (lineGrids.Count == 0)
            return null;

        var angles = lineGrids.Select(g => AngleClusterUtils.Mod90Deg(GridDirection(g))).ToList();
        var dominant = AngleClusterUtils.DetectDominantAngle(angles, AngleToleranceDegrees);
        if (dominant is null)
            return null;

        var thetaRad = dominant.Value.ThetaDeg * Math.PI / 180.0;
        var uAxis = new XYZ(Math.Cos(thetaRad), Math.Sin(thetaRad), 0);
        var vAxis = new XYZ(-Math.Sin(thetaRad), Math.Cos(thetaRad), 0);
        return (uAxis, vAxis);
    }

    private static bool IsParallel(XYZ direction, XYZ axis)
    {
        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);
        return Math.Abs(direction.DotProduct(axis)) >= cosTolerance;
    }

    private static XYZ GridDirection(Grid grid)
    {
        var line = (Line)grid.Curve!;
        return (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
    }
}

/// <summary>Kết quả 1 lượt snap cọc/đài vừa đặt theo Grid.</summary>
internal sealed record SnapSummary(int Snapped, int AlreadyRound, int SkippedNoGrid);
