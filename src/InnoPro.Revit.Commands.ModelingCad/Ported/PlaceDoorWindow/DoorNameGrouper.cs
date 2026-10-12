using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Gom các OpeningInstance thành từng DoorNameGroup — mỗi TÊN (đọc từ Text Note gần nhất) 1
/// nhóm, tự tìm Type (Door/Window) để chọn sẵn: ƯU TIÊN Type có tên KHỚP ĐÚNG tên đó (không phân biệt
/// hoa/thường, đã trim); nếu không có, tìm Type có tên CHỨA tên đó như 1 TOKEN riêng (vd tên đọc được
/// "D10" khớp Type "D10-2000x2000" vì sau "D10" là dấu "-", không phải chữ/số — nhưng KHÔNG khớp Type
/// "D10A-..." vì sau "D10" là chữ "A", vẫn thuộc cùng token — tránh nhận nhầm "D1" khớp "D10-..."). Chỉ
/// tự chọn khi CHỈ ĐÚNG 1 Type thoả — nhiều Type cùng chứa tên đó thì coi là chưa xác định (tránh chọn
/// nhầm), người dùng tự chọn tay. Vị trí KHÔNG đọc được tên (TagName null) mỗi vị trí thành 1 nhóm
/// RIÊNG, không auto-match, người dùng tự chọn Type tay — vẫn đặt được, chỉ là không tự động.
///
/// Mọi nhóm (kể cả đã auto-match) đều mang theo FamilyChoices (danh sách Family Door/Window trong model)
/// — dùng cho nhóm CHƯA có Type khớp: người dùng chọn Family + nhập Width/Height thay vì chọn thẳng
/// Type có sẵn, tool tự Duplicate Type mới đúng kích thước lúc đặt (xem DoorWindowPlacer,
/// DoorNameGroup.SelectedFamily/CustomWidthMm/CustomHeightMm). CustomWidthMm gợi ý sẵn bằng bề rộng đo
/// được từ CAD cho các nhóm này, đỡ phải gõ lại.</summary>
internal static class DoorNameGrouper
{
    public static List<DoorNameGroup> Build(
        List<OpeningInstance> instances, IReadOnlyList<FamilySymbol> doorTypes, IReadOnlyList<FamilySymbol> windowTypes)
    {
        var typeChoices = doorTypes.Concat(windowTypes)
            .OrderBy(t => t.Family.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new SymbolChoice(t, $"{t.Family.Name} : {t.Name}"))
            .ToList();

        var byName = typeChoices
            .GroupBy(c => c.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var familyChoices = typeChoices
            .Select(c => c.Symbol.Family)
            .GroupBy(f => f.Id)
            .Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var groups = new List<DoorNameGroup>();
        var unnamedIndex = 1;

        foreach (var byTagGroup in instances.GroupBy(i => i.TagName))
        {
            if (byTagGroup.Key is null)
            {
                foreach (var instance in byTagGroup)
                {
                    groups.Add(new DoorNameGroup
                    {
                        Name = $"(Không đọc được tên) — vị trí #{unnamedIndex++}",
                        Instances = new List<OpeningInstance> { instance },
                        TypeChoices = typeChoices,
                        FamilyChoices = familyChoices,
                        AutoMatched = false,
                        IsUnnamed = true,
                        CustomWidthMm = instance.WidthMm
                    });
                }

                continue;
            }

            var matched = byName.TryGetValue(byTagGroup.Key, out var choice)
                ? choice
                : FindSingleContainingMatch(typeChoices, byTagGroup.Key);
            var tagInstances = byTagGroup.ToList();
            groups.Add(new DoorNameGroup
            {
                Name = byTagGroup.Key,
                Instances = tagInstances,
                TypeChoices = typeChoices,
                FamilyChoices = familyChoices,
                AutoMatched = matched is not null,
                SelectedChoice = matched,
                CustomWidthMm = tagInstances[0].WidthMm
            });
        }

        return groups
            .OrderBy(g => g.AutoMatched ? 0 : g.IsUnnamed ? 2 : 1)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Tìm Type có tên CHỨA <paramref name="tagName"/> như 1 token riêng (không dính liền
    /// chữ/số ở 2 đầu) — xem doc-comment lớp. Trả về null nếu không Type nào thoả, HOẶC nhiều hơn 1 Type
    /// cùng thoả (không đủ chắc chắn để tự chọn, người dùng tự chọn tay).</summary>
    private static SymbolChoice? FindSingleContainingMatch(List<SymbolChoice> typeChoices, string tagName)
    {
        var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(tagName)}(?![A-Za-z0-9])";
        var candidates = typeChoices
            .Where(c => Regex.IsMatch(c.Symbol.Name, pattern, RegexOptions.IgnoreCase))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
