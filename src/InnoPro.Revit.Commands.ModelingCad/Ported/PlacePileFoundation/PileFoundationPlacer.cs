using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Gộp nhóm cọc theo đường kính + đặt cọc (FamilyInstance) và đài (Foundation Slab) thật vào model.</summary>
internal static class PileFoundationPlacer
{
    private const double SnapUnitMm = 5.0;

    /// <summary>Ngưỡng mở rộng bounding box (X/Y) khi chống đặt trùng.</summary>
    private const double DuplicateExpandMm = 5.0;

    private const string PileDiameterParam = "Diameter";

    /// <summary>Tên tham số family cọc quyết định chiều cao cọc ăn vào đài — set trực tiếp vào biến này thay vì cộng dồn vào cao độ đặt cọc.</summary>
    private const string PileEmbedmentParam = "Minimum Embedment";

    /// <summary>Ngưỡng coi 2 circle là cùng 1 tim cọc (CAD hay vẽ lồng 2 vòng tròn cho 1 cọc).</summary>
    private const double ConcentricTolMm = 20.0;

    /// <summary>Ngưỡng coi 2 đường bao là cùng 1 đài (CAD hay vẽ lồng 2 đường bao cho 1 đài — vd viền ván khuôn + viền bê tông thực).</summary>
    private const double BoundaryDuplicateTolMm = 200.0;

    /// <summary>1 vùng chữ nhật mặt bằng (X/Y) đã mở rộng của 1 element cũ, dùng chống trùng.</summary>
    internal readonly record struct PlanBox(double MinX, double MaxX, double MinY, double MaxY)
    {
        public bool Contains(XYZ point) => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
    }

    /// <summary>Gộp circle theo bán kính đã làm tròn bội 5mm, so bằng đúng qua key nguyên hoá.</summary>
    public static List<PileGroup> GroupCircles(IEnumerable<RawCircle> circles, IReadOnlyList<SymbolChoice> typeChoices)
    {
        var byKey = new Dictionary<long, (double DiameterMm, List<RawCircle> Circles)>();

        foreach (var circle in DeduplicateConcentric(circles, typeChoices))
        {
            var radiusMm = UnitUtils.ConvertFromInternalUnits(circle.RadiusFeet, UnitTypeId.Millimeters);
            var roundedRadiusMm = Math.Round(radiusMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;
            var diameterMm = roundedRadiusMm * 2;
            var key = (long)Math.Round(roundedRadiusMm * 100);

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (diameterMm, new List<RawCircle>());
                byKey[key] = entry;
            }

            entry.Circles.Add(circle);
            byKey[key] = entry;
        }

        return byKey.Values
            .OrderBy(e => e.DiameterMm)
            .Select(e =>
            {
                var group = new PileGroup { DiameterMm = e.DiameterMm, Circles = e.Circles, TypeChoices = typeChoices.ToList() };
                var exactMatch = FindExactMatch(e.DiameterMm, typeChoices);
                if (exactMatch is not null)
                {
                    group.SelectedType = exactMatch;
                    group.IsExactMatch = true;
                }
                else
                {
                    group.SelectedType = AutoMatchPileType(e.DiameterMm, typeChoices);
                    group.IsExactMatch = false;
                }
                return group;
            })
            .ToList();
    }

    /// <summary>Ngưỡng coi Type đã khớp đúng đường kính nhóm (mm) — mirror PlaceColumn.ColumnPlacer.MatchToleranceMm.</summary>
    private const double MatchToleranceMm = 2.0;

    public static bool IsMatch(SymbolChoice choice, double diameterMm)
    {
        var d = ReadLengthMm(choice.Symbol, PileDiameterParam);
        return d is not null && Math.Abs(d.Value - diameterMm) <= MatchToleranceMm;
    }

    public static SymbolChoice? FindExactMatch(double diameterMm, IReadOnlyList<SymbolChoice> choices) =>
        choices.FirstOrDefault(choice => IsMatch(choice, diameterMm));

    /// <summary>Gộp các circle đồng tâm (CAD hay vẽ lồng 2 vòng tròn cho 1 tim cọc) về 1 circle đại diện.</summary>
    private static List<RawCircle> DeduplicateConcentric(IEnumerable<RawCircle> circles, IReadOnlyList<SymbolChoice> typeChoices)
    {
        var tolFeet = UnitUtils.ConvertToInternalUnits(ConcentricTolMm, UnitTypeId.Millimeters);
        var clusters = new List<List<RawCircle>>();

        foreach (var circle in circles)
        {
            var cluster = clusters.FirstOrDefault(c => c[0].Center.DistanceTo(circle.Center) <= tolFeet);
            if (cluster is null)
                clusters.Add(new List<RawCircle> { circle });
            else
                cluster.Add(circle);
        }

        return clusters.Select(c => c.Count == 1 ? c[0] : PickRepresentativeCircle(c, typeChoices)).ToList();
    }

