using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceColumn;

/// <summary>Gộp nhóm cột theo kích thước b×h + đặt Family Instance thật vào model, đúng góc xoay CAD.</summary>
internal static class ColumnPlacer
{
    private const double SnapUnitMm = 5.0;

    /// <summary>Ngưỡng mở rộng bounding box (X/Y) khi chống đặt trùng.</summary>
    private const double DuplicateExpandMm = 5.0;

    /// <summary>Tên Parameter kích thước tiết diện — mặc định theo quy ước phổ biến của Revit, CHƯA đối chiếu
    /// với Family cột chuẩn công ty (cần verify trên Revit thật, xem claude/PlaceColumn-handoff.md).</summary>
    private const string ColumnBParam = "b";
    private const string ColumnHParam = "h";

    private const double MatchToleranceMm = 2.0;

    /// <summary>Ngưỡng chồng lấn Z tối thiểu để coi 2 cột khác tầng là trùng — 2 khoảng Z chỉ CHẠM đúng ranh
    /// giới Level (vd đáy cột tầng 2 = đỉnh cột tầng 1, chênh lệch = 0) KHÔNG bị tính là chồng lấn.</summary>
    private const double DuplicateZOverlapTolMm = 10.0;

    /// <summary>1 hộp 3D (X/Y đã mở rộng, Z KHÔNG mở rộng) của 1 element cũ, dùng chống trùng — X/Y xác định
    /// cùng lưới mặt bằng, Z xác định có thật sự chồng lấn cao độ hay chỉ là cột ở tầng khác cùng lưới.</summary>
    internal readonly record struct PlanBox(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
    {
        public bool Contains(XYZ point) => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

        /// <summary>Phần chồng lấn Z (feet) với khoảng [testMinZ, testMaxZ] cho trước — âm/0 nghĩa là không chồng lấn (chỉ chạm hoặc tách rời).</summary>
        public double OverlapZFeet(double testMinZ, double testMaxZ) =>
            Math.Min(MaxZ, testMaxZ) - Math.Max(MinZ, testMinZ);
    }

    /// <summary>Gộp cột theo kích thước b×h đã làm tròn bội 5mm, không phân biệt hướng xoay (cặp kích thước không thứ tự).</summary>
    public static List<ColumnGroup> GroupColumns(IEnumerable<RawColumn> columns, IReadOnlyList<SymbolChoice> typeChoices)
    {
        var byKey = new Dictionary<(long A, long B), (double SizeAMm, double SizeBMm, List<RawColumn> Columns)>();

        foreach (var column in columns)
        {
            var lengthMm = UnitUtils.ConvertFromInternalUnits(column.LengthFeet, UnitTypeId.Millimeters);
            var widthMm = UnitUtils.ConvertFromInternalUnits(column.WidthFeet, UnitTypeId.Millimeters);
            var roundedLength = RoundTo5(lengthMm);
            var roundedWidth = RoundTo5(widthMm);
            var sizeA = Math.Min(roundedLength, roundedWidth);
            var sizeB = Math.Max(roundedLength, roundedWidth);
            var key = ((long)Math.Round(sizeA * 100), (long)Math.Round(sizeB * 100));

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (sizeA, sizeB, new List<RawColumn>());
                byKey[key] = entry;
            }

            entry.Columns.Add(column);
            byKey[key] = entry;
        }

        return byKey.Values
            .OrderBy(e => e.SizeBMm).ThenBy(e => e.SizeAMm)
            .Select(e =>
            {
                var group = new ColumnGroup
                {
                    SizeAMm = e.SizeAMm,
                    SizeBMm = e.SizeBMm,
                    Columns = e.Columns,
                    TypeChoices = typeChoices.ToList(),
                };
                var exactMatch = FindExactMatch(e.SizeAMm, e.SizeBMm, typeChoices);
                if (exactMatch != null)
                {
                    group.SelectedType = exactMatch;
                    group.IsExactMatch = true;
                }
                else
                {
                    group.SelectedType = AutoMatchColumnType(e.SizeAMm, e.SizeBMm, typeChoices);
                    group.IsExactMatch = false;
                }
                return group;
            })
            .ToList();
    }

