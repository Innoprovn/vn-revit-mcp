namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Nhóm B (spec §5): classifies each clash candidate into Info/Warning/Fail purely by how
/// far the two bars overlap (clearance - distance), no other signal (shape code / angle / overlap
/// length are still shown in the report for context, but no longer drive severity).</summary>
internal sealed class ClashRebarRebarCheck : IRuleCheck
{
    public string Name => "Clash rebar-rebar";

    private readonly ClashDetector _detector = new();

    public IEnumerable<RuleViolation> Run(IReadOnlyList<BarGeometryDto> bars, RuleConfig config)
    {
        var clashConfig = config.ClashRebarRebar;
        foreach (var candidate in _detector.FindCandidates(bars, clashConfig))
        {
            yield return Classify(candidate);
        }
    }

    private static RuleViolation Classify(ClashCandidate c)
    {
        var clearanceMm = c.BarA.DiameterMm / 2 + c.BarB.DiameterMm / 2;
        var overlapDepthMm = clearanceMm - c.DistanceMm;

        var severity = overlapDepthMm switch
        {
            >= 2 => Severity.Fail,
            > 1 => Severity.Warning,
            _ => Severity.Info
        };

        return new RuleViolation
        {
            ElementId = c.BarA.ElementId,
            BarPositionIndex = c.BarA.BarPositionIndex,
            Mark = c.BarA.Mark,
            RuleName = "Clash: rebar overlap",
            ModelValue = $"distance={c.DistanceMm:F1}mm",
            RequiredValue = $"clearance >= {clearanceMm:F1}mm",
            Severity = severity,
            Location = c.LocationMm,
            OtherElementId = c.BarB.ElementId,
            PrimaryGeometry = new BarHighlightGeometry { Segments = c.BarA.CenterlineSegments, DiameterMm = c.BarA.DiameterMm },
            OtherGeometry = new BarHighlightGeometry { Segments = c.BarB.CenterlineSegments, DiameterMm = c.BarB.DiameterMm }
        };
    }
}
