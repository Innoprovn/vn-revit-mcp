using System.Text.Json;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Sdk;

/// <summary>
/// Đọc tham số từ JSON, báo lỗi rõ ràng khi thiếu hoặc sai kiểu.
/// </summary>
public static class Args
{
    public static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null;

    public static string GetString(JsonElement args, string name) =>
        Has(args, name) ? Get(args, name).ToString() : throw Missing(name);

    public static string? GetStringOrDefault(JsonElement args, string name, string? fallback = null) =>
        Has(args, name) ? Get(args, name).ToString() : fallback;

    public static long GetLong(JsonElement args, string name) =>
        Has(args, name) ? ReadLong(Get(args, name), name) : throw Missing(name);

    public static long GetLongOrDefault(JsonElement args, string name, long fallback) =>
        Has(args, name) ? ReadLong(Get(args, name), name) : fallback;

    public static double GetDoubleOrDefault(JsonElement args, string name, double fallback)
    {
        if (!Has(args, name)) return fallback;
        var v = Get(args, name);
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw WrongType(name, "số");
    }

    public static bool GetBoolOrDefault(JsonElement args, string name, bool fallback)
    {
        if (!Has(args, name)) return fallback;
        var v = Get(args, name);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw WrongType(name, "true/false"),
        };
    }

    /// <summary>Đọc một mảng id phần tử (số nguyên).</summary>
    public static IReadOnlyList<ElementId> GetElementIds(JsonElement args, string name)
    {
        if (!Has(args, name)) throw Missing(name);
        var v = Get(args, name);
        if (v.ValueKind != JsonValueKind.Array) throw WrongType(name, "mảng id");
        return v.EnumerateArray().Select(e => new ElementId(ReadLong(e, name))).ToList();
    }

    /// <summary>Đọc một mảng bắt buộc (ví dụ danh sách tường cần tạo).</summary>
    public static IReadOnlyList<JsonElement> GetArray(JsonElement args, string name)
    {
        if (!Has(args, name)) throw Missing(name);
        var v = Get(args, name);
        if (v.ValueKind != JsonValueKind.Array) throw WrongType(name, "mảng");
        return v.EnumerateArray().ToList();
    }

    /// <summary>Đọc mảng chuỗi, không có thì trả danh sách rỗng.</summary>
    public static IReadOnlyList<string> GetStringsOrEmpty(JsonElement args, string name)
    {
        if (!Has(args, name)) return [];
        var v = Get(args, name);
        if (v.ValueKind != JsonValueKind.Array) throw WrongType(name, "mảng chuỗi");
        return v.EnumerateArray().Select(e => e.ToString()).ToList();
    }

    /// <summary>
    /// Đọc điểm {x, y, z} tính bằng mm (z mặc định 0) và đổi sang feet của Revit.
    /// </summary>
    public static XYZ GetPointMm(JsonElement args, string name)
    {
        if (!Has(args, name)) throw Missing(name);
        var v = Get(args, name);
        if (v.ValueKind != JsonValueKind.Object) throw WrongType(name, "điểm {x, y, z} tính bằng mm");
        return new XYZ(
            UnitConvert.MmToFeet(GetDoubleOrDefault(v, "x", double.NaN) is var x && !double.IsNaN(x) ? x : throw Missing($"{name}.x")),
            UnitConvert.MmToFeet(GetDoubleOrDefault(v, "y", double.NaN) is var y && !double.IsNaN(y) ? y : throw Missing($"{name}.y")),
            UnitConvert.MmToFeet(GetDoubleOrDefault(v, "z", 0)));
    }

    private static JsonElement Get(JsonElement args, string name) => args.GetProperty(name);

    private static long ReadLong(JsonElement v, string name) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
        _ => throw WrongType(name, "số nguyên"),
    };

    private static CommandException Missing(string name) => new($"Thiếu tham số bắt buộc '{name}'.");

    private static CommandException WrongType(string name, string expected) =>
        new($"Tham số '{name}' phải là {expected}.");
}
