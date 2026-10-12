namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>So sánh 2 snapshot rebar (dùng cho tính năng "Kiểm tra thay đổi Rebar" — xem
/// Kien-truc-Rebar-Change-Checker.md mục 4.4). Pure C#, no Revit dependency, unit-test được như
/// BarScheduleBuilder.</summary>
internal static class RebarChangeDiff
{
    /// <summary>Ngưỡng dung sai khi so sánh 2 giá trị double (mm) — tránh báo "Modified" giả do sai
    /// số làm tròn floating-point (giá trị đọc từ Revit đã quy đổi ra mm ngay tại
    /// RebarShapeSnapshotReader, xem RebarSegmentValue.ValueMm).</summary>
    private const double ToleranceMm = 0.1;

    public static RebarChangeResult Compute(
        IReadOnlyDictionary<long, RebarShapeSnapshotEntry> oldSnapshot,
        IReadOnlyDictionary<long, RebarShapeSnapshotEntry> newSnapshot)
    {
        var added = new List<RebarShapeSnapshotEntry>();
        var modified = new List<RebarChangeModifiedEntry>();
        var deleted = new List<RebarShapeSnapshotEntry>();

        foreach (var (id, entry) in newSnapshot)
        {
            if (!oldSnapshot.TryGetValue(id, out var oldEntry))
            {
                added.Add(entry);
                continue;
            }

            var changes = DiffSegments(oldEntry, entry);
            if (changes.Count > 0)
                modified.Add(new RebarChangeModifiedEntry { Entry = entry, Changes = changes });
        }

        foreach (var (id, entry) in oldSnapshot)
        {
            if (!newSnapshot.ContainsKey(id))
                deleted.Add(entry);
        }

        return new RebarChangeResult { Added = added, Modified = modified, Deleted = deleted };
    }

    private static List<RebarSegmentChange> DiffSegments(RebarShapeSnapshotEntry oldEntry, RebarShapeSnapshotEntry newEntry)
    {
        var changes = new List<RebarSegmentChange>();

        if (oldEntry.RebarShapeTypeId != newEntry.RebarShapeTypeId || oldEntry.Segments.Count != newEntry.Segments.Count)
        {
            changes.Add(new RebarSegmentChange { Label = "Shape", Note = "Cấu trúc shape đã đổi (số đoạn hoặc RebarShape Type khác trước)" });
            return changes;
        }

        for (var i = 0; i < newEntry.Segments.Count; i++)
        {
            var newSegment = newEntry.Segments[i];
            var oldSegment = FindMatchingSegment(oldEntry.Segments, newSegment);

            if (oldSegment is null)
            {
                changes.Add(new RebarSegmentChange { Label = newSegment.Label, Note = "Cấu trúc shape đã đổi" });
            }
            else if (Math.Abs(oldSegment.ValueMm - newSegment.ValueMm) > ToleranceMm)
            {
                changes.Add(new RebarSegmentChange
                {
                    Label = newSegment.Label,
                    OldValueMm = oldSegment.ValueMm,
                    NewValueMm = newSegment.ValueMm
                });
            }
        }

        return changes;
    }

    private static RebarSegmentValue? FindMatchingSegment(IReadOnlyList<RebarSegmentValue> segments, RebarSegmentValue target)
    {
        foreach (var segment in segments)
        {
            if (segment.SegmentIndex == target.SegmentIndex && segment.Label == target.Label)
                return segment;
        }
        return null;
    }
}
