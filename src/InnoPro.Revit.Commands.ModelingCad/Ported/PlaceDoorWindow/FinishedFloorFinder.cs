using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Tìm chênh lệch giữa CAO ĐỘ HOÀN THIỆN SÀN thật (mặt trên cùng của Floor) và Level — dùng
/// để quy đổi "cao độ tính từ mặt sàn hoàn thiện" (người dùng nhập) sang giá trị tham số Sill Height
/// của Revit (vốn tính từ Level, KHÔNG phải từ mặt sàn hoàn thiện — 2 mốc này lệch nhau đúng bằng
/// "Height Offset From Level" của Floor tại tầng đó).</summary>
internal static class FinishedFloorFinder
{
    public static List<Floor> GetFloorsOnLevel(Document doc, Level level)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .Where(f => f.LevelId == level.Id)
            .ToList();
    }

    /// <summary>Trả về offset (feet) từ Level tới mặt trên Floor LỚN NHẤT (theo diện tích mặt bằng
    /// Bounding Box) trong <paramref name="floorsOnLevel"/> — đại diện cho sàn chính của tầng, dùng khi
    /// KHÔNG có vị trí cụ thể để tra (cửa sổ/cửa lùa, không có khái niệm "phía trước"). 0 nếu danh sách
    /// rỗng, kèm cờ <paramref name="found"/> = false để báo cho người dùng biết giả định này.</summary>
    public static double GetOffsetFeet(List<Floor> floorsOnLevel, out bool found)
    {
        if (floorsOnLevel.Count == 0)
        {
            found = false;
            return 0.0;
        }

        var mainFloor = floorsOnLevel.OrderByDescending(PlanAreaOf).First();
        found = true;
        return mainFloor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
    }

    /// <summary>Trả về offset (feet) từ Level tới mặt trên Floor CHỨA <paramref name="point"/> (chiếu
    /// vuông góc lên mặt trên từng Floor, kiểm tra có nằm trong biên thật của Floor đó không — đúng
    /// Floor tầng có thể có NHIỀU cao độ hoàn thiện khác nhau, vd sàn WC thấp hơn sàn phòng, không thể
    /// dùng chung 1 offset cho cả tầng). Dự phòng bằng <see cref="GetOffsetFeet"/> (Floor lớn nhất của
    /// tầng) nếu không có Floor nào chứa đúng điểm này (vd điểm nằm ngoài mọi biên Floor đã vẽ).</summary>
    public static double GetOffsetFeetAtPoint(List<Floor> floorsOnLevel, XYZ point, out bool found)
    {
        foreach (var floor in floorsOnLevel)
        {
            var topFace = GetTopFace(floor);
            if (topFace is null)
                continue;

            if (topFace.Project(point) is null)
                continue;

            found = true;
            return floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
        }

        return GetOffsetFeet(floorsOnLevel, out found);
    }

    private static double PlanAreaOf(Floor floor)
    {
        var box = floor.get_BoundingBox(null);
        if (box is null)
            return 0.0;

        return (box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y);
    }

    /// <summary>Mặt PHẲNG NẰM NGANG hướng LÊN cao nhất (FaceNormal.Z gần 1) trong hình học Solid của
    /// Floor — đúng mặt trên cùng thật của sàn, dùng để test điểm có nằm trong biên Floor hay không qua
    /// PlanarFace.Project (trả về null nếu điểm chiếu ra ngoài biên đã trim của mặt).</summary>
    private static PlanarFace? GetTopFace(Floor floor)
    {
        var geometryElement = floor.get_Geometry(new Options());
        if (geometryElement is null)
            return null;

        PlanarFace? best = null;
        foreach (var geomObj in geometryElement)
        {
            if (geomObj is not Solid solid || solid.Faces.IsEmpty)
                continue;

            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar || planar.FaceNormal.Z < 0.9)
                    continue;

                if (best is null || planar.Origin.Z > best.Origin.Z)
                    best = planar;
            }
        }

        return best;
    }
}
