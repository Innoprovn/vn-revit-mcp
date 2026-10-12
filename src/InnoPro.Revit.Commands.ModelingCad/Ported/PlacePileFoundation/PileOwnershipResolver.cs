using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Xác định cọc thuộc đài nào bằng point-in-polygon trên đường bao CAD thật, fallback tâm gần nhất.</summary>
internal static class PileOwnershipResolver
{
    /// <summary>Gán OwningBoundary cho từng circle trong danh sách — point-in-polygon trước, fallback tâm-tâm gần nhất nếu không đài nào chứa.</summary>
    public static void AssignOwners(IEnumerable<RawCircle> circles, IReadOnlyList<BoundaryRow> boundaries)
    {
        foreach (var circle in circles)
            circle.OwningBoundary = FindOwner(circle.Center, boundaries);
    }

    /// <summary>Đài chứa điểm (point-in-polygon) — không đài nào chứa thì lấy đài có Center gần điểm nhất.</summary>
    private static BoundaryRow? FindOwner(XYZ point, IReadOnlyList<BoundaryRow> boundaries)
    {
        foreach (var row in boundaries)
        {
            if (IsPointInPolygon(point, row.Boundary.Points))
                return row;
        }

        return boundaries.OrderBy(row => row.Boundary.Center.DistanceTo(point)).FirstOrDefault();
    }

    /// <summary>Ray-casting chẵn-lẻ tiêu chuẩn trên đa giác khép kín (mặt phẳng XY).</summary>
    private static bool IsPointInPolygon(XYZ point, IReadOnlyList<XYZ> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (pi.Y > point.Y != pj.Y > point.Y &&
                point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }

        return inside;
    }
}
