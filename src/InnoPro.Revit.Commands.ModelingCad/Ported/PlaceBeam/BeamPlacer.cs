using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceBeam;

/// <summary>Gộp nhóm dầm (theo tên đọc từ Text nếu có, hoặc theo bề rộng b hình học nếu không) + đặt Family
/// Instance thật vào model, đúng vị trí tim dầm đọc từ CAD.</summary>
internal static class BeamPlacer
{
    private const double SnapUnitMm = 5.0;

    private const double DuplicatePositionToleranceMm = 10.0;
    private const double DuplicateAngleToleranceDegrees = 1.0;

    /// <summary>Tên Parameter bề rộng/chiều cao tiết diện — mặc định theo quy ước phổ biến của Revit (giống
    /// PlaceColumn), CHƯA đối chiếu với Family dầm chuẩn công ty (cần verify trên Revit thật). "h" chỉ dùng
    /// để auto-match khi đọc được từ Text (AutoMatchBeamTypeExact) — không đo được từ mặt bằng CAD nên
    /// nhóm gộp theo hình học (không có tên) vẫn chỉ so khớp theo b (AutoMatchBeamType).</summary>
    private const string BeamBParam = "b";
    private const string BeamHParam = "h";

    /// <summary>Dung sai (mm) coi Type là khớp ĐÚNG (b, h) đọc từ Text — dùng cho FindExactMatch/IsMatch/
    /// CreateMissingTypes, giống hệt ngưỡng PlaceColumn dùng cho cột.</summary>
    private const double MatchToleranceMm = 2.0;

    /// <summary>Lựa chọn "không gán" trong ComboBox chọn Shared Parameter ở Bước 1 — chọn mục này thì tên đọc
    /// được từ Text (nếu có) vẫn dùng để gộp nhóm/auto-match Type, chỉ không ghi vào Parameter nào khi đặt.</summary>
    internal const string NoTagParameterLabel = "(Không gán Shared Parameter)";

    internal readonly record struct BeamLine(XYZ Start, XYZ End);

    /// <summary>Gộp dầm theo 3 loại: có TagName (gộp theo tên) — có TagHMm nhưng không tên (Text "bxh" trần, gộp
    /// theo cặp b×h) — không có Text nào (gộp theo b hình học). 2 loại đầu match Type theo cả b và h, loại cuối
    /// chỉ theo b. b luôn lấy từ hình học CAD, không lấy từ Text.</summary>
    public static List<BeamGroup> GroupBeams(IEnumerable<RawBeam> beams, IReadOnlyList<SymbolChoice> typeChoices)
    {
        var beamList = beams.ToList();
        var groups = new List<BeamGroup>();

        // Cùng tên nhưng khác tiết diện (vd D1X-5 nhịp 200x600 và nhịp 200x350) là các nhóm riêng.
        foreach (var byName in beamList
                     .Where(b => b.TagName is not null)
                     .GroupBy(b => (Name: b.TagName!.ToUpperInvariant(),
                         B: RoundTo5(UnitUtils.ConvertFromInternalUnits(b.WidthFeet, UnitTypeId.Millimeters)), H: b.TagHMm ?? 0)))
        {
            var first = byName.First();
            var bMm = byName.Key.B;
            var hMm = first.TagHMm!.Value;

            var group = new BeamGroup
            {
                TagName = first.TagName,
                SizeBMm = bMm,
                SizeHMm = hMm,
                Beams = byName.ToList(),
                TypeChoices = typeChoices.ToList(),
            };
            ApplyExactMatch(group, bMm, hMm, typeChoices);
            groups.Add(group);
        }

        var byDim = new Dictionary<(long B, long H), (double SizeBMm, double SizeHMm, List<RawBeam> Beams)>();
        foreach (var beam in beamList.Where(b => b.TagName is null && b.TagHMm is not null))
        {
            var bMm = RoundTo5(UnitUtils.ConvertFromInternalUnits(beam.WidthFeet, UnitTypeId.Millimeters));
            var hMm = beam.TagHMm!.Value;
            var key = ((long)Math.Round(bMm * 100), (long)Math.Round(hMm * 100));

            if (!byDim.TryGetValue(key, out var dimEntry))
            {
                dimEntry = (bMm, hMm, new List<RawBeam>());
                byDim[key] = dimEntry;
            }

            dimEntry.Beams.Add(beam);
            byDim[key] = dimEntry;
        }

        foreach (var entry in byDim.Values.OrderBy(e => e.SizeBMm).ThenBy(e => e.SizeHMm))
        {
            var group = new BeamGroup
            {
                SizeBMm = entry.SizeBMm,
                SizeHMm = entry.SizeHMm,
                Beams = entry.Beams,
                TypeChoices = typeChoices.ToList(),
            };
            ApplyExactMatch(group, entry.SizeBMm, entry.SizeHMm, typeChoices);
            groups.Add(group);
        }

        var byKey = new Dictionary<long, (double SizeBMm, List<RawBeam> Beams)>();
        foreach (var beam in beamList.Where(b => b.TagName is null && b.TagHMm is null))
        {
            var widthMm = UnitUtils.ConvertFromInternalUnits(beam.WidthFeet, UnitTypeId.Millimeters);
            var rounded = RoundTo5(widthMm);
            var key = (long)Math.Round(rounded * 100);

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (rounded, new List<RawBeam>());
                byKey[key] = entry;
            }

            entry.Beams.Add(beam);
            byKey[key] = entry;
        }

