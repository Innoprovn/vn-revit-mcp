namespace InnoPro.Revit.Commands.Rebar.Qa;

internal interface IRuleCheck
{
    string Name { get; }
    IEnumerable<RuleViolation> Run(IReadOnlyList<BarGeometryDto> bars, RuleConfig config);
}
