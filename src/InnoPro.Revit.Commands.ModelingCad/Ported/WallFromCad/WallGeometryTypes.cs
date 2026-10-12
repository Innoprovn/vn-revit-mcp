using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFromCad;

/// <summary>1 đoạn thẳng thô đọc được từ hình học CAD, đã "làm phẳng" về Z=0 (coi mặt bằng CAD là
/// phẳng — đúng với gần như mọi trường hợp CAD nền kiến trúc chèn vào 1 view mặt bằng).</summary>
internal sealed class RawSegment
{
    public required XYZ P0 { get; init; }
    public required XYZ P1 { get; init; }

    /// <summary>Vector đơn vị từ P0 đến P1.</summary>
    public required XYZ Direction { get; init; }

    public required double Length { get; init; }
}

/// <summary>1 cung tròn thô đọc được từ hình học CAD, đã làm phẳng về Z=0. Lưu cả 3 điểm (đầu/cuối/
/// giữa) chứ không chỉ góc bắt đầu-kết thúc, vì góc tuyệt đối chỉ tính được khi biết tâm nào dùng để
/// so sánh (xem WallPairing.ComputeAbsoluteSweep) — 2 cung đồng tâm có thể có tâm lệch nhau vài mm do
/// sai số CAD, tính lại góc theo 1 tâm chung mới cho kết quả nhất quán.</summary>
internal sealed class RawArc
{
    public required XYZ Center { get; init; }
    public required double Radius { get; init; }
    public required XYZ P0 { get; init; }
    public required XYZ P1 { get; init; }
    public required XYZ Mid { get; init; }
    public required double ArcLengthFeet { get; init; }
}

/// <summary>1 layer CAD, kèm số liệu để hiển thị trong danh sách chọn.</summary>
internal sealed record CadLayerEntry(string Name, int LineCount, int ArcCount, int SkippedCurvedCount);

/// <summary>Kết quả quét hình học CAD: nét thẳng (gộp từ Line + PolyLine) và nét cung tròn (Arc) nhóm
/// theo tên layer — cả 2 loại đều được tool dùng để nhận diện tường (xem WallPairing). Chỉ những nét
/// cong khác (Ellipse/NurbSpline/HermiteSpline...) mới thật sự bị bỏ qua, đếm riêng để báo người
/// dùng.</summary>
internal sealed class CadScanResult
{
    public required IReadOnlyDictionary<string, List<RawSegment>> SegmentsByLayer { get; init; }
    public required IReadOnlyDictionary<string, List<RawArc>> ArcsByLayer { get; init; }
    public required IReadOnlyDictionary<string, int> SkippedCurvedByLayer { get; init; }

