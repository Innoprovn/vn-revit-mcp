using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.SnapWallToGrid;

/// <summary>Xử lý Category Sàn (Floor) — MÉP sàn (boundary edge của mặt trên), không phải tim/mặt như
/// Wall/Beam, và KHÔNG giả định hình chữ nhật — biên sàn có thể là đa giác BẤT KỲ (tam giác, lục
/// giác...). 2 việc:
///   1. CreateDimensions: tạo Dimension THẬT cho MỖI cạnh biên có Grid song song gần nó (Edge.Reference
///      lấy từ mặt trên — không cần vào chế độ Sửa Sketch, chỉ đọc hình học) — CHỈ đọc, không sửa gì.
///   2. Snap: THẬT SỰ sửa hình dạng sàn — với MỌI cạnh biên có Grid song song gần đó VÀ đang lẻ, dịch
///      ĐÚNG cạnh đó (Edit Boundary) — KHÔNG di chuyển nguyên tấm sàn. Nhiều cạnh cùng lệch thì sửa HẾT
///      trong 1 lần bằng cách dựng lại toàn bộ VÒNG BIÊN (xem BuildLoop): mỗi đỉnh tính lại = giao điểm
///      của 2 cạnh kề nó SAU KHI đã áp dụng dịch chuyển (cạnh nào không lệch thì giữ nguyên) — cách này
///      đúng cho đa giác bất kỳ, không chỉ hình chữ nhật, và xử lý đúng cả trường hợp 2 cạnh cùng kề 1
///      đỉnh đều bị dịch. ĐÂY LÀ PHẦN RỦI RO NHẤT trong cả tool: sửa sketch lập trình (không qua UI) là
///      vùng API ít tài liệu, CHƯA kiểm chứng được trên Revit thật (môi trường phát triển không có
///      Revit) — bắt buộc người dùng tự kiểm tra kỹ kết quả trên vài sàn trước khi tin dùng hàng loạt.
///      Chỉ hỗ trợ biên là các cạnh THẲNG (Line) nối liền nhau theo kiểu đa giác đơn giản (1 vòng khép
///      kín) — biên có cạnh cong (Arc) bị bỏ qua; sàn có lỗ mở (nhiều vòng) chỉ xử lý vòng NGOÀI CÙNG
///      (tổng chiều dài lớn nhất), bỏ qua các vòng lỗ mở bên trong.</summary>
internal static class FloorSnapper
{
    private static readonly double DimensionLinePaddingFeet =
        UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    /// <summary>Dung sai để coi 2 đầu mút thuộc 2 cạnh khác nhau là "cùng 1 góc" (nối liền nhau) trong
    /// sketch biên sàn — dùng cả khi dựng vòng biên (BuildLoop) lẫn khi tra Edge/ModelCurve theo cặp
    /// điểm.</summary>
    private static readonly double CornerToleranceFeet =
        UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    internal static List<Floor> Collect(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id).OfClass(typeof(Floor)).Cast<Floor>().ToList();

