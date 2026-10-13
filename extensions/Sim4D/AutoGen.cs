using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace BIMTech4D
{
    public enum GroupMode { Category, Parameter, Filter }

    public class GenOptions
    {
        public GroupMode Mode;
        public string ParamName;
        public List<long> FilterIds = new List<long>(); // theo thứ tự thi công
        public bool ByLevel = true;
        public DateTime Start = DateTime.Today;
        public int Days = 7;
    }

    /// <summary>Filter mẫu trong dự án (View Filter hoặc Selection Set).</summary>
    public class FilterInfo : INotifyPropertyChanged
    {
        bool _checked;
        public long Id { get; set; }
        public string Name { get; set; }
        public bool Checked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Tự gán tiến độ theo quy tắc: hạng mục, tham số, hoặc filter mẫu; có thể chia theo tầng.</summary>
    public static class AutoGen
    {
        // Trong một tầng: dầm, sàn của tầng đó đi trước, rồi cột, vách lên tầng trên, sau đó hoàn thiện.
        static readonly (BuiltInCategory cat, string label)[] Order =
        {
            (BuiltInCategory.OST_StructuralFoundation, "Móng"),
            (BuiltInCategory.OST_StructuralFraming, "Dầm"),
            (BuiltInCategory.OST_Floors, "Sàn"),
            (BuiltInCategory.OST_StructuralColumns, "Cột"),
            (BuiltInCategory.OST_Columns, "Cột kiến trúc"),
            (BuiltInCategory.OST_Walls, "Tường / vách"),
            (BuiltInCategory.OST_Stairs, "Cầu thang"),
            (BuiltInCategory.OST_StairsRailing, "Lan can"),
            (BuiltInCategory.OST_Roofs, "Mái"),
            (BuiltInCategory.OST_CurtainWallPanels, "Vách kính"),
            (BuiltInCategory.OST_CurtainWallMullions, "Khung vách kính"),
            (BuiltInCategory.OST_Doors, "Cửa đi"),
            (BuiltInCategory.OST_Windows, "Cửa sổ"),
            (BuiltInCategory.OST_Ceilings, "Trần"),
        };

        // Không đưa vào mô phỏng: phần tử không phải cấu kiện, hoặc là bộ phận con (ẩn cha là ẩn luôn con).
        static readonly HashSet<long> Excluded = new HashSet<long>(new[]
        {
            BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces, BuiltInCategory.OST_HVAC_Zones,
            BuiltInCategory.OST_Lines, BuiltInCategory.OST_SketchLines, BuiltInCategory.OST_Cameras, BuiltInCategory.OST_RvtLinks,
            BuiltInCategory.OST_ProjectBasePoint, BuiltInCategory.OST_SharedBasePoint, BuiltInCategory.OST_IOSModelGroups,
            BuiltInCategory.OST_StairsRuns, BuiltInCategory.OST_StairsLandings, BuiltInCategory.OST_StairsSupports,
            BuiltInCategory.OST_StairsRailingBaluster, BuiltInCategory.OST_RailingTopRail, BuiltInCategory.OST_RailingHandRail,
            BuiltInCategory.OST_RailingSupport, BuiltInCategory.OST_RailingTermination, BuiltInCategory.OST_PointClouds,
            // Điểm gốc, ranh đất, lưới vách kính, đường phụ của lan can, cây cảnh: không phải hạng mục thi công.
            BuiltInCategory.OST_IOS_GeoSite, BuiltInCategory.OST_SiteProperty, BuiltInCategory.OST_SitePropertyLineSegment,
            BuiltInCategory.OST_CurtainGrids, BuiltInCategory.OST_CurtainGridsWall, BuiltInCategory.OST_CurtainGridsRoof,
            BuiltInCategory.OST_CurtainGridsSystem, BuiltInCategory.OST_CurtainGridsCurtaSystem,
            BuiltInCategory.OST_RailingRailPathExtensionLines, BuiltInCategory.OST_RailingRailPathLines, BuiltInCategory.OST_Entourage,
        }.Select(c => (long)c));

        static readonly BuiltInParameter[] LevelParams =
        {
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM,
            BuiltInParameter.LEVEL_PARAM,
        };

        public static List<Element> ModelElements(Document doc)
        {
            var list = new List<Element>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent())
            {
                var c = e.Category;
                if (c == null || c.CategoryType != CategoryType.Model || Excluded.Contains(c.Id.Value)) continue;
                if (e is RevitLinkInstance || e is ImportInstance || e is View) continue;
                // Bỏ nét phác thảo, chú giải, vật liệu..., khối Mass ý tưởng: không phải cấu kiện thi công.
                if (!InnoPro.Revit.Sdk.ElementInfo.IsPhysical(e) || c.BuiltInCategory == BuiltInCategory.OST_Mass || e is InternalOrigin) continue;
                if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                if (e.get_BoundingBox(null) == null) continue;
                list.Add(e);
            }
            return list;
        }

        /// <summary>Tên tham số có trên cấu kiện (lấy mẫu), để người dùng chọn.</summary>
        public static List<string> ParameterNames(Document doc, List<Element> elems)
        {
            var names = new HashSet<string>();
            int stride = Math.Max(1, elems.Count / 3000);
            var types = new HashSet<long>();
            for (int i = 0; i < elems.Count; i += stride)
            {
                var e = elems[i];
                foreach (Parameter p in e.Parameters)
                    if (p.Definition != null && p.StorageType != StorageType.None) names.Add(p.Definition.Name);
                var tid = e.GetTypeId();
                if (tid != ElementId.InvalidElementId && types.Count < 300 && types.Add(tid.Value) && doc.GetElement(tid) is Element t)
                    foreach (Parameter p in t.Parameters)
                        if (p.Definition != null && p.StorageType != StorageType.None) names.Add(p.Definition.Name);
            }
            return names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static List<FilterInfo> Filters(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>()
                .Where(f => f is ParameterFilterElement || f is SelectionFilterElement)
                .Select(f => new FilterInfo { Id = f.Id.Value, Name = f.Name })
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static (List<Task4D> tasks, int skipped) Build(Document doc, GenOptions o)
        {
            var elems = ModelElements(doc);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var lvIndex = levels.Select((l, i) => (l, i)).ToDictionary(x => x.l.Id.Value, x => x.i);

            // nhóm → hạng (thứ tự thi công)
            var rank = new Dictionary<string, double>();
            Func<Element, string> groupOf;
            switch (o.Mode)
            {
                case GroupMode.Parameter:
                    if (string.IsNullOrWhiteSpace(o.ParamName)) throw new InvalidOperationException("Chưa chọn tham số.");
                    groupOf = e => ParamValue(doc, e, o.ParamName);
                    break;
                case GroupMode.Filter:
                    if (o.FilterIds.Count == 0) throw new InvalidOperationException("Chưa tick filter nào.");
                    var map = FilterMap(doc, o.FilterIds, out var fnames);
                    for (int i = 0; i < fnames.Count; i++) rank[fnames[i]] = i;
                    groupOf = e => map.TryGetValue(e.Id.Value, out var g) ? g : null;
                    break;
                default:
                    for (int i = 0; i < Order.Length; i++) rank[Order[i].label] = i;
                    var labels = Order.ToDictionary(x => (long)x.cat, x => x.label);
                    groupOf = e => labels.TryGetValue(e.Category.Id.Value, out var l) ? l : e.Category.Name;
                    break;
            }

            var items = new List<(int lv, string g, long id)>();
            int skipped = 0;
            foreach (var e in elems)
            {
                var g = groupOf(e);
                if (g == null) { skipped++; continue; }
                int lv = o.ByLevel ? LevelIndex(doc, e, levels, lvIndex) : 0;
                items.Add((lv, g, e.Id.Value));
            }

            // Nhóm chưa có hạng: xếp tự nhiên (1, 2, 10 thay vì 1, 10, 2), đặt sau các nhóm đã có hạng
            var unranked = items.Select(x => x.g).Distinct().Where(g => !rank.ContainsKey(g)).OrderBy(g => g, NaturalComparer.Instance).ToList();
            for (int i = 0; i < unranked.Count; i++) rank[unranked[i]] = 10000 + i;

            var tasks = new List<Task4D>();
            var d = o.Start.Date;
            foreach (var grp in items.GroupBy(x => (x.lv, x.g)).OrderBy(x => x.Key.lv).ThenBy(x => rank[x.Key.g]))
            {
                string name = grp.Key.g;
                if (o.ByLevel) name = (grp.Key.lv >= 0 && grp.Key.lv < levels.Count ? levels[grp.Key.lv].Name : "Không rõ tầng") + " - " + name;
                tasks.Add(new Task4D { Name = name, Start = d, End = d.AddDays(o.Days - 1), ElementIds = grp.Select(x => x.id).ToList() });
                d = d.AddDays(o.Days);
            }
            return (tasks, skipped);
        }

        static Dictionary<long, string> FilterMap(Document doc, List<long> filterIds, out List<string> names)
        {
            var map = new Dictionary<long, string>();
            names = new List<string>();
            foreach (var fid in filterIds)
            {
                var f = doc.GetElement(new ElementId(fid)) as FilterElement;
                if (f == null) continue;
                names.Add(f.Name);
                IEnumerable<ElementId> ids;
                if (f is SelectionFilterElement sf) ids = sf.GetElementIds();
                else if (f is ParameterFilterElement pf)
                {
                    var cats = pf.GetCategories();
                    if (cats.Count == 0) continue;
                    var col = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(cats));
                    var ef = pf.GetElementFilter();
                    if (ef != null) col = col.WherePasses(ef);
                    ids = col.ToElementIds();
                }
                else continue;
                foreach (var id in ids) map.TryAdd(id.Value, f.Name); // thuộc nhiều filter: lấy filter đứng trước
            }
            return map;
        }

        static string ParamValue(Document doc, Element e, string name)
        {
            var p = e.LookupParameter(name);
            if (p == null || !p.HasValue) p = doc.GetElement(e.GetTypeId())?.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            var s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            if (string.IsNullOrWhiteSpace(s) && p.StorageType == StorageType.Integer) s = p.AsInteger().ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        static int LevelIndex(Document doc, Element e, List<Level> levels, Dictionary<long, int> lvIndex)
        {
            var id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
            {
                foreach (var bip in LevelParams)
                {
                    var p = e.get_Parameter(bip);
                    if (p == null || p.StorageType != StorageType.ElementId) continue;
                    var v = p.AsElementId();
                    if (v != null && v != ElementId.InvalidElementId) { id = v; break; }
                }
            }
            if (id != null && lvIndex.TryGetValue(id.Value, out int idx)) return idx;
            if (levels.Count == 0) return -1;
            var bb = e.get_BoundingBox(null);
            double z = bb.Min.Z + 0.01;
            int best = -1;
            for (int i = 0; i < levels.Count; i++) if (levels[i].Elevation <= z) best = i;
            return best < 0 ? 0 : best;
        }
    }

    /// <summary>So sánh chuỗi có số theo kiểu tự nhiên: "Đợt 2" đứng trước "Đợt 10".</summary>
    public class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        static readonly Regex Parts = new Regex(@"\d+|\D+");

        public int Compare(string a, string b)
        {
            var pa = Parts.Matches(a ?? ""); var pb = Parts.Matches(b ?? "");
            for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
            {
                string x = pa[i].Value, y = pb[i].Value;
                int c = char.IsDigit(x[0]) && char.IsDigit(y[0])
                    ? decimal.Parse(x).CompareTo(decimal.Parse(y))
                    : string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
            }
            return pa.Count.CompareTo(pb.Count);
        }
    }
}
