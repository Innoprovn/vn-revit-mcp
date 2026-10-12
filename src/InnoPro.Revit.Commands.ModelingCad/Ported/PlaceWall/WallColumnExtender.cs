using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceWall;

internal static class WallColumnExtender
{
    private const double PositionToleranceMm = 10.0;

    public static WallExtensionResult Extend(Document doc, List<RawWall> walls, double elevation, double maxDistanceFeet)
    {
        var columns = CollectColumns(doc, elevation);
        var result = new List<RawWall>(walls.Count);
        var extendedWallCount = 0;

        foreach (var wall in walls)
        {
            var startDirection = DirectionFrom(wall.End, wall.Start);
            var endDirection = DirectionFrom(wall.Start, wall.End);
            var startAtElevation = new XYZ(wall.Start.X, wall.Start.Y, elevation);
            var endAtElevation = new XYZ(wall.End.X, wall.End.Y, elevation);
            var startTarget = FindTarget(startAtElevation, startDirection, columns, maxDistanceFeet);
            var endTarget = FindTarget(endAtElevation, endDirection, columns, maxDistanceFeet);
            var start = startTarget is null ? wall.Start : new XYZ(startTarget.X, startTarget.Y, wall.Start.Z);
            var end = endTarget is null ? wall.End : new XYZ(endTarget.X, endTarget.Y, wall.End.Z);

            if (!start.IsAlmostEqualTo(wall.Start) || !end.IsAlmostEqualTo(wall.End))
                extendedWallCount++;

            result.Add(new RawWall
            {
                Start = start,
                End = end,
                WidthFeet = wall.WidthFeet,
                Segments = wall.Segments,
                TagName = wall.TagName,
                TagBMm = wall.TagBMm,
                TagHMm = wall.TagHMm,
                IsCrossLayerPair = wall.IsCrossLayerPair,
                CrossLayerNote = wall.CrossLayerNote,
            });
        }

        return new WallExtensionResult(result, extendedWallCount);
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

internal sealed record WallExtensionResult(List<RawWall> Walls, int ExtendedWallCount);


