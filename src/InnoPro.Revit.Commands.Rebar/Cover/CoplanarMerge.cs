using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Rebar.Cover;

/// <summary>Gộp các mặt ĐỒNG PHẲNG của khối gốc thành một mặt duy nhất trước khi offset.
///
/// Vì sao cần: hình lấy từ link/IFC hay bị chia nhỏ - một mặt phẳng lớn nằm dưới dạng hàng chục
/// tam giác. Offset giữ nguyên topology thì mặt gốc chia bao nhiêu, cover chia bấy nhiêu: đúng quy
/// luật nhưng nhìn nát và không dùng được. Tệ hơn, facet nhỏ hơn chiều dày cover sẽ lật hướng khi
/// dời làm hỏng cả lần dựng.
///
/// Cách gộp: trong cùng một mặt phẳng, cạnh nào được HAI mặt dùng chung thì là cạnh trong - bỏ đi.
/// Các cạnh còn lại (chỉ một mặt dùng) chính là biên của vùng đã gộp, nối lại thành vòng.
///   - Vòng quay thuận theo pháp tuyến = biên ngoài, quay ngược = lỗ. Nhờ giữ nguyên hướng của cạnh
///     lấy từ mặt gốc nên chiều quay tự đúng, không phải đoán.
///   - Một mặt phẳng có thể chứa nhiều mảng rời nhau: mỗi biên ngoài thành một mặt riêng, lỗ gán vào
///     đúng biên ngoài bao nó.
///   - CỐ Ý KHÔNG bỏ đỉnh thẳng hàng trên biên: đỉnh đó vẫn là đỉnh của mặt kề bên, bỏ đi thì hai
///     mặt không còn dùng chung cạnh và BRepBuilder từ chối dựng.</summary>
internal static class CoplanarMerge
{
    /// <summary>Một mặt sau khi gộp: vòng đầu tiên là biên ngoài, các vòng sau là lỗ.</summary>
    public sealed record MergedFace(XYZ Normal, double PlaneDistance, double Cover, List<List<int>> Loops);

    public sealed record SourceFace(XYZ Normal, double PlaneDistance, double Cover, List<List<int>> Loops);

    private const double Tolerance = 1e-6;

    /// <summary>Hai mặt coi là song song khi tích vô hướng pháp tuyến sát 1 tới mức này.</summary>
    private const double NormalTolerance = 1e-9;

    /// <summary>Lệch mặt phẳng dưới mức này (ft, ~0.03 mm) thì coi là cùng mặt phẳng.</summary>
    private const double PlaneTolerance = 1e-4;

    public static List<MergedFace> Run(IReadOnlyList<SourceFace> faces, Func<int, XYZ> pointOf)
    {
        var merged = new List<MergedFace>();

        foreach (var list in GroupByPlane(faces, pointOf))
        {
            if (list.Count == 1)
            {
                var single = list[0];
                merged.Add(new MergedFace(single.Normal, single.PlaneDistance, single.Cover, single.Loops));
                continue;
            }

            var result = Merge(list, pointOf);
            if (result is null)
            {
                // Gộp không xong thì giữ nguyên từng mặt, thà chia nhỏ còn hơn sai topology.
                foreach (var face in list)
                    merged.Add(new MergedFace(face.Normal, face.PlaneDistance, face.Cover, face.Loops));
            }
            else
            {
                merged.AddRange(result);
            }
        }

        return merged;
    }

    /// <summary>Gom mặt theo mặt phẳng.
    ///
    /// LỊCH SỬ - ĐỪNG LẶP LẠI: bản trước gom bằng khoá băm từ (pháp tuyến, n·origin) làm tròn ở
    /// 1e-6 ft. Dự án cầu đường có toạ độ cỡ trăm nghìn feet, sai số double của chính phép nhân
    /// n·origin đã xấp xỉ 1e-6 ft — hai tam giác NẰM CÙNG một mặt phẳng rơi vào hai ô băm khác nhau
    /// nên không bao giờ gộp được, và mọi công sức gộp mặt thành vô ích.
    ///
    /// Nay đo khoảng cách mặt phẳng theo một ĐIỂM MỐC của chính khối (tâm bounding box) nên trị số
    /// nhỏ, rồi so bằng dung sai thay vì băm — hai mặt cùng phẳng luôn về chung nhóm.</summary>
    private static List<List<SourceFace>> GroupByPlane(
        IReadOnlyList<SourceFace> faces, Func<int, XYZ> pointOf)
    {
        var reference = ReferencePoint(faces, pointOf);
        var groups = new List<(XYZ Normal, double Distance, List<SourceFace> Faces)>();

        foreach (var face in faces)
        {
            var distance = face.PlaneDistance - face.Normal.DotProduct(reference);
            var index = groups.FindIndex(g =>
                g.Normal.DotProduct(face.Normal) > 1.0 - NormalTolerance &&
                Math.Abs(g.Distance - distance) <= PlaneTolerance);

            if (index >= 0)
                groups[index].Faces.Add(face);
            else
                groups.Add((face.Normal, distance, new List<SourceFace> { face }));
        }

        return groups.Select(g => g.Faces).ToList();
    }

    private static XYZ ReferencePoint(IReadOnlyList<SourceFace> faces, Func<int, XYZ> pointOf)
    {
        var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);

