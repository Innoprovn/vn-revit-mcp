using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Làm tròn đường bao CAD đài về đúng phương ngang/đứng trước khi dựng Foundation Slab — chỉ làm tròn cạnh ngang/đứng, giữ nguyên cạnh xuyên (chéo).</summary>
internal static class PileBoundaryRegularizer
{
    private const double AngleToleranceDegrees = 2.0;
    private const double SnapUnitMm = 1.0;

    /// <summary>Regularize 1 đường bao — gridThetaDeg (nếu có, [0,90)) khoá cứng phương đài theo đúng hệ Grid công trình khi đủ gần; topZ là cao độ đặt toàn bộ đỉnh kết quả (đỉnh Foundation Slab = capLevel).</summary>
    public static RegularizeResult Regularize(RawBoundary boundary, double? gridThetaDeg, double topZ)
    {
        var vertices = ClosedPolygonVertices(boundary.Points);
        var n = vertices.Count;
        if (n < 3)
            return RegularizeResult.Fallback(boundary, topZ, "Đường bao không hợp lệ (dưới 3 đỉnh).");

        var edgeVectors = new XYZ[n];
        for (var i = 0; i < n; i++)
            edgeVectors[i] = vertices[(i + 1) % n] - vertices[i];

        var mod90Angles = edgeVectors.Select(AngleClusterUtils.Mod90Deg).ToList();
        var dominant = AngleClusterUtils.DetectDominantAngle(mod90Angles, AngleToleranceDegrees);
        if (dominant is null || dominant.Value.Count < 2)
            return RegularizeResult.Fallback(boundary, topZ, "Đài không có cặp cạnh nào gần vuông góc — giữ nguyên theo CAD gốc, chưa làm tròn.");

        var thetaDai = dominant.Value.ThetaDeg;
        if (gridThetaDeg is { } gridTheta && AngleClusterUtils.CircularDistance90(thetaDai, gridTheta) <= AngleToleranceDegrees)
            thetaDai = gridTheta;

        var thetaRad = thetaDai * Math.PI / 180.0;
        var uAxis = new XYZ(Math.Cos(thetaRad), Math.Sin(thetaRad), 0);
        var vAxis = new XYZ(-Math.Sin(thetaRad), Math.Cos(thetaRad), 0);
        var cosTolerance = Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);

        var isAxisEdge = new bool[n];
        var rawLengthsFeet = new double[n];
        var lockedDirs = new XYZ[n];
        for (var i = 0; i < n; i++)
        {
            rawLengthsFeet[i] = edgeVectors[i].GetLength();
            if (rawLengthsFeet[i] < 1e-9)
                continue;

            var dir = edgeVectors[i] / rawLengthsFeet[i];
            var onU = Math.Abs(dir.DotProduct(uAxis)) >= cosTolerance;
            var onV = !onU && Math.Abs(dir.DotProduct(vAxis)) >= cosTolerance;

            if (onU || onV)
            {
                isAxisEdge[i] = true;
                var axisDir = onU ? uAxis : vAxis;
                lockedDirs[i] = dir.DotProduct(axisDir) >= 0 ? axisDir : axisDir.Negate();
            }
            else
            {
                lockedDirs[i] = dir; // canh xuyen - giu nguyen huong CAD goc.
            }
        }

        // Dai chu nhat thuan (khong canh xuyen nao): ep 2 cap canh doi dien ve cung 1 chieu dai GOC
        // (truoc lam tron) roi moi lam tron, dam bao khep kin dung tuyet doi.
        var finalLengthsFeet = (double[])rawLengthsFeet.Clone();
        if (n == 4 && isAxisEdge.All(b => b))
        {
            EqualizePair(finalLengthsFeet, 0, 2);
            EqualizePair(finalLengthsFeet, 1, 3);
        }

        // Chon 1 canh xuyen lam "canh dong" - sai so khep kin polygon roi vao dung canh nay, khong lam
        // lech cac canh ngang/dung da lam tron. Khong co canh xuyen nao (vd tu giac thuan) thi khong can
        // vi 2 cap canh doi dien da duoc ep bang nhau tuyet doi o buoc tren.
        var closerOriginalIndex = Array.IndexOf(isAxisEdge, false);
        var startIndex = closerOriginalIndex < 0 ? 0 : (closerOriginalIndex + 1) % n;

        var newVertices = new XYZ[n];
        newVertices[0] = vertices[startIndex];
        for (var k = 0; k < n - 1; k++)
        {
            var i = (startIndex + k) % n;
            var lengthFeet = finalLengthsFeet[i];
            if (isAxisEdge[i])
            {
                var lengthMm = UnitUtils.ConvertFromInternalUnits(lengthFeet, UnitTypeId.Millimeters);
                var roundedMm = Math.Round(lengthMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;
                lengthFeet = UnitUtils.ConvertToInternalUnits(roundedMm, UnitTypeId.Millimeters);
            }

            newVertices[k + 1] = newVertices[k] + lockedDirs[i] * lengthFeet;
        }

        try
        {
            var loop = new CurveLoop();
            for (var k = 0; k < n; k++)
            {
                var p0 = WithZ(newVertices[k], topZ);
                var p1 = WithZ(newVertices[(k + 1) % n], topZ);
                if (p0.DistanceTo(p1) > 1e-9)
                    loop.Append(Line.CreateBound(p0, p1));
            }

            return RegularizeResult.Success(loop);
        }
        catch (Exception ex)
        {
            return RegularizeResult.Fallback(boundary, topZ, $"Regularize thất bại ({ex.Message}) — giữ nguyên theo CAD gốc.");
        }
    }

    private static XYZ WithZ(XYZ p, double z) => new(p.X, p.Y, z);

    private static void EqualizePair(double[] lengthsFeet, int a, int b)
    {
        var avg = (lengthsFeet[a] + lengthsFeet[b]) / 2.0;
        lengthsFeet[a] = avg;
        lengthsFeet[b] = avg;
    }

    /// <summary>Đỉnh đa giác khép kín, đã bỏ điểm cuối trùng điểm đầu.</summary>
    private static List<XYZ> ClosedPolygonVertices(IReadOnlyList<XYZ> points)
    {
        if (points.Count > 1 && points[0].DistanceTo(points[^1]) < 1e-9)
            return points.Take(points.Count - 1).ToList();

        return points.ToList();
    }
}

/// <summary>Kết quả regularize 1 đường bao đài — Loop dùng cho Floor.Create, kèm cảnh báo nếu phải giữ nguyên CAD gốc (không regularize được).</summary>
internal sealed record RegularizeResult(CurveLoop? Loop, string? FallbackReason)
{
    public static RegularizeResult Success(CurveLoop loop) => new(loop, null);

    public static RegularizeResult Fallback(RawBoundary boundary, double topZ, string reason) => new(BuildRawLoop(boundary, topZ), reason);

    /// <summary>Dựng CurveLoop thẳng theo đúng toạ độ CAD gốc, không làm tròn gì — dùng khi không regularize được.</summary>
    private static CurveLoop? BuildRawLoop(RawBoundary boundary, double topZ)
    {
        try
        {
            var loop = new CurveLoop();
            var pts = boundary.Points;
            for (var i = 0; i < pts.Count - 1; i++)
            {
                var p0 = new XYZ(pts[i].X, pts[i].Y, topZ);
                var p1 = new XYZ(pts[i + 1].X, pts[i + 1].Y, topZ);
                if (p0.DistanceTo(p1) > 1e-9)
                    loop.Append(Line.CreateBound(p0, p1));
            }

            return loop;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
