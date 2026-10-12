using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;
using Ent = ACadSharp.Entities;
using RXYZ = Autodesk.Revit.DB.XYZ;

namespace InnoPro.Revit.Commands.ModelingCad.Common;

/// <summary>
/// Một dòng chữ dùng làm tag (tên cấu kiện) trong toạ độ mô hình Revit (feet, Z = 0).
/// Center: tâm khung chữ; Direction: hướng dòng chữ; LeaderTips: điểm mũi tên các leader chỉ vào chữ này
/// (bằng chứng mạnh nhất để biết chữ thuộc cấu kiện nào).
/// </summary>
public sealed record TextTag(string Text, RXYZ Center, RXYZ Direction, double HeightFeet, IReadOnlyList<RXYZ> LeaderTips, string Layer, string Source)
{
    /// <summary>Nửa chiều dài và nửa chiều cao khung chữ ước lượng (feet).</summary>
    public double HalfLength => Math.Max(Text.Length, 1) * HeightFeet * 0.35;
    public double HalfHeight => HeightFeet * 0.5;
}

/// <summary>
/// Gom chữ làm tag từ hai nguồn: đọc thẳng file DWG/DXF của CAD link (TEXT, MTEXT, ATTRIB, LEADER, MLEADER,
/// cả chữ nằm trong block) và Text Note thật trong view. Revit API không đọc được chữ trong CAD nên phần đọc file
/// dùng ACadSharp, rồi tự hiệu chỉnh toạ độ file về toạ độ Revit bằng cách so khớp nét vẽ hai bên.
/// </summary>
public static class CadText
{
    private static readonly Dictionary<string, (DateTime Stamp, List<RawText> Texts, List<(double X, double Y)> Points, UnitsType Units)> Cache = new();

    private sealed record RawText(string Text, double X, double Y, double DirX, double DirY, double Height, List<(double X, double Y)> Tips, string Layer, string Kind);

