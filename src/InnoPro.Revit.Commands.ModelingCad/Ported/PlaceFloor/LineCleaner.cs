using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Làm sạch topology, nối khoảng hở và bỏ nhánh thừa.</summary>
internal static class LineCleaner
{
    private const double AngleToleranceDegrees = 2.0;
    private static readonly double ExtendRangeFeet = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);
    private static readonly double SearchBoxHalfFeet = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);
    private static readonly double ConnectedTolFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
    private static readonly double KeyTolFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

    public static CleanLineSummary Clean(Document doc, ViewPlan view)
    {
        var style = OwnedLineStyle.FindExisting(doc);
        if (style is null)
            return new CleanLineSummary(false, 0, 0, 0, new List<string> { "Chưa có line tim dầm/vách để làm sạch." });

        var owned = OwnedLineStyle.FindOwned(doc, view, style.Id);
        var sourceLines = owned
            .Select(element => element.GeometryCurve as Line)
            .Where(line => line is not null)
            .Select(line => Flatten(line!))
            .ToList();

        if (sourceLines.Count == 0)
            return new CleanLineSummary(false, 0, 0, 0, new List<string> { "Không có line thẳng nào để làm sạch." });

        var repaired = RepairGaps(sourceLines, out var joinedGaps);
        var provisionalLoops = BeamLoopScanner.TraceClosedLoops(repaired.Cast<Curve>().ToList());
        if (provisionalLoops.Count == 0)
            return new CleanLineSummary(false, joinedGaps, 0, sourceLines.Count, new List<string> { "Chưa tạo được ô kín sau khi nối line thiếu; line hiện tại được giữ nguyên." });

        var finalLines = new Dictionary<string, Line>(StringComparer.Ordinal);

        foreach (var loop in provisionalLoops)
        {
            var adjusted = new List<Line>();
            foreach (var curve in loop)
            {
                if (curve is not Line line)
                    continue;

                adjusted.Add(Flatten(line));
            }

            foreach (var line in adjusted)
                finalLines.TryAdd(EdgeKey(line), line);
        }

        if (finalLines.Count == 0)
            return new CleanLineSummary(false, joinedGaps, 0, sourceLines.Count, new List<string> { "Không dựng lại được đường bao sau khi làm sạch." });

        var mergedFinalLines = CenterlineMerger.MergeCollinearAdjacent(finalLines.Values.ToList(), perpendicularToleranceFeet: KeyTolFeet);

        var errors = new List<string>();

        doc.Delete(owned.Select(element => element.Id).ToList());

        var created = 0;
        foreach (var line in mergedFinalLines)
        {
            try
            {
                var detail = doc.Create.NewDetailCurve(view, line);
                detail.LineStyle = style;
                created++;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }


        var removedBranches = Math.Max(0, sourceLines.Count - created);
        return new CleanLineSummary(true, joinedGaps, removedBranches, created, errors);
    }

    /// <summary>Nối đầu hở vào line cắt ngang hoặc đầu collinear trong phạm vi 500mm.</summary>
    private static List<Line> RepairGaps(IReadOnlyList<Line> source, out int joinedGaps)
    {
        var repaired = source.Select(Flatten).ToList();
        joinedGaps = 0;
        var maximumIterations = Math.Max(1, repaired.Count * 2);

        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var changed = ExtendOpenEndsToCrossings(repaired);
            changed += JoinOpenCornersInSearchBox(repaired);
            changed += JoinCollinearOpenEnds(repaired);
            joinedGaps += changed;

            if (changed == 0)
                break;
        }

        return repaired;
    }

    /// <summary>Extend các đầu hở tới line đang tồn tại rồi trả về số đầu đã nối.</summary>
    private static int ExtendOpenEndsToCrossings(List<Line> lines)
    {
        var joined = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var direction = (p1 - p0).Normalize();
            var others = lines.Where((_, index) => index != i).ToList();

            var newP0 = IsConnected(p0, others) ? p0 : ExtendToCrossing(p0, -direction, others) ?? p0;
            var newP1 = IsConnected(p1, others) ? p1 : ExtendToCrossing(p1, direction, others) ?? p1;
            if (newP0.DistanceTo(p0) > ConnectedTolFeet)
                joined++;
            if (newP1.DistanceTo(p1) > ConnectedTolFeet)
                joined++;

            lines[i] = Line.CreateBound(newP0, newP1);
        }

        return joined;
    }

    /// <summary>Nối các cặp đầu hở collinear trong phạm vi 500mm.</summary>
    private static int JoinCollinearOpenEnds(List<Line> lines)
    {
        var openEnds = CollectOpenEnds(lines);
        var used = new HashSet<(int LineIndex, int EndIndex)>();
        var joined = 0;
        foreach (var end in openEnds)
        {
            if (used.Contains((end.LineIndex, end.EndIndex)))
                continue;

            var best = openEnds
                .Where(other => other.LineIndex != end.LineIndex && !used.Contains((other.LineIndex, other.EndIndex)))
                .Select(other => (Other: other, Distance: end.Point.DistanceTo(other.Point)))
                .Where(item => item.Distance <= ExtendRangeFeet && AreFacingCollinear(end, item.Other))
                .OrderBy(item => item.Distance)
                .FirstOrDefault();

            if (best.Other is null || best.Distance < ConnectedTolFeet)
                continue;

            lines.Add(Line.CreateBound(end.Point, best.Other.Point));
            used.Add((end.LineIndex, end.EndIndex));
            used.Add((best.Other.LineIndex, best.Other.EndIndex));
            joined++;
        }

        return joined;
    }

    /// <summary>Nối hai đầu hở tới giao điểm kéo dài khi giao điểm nằm trong vùng 1000x1000mm.</summary>
    private static int JoinOpenCornersInSearchBox(List<Line> lines)
    {
        var joined = 0;
        var maximumIterations = lines.Count * 2;

        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var openEnds = CollectOpenEnds(lines);
            CornerCandidate? best = null;

            for (var i = 0; i < openEnds.Count; i++)
            {
                for (var j = i + 1; j < openEnds.Count; j++)
                {
                    var first = openEnds[i];
                    var second = openEnds[j];
                    if (first.LineIndex == second.LineIndex)
                        continue;

                    if (!TryIntersectInfinite(lines[first.LineIndex], lines[second.LineIndex], out var intersection))
                        continue;

                    if (!IsInsideSearchBox(first.Point, intersection) || !IsInsideSearchBox(second.Point, intersection))
                        continue;

                    var firstVector = intersection - first.Point;
                    var secondVector = intersection - second.Point;
                    if (firstVector.DotProduct(first.OutwardDirection) <= 0 || secondVector.DotProduct(second.OutwardDirection) <= 0)
                        continue;

                    var score = firstVector.GetLength() + secondVector.GetLength();
                    if (best is null || score > best.Score)
                        best = new CornerCandidate(first, second, intersection, score);
                }
            }

            if (best is null)
                break;

            lines[best.First.LineIndex] = ExtendEnd(lines[best.First.LineIndex], best.First.EndIndex, best.Intersection);
            lines[best.Second.LineIndex] = ExtendEnd(lines[best.Second.LineIndex], best.Second.EndIndex, best.Intersection);
            joined++;
        }

        return joined;
    }

    private static bool IsInsideSearchBox(XYZ center, XYZ point)
    {
        return Math.Abs(point.X - center.X) <= SearchBoxHalfFeet &&
               Math.Abs(point.Y - center.Y) <= SearchBoxHalfFeet;
    }

    private static Line ExtendEnd(Line line, int endIndex, XYZ point)
    {
        var extended = endIndex == 0
            ? Line.CreateBound(point, line.GetEndPoint(1))
            : Line.CreateBound(line.GetEndPoint(0), point);
        return extended.Length > line.Length ? extended : line;
    }

    private static List<OpenEnd> CollectOpenEnds(IReadOnlyList<Line> lines)
    {
        var result = new List<OpenEnd>();
        for (var i = 0; i < lines.Count; i++)
        {
            var p0 = lines[i].GetEndPoint(0);
            var p1 = lines[i].GetEndPoint(1);
            var direction = (p1 - p0).Normalize();
            var others = lines.Where((_, index) => index != i).ToList();

            if (!IsConnected(p0, others))
                result.Add(new OpenEnd(i, 0, p0, -direction));
            if (!IsConnected(p1, others))
                result.Add(new OpenEnd(i, 1, p1, direction));
        }

        return result;
    }

    private static bool AreFacingCollinear(OpenEnd first, OpenEnd second)
    {
        if (first.OutwardDirection.DotProduct(second.OutwardDirection) > -Math.Cos(AngleToleranceDegrees * Math.PI / 180.0))
            return false;

        var between = second.Point - first.Point;
        if (between.IsZeroLength())
            return false;

        var toward = between.Normalize();
        return first.OutwardDirection.DotProduct(toward) >= Math.Cos(AngleToleranceDegrees * Math.PI / 180.0) &&
               second.OutwardDirection.DotProduct(-toward) >= Math.Cos(AngleToleranceDegrees * Math.PI / 180.0);
    }

    private static bool IsConnected(XYZ point, IReadOnlyList<Line> others)
    {
        return others.Any(line => DistanceToSegment(point, line) <= ConnectedTolFeet);
    }

    private static XYZ? ExtendToCrossing(XYZ origin, XYZ direction, IReadOnlyList<Line> others)
    {
        var bestDistance = double.MinValue;
        XYZ? best = null;
        foreach (var other in others)
        {
            if (!TryIntersectRaySegment(origin, direction, other, out var distance, out var point) || distance > ExtendRangeFeet)
                continue;

            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = point;
            }
        }

        return best;
    }

    private static bool TryIntersectRaySegment(XYZ origin, XYZ direction, Line segment, out double distance, out XYZ point)
    {
        distance = 0;
        point = XYZ.Zero;
        var a = segment.GetEndPoint(0);
        var segmentDirection = segment.GetEndPoint(1) - a;
        var denominator = direction.X * segmentDirection.Y - direction.Y * segmentDirection.X;
        if (Math.Abs(denominator) < 1e-9)
            return false;

        var difference = a - origin;
        var rayT = (difference.X * segmentDirection.Y - difference.Y * segmentDirection.X) / denominator;
        var segmentT = (difference.X * direction.Y - difference.Y * direction.X) / denominator;
        if (rayT <= 1e-6 || segmentT < -1e-6 || segmentT > 1 + 1e-6)
            return false;

        distance = rayT;
        point = origin + rayT * direction;
        return true;
    }

    private static bool TryIntersectInfinite(Line first, Line second, out XYZ point)
    {
        var a = first.GetEndPoint(0);
        var da = first.GetEndPoint(1) - a;
        var b = second.GetEndPoint(0);
        var db = second.GetEndPoint(1) - b;
        var denominator = da.X * db.Y - da.Y * db.X;
        if (Math.Abs(denominator) < 1e-9)
        {
            point = XYZ.Zero;
            return false;
        }

        var difference = b - a;
        var t = (difference.X * db.Y - difference.Y * db.X) / denominator;
        point = new XYZ(a.X + t * da.X, a.Y + t * da.Y, 0);
        return true;
    }

    private static double DistanceToSegment(XYZ point, Line line)
    {
        var a = line.GetEndPoint(0);
        var ab = line.GetEndPoint(1) - a;
        var denominator = ab.DotProduct(ab);
        if (denominator < 1e-12)
            return point.DistanceTo(a);

        var t = Math.Clamp((point - a).DotProduct(ab) / denominator, 0.0, 1.0);
        return point.DistanceTo(a + t * ab);
    }

    private static Line Flatten(Line line)
    {
        var p0 = line.GetEndPoint(0);
        var p1 = line.GetEndPoint(1);
        return Line.CreateBound(new XYZ(p0.X, p0.Y, 0), new XYZ(p1.X, p1.Y, 0));
    }

    private static string EdgeKey(Line line)
    {
        var first = PointKey(line.GetEndPoint(0));
        var second = PointKey(line.GetEndPoint(1));
        return string.CompareOrdinal(first, second) <= 0 ? $"{first}|{second}" : $"{second}|{first}";
    }

    private static string PointKey(XYZ point)
    {
        var x = (long)Math.Round(point.X / KeyTolFeet);
        var y = (long)Math.Round(point.Y / KeyTolFeet);
        return $"{x},{y}";
    }

    private sealed record OpenEnd(int LineIndex, int EndIndex, XYZ Point, XYZ OutwardDirection);

    private sealed record CornerCandidate(OpenEnd First, OpenEnd Second, XYZ Intersection, double Score);
}

internal sealed record CleanLineSummary(bool Succeeded, int GapsJoined, int BranchesRemoved, int LinesCreated, List<string> Errors);
