using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.FloorFromCad;

/// <summary>1 vùng hatch đọc được từ CAD, đã làm phẳng về Z=0 — OuterLoop là biên ngoài (diện tích lớn
/// nhất trong các loop của mặt hatch), InnerLoops là các lỗ khoét bên trong (nếu có). Center là tâm
/// bounding box XY (đủ ổn định để tìm Text Note tên sàn gần nhất — không dùng centroid hình học vì có
/// thể lệch nhiều với biên cong/lõm bất thường).</summary>
internal sealed class RawHatchRegion
{
    public required CurveLoop OuterLoop { get; init; }
    public required List<CurveLoop> InnerLoops { get; init; }
    public required XYZ Center { get; init; }
    public required double AreaSqFeet { get; init; }
}

/// <summary>1 layer CAD, kèm số liệu để hiển thị trong danh sách chọn.</summary>
internal sealed record CadLayerEntry(string Name, int RegionCount);

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadFloorScanResult
{
    public required IReadOnlyDictionary<string, List<RawHatchRegion>> RegionsByLayer { get; init; }

    public List<CadLayerEntry> GetLayerSummaries() =>
        RegionsByLayer
            .Select(kv => new CadLayerEntry(kv.Key, kv.Value.Count))
            .OrderByDescending(e => e.RegionCount)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>Vẽ Sàn (Floor) hay Trần (Ceiling) — lựa chọn CHUNG cho cả 1 lượt chạy tool (chọn ở đầu cửa
/// sổ, áp dụng cho MỌI tên/vùng, không phải lựa chọn riêng từng nhóm) — quyết định dùng danh sách Type
/// nào (FloorType/CeilingType), gọi Floor.Create hay Ceiling.Create, và gán tham số Height Offset From
/// Level đúng loại (FLOOR_HEIGHTABOVELEVEL_PARAM/CEILING_HEIGHTABOVELEVEL_PARAM) — xem FloorBuilder.</summary>
internal enum SurfaceKind
{
    Floor,
    Ceiling
}

/// <summary>1 lựa chọn Type trong ComboBox — dùng <see cref="HostObjAttributes"/> (lớp cha chung của cả
/// FloorType lẫn CeilingType, đều có Compound Structure) thay vì FloorType cụ thể, để dùng chung được
/// cho cả 2 chế độ Sàn/Trần (xem SurfaceKind) mà không cần 2 bộ kiểu dữ liệu song song.</summary>
internal sealed record FloorTypeChoice(HostObjAttributes Type, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>1 dòng trong bảng UI — gộp MỌI RawHatchRegion cùng tên (đọc từ Text Note gần tâm mỗi vùng)
/// — người dùng chỉ cần xác nhận/chọn Type (hoặc Duplicate Type mới + bề dày) + nhập cao độ 1 LẦN cho cả
/// nhóm, áp dụng cho mọi vùng cùng tên. Implement INotifyPropertyChanged để cửa sổ WPF cập nhật ngay khi
/// tool tự động chọn sẵn Type khớp tên (xem FloorNameGrouper).</summary>
internal sealed class FloorNameGroup : INotifyPropertyChanged
{
    private FloorTypeChoice? _selectedChoice;
    private double _offsetMm;
    private double _edgeOffsetMm;
    private HostObjAttributes? _duplicateBaseType;
    private double _customThicknessMm;

    public required string Name { get; init; }
    public required List<RawHatchRegion> Regions { get; init; }
    public List<FloorTypeChoice> TypeChoices { get; init; } = new();

    /// <summary>True nếu tool tự tìm thấy đúng 1 Floor Type có tên KHỚP Name (không phân biệt hoa/thường,
    /// đã trim, hoặc chứa Name như 1 token riêng — xem FloorNameGrouper.FindSingleContainingMatch) và đã
    /// tự chọn sẵn — người dùng vẫn có thể đổi tay qua ComboBox nếu sai.</summary>
    public bool AutoMatched { get; init; }

    /// <summary>True CHỈ cho nhóm hoàn toàn KHÔNG đọc được tên (không có Text Note nào đủ gần/hợp lệ) —
    /// phân biệt với nhóm CÓ tên nhưng chưa khớp Type.</summary>
    public bool IsUnnamed { get; init; }

    public FloorTypeChoice? SelectedChoice
    {
        get => _selectedChoice;
        set => SetField(ref _selectedChoice, value);
    }

    /// <summary>Toàn bộ Type (Floor/Ceiling — tuỳ SurfaceKind đang chọn) hiện có trong model — dùng làm
    /// GỐC để Duplicate khi nhóm này CHƯA CÓ Type khớp (AutoMatched false, xem
    /// DuplicateBaseType/CustomThicknessMm).</summary>
    public List<HostObjAttributes> DuplicateBaseChoices { get; init; } = new();

    /// <summary>Type người dùng chọn làm gốc để tool tự Duplicate 1 Type MỚI (xem
    /// FloorBuilder.DuplicateTypeWithThickness) khi nhóm này chưa có Type khớp sẵn — chỉ có ý nghĩa khi
    /// SelectedChoice vẫn null lúc đóng cửa sổ.</summary>
    public HostObjAttributes? DuplicateBaseType
    {
        get => _duplicateBaseType;
        set => SetField(ref _duplicateBaseType, value);
    }

    /// <summary>Bề dày TỔNG (mm) của Type mới sẽ Duplicate từ DuplicateBaseType — chỉ chỉnh lớp Function
    /// = Structure của Type gốc, các lớp khác giữ nguyên (xem FloorBuilder).</summary>
    public double CustomThicknessMm
    {
        get => _customThicknessMm;
        set => SetField(ref _customThicknessMm, value);
    }

    /// <summary>Cao độ (mm) so với Level của mặt bằng đang thao tác — gán vào tham số Height Offset From
    /// Level của Floor sau khi tạo.</summary>
    public double OffsetMm
    {
        get => _offsetMm;
        set => SetField(ref _offsetMm, value);
    }

    /// <summary>Offset (mm) LÙI VÀO TRONG áp dụng cho MỌI cạnh biên sketch (cả biên ngoài lẫn biên các lỗ
    /// khoét, nếu có) trước khi tạo Floor — dương = mép sàn lùi vào trong (biên ngoài co lại, biên lỗ
    /// khoét nới rộng ra, đều làm diện tích sàn nhỏ đi đều ở mọi cạnh), âm = mép sàn tràn ra ngoài biên
    /// hatch gốc. 0 = giữ nguyên đúng biên hatch đọc được từ CAD. Xem FloorBuilder.OffsetLoopInPlane.</summary>
    public double EdgeOffsetMm
    {
        get => _edgeOffsetMm;
        set => SetField(ref _edgeOffsetMm, value);
    }

    public HostObjAttributes? SelectedType => SelectedChoice?.Type;
    public int Count => Regions.Count;
    public string CountLabel => $"{Regions.Count} vùng";
    public string NameLabel => AutoMatched ? Name : $"{Name}  (CHƯA CÓ TYPE)";

    /// <summary>True nếu nhóm này đã đủ thông tin để vẽ sàn — HOẶC đã có Type khớp/chọn sẵn, HOẶC đã
    /// chọn Type gốc + nhập bề dày hợp lệ (> 0) để tool tự Duplicate Type mới.</summary>
    public bool IsResolved =>
        SelectedChoice is not null || (DuplicateBaseType is not null && CustomThicknessMm > 0);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Kết quả vẽ 1 RawHatchRegion — xem FloorBuilder.</summary>
internal enum FloorPlacementOutcome
{
    Created,
    SkippedNoType,
    Failed
}

internal sealed record FloorPlacementResult(
    FloorNameGroup Group, RawHatchRegion Region, FloorPlacementOutcome Outcome, string? ErrorMessage = null);
