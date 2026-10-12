using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Rebar.Cover;

internal sealed record CoverOptions
{
    /// <summary>Cover mặt đáy, đơn vị nội bộ (ft).</summary>
    public double BottomCover { get; init; }

    /// <summary>Cover các mặt còn lại, đơn vị nội bộ (ft).</summary>
    public double SideCover { get; init; }

    /// <summary>Áp cover đáy cho mọi mặt phẳng song song cùng chiều với các mặt đã pick (vd đáy giật
    /// cấp). Tắt = chỉ đúng những mặt phẳng chứa mặt đã pick.</summary>
    public bool ApplyToParallelFaces { get; init; } = true;

    /// <summary>Cho phép khối cover làm host cho rebar. Thực chất là đổi category của DirectShape:
    /// Generic Models KHÔNG bao giờ host được rebar, phải là category kết cấu. Tắt = Generic Models,
    /// khối hoàn toàn trơ, không dính dáng gì tới thép.</summary>
    public bool AllowRebarHost { get; init; }

    /// <summary>Category dùng khi AllowRebarHost bật.</summary>
    public BuiltInCategory HostCategory { get; init; } = BuiltInCategory.OST_StructuralFoundation;

    /// <summary>Chữ ký của những mặt user đã pick để LOẠI khỏi việc dựng cover (mặt thành lỗ, mặt
    /// chứa biên dạng lỗ). Xem CoverBuilder.Signature.</summary>
    public HashSet<string> ExcludedFaces { get; init; } = new();

    /// <summary>Lấp lỗ/hốc rỗng của cấu kiện, chỉ lấy vỏ ngoài để dựng cover. Cover không cần khoét
    /// lỗ theo, mà giữ lỗ thì mặt trụ làm hỏng cả đường dựng.</summary>
    public bool FillHoles { get; init; } = true;

    /// <summary>Bỏ qua đường offset topology, đi thẳng cắt boolean. Dùng khi khối offset dựng ra bị
    /// DirectShape từ chối (cạnh quá ngắn) — cắt boolean cho hình "thô" hơn nhưng Revit nhận.</summary>
    public bool SkipPlaneOffset { get; init; }
}

/// <summary>Một mặt người dùng đã pick để tính cover đáy, trong toạ độ model. Cho phép nhiều mặt vì
/// một cục có thể có nhiều mặt "đáy" khác hướng nhau (đáy giật cấp, mặt tiếp giáp đất, mặt đúc...).</summary>
internal sealed record BottomPlane(XYZ Normal, XYZ Origin);

internal sealed class CoverBuildResult
{
    public List<Solid> Solids { get; } = new();

    /// <summary>Mặt không dựng được dao cắt (mặt suy biến, boolean lỗi).</summary>
    public int SkippedFaces { get; set; }

    /// <summary>Mặt quá nhỏ, bỏ qua để không sinh dao dạng vảy.</summary>
    public int TinyFaces { get; set; }

    /// <summary>Tổng số mặt của khối gốc - dùng để cảnh báo hình đã bị tessellate.</summary>
    public int SourceFaces { get; set; }

    /// <summary>Khối quá vụn, vượt ngưỡng an toàn nên tool không dựng.</summary>
    public bool TooComplex { get; set; }

    /// <summary>Kết quả có phần lòi ra ngoài khối gốc - hình sai, đã loại bỏ.</summary>
    public int Escaped { get; set; }

    /// <summary>Số khối dựng bằng offset theo topology (đường chính, giữ nguyên số mặt).</summary>
    public int OffsetSolids { get; set; }

    /// <summary>Số khối offset đúng topology nhưng phải dựng bằng shell (mặt có thể bị chia nhỏ).</summary>
    public int ShellOffsets { get; set; }

    /// <summary>Số mặt của khối gốc trước/sau khi gộp đồng phẳng — cho biết hình nguồn vụn cỡ nào.</summary>
    public int FacesBeforeMerge { get; set; }

    public int FacesAfterMerge { get; set; }

    /// <summary>Số khối phải dựng BRep trên topology gốc vì bản đã gộp không hợp lệ.</summary>
    public int RawTopologyOffsets { get; set; }

    /// <summary>Lý do BRepBuilder từ chối — dùng để lần ra lỗi, không phải thông tin cho end user.</summary>
    public string? BRepError { get; set; }

    /// <summary>Số mặt của khối cover trước/sau bước ghép mặt đồng phẳng cuối cùng.</summary>
    public int CoverFacesBefore { get; set; }

    public int CoverFacesAfter { get; set; }

    public string? MergeError { get; set; }

    /// <summary>Số khối đã lấp lỗ để lấy vỏ ngoài.</summary>
    public int HolesFilled { get; set; }

    /// <summary>Số khối dựng bằng cách gọt giao-bản-sao-dời (đường chính), và số hướng đã gọt.</summary>
    public int ErodedSolids { get; set; }

    public int ErosionDirections { get; set; }

    /// <summary>Số mặt bị loại theo chỉ định của user, và số lỗ đã nút được từ các mặt đó.</summary>
    public int ExcludedFaces { get; set; }

    public int PickedHolesPlugged { get; set; }

    public int PickedHolesFailed { get; set; }

    public string? ErosionError { get; set; }

    /// <summary>Số mặt thành lỗ đã loại bỏ theo topology.</summary>
    public int HoleFacesRemoved { get; set; }