    public static bool IsMatch(SymbolChoice choice, double sizeAMm, double sizeBMm)
    {
        var b = ReadLengthMm(choice.Symbol, ColumnBParam);
        var h = ReadLengthMm(choice.Symbol, ColumnHParam);
        if (b is null || h is null) return false;

        var matchesStraight = Math.Abs(b.Value - sizeAMm) <= MatchToleranceMm && Math.Abs(h.Value - sizeBMm) <= MatchToleranceMm;
        var matchesSwapped = Math.Abs(b.Value - sizeBMm) <= MatchToleranceMm && Math.Abs(h.Value - sizeAMm) <= MatchToleranceMm;
        return matchesStraight || matchesSwapped;
    }

    public static SymbolChoice? FindExactMatch(double sizeAMm, double sizeBMm, IReadOnlyList<SymbolChoice> choices)
    {
        return choices.FirstOrDefault(choice => IsMatch(choice, sizeAMm, sizeBMm));
    }

    private static double RoundTo5(double valueMm) =>
        Math.Round(valueMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;

    /// <summary>Đọc 1 parameter Length của Type, quy đổi ra mm — null nếu Type không có parameter này.</summary>
    private static double? ReadLengthMm(FamilySymbol symbol, string paramName)
    {
        var p = symbol.LookupParameter(paramName);
        if (p is null || p.StorageType != StorageType.Double)
            return null;

        return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
    }

    /// <summary>Type có (b, h) gần đúng nhất với cặp kích thước nhóm cột — thử cả 2 chiều gán vì cặp kích thước
    /// nhóm không có thứ tự cố định. Null nếu không Type nào có đủ cả 2 parameter.</summary>
    public static SymbolChoice? AutoMatchColumnType(double sizeAMm, double sizeBMm, IReadOnlyList<SymbolChoice> choices)
    {
        SymbolChoice? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var b = ReadLengthMm(choice.Symbol, ColumnBParam);
            var h = ReadLengthMm(choice.Symbol, ColumnHParam);
            if (b is null || h is null)
                continue;

            var diffStraight = Math.Abs(b.Value - sizeAMm) + Math.Abs(h.Value - sizeBMm);
            var diffSwapped = Math.Abs(b.Value - sizeBMm) + Math.Abs(h.Value - sizeAMm);
            var diff = Math.Min(diffStraight, diffSwapped);

            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Đặt toàn bộ cột vào model trong 1 Transaction, bỏ qua vị trí đã có sẵn (chống trùng).</summary>
    public static PlaceSummary Place(Document doc, List<ColumnGroup> groups, Level? baseLevel, Level? topLevel)
    {
        var created = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();
        var createdIds = new List<ElementId>();
        var createdFrom = new List<(RawColumn Column, ElementId Id)>();

        var existingBoxesByFamily = new Dictionary<string, List<(PlanBox Box, ElementId Id)>>(StringComparer.Ordinal);

        List<(PlanBox Box, ElementId Id)> BoxesFor(string familyName)
        {
            if (!existingBoxesByFamily.TryGetValue(familyName, out var boxes))
            {
                var elements = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => string.Equals(fi.Symbol?.Family?.Name, familyName, StringComparison.Ordinal));
                boxes = BuildExistingBoxes(elements, DuplicateExpandMm);
                existingBoxesByFamily[familyName] = boxes;
            }

            return boxes;
        }

        // 1 luot Dat luon dung dung 1 cap Level day/dinh cho moi nhom - tinh 1 lan ngoai vong lap, dung
        // chung de phan biet cot cung luoi X/Y nhung khac tang (xem PlanBox/FindDuplicate).
        var testMinZ = baseLevel?.Elevation ?? 0.0;
        var testMaxZ = topLevel?.Elevation ?? 0.0;


        foreach (var group in groups)
        {
            var symbol = group.SelectedType?.Symbol;
            if (symbol is null || baseLevel is null || topLevel is null)
            {
                failed += group.Columns.Count;
                if (errors.Count < 20)
                    errors.Add($"Cột {group.SizeLabel}: chưa gán đủ Type/Level, bỏ qua toàn bộ nhóm.");
                continue;
            }

            var existingBoxes = BoxesFor(symbol.Family.Name);

            foreach (var column in group.Columns)
            {
                if (FindDuplicate(column.Center, existingBoxes, testMinZ, testMaxZ) is not null)
                {
                    skipped++;
                    continue;
                }

                var result = PlaceOne(doc, column, symbol, baseLevel, topLevel);
                if (result.Outcome == PlaceOutcome.Created)
                {
                    created++;
                    if (result.CreatedId is not null)
                    {
                        createdIds.Add(result.CreatedId);
                        createdFrom.Add((column, result.CreatedId));
                    }
                }
                else
                {
                    failed++;
                    if (result.ErrorMessage is not null && errors.Count < 20)
                        errors.Add($"Cột {group.SizeLabel}: {result.ErrorMessage}");
                }
            }
        }


        return new PlaceSummary(created, failed, skipped, errors, createdIds) { CreatedFrom = createdFrom };
    }

    /// <summary>Bounding box (X/Y, đã mở rộng expandMm) của các element cho trước — dùng chung cho cơ chế chống trùng.</summary>
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

            // Z KHONG mo rong theo expandMm - nguong mo rong 5mm chi ap dung cho X/Y (chong dat trung tren
            // cung mat bang), chieu Z dung nguong chong lan rieng o FindDuplicate (DuplicateZOverlapTolMm).
            boxes.Add((
                new PlanBox(bb.Min.X - expandFeet, bb.Max.X + expandFeet, bb.Min.Y - expandFeet, bb.Max.Y + expandFeet, bb.Min.Z, bb.Max.Z),
                element.Id));
        }