    /// <summary>Gộp các đường bao đồng tâm (CAD hay vẽ lồng 2 đường bao cho 1 đài) về 1 đường bao đại diện —
    /// so kích thước với box lớn nhất trong cụm: chênh lệch trong ngưỡng BoundaryDuplicateTolMm thì lấy box
    /// NHỎ HƠN (coi là cùng 1 đài vẽ trùng); chênh lệch vượt ngưỡng (khác biệt quá lớn, không phải trùng lặp)
    /// thì lấy lại box LỚN NHẤT.</summary>
    public static List<RawBoundary> DeduplicateOverlappingBoundaries(IEnumerable<RawBoundary> boundaries)
    {
        var tolFeet = UnitUtils.ConvertToInternalUnits(BoundaryDuplicateTolMm, UnitTypeId.Millimeters);
        var clusters = new List<List<RawBoundary>>();

        foreach (var boundary in boundaries)
        {
            var cluster = clusters.FirstOrDefault(c => c[0].Center.DistanceTo(boundary.Center) <= tolFeet);
            if (cluster is null)
                clusters.Add(new List<RawBoundary> { boundary });
            else
                cluster.Add(boundary);
        }

        return clusters.Select(c => PickRepresentativeBoundary(c, tolFeet)).ToList();
    }

    /// <summary>1 cụm đường bao cùng vị trí: box nhỏ hơn chênh lệch kích thước trong ngưỡng thì lấy box đó, vượt ngưỡng thì lấy box lớn nhất.</summary>
    private static RawBoundary PickRepresentativeBoundary(List<RawBoundary> cluster, double tolFeet)
    {
        if (cluster.Count == 1)
            return cluster[0];

        var largest = cluster.OrderByDescending(b => Math.Max(b.LengthFeet, b.WidthFeet)).First();
        var smallest = cluster.OrderBy(b => Math.Max(b.LengthFeet, b.WidthFeet)).First();
        var sizeDiffFeet = Math.Max(largest.LengthFeet, largest.WidthFeet) - Math.Max(smallest.LengthFeet, smallest.WidthFeet);

        return sizeDiffFeet <= tolFeet ? smallest : largest;
    }

    /// <summary>Trong 1 cụm circle đồng tâm, ưu tiên circle có đường kính khớp sẵn 1 Type — không khớp thì lấy circle lớn nhất.</summary>
    private static RawCircle PickRepresentativeCircle(List<RawCircle> cluster, IReadOnlyList<SymbolChoice> typeChoices)
    {
        RawCircle? bestMatch = null;
        var bestDiff = double.MaxValue;

        foreach (var circle in cluster)
        {
            var diameterMm = UnitUtils.ConvertFromInternalUnits(circle.RadiusFeet * 2, UnitTypeId.Millimeters);
            var match = AutoMatchPileType(diameterMm, typeChoices);
            var symbolDiameter = match is null ? null : ReadLengthMm(match.Symbol, PileDiameterParam);
            if (symbolDiameter is null)
                continue;

            var diff = Math.Abs(symbolDiameter.Value - diameterMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestMatch = circle;
            }
        }

        return bestMatch ?? cluster.OrderByDescending(c => c.RadiusFeet).First();
    }

    /// <summary>Đọc 1 parameter Length của Type, quy đổi ra mm — null nếu Type không có parameter này.</summary>
    private static double? ReadLengthMm(FamilySymbol symbol, string paramName)
    {
        var p = symbol.LookupParameter(paramName);
        if (p is null || p.StorageType != StorageType.Double)
            return null;

        return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
    }