    /// <summary>Số lỗ tròn đã nút bằng khối trụ, và số mặt trụ đã xét.</summary>
    public int RoundHolesPlugged { get; set; }

    public int RoundFacesSeen { get; set; }

    /// <summary>Chất lượng hình gốc — xem SolidCleanup.Inspect.</summary>
    public int SourceCurvedFaces { get; set; }

    public int SourceBrokenEdges { get; set; }

    /// <summary>Số mặt không phải mặt phẳng cũng không phải mặt trụ — không nút được bằng khối trụ.</summary>
    public int OtherCurvedFaces { get; set; }

    public string? FillError { get; set; }

    /// <summary>Số khối phải lùi về cách cắt boolean (khối có mặt cong/cạnh cong).</summary>
    public int BooleanSolids { get; set; }

    /// <summary>Có khối bị lộn hướng mặt khi offset: cấu kiện mỏng hơn hai lần cover.</summary>
    public int Collapsed { get; set; }

    public int FailedSolids { get; set; }
}

/// <summary>Dựng khối "cover": lấy chính khối của cấu kiện rồi CO VÀO TRONG theo chiều dày cover.
///
/// ĐƯỜNG CHÍNH là PlaneOffsetSolid — offset theo topology, khối gốc bao nhiêu mặt thì cover bấy
/// nhiêu mặt. File này chỉ còn là DỰ PHÒNG cho khối có mặt cong/cạnh cong, nơi không thể tính đỉnh
/// mới bằng giao ba mặt phẳng.
///
/// Cách dự phòng: với mỗi mặt của khối, dựng một khối dao đúng bằng phần vật liệu nằm trong khoảng
/// cover tính từ mặt đó, GỘP hết dao lại rồi trừ MỘT LẦN.
///   - Mặt phẳng: lấy đường bao của chính mặt đó (GetEdgesAsCurveLoops) đùn vào trong đúng chiều dày
///     cover. Cố ý KHÔNG cắt bằng nửa không gian vô hạn: mặt phẳng kéo dài vô tận sẽ ăn nhầm sang
///     phần khác của khối lõm (vd tiết diện chữ L, khối có hốc).
///   - Mặt trụ: lỗ thì khoét rộng thêm đúng cover (thép phải tránh xa lỗ); trụ lồi thì gọt vỏ ngoài.
///   - Mặt cong khác (ruled/hermite/nón - vd bản mặt cầu vặn): chia tam giác rồi đùn từng tam giác
///     vào trong, gộp lại thành một dao cho cả mặt.
///
/// Xử lý góc (đây là phần khó nhất của bài toán offset, đừng rút gọn):
///   - Góc LÕM: hai mặt offset vào trong bị hở một mảnh ngay góc, để nguyên thì khối cover mọc ra
///     vấu nhọn. Giải bằng cách nới đường bao mỗi mặt ra đúng bằng cover trước khi đùn, tương đương
///     kéo dài hai mặt offset cho gặp nhau (miter). Xem PlanarCutter.
///   - Góc LỒI/NHỌN: phần offset dư tự bị trim khi gộp (union) các dao, không cần xử lý riêng.
///
/// LỊCH SỬ - ĐỪNG LẶP LẠI: bản đầu trừ TỪNG MẶT trực tiếp vào khối chính. Với khối nhiều mặt vụn
/// (mặt cong bị chia nhỏ, hoặc hình đã tessellate sẵn từ lúc convert link) thì mỗi lần trừ để lại
/// sliver, vài trăm lần thành mớ hình rối như lưới - không dùng được. Nay dao được GỘP (union) trước
/// rồi mới trừ, nên khối chính chỉ chịu một vài phép boolean.
///
/// Kết quả chỉ là khối hình học tượng trưng cho lớp cover — không join, không host rebar (trừ khi
/// user bật), không ảnh hưởng cấu kiện gốc.</summary>
internal static class CoverBuilder
{
    private const double MinVolume = 1e-9;         // ft3
    private const double MinFaceArea = 1e-6;       // ft2 (~0.09 mm2) - mặt suy biến
    private const double ParallelTolerance = 0.95; // cos(~18 do)
    private const double PointTolerance = 1e-6;    // ft
    private const int MaxCutters = 4000;           // ngưỡng "hình quá vụn"
    private const int MaxTrianglesPerFace = 2000;

