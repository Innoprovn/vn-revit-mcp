namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Flags bars whose centerline falls outside the concrete — a strong signal of a gross
/// placement error. The inside-percentage is computed during extraction by intersecting the bar
/// against nearby concrete solids found geometrically (see ConcreteSolidIndex), not via
/// Rebar.GetHostId(), so it still works on IFC-derived models where rebar has no host link and on
/// Free Form concrete, which has no RebarHostData / RebarCoverType at all.</summary>
internal sealed class BarOutsideHostCheck : IRuleCheck
{
    /// <summary>Solid.IntersectWithCurve sums segment lengths, so a fully-enclosed bar can land a
    /// hair under 100% — without this tolerance a 100% threshold would flag every bar in the model.</summary>
    private const double PercentTolerance = 0.5;

    public string Name => "Bar outside host";

    public IEnumerable<RuleViolation> Run(IReadOnlyList<BarGeometryDto> bars, RuleConfig config)
    {
        var threshold = config.BarOutsideHost.MinInsidePercent;
        if (threshold is not { } minPercent)
            yield break;

        foreach (var bar in bars)
        {
            if (bar.InsideHostSolidPercent is not { } percent)
                continue;

            if (percent >= minPercent - PercentTolerance)
                continue;

            var severity = percent < minPercent / 2 ? Severity.Fail : Severity.Warning;

            yield return new RuleViolation
            {
                ElementId = bar.ElementId,
                BarPositionIndex = bar.BarPositionIndex,
                Mark = bar.Mark,
                RuleName = "Bar outside concrete",
                ModelValue = $"inside concrete={percent:F0}%",
                RequiredValue = $"inside concrete >= {minPercent:F0}%",
                Severity = severity,
                Location = bar.CenterlineSegments.Count > 0 ? bar.CenterlineSegments[0].Start : null,
                PrimaryGeometry = new BarHighlightGeometry { Segments = bar.CenterlineSegments, DiameterMm = bar.DiameterMm }
            };
        }
    }
}
