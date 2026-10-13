using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceWall;

internal static class WallPlacer
{
    private const double MatchToleranceMm = 2.0;
    private const double DuplicatePositionToleranceMm = 10.0;
    private const double DuplicateAngleToleranceDegrees = 1.0;
    private const double MinimumRemainingLengthMm = 50.0;
    private const double MergeAngleToleranceDegrees = 2.0;
    private static readonly double MergePerpendicularToleranceFeet = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
    private static readonly double MergeAdjacentGapFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
    internal const string NoTagParameterLabel = "(Không gán Shared Parameter)";
    internal readonly record struct WallLine(XYZ Start, XYZ End);

    public static List<WallGroup> GroupWalls(IEnumerable<RawWall> walls, IReadOnlyList<SymbolChoice> choices)
    {
        // Union tim theo hình học trước khi TagName/bề dày chia chúng thành các nhóm UI.
        // Nhờ vậy một tim ngắn nằm trong tim dài không thể sống sót chỉ vì đọc được tag khác.
        var mergedWalls = MergeCollinearWalls(walls.ToList());

        return mergedWalls.GroupBy(wall =>
        {
            var width = Math.Round(UnitUtils.ConvertFromInternalUnits(wall.WidthFeet, UnitTypeId.Millimeters) / 5) * 5;
            return $"{wall.TagName?.ToUpperInvariant() ?? "(NO-TAG)"}|W:{width}";
        })
            .Select(g =>
            {
                var first = g.First();
                var width = Math.Round(UnitUtils.ConvertFromInternalUnits(first.WidthFeet, UnitTypeId.Millimeters) / 5) * 5;
                var group = new WallGroup
                {
                    TagName = first.TagName,
                    SizeBMm = width,
                    Walls = g.ToList(),
                    TypeChoices = new System.Collections.ObjectModel.ObservableCollection<SymbolChoice>(choices),
                };
                group.SelectedType = FindExactMatch(width, choices) ?? AutoMatchWallType(width, choices);
                group.IsExactMatch = group.SelectedType is not null && IsMatch(group.SelectedType, width, 0);
                group.ShouldModel = !string.IsNullOrWhiteSpace(group.TagName);
                return group;
            }).OrderBy(g => g.TagName is null).ThenBy(g => g.SizeBMm).ToList();
    }