        foreach (var face in faces)
        {
            foreach (var loop in face.Loops)
            {
                foreach (var id in loop)
                {
                    var p = pointOf(id);
                    min = new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
            }
        }

        return max.X < min.X ? XYZ.Zero : (min + max) / 2.0;
    }

    private static List<MergedFace>? Merge(List<SourceFace> faces, Func<int, XYZ> pointOf)
    {
        // Cạnh có hướng: (a -> b). Cạnh trong xuất hiện đúng hai lần ngược chiều nhau.
        var directed = new Dictionary<(int, int), int>();
        foreach (var face in faces)
        {
            foreach (var loop in face.Loops)
            {
                for (var i = 0; i < loop.Count; i++)
                {
                    var edge = (loop[i], loop[(i + 1) % loop.Count]);
                    directed[edge] = directed.GetValueOrDefault(edge) + 1;
                }
            }
        }

        var boundary = new Dictionary<int, List<int>>();
        var count = 0;
        foreach (var ((a, b), times) in directed)
        {
            var opposite = directed.GetValueOrDefault((b, a));
            var remaining = times - opposite;
            if (remaining <= 0)
                continue;

            if (!boundary.TryGetValue(a, out var targets))
                boundary[a] = targets = new List<int>();

            for (var i = 0; i < remaining; i++)
            {
                targets.Add(b);
                count++;
            }
        }

        if (count == 0)
            return null;

        var loops = Stitch(boundary, count);
        if (loops is null)
            return null;

        return Assemble(loops, faces[0], pointOf);
    }

    /// <summary>Nối các cạnh biên thành vòng kín.
    ///
    /// CỐ Ý bỏ cuộc khi có đỉnh nào có nhiều hơn một cạnh biên đi ra: đó là chỗ hai mảng phẳng chạm
    /// nhau tại một điểm, đi tiếp là đoán mò và dễ sinh vòng sai — mà vòng sai thì BRepBuilder từ
    /// chối cả khối. Nhóm đó giữ nguyên từng mặt, các nhóm khác vẫn gộp bình thường.</summary>
    private static List<List<int>>? Stitch(Dictionary<int, List<int>> boundary, int edgeCount)
    {
        if (boundary.Values.Any(targets => targets.Count > 1))
            return null;

        var loops = new List<List<int>>();
        var guard = edgeCount + 1;

        while (edgeCount > 0 && guard-- > 0)
        {
            var pending = boundary.FirstOrDefault(pair => pair.Value.Count > 0);
            if (pending.Value is null || pending.Value.Count == 0)
                return null;

            var start = pending.Key;
            if (!boundary.TryGetValue(start, out var first) || first.Count == 0)
                return null;

            var loop = new List<int> { start };
            var current = start;

            while (true)
            {
                if (!boundary.TryGetValue(current, out var targets) || targets.Count == 0)
                    return null;

                var next = targets[0];
                targets.RemoveAt(0);
                edgeCount--;

                if (next == start)
                    break;

                loop.Add(next);
                current = next;

                if (loop.Count > guard)
                    return null;
            }

            if (loop.Count >= 3)
                loops.Add(loop);
        }

        return loops.Count == 0 ? null : loops;
    }

    /// <summary>Vòng quay thuận = biên ngoài (mỗi cái thành một mặt), quay ngược = lỗ, gán vào biên
    /// ngoài nhỏ nhất bao nó.</summary>
    private static List<MergedFace>? Assemble(
        List<List<int>> loops, SourceFace template, Func<int, XYZ> pointOf)
    {
        var normal = template.Normal;
        var u = Perpendicular(normal);
        var v = normal.CrossProduct(u).Normalize();

        var flat = loops
            .Select(loop => loop.Select(id => Project(pointOf(id), u, v)).ToList())
            .ToList();
        var areas = flat.Select(SignedArea).ToList();

        var outerIndices = Enumerable.Range(0, loops.Count).Where(i => areas[i] > 0).ToList();
        if (outerIndices.Count == 0)
            return null;

        var faces = outerIndices.ToDictionary(
            i => i,
            i => new MergedFace(normal, template.PlaneDistance, template.Cover,
                new List<List<int>> { loops[i] }));

        for (var i = 0; i < loops.Count; i++)
        {
            if (areas[i] >= 0)
                continue;

            var host = outerIndices
                .Where(o => Contains(flat[o], flat[i][0]))
                .OrderBy(o => areas[o])
                .FirstOrDefault(-1);

            if (host < 0)
                return null;

            faces[host].Loops.Add(loops[i]);
        }

        return faces.Values.ToList();
    }

    private static XYZ Perpendicular(XYZ normal)
    {
        var candidate = normal.CrossProduct(XYZ.BasisZ);
        if (candidate.GetLength() < Tolerance)
            candidate = normal.CrossProduct(XYZ.BasisX);
        return candidate.Normalize();
    }

    private static (double U, double V) Project(XYZ point, XYZ u, XYZ v) =>
        (point.DotProduct(u), point.DotProduct(v));

    private static double SignedArea(List<(double U, double V)> loop)
    {
        double sum = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            sum += p.U * q.V - q.U * p.V;
        }

        return sum / 2.0;
    }

    private static bool Contains(List<(double U, double V)> polygon, (double U, double V) point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if (a.V > point.V != b.V > point.V &&
                point.U < (b.U - a.U) * (point.V - a.V) / (b.V - a.V) + a.U)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
