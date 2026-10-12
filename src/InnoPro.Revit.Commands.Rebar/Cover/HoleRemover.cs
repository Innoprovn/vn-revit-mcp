using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Rebar.Cover;

/// <summary>Nhận diện và loại bỏ lỗ khoét khỏi khối, bằng TOPOLOGY thuần — không dung sai, không
/// đoán, không boolean.
///
/// Hai câu hỏi cần trả lời, và Revit cho sẵn dữ liệu để trả lời cả hai:
///   1. Mặt nào chứa biên dạng lỗ? Mỗi mặt có nhiều EdgeLoop; vòng lớn nhất là biên ngoài, MỌI vòng
///      còn lại đều là biên dạng của một lỗ mở ra tại mặt đó. Bỏ các vòng này đi là mặt liền lại.
///   2. Mặt nào là thành lỗ? Đi từ cạnh của vòng lỗ sang mặt bên kia — đó là mặt thành lỗ đầu tiên —
///      rồi LOANG tiếp qua các mặt kề, dừng đúng ở những cạnh thuộc vòng lỗ. Tập loang được chính là
///      toàn bộ thành lỗ, kể cả lỗ xuyên suốt (loang chạm vòng lỗ ở cả hai đầu rồi dừng).
///
/// Bỏ hai thứ đó TRƯỚC KHI dựng bất cứ thứ gì, phần còn lại là vỏ ngoài liền lạc.
///
/// LỊCH SỬ - ĐỪNG LẶP LẠI: bản trước nhận diện thành lỗ bằng "mặt cong". Ống đã tessellate thì thành
/// ống là MẶT PHẲNG, nên nó bỏ vòng biên lỗ ở hai mặt đầu mà vẫn giữ nguyên facet thành ống — vỏ
/// thủng, BRepBuilder.Finish() trả về Failure. Nhận diện theo topology thì không dính bẫy đó, hình
/// gốc là BRep hay đã tessellate đều đúng.</summary>
internal static class HoleRemover
{
    private const double Tolerance = 1e-6;

    /// <summary>Một mặt của vỏ ngoài, chỉ còn vòng biên ngoài.</summary>
    public sealed record ShellFace(PlanarFace Face, List<XYZ> OuterLoop);

    public sealed record Shell(
        List<ShellFace> Faces, int HoleFacesRemoved, int HoleLoopsRemoved, string? Error);

    public static Shell Extract(Solid solid)
    {
        var faces = new List<Face>();
        foreach (Face face in solid.Faces)
            faces.Add(face);

        if (faces.Count < 4)
            return new Shell(new List<ShellFace>(), 0, 0, "khối có quá ít mặt");

        // --- 1. Đọc vòng biên của từng mặt, tách vòng ngoài và vòng lỗ ---
        var points = new PointIndex();
        var loops = new List<List<LoopEdge>>[faces.Count];
        var outerIndex = new int[faces.Count];

        for (var i = 0; i < faces.Count; i++)
        {
            var read = ReadLoops(faces[i], points, out var outer);
            if (read is null)
                return new Shell(new List<ShellFace>(), 0, 0, "không đọc được vòng biên của mặt");

            loops[i] = read;
            outerIndex[i] = outer;
        }

        // --- 2. Cạnh nào thuộc vòng lỗ thì là "cạnh chặn" ---
        var occurrences = new Dictionary<EdgeKey, List<(int Face, bool Inner)>>();
        for (var i = 0; i < faces.Count; i++)
        {
            for (var l = 0; l < loops[i].Count; l++)
            {
                var inner = l != outerIndex[i];
                foreach (var edge in loops[i][l])
                {
                    if (!occurrences.TryGetValue(edge.Key, out var list))
                        occurrences[edge.Key] = list = new List<(int, bool)>();
                    list.Add((i, inner));
                }
            }
        }

        var barriers = new HashSet<EdgeKey>(
            occurrences.Where(pair => pair.Value.Any(use => use.Inner)).Select(pair => pair.Key));

        if (barriers.Count == 0)
            return BuildShell(faces, loops, outerIndex, new HashSet<int>(), 0);

        // --- 3. Loang từ mặt thành lỗ, dừng ở cạnh chặn ---
        var holeFaces = new HashSet<int>();
        var queue = new Queue<int>();

        foreach (var key in barriers)
        {
            foreach (var (face, inner) in occurrences[key])
            {
                // Phía KHÔNG phải vòng lỗ chính là mặt thành lỗ.
                if (!inner && holeFaces.Add(face))
                    queue.Enqueue(face);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var loop in loops[current])
            {
                foreach (var edge in loop)
                {
                    if (barriers.Contains(edge.Key))
                        continue; // chạm biên dạng lỗ thì dừng, không loang ra vỏ ngoài

                    foreach (var (neighbour, _) in occurrences[edge.Key])
                    {
                        if (neighbour != current && holeFaces.Add(neighbour))
                            queue.Enqueue(neighbour);
                    }
                }
            }
        }

        // Chốt an toàn: loang mà nuốt quá nửa số mặt thì đang gỡ nhầm vỏ ngoài chứ không phải lỗ.
        if (holeFaces.Count * 2 >= faces.Count)
        {
            return new Shell(new List<ShellFace>(), 0, 0,
                $"loang nhận diện lỗ ăn tới {holeFaces.Count}/{faces.Count} mặt nên bỏ");
        }

        return BuildShell(faces, loops, outerIndex, holeFaces, barriers.Count);
    }

