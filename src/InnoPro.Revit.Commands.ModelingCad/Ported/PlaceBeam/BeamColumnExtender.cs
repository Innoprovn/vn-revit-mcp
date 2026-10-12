using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceBeam;

internal static class BeamColumnExtender
{
    private const double PositionToleranceMm = 10.0;

    public static BeamExtensionResult Extend(Document doc, List<RawBeam> beams, double elevation, double maxDistanceFeet)
    {
        var columns = CollectColumns(doc, elevation);
        var result = new List<RawBeam>(beams.Count);
        var extendedBeamCount = 0;

        foreach (var beam in beams)
        {
            var startDirection = DirectionFrom(beam.End, beam.Start);
            var endDirection = DirectionFrom(beam.Start, beam.End);
            var startAtElevation = new XYZ(beam.Start.X, beam.Start.Y, elevation);
            var endAtElevation = new XYZ(beam.End.X, beam.End.Y, elevation);
            var startTarget = FindTarget(startAtElevation, startDirection, columns, maxDistanceFeet);
            var endTarget = FindTarget(endAtElevation, endDirection, columns, maxDistanceFeet);
            // Không kéo xuyên qua dầm thẳng hàng khác (dầm đã chia nhịp tại Grid nằm giữa đầu dầm và cột).
            if (startTarget is not null && Blocked(beam, beam.Start, startTarget, beams)) startTarget = null;
            if (endTarget is not null && Blocked(beam, beam.End, endTarget, beams)) endTarget = null;
            var start = startTarget is null ? beam.Start : new XYZ(startTarget.X, startTarget.Y, beam.Start.Z);
            var end = endTarget is null ? beam.End : new XYZ(endTarget.X, endTarget.Y, beam.End.Z);

            if (!start.IsAlmostEqualTo(beam.Start) || !end.IsAlmostEqualTo(beam.End))
                extendedBeamCount++;

            result.Add(new RawBeam
            {
                Start = start,
                End = end,
                WidthFeet = beam.WidthFeet,
                Segments = beam.Segments,
                TagName = beam.TagName,
                TagBMm = beam.TagBMm,
                TagHMm = beam.TagHMm,
                IsCrossLayerPair = beam.IsCrossLayerPair,
                CrossLayerNote = beam.CrossLayerNote,
            });
        }

        return new BeamExtensionResult(result, extendedBeamCount);
    }

    private static List<ColumnTarget> CollectColumns(Document doc, double elevation)
    {
        var toleranceFeet = UnitUtils.ConvertToInternalUnits(PositionToleranceMm, UnitTypeId.Millimeters);
        var result = new List<ColumnTarget>();
        var columns = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.StructuralType == StructuralType.Column);