    /// <summary>Đường dẫn file của CAD link; null khi là CAD Import (không còn file) hoặc file không tồn tại.</summary>
    public static string? FilePath(Document doc, ImportInstance cad)
    {
        try
        {
            var typeId = cad.GetTypeId();
            if (!cad.IsLinked || !ExternalFileUtils.IsExternalFileReference(doc, typeId)) return null;
            var path = ModelPathUtils.ConvertModelPathToUserVisiblePath(ExternalFileUtils.GetExternalFileReference(doc, typeId).GetAbsolutePath());
            return File.Exists(path) ? path : null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Toàn bộ tag: chữ trong file CAD (nếu đọc được) + Text Note trong view. <paramref name="status"/> mô tả nguồn
    /// để báo lại cho người dùng/AI (ví dụ "CAD: 152 chữ, 12 leader; Text Note: 3").
    /// </summary>
    public static List<TextTag> Collect(Document doc, ImportInstance cad, View view, out string status, IReadOnlyCollection<string>? layers = null)
    {
        var tags = new List<TextTag>();
        var parts = new List<string>();
        var path = FilePath(doc, cad);
        if (path == null)
            parts.Add(cad.IsLinked ? "CAD: không tìm thấy file" : "CAD Import: không đọc được chữ (dùng Link CAD)");
        else
        {
            try
            {
                var fromFile = ReadFile(doc, cad, view, path, out var note);
                if (layers is { Count: > 0 }) fromFile = fromFile.Where(t => layers.Contains(t.Layer, StringComparer.OrdinalIgnoreCase)).ToList();
                tags.AddRange(fromFile);
                parts.Add($"CAD: {fromFile.Count} chữ, {fromFile.Count(t => t.LeaderTips.Count > 0)} có leader{note}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                parts.Add($"CAD: lỗi đọc file ({ex.GetType().Name}: {ex.Message})");
            }
        }

        var notes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
        foreach (var n in notes)
        {
            var text = Clean(n.Text);
            if (text.Length == 0) continue;
            var box = n.get_BoundingBox(null);
            var center = box == null ? n.Coord : (box.Min + box.Max) / 2;
            var tips = n.GetLeaders().Select(l => Flat(l.End)).ToList();
            var height = n.TextNoteType?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0.008;
            tags.Add(new TextTag(text, Flat(center), Flat(n.BaseDirection).Normalize(), height * Math.Max(view.Scale, 1), tips, "", "TextNote"));
        }
        parts.Add($"Text Note: {notes.Count}");
        status = string.Join("; ", parts);
        return tags;
    }

    private static List<TextTag> ReadFile(Document doc, ImportInstance cad, View view, string path, out string note)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (!Cache.TryGetValue(path, out var entry) || entry.Stamp != stamp)
        {
            CadDocument file = Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase)
                ? DxfReader.Read(path)
                : DwgReader.Read(path);
            var texts = new List<RawText>();
            var points = new List<(double, double)>();
            var leaders = new List<(List<(double X, double Y)> Vertices, Entity? Annotation)>();
            Walk(file.ModelSpace.Entities, texts, points, leaders, 0);
            AttachLeaders(texts, leaders);
            entry = (stamp, texts, points, file.Header.InsUnits);
            Cache[path] = entry;
        }

        var map = Calibrate(doc, cad, view, entry.Points, entry.Units, out note);
        return entry.Texts.Select(t =>
        {
            var c = map.Point(t.X, t.Y);
            var d = map.Vector(t.DirX, t.DirY);
            return new TextTag(t.Text, c, d.GetLength() < 1e-9 ? RXYZ.BasisX : d.Normalize(), t.Height * map.Scale,
                t.Tips.Select(p => map.Point(p.X, p.Y)).ToList(), t.Layer, "CAD " + t.Kind);
        }).ToList();
    }

    // ---------- Đọc file ----------

    private static void Walk(IEnumerable<Entity> entities, List<RawText> texts, List<(double X, double Y)> points,
        List<(List<(double X, double Y)>, Entity?)> leaders, int depth)
    {
        foreach (var e in entities)
        {
            switch (e)
            {
                case Ent.TextEntity t:
                    AddText(texts, t);
                    break;
                case MText m:
                    AddMText(texts, m);
                    break;
                case MultiLeader ml when ml.ContextData is { } ctx:
                {
                    var label = Clean(StripFormat(ctx.TextLabel ?? ""));
                    var tips = ctx.LeaderRoots.SelectMany(r => r.Lines).Where(l => l.Points.Count > 0)
                        .Select(l => (l.Points[0].X, l.Points[0].Y)).ToList();
                    if (label.Length > 0)
                    {
                        var rot = ctx.TextRotation;
                        var h = ctx.TextHeight > 0 ? ctx.TextHeight : 2.5;
                        // TextLocation là góc trên-trái khung chữ: dời về tâm.
                        var (dx, dy) = (Math.Cos(rot), Math.Sin(rot));
                        var half = label.Length * h * 0.35;
                        texts.Add(new RawText(label, ctx.TextLocation.X + dx * half + dy * h * 0.5, ctx.TextLocation.Y + dy * half - dx * h * 0.5,
                            dx, dy, h, tips, ml.Layer?.Name ?? "", "MLEADER"));
                    }
                    break;
                }
                case Ent.Leader leader when leader.Vertices.Count > 0:
                    leaders.Add((leader.Vertices.Select(v => (v.X, v.Y)).ToList(), leader.AssociatedAnnotation));
                    break;
                case Insert insert when depth < 6:
                    foreach (var a in insert.Attributes) AddText(texts, a);
                    IEnumerable<Entity> exploded;
                    try { exploded = insert.Explode().ToList(); }
                    catch { exploded = []; }
                    Walk(exploded, texts, points, leaders, depth + 1);
                    break;
                case Ent.Line line:
                    points.Add((line.StartPoint.X, line.StartPoint.Y));
                    points.Add((line.EndPoint.X, line.EndPoint.Y));
                    break;
                case LwPolyline lw:
                    points.AddRange(lw.Vertices.Select(v => (v.Location.X, v.Location.Y)));
                    break;
                case Polyline2D p2:
                    points.AddRange(p2.Vertices.Select(v => (v.Location.X, v.Location.Y)));
                    break;
            }
        }
    }

    private static void AddText(List<RawText> texts, Ent.TextEntity t)
    {
        var text = Clean(t.Value);
        if (text.Length == 0 || t.Height <= 0) return;
        var (dx, dy) = (Math.Cos(t.Rotation), Math.Sin(t.Rotation));
        var length = text.Length * t.Height * 0.7 * (t.WidthFactor > 0 ? t.WidthFactor : 1);
        var aligned = t.HorizontalAlignment != TextHorizontalAlignment.Left || t.VerticalAlignment != TextVerticalAlignmentType.Baseline;
        var anchor = aligned && (t.AlignmentPoint.X != 0 || t.AlignmentPoint.Y != 0) ? t.AlignmentPoint : t.InsertPoint;
        // Dời điểm neo về tâm khung chữ theo kiểu căn lề.
        var along = t.HorizontalAlignment switch
        {
            TextHorizontalAlignment.Center or TextHorizontalAlignment.Middle => 0.0,
            TextHorizontalAlignment.Right => -length / 2,
            TextHorizontalAlignment.Aligned or TextHorizontalAlignment.Fit => aligned ? -length / 2 : length / 2,
            _ => length / 2,
        };
        var up = t.VerticalAlignment switch
        {
            TextVerticalAlignmentType.Top => -t.Height / 2,
            TextVerticalAlignmentType.Middle => 0.0,
            _ => t.HorizontalAlignment == TextHorizontalAlignment.Middle ? 0.0 : t.Height / 2,
        };
        texts.Add(new RawText(text, anchor.X + dx * along - dy * up, anchor.Y + dy * along + dx * up, dx, dy, t.Height, [], t.Layer?.Name ?? "", "TEXT"));
    }

    private static void AddMText(List<RawText> texts, MText m)
    {
        var lines = m.GetPlainTextLines().Select(Clean).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || m.Height <= 0) return;
        var text = string.Join(" ", lines);
        var dir = m.AlignmentPoint.X != 0 || m.AlignmentPoint.Y != 0
            ? Unit(m.AlignmentPoint.X, m.AlignmentPoint.Y)
            : (Math.Cos(m.Rotation), Math.Sin(m.Rotation));
        var width = m.RectangleWidth > 0 ? Math.Min(m.RectangleWidth, lines.Max(l => l.Length) * m.Height * 0.7) : lines.Max(l => l.Length) * m.Height * 0.7;
        var height = lines.Count * m.Height * 1.6;
        var name = m.AttachmentPoint.ToString();
        var along = name.EndsWith("Left") ? width / 2 : name.EndsWith("Right") ? -width / 2 : 0;
        var up = name.StartsWith("Top") ? -height / 2 : name.StartsWith("Bottom") ? height / 2 : 0;
        var (dx, dy) = dir;
        texts.Add(new RawText(text, m.InsertPoint.X + dx * along - dy * up, m.InsertPoint.Y + dy * along + dx * up, dx, dy, m.Height, [], m.Layer?.Name ?? "", "MTEXT"));
    }