    /// <summary>Danh sách layer để hiển thị lên ComboBox, sắp xếp theo tổng số đối tượng giảm dần
    /// (layer tường thường là layer có nhiều nét nhất).</summary>
    public List<CadLayerEntry> GetLayerSummaries()
    {
        var names = new HashSet<string>(SegmentsByLayer.Keys);
        names.UnionWith(ArcsByLayer.Keys);
        names.UnionWith(SkippedCurvedByLayer.Keys);

        var result = new List<CadLayerEntry>();
        foreach (var name in names)
        {
            var lineCount = SegmentsByLayer.TryGetValue(name, out var lines) ? lines.Count : 0;
            var arcCount = ArcsByLayer.TryGetValue(name, out var arcs) ? arcs.Count : 0;
            var curvedCount = SkippedCurvedByLayer.TryGetValue(name, out var c) ? c : 0;
            result.Add(new CadLayerEntry(name, lineCount, arcCount, curvedCount));
        }

        return result
            .OrderByDescending(e => e.LineCount + e.ArcCount + e.SkippedCurvedCount)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>1 đoạn tường đã xác định được đường tim + bề rộng — luôn đến từ 1 cặp nét thẳng song song
/// hoặc 1 cặp cung tròn đồng tâm ĐO TRỰC TIẾP (xem WallPairing) — không còn suy luận/"mượn" bề rộng
/// cho nét lẻ như bản trước. Centerline dùng kiểu Curve (không phải Line) để chứa được cả Line lẫn Arc — Revit
/// Wall.Create nhận Curve nên tường cong tạo được y hệt tường thẳng, không cần code riêng.
///
/// KHÔNG còn lưu 2 nét biên (boundary) như bản trước: vì Wall Type luôn được lọc khớp CHÍNH XÁC
/// ThicknessMm (xem ThicknessBucket), và cơ chế Location Line chỉ cần Centerline + WallType.Width là
/// đủ tính đúng mọi trường hợp (xem WallCoreOffsets) — giữ lại 2 đường biên chỉ tạo thêm chỗ để sai
/// mà không ai dùng tới.</summary>
internal sealed class WallCandidate
{
    /// <summary>Đường tim, nằm ở Z=0 — WallBuilder nâng lên đúng cao độ Base Level lúc tạo Wall.</summary>
    public required Curve Centerline { get; init; }

    /// <summary>Bề rộng đo trực tiếp từ cặp nét/cung CAD — đã làm tròn đến mm nguyên gần nhất.</summary>
    public required double ThicknessMm { get; init; }

    public required double LengthFeet { get; init; }

}

/// <summary>Kết quả bắt cặp/nhận diện tường của 1 layer. "Unresolved" = nét/cung lẻ không bắt được cặp
/// song song/đồng tâm hợp lệ với nét/cung nào khác trong layer (xem WallPairing.Detect) — bị bỏ qua
/// hoàn toàn, không vẽ.</summary>
internal sealed record WallDetectionResult(
    IReadOnlyList<WallCandidate> Candidates,
    int UnresolvedLineCount,
    int UnresolvedArcCount);

internal enum TopConstraintMode
{
    UpToLevel,
    Unconnected
}

/// <summary>1 nhóm các đoạn tường có cùng bề rộng (đã làm tròn mm) — mỗi nhóm ứng với 1 dòng trong
/// bảng gán Wall Type. SelectedWallType == null nghĩa là KHÔNG vẽ nhóm này (yêu cầu Mục 4 của
/// người dùng).
///
/// Tất cả class ở đây khai internal (không phải public) dù được cửa sổ WPF (public partial class,
/// bắt buộc bởi codegen XAML) dùng làm kiểu tham số/thuộc tính — vì thế constructor và property
/// `Plan` của WallFromCadWindow cũng phải khai internal (không phải public), nếu không sẽ lỗi biên
/// dịch CS0051/CS0053 "kiểu ít truy cập hơn thành viên" (bài học đã ghi lại ở RebarUBar/OptionsWindow
/// trong repo này — property public trỏ tới enum internal).</summary>
internal sealed class ThicknessBucket
{
    public required int ThicknessMm { get; init; }
    public required List<WallCandidate> Candidates { get; init; }

    /// <summary>Chỉ các Wall Type (Kind = Basic) có Width làm tròn mm khớp đúng ThicknessMm — đảm
    /// bảo chọn Type nào cũng khớp bề rộng CAD, đúng yêu cầu Mục 4.</summary>
    public required List<WallType> MatchingWallTypes { get; init; }

    public WallType? SelectedWallType { get; set; }
}

internal sealed class WallBuildOptions
{
    public required WallLocationLine LocationLine { get; init; }
    public required Level BaseLevel { get; init; }
    public required double BaseOffsetMm { get; init; }
    public required TopConstraintMode TopMode { get; init; }
    public Level? TopLevel { get; init; }
    public double TopOffsetMm { get; init; }
    public double UnconnectedHeightMm { get; init; }
}

internal sealed class WallBuildPlan
{
    public required string LayerName { get; init; }
    public required List<ThicknessBucket> Buckets { get; init; }
    public required WallBuildOptions Options { get; init; }
}
