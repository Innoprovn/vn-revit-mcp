using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.WallFinishLayer;

internal enum HeightMode
{
    MatchWallHeight,
    BelowCeiling
}

/// <summary>Lựa chọn tạo lớp tô hai mặt tường.</summary>
internal sealed class FinishLayerOptions
{
    public required WallType CoatingType { get; init; }
    public required double BaseOffsetMm { get; init; }
    public required HeightMode HeightMode { get; init; }
    public required double TopOffsetMm { get; init; }

    /// <summary>Chỉ dùng khi HeightMode = BelowCeiling: đỉnh lớp tô = mặt trên trần gần đó + giá trị này.</summary>
    public required double CeilingGapMm { get; init; }
}

internal enum SecondLayerTopMode
{
    FreeHeight,
    OffsetFromCeilingBottom,
    OffsetFromSelectedFace
}

/// <summary>Lựa chọn tạo lớp vật liệu trên một mặt tường đã chọn.</summary>
internal sealed class SecondLayerOptions
{
    public required WallType MaterialType { get; init; }
    public required double BaseOffsetMm { get; init; }
    public required SecondLayerTopMode TopMode { get; init; }
    public required double FreeHeightMm { get; init; }
    public required double CeilingOffsetMm { get; init; }
    public required double SelectedFaceOffsetMm { get; init; }
}
