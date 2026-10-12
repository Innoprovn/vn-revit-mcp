namespace InnoPro.Revit.Commands.Rebar.Qa;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

/// <summary>
/// Layer 1 (spec §2/§5): reads Rebar geometry straight out of the active model via the Revit API,
/// expanding Rebar Sets and Area/Path Reinforcement systems into one BarGeometryDto per physical bar.
/// </summary>
internal sealed class RebarExtractor
{
    /// <summary>
    /// scopeElementIds: the user's current Revit selection. Empty/null → scan the whole document
    /// (unchanged behavior). Non-empty → only bars directly selected (Rebar/AreaReinforcement/
    /// PathReinforcement) or hosted by a selected non-rebar element (e.g. the barrier host) are
    /// extracted — lets a model with many components be QA'd one component at a time.
    /// </summary>
    public IReadOnlyList<BarGeometryDto> ExtractFromDocument(Document doc, ICollection<ElementId>? scopeElementIds = null, bool measureInsideConcrete = true)
    {
        var rebarIds = new HashSet<ElementId>(
            new FilteredElementCollector(doc).OfClass(typeof(Rebar)).ToElementIds());

        foreach (var ar in new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).Cast<AreaReinforcement>())
        {
            foreach (var id in ar.GetRebarInSystemIds())
                rebarIds.Add(id);
        }

