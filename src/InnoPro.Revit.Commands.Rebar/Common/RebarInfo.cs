namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

/// <summary>Đọc thông tin thường dùng của một phần tử thép, không ném lỗi.</summary>
internal static class RebarInfo
{
    /// <summary>Khối lượng riêng thép mặc định (kg/m3).</summary>
    public const double SteelDensity = 7850;

    public static RebarBarType? BarType(Document doc, Element e) => doc.GetElement(e.GetTypeId()) as RebarBarType;

    public static double NominalDiameterMm(Document doc, Element e) =>
        BarType(doc, e) is { } t ? UnitConvert.FeetToMm(t.BarNominalDiameter) : 0;

    /// <summary>Bán kính hình học (feet) - đường kính Revit dùng để vẽ thanh.</summary>
    public static double ModelRadiusFeet(Document doc, Element e) =>
        BarType(doc, e) is { } t ? t.BarModelDiameter / 2.0 : 0;

    public static string ShapeName(Document doc, Element e)
    {
        try
        {
            var id = e switch
            {
                Rebar r => r.GetShapeId(),
                RebarInSystem ris => ris.RebarShapeId,
                _ => ElementId.InvalidElementId,
            };
            return (doc.GetElement(id) as RebarShape)?.Name ?? "";
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            // Free Form uốn khớp nhiều shape cùng lúc.
            return "";
        }
    }

    public static ElementId HostId(Element e) => e switch
    {
        Rebar r => r.GetHostId(),
        RebarInSystem ris => ris.GetHostId(),
        RebarContainer c => c.GetHostId(),
        _ => ElementId.InvalidElementId,
    };

    public static int Quantity(Element e) => e switch
    {
        Rebar r => r.Quantity,
        RebarInSystem ris => ris.Quantity,
        _ => 0,
    };

    public static double TotalLengthFeet(Element e) => e switch
    {
        Rebar r => r.TotalLength,
        RebarInSystem ris => ris.TotalLength,
        _ => 0,
    };

    public static double VolumeCubicFeet(Element e) => e switch
    {
        Rebar r => r.Volume,
        RebarInSystem ris => ris.Volume,
        _ => 0,
    };

    public static double WeightKg(Element e, double density = SteelDensity) =>
        UnitConvert.CubicFeetToCubicMeters(VolumeCubicFeet(e)) * density;

    public static string Partition(Element e) =>
        e.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString()?.Trim() ?? "";

    public static string RebarNumber(Element e)
    {
        var p = e.get_Parameter(BuiltInParameter.REBAR_NUMBER);
        if (p == null || !p.HasValue) return "";
        return p.StorageType switch
        {
            StorageType.Integer => p.AsInteger().ToString(),
            StorageType.String => p.AsString() ?? "",
            _ => p.AsValueString() ?? "",
        };
    }

    public static string Mark(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

    /// <summary>Giá trị tham số dạng chuỗi (instance, rồi tới type); rỗng nếu không có.</summary>
    public static string ParameterText(Document doc, Element e, string name)
    {
        var text = ElementInfo.ParameterText(e, name);
        if (text == null && doc.GetElement(e.GetTypeId()) is { } type)
            text = ElementInfo.ParameterText(type, name);
        return text?.Trim() ?? "";
    }

    public static string HostLabel(Document doc, Element e)
    {
        var id = HostId(e);
        if (id == ElementId.InvalidElementId) return "";
        var host = doc.GetElement(id);
        if (host == null) return $"#{id.Value}";
        var mark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
        return string.IsNullOrWhiteSpace(mark)
            ? $"{host.Category?.Name}: {host.Name} #{id.Value}"
            : $"{host.Category?.Name}: {mark} #{id.Value}";
    }
}
