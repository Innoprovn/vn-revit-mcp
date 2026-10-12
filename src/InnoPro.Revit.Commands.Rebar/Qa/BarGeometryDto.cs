namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>
/// One physical bar position, already expanded out of its Rebar Set / Area or Path Reinforcement
/// parent (spec §5 "bản chất dữ liệu cần trích xuất"). Revit-independent so RuleEngine can be
/// unit-tested without opening Revit.
/// </summary>
internal sealed class BarGeometryDto
{
    public required long ElementId { get; init; }
    public required int BarPositionIndex { get; init; }
    public required string Mark { get; init; }
    public required string ShapeCode { get; init; }
    public required string BarTypeName { get; init; }
    public required double DiameterMm { get; init; }
    public required IReadOnlyList<BarSegment> CenterlineSegments { get; init; }
    public long? HostElementId { get; init; }

    /// <summary>% of this bar's length that lies inside concrete, matched geometrically against
    /// nearby concrete solids rather than via the host link (see ConcreteSolidIndex). A bar nowhere
    /// near concrete scores 0, not null — null means the bar had no usable length to measure.</summary>
    public double? InsideHostSolidPercent { get; init; }
}