    /// <summary>Tạo 1 Dimension cho MỖI cạnh biên (không chỉ cạnh gần Grid nhất) có Grid song song đủ
    /// gần — để bản xem trước phản ánh ĐÚNG mọi cạnh mà Snap sẽ đụng tới.</summary>
    internal static List<string> CreateDimension(
        Document doc, View view, Floor floor, IReadOnlyList<Grid> grids, DimensionType dimensionType)
    {
        var label = Describe(floor);

        var topFace = GetTopFace(floor);
        if (topFace is null)
            return new List<string> { $"{label}: bỏ qua — không đọc được mặt trên sàn." };

        var edgeLines = GetOuterLoopLinesWithEdge(topFace);
        if (edgeLines.Count == 0)
            return new List<string> { $"{label}: bỏ qua — biên sàn không phải đa giác thẳng đơn giản (có cạnh cong?)." };

        var results = new List<string>();
        var matchedAny = false;

        foreach (var (edge, line) in edgeLines)
        {
            var p0 = line.GetEndPoint(0);
            var direction = (line.GetEndPoint(1) - p0).Normalize();
            var normal = new XYZ(-direction.Y, direction.X, 0);
            var edgeN = p0.DotProduct(normal);

            var grid = GridMatcher.FindNearestParallel(direction, normal, edgeN, grids, out var gridN);
            if (grid is null)
                continue;

            matchedAny = true;

            try
            {
                var references = new ReferenceArray();
                references.Append(edge.Reference);
                references.Append(new Reference(grid));

                var midpoint = (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2.0;
                var startN = Math.Min(edgeN, gridN) - DimensionLinePaddingFeet;
                var endN = Math.Max(edgeN, gridN) + DimensionLinePaddingFeet;
                var dimLine = Line.CreateBound(
                    midpoint + normal.Multiply(startN - edgeN), midpoint + normal.Multiply(endN - edgeN));

                doc.Create.NewDimension(view, dimLine, references, dimensionType);
                results.Add($"{label}: đã tạo Dimension tới Grid \"{grid.Name}\".");
            }
            catch (Exception ex)
            {
                results.Add($"{label}: lỗi khi tạo Dimension tới Grid \"{grid.Name}\" — {ex.Message}");
            }
        }

        if (!matchedAny)
            results.Add($"{label}: bỏ qua — không có mép nào đủ song song với Grid.");

        return results;
    }

    /// <summary>SỬA THẬT biên sàn — xem cảnh báo rủi ro ở doc-comment lớp. Với MỌI cạnh có Grid song
    /// song đủ gần VÀ đang lẻ, dịch đúng cạnh đó, rồi dựng lại toàn bộ vòng biên (mỗi đỉnh = giao điểm
    /// 2 cạnh kề SAU dịch chuyển) để biên luôn khép kín — đúng cho đa giác bất kỳ.</summary>
    internal static (bool Changed, string? Note) Snap(
        Document doc, Floor floor, IReadOnlyList<Grid> grids, double snapUnitMm)
    {
        var label = Describe(floor);

        if (floor.SketchId == ElementId.InvalidElementId)
            return (false, $"{label}: bỏ qua — sàn không có Sketch biên (Floor by Face?).");

        // Chan doan CU THE ly do SketchEditScope co the bi Revit tu choi Start() ("not permitted to
        // start... document is currently modifiable/another edit mode active") ma KHONG lien quan gi
        // toi cach to chuc Transaction cua tool - Revit tu choi vao che do Sua Sketch truc tiep voi
        // phan tu dang nam trong Group (phai Ungroup/vao Edit Group truoc) hoac trong Assembly, cho du
        // Transaction ben ngoai co dung cach hay khong. Kiem tra truoc de BAO RO nguyen nhan thay vi
        // de Revit nem ra thong bao chung chung kho hieu.
        if (floor.GroupId != ElementId.InvalidElementId)
        {
            return (false,
                $"{label}: bỏ qua — sàn đang nằm trong 1 Group (Id {floor.GroupId.Value}), Revit không " +
                "cho sửa Sketch trực tiếp khi đang ở trong Group — cần Ungroup (hoặc Edit Group) rồi chạy lại.");
        }

        if (floor.AssemblyInstanceId != ElementId.InvalidElementId)
        {
            return (false,
                $"{label}: bỏ qua — sàn đang thuộc 1 Assembly (Id {floor.AssemblyInstanceId.Value}), " +
                "Revit không cho sửa Sketch trực tiếp khi đang ở trong Assembly.");
        }

        List<(ModelCurve Curve, Line Line)> modelLines;
        var levelDiagnostic = "";
        try
        {
            var sketch = doc.GetElement(floor.SketchId) as Sketch;
            if (sketch is null)
                return (false, $"{label}: bỏ qua — không đọc được Sketch.");

            // CHAN DOAN: theo Autodesk Community, SketchEditScope.Start() bi Revit tu choi ("not
            // permitted to start...") khi Sketch.SketchPlane cua Floor KHONG dong bo voi Level hien tai
            // cua Floor (thuong xay ra sau khi doi Level cua Floor qua UI/API ma Revit khong cap nhat
            // lai SketchPlane) - tinh truoc so lieu, GAN vao thong bao loi neu Start() facl (o duoi) de
            // nguoi dung thay ngay trong TaskDialog, khong can debugger.
            if (doc.GetElement(floor.LevelId) is Level level && sketch.SketchPlane is not null)
            {
                var sketchPlaneZFeet = sketch.SketchPlane.GetPlane().Origin.Z;
                var levelZFeet = level.ProjectElevation;
                var sketchPlaneZMm = Math.Round(UnitUtils.ConvertFromInternalUnits(sketchPlaneZFeet, UnitTypeId.Millimeters));
                var levelZMm = Math.Round(UnitUtils.ConvertFromInternalUnits(levelZFeet, UnitTypeId.Millimeters));
                levelDiagnostic = $" [Chẩn đoán: Level \"{level.Name}\" cao độ={levelZMm}mm, SketchPlane Z={sketchPlaneZMm}mm, lệch={sketchPlaneZMm - levelZMm}mm]";
            }

            // ElementClassFilter KHONG ho tro loc truc tiep bang ModelCurve (Revit tra loi ro: kieu
            // nay "exists in the API, but not in Revit's native object model") - phai loc bang
            // CurveElement (lop cha THAT trong native model) roi tu ep kieu/loc lai ModelCurve sau,
            // dung y huong dan trong chinh thong bao loi cua Revit.
            var curveIds = sketch.GetDependentElements(new ElementClassFilter(typeof(CurveElement)));
            modelLines = curveIds
                .Select(id => doc.GetElement(id) as ModelCurve)
                .Where(mc => mc is not null)
                .Select(mc => (Curve: mc!, Line: mc!.GeometryCurve as Line))
                .Where(t => t.Line is not null)
                .Select(t => (t.Curve, Line: t.Line!))
                .ToList();
        }
        catch (Exception ex)
        {
            return (false, $"{label}: lỗi khi đọc Sketch — {ex.Message}");
        }

        if (modelLines.Count < 3)
            return (false, $"{label}: bỏ qua — biên sàn không phải đa giác thẳng đơn giản (có cạnh cong?).");

        // Sap xep cac canh thanh 1 VONG LAP LIEN TUC theo dung thu tu noi tiep (can biet CHINH XAC 2
        // canh nao gap nhau tai moi dinh de tinh lai dinh do sau khi dich chuyen) - san co lo mo se ra
        // NHIEU vong, chi lay vong NGOAI CUNG (tong chieu dai lon nhat).
        var loop = BuildLoop(modelLines);
        if (loop is null || loop.Count < 3)
            return (false, $"{label}: bỏ qua — không dựng được vòng biên khép kín (topology không đơn giản).");

        // Tinh truoc cho MOI canh: co Grid song song gan khong, dang le khong - canh nao dang le thi co
        // sẵn duong thang MOI (da dich), canh nao khong co Grid hoac da tron thi giu nguyen duong cu.
        var newLines = new Line[loop.Count];
        var changeNotes = new List<string>();
        var anyChanged = false;

        for (var i = 0; i < loop.Count; i++)
        {
            var (_, p0, p1) = loop[i];
            var direction = (p1 - p0).Normalize();
            var normal = new XYZ(-direction.Y, direction.X, 0);
            var edgeN = p0.DotProduct(normal);

            var grid = GridMatcher.FindNearestParallel(direction, normal, edgeN, grids, out var gridN);
            if (grid is null)
            {
                newLines[i] = Line.CreateBound(p0, p1);
                continue;
            }

            var (roundedMm, deltaFeet) = GridMatcher.RoundGap(edgeN - gridN, snapUnitMm);
            if (Math.Abs(deltaFeet) < 1e-6)
            {
                newLines[i] = Line.CreateBound(p0, p1);
                continue;
            }

            var moveVector = normal.Multiply(deltaFeet);
            newLines[i] = Line.CreateBound(p0 + moveVector, p1 + moveVector);
            changeNotes.Add($"Grid \"{grid.Name}\" -> {roundedMm}mm");
            anyChanged = true;
        }

        if (!anyChanged)
            return (false, $"{label}: đã tròn theo mọi Grid liên quan, không cần chỉnh.");

        // Dinh thu i (giua canh i-1 va canh i) = giao diem 2 duong thang (da dich hoac giu nguyen) —
        // dung cho DA GIAC BAT KY, khong chi hinh chu nhat.
        var newVertices = new XYZ[loop.Count];
        for (var i = 0; i < loop.Count; i++)
        {
            var prevLine = newLines[(i - 1 + loop.Count) % loop.Count];
            var currLine = newLines[i];

            var prevP0 = prevLine.GetEndPoint(0);
            var prevDir = (prevLine.GetEndPoint(1) - prevP0).Normalize();
            var currP0 = currLine.GetEndPoint(0);
            var currDir = (currLine.GetEndPoint(1) - currP0).Normalize();

            newVertices[i] = IntersectLines(currP0, currDir, prevP0, prevDir) ?? currP0;
        }

        try
        {
            // SketchEditScope KHONG phai 1 Transaction (giong FamilyEditScope) - Start()/Commit() BAT
            // BUOC phai goi khi KHONG co Transaction nao dang mo ("document dang modifiable" chinh la
            // Revit noi co Transaction dang mo, day la nguyen nhan that su cua loi "not permitted to
            // start" gap truoc do khi ham nay duoc goi tu ben trong 1 Transaction boc ngoai o Command).
            // Viec sua geometry that (SetGeometryCurve) lai CAN 1 Transaction binh thuong - nhung phai
            // mo/dong Transaction do LONG BEN TRONG scope (sau Start(), truoc Commit()), khong phai boc
            // quanh ca scope.
            using var sketchScope = new SketchEditScope(doc, $"Sửa biên sàn — {label}");
            sketchScope.Start(floor.SketchId);

            using (var editTx = new Transaction(doc, "Sửa biên sàn"))
            {
                editTx.Start();

                for (var i = 0; i < loop.Count; i++)
                {
                    var newP0 = newVertices[i];
                    var newP1 = newVertices[(i + 1) % loop.Count];
                    if (newP0.DistanceTo(newP1) < 1e-6)
                        continue;

                    loop[i].Curve.SetGeometryCurve(Line.CreateBound(newP0, newP1), false);
                }

                editTx.Commit();
            }

            sketchScope.Commit(new SketchFailuresPreprocessor());
        }
        catch (Exception ex)
        {
            return (false, $"{label}: lỗi khi sửa Sketch — {ex.Message}{levelDiagnostic}");
        }

        return (true, $"{label}: {string.Join("; ", changeNotes)} (đã sửa biên sàn).");
    }

    /// <summary>Sắp xếp danh sách cạnh (chưa có thứ tự) thành 1 VÒNG LẶP liên tục — đi từ 1 cạnh bất kỳ,
    /// lặp tìm cạnh còn lại có 1 đầu trùng (trong CornerToleranceFeet) với đầu cuối hiện tại, nối tiếp
    /// (đảo chiều nếu cần) cho tới khi hết cạnh khớp được. Nếu biên có NHIỀU vòng tách biệt (sàn có lỗ
    /// mở), trả về vòng có TỔNG CHIỀU DÀI LỚN NHẤT (biên ngoài cùng) — bỏ qua các vòng lỗ mở bên trong.
    /// Trả về null nếu danh sách rỗng.</summary>
    private static List<(ModelCurve Curve, XYZ P0, XYZ P1)>? BuildLoop(
        List<(ModelCurve Curve, Line Line)> modelLines)
    {
        var remaining = modelLines
            .Select(m => (m.Curve, P0: m.Line.GetEndPoint(0), P1: m.Line.GetEndPoint(1)))
            .ToList();
        var loops = new List<List<(ModelCurve Curve, XYZ P0, XYZ P1)>>();

        while (remaining.Count > 0)
        {
            var first = remaining[0];
            remaining.RemoveAt(0);
            var loop = new List<(ModelCurve Curve, XYZ P0, XYZ P1)> { first };
            var currentEnd = first.P1;

            var progress = true;
            while (progress && remaining.Count > 0)
            {
                progress = false;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var (curve, p0, p1) = remaining[i];
                    if (p0.DistanceTo(currentEnd) <= CornerToleranceFeet)
                    {
                        loop.Add((curve, p0, p1));
                        currentEnd = p1;
                        remaining.RemoveAt(i);
                        progress = true;
                        break;
                    }

                    if (p1.DistanceTo(currentEnd) <= CornerToleranceFeet)
                    {
                        loop.Add((curve, p1, p0));
                        currentEnd = p0;
                        remaining.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
            }

            loops.Add(loop);
        }

        return loops.Count == 0
            ? null
            : loops.OrderByDescending(l => l.Sum(e => e.P0.DistanceTo(e.P1))).First();
    }

    private static XYZ? IntersectLines(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
    {
        var denom = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denom) < 1e-9)
            return null;

        var diff = p2 - p1;
        var t1 = (diff.X * d2.Y - diff.Y * d2.X) / denom;
        return new XYZ(p1.X + d1.X * t1, p1.Y + d1.Y * t1, p1.Z);
    }

    private static List<(Edge Edge, Line Line)> GetOuterLoopLinesWithEdge(PlanarFace face)
    {
        var loops = new List<List<(Edge Edge, Line Line)>>();
        foreach (EdgeArray loop in face.EdgeLoops)
        {
            var lines = new List<(Edge, Line)>();
            foreach (Edge edge in loop)
            {
                if (edge.AsCurve() is Line line)
                    lines.Add((edge, line));
            }

            loops.Add(lines);
        }

        return loops.Count == 0
            ? new List<(Edge, Line)>()
            : loops.OrderByDescending(l => l.Sum(e => e.Line.Length)).First();
    }

    private static PlanarFace? GetTopFace(Floor floor)
    {
        // ComputeReferences = true BAT BUOC de Edge.Reference (dung cho CreateDimension) tra ve
        // Reference THAT su dung duoc - mac dinh (false) khien Reference vo hieu, gay loi "Invalid
        // number of references" luc tao Dimension (da gap that voi BeamSnapper, sua cung nguyen nhan).
        var geometryElement = floor.get_Geometry(new Options { ComputeReferences = true });
        if (geometryElement is null)
            return null;

        PlanarFace? best = null;
        foreach (var geomObj in geometryElement)
        {
            if (geomObj is not Solid solid || solid.Faces.IsEmpty)
                continue;

            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar || planar.FaceNormal.Z < 0.9)
                    continue;

                if (best is null || planar.Origin.Z > best.Origin.Z)
                    best = planar;
            }
        }

        return best;
    }

    private static string Describe(Floor floor)
    {
        var bbox = floor.get_BoundingBox(null);
        if (bbox is null)
            return "Sàn";

        var xMm = Math.Round(UnitUtils.ConvertFromInternalUnits((bbox.Min.X + bbox.Max.X) / 2.0, UnitTypeId.Millimeters));
        var yMm = Math.Round(UnitUtils.ConvertFromInternalUnits((bbox.Min.Y + bbox.Max.Y) / 2.0, UnitTypeId.Millimeters));
        return $"Sàn tại ({xMm}, {yMm})";
    }

    private sealed class SketchFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
            }

            return FailureProcessingResult.Continue;
        }
    }
}