    private static Shell BuildShell(
        List<Face> faces, List<List<LoopEdge>>[] loops, int[] outerIndex, HashSet<int> holeFaces,
        int holeLoops)
    {
        var shell = new List<ShellFace>();

        for (var i = 0; i < faces.Count; i++)
        {
            if (holeFaces.Contains(i))
                continue;

            if (faces[i] is not PlanarFace planar)
            {
                return new Shell(new List<ShellFace>(), 0, 0,
                    "vỏ ngoài còn mặt cong nên chưa dựng lại được");
            }

            var outer = loops[i][outerIndex[i]];
            if (outer.Any(edge => edge.Curved))
            {
                return new Shell(new List<ShellFace>(), 0, 0,
                    "vỏ ngoài có cạnh cong nên chưa dựng lại được");
            }

            shell.Add(new ShellFace(planar, outer.Select(edge => edge.Start).ToList()));
        }

        return shell.Count < 4
            ? new Shell(new List<ShellFace>(), 0, 0, "vỏ ngoài không đủ mặt")
            : new Shell(shell, holeFaces.Count, holeLoops, null);
    }

    // ===================== Đọc topology =====================

    private sealed record LoopEdge(EdgeKey Key, XYZ Start, bool Curved);

    /// <summary>Định danh cạnh: cặp id đỉnh CỘNG điểm giữa. Chỉ dùng cặp đỉnh là không đủ — vòng tròn
    /// gồm hai nửa cung có chung hai đỉnh, sẽ bị coi là một cạnh.</summary>
    private readonly record struct EdgeKey(int A, int B, long X, long Y, long Z);

    private static List<List<LoopEdge>>? ReadLoops(Face face, PointIndex points, out int outer)
    {
        outer = 0;
        var loops = new List<List<LoopEdge>>();
        var spans = new List<double>();

        foreach (EdgeArray edgeLoop in face.EdgeLoops)
        {
            var edges = new List<LoopEdge>();
            var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);

            foreach (Edge edge in edgeLoop)
            {
                Curve curve;
                try
                {
                    curve = edge.AsCurveFollowingFace(face);
                }
                catch (Exception)
                {
                    return null;
                }

                var start = curve.GetEndPoint(0);
                var end = curve.GetEndPoint(1);
                var middle = curve.Evaluate(0.5, true);

                var a = points.Intern(start);
                var b = points.Intern(end);
                edges.Add(new LoopEdge(
                    new EdgeKey(Math.Min(a, b), Math.Max(a, b),
                        Quantize(middle.X), Quantize(middle.Y), Quantize(middle.Z)),
                    start,
                    curve is not Line));

                min = new XYZ(Math.Min(min.X, start.X), Math.Min(min.Y, start.Y), Math.Min(min.Z, start.Z));
                max = new XYZ(Math.Max(max.X, start.X), Math.Max(max.Y, start.Y), Math.Max(max.Z, start.Z));
            }

            if (edges.Count < 2)
                continue;

            loops.Add(edges);
            spans.Add((max - min).GetLength());
        }

        if (loops.Count == 0)
            return null;

        outer = spans.IndexOf(spans.Max());
        return loops;
    }

    private static long Quantize(double value) => (long)Math.Round(value / Tolerance);

    private sealed class PointIndex
    {
        private readonly Dictionary<(long, long, long), int> _cells = new();
        private readonly List<XYZ> _points = new();

        public int Intern(XYZ point)
        {
            var (x, y, z) = Key(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (_cells.TryGetValue((x + dx, y + dy, z + dz), out var found) &&
                            _points[found].DistanceTo(point) <= Tolerance)
                        {
                            return found;
                        }
                    }
                }
            }

            var id = _points.Count;
            _points.Add(point);
            _cells[(x, y, z)] = id;
            return id;
        }

        private static (long, long, long) Key(XYZ p) =>
            (Quantize(p.X), Quantize(p.Y), Quantize(p.Z));
    }
}