    /// <summary>Type có Diameter gần đúng nhất với đường kính nhóm cọc — null nếu không Type nào có parameter Diameter.</summary>
    public static SymbolChoice? AutoMatchPileType(double diameterMm, IReadOnlyList<SymbolChoice> choices)
    {
        SymbolChoice? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var diameter = ReadLengthMm(choice.Symbol, PileDiameterParam);
            if (diameter is null)
                continue;

            var diff = Math.Abs(diameter.Value - diameterMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Đặt toàn bộ cọc (FamilyInstance) + đài (Foundation Slab, v7 Job S) vào model trong 1 Transaction, bỏ qua vị trí đã có sẵn (chống trùng). Cao độ đỉnh cọc tính theo chiều dày Type đài đã chọn (v7 Job U).</summary>
    public static PlaceSummary Place(
        Document doc, List<PileGroup> pileGroups, List<BoundaryRow> boundaryRows,
        Level? capLevel, FloorType? capFloorType, double embedmentMm, double? gridThetaDeg)
    {
        var embedmentFeet = UnitUtils.ConvertToInternalUnits(embedmentMm, UnitTypeId.Millimeters);
        var pilesCreated = 0;
        var pilesFailed = 0;
        var pilesSkipped = 0;
        var capsCreated = 0;
        var capsFailed = 0;
        var capsSkipped = 0;
        var errors = new List<string>();
        var createdPileIds = new List<ElementId>();
        var createdCapIds = new List<ElementId>();

        // Cache bounding box instance cu theo Family, dung 1 lan truoc vong foreach ben duoi (chi ap dung cho coc).
        var existingBoxesByFamily = new Dictionary<string, List<(PlanBox Box, ElementId Id)>>(StringComparer.Ordinal);

        List<(PlanBox Box, ElementId Id)> BoxesFor(string familyName)
        {
            if (!existingBoxesByFamily.TryGetValue(familyName, out var boxes))
            {
                var elements = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => string.Equals(fi.Symbol?.Family?.Name, familyName, StringComparison.Ordinal));
                boxes = BuildExistingBoxes(elements, DuplicateExpandMm);
                existingBoxesByFamily[familyName] = boxes;
            }

            return boxes;
        }

        // Doc chieu day dai 1 LAN duy nhat cho ca lot Dat - Type dai gio dung chung cho moi dai (Job S).
        double? foundationThicknessFeet = null;
        if (capFloorType is not null)
        {
            try
            {
                foundationThicknessFeet = capFloorType.GetCompoundStructure()?.GetWidth();
            }
            catch (Exception)
            {
                foundationThicknessFeet = null;
            }
        }


        foreach (var group in pileGroups)
        {
            var symbol = group.SelectedType?.Symbol;
            if (symbol is null || capLevel is null)
            {
                pilesFailed += group.Circles.Count;
                if (errors.Count < 20)
                    errors.Add($"Cọc Ø{group.DiameterMm:0}mm: chưa gán đủ Type cọc/Level đài, bỏ qua toàn bộ nhóm.");
                continue;
            }

            // So trung theo Family.Name, khong phan biet Type/duong kinh.
            var existingBoxes = BoxesFor(symbol.Family.Name);

            foreach (var circle in group.Circles)
            {
                if (FindDuplicate(circle.Center, existingBoxes) is not null)
                {
                    pilesSkipped++;
                    continue;
                }

                if (circle.OwningBoundary is null)
                {
                    pilesFailed++;
                    if (errors.Count < 20)
                        errors.Add($"Cọc Ø{group.DiameterMm:0}mm: không xác định được đài chứa (không có đài nào trong phạm vi), chưa đặt được cao độ.");
                    continue;
                }

                if (foundationThicknessFeet is null)
                {
                    pilesFailed++;
                    if (errors.Count < 20)
                        errors.Add($"Cọc Ø{group.DiameterMm:0}mm: chưa chọn Type đài (Foundation Slab) hoặc Type đó không có cấu tạo lớp (compound structure), chưa tính được cao độ.");
                    continue;
                }

                var pileTopElevFeet = capLevel.Elevation - foundationThicknessFeet.Value;

                var result = PlaceOne(doc, circle.Center, symbol, capLevel, pileTopElevFeet, embedmentFeet);
                if (result.Outcome == PlaceOutcome.Created)
                {
                    pilesCreated++;
                    if (result.CreatedId is not null)
                        createdPileIds.Add(result.CreatedId);
                }
                else
                {
                    pilesFailed++;
                    if (result.ErrorMessage is not null && errors.Count < 20)
                        errors.Add($"Cọc Ø{group.DiameterMm:0}mm: {result.ErrorMessage}");
                }
            }
        }

        // So trung dai theo Floor + dung FloorType.Id (v7 Job T) - khong con Family.Name vi dai gio la Floor.
        var capExistingBoxes = capFloorType is not null
            ? BuildExistingBoxes(
                new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFoundation)
                    .OfClass(typeof(Floor))
                    .Cast<Floor>()
                    .Where(f => f.FloorType.Id == capFloorType.Id),
                DuplicateExpandMm)
            : new List<(PlanBox Box, ElementId Id)>();

        var resolvedCapIds = new List<ElementId?>();
        var createdCapProfiles = new Dictionary<ElementId, CurveLoop>();

        foreach (var row in boundaryRows)
        {
            if (capFloorType is null || capLevel is null)
            {
                capsFailed++;
                resolvedCapIds.Add(null);
                if (errors.Count < 20)
                    errors.Add($"{row.Label}: chưa chọn Type đài (Foundation Slab) hoặc Level, bỏ qua.");
                continue;
            }

            var duplicateId = FindDuplicate(row.Boundary.Center, capExistingBoxes);
            if (duplicateId is not null)
            {
                capsSkipped++;
                resolvedCapIds.Add(duplicateId);
                continue;
            }

            var regularized = PileBoundaryRegularizer.Regularize(row.Boundary, gridThetaDeg, capLevel.Elevation);
            if (regularized.FallbackReason is not null && errors.Count < 20)
                errors.Add($"{row.Label}: {regularized.FallbackReason}");

            if (regularized.Loop is null)
            {
                capsFailed++;
                resolvedCapIds.Add(null);
                if (errors.Count < 20)
                    errors.Add($"{row.Label}: không dựng được biên dạng, bỏ qua.");
                continue;
            }

            try
            {
                var floor = Floor.Create(doc, new List<CurveLoop> { regularized.Loop }, capFloorType.Id, capLevel.Id, true, null, 0.0);
                capsCreated++;
                resolvedCapIds.Add(floor.Id);
                createdCapIds.Add(floor.Id);
                // Luu lai dung CurveLoop da dung de dung Floor - lop lot doc lai loop nay thay vi doc qua
                // Sketch.Profile (tranh sai so lam tron/mat do chinh xac khi round-trip qua Revit).
                createdCapProfiles[floor.Id] = regularized.Loop;
            }
            catch (Exception ex)
            {
                capsFailed++;
                resolvedCapIds.Add(null);
                if (errors.Count < 20)
                    errors.Add($"{row.Label}: {ex.Message}");
            }
        }


        return new PlaceSummary(
            pilesCreated, pilesFailed, pilesSkipped, capsCreated, capsFailed, capsSkipped, errors,
            createdPileIds, createdCapIds, resolvedCapIds, createdCapProfiles);
    }