    public static CoverBuildResult Build(
        IReadOnlyList<Solid> solids, IReadOnlyList<BottomPlane> bottoms, CoverOptions options)
    {
        var result = new CoverBuildResult();

        foreach (var source in solids)
        {
            try
            {
                // Lấp lỗ trước: cover chỉ cần bao ngoài. Giữ lỗ theo thì mặt trụ chặn đường offset
                // theo topology, đẩy cả khối xuống cắt boolean và cho ra hình nát.
                SolidCleanup.Inspect(source, out var faceCount, out var curvedCount, out var brokenEdges);
                result.SourceFaces += faceCount;
                result.SourceCurvedFaces += curvedCount;
                result.SourceBrokenEdges += brokenEdges;

                var solid = source;
                if (options.FillHoles)
                {
                    // Nút lỗ user chỉ định trước (chắc ăn nhất vì người dùng nhìn thấy khối), rồi tới
                    // lỗ tròn tự nhận diện.
                    var plugged = PlugRoundHoles(PlugPickedHoles(solid, options, result), result);

                    // Rồi mới thử dựng lại vỏ ngoài, để bắt nốt hốc rỗng không phải hình trụ.
                    var filled = SolidCleanup.FillHoles(plugged, out var fillError, out var holeFaces);
                    result.HoleFacesRemoved += holeFaces;
                    if (filled is not null)
                    {
                        solid = filled;
                        result.HolesFilled++;
                    }
                    else
                    {
                        solid = plugged;
                        result.FillError ??= fillError;
                    }
                }

                // Đường chính: gọt bằng cách giao khối với bản sao đã dời (xem TranslateErosion).
                // Không đụng topology, không dao theo từng mặt, nên hình gốc vụn cỡ nào cũng chạy.
                var shrunk = TranslateErosion.Build(
                    solid, bottoms, options, face => IsExcluded(face, options),
                    out var erosionError, out var directions);

                result.ErosionDirections = Math.Max(result.ErosionDirections, directions);
                result.ErosionError ??= erosionError;

                if (shrunk is not null && shrunk.Volume > MinVolume && IsInside(shrunk, solid))
                {
                    result.Solids.Add(Tidy(shrunk, result));
                    result.ErodedSolids++;
                    continue;
                }

                // Dự phòng: offset theo topology - khối gốc bao nhiêu mặt thì cover bấy nhiêu mặt.
                Solid? offset = null;
                var offsetReport = new PlaneOffsetSolid.OffsetReport(
                    PlaneOffsetSolid.Failure.None, false, false, 0, 0, null);

                if (!options.SkipPlaneOffset)
                {
                    offset = PlaneOffsetSolid.Build(
                        solid, face => CoverFor(face, bottoms, options), out offsetReport);
                }

                if (offsetReport.MergedFaces > 0)
                {
                    result.FacesBeforeMerge += offsetReport.SourceFaces;
                    result.FacesAfterMerge += offsetReport.MergedFaces;
                }

                if (offset is not null && offset.Volume > MinVolume && IsInside(offset, solid))
                {
                    result.Solids.Add(Tidy(offset, result));
                    result.OffsetSolids++;
                    if (!offsetReport.ExactBRep)
                        result.ShellOffsets++;
                    if (offsetReport.UsedRawTopology)
                        result.RawTopologyOffsets++;
                    result.BRepError ??= offsetReport.BRepError;
                    continue;
                }

                var failure = offsetReport.Failure;

                // Mặt lật hướng KHÔNG có nghĩa là cả cấu kiện mỏng hơn hai lần cover: chỉ cần một
                // chi tiết nhỏ hơn cover (gờ vát, facet vụn của hình đã tessellate) là mặt đó lật,
                // vì trong khối offset thật nó BIẾN MẤT - mà giữ nguyên topology thì không biểu diễn
                // được chuyện mất mặt. Lúc đó lùi về cắt boolean, cách đó xử lý được mất chi tiết.
                if (failure == PlaneOffsetSolid.Failure.Collapsed)
                    result.Collapsed++;

                // Dự phòng: khối có mặt cong/cạnh cong, hoặc có mặt biến mất khi offset.
                // IsInside cũng bắt buộc cho đường boolean: thiếu nó thì mảnh vụn lòi ra ngoài cấu
                // kiện vẫn được giao cho người dùng.
                var eroded = Erode(solid, bottoms, options, result);
                if (eroded is not null && eroded.Volume > MinVolume && IsInside(eroded, solid))
                {
                    result.Solids.Add(Tidy(eroded, result));
                    result.BooleanSolids++;
                }
                else
                {
                    result.FailedSolids++;
                }
            }
            catch (Exception)
            {
                result.FailedSolids++;
            }
        }

        return result;
    }

    private static Solid? Erode(
        Solid source, IReadOnlyList<BottomPlane> bottoms, CoverOptions options, CoverBuildResult report)
    {
        report.SourceFaces += source.Faces.Size;

        var cutters = BuildCutters(source, bottoms, options, report);
        if (report.TooComplex || cutters.Count == 0)
            return null;

        var current = source;
        foreach (var cutter in Merge(cutters))
        {
            try
            {
                var next = BooleanOperationsUtils.ExecuteBooleanOperation(
                    current, cutter, BooleanOperationsType.Difference);
                if (next is not null)
                    current = next;
            }
            catch (Exception)
            {
                report.SkippedFaces++;
            }

            if (current.Volume <= MinVolume)
                return null; // Cấu kiện mỏng hơn 2 lần cover - không còn gì bên trong.
        }

        if (ReferenceEquals(current, source))
            return null;

        // Khối cover BẮT BUỘC nằm trọn trong khối gốc. Đây là chốt chặn cho cả lớp lỗi "dao dựng
        // sai lại đi cộng thêm vật liệu": thà không ra khối còn hơn ra hình có vây đâm ra ngoài.
        if (!IsInside(current, source))
        {
            report.Escaped++;
            return null;
        }

        return current;
    }

