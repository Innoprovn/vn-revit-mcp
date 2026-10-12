namespace InnoPro.Revit.Commands.Rebar.Qa;

/// <summary>Ngưỡng kiểm tra; giá trị lấy từ tham số lệnh thay cho file YAML của bản gốc.</summary>
internal sealed class RuleConfig
{
    public ClashConfig ClashRebarRebar { get; set; } = new();
    public BarOutsideHostConfig BarOutsideHost { get; set; } = new();
}

internal sealed class ClashConfig
{
    public double? CellSizeMm { get; set; } = 250;
    public double? ToleranceMm { get; set; } = 0;

    /// <summary>Các nhóm shape không bao giờ tính va chạm với nhau (khác nhóm thì bỏ qua).</summary>
    public List<List<string>> ShapeCodeIsolatedGroups { get; set; } = [];
}

internal sealed class BarOutsideHostConfig
{
    public double? MinInsidePercent { get; set; } = 100;
}
