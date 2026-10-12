using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Tiện ích dùng chung cho Gộp sàn/Chia sàn — đọc biên mặt bằng thật của 1 Floor qua Solid.Faces (mọi lớp cấu tạo dùng chung 1 biên mặt bằng), dựng solid tạm chỉ để chạy Boolean, rồi đọc lại biên từ solid kết quả.</summary>
internal static class FloorSolidHelper
{
    private const double HelperExtrudeFeet = 1.0;

    /// <summary>Đọc CurveLoop (outer+hole) từ mặt phẳng nằm ngang lớn nhất (Solid có thể tích lớn nhất) trong hình học thật của Floor, kèm cao độ Z của mặt đó.</summary>
    public static (List<CurveLoop> Loops, double TopZ)? GetTopLoops(Floor floor)
    {
        var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
        GeometryElement? geom;
        try
        {
            geom = floor.get_Geometry(options);
        }
        catch (Exception)
        {
            geom = null;
        }

        if (geom is null)
            return null;

        var solid = FindLargestSolid(geom);
        if (solid is null)
            return null;

        var topFace = FindTopFace(solid);
        if (topFace is null)
            return null;

        try
        {
            return (topFace.GetEdgesAsCurveLoops().ToList(), topFace.Origin.Z);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Solid? FindLargestSolid(GeometryElement geom)
    {
        Solid? best = null;
        foreach (var obj in geom)
        {
            var candidate = obj switch
            {
                Solid s => s,
                GeometryInstance gi => FindLargestSolid(gi.GetInstanceGeometry()),
                _ => null,
            };

            if (candidate is not null && candidate.Volume > 1e-9 && (best is null || candidate.Volume > best.Volume))
                best = candidate;
        }

        return best;
    }

    private static PlanarFace? FindTopFace(Solid solid)
    {
        PlanarFace? best = null;
        foreach (Face face in solid.Faces)
        {
            if (face is not PlanarFace pf || pf.FaceNormal.Z < 0.9)
                continue;

            if (best is null || pf.Origin.Z > best.Origin.Z)
                best = pf;
        }

        return best;
    }

    /// <summary>Dựng solid mỏng chỉ dùng để chạy Boolean (không phải hình khối sàn thật) từ 1 profile, tại đúng cao độ Z của profile đó.</summary>
    public static Solid? BuildHelperSolid(List<CurveLoop> loops)
    {
        try
        {
            return GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, HelperExtrudeFeet);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Đọc lại toàn bộ mặt hướng lên (Normal.Z &gt; 0.9) của 1 solid kết quả Boolean — gộp hết loop của MỌI mặt (có thể tách rời nếu không liền nhau) thành 1 profile cho Floor.Create.</summary>
    public static List<CurveLoop> ExtractTopLoops(Solid solid)
    {
        var loops = new List<CurveLoop>();
        foreach (Face face in solid.Faces)
        {
            if (face is not PlanarFace pf || pf.FaceNormal.Z < 0.9)
                continue;

            try
            {
                loops.AddRange(pf.GetEdgesAsCurveLoops());
            }
            catch (Exception)
            {
                // 1 mat loi khong duoc lam hong ca ket qua - bo qua rieng mat nay.
            }
        }

        return loops;
    }
}
