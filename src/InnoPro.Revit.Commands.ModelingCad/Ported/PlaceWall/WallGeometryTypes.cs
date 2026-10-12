using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceWall;

/// <summary>1 tường đọc được từ layer CAD — ghép từ 2 Line song song gần nhau, lấy tim làm đường đặt tường.</summary>
internal sealed class RawWall
{
    public required XYZ Start { get; init; }

    public required XYZ End { get; init; }

    /// <summary>Khoảng cách vuông góc giữa 2 Line đã ghép cặp (feet) — dùng làm bề rộng b của tường.</summary>
    public required double WidthFeet { get; init; }

    /// <summary>2 Line CAD gốc (Z=0) đã ghép cặp — dùng để vẽ lại highlight đỏ đúng hình dạng đã vẽ.</summary>
    public required List<(XYZ A, XYZ B)> Segments { get; init; }

    /// <summary>Tên đọc được từ Text Note gần tim tường nhất, dạng "TÊN(bxh)" (vd "D2-X7(220x600)") — null nếu
    /// không tìm được Text Note nào đủ gần/đúng định dạng. Gán SAU khi CadWallScanner quét xong hình học
    /// (xem WallTagReader.AttachTags), không phải lúc tạo RawWall — không đổi thuật toán ghép cặp Line.</summary>
    public string? TagName { get; set; }

    /// <summary>Bề rộng b đọc từ Text (mm), độc lập với TagName (có thể có dù TagName null — Text dạng "bxh" trần không tên) — chỉ dùng xác nhận cặp Line lúc ghép, KHÔNG dùng làm b cuối cùng (xem WidthFeet/WallPlacer.GroupWalls).</summary>
    public double? TagBMm { get; set; }

    /// <summary>Chiều cao h đọc từ Text (mm), độc lập với TagName — không đo được từ mặt bằng CAD nên đây là nguồn duy nhất.</summary>
    public double? TagHMm { get; set; }

    /// <summary>True nếu tường này được WallOrphanRepairer tự sửa từ 1 Line mồ côi (không có cặp CAD hợp lệ cùng layer) — 1 trong 2 Segments là Line vẽ thêm (synthetic), không phải Line CAD thật.</summary>
    public bool IsCrossLayerPair { get; init; }

    /// <summary>Ghi chú cách sửa mồ côi (layer gốc, có khớp Text hay không) — chỉ có giá trị khi IsCrossLayerPair = true, phục vụ hiển thị cảnh báo.</summary>
    public string? CrossLayerNote { get; init; }

}

/// <summary>1 layer CAD kèm số liệu hiển thị trong ComboBox chọn layer — dùng chung cho cả quét line tường
/// (UnitLabel "tường") lẫn quét mặt cắt (UnitLabel "mặt cắt").</summary>
internal sealed record CadLayerEntry(string Name, int Count, string UnitLabel)
{
    public override string ToString() => $"{Name}  ({Count} {UnitLabel})";
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadWallScanResult
{
    public required IReadOnlyDictionary<string, List<RawWall>> WallsByLayer { get; init; }

    /// <summary>Toàn bộ đoạn thẳng thô theo layer, TRƯỚC khi ghép cặp — dùng để highlight xác nhận layer ở Bước 3 (kể cả đoạn chưa/không ghép được cặp), không dùng cho việc đặt tường.</summary>
    public required IReadOnlyDictionary<string, List<(XYZ A, XYZ B)>> RawSegmentsByLayer { get; init; }

    /// <summary>Line không ghép được cặp cùng layer ở Pass 1 (mồ côi thật + bị "cướp" candidate) kèm tên layer gốc — input cho WallOrphanRepairer.</summary>
    public required IReadOnlyList<(XYZ A, XYZ B, string LayerName)> Orphans { get; init; }

    public List<CadLayerEntry> GetLayerSummaries() =>
        WallsByLayer
            .Select(kv => new CadLayerEntry(kv.Key, kv.Value.Count, "tường"))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>1 lựa chọn Basic Wall Type trong ComboBox.</summary>
internal sealed record SymbolChoice(WallType Symbol, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>1 dòng trong bảng nhóm tường — gộp theo tên (có Text), theo cặp bxh (có h nhưng không tên), hoặc theo b hình học (không có Text nào).</summary>
internal sealed class WallGroup : INotifyPropertyChanged
{
    private SymbolChoice? _selectedType;
    private bool _isExactMatch;
    private bool _shouldModel;

    public bool ShouldModel
    {
        get => _shouldModel;
        set => SetField(ref _shouldModel, value);
    }

    /// <summary>Tên đọc được từ Text — null nếu không có Text nào có tên gần tường (có thể vẫn có SizeHMm nếu Text chỉ ghi "bxh" trần).</summary>
    public string? TagName { get; init; }

    /// <summary>Bề rộng b — LUÔN lấy từ hình học CAD đo được (đã làm tròn bội 5mm), kể cả khi TagName != null,
    /// không lấy từ Text (Text có thể lệch so với CAD vẽ thực tế — xem RawWall.TagBMm).</summary>
    public required double SizeBMm { get; init; }

    /// <summary>Chiều cao h đọc từ Text — có giá trị khi ít nhất 1 tường trong nhóm đọc được h (kể cả không có tên), null nếu hoàn toàn không có Text.</summary>
    public double? SizeHMm { get; init; }

    public required List<RawWall> Walls { get; init; }
    public ObservableCollection<SymbolChoice> TypeChoices { get; init; } = new();

    public string SizeLabel => SizeHMm is double h
        ? (TagName is not null ? $"{TagName} — {SizeBMm:0}x{h:0}mm" : $"{SizeBMm:0}x{h:0}mm")
        : $"{SizeBMm:0}mm";

    public string CountLabel
    {
        get
        {
            var crossCount = Walls.Count(b => b.IsCrossLayerPair);
            return crossCount > 0
                ? $"{Walls.Count} tường ({crossCount} tự sửa từ Line mồ côi — kiểm tra lại)"
                : $"{Walls.Count} tường";
        }
    }

    /// <summary>True nếu có ít nhất 1 tường trong nhóm được WallOrphanRepairer tự sửa từ Line mồ côi.</summary>
    public bool HasCrossLayerWalls => Walls.Any(b => b.IsCrossLayerPair);

    public SymbolChoice? SelectedType
    {
        get => _selectedType;
        set => SetField(ref _selectedType, value);
    }

    /// <summary>Type đang chọn có khớp ĐÚNG (trong dung sai WallPlacer.MatchToleranceMm) cả b và h hay không — chỉ có ý nghĩa khi nhóm có SizeHMm (xem MatchStatusLabel).</summary>
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

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

/// <summary>Kết quả đặt 1 tường.</summary>
internal enum PlaceOutcome
{
    Created,
    Failed
}

internal sealed record PlaceResult(PlaceOutcome Outcome, string? ErrorMessage = null, ElementId? CreatedId = null);


