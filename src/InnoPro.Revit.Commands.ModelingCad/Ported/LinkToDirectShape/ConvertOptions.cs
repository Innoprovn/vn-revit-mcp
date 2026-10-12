using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.LinkToDirectShape;

public enum MetadataScope
{
    /// <summary>Không tạo parameter nào, chỉ có hình khối (+ Comments/Mark nếu bật).</summary>
    None,

    /// <summary>Chỉ thông tin nhận dạng: link, Id, UniqueId, category, family, type, level, workset.</summary>
    Identity,

    /// <summary>Nhận dạng + toàn bộ parameter của element gốc.</summary>
    All
}

public enum ConvertMode
{
    /// <summary>Pick từng element bên trong link (nhiều link cùng lúc cũng được).</summary>
    PickElements,

    /// <summary>Duyệt toàn bộ element của 1 link instance theo category đã chọn.</summary>
    WholeLink
}

/// <summary>Lựa chọn user đặt trong cửa sổ trước khi chạy. Là snapshot thuần dữ liệu để phần
/// convert không phải biết gì về WPF.</summary>
public sealed class ConvertOptions
{
    public ConvertMode Mode { get; init; } = ConvertMode.PickElements;

    /// <summary>Chỉ dùng khi Mode == WholeLink.</summary>
    public RevitLinkInstance? Link { get; init; }

    /// <summary>Chỉ lấy element đang hiển thị trong view hiện tại (WholeLink).</summary>
    public bool ActiveViewOnly { get; init; }

    /// <summary>Category (theo Id trong link doc) được phép convert. Rỗng = tất cả.</summary>
    public HashSet<long> CategoryIds { get; init; } = new();

    /// <summary>Ghi Comments + Mark trỏ về link/element gốc.</summary>
    public bool WriteTracking { get; init; } = true;

    /// <summary>Mức metadata chép sang DirectShape (xem MetadataCopier).</summary>
    public MetadataScope Metadata { get; init; } = MetadataScope.All;

    /// <summary>Chép cả parameter của Type, không chỉ parameter của instance.</summary>
    public bool IncludeTypeParameters { get; init; } = true;

    /// <summary>Tiền tố tên parameter tạo trong file host, tránh đụng parameter thật của dự án.</summary>
    public string ParameterPrefix { get; init; } = "LNK_";

    /// <summary>Cách dựng hình khối — xem GeometryMode.</summary>
    public GeometryMode Geometry { get; init; } = GeometryMode.ExactSolid;
}

public enum GeometryMode
{
    /// <summary>Giữ nguyên solid BRep của link: mặt cong vẫn là mặt cong, lỗ/hốc còn nguyên. Đây là
    /// mức chính xác cao nhất Revit API cho phép khi đưa hình học sang document khác. Material của
    /// link không mang theo được (id material của link vô nghĩa ở file host) — tool chỉ gán được
    /// material ở mức element nếu category có ô material, và ghi tên material vào metadata.</summary>
    ExactSolid,

    /// <summary>Copy material sang host rồi dựng lại khối bằng TessellatedShapeBuilder để gán được
    /// material cho từng mặt. ĐÁNH ĐỔI: mặt cong bị chia thành mặt phẳng, và khối nào không khép kín
    /// sau khi chia sẽ rớt xuống Mesh (nhìn mỏng/rỗng, mất cảm giác khối đặc).</summary>
    MaterialTessellated
}