    /// <summary>Bounding box (X/Y, đã mở rộng expandMm) của các element cho trước — dùng chung cho mọi cơ chế chống trùng (cọc/đài/lớp lót).</summary>
    internal static List<(PlanBox Box, ElementId Id)> BuildExistingBoxes(IEnumerable<Element> elements, double expandMm)
    {
        var expandFeet = UnitUtils.ConvertToInternalUnits(expandMm, UnitTypeId.Millimeters);
        var boxes = new List<(PlanBox, ElementId)>();

        foreach (var element in elements)
        {
            BoundingBoxXYZ? bb;
            try
            {
                bb = element.get_BoundingBox(null);
            }
            catch (Exception)
            {
                bb = null;
            }

            if (bb is null)
                continue;

            boxes.Add((
                new PlanBox(bb.Min.X - expandFeet, bb.Max.X + expandFeet, bb.Min.Y - expandFeet, bb.Max.Y + expandFeet),
                element.Id));
        }

        return boxes;
    }

    /// <summary>Tìm ElementId của box đã có chứa điểm cho trước — null nếu không trùng box nào.</summary>
    internal static ElementId? FindDuplicate(XYZ point, List<(PlanBox Box, ElementId Id)> boxes)
    {
        foreach (var (box, id) in boxes)
        {
            if (box.Contains(point))
                return id;
        }

        return null;
    }

    /// <summary>Tên Family của mọi Type có parameter Diameter (đặc trưng riêng của Type cọc, không phải đài) trong danh sách Type đang có.</summary>
    internal static HashSet<string> FindPileFamilyNames(IEnumerable<SymbolChoice> typeChoices) =>
        typeChoices
            .Where(c => ReadLengthMm(c.Symbol, PileDiameterParam) is not null)
            .Select(c => c.Symbol.Family.Name)
            .ToHashSet();

    /// <summary>Đặt 1 Family Instance cọc tại targetZ tuỳ ý (không nhất thiết = level.Elevation — level chỉ là Level tham chiếu truyền vào constructor), offset = 0 (gốc chèn family CHƯA xác nhận). embedmentFeet set thẳng vào biến Minimum Embedment của cọc (nếu family có) thay vì cộng vào targetZ.</summary>
    private static PlaceResult PlaceOne(Document doc, XYZ centerXY, FamilySymbol symbol, Level level, double targetZ, double embedmentFeet)
    {
        try
        {
            if (!symbol.IsActive)
                symbol.Activate();

            var point = new XYZ(centerXY.X, centerXY.Y, targetZ);

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.Footing);
            }
            catch (Exception)
            {
                instance = doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            }

