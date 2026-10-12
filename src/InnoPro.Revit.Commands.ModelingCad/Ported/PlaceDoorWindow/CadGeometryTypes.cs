using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>1 đoạn thẳng thô đọc được từ hình học CAD, đã làm phẳng về Z=0.</summary>
internal sealed class RawSegment
{
    public required XYZ P0 { get; init; }
    public required XYZ P1 { get; init; }
    public required XYZ Direction { get; init; }
    public required double Length { get; init; }
}

/// <summary>1 cung tròn thô đọc được từ hình học CAD, đã làm phẳng về Z=0.</summary>
internal sealed class RawArc
{
    public required XYZ Center { get; init; }
    public required double Radius { get; init; }
    public required XYZ P0 { get; init; }
    public required XYZ P1 { get; init; }
    public required XYZ Mid { get; init; }
}

/// <summary>1 layer CAD, kèm số liệu để hiển thị trong danh sách chọn.</summary>
internal sealed record CadLayerEntry(string Name, int LineCount, int ArcCount);

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadScanResult
{
    public required IReadOnlyDictionary<string, List<RawSegment>> SegmentsByLayer { get; init; }
    public required IReadOnlyDictionary<string, List<RawArc>> ArcsByLayer { get; init; }

    public List<CadLayerEntry> GetLayerSummaries()
    {
        var names = new HashSet<string>(SegmentsByLayer.Keys);
        names.UnionWith(ArcsByLayer.Keys);

        return names
            .Select(name => new CadLayerEntry(
                name,
                SegmentsByLayer.TryGetValue(name, out var lines) ? lines.Count : 0,
                ArcsByLayer.TryGetValue(name, out var arcs) ? arcs.Count : 0))
            .OrderByDescending(e => e.LineCount + e.ArcCount)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>1 vị trí cửa đi/cửa sổ nhận diện được từ layer CAD — xem OpeningDetector. Center là điểm
/// GIỮA lỗ mở (cách điểm neo gần — tâm cung/bản lề, hoặc trung điểm nét ngắn hơn — đúng nửa LengthFeet
/// dọc theo Direction) — ĐÚNG CHUẨN family Door/Window mặc định của Revit: mặt phẳng tham chiếu
/// "Center" của family luôn nằm giữa bề rộng lỗ mở, `NewFamilyInstance(point,...)` đặt family sao cho
/// mặt phẳng đó trùng với điểm truyền vào. Center dùng CHUNG cho cả việc đặt Family Instance (sau khi
/// chiếu lên trục tường ở WallHostFinder) và tìm Text Note tên cửa gần nhất (TextTagReader — nhãn tên
/// cửa trên bản vẽ thường đặt gần giữa/tổng thể ký hiệu cửa). LengthFeet là bề rộng lỗ mở đo được trực
/// tiếp từ CAD (bán kính cung xoay, hoặc khoảng cách cặp đường thẳng song song xa nhất — dùng lại đúng
/// thuật toán BuildDoorLines của WallFromCad).</summary>
internal sealed class DetectedOpening
{
    public required XYZ Center { get; init; }
    public required XYZ Direction { get; init; }
    public required double LengthFeet { get; init; }

    /// <summary>Hướng (vuông góc Direction) chứa phần cong của cung xoay trong CAD — phía cửa MỞ RA.
    /// Null nếu cụm không có cung (cửa sổ, cửa lùa...) — không có khái niệm hướng mở để khớp, xem yêu
    /// cầu Command.cs/DoorWindowPlacer.</summary>
    public XYZ? SwingDirection { get; init; }
}

/// <summary>Tường thật trong model được chọn làm host cho 1 DetectedOpening — xem WallHostFinder.</summary>
internal sealed class HostWallMatch
{
    public required Wall Wall { get; init; }
    public required XYZ InsertionPoint { get; init; }
}

/// <summary>1 lựa chọn Type trong ComboBox — bọc FamilySymbol kèm chuỗi hiển thị "Family : Type" vì
/// ComboBox DisplayMemberPath không ghép được 2 property gốc.</summary>
internal sealed record SymbolChoice(FamilySymbol Symbol, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>1 vị trí cửa đi/cửa sổ nhận diện được, ĐÃ khớp tường host + tên (nếu tìm thấy Text Note
/// đủ gần — xem TextTagReader). Đây là đơn vị nhỏ nhất khi đặt Family Instance thật (DoorWindowPlacer)
/// — nhiều OpeningInstance CÙNG TagName được gộp vào 1 DoorNameGroup để người dùng chỉ cần chọn
/// Type/nhập cao độ 1 lần cho cả nhóm.</summary>
internal sealed class OpeningInstance
{
    public required DetectedOpening Opening { get; init; }
    public required double WidthMm { get; init; }
    public HostWallMatch? Host { get; init; }

    /// <summary>Nội dung Text Note gần nhất (đã explode thành Text Note thật trong Revit — KHÔNG đọc
    /// được text CAD thô, xem Command.cs) trong bán kính người dùng tự nhập ở UI (xem
    /// PlaceDoorWindowWindow, TextTagReader) — null nếu không tìm thấy Text Note nào đủ gần/hợp lệ.</summary>
    public string? TagName { get; init; }
}

/// <summary>1 dòng trong bảng UI — gộp MỌI OpeningInstance cùng TagName (hoặc 1 instance đơn lẻ nếu
/// không đọc được tên) — người dùng chỉ cần xác nhận/chọn Type + nhập cao độ 1 LẦN cho cả nhóm, áp
/// dụng cho mọi vị trí cùng tên. Implement INotifyPropertyChanged để cửa sổ WPF cập nhật ngay khi tool
/// tự động chọn sẵn Type khớp tên (xem DoorNameGrouper). Tất cả class ở đây khai internal dù được cửa
/// sổ WPF (public partial class) dùng làm kiểu tham số/thuộc tính — lý do xem ghi chú đầu
/// WallFromCad\WallGeometryTypes.cs (CS0051/CS0053).</summary>
internal sealed class DoorNameGroup : INotifyPropertyChanged
{
    private SymbolChoice? _selectedChoice;
    private double _sillHeightMm;
    private Family? _selectedFamily;
    private double _customWidthMm;
    private double _customHeightMm;

    public required string Name { get; init; }
    public required List<OpeningInstance> Instances { get; init; }
    public List<SymbolChoice> TypeChoices { get; init; } = new();

    /// <summary>True nếu tool tự tìm thấy đúng 1 Type (Door/Window) có tên KHỚP Name (không phân biệt
    /// hoa/thường, đã trim, hoặc chứa Name như 1 token riêng — xem DoorNameGrouper.FindSingleContainingMatch)
    /// và đã tự chọn sẵn — người dùng vẫn có thể đổi tay qua ComboBox nếu sai.</summary>
    public bool AutoMatched { get; init; }

    /// <summary>True CHỈ cho nhóm hoàn toàn KHÔNG đọc được tên (không có Text Note nào đủ gần/hợp lệ) —
    /// phân biệt với nhóm CÓ tên nhưng chưa khớp Type ("CHƯA CÓ TYPE"). Dùng để tô màu ĐỎ (thay vì xanh
    /// dương) và xếp CUỐI danh sách — xem PlaceDoorWindowWindow.xaml, DoorNameGrouper.</summary>
    public bool IsUnnamed { get; init; }

    public SymbolChoice? SelectedChoice
    {
        get => _selectedChoice;
        set => SetField(ref _selectedChoice, value);
    }

    /// <summary>Toàn bộ Family (Door/Window) hiện có trong model — dùng cho ô chọn Family khi nhóm này
    /// CHƯA CÓ Type khớp (AutoMatched false, xem SelectedFamily/CustomWidthMm/CustomHeightMm). Không
    /// dùng cho nhóm ĐÃ có Type khớp — nhóm đó vẫn chọn thẳng Type qua TypeChoices như cũ.</summary>
    public List<Family> FamilyChoices { get; init; } = new();

    /// <summary>Family người dùng chọn để tool tự Duplicate 1 Type MỚI (xem
    /// DoorWindowPlacer.DuplicateSizedSymbol) khi nhóm này chưa có Type khớp sẵn — chỉ có ý nghĩa khi
    /// SelectedChoice vẫn null lúc đóng cửa sổ (DoorWindowPlacer tự tạo Type rồi gán vào SelectedChoice
    /// ngay trước khi đặt, xem doc-comment lớp DoorWindowPlacer).</summary>
    public Family? SelectedFamily
    {
        get => _selectedFamily;
        set => SetField(ref _selectedFamily, value);
    }

    /// <summary>Chiều rộng (mm) người dùng nhập cho Type MỚI sẽ Duplicate từ SelectedFamily — mặc định
    /// gợi ý sẵn bằng bề rộng đo được từ CAD (xem DoorNameGrouper), người dùng có thể sửa lại.</summary>
    public double CustomWidthMm
    {
        get => _customWidthMm;
        set => SetField(ref _customWidthMm, value);
    }

    /// <summary>Chiều cao (mm) người dùng nhập cho Type MỚI sẽ Duplicate từ SelectedFamily — CAD không
    /// đo được chiều cao (chỉ có bề rộng lỗ mở), người dùng phải tự nhập, không có gợi ý mặc định.</summary>
    public double CustomHeightMm
    {
        get => _customHeightMm;
        set => SetField(ref _customHeightMm, value);
    }

    /// <summary>Cao độ người dùng nhập cho nhóm này — ĐƠN VỊ mm, nhưng MỐC tính (từ Level hay từ mặt
    /// sàn hoàn thiện) phụ thuộc lựa chọn SillHeightMode chung của cả cửa sổ (xem
    /// PlaceDoorWindowWindow/DoorWindowPlacer), không cố định vào riêng property này.</summary>
    public double SillHeightMm
    {
        get => _sillHeightMm;
        set => SetField(ref _sillHeightMm, value);
    }

    public FamilySymbol? SelectedType => SelectedChoice?.Symbol;
    public int Count => Instances.Count;
    public string CountLabel => $"{Instances.Count} vị trí";
    public string NameLabel => AutoMatched ? Name : $"{Name}  (CHƯA CÓ TYPE)";
    public int NoHostCount => Instances.Count(i => i.Host is null);

    /// <summary>True nếu nhóm này đã đủ thông tin để đặt cửa — HOẶC đã có Type khớp/chọn sẵn, HOẶC đã
    /// chọn Family + nhập Width/Height hợp lệ (> 0) để tool tự Duplicate Type mới. Dùng để validate
    /// trước khi đóng cửa sổ (xem PlaceDoorWindowWindow.OnPlaceClick) — check cũ chỉ nhìn SelectedType
    /// không còn đủ vì nhóm dùng Family+kích thước chưa có SelectedChoice cho tới lúc đặt thật.</summary>
    public bool IsResolved =>
        SelectedChoice is not null || (SelectedFamily is not null && CustomWidthMm > 0 && CustomHeightMm > 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Cách quy đổi cao độ người dùng nhập (DoorNameGroup.SillHeightMm) sang tham số Sill Height
/// của Revit (vốn luôn tính từ Level) — áp dụng CHUNG cho toàn bộ cửa trong 1 lần đặt, chọn ở
/// PlaceDoorWindowWindow, xem DoorWindowPlacer.</summary>
internal enum SillHeightMode
{
    /// <summary>Cao độ nhập vào coi như đã tính từ Level — dùng thẳng, không cộng thêm gì.</summary>
    FromLevel,

    /// <summary>Cao độ nhập vào tính từ mặt sàn HOÀN THIỆN — cộng thêm offset Sàn→Level
    /// (FinishedFloorFinder). Với cửa đi có cung xoay (biết hướng mở — SwingDirection), lấy đúng Floor
    /// nằm NGAY PHÍA TRƯỚC cửa (phía có cung, theo chiều mở) thay vì Floor lớn nhất của cả tầng — vì 1
    /// tầng có thể có nhiều cao độ sàn hoàn thiện khác nhau (vd sàn WC thấp hơn sàn phòng).</summary>
    FromFinishedFloor
}

/// <summary>Kết quả đặt 1 OpeningInstance — xem DoorWindowPlacer.</summary>
internal enum PlacementOutcome
{
    Created,
    SkippedNoType,
    SkippedNoHostWall,
    Failed
}

/// <summary>CenteringNote: chẩn đoán bước tự căn giữa (DoorWindowPlacer.CenterOnWall) cho 1
/// PlacementOutcome.Created — có đọc được reference "Center (Left/Right)" thật hay phải rơi vào dự
/// phòng Bounding Box, và đã dịch chuyển bao nhiêu mm — để biết CHÍNH XÁC nguyên nhân nếu vị trí cuối
/// vẫn lệch so với mong muốn, thay vì phải đoán qua ảnh chụp màn hình.
///
/// GridSnapNote: chẩn đoán bước làm tròn vị trí theo Grid (DoorWindowPlacer.SnapToGrid) — Grid nào được
/// dùng, khoảng cách gốc (thường lẻ do sai số CAD) và đã làm tròn/dịch chuyển bao nhiêu, hoặc lý do bỏ
/// qua (tường xiên, không tìm được Grid vuông góc).</summary>
internal sealed record PlacementResult(
    DoorNameGroup Group, OpeningInstance Instance, PlacementOutcome Outcome,
    string? ErrorMessage = null, string? CenteringNote = null, string? GridSnapNote = null);
