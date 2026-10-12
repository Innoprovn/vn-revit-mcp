namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>
/// Snapshot of one Rebar's shape-segment lengths at a point in time, used by the "Kiểm tra thay đổi
/// Rebar" feature (xem Kien-truc-Rebar-Change-Checker.md) — so sánh giữa 2 lần bấm nút để phát hiện
/// kích thước đoạn (A, B, C...) bị đổi ngoài ý muốn, thường do Type Parameter dùng chung giữa nhiều
/// Rebar instance cùng RebarShape Type. Revit-independent so RebarChangeDiff can be unit-tested
/// without opening Revit.
/// </summary>
internal sealed class RebarShapeSnapshotEntry
{
    public required long ElementId { get; init; }
    public string Mark { get; init; } = "";
    public long? HostElementId { get; init; }
    public string HostElementName { get; init; } = "";
    public required long RebarShapeTypeId { get; init; }
    public string RebarShapeTypeName { get; init; } = "";
    public required IReadOnlyList<RebarSegmentValue> Segments { get; init; }
}

/// <summary>1 giá trị chiều dài đoạn (segment) trong RebarShape — đọc động qua ParamId, không hardcode
/// tên "A"/"B"/"Length 1" vì tên tham số phụ thuộc ngôn ngữ/phiên bản Revit (xem
/// RebarShapeSnapshotReader trong RebarQA.Extraction).</summary>
internal sealed class RebarSegmentValue
{
    public required int SegmentIndex { get; init; }
    public required string Label { get; init; }

    /// <summary>Milimét — quy đổi từ internal units (feet) ngay lúc đọc (RebarShapeSnapshotReader),
    /// khớp quy ước đơn vị hiển thị đã dùng cho DiameterMm trong BarGeometryDto.</summary>
    public required double ValueMm { get; init; }
}

internal sealed class RebarSegmentChange
{
    public required string Label { get; init; }
    public double? OldValueMm { get; init; }
    public double? NewValueMm { get; init; }

    /// <summary>Khác null khi số lượng/cấu trúc đoạn thay đổi (không còn khớp segment cũ theo index)
    /// thay vì chỉ đổi giá trị 1 đoạn đã có — vd shape bị đổi loại.</summary>
    public string? Note { get; init; }
}

internal sealed class RebarChangeModifiedEntry
{
    public required RebarShapeSnapshotEntry Entry { get; init; }
    public required IReadOnlyList<RebarSegmentChange> Changes { get; init; }
}

internal sealed class RebarChangeResult
{
    public required IReadOnlyList<RebarShapeSnapshotEntry> Added { get; init; }
    public required IReadOnlyList<RebarChangeModifiedEntry> Modified { get; init; }
    public required IReadOnlyList<RebarShapeSnapshotEntry> Deleted { get; init; }

    public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0;
}

/// <summary>1 dòng trong bảng "Lịch sử thay đổi" (tab thứ 3 của cửa sổ báo cáo). Giá trị đã format
/// sẵn thành chuỗi để bind thẳng vào DataGrid, cùng quy ước với BarScheduleRow.LengthText — lượt
/// kiểm tra không phát hiện thay đổi nào thì KHÔNG sinh dòng nào (xem RebarChangeHistoryBuilder).</summary>
internal sealed class RebarChangeHistoryRow
{
    public required string Time { get; init; }
    public required string ChangeType { get; init; }
    public required string RebarLabel { get; init; }
    public required long ElementId { get; init; }
    public string HostName { get; init; } = "";
    public string ShapeTypeName { get; init; } = "";
    public string SegmentLabel { get; init; } = "";
    public string OldValue { get; init; } = "";
    public string NewValue { get; init; } = "";
}