        return boxes;
    }

    /// <summary>Tìm ElementId của box đã có chứa điểm cho trước VÀ thật sự chồng lấn cao độ với [testMinZ, testMaxZ]
    /// (cột khác tầng cùng lưới X/Y không bị coi là trùng) — null nếu không trùng box nào.</summary>
    internal static ElementId? FindDuplicate(XYZ point, List<(PlanBox Box, ElementId Id)> boxes, double testMinZ, double testMaxZ)
    {
        var tolFeet = UnitUtils.ConvertToInternalUnits(DuplicateZOverlapTolMm, UnitTypeId.Millimeters);

        foreach (var (box, id) in boxes)
        {
            if (box.Contains(point) && box.OverlapZFeet(testMinZ, testMaxZ) > tolFeet)
                return id;
        }

        return null;
    }

    /// <summary>Đặt 1 Family Instance Cột giữa Level đáy/đỉnh, xoay đúng góc CAD dựa trên (b, h) của Type đã chọn.</summary>
    private static PlaceResult PlaceOne(Document doc, RawColumn column, FamilySymbol symbol, Level baseLevel, Level topLevel)
    {
        try
        {
            if (!symbol.IsActive)
                symbol.Activate();

            var point = new XYZ(column.Center.X, column.Center.Y, baseLevel.Elevation);
            var instance = doc.Create.NewFamilyInstance(point, symbol, baseLevel, StructuralType.Column);

            instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.Set(baseLevel.Id);
            instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
            instance.get_Parameter(BuiltInParameter.SCHEDULE_BASE_LEVEL_OFFSET_PARAM)?.Set(0.0);
            instance.get_Parameter(BuiltInParameter.SCHEDULE_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);

            var rotation = DetermineRotation(column, symbol);
            if (Math.Abs(rotation) > 1e-6)
            {
                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation);
            }

            return new PlaceResult(PlaceOutcome.Created, CreatedId: instance.Id);
        }
        catch (Exception ex)
        {
            return new PlaceResult(PlaceOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Chọn xoay trục b (X cục bộ) hay trục h (Y cục bộ) của Type khớp với trục chính CAD (AngleRadians)
    /// — so (b,h) của Type với (Length,Width) đo được của chính cột này, không dùng chung 1 hướng cho cả nhóm.</summary>
    private static double DetermineRotation(RawColumn column, FamilySymbol symbol)
    {
        var bMm = ReadLengthMm(symbol, ColumnBParam);
        var hMm = ReadLengthMm(symbol, ColumnHParam);
        if (bMm is null || hMm is null)
            return column.AngleRadians;

        var lengthMm = UnitUtils.ConvertFromInternalUnits(column.LengthFeet, UnitTypeId.Millimeters);
        var widthMm = UnitUtils.ConvertFromInternalUnits(column.WidthFeet, UnitTypeId.Millimeters);

        var diffStraight = Math.Abs(bMm.Value - lengthMm) + Math.Abs(hMm.Value - widthMm);
        var diffSwapped = Math.Abs(bMm.Value - widthMm) + Math.Abs(hMm.Value - lengthMm);

        return diffStraight <= diffSwapped ? column.AngleRadians : column.AngleRadians - Math.PI / 2;
    }

    internal static string DescribeSymbol(FamilySymbol symbol) => symbol.Name;

    public static CreateTypesSummary CreateMissingTypes(Document doc, List<ColumnGroup> groups, Family templateFamily)
    {
        var created = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdChoices = new List<SymbolChoice>();
        var poolChoices = new List<SymbolChoice>(groups.FirstOrDefault()?.TypeChoices ?? Enumerable.Empty<SymbolChoice>());
        var familyChoices = poolChoices
            .Where(choice => choice.Symbol.Family.Id == templateFamily.Id)
            .ToList();


        foreach (var group in groups)
        {
            if (group.IsExactMatch)
                continue;

            var reuse = FindExactMatch(group.SizeAMm, group.SizeBMm, familyChoices);
            if (reuse is not null)
            {
                group.SelectedType = reuse;
                group.IsExactMatch = true;
                continue;
            }

            var template = AutoMatchColumnType(group.SizeAMm, group.SizeBMm, familyChoices)?.Symbol;
            if (template is null)
            {
                failed++;
                errors.Add($"Family mẫu \"{templateFamily.Name}\" không có Type chứa tham số b/h dạng Length.");
                continue;
            }

            var baseName = $"{(long)Math.Round(group.SizeAMm)}x{(long)Math.Round(group.SizeBMm)}";
            var family = template.Family;
            var existingNames = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id)?.Name)
                .Where(n => n is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newName = baseName;
            for (var suffix = 2; existingNames.Contains(newName); suffix++)
                newName = $"{baseName} ({suffix})";

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
                errors.Add($"Cột {group.SizeLabel}: lỗi khi tạo Type — {ex.Message}");
                continue;
            }

            var bParam = newSymbol.LookupParameter(ColumnBParam);
            var hParam = newSymbol.LookupParameter(ColumnHParam);

            if (bParam is null || hParam is null || bParam.StorageType != StorageType.Double || hParam.StorageType != StorageType.Double)
            {
                failed++;
                errors.Add($"Family \"{family.Name}\" không có tham số b/h dạng Length — Type \"{newName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                continue;
            }

            bParam.Set(UnitUtils.ConvertToInternalUnits(group.SizeAMm, UnitTypeId.Millimeters));
            hParam.Set(UnitUtils.ConvertToInternalUnits(group.SizeBMm, UnitTypeId.Millimeters));

            created++;
            var choice = new SymbolChoice(newSymbol, DescribeSymbol(newSymbol));
            createdChoices.Add(choice);
            poolChoices.Add(choice);
            familyChoices.Add(choice);
            group.SelectedType = choice;
            group.IsExactMatch = true;
        }

        return new CreateTypesSummary(created, failed, errors, createdChoices);
    }
}

internal sealed record CreateTypesSummary(int Created, int Failed, List<string> Errors, List<SymbolChoice> CreatedChoices);

internal sealed record PlaceSummary(
    int Created, int Failed, int Skipped,
    List<string> Errors,
    IReadOnlyList<ElementId> CreatedIds)
{
    /// <summary>Cột vừa tạo kèm khung CAD sinh ra nó (để ghi tên đọc từ CAD).</summary>
    public IReadOnlyList<(RawColumn Column, ElementId Id)> CreatedFrom { get; init; } = [];
}
