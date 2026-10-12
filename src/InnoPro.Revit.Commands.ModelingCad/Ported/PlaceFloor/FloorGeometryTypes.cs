using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>1 dòng trong bảng tổng hợp Bước cuối — 1 ô sàn (outer boundary) kèm các lỗ mở (hole) đã gắn. Level/Offset/Floor Type nhập RIÊNG từng dòng, không gán trước lúc pick.</summary>
internal sealed class FloorRow : INotifyPropertyChanged
{
    private Level? _selectedLevel;
    private string _offsetMmText = "0";
    private FloorType? _selectedFloorType;

    /// <summary>Khoá ổn định cho highlight — không đổi kể cả khi danh sách dòng bị xoá/chèn giữa chừng.</summary>
    public required int RowKey { get; init; }

    public required List<Curve> OuterLoop { get; set; }
    public List<List<Curve>> Holes { get; } = new();

    public required List<Level> LevelChoices { get; init; }

    public string Label => $"Sàn #{RowKey}";
    public string HoleCountLabel => Holes.Count == 0 ? "Không có lỗ mở" : $"{Holes.Count} lỗ mở";

    public double AreaM2 { get; private set; }
    public string AreaLabel => $"{AreaM2:0.00} m²";

    public XYZ Center { get; private set; } = XYZ.Zero;

    public Level? SelectedLevel
    {
        get => _selectedLevel;
        set => SetField(ref _selectedLevel, value);
    }

    public string OffsetMmText
    {
        get => _offsetMmText;
        set => SetField(ref _offsetMmText, value);
    }

    public FloorType? SelectedFloorType
    {
        get => _selectedFloorType;
        set => SetField(ref _selectedFloorType, value);
    }

    public double ParseOffsetMmOrZero() =>
        double.TryParse(OffsetMmText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;

    /// <summary>Tính lại diện tích + tâm sau khi thêm/xoá lỗ mở — diện tích outer trừ tổng diện tích các lỗ, quy đổi m².</summary>
    public void Recompute()
    {
        var outerPts = PolygonMath.Tessellate(OuterLoop);
        var outerAreaFeet2 = PolygonMath.AreaAbs(outerPts);
        var holesAreaFeet2 = Holes.Sum(h => PolygonMath.AreaAbs(PolygonMath.Tessellate(h)));
        var netAreaFeet2 = Math.Max(0, outerAreaFeet2 - holesAreaFeet2);

        AreaM2 = UnitUtils.ConvertFromInternalUnits(netAreaFeet2, UnitTypeId.SquareMeters);
        Center = PolygonMath.Centroid(outerPts);

        OnPropertyChanged(nameof(AreaM2));
        OnPropertyChanged(nameof(AreaLabel));
        OnPropertyChanged(nameof(HoleCountLabel));
    }

    /// <summary>Toàn bộ điểm (outer + mọi lỗ) dùng để zoom "Xem" bao trọn cả ô sàn.</summary>
    public List<XYZ> AllPointsForZoom()
    {
        var pts = new List<XYZ>(PolygonMath.Tessellate(OuterLoop));
        foreach (var hole in Holes)
            pts.AddRange(PolygonMath.Tessellate(hole));
        return pts;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Kết quả 1 lượt "Đặt" toàn bộ sàn.</summary>
internal sealed record PlaceSummary(
    int Created, int Skipped, int Failed,
    List<string> Errors,
    IReadOnlyList<ElementId> CreatedIds);