    /// <summary>LEADER thường: mũi tên là đỉnh đầu; gắn vào chữ được liên kết, không có thì chữ gần đuôi leader nhất.</summary>
    private static void AttachLeaders(List<RawText> texts, List<(List<(double X, double Y)> Vertices, Entity? Annotation)> leaders)
    {
        foreach (var (vertices, annotation) in leaders)
        {
            var tip = vertices[0];
            var tail = vertices[^1];
            RawText? target = null;
            if (annotation is MText m)
                target = texts.FirstOrDefault(t => t.Kind == "MTEXT" && Math.Abs(t.Height - m.Height) < 1e-9 && Clean(string.Join(" ", m.GetPlainTextLines())) == t.Text);
            target ??= texts
                .Select(t => (t, d: Math.Sqrt((t.X - tail.X) * (t.X - tail.X) + (t.Y - tail.Y) * (t.Y - tail.Y))))
                .Where(x => x.d <= Math.Max(x.t.Height * 3, x.t.Text.Length * x.t.Height * 0.5))
                .OrderBy(x => x.d).Select(x => x.t).FirstOrDefault();
            target?.Tips.Add(tip);
        }
    }

    // ---------- Hiệu chỉnh toạ độ file → Revit ----------

    private sealed record Mapping(Transform Model, double Scale, double OffsetX, double OffsetY)
    {
        public RXYZ Point(double x, double y)
        {
            var p = Model.OfPoint(new RXYZ(x * Scale + OffsetX, y * Scale + OffsetY, 0));
            return new RXYZ(p.X, p.Y, 0);
        }

        public RXYZ Vector(double x, double y)
        {
            var v = Model.OfVector(new RXYZ(x, y, 0));
            return new RXYZ(v.X, v.Y, 0);
        }
    }