            instance.LookupParameter(PileEmbedmentParam)?.Set(embedmentFeet);

            FixElevation(doc, instance, targetZ);

            return new PlaceResult(PlaceOutcome.Created, CreatedId: instance.Id);
        }
        catch (Exception ex)
        {
            return new PlaceResult(PlaceOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Đo lại Z thực tế sau khi đặt và bù chênh lệch về đúng targetZ — tránh trường hợp overload NewFamilyInstance có Level cộng dồn thêm Level.Elevation vào Z đã truyền vào.</summary>
    private static void FixElevation(Document doc, FamilyInstance instance, double targetZ)
    {
        doc.Regenerate();

        if (instance.Location is not LocationPoint lp)
            return;

        var deltaZ = targetZ - lp.Point.Z;
        if (Math.Abs(deltaZ) > 1e-6)
            ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, deltaZ));
    }

    internal static string DescribeSymbol(FamilySymbol symbol)
    {
        var categoryName = symbol.Category?.Name ?? "(?)";
        return $"{categoryName} : {symbol.Family.Name} : {symbol.Name}";
    }

    /// <summary>Tự tạo Type cọc còn thiếu (v7 Job W, tham khảo PlaceColumn.ColumnPlacer.CreateMissingTypes) — nhóm nào chưa có Type khớp đúng đường kính (IsExactMatch=false) thì duplicate Type gần đúng nhất đang gán, đổi tên theo đường kính thật + set lại tham số Diameter, tránh nhiều nhóm khác đường kính bị gán lẫn chung 1 Type.</summary>
    public static CreateTypesSummary CreateMissingTypes(Document doc, List<PileGroup> groups)
    {
        var created = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdChoices = new List<SymbolChoice>();


        foreach (var group in groups)
        {
            if (group.IsExactMatch)
                continue;

            var template = group.SelectedType?.Symbol;
            if (template is null)
            {
                failed++;
                errors.Add($"Cọc {group.DiameterLabel}: chưa có Type gần đúng nào để nhân bản — chọn tay 1 Type trong ComboBox trước.");
                continue;
            }

            var newName = $"{(long)Math.Round(group.DiameterMm)}";
            var family = template.Family;
            var existingNames = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id)?.Name)
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existingNames.Contains(newName))
            {
                failed++;
                errors.Add($"Cọc {group.DiameterLabel}: tên Type \"{newName}\" đã tồn tại trong family \"{family.Name}\" — bỏ qua, KHÔNG ghi đè.");
                continue;
            }

            FamilySymbol newSymbol;
            try
            {
                if (template.Duplicate(newName) is not FamilySymbol duplicated)
                    throw new InvalidOperationException("Duplicate không trả về FamilySymbol.");
                newSymbol = duplicated;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Cọc {group.DiameterLabel}: lỗi khi tạo Type — {ex.Message}");
                continue;
            }

            var diameterParam = newSymbol.LookupParameter(PileDiameterParam);
            if (diameterParam is null || diameterParam.StorageType != StorageType.Double)
            {
                failed++;
                errors.Add($"Family \"{family.Name}\" không có tham số \"{PileDiameterParam}\" dạng Length — Type \"{newName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                continue;
            }

            diameterParam.Set(UnitUtils.ConvertToInternalUnits(group.DiameterMm, UnitTypeId.Millimeters));

            created++;
            var choice = new SymbolChoice(newSymbol, DescribeSymbol(newSymbol));
            createdChoices.Add(choice);
            group.SelectedType = choice;
            group.IsExactMatch = true;
        }

        return new CreateTypesSummary(created, failed, errors, createdChoices);
    }
}

internal sealed record CreateTypesSummary(int Created, int Failed, List<string> Errors, List<SymbolChoice> CreatedChoices);

internal sealed record PlaceSummary(
    int PilesCreated, int PilesFailed, int PilesSkipped,
    int CapsCreated, int CapsFailed, int CapsSkipped,
    List<string> Errors,
    IReadOnlyList<ElementId> CreatedPileIds,
    IReadOnlyList<ElementId> CreatedCapIds,
    /// <summary>1 ElementId? song song từng dòng đài trong bảng — mới tạo, đã có sẵn (trùng), hoặc null nếu lỗi/không đặt được.</summary>
    IReadOnlyList<ElementId?> ResolvedCapIds,
    /// <summary>CurveLoop sạch (đã regularize) THẬT SỰ dùng để dựng từng đài mới tạo trong lượt này — lớp lót đọc lại từ đây thay vì đọc qua Sketch.Profile để tránh sai số round-trip.</summary>
    IReadOnlyDictionary<ElementId, CurveLoop> CreatedCapProfiles);
