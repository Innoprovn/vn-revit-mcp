using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Rebar.Cover;

/// <summary>Offset khối theo ĐÚNG TOPOLOGY, sau khi đã gộp các mặt đồng phẳng.
///
///   - Gộp mặt đồng phẳng trước (CoplanarMerge): hình từ link hay có một mặt phẳng bị chia thành
///     hàng chục facet, giữ nguyên thì cover cũng chia bấy nhiêu.
///   - Mỗi mặt (đã gộp) dời vào trong đúng chiều dày cover của mặt đó.
///   - Mỗi ĐỈNH mới = giao của các mặt ĐÃ DỜI gặp nhau tại đỉnh đó. Góc tù thì mặt tự kéo dài ra cho
///     gặp nhau, góc nhọn thì tự trim — không cần xử lý riêng từng trường hợp.
///   - Dựng lại bằng BRepBuilder: mặt phẳng thật, cạnh thật, đúng bằng số mặt sau khi gộp.
///
/// Chỉ áp dụng cho khối toàn mặt phẳng và cạnh thẳng. Gặp mặt cong/cạnh cong, hoặc có mặt biến mất
/// khi offset (chi tiết nhỏ hơn cover), thì trả null để người gọi lùi về cắt boolean.
///
/// LỊCH SỬ - ĐỪNG LẶP LẠI:
///   1. Các bản đầu dựng cover bằng cách TRỪ dao vào khối gốc. Phép trừ không tái tạo được ĐỈNH MỚI
///      nên góc lõm luôn còn vấu; nới dao ra để bù thì CurveLoop.CreateViaOffset đẻ loop tự cắt và
///      mọc vây đâm ra ngoài khối.
///   2. Bản sau giải đúng đỉnh nhưng giải trong TOẠ ĐỘ TUYỆT ĐỐI. Dự án cầu đường toạ độ cỡ trăm
///      nghìn feet nên đỉnh lệch khỏi mặt phẳng vài mm, mặt hết phẳng thì builder băm thành tam giác.
///      Nay giải trong toạ độ ĐỊA PHƯƠNG quanh đỉnh gốc (n·u = -cover), sai số cỡ nano.</summary>
internal static class PlaneOffsetSolid
{
    private const double VertexTolerance = 1e-6; // ft - gom đỉnh GỐC, chỉ để khử sai số

    /// <summary>Hàn đỉnh MỚI: phải lớn hơn short curve tolerance của Revit (1/32 inch), nếu không
    /// offset sẽ đẻ ra cạnh siêu ngắn. BRepBuilder có SetAllowShortEdges nên vẫn dựng được, nhưng
    /// DirectShape.SetShape thì từ chối thẳng ("does not satisfy DirectShape validation criteria").</summary>
    private const double WeldTolerance = 0.0035; // ft (~1.1 mm)
    private const double MinVolume = 1e-9;
    private const double NormalEpsilon = 1e-12;
    private const double Regularization = 1e-6;

    public enum Failure
    {
        None,
        CurvedGeometry,
        Collapsed,
        BuildFailed
    }

    private sealed record FaceData(XYZ Normal, double Offset, List<List<int>> Loops);

    /// <param name="report">Kết quả chẩn đoán: lý do thất bại, có dựng được BRep thật không, và số
    /// mặt trước/sau khi gộp đồng phẳng.</param>
    public static Solid? Build(Solid source, Func<PlanarFace, double> coverOf, out OffsetReport report)
    {
        var failure = Failure.None;
        var exactBRep = false;
        var usedRawTopology = false;
        var sourceFaceCount = 0;
        var mergedFaceCount = 0;
        string? brepError = null;

        try
        {
            return Run(source, coverOf, ref failure, ref exactBRep, ref usedRawTopology,
                ref sourceFaceCount, ref mergedFaceCount, ref brepError);
        }
        finally
        {
            report = new OffsetReport(
                failure, exactBRep, usedRawTopology, sourceFaceCount, mergedFaceCount, brepError);
        }
    }

    public sealed record OffsetReport(
        Failure Failure, bool ExactBRep, bool UsedRawTopology, int SourceFaces, int MergedFaces,
        string? BRepError);

