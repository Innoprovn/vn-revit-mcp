namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using InnoPro.Revit.Sdk;

/// <summary>Đọc tham số tùy chọn: chuỗi rỗng từ form tự sinh coi như không truyền.</summary>
internal static class ArgsX
{
    public static bool Has(JsonElement args, string name)
    {
        if (!Args.Has(args, name)) return false;
        var v = args.GetProperty(name);
        return v.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.Object => v.EnumerateObject().Any(),
            _ => true,
        };
    }

    public static string? String(JsonElement args, string name) =>
        Has(args, name) ? Args.GetString(args, name).Trim() : null;

    public static double? Double(JsonElement args, string name) =>
        Has(args, name) ? Args.GetDoubleOrDefault(args, name, 0) : null;

    public static long? Long(JsonElement args, string name) =>
        Has(args, name) ? Args.GetLong(args, name) : null;
}
