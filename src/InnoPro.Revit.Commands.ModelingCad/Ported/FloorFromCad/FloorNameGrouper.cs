using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.FloorFromCad;

/// <summary>Gom các vùng hatch thành từng FloorNameGroup — mỗi TÊN (đọc từ Text Note gần tâm vùng) 1
/// nhóm, tự tìm Type (Floor hoặc Ceiling — tuỳ SurfaceKind đang chọn, xem <paramref name="types"/>) để
/// chọn sẵn: ƯU TIÊN Type có tên KHỚP ĐÚNG tên đó (không phân biệt hoa/thường, đã trim); nếu không có,
/// tìm Type có tên CHỨA tên đó như 1 TOKEN riêng — chỉ tự chọn khi CHỈ ĐÚNG 1 Type thoả (tránh chọn nhầm)
/// — rút gọn/mô phỏng đúng logic PlaceDoorWindow\DoorNameGrouper.cs. Vùng KHÔNG đọc được tên (TagName
/// null) mỗi vùng thành 1 nhóm RIÊNG, không auto-match.</summary>
internal static class FloorNameGrouper
{
    public static List<FloorNameGroup> Build(
        List<(RawHatchRegion Region, string? TagName)> detected, IReadOnlyList<HostObjAttributes> types)
    {
        var typeChoices = types
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new FloorTypeChoice(t, t.Name))
            .ToList();

        var byName = typeChoices
            .GroupBy(c => c.Type.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var duplicateBaseChoices = types
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var groups = new List<FloorNameGroup>();
        var unnamedIndex = 1;

        foreach (var byTagGroup in detected.GroupBy(d => d.TagName))
        {
            if (byTagGroup.Key is null)
            {
                foreach (var item in byTagGroup)
                {
                    groups.Add(new FloorNameGroup
                    {
                        Name = $"(Không đọc được tên) — vùng #{unnamedIndex++}",
                        Regions = new List<RawHatchRegion> { item.Region },
                        TypeChoices = typeChoices,
                        DuplicateBaseChoices = duplicateBaseChoices,
                        AutoMatched = false,
                        IsUnnamed = true
                    });
                }

                continue;
            }

            var matched = byName.TryGetValue(byTagGroup.Key, out var choice)
                ? choice
                : FindSingleContainingMatch(typeChoices, byTagGroup.Key);

            groups.Add(new FloorNameGroup
            {
                Name = byTagGroup.Key,
                Regions = byTagGroup.Select(d => d.Region).ToList(),
                TypeChoices = typeChoices,
                DuplicateBaseChoices = duplicateBaseChoices,
                AutoMatched = matched is not null,
                SelectedChoice = matched
            });
        }

        return groups
            .OrderBy(g => g.AutoMatched ? 0 : g.IsUnnamed ? 2 : 1)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Tìm Type có tên CHỨA <paramref name="tagName"/> như 1 token riêng (không dính liền
    /// chữ/số ở 2 đầu). Trả về null nếu không Type nào thoả, HOẶC nhiều hơn 1 Type cùng thoả.</summary>
    private static FloorTypeChoice? FindSingleContainingMatch(List<FloorTypeChoice> typeChoices, string tagName)
    {
        var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(tagName)}(?![A-Za-z0-9])";
        var candidates = typeChoices
            .Where(c => Regex.IsMatch(c.Type.Name, pattern, RegexOptions.IgnoreCase))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
