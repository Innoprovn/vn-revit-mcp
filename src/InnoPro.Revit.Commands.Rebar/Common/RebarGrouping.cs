namespace InnoPro.Revit.Commands.Rebar;

using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

/// <summary>Tiêu chí gom nhóm thép dùng chung cho thống kê và chọn nhanh.</summary>
internal static class RebarGrouping
{
    public static readonly string[] Modes =
        ["partition", "parameter", "bar_type", "diameter", "shape", "host", "host_category", "level", "mark", "rebar_number"];

    public const string ModesJson =
        "\"partition\",\"parameter\",\"bar_type\",\"diameter\",\"shape\",\"host\",\"host_category\",\"level\",\"mark\",\"rebar_number\"";

    public static string Normalize(string mode)
    {
        var m = mode.Trim().ToLowerInvariant();
        if (!Modes.Contains(m))
            throw new CommandException($"Tiêu chí gom nhóm '{mode}' không hợp lệ. Dùng một trong: {string.Join(", ", Modes)}.");
        return m;
    }

    public static string Label(string mode, string? parameterName) => mode switch
    {
        "partition" => "Partition",
        "parameter" => parameterName ?? "Tham số",
        "bar_type" => "Bar type",
        "diameter" => "Đường kính (mm)",
        "shape" => "Shape",
        "host" => "Host",
        "host_category" => "Category host",
        "level" => "Tầng",
        "mark" => "Mark",
        "rebar_number" => "Rebar Number",
        _ => mode,
    };

    /// <summary>Giá trị nhóm của một phần tử thép; chuỗi rỗng = chưa gán.</summary>
    public static string Key(Document doc, Element e, string mode, string? parameterName)
    {
        switch (mode)
        {
            case "partition": return RebarInfo.Partition(e);
            case "parameter":
                if (string.IsNullOrWhiteSpace(parameterName))
                    throw new CommandException("Gom theo 'parameter' cần 'parameter_name' (ví dụ \"GH_Rebar Layer\").");
                return RebarInfo.ParameterText(doc, e, parameterName);
            case "bar_type": return RebarInfo.BarType(doc, e)?.Name ?? "";
            case "diameter":
                var d = RebarInfo.NominalDiameterMm(doc, e);
                return d > 0 ? Math.Round(d, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            case "shape": return RebarInfo.ShapeName(doc, e);
            case "host": return RebarInfo.HostLabel(doc, e);
            case "host_category":
                var hostId = RebarInfo.HostId(e);
                return hostId == ElementId.InvalidElementId ? "" : doc.GetElement(hostId)?.Category?.Name ?? "";
            case "level":
                var host = doc.GetElement(RebarInfo.HostId(e));
                return ElementInfo.LevelName(doc, e) ?? (host == null ? null : ElementInfo.LevelName(doc, host)) ?? "";
            case "mark": return RebarInfo.Mark(e);
            case "rebar_number": return RebarInfo.RebarNumber(e);
            default: throw new CommandException($"Tiêu chí gom nhóm '{mode}' không hợp lệ.");
        }
    }

    /// <summary>Sắp xếp giá trị nhóm: số theo giá trị số, chữ theo bảng chữ cái, nhóm rỗng xuống cuối.</summary>
    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.OrderBy(i => key(i).Length == 0 ? 1 : 0)
             .ThenBy(i => double.TryParse(key(i), System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
             .ThenBy(key, StringComparer.OrdinalIgnoreCase);
}