    /// <summary>Union các tim tường CAD cùng trục, chồng lấn hoặc liền nhau. Phỏng theo
    /// PlaceFloor.CenterlineMerger nhưng giữ lại metadata/segment nguồn của RawWall.</summary>
    internal static List<RawWall> MergeCollinearWalls(IReadOnlyList<RawWall> walls)
    {
        // Xét tim dài trước để đoạn bao phủ lớn nhất quyết định trục và bề dày.
        var orderedWalls = walls
            .OrderByDescending(wall => wall.Start.DistanceTo(wall.End))
            .ToList();
        var used = new bool[orderedWalls.Count];
        var result = new List<RawWall>();

        for (var i = 0; i < orderedWalls.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var source = orderedWalls[i];
            var p0 = source.Start;
            var p1 = source.End;
            var vector = p1 - p0;
            if (vector.IsZeroLength()) continue;
            var direction = vector.Normalize();
            var members = new List<RawWall> { source };

            var mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (var j = 0; j < orderedWalls.Count; j++)
                {
                    if (used[j] || !TryUnion(p0, p1, direction, orderedWalls[j], out var newP0, out var newP1))
                        continue;
                    p0 = newP0; p1 = newP1;
                    used[j] = true; members.Add(orderedWalls[j]); mergedAny = true;
                }
            }

            var metadataSource = members
                .Where(member => !string.IsNullOrWhiteSpace(member.TagName) || member.TagHMm.HasValue)
                .OrderByDescending(member => !string.IsNullOrWhiteSpace(member.TagName))
                .ThenByDescending(member => member.Start.DistanceTo(member.End))
                .FirstOrDefault() ?? source;

            result.Add(new RawWall
            {
                Start = p0,
                End = p1,
                WidthFeet = source.WidthFeet,
                Segments = members.SelectMany(member => member.Segments).ToList(),
                TagName = metadataSource.TagName,
                TagBMm = metadataSource.TagBMm,
                TagHMm = metadataSource.TagHMm,
                IsCrossLayerPair = members.Any(member => member.IsCrossLayerPair),
                CrossLayerNote = members.Select(member => member.CrossLayerNote).FirstOrDefault(note => !string.IsNullOrWhiteSpace(note)),
            });
        }
        return result;
    }

    private static bool TryUnion(XYZ p0, XYZ p1, XYZ direction, RawWall candidate, out XYZ newP0, out XYZ newP1)
    {
        newP0 = p0; newP1 = p1;
        var candidateVector = candidate.End - candidate.Start;
        if (candidateVector.IsZeroLength()) return false;
        var candidateDirection = candidateVector.Normalize();
        var angle = Math.Acos(Math.Clamp(Math.Abs(direction.DotProduct(candidateDirection)), -1.0, 1.0)) * 180.0 / Math.PI;
        if (angle > MergeAngleToleranceDegrees) return false;

        var offset = candidate.Start - p0;
        var projectedOffset = offset.DotProduct(direction);
        var perpendicular = offset - projectedOffset * direction;
        if (perpendicular.GetLength() > MergePerpendicularToleranceFeet) return false;

        var currentEnd = (p1 - p0).DotProduct(direction);
        var currentMin = Math.Min(0.0, currentEnd);
        var currentMax = Math.Max(0.0, currentEnd);
        var candidateT0 = (candidate.Start - p0).DotProduct(direction);
        var candidateT1 = (candidate.End - p0).DotProduct(direction);
        var candidateMin = Math.Min(candidateT0, candidateT1);
        var candidateMax = Math.Max(candidateT0, candidateT1);
        var gap = Math.Max(currentMin, candidateMin) - Math.Min(currentMax, candidateMax);
        if (gap > MergeAdjacentGapFeet) return false;

        var unionMin = Math.Min(currentMin, candidateMin);
        var unionMax = Math.Max(currentMax, candidateMax);
        newP0 = p0 + unionMin * direction;
        newP1 = p0 + unionMax * direction;
        return true;
    }

    private static SymbolChoice? AutoMatchWallType(double widthMm, IReadOnlyList<SymbolChoice> choices) =>
        choices.OrderBy(c => Math.Abs(UnitUtils.ConvertFromInternalUnits(c.Symbol.Width, UnitTypeId.Millimeters) - widthMm)).FirstOrDefault();

    public static bool IsMatch(SymbolChoice choice, double widthMm, double unused) =>
        Math.Abs(UnitUtils.ConvertFromInternalUnits(choice.Symbol.Width, UnitTypeId.Millimeters) - widthMm) <= MatchToleranceMm;

    private static SymbolChoice? FindExactMatch(double widthMm, IReadOnlyList<SymbolChoice> choices) =>
        choices.FirstOrDefault(choice => IsMatch(choice, widthMm, 0));

    public static CreateTypesSummary CreateMissingTypes(Document doc, List<WallGroup> groups, WallType templateType)
    {
        var created = 0; var failed = 0;
        var errors = new List<string>(); var createdChoices = new List<SymbolChoice>();
        var choices = groups.SelectMany(group => group.TypeChoices).GroupBy(choice => choice.Symbol.Id).Select(group => group.First()).ToList();

        foreach (var group in groups.Where(group => group.ShouldModel && !group.IsExactMatch))
        {
            var reuse = choices.FirstOrDefault(choice => IsMatch(choice, group.SizeBMm, 0));
            if (reuse is not null)
            {
                group.SelectedType = reuse; group.IsExactMatch = true; continue;
            }

            try
            {
                var baseName = $"Vách {Math.Round(group.SizeBMm):0}mm";
                var existingNames = choices
                    .Select(choice => choice.Symbol.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var typeName = baseName;
                for (var suffix = 2; existingNames.Contains(typeName); suffix++)
                    typeName = $"{baseName} ({suffix})";

                var newType = (WallType)templateType.Duplicate(typeName);
                ResizeWallType(newType, group.SizeBMm);
                var choice = new SymbolChoice(newType, newType.Name);
                choices.Add(choice); createdChoices.Add(choice);
                foreach (var targetGroup in groups)
                    if (!targetGroup.TypeChoices.Any(existing => existing.Symbol.Id == choice.Symbol.Id))
                        targetGroup.TypeChoices.Add(choice);
                group.SelectedType = choice; group.IsExactMatch = true; created++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 20) errors.Add($"Tường {group.SizeBMm:0}mm: {ex.Message}");
            }
        }
        return new(created, failed, errors, createdChoices);
    }

    private static void ResizeWallType(WallType wallType, double targetWidthMm)
    {
        var structure = wallType.GetCompoundStructure()
            ?? throw new InvalidOperationException("Wall Type mẫu không có cấu tạo lớp.");
        var layers = structure.GetLayers();
        if (layers.Count == 0) throw new InvalidOperationException("Wall Type mẫu không có lớp vật liệu.");
        var index = Enumerable.Range(0, layers.Count)
            .Where(i => layers[i].Function == MaterialFunctionAssignment.Structure)
            .OrderByDescending(i => layers[i].Width).Cast<int?>().FirstOrDefault()
            ?? Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].Width).First();
        var target = UnitUtils.ConvertToInternalUnits(targetWidthMm, UnitTypeId.Millimeters);
        var otherWidth = layers.Where((_, i) => i != index).Sum(layer => layer.Width);
        var layerWidth = target - otherWidth;
        if (layerWidth <= UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters))
            throw new InvalidOperationException("Các lớp còn lại của Type mẫu đã dày hơn kích thước cần tạo.");
        structure.SetLayerWidth(index, layerWidth);
        wallType.SetCompoundStructure(structure);
    }

    public static PlaceSummary Place(
        Document doc, List<WallGroup> groups, Level? baseLevel, Level? topLevel,
        double baseOffsetFeet, double topOffsetFeet, string? tagParameterName)
    {
        var created = 0; var failed = 0; var skipped = 0;
        var errors = new List<string>(); var ids = new List<ElementId>();
        var existing = CollectExistingWallLines(doc);
        var height = baseLevel is null || topLevel is null
            ? 0
            : topLevel.Elevation + topOffsetFeet - baseLevel.Elevation - baseOffsetFeet;
        foreach (var group in groups.Where(g => g.ShouldModel))
        foreach (var raw in group.Walls)
        {
            if (baseLevel is null || topLevel is null || height <= 0 || group.SelectedType is null) { failed++; continue; }
            var baseElevation = baseLevel.Elevation + baseOffsetFeet;
            var line = new WallLine(new(raw.Start.X, raw.Start.Y, baseElevation), new(raw.End.X, raw.End.Y, baseElevation));
            var remainingLines = SubtractExisting(line, existing);
            if (remainingLines.Count == 0) { skipped++; continue; }

            foreach (var remainingLine in remainingLines)
            {
                try
                {
                    var creationLine = Line.CreateBound(
                        new XYZ(remainingLine.Start.X, remainingLine.Start.Y, baseLevel.Elevation),
                        new XYZ(remainingLine.End.X, remainingLine.End.Y, baseLevel.Elevation));
                    var wall = Wall.Create(
                        doc, creationLine, group.SelectedType.Symbol.Id,
                        baseLevel.Id, height, baseOffsetFeet, false, true);
                    wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topLevel.Id);
                    wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(topOffsetFeet);
                    if (!string.IsNullOrWhiteSpace(tagParameterName) && !string.IsNullOrWhiteSpace(group.TagName))
                    {
                        var p = wall.LookupParameter(tagParameterName);
                        if (p is { IsReadOnly: false, StorageType: StorageType.String }) p.Set(group.TagName);
                    }
                    created++; ids.Add(wall.Id); existing.Add(remainingLine);
                }
                catch (Exception ex) { failed++; if (errors.Count < 20) errors.Add(ex.Message); }
            }
        }
        return new(created, failed, skipped, errors, ids);
    }

    /// <summary>Hai tim tường được coi là trùng khi cùng cao độ, gần song song, nằm trên cùng trục
    /// trong sai số và có đoạn chồng lấn. Tái sử dụng cùng nguyên tắc chống trùng của PlaceWall.</summary>
    internal static bool AreDuplicate(WallLine existing, WallLine candidate)
    {
        var toleranceFeet = UnitUtils.ConvertToInternalUnits(DuplicatePositionToleranceMm, UnitTypeId.Millimeters);
        if (Math.Abs(existing.Start.Z - candidate.Start.Z) > toleranceFeet ||
            Math.Abs(existing.End.Z - candidate.End.Z) > toleranceFeet)
            return false;

        var existingVector = new XYZ(existing.End.X - existing.Start.X, existing.End.Y - existing.Start.Y, 0);
        var candidateVector = new XYZ(candidate.End.X - candidate.Start.X, candidate.End.Y - candidate.Start.Y, 0);
        var existingLength = existingVector.GetLength();
        var candidateLength = candidateVector.GetLength();
        if (existingLength <= toleranceFeet || candidateLength <= toleranceFeet)
            return false;

        var existingDirection = existingVector / existingLength;
        var candidateDirection = candidateVector / candidateLength;
        var cross = Math.Abs(existingDirection.X * candidateDirection.Y - existingDirection.Y * candidateDirection.X);
        if (cross > Math.Sin(DuplicateAngleToleranceDegrees * Math.PI / 180.0))
            return false;

        double DistanceToExistingAxis(XYZ point)
        {
            var offsetX = point.X - existing.Start.X;
            var offsetY = point.Y - existing.Start.Y;
            return Math.Abs(offsetX * existingDirection.Y - offsetY * existingDirection.X);
        }

        if (DistanceToExistingAxis(candidate.Start) > toleranceFeet ||
            DistanceToExistingAxis(candidate.End) > toleranceFeet)
            return false;

        double ProjectOnExistingAxis(XYZ point) =>
            (point.X - existing.Start.X) * existingDirection.X +
            (point.Y - existing.Start.Y) * existingDirection.Y;

        var candidateMin = Math.Min(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var candidateMax = Math.Max(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var overlap = Math.Min(existingLength, candidateMax) - Math.Max(0.0, candidateMin);
        return overlap > toleranceFeet;
    }

    /// <summary>Trừ khỏi tim CAD toàn bộ khoảng đã được các Wall thẳng cùng trục che phủ.
    /// Một candidate có thể trả về 0, 1 hoặc nhiều đoạn còn thiếu để tạo.</summary>
    internal static List<WallLine> SubtractExisting(WallLine candidate, IReadOnlyList<WallLine> existingLines)
    {
        var positionTolerance = UnitUtils.ConvertToInternalUnits(DuplicatePositionToleranceMm, UnitTypeId.Millimeters);
        var minimumLength = UnitUtils.ConvertToInternalUnits(MinimumRemainingLengthMm, UnitTypeId.Millimeters);
        var vector = new XYZ(candidate.End.X - candidate.Start.X, candidate.End.Y - candidate.Start.Y, 0);
        var length = vector.GetLength();
        if (length <= minimumLength)
            return new List<WallLine>();

        var direction = vector / length;
        var blocked = new List<(double Start, double End)>();

        foreach (var existing in existingLines)
        {
            if (Math.Abs(existing.Start.Z - candidate.Start.Z) > positionTolerance ||
                Math.Abs(existing.End.Z - candidate.End.Z) > positionTolerance)
                continue;

            var existingVector = new XYZ(existing.End.X - existing.Start.X, existing.End.Y - existing.Start.Y, 0);
            var existingLength = existingVector.GetLength();
            if (existingLength <= positionTolerance)
                continue;

            var existingDirection = existingVector / existingLength;
            var cross = Math.Abs(direction.X * existingDirection.Y - direction.Y * existingDirection.X);
            if (cross > Math.Sin(DuplicateAngleToleranceDegrees * Math.PI / 180.0))
                continue;

            var existingMid = (existing.Start + existing.End) / 2.0;
            var midOffsetX = existingMid.X - candidate.Start.X;
            var midOffsetY = existingMid.Y - candidate.Start.Y;
            var distanceToAxis = Math.Abs(midOffsetX * direction.Y - midOffsetY * direction.X);
            if (distanceToAxis > positionTolerance)
                continue;

            double Project(XYZ point) =>
                (point.X - candidate.Start.X) * direction.X +
                (point.Y - candidate.Start.Y) * direction.Y;

            var start = Math.Max(0.0, Math.Min(Project(existing.Start), Project(existing.End)));
            var end = Math.Min(length, Math.Max(Project(existing.Start), Project(existing.End)));
            if (end - start > positionTolerance)
                blocked.Add((start, end));
        }

        if (blocked.Count == 0)
            return new List<WallLine> { candidate };

        blocked.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(double Start, double End)>();
        foreach (var interval in blocked)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End + positionTolerance)
            {
                merged.Add(interval);
                continue;
            }

            var last = merged[^1];
            merged[^1] = (last.Start, Math.Max(last.End, interval.End));
        }

        var remaining = new List<WallLine>();
        var cursor = 0.0;
        foreach (var interval in merged)
        {
            AddRemaining(cursor, interval.Start);
            cursor = Math.Max(cursor, interval.End);
        }
        AddRemaining(cursor, length);
        return remaining;

        void AddRemaining(double start, double end)
        {
            if (end - start < minimumLength)
                return;
            remaining.Add(new WallLine(candidate.Start + direction * start, candidate.Start + direction * end));
        }
    }

    internal static List<WallLine> CollectExistingWallLines(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .Select(w => w.Location).OfType<LocationCurve>().Select(x => x.Curve).OfType<Line>()
            .Select(x => new WallLine(x.GetEndPoint(0), x.GetEndPoint(1))).ToList();
}

internal sealed record CreateTypesSummary(int Created, int Failed, List<string> Errors, List<SymbolChoice> CreatedChoices);
internal sealed record PlaceSummary(int Created, int Failed, int Skipped, List<string> Errors, IReadOnlyList<ElementId> CreatedIds);