    /// <summary>Nút những lỗ do USER CHỈ ĐỊNH, kể cả lỗ đã bị tessellate thành hàng trăm mặt phẳng.
    ///
    /// Mấu chốt: các facet của một ống đều SONG SONG với trục ống, nên trục = tích có hướng của hai
    /// pháp tuyến bất kỳ không song song trong nhóm. Biết trục rồi thì bán kính và đoạn trục chỉ là
    /// đo đạc. Không cần PCA, không cần nhận diện mặt trụ - chạy được với cả ống đã băm nhỏ.
    ///
    /// Các mặt được gom thành từng nhóm theo đỉnh dùng chung, nên nhiều ống trong một cấu kiện thì
    /// mỗi ống ra một khối nút riêng.</summary>
    private static Solid PlugPickedHoles(Solid solid, CoverOptions options, CoverBuildResult report)
    {
        if (options.ExcludedFaces.Count == 0)
            return solid;

        var groups = GroupPickedFaces(solid, options, report);
        var current = solid;

        // Lỗ khoét luôn NHỎ so với cấu kiện. Khối nút to hơn ngưỡng này chắc chắn là dựng sai (trục
        // đoán nhầm thì bán kính vọt lên và ra một cái đĩa khổng lồ trùm cả cấu kiện).
        var box = solid.GetBoundingBox();
        var size = box.Max - box.Min;
        var maxRadius = 0.5 * Math.Min(size.X, Math.Min(size.Y, size.Z));
        var maxGrowth = solid.Volume * 0.25;

        foreach (var group in groups)
        {
            try
            {
                var plug = FitCylinder(group, maxRadius, size.GetLength());
                if (plug is null)
                {
                    report.PickedHolesFailed++;
                    continue;
                }

                var next = BooleanOperationsUtils.ExecuteBooleanOperation(
                    current, plug, BooleanOperationsType.Union);

                if (next is null || next.Volume <= current.Volume + MinVolume)
                    continue; // không phải lỗ, hoặc nút không ăn gì

                if (next.Volume - current.Volume > maxGrowth)
                {
                    report.PickedHolesFailed++;
                    continue; // nút làm phình khối: dựng sai, bỏ
                }

                current = next;
                report.PickedHolesPlugged++;
            }
            catch (Exception)
            {
                report.PickedHolesFailed++;
            }
        }

        return current;
    }

