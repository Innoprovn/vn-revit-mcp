using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Quy ước chữ quanh ký hiệu cửa: vùng tìm mặc định và nhận diện chữ kích thước.</summary>
internal static class TextTagReader
{
    /// <summary>Vùng tìm tên mặc định quanh lỗ cửa (mm); mật độ chữ mỗi bản vẽ khác nhau nên cho phép đổi.</summary>
    internal const double DefaultSearchRadiusMm = 2000.0;

    /// <summary>Nhận diện chuỗi kiểu "2000x2700"/"2000 X 2700"/"2000×2700mm" — kích thước cửa (rộng x
    /// cao) mà người vẽ CAD thường đặt Text NGAY CẠNH tên cửa (vd "VK3" phía trên, "2000x2700" phía
    /// dưới, khoảng cách tới Center còn gần hơn cả tên thật) — phải loại các Text dạng này khỏi danh
    /// sách ứng viên tên cửa, nếu không tool sẽ nhận nhầm kích thước làm tên (xem bug thực tế: đọc được
    /// "2000x2700" thay vì "VK3").</summary>
    private static readonly Regex DimensionPattern =
        new(@"^\s*\d+\s*[xX×]\s*\d+\s*(mm)?\s*$", RegexOptions.Compiled);

    public static bool IsDimension(string text) => DimensionPattern.IsMatch(text);

    /// <summary>Ký hiệu cửa: bắt đầu bằng chữ, không khoảng trắng, 2–10 ký tự (D1, Đ1, FF1, WF2a, CS-01).
    /// Loại cao độ "-20", số đếm, tên phòng "NGỦ 1", ghi chú dài.</summary>
    private static readonly Regex MarkPattern = new(@"^\p{L}[\p{L}\d\-._/]{1,9}$", RegexOptions.Compiled);

    public static bool IsMark(string text) => MarkPattern.IsMatch(text.Trim());
}