        foreach (var column in columns)
        {
            BoundingBoxXYZ? box;
            try
            {
                box = column.get_BoundingBox(null);
            }
            catch (Exception)
            {
                box = null;
            }

            if (box is null || elevation < box.Min.Z - toleranceFeet || elevation > box.Max.Z + toleranceFeet)
                continue;

            var solids = new List<Solid>();
            var geometry = column.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geometry is not null)
                CollectSolids(geometry, solids);
            if (solids.Count > 0)
                result.Add(new ColumnTarget(box, solids));
        }

        return result;
    }

    private static XYZ? FindTarget(XYZ endpoint, XYZ? direction, List<ColumnTarget> columns, double maxDistanceFeet)
    {
        if (direction is null)
            return null;

        var toleranceFeet = UnitUtils.ConvertToInternalUnits(PositionToleranceMm, UnitTypeId.Millimeters);
        var bestEntry = double.MaxValue;

        foreach (var column in columns)
        {
            if (!TryGetRayEntry(endpoint, direction, column.Box, out var boxEntry) || boxEntry > maxDistanceFeet)
                continue;

            var entry = FindSolidEntry(endpoint, direction, column.Solids, maxDistanceFeet);
            if (entry is null)
                continue;
            if (entry <= toleranceFeet)
                return endpoint;
            if (entry >= bestEntry)
                continue;

            bestEntry = entry.Value;
        }

        return bestEntry < double.MaxValue
            ? new XYZ(endpoint.X + direction.X * bestEntry, endpoint.Y + direction.Y * bestEntry, endpoint.Z)
            : null;
    }

    /// <summary>Đoạn kéo dài [from → to] có đè lên một dầm khác cùng trục hay không.</summary>
    private static bool Blocked(RawBeam self, XYZ from, XYZ to, List<RawBeam> beams)
    {
        var tolerance = UnitUtils.ConvertToInternalUnits(PositionToleranceMm, UnitTypeId.Millimeters);
        var dir = DirectionFrom(from, to);
        if (dir is null) return false;
        var perp = new XYZ(-dir.Y, dir.X, 0);
        var length = new XYZ(to.X - from.X, to.Y - from.Y, 0).GetLength();
        foreach (var other in beams)
        {
            if (ReferenceEquals(other, self)) continue;
            var a = new XYZ(other.Start.X - from.X, other.Start.Y - from.Y, 0);
            var b = new XYZ(other.End.X - from.X, other.End.Y - from.Y, 0);
            if (Math.Abs(a.DotProduct(perp)) > self.WidthFeet / 2 || Math.Abs(b.DotProduct(perp)) > self.WidthFeet / 2) continue;
            var lo = Math.Min(a.DotProduct(dir), b.DotProduct(dir));
            var hi = Math.Max(a.DotProduct(dir), b.DotProduct(dir));
            if (hi > tolerance && lo < length - tolerance) return true;
        }
        return false;
    }

    private static XYZ? DirectionFrom(XYZ from, XYZ to)
    {
        var vector = new XYZ(to.X - from.X, to.Y - from.Y, 0);
        return vector.GetLength() > 1e-9 ? vector.Normalize() : null;
    }

    private static bool TryGetRayEntry(XYZ origin, XYZ direction, BoundingBoxXYZ box, out double entry)
    {
        var min = double.NegativeInfinity;
        var max = double.PositiveInfinity;

        if (!UpdateRayInterval(origin.X, direction.X, box.Min.X, box.Max.X, ref min, ref max) ||
            !UpdateRayInterval(origin.Y, direction.Y, box.Min.Y, box.Max.Y, ref min, ref max) ||
            max < 0)
        {
            entry = 0;
            return false;
        }

        entry = Math.Max(0, min);
        return entry <= max;
    }

    private static double? FindSolidEntry(XYZ endpoint, XYZ direction, List<Solid> solids, double maxDistanceFeet)
    {
        var sampleOffsetFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
        var best = double.MaxValue;

        foreach (var zOffset in new[] { 0.0, -sampleOffsetFeet, sampleOffsetFeet })
        {
            var start = new XYZ(endpoint.X, endpoint.Y, endpoint.Z + zOffset);
            var end = start + direction * maxDistanceFeet;
            var ray = Line.CreateBound(start, end);

            foreach (var solid in solids)
            {
                try
                {
                    var options = new SolidCurveIntersectionOptions
                    {
                        ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
                    };
                    var intersection = solid.IntersectWithCurve(ray, options);
                    for (var i = 0; i < intersection.SegmentCount; i++)
                    {
                        var segment = intersection.GetCurveSegment(i);
                        for (var endpointIndex = 0; endpointIndex < 2; endpointIndex++)
                        {
                            var point = segment.GetEndPoint(endpointIndex);
                            var distance = (point.X - start.X) * direction.X +
                                           (point.Y - start.Y) * direction.Y;
                            if (distance >= 0 && distance < best)
                                best = distance;
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                }
            }
        }

        return best < double.MaxValue ? best : null;
    }

    private static bool UpdateRayInterval(double origin, double direction, double boxMin, double boxMax, ref double min, ref double max)
    {
        if (Math.Abs(direction) < 1e-9)
            return origin >= boxMin && origin <= boxMax;

        var first = (boxMin - origin) / direction;
        var second = (boxMax - origin) / direction;
        if (first > second)
            (first, second) = (second, first);

        min = Math.Max(min, first);
        max = Math.Min(max, second);
        return min <= max;
    }

    private static void CollectSolids(GeometryElement geometry, List<Solid> solids)
    {
        foreach (var geometryObject in geometry)
        {
            switch (geometryObject)
            {
                case Solid solid when solid.Volume > 1e-9:
                    solids.Add(solid);
                    break;
                case GeometryInstance instance:
                    CollectSolids(instance.GetInstanceGeometry(), solids);
                    break;
            }
        }
    }

    private sealed record ColumnTarget(BoundingBoxXYZ Box, List<Solid> Solids);
}

internal sealed record BeamExtensionResult(List<RawBeam> Beams, int ExtendedBeamCount);