        foreach (var entry in byKey.Values.OrderBy(e => e.SizeBMm))
        {
            var group = new BeamGroup
            {
                SizeBMm = entry.SizeBMm,
                Beams = entry.Beams,
                TypeChoices = typeChoices.ToList(),
            };
            group.SelectedType = AutoMatchBeamType(entry.SizeBMm, typeChoices);
            groups.Add(group);
        }

        foreach (var group in groups)
            group.ShouldModel = !string.IsNullOrWhiteSpace(group.TagName);

        return groups
            .OrderBy(g => g.TagName is null ? 1 : 0)
            .ThenBy(g => g.TagName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.SizeBMm)
            .ToList();
    }

    /// <summary>Gán SelectedType/IsExactMatch cho nhóm theo b×h — dùng chung cho nhóm có tên lẫn nhóm chỉ có kích thước.</summary>
    private static void ApplyExactMatch(BeamGroup group, double bMm, double hMm, IReadOnlyList<SymbolChoice> typeChoices)
    {
        var exactMatch = FindExactMatch(bMm, hMm, typeChoices);
        if (exactMatch is not null)
        {
            group.SelectedType = exactMatch;
            group.IsExactMatch = true;
        }
        else
        {
            group.SelectedType = AutoMatchBeamTypeExact(bMm, hMm, typeChoices);
            group.IsExactMatch = false;
        }
    }

    private static double RoundTo5(double valueMm) =>
        Math.Round(valueMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;

    private static double? ReadLengthMm(FamilySymbol symbol, string paramName)
    {
        var p = symbol.LookupParameter(paramName);
        if (p is null || p.StorageType != StorageType.Double)
            return null;

        return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
    }

    /// <summary>Type có bề rộng "b" gần đúng nhất với bề rộng nhóm dầm — không so "h" vì không đọc được từ
    /// mặt bằng CAD. Null nếu không Type nào có parameter "b".</summary>
    public static SymbolChoice? AutoMatchBeamType(double sizeBMm, IReadOnlyList<SymbolChoice> choices)
    {
        SymbolChoice? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var b = ReadLengthMm(choice.Symbol, BeamBParam);
            if (b is null)
                continue;

            var diff = Math.Abs(b.Value - sizeBMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Type có (b, h) gần đúng nhất với kích thước đọc được từ Text — so cả 2 chiều, không hoán đổi
    /// (thứ tự "bxh" trong Text đã rõ ràng, khác với PlaceColumn nơi góc xoay CAD có thể khiến 2 chiều bị lật).
    /// Null nếu không Type nào có đủ cả 2 parameter "b"/"h".</summary>
    public static SymbolChoice? AutoMatchBeamTypeExact(double bMm, double hMm, IReadOnlyList<SymbolChoice> choices)
    {
        SymbolChoice? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var b = ReadLengthMm(choice.Symbol, BeamBParam);
            var h = ReadLengthMm(choice.Symbol, BeamHParam);
            if (b is null || h is null)
                continue;

            var diff = Math.Abs(b.Value - bMm) + Math.Abs(h.Value - hMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Type có (b, h) khớp ĐÚNG (trong dung sai MatchToleranceMm) với kích thước đọc từ Text — không
    /// hoán đổi (khác PlaceColumn: thứ tự "bxh" trong Text đã rõ ràng, không có ẩn số góc xoay CAD).</summary>
    public static bool IsMatch(SymbolChoice choice, double bMm, double hMm)
    {
        var b = ReadLengthMm(choice.Symbol, BeamBParam);
        var h = ReadLengthMm(choice.Symbol, BeamHParam);
        if (b is null || h is null)
            return false;

        return Math.Abs(b.Value - bMm) <= MatchToleranceMm && Math.Abs(h.Value - hMm) <= MatchToleranceMm;
    }

    public static SymbolChoice? FindExactMatch(double bMm, double hMm, IReadOnlyList<SymbolChoice> choices) =>
        choices.FirstOrDefault(choice => IsMatch(choice, bMm, hMm));

    internal static string DescribeSymbol(FamilySymbol symbol) => symbol.Name;

    /// <summary>Duplicate Type mới + set b/h cho các nhóm có SizeHMm nhưng CHƯA có Type khớp đúng, luôn tự gán vào nhóm — nhiều nhóm khác tên nhưng cùng kích thước thì dùng chung 1 Type vừa tạo/khớp trong cùng lượt gọi, không tạo trùng.</summary>
    public static CreateTypesSummary CreateMissingTypes(Document doc, List<BeamGroup> groups, Family templateFamily)
    {
        var created = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdChoices = new List<SymbolChoice>();
        var poolChoices = new List<SymbolChoice>(groups.FirstOrDefault()?.TypeChoices ?? Enumerable.Empty<SymbolChoice>());
        var familyChoices = poolChoices
            .Where(choice => choice.Symbol.Family.Id == templateFamily.Id)
            .ToList();


        foreach (var group in groups.Where(g => g.ShouldModel))
        {
            if (group.SizeHMm is null || group.IsExactMatch)
                continue;

            var hMm = group.SizeHMm ?? 0;
            var reuse = FindExactMatch(group.SizeBMm, hMm, familyChoices);
            if (reuse is not null)
            {
                group.SelectedType = reuse;
                group.IsExactMatch = true;
                continue;
            }

            var template = AutoMatchBeamTypeExact(group.SizeBMm, hMm, familyChoices)?.Symbol;
            if (template is null)
            {
                failed++;
                errors.Add($"Family mẫu \"{templateFamily.Name}\" không có Type chứa tham số b/h dạng Length.");
                continue;
            }

            var baseName = $"{(long)Math.Round(group.SizeBMm)}x{(long)Math.Round(hMm)}";
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
                errors.Add($"Dầm {group.SizeLabel}: lỗi khi tạo Type — {ex.Message}");
                continue;
            }

            var bParam = newSymbol.LookupParameter(BeamBParam);
            var hParam = newSymbol.LookupParameter(BeamHParam);

            if (bParam is null || hParam is null || bParam.StorageType != StorageType.Double || hParam.StorageType != StorageType.Double)
            {
                failed++;
                errors.Add($"Family \"{family.Name}\" không có tham số b/h dạng Length — Type \"{newName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                continue;
            }

            bParam.Set(UnitUtils.ConvertToInternalUnits(group.SizeBMm, UnitTypeId.Millimeters));
            hParam.Set(UnitUtils.ConvertToInternalUnits(hMm, UnitTypeId.Millimeters));

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

    /// <summary>Đặt toàn bộ dầm vào model trong 1 Transaction, bỏ qua vị trí đã có sẵn (chống trùng). Dầm đặt
    /// thẳng theo Level tham chiếu, offset = 0. <paramref name="tagParameterName"/> — Shared Parameter (nếu
    /// người dùng có chọn ở Bước 1) để ghi TagName của từng nhóm vào — null/rỗng thì không ghi gì (nhóm vẫn
    /// gộp/auto-match theo tên đọc được như bình thường, chỉ là không lưu tên vào Parameter nào).</summary>
    public static PlaceSummary Place(Document doc, List<BeamGroup> groups, Level? level, string? tagParameterName)
    {
        var created = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();
        var createdIds = new List<ElementId>();

        var existingLines = CollectExistingBeamLines(doc);


        foreach (var group in groups.Where(g => g.ShouldModel))
        {
            var symbol = group.SelectedType?.Symbol;
            if (symbol is null || level is null)
            {
                failed += group.Beams.Count;
                if (errors.Count < 20)
                    errors.Add($"Dầm {group.SizeLabel}: chưa gán đủ Type/Level, bỏ qua toàn bộ nhóm.");
                continue;
            }

            foreach (var beam in group.Beams)
            {
                var candidate = new BeamLine(
                    new XYZ(beam.Start.X, beam.Start.Y, level.Elevation),
                    new XYZ(beam.End.X, beam.End.Y, level.Elevation));
                if (existingLines.Any(existing => AreDuplicate(existing, candidate)))
                {
                    skipped++;
                    continue;
                }

                var result = PlaceOne(doc, beam, symbol, level, tagParameterName, group.TagName);
                if (result.Outcome == PlaceOutcome.Created)
                {
                    created++;
                    existingLines.Add(candidate);
                    if (result.CreatedId is not null)
                        createdIds.Add(result.CreatedId);
                }
                else
                {
                    failed++;
                    if (result.ErrorMessage is not null && errors.Count < 20)
                        errors.Add($"Dầm {group.SizeLabel}: {result.ErrorMessage}");
                }
            }
        }


        return new PlaceSummary(created, failed, skipped, errors, createdIds);
    }

    internal static List<BeamLine> CollectExistingBeamLines(Document doc)
    {
        var lines = new List<BeamLine>();
        var beams = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.StructuralType == StructuralType.Beam);

        foreach (var beam in beams)
        {
            if (beam.Location is not LocationCurve { Curve: Line line })
                continue;
            lines.Add(new BeamLine(line.GetEndPoint(0), line.GetEndPoint(1)));
        }

        return lines;
    }

    internal static bool AreDuplicate(BeamLine existing, BeamLine candidate)
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

    /// <summary>Đặt 1 Family Instance Dầm nối 2 đầu tim đọc từ CAD, theo đúng Level tham chiếu (offset = 0).
    /// Ghi <paramref name="tagValue"/> (TagName của nhóm) vào Parameter tên <paramref name="tagParameterName"/>
    /// nếu cả 2 đều có giá trị — lỗi/thiếu Parameter khi ghi KHÔNG làm hỏng việc đặt dầm (bỏ qua âm thầm),
    /// vì dầm đã tạo thành công là phần việc chính, gán tên chỉ là phụ trợ.</summary>
    private static PlaceResult PlaceOne(
        Document doc, RawBeam beam, FamilySymbol symbol, Level level,
        string? tagParameterName, string? tagValue)
    {
        try
        {
            if (!symbol.IsActive)
                symbol.Activate();

            var start = new XYZ(beam.Start.X, beam.Start.Y, level.Elevation);
            var end = new XYZ(beam.End.X, beam.End.Y, level.Elevation);
            var curve = Line.CreateBound(start, end);

            var instance = doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam);

            if (!string.IsNullOrEmpty(tagParameterName) && !string.IsNullOrEmpty(tagValue))
            {
                var tagParam = instance.LookupParameter(tagParameterName);
                if (tagParam is { IsReadOnly: false, StorageType: StorageType.String })
                    tagParam.Set(tagValue);
            }

            return new PlaceResult(PlaceOutcome.Created, CreatedId: instance.Id);
        }
        catch (Exception ex)
        {
            return new PlaceResult(PlaceOutcome.Failed, ex.Message);
        }
    }
}

internal sealed record CreateTypesSummary(int Created, int Failed, List<string> Errors, List<SymbolChoice> CreatedChoices);

internal sealed record PlaceSummary(
    int Created, int Failed, int Skipped,
    List<string> Errors,
    IReadOnlyList<ElementId> CreatedIds);