        foreach (var pr in new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).Cast<PathReinforcement>())
        {
            foreach (var id in pr.GetRebarInSystemIds())
                rebarIds.Add(id);
        }

        if (scopeElementIds is { Count: > 0 } scope)
            rebarIds = FilterByScope(doc, rebarIds, scope);

        var concreteIndex = measureInsideConcrete ? new ConcreteSolidIndex(doc) : null;

        var results = new List<BarGeometryDto>();
        foreach (var id in rebarIds)
        {
            if (doc.GetElement(id) is Rebar rebar)
                results.AddRange(ExtractFromRebar(rebar, doc, concreteIndex));
        }
        return results;
    }

    private static HashSet<ElementId> FilterByScope(Document doc, HashSet<ElementId> allRebarIds, ICollection<ElementId> scope)
    {
        var directIds = new HashSet<ElementId>();
        var hostFilterIds = new HashSet<ElementId>();

        foreach (var id in scope)
        {
            switch (doc.GetElement(id))
            {
                case Rebar:
                    directIds.Add(id);
                    break;
                case AreaReinforcement ar:
                    foreach (var rid in ar.GetRebarInSystemIds())
                        directIds.Add(rid);
                    break;
                case PathReinforcement pr:
                    foreach (var rid in pr.GetRebarInSystemIds())
                        directIds.Add(rid);
                    break;
                default:
                    // Not a rebar/system element itself — treat as a host to filter by
                    // (e.g. the barrier Generic Model instance).
                    hostFilterIds.Add(id);
                    break;
            }
        }

        var filtered = new HashSet<ElementId>();
        foreach (var id in allRebarIds)
        {
            if (directIds.Contains(id))
            {
                filtered.Add(id);
                continue;
            }

            if (hostFilterIds.Count > 0 && doc.GetElement(id) is Rebar rebar)
            {
                var hostId = rebar.GetHostId();
                if (hostId != ElementId.InvalidElementId && hostFilterIds.Contains(hostId))
                    filtered.Add(id);
            }
        }

        return filtered;
    }

    private static IEnumerable<BarGeometryDto> ExtractFromRebar(Rebar rebar, Document doc, ConcreteSolidIndex? concreteIndex)
    {
        var diameterMm = GetDiameterMm(doc, rebar);
        var mark = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
        var shapeCode = GetShapeCode(doc, rebar);
        var barTypeName = GetBarTypeName(doc, rebar);
        var hostId = SafeHostId(rebar);

        // For shape-driven rebar, GetCenterlineCurves ignores barPositionIndex and always returns the
        // first bar's geometry — every position in the set would collapse onto the same location,
        // making set members invisible to the clash and outside-concrete checks. The per-position
        // placement only comes from GetBarPositionTransform. Free Form rebar has no shape-driven
        // accessor, but there GetCenterlineCurves(barPositionIndex) really does vary per position.
        var accessor = rebar.IsRebarShapeDriven() ? rebar.GetShapeDrivenAccessor() : null;
        var baseCurves = accessor is not null ? TryGetCenterlineCurves(rebar, 0) : null;
        if (accessor is not null && baseCurves is null)
            yield break;

        var positionCount = rebar.NumberOfBarPositions;
        for (var i = 0; i < positionCount; i++)
        {
            if (!rebar.DoesBarExistAtPosition(i))
                continue;

            IList<Curve>? curves;
            if (accessor is not null)
            {
                // GetBarPositionTransform only describes the set's regular layout. Bars edited with
                // the ribbon's Edit Bars (Customization) tools carry an extra per-bar transform that
                // has to be applied on top, or the check silently runs against the pre-edit position.
                var layoutTransform = accessor.GetBarPositionTransform(i);
                var movedTransform = TryGetMovedBarTransform(rebar, i);

                curves = TryTransformCurves(baseCurves!, layoutTransform, movedTransform);
            }
            else
            {
                curves = TryGetCenterlineCurves(rebar, i);
            }

            if (curves is null)
                continue;

            var segments = CurveTessellator.TessellateAll(curves);
            if (segments.Count == 0)
                continue;

            var insideHostPercent = concreteIndex?.ComputeInsidePercent(curves);

            yield return new BarGeometryDto
            {
                ElementId = rebar.Id.Value,
                BarPositionIndex = i,
                Mark = mark,
                ShapeCode = shapeCode,
                BarTypeName = barTypeName,
                DiameterMm = diameterMm,
                CenterlineSegments = segments,
                HostElementId = hostId,
                InsideHostSolidPercent = insideHostPercent
            };
        }
    }

    /// <summary>Extra transform applied to one bar by the Edit Bars customization tools, or null if
    /// that bar was never moved.</summary>
    private static Transform? TryGetMovedBarTransform(Rebar rebar, int barPositionIndex)
    {
        try
        {
            var transform = rebar.GetMovedBarTransform(barPositionIndex);
            return transform is null || transform.IsIdentity ? null : transform;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Rebar doesn't support per-bar moves (or the index isn't movable) — treat as unmoved.
            return null;
        }
    }

    /// <summary>Applies the set's layout transform (+ per-bar moved transform, if any) to the base
    /// curves for one bar position. Guards CreateTransformed the same way TryGetCenterlineCurves
    /// and TryGetMovedBarTransform already guard their own Revit API calls: some bar-set layouts
    /// (irregular array/taper) produce a non-conformal transform for a specific position, which
    /// Revit refuses with "transform is not conformal" — without this guard that ArgumentException
    /// was unhandled and killed the ENTIRE extraction (whole-model or scoped selection alike), not
    /// just the one bad position.</summary>
    private static IList<Curve>? TryTransformCurves(IList<Curve> curves, Transform layoutTransform, Transform? movedTransform)
    {
        try
        {
            return curves
                .Select(c =>
                {
                    var positioned = c.CreateTransformed(layoutTransform);
                    return movedTransform is null ? positioned : positioned.CreateTransformed(movedTransform);
                })
                .ToList();
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            return null;
        }
    }

    private static IList<Curve>? TryGetCenterlineCurves(Rebar rebar, int barPositionIndex)
    {
        try
        {
            return rebar.GetCenterlineCurves(
                adjustForSelfIntersection: false,
                suppressBendRadius: false,
                suppressHooks: false,
                multiplanarOption: MultiplanarOption.IncludeOnlyPlanarCurves,
                barPositionIndex: barPositionIndex);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // TODO: verify against a real model whether any multiplanar (e.g. spiral) bars need
            // MultiplanarOption.IncludeAllMultiplanarCurves instead of being skipped here.
            return null;
        }
    }

    private static double GetDiameterMm(Document doc, Rebar rebar)
    {
        return doc.GetElement(rebar.GetTypeId()) is RebarBarType barType
            ? RevitUnits.FeetToMm(barType.BarModelDiameter)
            : 0;
    }

    private static string GetBarTypeName(Document doc, Rebar rebar)
    {
        return (doc.GetElement(rebar.GetTypeId()) as RebarBarType)?.Name ?? "";
    }

    private static string GetShapeCode(Document doc, Rebar rebar)
    {
        try
        {
            return (doc.GetElement(rebar.GetShapeId()) as RebarShape)?.Name ?? "";
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            // Free Form rebar with Workshop Instructions = Bent is matched to multiple shapes.
            return "";
        }
    }

    private static long? SafeHostId(Rebar rebar)
    {
        var hostId = rebar.GetHostId();
        return hostId != ElementId.InvalidElementId ? hostId.Value : null;
    }
}