    /// <summary>
    /// Toạ độ trong file × tỉ lệ + dời = toạ độ symbol của CAD trong Revit; rồi Transform của instance ra mô hình.
    /// Thử tỉ lệ theo đơn vị file (dời 0) và tỉ lệ khớp khung bao nét; chọn phương án trùng nhiều đỉnh nét nhất.
    /// </summary>
    private static Mapping Calibrate(Document doc, ImportInstance cad, View view, List<(double X, double Y)> filePoints, UnitsType units, out string note)
    {
        var geometry = cad.get_Geometry(new Options { View = view });
        var instance = geometry?.OfType<GeometryInstance>().FirstOrDefault();
        var model = instance?.Transform ?? cad.GetTotalTransform();
        var revitPoints = new List<RXYZ>();
        if (instance != null) CollectPoints(instance.GetSymbolGeometry(), revitPoints, 0);

        var candidates = new List<(double Scale, double Ox, double Oy)>();
        if (UnitFeet(units) is { } feet) candidates.Add((feet, 0, 0));
        if (filePoints.Count > 1 && revitPoints.Count > 1)
        {
            var (fMinX, fMaxX, fMinY, fMaxY) = (filePoints.Min(p => p.X), filePoints.Max(p => p.X), filePoints.Min(p => p.Y), filePoints.Max(p => p.Y));
            var (rMinX, rMaxX, rMinY, rMaxY) = (revitPoints.Min(p => p.X), revitPoints.Max(p => p.X), revitPoints.Min(p => p.Y), revitPoints.Max(p => p.Y));
            var span = Math.Max(fMaxX - fMinX, fMaxY - fMinY);
            if (span > 1e-9)
            {
                var s = Math.Max(rMaxX - rMinX, rMaxY - rMinY) / span;
                candidates.Add((s, rMinX - fMinX * s, rMinY - fMinY * s));
                if (UnitFeet(units) is { } f2) candidates.Add((f2, rMinX - fMinX * f2, rMinY - fMinY * f2));
            }
        }
        if (candidates.Count == 0) candidates.Add((UnitConvert.MmToFeet(1), 0, 0));

        var best = candidates[0];
        var bestHits = -1;
        if (revitPoints.Count > 0 && filePoints.Count > 0)
        {
            var tol = UnitConvert.MmToFeet(2);
            var grid = revitPoints.Select(p => Key(p.X, p.Y, tol)).ToHashSet();
            var sample = filePoints.Count <= 400 ? filePoints : filePoints.Where((_, i) => i % (filePoints.Count / 400 + 1) == 0).ToList();
            foreach (var c in candidates)
            {
                var hits = sample.Count(p => Near(grid, p.X * c.Scale + c.Ox, p.Y * c.Scale + c.Oy, tol));
                if (hits > bestHits) { bestHits = hits; best = c; }
            }
            note = bestHits * 2 < sample.Count ? $" (khớp toạ độ yếu: {bestHits}/{sample.Count} đỉnh)" : "";
        }
        else note = " (chưa kiểm được toạ độ)";

        return new Mapping(model, best.Scale, best.Ox, best.Oy);
    }

    private static void CollectPoints(GeometryElement element, List<RXYZ> points, int depth)
    {
        foreach (var obj in element)
        {
            switch (obj)
            {
                case Autodesk.Revit.DB.Line l when l.IsBound:
                    points.Add(l.GetEndPoint(0)); points.Add(l.GetEndPoint(1));
                    break;
                case PolyLine p:
                    points.AddRange(p.GetCoordinates());
                    break;
                case GeometryInstance gi when depth < 8:
                    CollectPoints(gi.GetInstanceGeometry(), points, depth + 1);
                    break;
            }
            if (points.Count > 200_000) return;
        }
    }

    private static (long, long) Key(double x, double y, double tol) => ((long)Math.Round(x / tol), (long)Math.Round(y / tol));

    private static bool Near(HashSet<(long, long)> grid, double x, double y, double tol)
    {
        var (kx, ky) = Key(x, y, tol);
        for (var i = -1; i <= 1; i++)
        for (var j = -1; j <= 1; j++)
            if (grid.Contains((kx + i, ky + j))) return true;
        return false;
    }

    private static double? UnitFeet(UnitsType units) => units switch
    {
        UnitsType.Millimeters => UnitConvert.MmToFeet(1),
        UnitsType.Centimeters => UnitConvert.MmToFeet(10),
        UnitsType.Meters => UnitConvert.MmToFeet(1000),
        UnitsType.Inches => 1.0 / 12,
        UnitsType.Feet => 1.0,
        _ => null,
    };

    // ---------- Chuỗi ----------

    private static readonly Regex FormatCodes = new(@"\\[ACcFfHhLlOoQqTtWwKk][^;\\{}]*;?|\\[PpNn~]|[{}]", RegexOptions.Compiled);

    private static string StripFormat(string text) => FormatCodes.Replace(text.Replace("\\P", " "), " ");

    public static string Clean(string? text) =>
        Regex.Replace((text ?? "").Replace(' ', ' ').Replace("%%c", "Ø").Replace("%%C", "Ø"), @"\s+", " ").Trim();

    private static (double, double) Unit(double x, double y)
    {
        var l = Math.Sqrt(x * x + y * y);
        return l < 1e-12 ? (1, 0) : (x / l, y / l);
    }

    private static RXYZ Flat(RXYZ p) => new(p.X, p.Y, 0);
}
