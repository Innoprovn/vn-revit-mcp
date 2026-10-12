using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceWall;

/// <summary>Chỉ giữ tim tường có Wall hoặc Structural Foundation đỡ bên dưới.</summary>
internal static class WallSupportFilter
{
    private const int SampleCount = 7;
    private const double RequiredSupportFraction = 0.5;
    private static readonly double ProbeTopPaddingFeet =
        UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);

    public static List<RawWall> Filter(
        Document doc, IReadOnlyList<RawWall> walls, double baseElevation, double searchDepthFeet)
    {
        if (walls.Count == 0)
            return new List<RawWall>();

        var categoryFilter = new LogicalOrFilter(
            new ElementCategoryFilter(BuiltInCategory.OST_Walls),
            new ElementCategoryFilter(BuiltInCategory.OST_StructuralFoundation));

        var supportSolids = new List<Solid>();
        foreach (var element in new FilteredElementCollector(doc)
                     .WherePasses(categoryFilter)
                     .WhereElementIsNotElementType())
        {
            var box = element.get_BoundingBox(null);
            if (box is null || box.Max.Z < baseElevation - searchDepthFeet - ProbeTopPaddingFeet ||
                box.Min.Z > baseElevation + ProbeTopPaddingFeet)
                continue;

            var geometry = element.get_Geometry(new Options
            {
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false,
            });
            if (geometry is not null)
                CollectSolids(geometry, supportSolids);
        }

        return walls.Where(wall => IsSupported(wall, baseElevation, searchDepthFeet, supportSolids)).ToList();
    }

    private static bool IsSupported(
        RawWall wall, double baseElevation, double searchDepthFeet, IReadOnlyList<Solid> solids)
    {
        var requiredCount = (int)Math.Ceiling(SampleCount * RequiredSupportFraction);
        var supportedCount = 0;

        for (var index = 0; index < SampleCount; index++)
        {
            // Tránh đúng đầu mút, nơi giao tường/cột dễ làm kết quả dao động.
            var parameter = (index + 1.0) / (SampleCount + 1.0);
            var x = wall.Start.X + (wall.End.X - wall.Start.X) * parameter;
            var y = wall.Start.Y + (wall.End.Y - wall.Start.Y) * parameter;
            var ray = Line.CreateBound(
                new XYZ(x, y, baseElevation + ProbeTopPaddingFeet),
                new XYZ(x, y, baseElevation - searchDepthFeet));

            if (solids.Any(solid => Intersects(solid, ray)))
                supportedCount++;

            if (supportedCount >= requiredCount)
                return true;

            if (supportedCount + SampleCount - index - 1 < requiredCount)
                return false;
        }

        return false;
    }

    private static bool Intersects(Solid solid, Curve ray)
    {
        try
        {
            var result = solid.IntersectWithCurve(ray, new SolidCurveIntersectionOptions
            {
                ResultType = SolidCurveIntersectionMode.CurveSegmentsInside,
            });
            return result.SegmentCount > 0;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return false;
        }
    }

    private static void CollectSolids(GeometryElement geometry, List<Solid> solids)
    {
        foreach (var obj in geometry)
        {
            switch (obj)
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
}
