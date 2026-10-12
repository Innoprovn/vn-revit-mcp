using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.CreateBeamType;

/// <summary>1 mặt cắt dầm đọc được từ layer CAD — b/h đọc theo 2 cạnh đứng dài nhất + chiều cao toàn bộ, null nếu lỗi đọc (xem ErrorMessage).</summary>
internal sealed class RawBeamSection
{
    public required XYZ Center { get; init; }

    /// <summary>Bề rộng thân dầm (feet) — khoảng cách X giữa 2 cạnh đứng dài nhất. Null nếu đọc lỗi.</summary>
    public required double? BFeet { get; init; }

    /// <summary>Chiều cao mặt cắt (feet) — Y lớn nhất trừ Y nhỏ nhất trên các cạnh đứng/ngang. Null nếu đọc lỗi.</summary>
    public required double? HFeet { get; init; }

    /// <summary>Lý do đọc lỗi (ví dụ thiếu cạnh đứng) — null nếu đọc thành công.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Toàn bộ đoạn thẳng gốc (Z=0), kể cả đoạn xiên/gãy khúc — dùng để vẽ lại highlight đỏ đúng hình dạng đã vẽ.</summary>
    public required List<(XYZ A, XYZ B)> Segments { get; init; }
}

/// <summary>1 layer CAD kèm số liệu hiển thị trong ComboBox chọn layer.</summary>
internal sealed record CadLayerEntry(string Name, int ColumnCount)
{
    public override string ToString() => $"{Name}  ({ColumnCount} cột)";
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadBeamScanResult
{
    public required IReadOnlyDictionary<string, List<RawBeamSection>> ColumnsByLayer { get; init; }

    public List<CadLayerEntry> GetLayerSummaries() =>
        ColumnsByLayer
            .Select(kv => new CadLayerEntry(kv.Key, kv.Value.Count))
            .OrderByDescending(e => e.ColumnCount)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>1 lựa chọn Family trong ComboBox — bọc Family kèm chuỗi hiển thị.</summary>
internal sealed record FamilyChoice(Family Family, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal enum MatchStatus
{
    Existing,
    WillCreate
}

/// <summary>1 dòng trong bảng nhóm dầm — mọi mặt cắt cùng kích thước b×h được gộp vào đây, hoặc 1 mặt cắt lỗi đọc đứng riêng.</summary>
internal sealed class BeamTypeGroup : INotifyPropertyChanged
{
    private readonly IReadOnlyList<(double BMm, double HMm)> _existingSizes;
    private double _bMm;
    private double _hMm;
    private MatchStatus _status;

    public BeamTypeGroup(List<RawBeamSection> columns, double bMm, double hMm,
        IReadOnlyList<(double BMm, double HMm)> existingSizes, string? errorMessage = null)
    {
        Columns = columns;
        _existingSizes = existingSizes;
        _bMm = bMm;
        _hMm = hMm;
        ErrorMessage = errorMessage;
        _status = ComputeStatus();
    }

    public List<RawBeamSection> Columns { get; }

    /// <summary>Lý do lỗi đọc mặt cắt — bị xoá ngay khi người dùng sửa tay B hoặc H.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Bề rộng b (mm) — sửa tay được, tính lại Status/SizeLabel ngay khi đổi.</summary>
    public double BMm
    {
        get => _bMm;
        set
        {
            if (_bMm == value)
                return;

            _bMm = value;
            ErrorMessage = null;
            Status = ComputeStatus();
            NotifyEditableChanged();
        }
    }

    /// <summary>Chiều cao h (mm) — sửa tay được, tính lại Status/SizeLabel ngay khi đổi.</summary>
    public double HMm
    {
        get => _hMm;
        set
        {
            if (_hMm == value)
                return;

            _hMm = value;
            ErrorMessage = null;
            Status = ComputeStatus();
            NotifyEditableChanged();
        }
    }

    public string SizeLabel => ErrorMessage is not null ? "?" : $"{BMm:0}x{HMm:0}";
    public string CountLabel => $"{Columns.Count} mặt cắt";

    public MatchStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string StatusText => ErrorMessage ?? (Status == MatchStatus.Existing ? "Đã có sẵn" : "Sẽ tạo Type mới");

    private MatchStatus ComputeStatus() =>
        BeamTypeCreator.IsSizeExisting(_bMm, _hMm, _existingSizes) ? MatchStatus.Existing : MatchStatus.WillCreate;

    private void NotifyEditableChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
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
