using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Rebar.Cover;

/// <summary>Hai phép dọn khối, đều dựa trên một ý: đọc topology của khối đã có rồi dựng lại bằng
/// BRepBuilder, và chỉ nhận kết quả khi kiểm chứng được.
///
///   - FillHoles: bỏ lỗ, chỉ giữ vỏ ngoài. Cover chỉ cần bao ngoài; giữ lỗ theo thì mặt trụ chặn
///     đường offset theo topology, đẩy cả khối xuống cắt boolean và cho ra hình nát.
///   - MergeCoplanarFaces: ghép các mặt lẻ đồng phẳng thành một mặt liền.
///
/// Cả hai chạy trên khối do Revit sinh ra nên topology chắc chắn hợp lệ, không có đỉnh nào phải dời,
/// và kết quả được kiểm bằng phép so hình trước khi nhận.</summary>
internal static class SolidCleanup
{
    private const double Tolerance = 1e-6;

    /// <summary>Soi chất lượng hình gốc: bao nhiêu mặt, có mặt cong không, và topology có lành không
    /// (mỗi cạnh đúng hai mặt dùng chung). Hình lấy từ link mà đã bị tessellate thì mặt vụn rất
    /// nhiều, không mặt cong nào, và thường có hàng trăm cạnh không khớp — mọi thuật toán offset đều
    /// gãy trên hình như vậy, nên phải nói thẳng cho người dùng biết thay vì cố dựng.</summary>
    public static void Inspect(Solid solid, out int faces, out int curvedFaces, out int brokenEdges)
    {
        faces = solid.Faces.Size;
        curvedFaces = 0;
        brokenEdges = 0;

        var points = new PointIndex();
        var loops = new List<List<List<int>>>();

        foreach (Face face in solid.Faces)
        {
            if (face is not PlanarFace planar)
            {
                curvedFaces++;
                continue;
            }

            var read = ReadLoops(planar, points);
            if (read is not null)
                loops.Add(read);
        }

        if (curvedFaces > 0 || loops.Count != faces)
            return; // có mặt cong/cạnh cong thì phép đếm cạnh không nói lên điều gì

        var usage = new Dictionary<(int, int), int>();
        foreach (var face in loops)
        {
            foreach (var loop in face)
            {
                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    usage[key] = usage.GetValueOrDefault(key) + 1;
                }
            }
        }