    /// <summary>Gom các mặt user đã chỉ thành từng lỗ, theo đỉnh dùng chung.</summary>
    private static List<List<(XYZ Normal, List<XYZ> Points)>> GroupPickedFaces(
        Solid solid, CoverOptions options, CoverBuildResult report)
    {
        var faces = new List<(XYZ Normal, List<XYZ> Points)>();

        foreach (Face face in solid.Faces)
        {
            if (!IsExcluded(face, options))
                continue;

            report.ExcludedFaces++;

            var points = new List<XYZ>();
            try
            {
                var mesh = face.Triangulate(0.2);
                if (mesh is null)
                    continue;

                for (var i = 0; i < mesh.NumTriangles; i++)
                {
                    var triangle = mesh.get_Triangle(i);
                    for (var v = 0; v < 3; v++)
                        points.Add(triangle.get_Vertex(v));
                }

                var normal = face is PlanarFace planar
                    ? planar.FaceNormal
                    : face.ComputeNormal(new UV(0.5, 0.5));

                if (points.Count > 0)
                    faces.Add((normal, points));
            }
            catch (Exception)
            {
                // Mặt này không đọc được thì bỏ, các mặt khác vẫn gom.
            }
        }

        // Gom theo đỉnh dùng chung: hai mặt chạm nhau thì cùng một lỗ.
        var groups = new List<List<(XYZ Normal, List<XYZ> Points)>>();
        var used = new bool[faces.Count];

        for (var i = 0; i < faces.Count; i++)
        {
            if (used[i])
                continue;

            var group = new List<(XYZ, List<XYZ>)> { faces[i] };
            used[i] = true;

            var grown = true;
            while (grown)
            {
                grown = false;
                for (var j = 0; j < faces.Count; j++)
                {
                    if (used[j] || !Touches(group, faces[j].Points))
                        continue;

                    group.Add(faces[j]);
                    used[j] = true;
                    grown = true;
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static bool Touches(
        List<(XYZ Normal, List<XYZ> Points)> group, List<XYZ> candidate)
    {
        foreach (var (_, points) in group)
        {
            foreach (var a in points)
            {
                foreach (var b in candidate)
                {
                    if (a.DistanceTo(b) <= PointTolerance)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>Dựng khối trụ lấp đúng cái lỗ mà nhóm mặt này bao quanh.
    ///
    /// LỊCH SỬ - ĐỪNG LẶP LẠI: bản trước lấy tích có hướng của CẶP PHÁP TUYẾN ĐẦU TIÊN có tích đủ
    /// lớn. Hai facet gần song song vẫn qua được ngưỡng đó, và trục tính ra lệch hẳn — bán kính đo
    /// theo trục sai thì vọt lên vài mét, ra một cái đĩa khổng lồ trùm cả cấu kiện. Nay lấy cặp
    /// VUÔNG GÓC NHẤT (tích có hướng lớn nhất) và bắt buộc kiểm tra lại trục trước khi dùng.</summary>
    private static Solid? FitCylinder(
        List<(XYZ Normal, List<XYZ> Points)> group, double maxRadius, double maxLength)
    {
        // Trục ống vuông góc với MỌI pháp tuyến facet. Cặp vuông góc nhất cho trục ổn định nhất.
        XYZ? axis = null;
        var best = 0.3; // dưới ~17 độ lệch nhau thì không đủ tin cậy để tính trục
        for (var i = 0; i < group.Count; i++)
        {
            for (var j = i + 1; j < group.Count; j++)
            {
                var cross = group[i].Normal.CrossProduct(group[j].Normal);
                if (cross.GetLength() > best)
                {
                    best = cross.GetLength();
                    axis = cross.Normalize();
                }
            }
        }

        if (axis is null)
            return null; // các mặt gần song song hết: không đủ dữ liệu để dựng ống

        // Trục thật phải vuông góc với mọi pháp tuyến. Lệch nhiều nghĩa là nhóm này không phải một
        // ống (vd gom nhầm nhiều lỗ khác hướng vào chung một nhóm).
        if (group.Any(face => Math.Abs(face.Normal.DotProduct(axis)) > 0.1))
            return null;

        var points = group.SelectMany(face => face.Points).ToList();
        var centroid = points.Aggregate(XYZ.Zero, (sum, p) => sum + p) / points.Count;

        double min = double.MaxValue, max = double.MinValue, radius = 0;
        foreach (var point in points)
        {
            var offset = point - centroid;
            var along = offset.DotProduct(axis);
            min = Math.Min(min, along);
            max = Math.Max(max, along);
            radius = Math.Max(radius, (offset - axis * along).GetLength());
        }

        var length = max - min;
        if (length <= PointTolerance || radius <= PointTolerance)
            return null;

        // Chốt chặn kích thước: lỗ khoét không thể to hơn chính cấu kiện.
        if (radius > maxRadius || length > maxLength)
            return null;

        return Cylinder(centroid + axis * min, axis, radius, length);
    }

    /// <summary>Nút mọi lỗ tròn của khối bằng chính khối trụ của lỗ đó rồi hợp vào.
    ///
    /// Đây là cách lấp lỗ KHÔNG đụng tới topology: chỉ cần đọc trục/bán kính/đoạn trục của mặt trụ
    /// rồi union, nên hình gốc có vụn cỡ nào cũng chạy được. Cách dựng lại vỏ ngoài (SolidCleanup)
    /// sạch hơn nhưng đòi hình gốc đủ lành — với hình lấy từ link thì thường không.</summary>
    private static Solid PlugRoundHoles(Solid solid, CoverBuildResult report)
    {
        var current = solid;

        foreach (Face face in solid.Faces)
        {
            if (face is not CylindricalFace cylinder)
            {
                if (face is not PlanarFace)
                    report.OtherCurvedFaces++;
                continue;
            }

            report.RoundFacesSeen++;

            try
            {
                var axis = cylinder.Axis.Normalize();
                var origin = cylinder.Origin;

                // CỐ Ý không lọc trước xem mặt trụ là lỗ hay là trụ lồi. Phép thử bằng hướng pháp
                // tuyến không đáng tin (ComputeNormal của mặt trụ không chắc theo hướng của FACE),
                // và đã làm cả bước nút lỗ không chạy lần trước. Cứ union rồi xem thể tích: là lỗ
                // thì khối to ra, là trụ lồi thì khối trụ nằm gọn bên trong nên union không đổi gì.
                var radius = cylinder.get_Radius(0).GetLength();
                if (radius <= PointTolerance)
                    continue;

                if (!AxialExtent(cylinder, origin, axis, out var min, out var max))
                    continue;

                var plug = Cylinder(origin + axis * min, axis, radius, max - min);
                if (plug is null)
                    continue;

                var next = BooleanOperationsUtils.ExecuteBooleanOperation(
                    current, plug, BooleanOperationsType.Union);
                if (next is not null && next.Volume > current.Volume + MinVolume)
                {
                    current = next;
                    report.RoundHolesPlugged++;
                }
            }
            catch (Exception)
            {
                // Lỗ này nút không được thì bỏ qua, các lỗ khác vẫn nút.
            }
        }

        return current;
    }

    /// <summary>Bước dọn cuối: ghép các mặt lẻ đồng phẳng của khối vừa dựng thành một mặt liền.
    /// Chạy cho mọi đường dựng, và chỉ nhận kết quả khi thể tích không đổi.</summary>
    private static Solid Tidy(Solid cover, CoverBuildResult report)
    {
        var tidied = SolidCleanup.MergeCoplanarFaces(cover, out var before, out var after, out var error);

        report.CoverFacesBefore += before;
        report.CoverFacesAfter += after;
        report.MergeError ??= error;

        return tidied;
    }

    /// <summary>Chữ ký nhận dạng một mặt, để khớp mặt user pick với mặt đọc lại từ hình học của khối
    /// (hai lần đọc cho ra hai đối tượng Face khác nhau nên không so tham chiếu được). Dùng pháp
    /// tuyến + một điểm nằm trên mặt, làm tròn thô để chịu được sai số.</summary>
    public static string? Signature(Face face)
    {
        try
        {
            var box = face.GetBoundingBox();
            var uv = new UV((box.Min.U + box.Max.U) / 2.0, (box.Min.V + box.Max.V) / 2.0);
            var point = face.Evaluate(uv);
            var normal = face.ComputeNormal(uv);

            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{normal.X:F4}|{normal.Y:F4}|{normal.Z:F4}|{point.X:F3}|{point.Y:F3}|{point.Z:F3}");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsExcluded(Face face, CoverOptions options)
    {
        if (options.ExcludedFaces.Count == 0)
            return false;

        var signature = Signature(face);
        return signature is not null && options.ExcludedFaces.Contains(signature);
    }

    private static bool IsInside(Solid candidate, Solid container)
    {
        try
        {
            var outside = BooleanOperationsUtils.ExecuteBooleanOperation(
                candidate, container, BooleanOperationsType.Difference);
            return outside is null || outside.Volume <= container.Volume * 1e-6 + MinVolume;
        }
        catch (Exception)
        {
            return true; // Không kiểm được thì không chặn oan.
        }
    }

    // ===================== Dao cắt =====================

    /// <summary>Một dao cho mỗi MẶT PHẲNG (gộp các mặt đồng phẳng), mỗi mặt trụ và mỗi mặt cong.</summary>
    private static List<Solid> BuildCutters(
        Solid source, IReadOnlyList<BottomPlane> bottoms, CoverOptions options, CoverBuildResult report)
    {
        // Mặt đồng phẳng bị chia nhỏ là chuyện thường với hình lấy từ link/IFC - gộp theo mặt phẳng
        // để số phép boolean không nổ theo số mặt.
        var byPlane = new Dictionary<(long, long, long, long), List<Solid>>();
        var others = new List<Solid>();

        // Biên nới đường bao mặt phẳng: lấy cover LỚN NHẤT vì vấu ở góc lõm rộng đúng bằng cover của
        // mặt kề, mà lúc dựng dao thì chưa biết mặt kề là mặt nào.
        var margin = Math.Max(options.BottomCover, options.SideCover);

        foreach (Face face in source.Faces)
        {
            if (byPlane.Count + others.Count > MaxCutters)
            {
                report.TooComplex = true;
                return new List<Solid>();
            }

            // Mặt user đã chỉ là mặt lỗ: không dựng dao cho nó, cover không cắt theo lỗ.
            if (IsExcluded(face, options))
                continue;

            var cover = CoverFor(face, bottoms, options);
            if (cover <= PointTolerance)
                continue;

            if (TooSmall(face))
            {
                report.TinyFaces++;
                continue;
            }

            switch (face)
            {
                case PlanarFace planar:
                {
                    var pieces = PlanarCutters(planar, cover, margin);
                    if (pieces.Count == 0)
                    {
                        report.SkippedFaces++;
                        break;
                    }

                    var key = PlaneKey(planar);
                    if (!byPlane.TryGetValue(key, out var list))
                        byPlane[key] = list = new List<Solid>();
                    list.AddRange(pieces);
                    break;
                }

                case CylindricalFace cylinder:
                {
                    var cutter = CylindricalCutter(cylinder, cover);
                    if (cutter is null)
                        report.SkippedFaces++;
                    else
                        others.Add(cutter);
                    break;
                }

                default:
                {
                    var cutter = CurvedCutter(face, cover);
                    if (cutter is null)
                        report.SkippedFaces++;
                    else
                        others.Add(cutter);
                    break;
                }
            }
        }

        var cutters = new List<Solid>();
        foreach (var group in byPlane.Values)
        {
            var merged = Union(group);
            if (merged is null)
                cutters.AddRange(group);
            else
                cutters.Add(merged);
        }

        cutters.AddRange(others);
        return cutters;
    }

    /// <summary>Gộp hết dao thành càng ít khối càng tốt. Union hỏng ở đâu thì chốt khối đang gộp lại
    /// và bắt đầu khối mới, không bỏ dao nào.</summary>
    private static List<Solid> Merge(List<Solid> cutters)
    {
        var merged = new List<Solid>();
        Solid? accumulator = null;

        foreach (var cutter in cutters)
        {
            if (accumulator is null)
            {
                accumulator = cutter;
                continue;
            }

            try
            {
                accumulator = BooleanOperationsUtils.ExecuteBooleanOperation(
                    accumulator, cutter, BooleanOperationsType.Union);
            }
            catch (Exception)
            {
                merged.Add(accumulator);
                accumulator = cutter;
            }
        }

        if (accumulator is not null)
            merged.Add(accumulator);

        return merged;
    }

    private static Solid? Union(List<Solid> solids)
    {
        if (solids.Count == 0)
            return null;
        if (solids.Count == 1)
            return solids[0];

        var current = solids[0];
        for (var i = 1; i < solids.Count; i++)
        {
            try
            {
                current = BooleanOperationsUtils.ExecuteBooleanOperation(
                    current, solids[i], BooleanOperationsType.Union);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return current;
    }

    private static bool TooSmall(Face face)
    {
        try
        {
            return face.Area < MinFaceArea;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Khoá gộp mặt đồng phẳng: pháp tuyến + khoảng cách tới gốc, làm tròn về lưới 1e-6.</summary>
    private static (long, long, long, long) PlaneKey(PlanarFace face)
    {
        var n = face.FaceNormal;
        var d = n.DotProduct(face.Origin);
        return (Quantize(n.X), Quantize(n.Y), Quantize(n.Z), Quantize(d));
    }

    private static long Quantize(double value) => (long)Math.Round(value / PointTolerance);

    /// <summary>Mặt khớp với BẤT KỲ mặt đáy nào đã pick thì ăn cover đáy, còn lại ăn cover mặt bên.</summary>
    private static double CoverFor(Face face, IReadOnlyList<BottomPlane> bottoms, CoverOptions options)
    {
        if (face is not PlanarFace planar)
            return options.SideCover;

        foreach (var bottom in bottoms)
        {
            if (planar.FaceNormal.DotProduct(bottom.Normal) < ParallelTolerance)
                continue;

            if (!options.ApplyToParallelFaces &&
                Math.Abs((planar.Origin - bottom.Origin).DotProduct(bottom.Normal)) > PointTolerance)
            {
                continue;
            }

            return options.BottomCover;
        }

        return options.SideCover;
    }

    /// <summary>Dao cho một mặt phẳng: khối chính theo đúng đường bao mặt, CỘNG các khối viền chạy
    /// dọc từng cạnh biên và ăn lấn ra ngoài <paramref name="margin"/>.
    ///
    /// Vì sao cần khối viền — đây là phần khó của bài toán offset:
    ///   - Góc LÕM (vd chân tường gặp bản đáy): hai mặt offset vào trong hở ra một mảnh ngay góc mà
    ///     không dao nào phủ, hình còn lại mọc ra vấu nhọn. Đúng bản chất offset là KÉO DÀI hai mặt
    ///     offset cho gặp nhau (miter); khối viền lấn ra chính là cách làm điều đó bằng khối.
    ///   - Góc LỒI/NHỌN: phần offset dư tự bị trim khi gộp (union) các dao. Khối viền lấn ra ở đây
    ///     rơi vào chỗ không có vật liệu nên vô hại.
    ///
    /// LỊCH SỬ - ĐỪNG LẶP LẠI: bản trước nới bằng CurveLoop.CreateViaOffset. Với đường bao lõm, hàm
    /// này trả về loop TỰ CẮT CHÍNH NÓ mà không ném lỗi; đùn ra khối rác, trừ vào khối chính thì mọc
    /// thêm vây nhọn đâm ra ngoài cả khối gốc. Khối viền chạy theo từng đoạn cạnh thì luôn lồi và
    /// luôn hợp lệ, không bao giờ tự cắt.</summary>
    private static List<Solid> PlanarCutters(PlanarFace face, double distance, double margin)
    {
        var cutters = new List<Solid>();

        IList<CurveLoop> loops;
        try
        {
            loops = face.GetEdgesAsCurveLoops();
        }
        catch (Exception)
        {
            return cutters;
        }

        if (loops.Count == 0)
            return cutters;

        var normal = face.FaceNormal;

        var main = Extrude(loops, -normal, distance);
        if (main is not null)
            cutters.Add(main);

        if (margin <= PointTolerance)
            return cutters;

        var spans = loops.Select(Span).ToList();
        var outerIndex = spans.IndexOf(spans.Max());

        for (var i = 0; i < loops.Count; i++)
        {
            var loop = loops[i];

            // Hướng "ra khỏi phần vật liệu của mặt": bao ngoài thì ra ngoài loop, lỗ thì vào trong lỗ.
            bool counterClockwise;
            try
            {
                counterClockwise = loop.IsCounterclockwise(normal);
            }
            catch (Exception)
            {
                continue;
            }

            var sign = counterClockwise ? 1.0 : -1.0;
            if (i != outerIndex)
                sign = -sign;

            foreach (var curve in loop)
            {
                var points = curve.Tessellate();
                for (var p = 0; p + 1 < points.Count; p++)
                {
                    var box = EdgeCutter(points[p], points[p + 1], normal, sign, margin, distance);
                    if (box is not null)
                        cutters.Add(box);
                }
            }
        }

        return cutters;
    }

    /// <summary>Khối viền chạy dọc một đoạn cạnh, lấn ra ngoài mặt <paramref name="margin"/> và kéo
    /// dài thêm bấy nhiêu ở hai đầu để phủ luôn vấu tại đỉnh giữa hai cạnh.</summary>
    private static Solid? EdgeCutter(
        XYZ start, XYZ end, XYZ normal, double sign, double margin, double depth)
    {
        var direction = end - start;
        if (direction.GetLength() < PointTolerance)
            return null;

        try
        {
            var t = direction.Normalize();
            var outward = t.CrossProduct(normal).Normalize() * sign;

            var a = start - t * margin;
            var b = end + t * margin;
            var c = b + outward * margin;
            var d = a + outward * margin;

            var loop = CurveLoop.Create(new List<Curve>
            {
                Line.CreateBound(a, b),
                Line.CreateBound(b, c),
                Line.CreateBound(c, d),
                Line.CreateBound(d, a)
            });

            return Extrude(new List<CurveLoop> { loop }, -normal, depth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Đường chéo bounding box của loop — dùng để biết loop nào là bao ngoài.</summary>
    private static double Span(CurveLoop loop)
    {
        var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);

        foreach (var curve in loop)
        {
            foreach (var point in curve.Tessellate())
            {
                min = new XYZ(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                max = new XYZ(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
            }
        }

        return max.X < min.X ? 0 : (max - min).GetLength();
    }

    /// <summary>Lỗ tròn: khoét rộng thêm đúng cover. Trụ lồi: gọt vỏ ngoài dày cover.</summary>
    private static Solid? CylindricalCutter(CylindricalFace face, double distance)
    {
        var axis = face.Axis.Normalize();
        var origin = face.Origin;
        var radius = face.get_Radius(0).GetLength();
        if (radius <= PointTolerance)
            return null;

        if (!AxialExtent(face, origin, axis, out var min, out var max))
            return null;

        var length = max - min;
        if (length <= PointTolerance)
            return null;

        var basePoint = origin + axis * min;
        var outer = Cylinder(basePoint, axis, radius + distance, length);
        if (outer is null)
            return null;

        if (IsHole(face, origin, axis))
            return outer;

        var innerRadius = radius - distance;
        if (innerRadius <= PointTolerance)
            return outer;

        var inner = Cylinder(basePoint, axis, innerRadius, length);
        if (inner is null)
            return outer;

        try
        {
            return BooleanOperationsUtils.ExecuteBooleanOperation(
                outer, inner, BooleanOperationsType.Difference);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Mặt cong không phải trụ (bản vặn, mặt nón, hermite): chia tam giác, đùn từng tam giác
    /// vào trong rồi gộp thành MỘT dao cho cả mặt. Tam giác nào lộn hướng thì đùn ra ngoài - chỗ đó
    /// không có vật liệu nên chỉ mất cover ở mảnh đó, không làm méo hình.</summary>
    private static Solid? CurvedCutter(Face face, double distance)
    {
        Mesh? mesh;
        try
        {
            mesh = face.Triangulate(1.0);
        }
        catch (Exception)
        {
            return null;
        }

        if (mesh is null || mesh.NumTriangles == 0 || mesh.NumTriangles > MaxTrianglesPerFace)
            return null;

        var prisms = new List<Solid>();
        for (var i = 0; i < mesh.NumTriangles; i++)
        {
            var triangle = mesh.get_Triangle(i);
            var a = triangle.get_Vertex(0);
            var b = triangle.get_Vertex(1);
            var c = triangle.get_Vertex(2);

            var cross = (b - a).CrossProduct(c - a);
            if (cross.GetLength() < PointTolerance)
                continue;

            var normal = cross.Normalize();
            CurveLoop loop;
            try
            {
                loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(a, b),
                    Line.CreateBound(b, c),
                    Line.CreateBound(c, a)
                });
            }
            catch (Exception)
            {
                continue;
            }

            var prism = Extrude(new List<CurveLoop> { loop }, -normal, distance);
            if (prism is not null)
                prisms.Add(prism);
        }

        return Union(prisms);
    }

    /// <summary>Mặt trụ là lỗ (vật liệu nằm ngoài) hay là trụ lồi (vật liệu nằm trong).</summary>
    private static bool IsHole(CylindricalFace face, XYZ origin, XYZ axis)
    {
        try
        {
            var box = face.GetBoundingBox();
            var uv = new UV((box.Min.U + box.Max.U) / 2.0, (box.Min.V + box.Max.V) / 2.0);
            var point = face.Evaluate(uv);
            var normal = face.ComputeNormal(uv);
            var onAxis = origin + axis * (point - origin).DotProduct(axis);
            return normal.DotProduct(onAxis - point) > 0;
        }
        catch (Exception)
        {
            return true; // Lỗ là trường hợp phổ biến hơn nhiều trong cấu kiện bê tông.
        }
    }

    /// <summary>Đoạn trục mà mặt trụ thực sự chiếm — cắt dài hơn sẽ ăn nhầm sang phần khác của khối
    /// (vd lỗ cụt thành lỗ xuyên).</summary>
    private static bool AxialExtent(Face face, XYZ origin, XYZ axis, out double min, out double max)
    {
        min = double.MaxValue;
        max = double.MinValue;

        Mesh? mesh;
        try
        {
            mesh = face.Triangulate(0.2);
        }
        catch (Exception)
        {
            return false;
        }

        if (mesh is null || mesh.NumTriangles == 0)
            return false;

        for (var i = 0; i < mesh.NumTriangles; i++)
        {
            var triangle = mesh.get_Triangle(i);
            for (var v = 0; v < 3; v++)
            {
                var t = (triangle.get_Vertex(v) - origin).DotProduct(axis);
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }

        return max > min;
    }

    private static Solid? Cylinder(XYZ basePoint, XYZ axis, double radius, double length)
    {
        try
        {
            var x = axis.CrossProduct(XYZ.BasisZ);
            if (x.GetLength() < PointTolerance)
                x = axis.CrossProduct(XYZ.BasisX);
            x = x.Normalize();
            var y = axis.CrossProduct(x).Normalize();

            // Revit không cho Arc trọn vòng - ghép 2 nửa cung.
            var loop = CurveLoop.Create(new List<Curve>
            {
                Arc.Create(basePoint, radius, 0, Math.PI, x, y),
                Arc.Create(basePoint, radius, Math.PI, 2 * Math.PI, x, y)
            });

            return Extrude(new List<CurveLoop> { loop }, axis, length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Đùn theo hướng cho trước; nếu chiều xoay của đường bao không hợp với hướng đùn thì
    /// đùn ngược rồi dời về đúng chỗ.</summary>
    private static Solid? Extrude(IList<CurveLoop> loops, XYZ direction, double distance)
    {
        try
        {
            return GeometryCreationUtilities.CreateExtrusionGeometry(loops, direction, distance);
        }
        catch (Exception)
        {
            // Thử hướng ngược lại.
        }

        try
        {
            var flipped = GeometryCreationUtilities.CreateExtrusionGeometry(loops, -direction, distance);
            return SolidUtils.CreateTransformed(
                flipped, Transform.CreateTranslation(direction * distance));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
