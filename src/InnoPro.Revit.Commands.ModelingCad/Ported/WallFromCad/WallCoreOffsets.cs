using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFromCad;

/// <summary>Tính offset (feet) từ Wall Centerline tới mặt phẳng tham chiếu tương ứng với 1 giá trị
/// Location Line, đo theo hướng <c>Wall.Orientation</c> (dương = về phía "mặt ngoài" theo quy ước mặc
/// định của Revit cho tường vừa tạo — flip=false).
///
/// TẠI SAO CẦN CLASS NÀY (bẫy hay gặp của Revit API): <c>Wall.Create</c> luôn đặt curve truyền vào là
/// ĐƯỜNG TIM (Wall Centerline), bất kể sau này Location Line được set là gì. Nếu đổi tham số
/// BuiltInParameter.WALL_KEY_REF_PARAM SAU khi tạo tường, Revit sẽ TỰ DỊCH CHUYỂN tường sao cho mặt
/// phẳng tham chiếu MỚI trùng đúng vị trí đường tim CŨ — tức là dịch cả tường đi 1 khoảng bằng offset
/// này. Vì Wall Type luôn được chọn khớp CHÍNH XÁC bề rộng đo từ CAD (xem ThicknessBucket), nếu tạo
/// tường bằng đường tim CAD thì hình dạng đã đúng ngay từ đầu — nên sau khi đổi Location Line,
/// <see cref="WallBuilder"/> phải dịch tường ngược lại đúng bằng offset này để trả tường về đúng vị trí
/// ban đầu (2 mặt tường khớp đúng 2 nét CAD), chỉ có tham số Location Line là đổi theo ý người dùng.
///
/// CORE = FACE (CỐ Ý, theo đúng yêu cầu người dùng): bản trước đọc offset lớp Core thật qua
/// <c>WallType.GetCompoundStructure()</c>/<c>IsCoreLayer</c> cho 3 lựa chọn Core*, khiến "Core
/// Exterior/Interior" đặt mặt phẳng tham chiếu nằm LỌT VÀO GIỮA tường (ranh giới lớp lõi thật, không
/// trùng nét CAD nào) thay vì trùng 1 trong 2 nét CAD — đúng lỗi người dùng báo ("mặt Core lửng lơ giữa
/// tường, không chạm nét nào"). Vì bất biến cốt lõi của tool là TOÀN BỘ bề rộng tường (Width) khớp
/// đúng khoảng cách 2 nét CAD (không quan tâm cấu tạo lớp bên trong), 3 lựa chọn Core* ở đây được xử lý
/// GIỐNG HỆT 3 lựa chọn tương ứng Wall Centerline/Finish Face* — CoreExterior = FinishFaceExterior,
/// CoreInterior = FinishFaceInterior, CoreCenterline = WallCenterline. Nhờ vậy chọn Core Exterior/
/// Interior luôn cho đúng: 1 mặt tường (mặt A hoặc mặt B) nằm khớp đúng 1 nét CAD, bề rộng còn lại
/// hướng về nét CAD còn lại — đúng yêu cầu, và không còn phụ thuộc/rủi ro đọc sai Compound Structure.</summary>
internal static class WallCoreOffsets
{
    public static double GetReferenceOffsetFeet(WallType wallType, WallLocationLine locationLine)
    {
        var halfWidth = wallType.Width / 2.0;

        return locationLine switch
        {
            WallLocationLine.WallCenterline => 0.0,
            WallLocationLine.CoreCenterline => 0.0,
            WallLocationLine.FinishFaceExterior => halfWidth,
            WallLocationLine.FinishFaceInterior => -halfWidth,
            WallLocationLine.CoreExterior => halfWidth,
            WallLocationLine.CoreInterior => -halfWidth,
            _ => 0.0
        };
    }
}
