using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.LinkToDirectShape;

public sealed record LinkEntry(RevitLinkInstance Instance, string DisplayName);

public sealed record CategoryEntry(long CategoryId, string Name, int Count);

/// <summary>Đọc dữ liệu từ các Revit link đang có trong file host. Toàn bộ class này chỉ đọc, không
/// mở transaction — gọi được cả khi cửa sổ WPF đang mở.</summary>
public static class LinkScanner
{
    /// <summary>Các category không bao giờ sinh khối để convert — bỏ luôn cho danh sách gọn.</summary>
    private static readonly HashSet<long> ExcludedCategories = new()
    {
        (long)BuiltInCategory.OST_RvtLinks,
        (long)BuiltInCategory.OST_Levels,
        (long)BuiltInCategory.OST_Grids,
        (long)BuiltInCategory.OST_Cameras,
        (long)BuiltInCategory.OST_SectionBox,
        (long)BuiltInCategory.OST_IOSModelGroups,
        (long)BuiltInCategory.OST_Views,
        (long)BuiltInCategory.OST_Sheets,
        (long)BuiltInCategory.OST_VolumeOfInterest
    };

    /// <summary>Link đã nạp (GetLinkDocument() != null). Link bị unload không đọc được hình học.</summary>
    public static List<LinkEntry> CollectLoadedLinks(Document doc)
    {
        var result = new List<LinkEntry>();
        foreach (var link in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc is null)
                continue;

            result.Add(new LinkEntry(link, DescribeLink(link, linkDoc)));
        }

        return result.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string DescribeLink(RevitLinkInstance link, Document linkDoc)
    {
        var instanceName = link.Name;
        var title = linkDoc.Title;
        return string.IsNullOrWhiteSpace(instanceName) || instanceName.StartsWith(title, StringComparison.OrdinalIgnoreCase)
            ? instanceName is { Length: > 0 } ? instanceName : title
            : $"{title} ({instanceName})";
    }

    /// <summary>Các category có element trong link, kèm số lượng — dùng cho danh sách lọc.</summary>
    public static List<CategoryEntry> ScanCategories(
        Document hostDoc, RevitLinkInstance link, bool activeViewOnly, View? activeView)
    {
        var counters = new Dictionary<long, (string Name, int Count)>();

        foreach (var element in CollectCandidates(hostDoc, link, activeViewOnly, activeView))
        {
            var category = element.Category;
            if (category is null)
                continue;

            var id = category.Id.Value;
            if (ExcludedCategories.Contains(id))
                continue;

            if (counters.TryGetValue(id, out var current))
                counters[id] = (current.Name, current.Count + 1);
            else
                counters[id] = (category.Name, 1);
        }

        return counters
            .Select(pair => new CategoryEntry(pair.Key, pair.Value.Name, pair.Value.Count))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Element sẽ đem đi convert, đã lọc theo category user chọn.</summary>
    public static List<Element> CollectElements(
        Document hostDoc, RevitLinkInstance link, bool activeViewOnly, View? activeView,
        IReadOnlySet<long> categoryIds)
    {
        var result = new List<Element>();
        foreach (var element in CollectCandidates(hostDoc, link, activeViewOnly, activeView))
        {
            var category = element.Category;
            if (category is null)
                continue;

            var id = category.Id.Value;
            if (ExcludedCategories.Contains(id))
                continue;
            if (categoryIds.Count > 0 && !categoryIds.Contains(id))
                continue;

            result.Add(element);
        }

        return result;
    }

    /// <summary>Element thật (không phải type, không phải view-specific) của link doc. Khi
    /// activeViewOnly bật thì lọc theo đúng những gì view hiện tại của file host đang hiện — dùng
    /// overload collector nhận linkInstanceId (Revit 2023+), lỗi thì lùi về quét cả link doc.</summary>
    private static IEnumerable<Element> CollectCandidates(
        Document hostDoc, RevitLinkInstance link, bool activeViewOnly, View? activeView)
    {
        var linkDoc = link.GetLinkDocument();
        if (linkDoc is null)
            return Array.Empty<Element>();

        if (activeViewOnly && activeView is not null && !activeView.IsTemplate)
        {
            try
            {
                return new FilteredElementCollector(hostDoc, activeView.Id, link.Id)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // View không hỗ trợ lọc theo link (vd view template/schedule) — quét cả link doc.
            }
        }

        return new FilteredElementCollector(linkDoc)
            .WhereElementIsNotElementType()
            .WhereElementIsViewIndependent()
            .ToElements();
    }
}
