using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMTech4D
{
    public enum VisState { Normal, Hidden, Construct, Demolish, Temporary }

    /// <summary>Tính trạng thái hiển thị của từng phần tử tại một ngày. Không đụng tới Revit.</summary>
    public static class Sim
    {
        public static Dictionary<long, VisState> Compute(IEnumerable<Task4D> tasks, DateTime t)
        {
            // Phần tử nằm trong nhiều công việc: lấy công việc bắt đầu gần nhất (đã bắt đầu),
            // nếu chưa công việc nào bắt đầu thì lấy công việc sớm nhất.
            var pick = new Dictionary<long, Task4D>();
            foreach (var task in tasks)
            {
                foreach (var id in task.ElementIds)
                {
                    if (!pick.TryGetValue(id, out var cur)) { pick[id] = task; continue; }
                    bool ts = task.Start <= t, cs = cur.Start <= t;
                    if (ts && (!cs || task.Start > cur.Start)) pick[id] = task;
                    else if (!ts && !cs && task.Start < cur.Start) pick[id] = task;
                }
            }
            return pick.ToDictionary(kv => kv.Key, kv => StateOf(kv.Value, t));
        }

        public static VisState StateOf(Task4D k, DateTime t)
        {
            bool before = t < k.Start, after = t > k.End;
            return k.Type switch
            {
                TaskType.Demolish => before ? VisState.Normal : after ? VisState.Hidden : VisState.Demolish,
                TaskType.Temporary => before || after ? VisState.Hidden : VisState.Temporary,
                _ => before ? VisState.Hidden : after ? VisState.Normal : VisState.Construct,
            };
        }

        public static bool IsActive(VisState s) => s == VisState.Construct || s == VisState.Demolish || s == VisState.Temporary;
    }

    /// <summary>Thông số camera bay quanh: góc quay (độ) và hệ số khoảng cách so với cỡ công trình.</summary>
    public class CameraShot
    {
        public double AngleDeg;
        public double Distance = 2.2;
    }

    /// <summary>Phiên mô phỏng trên một view 3D riêng. Chỉ gọi trong ngữ cảnh Revit API.</summary>
    public class SimSession
    {
        public const string ViewName = "BIMTech 4D";
        public const string CameraViewName = "BIMTech 4D Camera";
        const double PitchDeg = 22;

        public Document Doc { get; private set; }
        public View3D View { get; private set; }
        public bool IsCamera { get; private set; }

        readonly Dictionary<long, VisState> _applied = new Dictionary<long, VisState>();
        readonly Dictionary<VisState, OverrideGraphicSettings> _ov = new Dictionary<VisState, OverrideGraphicSettings>();
        readonly Dictionary<long, double> _top = new Dictionary<long, double>(); // cao độ đỉnh từng phần tử
        XYZ _bbMin, _bbMax;
        double? _camZ;

        public static SimSession Start(UIDocument uidoc, ICollection<long> ids, bool camera)
        {
            var doc = uidoc.Document;
            var s = new SimSession { Doc = doc, IsCamera = camera };
            string name = camera ? CameraViewName : ViewName;
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);

            using (var t = new Transaction(doc, "BIMTech 4D - Chuẩn bị view"))
            {
                t.Start();
                if (view == null)
                {
                    var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                    view = camera ? View3D.CreatePerspective(doc, vft.Id) : View3D.CreateIsometric(doc, vft.Id);
                    try { view.Name = name; } catch { view.Name = name + " " + DateTime.Now.ToString("HHmmss"); }
                    view.DetailLevel = ViewDetailLevel.Medium;
                }
                if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                SetupLook(view, camera);

                // Dọn trạng thái cũ (lần chạy trước bị tắt đột ngột)
                var blank = new OverrideGraphicSettings();
                var unhide = new List<ElementId>();
                foreach (var v in ids)
                {
                    var id = new ElementId(v);
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    if (el.IsHidden(view)) unhide.Add(id);
                    view.SetElementOverrides(id, blank);
                }
                if (unhide.Count > 0) view.UnhideElements(unhide);
                t.Commit();
            }

            if (uidoc.ActiveView.Id != view.Id)
            {
                // Gọi từ lệnh AI (trong ExternalEvent) có lúc Revit khoá việc đổi view: gửi yêu cầu, Revit đổi khi rảnh.
                try { uidoc.ActiveView = view; }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { uidoc.RequestViewChange(view); }
            }
            s.View = view;
            s.BuildOverrides();
            s.MeasureBounds(ids);
            return s;
        }

        /// <summary>Chỉ hiện cấu kiện: tắt analytical, annotation (level, grid...), nền trời cho đẹp.</summary>
        static void SetupLook(View3D view, bool camera)
        {
            void Try(Action a) { try { a(); } catch { /* bản Revit / view không hỗ trợ thì bỏ qua */ } }
            Try(() => view.AreAnalyticalModelCategoriesHidden = true);
            Try(() => view.AreAnnotationCategoriesHidden = true);
            Try(() => view.DisplayStyle = DisplayStyle.ShadingWithEdges);
            Try(() => view.SetBackground(ViewDisplayBackground.CreateGradient(
                new Color(200, 218, 238), new Color(244, 246, 249), new Color(228, 230, 233))));
            if (camera)
            {
                Try(() => view.CropBoxVisible = false);
                Try(() => view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR)?.Set(0));
            }
            else
            {
                Try(() => view.IsSectionBoxActive = false);
            }
        }

        void BuildOverrides()
        {
            var solid = new FilteredElementCollector(Doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            _ov[VisState.Construct] = Make(solid, new Color(16, 185, 129), 10);
            _ov[VisState.Demolish] = Make(solid, new Color(239, 68, 68), 10);
            _ov[VisState.Temporary] = Make(solid, new Color(245, 158, 11), 25);
        }

        static OverrideGraphicSettings Make(FillPatternElement solid, Color c, int transparency)
        {
            var o = new OverrideGraphicSettings();
            if (solid != null)
            {
                o.SetSurfaceForegroundPatternId(solid.Id);
                o.SetSurfaceForegroundPatternColor(c);
                o.SetSurfaceForegroundPatternVisible(true);
                o.SetCutForegroundPatternId(solid.Id);
                o.SetCutForegroundPatternColor(c);
                o.SetCutForegroundPatternVisible(true);
            }
            o.SetSurfaceTransparency(transparency);
            return o;
        }

        void MeasureBounds(IEnumerable<long> ids)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var v in ids)
            {
                var bb = Doc.GetElement(new ElementId(v))?.get_BoundingBox(null);
                if (bb == null) continue;
                _top[v] = bb.Max.Z;
                x0 = Math.Min(x0, bb.Min.X); y0 = Math.Min(y0, bb.Min.Y); z0 = Math.Min(z0, bb.Min.Z);
                x1 = Math.Max(x1, bb.Max.X); y1 = Math.Max(y1, bb.Max.Y); z1 = Math.Max(z1, bb.Max.Z);
            }
            if (x0 > x1) { _bbMin = XYZ.Zero; _bbMax = new XYZ(1, 1, 1); return; }
            _bbMin = new XYZ(x0, y0, z0);
            _bbMax = new XYZ(x1, y1, z1);
        }

        void CheckView()
        {
            if (View == null || !View.IsValidObject)
                throw new InvalidOperationException($"View \"{View?.Name ?? ViewName}\" đã bị xoá. Hãy thoát rồi chạy lại mô phỏng.");
        }

        /// <summary>Chỉ cập nhật những phần tử đổi trạng thái so với lần trước; có camera thì đặt luôn camera.</summary>
        public void Apply(Dictionary<long, VisState> target, CameraShot shot = null)
        {
            CheckView();
            var hide = new List<ElementId>();
            var unhide = new List<ElementId>();
            var ov = new List<(ElementId id, VisState s)>();
            var clear = new List<ElementId>();

            var keys = new HashSet<long>(_applied.Keys);
            keys.UnionWith(target.Keys);
            foreach (var k in keys)
            {
                var now = target.TryGetValue(k, out var a) ? a : VisState.Normal;
                var old = _applied.TryGetValue(k, out var b) ? b : VisState.Normal;
                if (now == old) continue;

                var id = new ElementId(k);
                var el = Doc.GetElement(id);
                if (el == null) { _applied.Remove(k); continue; }

                if (now == VisState.Hidden) { if (el.CanBeHidden(View) && !el.IsHidden(View)) hide.Add(id); }
                else if (old == VisState.Hidden) { if (el.IsHidden(View)) unhide.Add(id); }

                if (Sim.IsActive(now)) ov.Add((id, now));
                else if (Sim.IsActive(old)) clear.Add(id);

                if (now == VisState.Normal) _applied.Remove(k); else _applied[k] = now;
            }

            bool cam = shot != null && IsCamera;
            if (hide.Count + unhide.Count + ov.Count + clear.Count == 0 && !cam) return;

            using var t = new Transaction(Doc, "BIMTech 4D");
            t.Start();
            if (unhide.Count > 0) View.UnhideElements(unhide);
            if (hide.Count > 0) View.HideElements(hide);
            var blank = new OverrideGraphicSettings();
            foreach (var id in clear) View.SetElementOverrides(id, blank);
            foreach (var (id, s) in ov) View.SetElementOverrides(id, _ov[s]);
            if (cam) PlaceCamera(shot);
            t.Commit();
        }

        /// <summary>
        /// Camera xoay quanh tâm công trình, độ cao bám theo phần đang thi công (đỉnh cao nhất đang hiện),
        /// nhìn chếch xuống một góc cố định về phía công trình.
        /// </summary>
        void PlaceCamera(CameraShot shot)
        {
            double zMin = _bbMin.Z, zMax = _bbMax.Z, h = Math.Max(1, zMax - zMin);
            double built = zMin;
            foreach (var kv in _top)
            {
                if (_applied.TryGetValue(kv.Key, out var s) && s == VisState.Hidden) continue;
                if (kv.Value > built) built = kv.Value;
            }
            _camZ = _camZ == null ? built : _camZ + (built - _camZ.Value) * 0.25; // bám mượt, không giật

            var c = (_bbMin + _bbMax) / 2;
            double footprint = Math.Max(_bbMax.X - _bbMin.X, _bbMax.Y - _bbMin.Y);
            double size = Math.Max(footprint, Math.Min(h, footprint * 1.5));
            double dist = size * shot.Distance;

            double tz = Math.Max(zMin, _camZ.Value - Math.Min(h, footprint) * 0.35);
            var target = new XYZ(c.X, c.Y, tz);
            double a = shot.AngleDeg * Math.PI / 180, p = PitchDeg * Math.PI / 180;
            var eye = new XYZ(c.X + dist * Math.Cos(p) * Math.Cos(a), c.Y + dist * Math.Cos(p) * Math.Sin(a), tz + dist * Math.Sin(p));
            var fwd = (target - eye).Normalize();
            var right = fwd.CrossProduct(XYZ.BasisZ).Normalize();
            var up = right.CrossProduct(fwd).Normalize();
            View.SetOrientation(new ViewOrientation3D(eye, up, fwd));
        }

        /// <summary>Trả view về bình thường.</summary>
        public void Reset()
        {
            if (View == null || !View.IsValidObject || !Doc.IsValidObject) return;
            var blank = new OverrideGraphicSettings();
            using var t = new Transaction(Doc, "BIMTech 4D - Kết thúc mô phỏng");
            t.Start();
            var unhide = new List<ElementId>();
            foreach (var kv in _applied)
            {
                var id = new ElementId(kv.Key);
                var el = Doc.GetElement(id);
                if (el == null) continue;
                if (el.IsHidden(View)) unhide.Add(id);
                if (Sim.IsActive(kv.Value)) View.SetElementOverrides(id, blank);
            }
            if (unhide.Count > 0) View.UnhideElements(unhide);
            t.Commit();
            _applied.Clear();
        }
    }
}
