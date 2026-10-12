using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Tìm tên cửa gần 1 vị trí — đọc Text Note THẬT trong Revit (KHÔNG đọc được text CAD thô từ
/// ImportInstance, giới hạn đã biết của Revit API — xem doc-comment Command.cs). Người dùng phải tự
/// explode riêng layer chứa tên cửa thành Text Note thật trước khi chạy tool. Bán kính tìm không cố
/// định — người dùng tự nhập trong UI (xem PlaceDoorWindowWindow), vì mật độ chữ trên mỗi bản vẽ CAD
/// khác nhau.</summary>
internal static class TextTagReader
{
    /// <summary>Giá trị mặc định gợi ý sẵn trong ô nhập bán kính của UI — KHÔNG còn là giá trị cố định
    /// dùng để tìm kiếm, xem <see cref="FindNearestTagName"/>.</summary>
    internal const double DefaultSearchRadiusMm = 2000.0;

    /// <summary>Nhận diện chuỗi kiểu "2000x2700"/"2000 X 2700"/"2000×2700mm" — kích thước cửa (rộng x
    /// cao) mà người vẽ CAD thường đặt Text NGAY CẠNH tên cửa (vd "VK3" phía trên, "2000x2700" phía
    /// dưới, khoảng cách tới Center còn gần hơn cả tên thật) — phải loại các Text dạng này khỏi danh
    /// sách ứng viên tên cửa, nếu không tool sẽ nhận nhầm kích thước làm tên (xem bug thực tế: đọc được
    /// "2000x2700" thay vì "VK3").</summary>
    private static readonly Regex DimensionPattern =
        new(@"^\s*\d+\s*[xX×]\s*\d+\s*(mm)?\s*$", RegexOptions.Compiled);

    /// <summary>Lấy nội dung Text Note GẦN NHẤT trong bán kính <paramref name="searchRadiusFeet"/> (tính
    /// khoảng cách 2D — làm phẳng cả điểm tra cứu lẫn vị trí Text Note về Z=0) tới <paramref name="point"/>
    /// — null nếu không có Text Note nào đủ gần và hợp lệ.
    ///
    /// XÉT TOÀN BỘ Text Note trong bán kính làm ứng viên (không dừng lại ở Text gần nhất về khoảng cách
    /// thô), LOẠI trước các Text khớp <see cref="DimensionPattern"/> (kích thước, không phải tên — xem
    /// doc-comment trên), rồi mới chọn Text GẦN NHẤT trong số ứng viên còn lại — tránh trường hợp Text
    /// kích thước (thường đặt ngay dưới/cạnh tên, nên có thể gần Center hơn) bị nhận nhầm thành tên cửa.
    ///
    /// Tính khoảng cách tới TÂM BOUNDING BOX thật của Text Note (get_BoundingBox — bao trọn hình dạng
    /// chữ THEO ĐÚNG GÓC XOAY hiện tại), KHÔNG dùng TextNote.Coord (điểm neo/anchor): Coord chỉ là 1
    /// GÓC cố định của khung chữ lúc CHƯA xoay (vd góc trên-trái nếu canh trái) — sau khi người dùng
    /// xoay Text Note, góc đó có thể lệch hẳn sang 1 phía so với hình dạng chữ nhìn thấy thực tế, làm
    /// khoảng cách tính từ Coord SAI lệch hẳn so với vị trí trực quan, dẫn tới bị loại nhầm dù chữ vẫn
    /// đang nằm ngay sát cửa. Bounding Box không bị ảnh hưởng bởi việc này vì luôn phản ánh đúng phạm vi
    /// hình học thật trong toạ độ thế giới, bất kể góc xoay/điểm neo nằm ở đâu.</summary>
    public static string? FindNearestTagName(
        XYZ point, IReadOnlyList<TextNote> textNotes, double searchRadiusFeet)
    {
        var flatPoint = new XYZ(point.X, point.Y, 0);
        string? best = null;
        var bestDistance = double.MaxValue;

        foreach (var note in textNotes)
        {
            var text = note.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            if (DimensionPattern.IsMatch(text))
                continue;

            var box = note.get_BoundingBox(null);
            var center = box is null
                ? note.Coord
                : new XYZ((box.Min.X + box.Max.X) / 2.0, (box.Min.Y + box.Max.Y) / 2.0, 0);
            var flatCenter = new XYZ(center.X, center.Y, 0);

            var distance = flatPoint.DistanceTo(flatCenter);
            if (distance > searchRadiusFeet || distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = text;
        }

        return best;
    }
}