        brokenEdges = usage.Count(pair => pair.Value != 2);
    }

    /// <summary>Lấp mọi lỗ/hốc rỗng bên trong, chỉ giữ vỏ ngoài.
    ///
    /// Cách làm: giữ đúng vòng biên NGOÀI của từng mặt phẳng, bỏ hết vòng lỗ và bỏ luôn các mặt cong
    /// (thành ống, thành lỗ khoan). Nếu phần còn lại khép kín được — kiểm bằng luật mỗi cạnh đúng hai
    /// mặt dùng chung — thì đúng là chỉ có lỗ bị bỏ, và khối vỏ ngoài dựng ra là hợp lệ. Không khép
    /// kín được thì trả về null, người gọi giữ khối gốc.</summary>
    public static Solid? FillHoles(Solid solid, out string? error, out int holeFaces)
    {
        // Nhận diện lỗ bằng TOPOLOGY (xem HoleRemover): vòng biên không phải vòng ngoài là biên dạng
        // lỗ, và loang từ đó ra là toàn bộ thành lỗ. Luật cũ "mặt cong = thành lỗ" sai với ống đã
        // tessellate vì thành ống khi đó là mặt phẳng.
        var shell = HoleRemover.Extract(solid);
        error = shell.Error;
        holeFaces = shell.HoleFacesRemoved;

        if (shell.Faces.Count == 0)
            return null;

        var points = new PointIndex();
        var faces = shell.Faces
            .Select(face => new CoplanarMerge.SourceFace(
                face.Face.FaceNormal,
                face.Face.FaceNormal.DotProduct(face.Face.Origin),
                0,
                new List<List<int>> { Orient(face, points) }))
            .ToList();

        var problem = CheckHalfEdges(faces.Select(f => f.Loops));
        if (problem is not null)
        {
            error = $"lấp lỗ: {problem}";
            return null;
        }

        // Vỏ ngoài phải BAO trọn khối gốc và không được nhỏ đi.
        var filled = BuildEitherOrientation(
            faces, points.Point,
            candidate => candidate.Volume >= solid.Volume - Math.Max(solid.Volume * 1e-6, 1e-9) &&
                         Contains(candidate, solid),
            ref error);

        if (filled is null)
            error ??= "lấp lỗ xong không bao trọn khối gốc nên bỏ";

        return filled;
    }

    /// <summary>Ghép các mặt lẻ đồng phẳng thành một mặt liền. Trả về khối cũ nếu không gộp được.</summary>
    public static Solid MergeCoplanarFaces(
        Solid solid, out int facesBefore, out int facesAfter, out string? error)
    {
        facesBefore = solid.Faces.Size;
        facesAfter = facesBefore;
        error = null;

        var points = new PointIndex();
        var sources = new List<CoplanarMerge.SourceFace>(facesBefore);

        foreach (Face face in solid.Faces)
        {
            if (face is not PlanarFace planar)
            {
                error = "khối có mặt cong nên không gộp";
                return solid;
            }

            var loops = ReadLoops(planar, points);
            if (loops is null)
            {
                error = "khối có cạnh cong nên không gộp";
                return solid;
            }

            sources.Add(new CoplanarMerge.SourceFace(
                planar.FaceNormal, planar.FaceNormal.DotProduct(planar.Origin), 0, loops));
        }

        var merged = CoplanarMerge.Run(sources, points.Point);
        if (merged.Count >= facesBefore)
            return solid;

        var problem = CheckHalfEdges(merged.Select(m => m.Loops));
        if (problem is not null)
        {
            error = $"gộp mặt: {problem}";
            return solid;
        }

        // Gộp mặt KHÔNG được phép đổi hình. Kiểm bằng HIỆU ĐỐI XỨNG chứ không chỉ so thể tích: nối
        // sai vòng biên hoàn toàn có thể ra khối khác hình mà thể tích xấp xỉ bằng.
        var built = BuildEitherOrientation(
            merged.Select(m => new CoplanarMerge.SourceFace(m.Normal, m.PlaneDistance, 0, m.Loops)).ToList(),
            points.Point, candidate => SameShape(candidate, solid), ref error);

        if (built is null)
        {
            error ??= "gộp xong lệch hình nên bỏ";
            return solid;
        }

        facesAfter = merged.Count;
        return built;
    }

    // ===================== Dựng lại =====================

    /// <summary>Dựng thử CẢ HAI chiều mặt rồi lấy cái qua được bài kiểm tra.
    ///
    /// Cố ý không tin vào giả định của mình về quy ước chiều vòng biên của BRepBuilder: đoán sai thì
    /// khối dựng ra lộn trong ra ngoài mà vẫn có thể tích dương, rất khó nhận ra — triệu chứng là
    /// "vỏ ngoài không bao được khối gốc". Thử hai chiều rẻ hơn nhiều so với đoán.</summary>
    private static Solid? BuildEitherOrientation(
        List<CoplanarMerge.SourceFace> faces, Func<int, XYZ> pointOf, Func<Solid, bool> accept,
        ref string? error)
    {
        foreach (var reversed in new[] { false, true })
        {
            string? attemptError = null;
            var candidate = Build(faces, pointOf, reversed, ref attemptError);

            if (candidate is not null && candidate.Volume > 1e-9)
            {
                try
                {
                    if (accept(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                    // Boolean kiểm tra lỗi - coi như không đạt, thử chiều còn lại.
                }
            }

            error ??= attemptError;
        }

        return null;
    }

    private static Solid? Build(
        List<CoplanarMerge.SourceFace> faces, Func<int, XYZ> pointOf, bool reversed, ref string? error)
    {
        try
        {
            var builder = new BRepBuilder(BRepType.Solid);
            builder.SetAllowShortEdges(); // hình từ link luôn có cạnh ngắn, không bật là Finish() fail
            var edges = new Dictionary<(int, int), (BRepBuilderGeometryId Id, int Start)>();

            foreach (var face in faces)
            {
                var plane = Plane.CreateByNormalAndOrigin(face.Normal, face.Normal * face.PlaneDistance);
                var faceId = builder.AddFace(BRepBuilderSurfaceGeometry.Create(plane, null), reversed);

                foreach (var loop in face.Loops)
                {
                    var loopId = builder.AddLoop(faceId);

                    for (var i = 0; i < loop.Count; i++)
                    {
                        var a = loop[i];
                        var b = loop[(i + 1) % loop.Count];
                        var key = (Math.Min(a, b), Math.Max(a, b));

                        if (!edges.TryGetValue(key, out var edge))
                        {
                            edge = (builder.AddEdge(
                                BRepBuilderEdgeGeometry.Create(pointOf(a), pointOf(b))), a);
                            edges[key] = edge;
                        }

                        builder.AddCoEdge(loopId, edge.Id, edge.Start != a);
                    }

                    builder.FinishLoop(loopId);
                }

                builder.FinishFace(faceId);
            }

            var outcome = builder.Finish();
            if (outcome != BRepBuilderOutcome.Success || !builder.IsResultAvailable())
            {
                error = $"BRepBuilder.Finish() = {outcome}";
                return null;
            }

            return builder.GetResult();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Mỗi cạnh phải đúng hai mặt dùng và hai lần đó ngược chiều nhau.</summary>
    private static string? CheckHalfEdges(IEnumerable<List<List<int>>> faces)
    {
        var usage = new Dictionary<(int, int), (int Forward, int Backward)>();

        foreach (var loops in faces)
        {
            foreach (var loop in loops)
            {
                var seen = new HashSet<int>();
                foreach (var id in loop)
                {
                    if (!seen.Add(id))
                        return "vòng biên đi qua một đỉnh hai lần";
                }

                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    var current = usage.GetValueOrDefault(key);
                    usage[key] = a < b
                        ? (current.Forward + 1, current.Backward)
                        : (current.Forward, current.Backward + 1);
                }
            }
        }

        var broken = usage.Count(pair => pair.Value.Forward != 1 || pair.Value.Backward != 1);
        return broken > 0 ? $"{broken} cạnh không khớp hai mặt" : null;
    }

    // ===================== Kiểm hình =====================

    private static bool SameShape(Solid a, Solid b) => Contains(a, b) && Contains(b, a);

    /// <summary>b có nằm trọn trong a không.</summary>
    private static bool Contains(Solid a, Solid b)
    {
        try
        {
            var limit = Math.Max(Math.Max(a.Volume, b.Volume) * 1e-6, 1e-9);
            var outside = BooleanOperationsUtils.ExecuteBooleanOperation(
                b, a, BooleanOperationsType.Difference);
            return outside is null || outside.Volume <= limit;
        }
        catch (Exception)
        {
            return false; // Không kiểm được thì không nhận.
        }
    }

    // ===================== Đọc topology =====================

    /// <summary>Vòng ngoài của mặt vỏ, chỉnh chiều xoay thuận theo pháp tuyến rồi đổi sang id đỉnh.</summary>
    private static List<int> Orient(HoleRemover.ShellFace face, PointIndex points)
    {
        var loop = new List<XYZ>(face.OuterLoop);
        if (Newell(loop).DotProduct(face.Face.FaceNormal) < 0)
            loop.Reverse();

        return loop.Select(points.Intern).ToList();
    }

    private static List<List<int>>? ReadLoops(PlanarFace face, PointIndex points)
    {
        var raw = new List<List<XYZ>>();
        var curved = new List<bool>();

        foreach (EdgeArray edgeLoop in face.EdgeLoops)
        {
            var loop = new List<XYZ>();
            var hasCurve = false;

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

                if (curve is not Line)
                    hasCurve = true;

                loop.Add(curve.GetEndPoint(0));
            }

            if (loop.Count >= 3)
            {
                raw.Add(loop);
                curved.Add(hasCurve);
            }
        }

        if (raw.Count == 0)
            return null;

        var normal = face.FaceNormal;
        var spans = raw.Select(Span).ToList();
        var outer = spans.IndexOf(spans.Max());

        // Vòng ngoài cong thì chịu; vòng lỗ cong không sao vì lúc lấp lỗ ta bỏ nó đi.
        if (curved[outer])
            return null;

        var result = new List<List<int>>();
        for (var i = 0; i < raw.Count; i++)
        {
            var expectOutward = i == outer;
            if (Newell(raw[i]).DotProduct(normal) > 0 != expectOutward)
                raw[i].Reverse();

            var ids = raw[i].Select(points.Intern).ToList();
            if (expectOutward)
                result.Insert(0, ids);
            else
                result.Add(ids);
        }

        return result;
    }

    private static XYZ Newell(IReadOnlyList<XYZ> points)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Count];
            x += (p.Y - q.Y) * (p.Z + q.Z);
            y += (p.Z - q.Z) * (p.X + q.X);
            z += (p.X - q.X) * (p.Y + q.Y);
        }

        return new XYZ(x, y, z);
    }

    private static double Span(IReadOnlyList<XYZ> points)
    {
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        return (max - min).GetLength();
    }

    /// <summary>Gom điểm trùng nhau về một id, có dò ô lân cận để không tách nhầm vì sai số.</summary>
    private sealed class PointIndex
    {
        private readonly Dictionary<(long, long, long), int> _cells = new();
        private readonly List<XYZ> _points = new();

        public XYZ Point(int id) => _points[id];

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

        private static (long, long, long) Key(XYZ p) => (
            (long)Math.Round(p.X / Tolerance),
            (long)Math.Round(p.Y / Tolerance),
            (long)Math.Round(p.Z / Tolerance));
    }
}
