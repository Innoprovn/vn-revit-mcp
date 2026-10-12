namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Exact centerline + diameter of one physical bar position, kept on the violation so the
/// UI can draw a highlight solid for just that bar — the parent Rebar element's ElementId is shared
/// by every bar in the same Rebar Set, so overriding graphics by ElementId alone would color the
/// whole set instead of the one flagged bar.</summary>
internal sealed class BarHighlightGeometry
{
    public required IReadOnlyList<BarSegment> Segments { get; init; }
    public required double DiameterMm { get; init; }
}

/// <summary>One report row (spec §7): Element ID / Mark / Rule / model value / required value / severity / location.</summary>
internal sealed class RuleViolation
{
    public required long ElementId { get; init; }

    /// <summary>Which bar position within the parent Rebar Set this row is about. Every bar in a set
    /// shares one ElementId, so without this the report shows several identical-looking rows and the
    /// user can't tell one flagged bar from another (or how many were flagged at all).</summary>
    public int? BarPositionIndex { get; init; }

    public required string Mark { get; init; }
    public required string RuleName { get; init; }
    public required string ModelValue { get; init; }
    public required string RequiredValue { get; init; }
    public required Severity Severity { get; init; }
    public Point3d? Location { get; init; }
    public long? OtherElementId { get; init; }
    public BarHighlightGeometry? PrimaryGeometry { get; init; }
    public BarHighlightGeometry? OtherGeometry { get; init; }

    /// <summary>Khoá định danh 1 dòng vi phạm, dùng cho tính năng "Bỏ qua" (ignore) — sau khi user
    /// bỏ qua 1 dòng thì lần Refresh kế tiếp phải nhận ra đúng dòng đó và tiếp tục ẩn nó, dù object
    /// RuleViolation đã được tạo mới hoàn toàn. KHÔNG đưa ModelValue/RequiredValue vào khoá: giá trị
    /// đo được (vd distance=18.0mm) có thể xê dịch chút sau khi model đổi, dòng vẫn là cùng 1 vấn đề
    /// giữa cùng cặp thanh — đưa vào khoá sẽ làm ignore "bung" ra trở lại một cách khó hiểu.</summary>
    public string IgnoreKey => $"{ElementId}|{BarPositionIndex}|{OtherElementId}|{RuleName}";
}
