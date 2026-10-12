using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>1 circle thô đọc được từ layer cọc.</summary>
internal sealed class RawCircle
{
    public required XYZ Center { get; init; }
    public required double RadiusFeet { get; init; }

    /// <summary>Đài chứa cọc này (point-in-polygon, gán sau khi quét) — null nếu chưa xác định được.</summary>
    public BoundaryRow? OwningBoundary { get; set; }
}

/// <summary>1 đường bao khép kín thô đọc được từ layer đài móng.</summary>
internal sealed class RawBoundary
{
    public required XYZ Center { get; init; }

    /// <summary>Kích thước bounding box trục X/Y (feet) — Length luôn ≥ Width.</summary>
    public required double LengthFeet { get; init; }
    public required double WidthFeet { get; init; }

    /// <summary>Toàn bộ điểm đa giác gốc (đã khép kín, Z=0) — dùng để vẽ lại highlight đỏ.</summary>
    public required List<XYZ> Points { get; init; }
}

/// <summary>1 layer CAD kèm số liệu hiển thị trong ComboBox chọn layer.</summary>
internal sealed record CadLayerEntry(string Name, int CircleCount, int BoundaryCount)
{
    public override string ToString() => $"{Name}  ({CircleCount} circle, {BoundaryCount} đường bao)";
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadPileScanResult
{
    public required IReadOnlyDictionary<string, List<RawCircle>> CirclesByLayer { get; init; }
    public required IReadOnlyDictionary<string, List<RawBoundary>> BoundariesByLayer { get; init; }

    public List<CadLayerEntry> GetLayerSummaries()
    {
        var names = new HashSet<string>(CirclesByLayer.Keys);
        names.UnionWith(BoundariesByLayer.Keys);

        return names
            .Select(name => new CadLayerEntry(
                name,
                CirclesByLayer.TryGetValue(name, out var c) ? c.Count : 0,
                BoundariesByLayer.TryGetValue(name, out var b) ? b.Count : 0))
            .OrderByDescending(e => e.CircleCount + e.BoundaryCount)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>1 lựa chọn Type trong ComboBox — bọc FamilySymbol kèm chuỗi hiển thị.</summary>
internal sealed record SymbolChoice(FamilySymbol Symbol, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>1 dòng trong bảng nhóm cọc — mọi circle cùng đường kính đã làm tròn được gộp vào đây.</summary>
internal sealed class PileGroup : INotifyPropertyChanged
{
    private SymbolChoice? _selectedType;
    private bool _isExactMatch;

    public required double DiameterMm { get; init; }
    public required List<RawCircle> Circles { get; init; }
    public List<SymbolChoice> TypeChoices { get; init; } = new();

    public string DiameterLabel => $"Ø{DiameterMm:0}mm";
    public string CountLabel => $"{Circles.Count} cọc";

    public SymbolChoice? SelectedType
    {
        get => _selectedType;
        set => SetField(ref _selectedType, value);
    }

    /// <summary>Type đang gán có đúng khớp đường kính nhóm (trong dung sai) hay chỉ là Type gần đúng nhất — false thì cần "Tạo Type thiếu" trước khi Đặt để không lẫn nhiều nhóm đường kính khác nhau vào chung 1 Type.</summary>
    public bool IsExactMatch
    {
        get => _isExactMatch;
        set
        {
            if (SetField(ref _isExactMatch, value))
                OnPropertyChanged(nameof(MatchStatusLabel));
        }
    }

    public string MatchStatusLabel => IsExactMatch ? "Đã có Type khớp" : "Chưa có Type khớp — cần tạo mới";

    public bool IsResolved => SelectedType is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>1 nguồn CAD trong ComboBox chọn file — bọc ImportInstance kèm tên hiển thị.</summary>
internal sealed record CadSourceChoice(ImportInstance Instance, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>1 dòng trong danh sách checkbox chọn layer (cho phép chọn nhiều layer/lượt).</summary>
internal sealed class LayerChoice : INotifyPropertyChanged
{
    private bool _isChecked;

    public required CadLayerEntry Entry { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>1 dòng trong bảng đài móng đã quét — Type đài giờ là 1 FloorType dùng chung cho cả lượt Đặt (v7 Job S), không còn chọn riêng theo dòng.</summary>
internal sealed class BoundaryRow
{
    public required int Index { get; init; }
    public required RawBoundary Boundary { get; init; }

    public string Label => $"Đài #{Index}";
}

/// <summary>Kết quả đặt 1 cọc hoặc 1 đài.</summary>
internal enum PlaceOutcome
{
    Created,
    Failed
}

internal sealed record PlaceResult(PlaceOutcome Outcome, string? ErrorMessage = null, ElementId? CreatedId = null);