    private static Solid? Run(
        Solid source, Func<PlanarFace, double> coverOf, ref Failure failure, ref bool exactBRep,
        ref bool usedRawTopology, ref int sourceFaceCount, ref int mergedFaceCount,
        ref string? brepError)
    {

        var faces = new List<PlanarFace>();
        foreach (Face face in source.Faces)
        {
            if (face is not PlanarFace planar)
            {
                failure = Failure.CurvedGeometry;
                return null;
            }

            faces.Add(planar);
        }

        if (faces.Count < 4)
        {
            failure = Failure.CurvedGeometry;
            return null;
        }

        sourceFaceCount = faces.Count;

        // --- 1. Đọc topology của khối gốc ---
        var vertices = new VertexIndex();
        var sources = new List<CoplanarMerge.SourceFace>(faces.Count);

        foreach (var face in faces)
        {
            var loops = ReadLoops(face, vertices);
            if (loops is null)
            {
                failure = Failure.CurvedGeometry;
                return null;
            }

            var normal = face.FaceNormal;
            sources.Add(new CoplanarMerge.SourceFace(
                normal, normal.DotProduct(face.Origin), coverOf(face), loops));
        }

        // --- 2. Giải đỉnh trên topology GỐC ---
        // Cố ý dùng mặt gốc chứ không dùng mặt đã gộp: đỉnh nằm lọt trong một vùng phẳng vẫn cần vị
        // trí mới để còn dựng được phương án dự phòng "BRep chưa gộp". Facet đồng phẳng chỉ là ràng
        // buộc lặp lại, không làm đổi nghiệm.
        var raw = sources
            .Select(s => new FaceData(s.Normal, s.PlaneDistance - s.Cover, s.Loops))
            .ToList();

        for (var i = 0; i < raw.Count; i++)
        {
            foreach (var loop in raw[i].Loops)
            {
                foreach (var id in loop)
                    vertices.Link(id, i);
            }
        }

        if (!vertices.Solve(raw.Select(f => (f.Normal, f.Offset)).ToList()))
        {
            failure = Failure.Collapsed;
            return null;
        }

        // --- 3. Dựng trên đúng topology gốc ---
        // CỐ Ý KHÔNG gộp mặt đồng phẳng ở đây. Đã thử: gộp trước khi dời mặt thì phải TỰ dựng lại
        // topology, và chỉ cần nối sai một vòng biên là hình ra sai hẳn - tệ hơn hẳn việc mặt bị chia
        // nhỏ. Việc gộp mặt để riêng cho SolidFaceMerger chạy trên khối ĐÃ DỰNG XONG, nơi topology
        // do Revit bảo đảm và có thể kiểm chứng bằng phép so hình.
        var data = raw;
        mergedFaceCount = data.Count;

        // --- 4. Hàn đỉnh trùng nhau rồi kiểm tra mặt có lộn hướng không ---
        vertices.WeldMoved();

        if (!Clean(data, vertices))
        {
            failure = Failure.Collapsed;
            return null;
        }

        // --- 5. Dựng lại: BRep thật, lỗi thì mới đến shell ---
        var solid = BuildBRep(data, vertices, ref brepError);
        exactBRep = solid is not null;
        usedRawTopology = exactBRep;

        solid ??= BuildTessellated(data, vertices);

        if (solid is null || solid.Volume <= MinVolume)
        {
            exactBRep = false;
            failure = Failure.BuildFailed;
            return null;
        }

        return solid;
    }

    // ===================== Topology =====================

    /// <summary>Vòng đỉnh của mặt theo id đỉnh toàn cục, đã chuẩn hoá chiều xoay: bao ngoài thuận
    /// theo pháp tuyến (đứng đầu danh sách), lỗ thì ngược. Trả null nếu có cạnh cong.</summary>
    private static List<List<int>>? ReadLoops(PlanarFace face, VertexIndex vertices)
    {
        var raw = new List<List<XYZ>>();

        foreach (EdgeArray edgeLoop in face.EdgeLoops)
        {
            var points = new List<XYZ>();
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
                    return null;

                points.Add(curve.GetEndPoint(0));
            }

            if (points.Count >= 3)
                raw.Add(points);
        }

        if (raw.Count == 0)
            return null;

        var normal = face.FaceNormal;
        var spans = raw.Select(Span).ToList();
        var outer = spans.IndexOf(spans.Max());

        var loops = new List<List<int>>();
        for (var i = 0; i < raw.Count; i++)
        {
            var points = raw[i];
            var expectOutward = i == outer;

            if (Newell(points).DotProduct(normal) > 0 != expectOutward)
                points.Reverse();

            var ids = points.Select(vertices.Intern).ToList();
            if (expectOutward)
                loops.Insert(0, ids);
            else
                loops.Add(ids);
        }

