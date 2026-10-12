using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceColumn;

/// <summary>1 đường bao cột chữ nhật thô đọc được từ layer CAD, đã quy về hình chữ nhật xoay nhỏ nhất (min-area rectangle).</summary>
internal sealed class RawColumn
{
    public required XYZ Center { get; init; }

    /// <summary>Kích thước theo trục chính (feet) — trục chính có góc trong [0°, 90°).</summary>
    public required double LengthFeet { get; init; }

    /// <summary>Kích thước theo trục vuông góc với trục chính (feet).</summary>
    public required double WidthFeet { get; init; }

    /// <summary>Góc trục chính so với trục X thế giới (radian), luôn trong [0, π/2).</summary>
    public required double AngleRadians { get; init; }

    /// <summary>Toàn bộ đoạn thẳng gốc (Z=0) — mỗi đoạn là 1 cạnh CAD thật (từ PolyLine hoặc từ cụm Line rời rạc
    /// đã gộp), dùng để vẽ lại highlight đỏ đúng hình dạng đã vẽ, không phụ thuộc thứ tự nối cạnh.</summary>
    public required List<(XYZ A, XYZ B)> Segments { get; init; }
}

/// <summary>1 layer CAD kèm số liệu hiển thị trong ComboBox chọn layer.</summary>
internal sealed record CadLayerEntry(string Name, int ColumnCount)
{
    public override string ToString() => $"{Name}  ({ColumnCount} cột)";
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadColumnScanResult
{
    public required IReadOnlyDictionary<string, List<RawColumn>> ColumnsByLayer { get; init; }

    public List<CadLayerEntry> GetLayerSummaries() =>
        ColumnsByLayer
            .Select(kv => new CadLayerEntry(kv.Key, kv.Value.Count))
            .OrderByDescending(e => e.ColumnCount)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>1 lựa chọn Type trong ComboBox — bọc FamilySymbol kèm chuỗi hiển thị.</summary>
internal sealed record SymbolChoice(FamilySymbol Symbol, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>1 dòng trong bảng nhóm cột — mọi cột cùng kích thước b×h (không phân biệt hướng xoay) được gộp vào đây.</summary>
internal sealed class ColumnGroup : INotifyPropertyChanged
{
    private SymbolChoice? _selectedType;
    private bool _isExactMatch;

    /// <summary>Kích thước nhỏ hơn đã làm tròn bội 5mm — dùng làm khoá gộp nhóm, không phân biệt cột nào xoay hướng nào.</summary>
    public required double SizeAMm { get; init; }

    /// <summary>Kích thước lớn hơn đã làm tròn bội 5mm.</summary>
    public required double SizeBMm { get; init; }

    public required List<RawColumn> Columns { get; init; }
    public List<SymbolChoice> TypeChoices { get; init; } = new();

    public string SizeLabel => $"{SizeBMm:0}x{SizeAMm:0}mm";
    public string CountLabel => $"{Columns.Count} cột";

    public SymbolChoice? SelectedType
    {
        get => _selectedType;
        set => SetField(ref _selectedType, value);
    }

    public bool IsExactMatch
    {
        get => _isExactMatch;
        set
        {
            if (SetField(ref _isExactMatch, value))
            {
                OnPropertyChanged(nameof(MatchStatusLabel));
                OnPropertyChanged(nameof(MatchStateKey));
            }
        }
    }

    public string MatchStatusLabel => IsExactMatch ? "Đã có Type khớp" : "Chưa có Type khớp — cần tạo mới";

    public string MatchStateKey => IsExactMatch ? "Matched" : "Missing";

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

/// <summary>Kết quả đặt 1 cột.</summary>
internal enum PlaceOutcome
{
    Created,
    Failed
}

internal sealed record PlaceResult(PlaceOutcome Outcome, string? ErrorMessage = null, ElementId? CreatedId = null);
