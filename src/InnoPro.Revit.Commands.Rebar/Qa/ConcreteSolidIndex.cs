namespace InnoPro.Revit.Commands.Rebar.Qa;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

/// <summary>
/// Finds how much of a bar lies inside concrete, by geometry rather than by Rebar.GetHostId().
///
/// IFC-derived models routinely import rebar and concrete as unrelated elements, so a host-based
/// lookup returns nothing for most bars — and a bar with no host would be silently skipped by the
/// outside-concrete check, which is exactly the failure mode that let visibly-protruding bars go
/// unreported. Matching on geometry instead means a bar sitting nowhere near any concrete scores 0%
/// and gets flagged, rather than disappearing from the report.
/// </summary>
internal sealed class ConcreteSolidIndex
{
    /// <summary>Categories treated as concrete hosts. Free Form / IFC-imported concrete usually
    /// lands in Generic Models; the structural ones cover natively-modelled hosts.</summary>
    private static readonly BuiltInCategory[] ConcreteCategories =
    {
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Mass
    };

    private const string HighlightNameMarker = "RebarQA Highlight";

    private readonly Document _doc;
    private readonly List<Candidate> _candidates = new();

    private sealed class Candidate
    {
        public required ElementId ElementId { get; init; }
        public required BoundingBoxXYZ BoundingBox { get; init; }
        public List<Solid>? Solids { get; set; }
    }

    public ConcreteSolidIndex(Document doc)
    {
        _doc = doc;
        CollectCandidates();
    }

    public int CandidateCount => _candidates.Count;

    private void CollectCandidates()
    {
        var filter = new ElementMulticategoryFilter(ConcreteCategories);
        var elements = new FilteredElementCollector(_doc)
            .WhereElementIsNotElementType()
            .WherePasses(filter);

        foreach (var element in elements)
        {
            // Skip reinforcement itself, and the temporary highlight solids this add-in creates
            // (they are Generic Models too, so they'd otherwise count as "concrete").
            if (element is Rebar or RebarInSystem or AreaReinforcement or PathReinforcement)
                continue;
            if (element.Name.Contains(HighlightNameMarker))
                continue;

            var bbox = element.get_BoundingBox(null);
            if (bbox is null)
                continue;

            _candidates.Add(new Candidate { ElementId = element.Id, BoundingBox = bbox });
        }
    }

    /// <summary>Percentage of the curves' total length that lies inside any concrete solid.
    /// Returns null only when the curves have no usable length.</summary>
    public double? ComputeInsidePercent(IList<Curve> curves)
    {
        var totalLength = curves.Sum(c => c.Length);
        if (totalLength < 1e-9)
            return null;

        var barBox = ComputeCurvesBoundingBox(curves);
        var insideLength = 0.0;

        foreach (var candidate in _candidates)
        {
            if (!Overlaps(candidate.BoundingBox, barBox))
                continue;

            foreach (var solid in GetSolids(candidate))
            {
                foreach (var curve in curves)
                {
                    try
                    {
                        var options = new SolidCurveIntersectionOptions
                        {
                            ResultType = SolidCurveIntersectionMode.CurveSegmentsInside
                        };
                        var intersection = solid.IntersectWithCurve(curve, options);
                        for (var i = 0; i < intersection.SegmentCount; i++)
                            insideLength += intersection.GetCurveSegment(i).Length;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // Bat ca goc Autodesk.Revit.Exceptions.ApplicationException (khong chi
                        // ArgumentException) - solid be tong hinh hoc phuc tap/nhieu mat (IFC-import,
                        // non-manifold...) co the khien geometry kernel cua Revit nem ra cac loai
                        // exception khac nhau tu IntersectWithCurve (vd loi noi bo kernel dang
                        // "Error from cutGCurveWithGeometry"), khong chi ArgumentException. Bo qua
                        // cap curve/solid nay, tinh conservative: lam giam % ket qua thay vi lam
                        // sap ca luot QA hoac thoi phong % sai.
                    }
                }
            }
        }

        // Adjacent concrete elements meet without overlapping, so summing across candidates is
        // right for a bar that spans a joint; clamp anyway in case solids do overlap.
        var percent = insideLength / totalLength * 100.0;
        return percent > 100.0 ? 100.0 : percent;
    }

    private List<Solid> GetSolids(Candidate candidate)
    {
        // Geometry extraction is the expensive part, so only pay for candidates a bar actually
        // reaches, and only once each.
        if (candidate.Solids is not null)
            return candidate.Solids;

        var solids = new List<Solid>();
        var element = _doc.GetElement(candidate.ElementId);
        var geometry = element?.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });

        if (geometry is not null)
            CollectSolids(geometry, solids);

        candidate.Solids = solids;
        return solids;
    }

    private static void CollectSolids(GeometryElement geometry, List<Solid> results)
    {
        foreach (var geomObj in geometry)
        {
            switch (geomObj)
            {
                case Solid solid when solid.Volume > 1e-9:
                    results.Add(solid);
                    break;
                case GeometryInstance instance:
                    CollectSolids(instance.GetInstanceGeometry(), results);
                    break;
            }
        }
    }

    private static (XYZ Min, XYZ Max) ComputeCurvesBoundingBox(IList<Curve> curves)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (var curve in curves)
        {
            foreach (var p in curve.Tessellate())
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        }

        return (new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
    }

    private static bool Overlaps(BoundingBoxXYZ elementBox, (XYZ Min, XYZ Max) barBox)
    {
        var min = elementBox.Transform.OfPoint(elementBox.Min);
        var max = elementBox.Transform.OfPoint(elementBox.Max);

        return min.X <= barBox.Max.X && max.X >= barBox.Min.X
            && min.Y <= barBox.Max.Y && max.Y >= barBox.Min.Y
            && min.Z <= barBox.Max.Z && max.Z >= barBox.Min.Z;
    }
}