        return loops;
    }

    /// <summary>Đổi loop sang id ĐẠI DIỆN sau khi hàn, rồi bỏ đỉnh lặp liên tiếp (cạnh đã co về 0).
    ///
    /// LỊCH SỬ - ĐỪNG LẶP LẠI: bản trước bỏ đỉnh trùng theo KHOẢNG CÁCH trong từng loop riêng lẻ.
    /// Cùng một đỉnh có thể bị mặt này bỏ mà mặt kia giữ (vì hàng xóm trong loop khác nhau), thế là
    /// hai mặt không còn dùng chung cạnh — BRepBuilder đòi mỗi cạnh phải được đúng hai mặt dùng nên
    /// từ chối dựng, rồi rơi xuống shell tam giác. Nay hàn đỉnh MỘT LẦN cho cả khối, mọi mặt dùng
    /// chung id nên bỏ giống hệt nhau.</summary>
    private static List<int>? DropCollapsedVertices(List<int> loop, VertexIndex vertices)
    {
        var cleaned = new List<int>(loop.Count);
        foreach (var id in loop)
        {
            var representative = vertices.Representative(id);
            if (cleaned.Count == 0 || cleaned[^1] != representative)
                cleaned.Add(representative);
        }

        while (cleaned.Count > 2 && cleaned[0] == cleaned[^1])
            cleaned.RemoveAt(cleaned.Count - 1);

        return cleaned.Count < 3 ? null : cleaned;
    }

    /// <summary>Dọn loop của cả bộ mặt: đổi sang đỉnh đại diện, bỏ cạnh co về 0, kiểm tra lật hướng.
    /// Trả false nếu bộ này không dùng được.</summary>
    private static bool Clean(List<FaceData> faces, VertexIndex vertices)
    {
        foreach (var face in faces)
        {
            for (var i = 0; i < face.Loops.Count; i++)
            {
                var cleaned = DropCollapsedVertices(face.Loops[i], vertices);
                if (cleaned is null)
                    return false;

                face.Loops[i] = cleaned;
            }

            if (!OrientationSurvived(face, vertices))
                return false;
        }

        return true;
    }

    /// <summary>Mặt lật hướng sau khi dời = có chi tiết nhỏ hơn chiều dày cover, mặt đó lẽ ra phải
    /// biến mất. Giữ nguyên topology thì không biểu diễn được, nên dừng để người gọi đổi cách.</summary>
    private static bool OrientationSurvived(FaceData face, VertexIndex vertices)
    {
        for (var i = 0; i < face.Loops.Count; i++)
        {
            var moved = face.Loops[i].Select(vertices.Moved).ToList();
            var normal = Newell(moved);
            if (normal.GetLength() <= NormalEpsilon)
                return false;

            var expectOutward = i == 0;
            if (normal.DotProduct(face.Normal) > 0 != expectOutward)
                return false;
        }

        return true;
    }

    // ===================== Dựng khối =====================

    /// <summary>BRep thật: mỗi mặt thành đúng một mặt phẳng, cạnh dùng chung giữa hai mặt.
    /// Kiểm tra topology trước khi gọi builder và ghi lại lý do hỏng, vì BRepBuilder chỉ trả về
    /// Success/Failure chứ không nói vì sao.</summary>
    private static Solid? BuildBRep(List<FaceData> faces, VertexIndex vertices, ref string? error)
    {
        var problem = CheckHalfEdges(faces);
        if (problem is not null)
        {
            error ??= problem;
            return null;
        }

        // Thử cả hai chiều mặt: đoán sai quy ước thì khối lộn trong ra ngoài mà vẫn có thể tích dương.
        foreach (var reversed in new[] { false, true })
        {
            var candidate = BuildBRep(faces, vertices, reversed, ref error);
            if (candidate is not null && candidate.Volume > MinVolume)
                return candidate;
        }

        return null;
    }

    private static Solid? BuildBRep(
        List<FaceData> faces, VertexIndex vertices, bool reversed, ref string? error)
    {
        try
        {
            var builder = new BRepBuilder(BRepType.Solid);
            builder.SetAllowShortEdges(); // offset hay sinh cạnh rất ngắn ở góc nhọn

            // Cạnh chỉ được khai báo MỘT lần rồi hai mặt cùng dùng, nên khoá theo cặp id đỉnh.
            var edges = new Dictionary<(int, int), (BRepBuilderGeometryId Id, int Start)>();

            foreach (var face in faces)
            {
                var plane = Plane.CreateByNormalAndOrigin(face.Normal, face.Normal * face.Offset);
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
                            var geometry = BRepBuilderEdgeGeometry.Create(
                                vertices.Moved(a), vertices.Moved(b));
                            edge = (builder.AddEdge(geometry), a);
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
                error ??= $"BRepBuilder.Finish() = {outcome}";
                return null;
            }

            return builder.GetResult();
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
            return null;
        }
    }

    /// <summary>Điều kiện sống còn của BRepBuilder: mỗi cạnh phải được ĐÚNG hai mặt dùng, và hai lần
    /// đó phải ngược chiều nhau. Kiểm tra trước để biết lỗi nằm ở dữ liệu hay ở builder.</summary>
    private static string? CheckHalfEdges(List<FaceData> faces)
    {
        var usage = new Dictionary<(int, int), (int Forward, int Backward)>();

        foreach (var face in faces)
        {
            foreach (var loop in face.Loops)
            {
                var seen = new HashSet<int>();
                foreach (var id in loop)
                {
                    if (!seen.Add(id))
                        return "loop đi qua cùng một đỉnh hai lần (vùng gộp bị thắt nút)";
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

        var dangling = usage.Count(pair => pair.Value.Forward + pair.Value.Backward != 2);
        if (dangling > 0)
            return $"{dangling} cạnh không được đúng hai mặt dùng chung";

        var mismatched = usage.Count(pair => pair.Value.Forward != 1 || pair.Value.Backward != 1);
        if (mismatched > 0)
            return $"{mismatched} cạnh bị hai mặt đi cùng chiều (sai hướng vòng)";

        return null;
    }

    /// <summary>Dự phòng khi BRepBuilder từ chối: shell. Hình đúng nhưng mặt có thể bị chia nhỏ.</summary>
    private static Solid? BuildTessellated(List<FaceData> faces, VertexIndex vertices)
    {
        try
        {
            var builder = new TessellatedShapeBuilder
            {
                GraphicsStyleId = ElementId.InvalidElementId,
                Target = TessellatedShapeBuilderTarget.Solid,
                Fallback = TessellatedShapeBuilderFallback.Abort
            };

            builder.OpenConnectedFaceSet(true);
            foreach (var face in faces)
            {
                var loops = face.Loops
                    .Select(loop => (IList<XYZ>)loop.Select(vertices.Moved).ToList())
                    .ToList();

                builder.AddFace(new TessellatedFace(loops, ElementId.InvalidElementId));
            }

            builder.CloseConnectedFaceSet();
            builder.Build();

            var result = builder.GetBuildResult();
            return result.Outcome != TessellatedShapeBuilderOutcome.Solid
                ? null
                : result.GetGeometricalObjects().OfType<Solid>().FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ===================== Tiện ích hình học =====================

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

    // ===================== Đỉnh =====================

    /// <summary>Gom đỉnh theo vị trí (dò cả ô lân cận để không tách nhầm đỉnh do sai số), nhớ những
    /// mặt gặp nhau ở đó, rồi giải vị trí mới.</summary>
    private sealed class VertexIndex
    {
        private readonly Dictionary<(long, long, long), int> _cells = new();
        private readonly List<Node> _nodes = new();

        /// <summary>Đăng ký đỉnh, chưa gắn mặt nào (mặt chỉ biết sau khi gộp đồng phẳng).</summary>
        public int Intern(XYZ point)
        {
            var id = Find(point);
            if (id >= 0)
                return id;

            id = _nodes.Count;
            _nodes.Add(new Node(point));
            _cells[Key(point)] = id;
            return id;
        }

        public void Link(int id, int faceIndex) => _nodes[id].Faces.Add(faceIndex);

        public int Representative(int id) => _nodes[id].Representative;

        /// <summary>Hàn các đỉnh có vị trí MỚI trùng nhau thành một, dùng chung cho mọi mặt. Phải làm
        /// một lần cho cả khối chứ không làm riêng từng loop, nếu không topology lệch (xem ghi chú ở
        /// DropCollapsedVertices).</summary>
        public void WeldMoved()
        {
            var cells = new Dictionary<(long, long, long), int>();

            for (var id = 0; id < _nodes.Count; id++)
            {
                var node = _nodes[id];
                if (node.Moved is null)
                    continue;

                var found = -1;
                var (x, y, z) = WeldKey(node.Moved);
                for (var dx = -1; dx <= 1 && found < 0; dx++)
                {
                    for (var dy = -1; dy <= 1 && found < 0; dy++)
                    {
                        for (var dz = -1; dz <= 1 && found < 0; dz++)
                        {
                            if (cells.TryGetValue((x + dx, y + dy, z + dz), out var other) &&
                                _nodes[other].Moved!.DistanceTo(node.Moved) <= WeldTolerance)
                            {
                                found = other;
                            }
                        }
                    }
                }

                if (found >= 0)
                {
                    node.Representative = found;
                }
                else
                {
                    node.Representative = id;
                    cells[(x, y, z)] = id;
                }
            }
        }

        private static (long, long, long) WeldKey(XYZ p) => (
            (long)Math.Round(p.X / WeldTolerance),
            (long)Math.Round(p.Y / WeldTolerance),
            (long)Math.Round(p.Z / WeldTolerance));

        public XYZ Original(int id) => _nodes[id].Original;

        public XYZ Moved(int id) => _nodes[id].Moved!;

        public bool Solve(IReadOnlyList<(XYZ Normal, double Offset)> planes)
        {
            foreach (var node in _nodes)
            {
                // Đỉnh nằm lọt trong vùng đã gộp thì không mặt nào dùng nữa - bỏ qua.
                if (node.Faces.Count == 0)
                    continue;

                var moved = Intersect(node, planes);
                if (moved is null)
                    return false;

                node.Moved = moved;
            }

            return true;
        }

        /// <summary>Giao các mặt đã dời tại đỉnh, giải trong TOẠ ĐỘ ĐỊA PHƯƠNG quanh đỉnh gốc: đặt
        /// x = v0 + u thì phương trình mặt thu gọn thành n·u = -cover, không còn dính toạ độ tuyệt
        /// đối (dự án cầu đường toạ độ rất lớn, giải trực tiếp là mất hết chữ số ý nghĩa).
        /// Ba mặt cho nghiệm đúng; ít hơn hoặc nhiều hơn thì bình phương tối thiểu có ràng buộc nhẹ
        /// kéo về đỉnh gốc, nên hệ luôn giải được và vỏ khối luôn kín.</summary>
        private static XYZ? Intersect(Node node, IReadOnlyList<(XYZ Normal, double Offset)> planes)
        {
            var a = new double[3, 3];
            var b = new double[3];

            foreach (var index in node.Faces)
            {
                var (n, offset) = planes[index];
                var local = offset - n.DotProduct(node.Original);
                double[] v = { n.X, n.Y, n.Z };

                for (var r = 0; r < 3; r++)
                {
                    for (var c = 0; c < 3; c++)
                        a[r, c] += v[r] * v[c];

                    b[r] += local * v[r];
                }
            }

            for (var r = 0; r < 3; r++)
                a[r, r] += Regularization;

            var det = Det(a);
            if (Math.Abs(det) < 1e-12)
                return null;

            var u = new XYZ(
                Det(Replace(a, 0, b)) / det,
                Det(Replace(a, 1, b)) / det,
                Det(Replace(a, 2, b)) / det);

            return node.Original + u;
        }

        private static double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
            m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
            m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static double[,] Replace(double[,] m, int column, double[] values)
        {
            var copy = (double[,])m.Clone();
            for (var r = 0; r < 3; r++)
                copy[r, column] = values[r];
            return copy;
        }

        /// <summary>Dò cả ô lân cận: hai mặt có thể trả về cùng một đỉnh lệch nhau vài phần tỉ, rơi
        /// vào hai ô khác nhau thì đỉnh bị tách đôi và khối hở.</summary>
        private int Find(XYZ point)
        {
            var (x, y, z) = Key(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (_cells.TryGetValue((x + dx, y + dy, z + dz), out var id) &&
                            _nodes[id].Original.DistanceTo(point) <= VertexTolerance)
                        {
                            return id;
                        }
                    }
                }
            }

            return -1;
        }

        private static (long, long, long) Key(XYZ p) => (
            (long)Math.Round(p.X / VertexTolerance),
            (long)Math.Round(p.Y / VertexTolerance),
            (long)Math.Round(p.Z / VertexTolerance));

        private sealed class Node
        {
            public Node(XYZ original) => Original = original;

            public XYZ Original { get; }
            public HashSet<int> Faces { get; } = new();
            public XYZ? Moved { get; set; }

            /// <summary>Id của đỉnh đại diện sau khi hàn (mặc định là chính nó).</summary>
            public int Representative { get; set; } = -1;
        }
    }
}
