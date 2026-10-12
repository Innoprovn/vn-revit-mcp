using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.CreateBeamType;

/// <summary>Gộp nhóm mặt cắt theo kích thước b×h + tạo Family Type mới nếu chưa có.</summary>
internal static class BeamTypeCreator
{
    private const double SnapUnitMm = 5.0;
    private const double ExistingSizeToleranceMm = 1.0;

    /// <summary>Tên Parameter kích thước tiết diện (theo yêu cầu người dùng: b/h).</summary>
    private const string BeamBParam = "b";
    private const string BeamHParam = "h";

    /// <summary>Gộp mặt cắt đọc thành công theo kích thước b×h đã làm tròn bội 5mm (không tự đảo b/h); mặt cắt lỗi đọc
    /// (thiếu cạnh đứng) thành 1 dòng riêng, không gộp, xếp cuối bảng.</summary>
    public static List<BeamTypeGroup> GroupColumns(IEnumerable<RawBeamSection> columns, FamilyChoice familyChoice)
    {
        var existingSizes = GetExistingTypes(familyChoice.Family).Select(t => (t.BMm, t.HMm)).ToList();

        var byKey = new Dictionary<(long B, long H), (double BMm, double HMm, List<RawBeamSection> Columns)>();
        var errorGroups = new List<BeamTypeGroup>();

        foreach (var column in columns)
        {
            if (column.ErrorMessage is not null || column.BFeet is not double bFeetValue || column.HFeet is not double hFeetValue)
            {
                errorGroups.Add(new BeamTypeGroup(
                    new List<RawBeamSection> { column }, 0, 0, existingSizes,
                    column.ErrorMessage ?? "Không xác định được mặt cắt — thiếu cạnh đứng"));
                continue;
            }

            var bMm = UnitUtils.ConvertFromInternalUnits(bFeetValue, UnitTypeId.Millimeters);
            var hMm = UnitUtils.ConvertFromInternalUnits(hFeetValue, UnitTypeId.Millimeters);
            var roundedB = RoundTo5(bMm);
            var roundedH = RoundTo5(hMm);
            var key = ((long)Math.Round(roundedB * 100), (long)Math.Round(roundedH * 100));

            if (!byKey.TryGetValue(key, out var entry))
                entry = (roundedB, roundedH, new List<RawBeamSection>());

            entry.Columns.Add(column);
            byKey[key] = entry;
        }

        var sizeGroups = byKey.Values
            .OrderBy(e => e.BMm).ThenBy(e => e.HMm)
            .Select(e => new BeamTypeGroup(e.Columns, e.BMm, e.HMm, existingSizes));

        return sizeGroups.Concat(errorGroups).ToList();
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

    private static List<(FamilySymbol Symbol, double BMm, double HMm)> GetExistingTypes(Family family)
    {
        var doc = family.Document;
        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => s.Family.Id == family.Id)
            .ToList();

        var list = new List<(FamilySymbol, double, double)>();
        foreach (var symbol in symbols)
        {
            var b = ReadLengthMm(symbol, BeamBParam);
            var h = ReadLengthMm(symbol, BeamHParam);
            if (b.HasValue && h.HasValue)
            {
                list.Add((symbol, b.Value, h.Value));
            }
        }
        return list;
    }

    /// <summary>So B/H (mm) với danh sách kích thước Type sẵn có trong ngưỡng 1mm — dùng cả lúc quét lẫn lúc sửa tay.</summary>
    internal static bool IsSizeExisting(double bMm, double hMm, IReadOnlyList<(double BMm, double HMm)> existingSizes) =>
        existingSizes.Any(t => Math.Abs(t.BMm - bMm) < ExistingSizeToleranceMm && Math.Abs(t.HMm - hMm) < ExistingSizeToleranceMm);

    /// <summary>Tạo các Type mới cho các nhóm mặt cắt chưa có Type trùng kích thước.</summary>
    public static CreateSummary CreateTypes(Document doc, List<BeamTypeGroup> groups, FamilyChoice familyChoice)
    {
        var created = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        var existingTypes = GetExistingTypes(familyChoice.Family);
        var baseSymbol = existingTypes.FirstOrDefault().Symbol;
        
        if (baseSymbol == null)
        {
             var allSymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family.Id == familyChoice.Family.Id)
                .ToList();
             baseSymbol = allSymbols.FirstOrDefault();
             if (baseSymbol == null)
                 return new CreateSummary(0, groups.Count, 0, new List<string> { "Family không có Type nào để duplicate." });
        }


        foreach (var group in groups)
        {
            if (group.ErrorMessage is not null)
            {
                failed++;
                errors.Add($"Bỏ qua 1 mặt cắt lỗi ({group.ErrorMessage}) — sửa tay B/H trước khi tạo Type.");
                continue;
            }

            if (group.Status == MatchStatus.Existing)
            {
                skipped++;
                continue;
            }

            try
            {
                var typeName = $"{group.BMm:0}x{group.HMm:0}";
                
                // Tránh lỗi nếu Type name đã tồn tại do ng dùng tạo nhưng parameter không khớp (hiếm)
                var existingNameType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => s.Family.Id == familyChoice.Family.Id && s.Name == typeName);
                
                if (existingNameType != null)
                {
                    // Tên đã tồn tại nhưng nhóm này không được phân loại Existing
                    // (kích thước thực tế không khớp) — KHÔNG ghi đè Type cũ theo spec v1.
                    failed++;
                    errors.Add($"Tên Type \"{typeName}\" đã tồn tại nhưng kích thước khác — bỏ qua, không ghi đè.");
                    continue;
                }

                var newSymbol = baseSymbol.Duplicate(typeName) as FamilySymbol;
                if (newSymbol == null)
                {
                    failed++;
                    errors.Add($"Không thể duplicate Type {typeName}.");
                    continue;
                }

                SetTypeSize(newSymbol, group.BMm, group.HMm);

                // Bug 2 guard: đọc lại để xác nhận family thực sự có param b/h dạng Length.
                var bCheck = newSymbol.LookupParameter(BeamBParam);
                var hCheck = newSymbol.LookupParameter(BeamHParam);
                if (bCheck is null || bCheck.StorageType != StorageType.Double
                    || hCheck is null || hCheck.StorageType != StorageType.Double)
                {
                    failed++;
                    errors.Add($"Family \"{familyChoice.Family.Name}\" không có tham số b/h dạng Length — " +
                               $"Type \"{typeName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                    continue;
                }

                created++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Lỗi tạo Type {group.BMm:0}x{group.HMm:0}: {ex.Message}");
            }
        }

        return new CreateSummary(created, failed, skipped, errors);
    }
    
    private static void SetTypeSize(FamilySymbol symbol, double bMm, double hMm)
    {
        var bParam = symbol.LookupParameter(BeamBParam);
        var hParam = symbol.LookupParameter(BeamHParam);
        if (bParam != null && bParam.StorageType == StorageType.Double)
            bParam.Set(UnitUtils.ConvertToInternalUnits(bMm, UnitTypeId.Millimeters));
        if (hParam != null && hParam.StorageType == StorageType.Double)
            hParam.Set(UnitUtils.ConvertToInternalUnits(hMm, UnitTypeId.Millimeters));
    }
}

internal sealed record CreateSummary(
    int Created, int Failed, int Skipped,
    List<string> Errors);
