using System.Collections;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Code;

public sealed class RunCSharp : RevitCommandBase
{
    private const int MaxItems = 500;

    public override string Name => "run_csharp";
    public override string Category => "Nâng cao";
    public override string Description => """
        Chạy đoạn mã C# (Revit API) trong Revit khi không có lệnh chuyên dụng phù hợp.
        Mã là THÂN một phương thức, có sẵn biến: doc (Document), uidoc (UIDocument), uiapp (UIApplication), print(object) để in.
        Đã có using: System, System.Linq, System.Collections.Generic, System.Text, Autodesk.Revit.DB (+ .Architecture, .Structure), Autodesk.Revit.UI.
        Cần thêm thì viết dòng 'using X;' ở đầu mã; dùng được mọi thư viện đang nạp trong Revit (WPF, IFC, add-in khác...).
        Dùng 'return <giá trị>;' để trả kết quả (Element và danh sách Element được tóm tắt tự động).
        KHÔNG tự tạo Transaction: host đã mở sẵn.
        mode="read" (mặc định): mọi thay đổi bị hủy sau khi chạy, an toàn để khảo sát.
        mode="write": giữ thay đổi, chạy ngay không cần duyệt; 'summary' (nên có) là tên bước trong Undo của Revit.
        Revit lưu chiều dài bằng feet (1 ft = 304.8 mm).
        """;
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "code": { "type": "string", "description": "Thân phương thức C#." },
            "mode": { "type": "string", "enum": ["read", "write"], "description": "Mặc định read." },
            "summary": { "type": "string", "description": "Mô tả ngắn việc mã làm, hiện trong Undo của Revit." }
          },
          "required": ["code"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Manual;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var code = Args.GetString(arguments, "code");
        var write = Args.GetStringOrDefault(arguments, "mode", "read") switch
        {
            "read" => false,
            "write" => true,
            var other => throw new CommandException($"mode '{other}' không hợp lệ, dùng read hoặc write."),
        };
        var summary = Args.GetStringOrDefault(arguments, "summary");

        var (entry, loadContext) = ScriptCompiler.Compile(code);
        try
        {
            return Run(context, entry, write, summary);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static object Run(RevitCommandContext context, ScriptEntry entry, bool write, string? summary)
    {
        var doc = context.Document;
        var output = new List<string>();
        void Print(object? value)
        {
            if (output.Count < MaxItems) output.Add(value?.ToString() ?? "null");
        }

        // summary làm tên bước trong Undo để người dùng biết hoàn tác cái gì.
        using var transaction = new Transaction(doc, string.IsNullOrWhiteSpace(summary) ? "InnoPro: run_csharp" : "InnoPro: " + summary.Trim());
        TransactionHelper.SuppressWarnings(transaction);
        transaction.Start();
        object? result;
        try
        {
            result = entry(doc, context.UIDocument, context.UIApplication, Print);
        }
        catch (Exception ex)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            throw new CommandException($"Mã bị lỗi khi chạy: {inner.GetType().Name}: {inner.Message}" +
                                       (output.Count > 0 ? "\nĐã in:\n" + string.Join("\n", output) : ""));
        }

        string status;
        if (write)
        {
            status = transaction.Commit() == TransactionStatus.Committed ? "committed" : "rolled_back";
        }
        else
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            status = "read_only";
        }

        return new { status, result = Simplify(result, depth: 0), output };
    }


    /// <summary>Biến kết quả thành dạng JSON an toàn: Element thì tóm tắt, danh sách thì giới hạn.</summary>
    private static object? Simplify(object? value, int depth)
    {
        switch (value)
        {
            case null: return null;
            case Enum: return value.ToString();
            case string or bool or char: return value;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal: return value;
            case ElementId id: return id.Value;
            case Element e: return new { element_id = e.Id.Value, category = e.Category?.Name, name = e.Name };
            case XYZ p:
                return new { x_mm = UnitConvert.FeetToMm(p.X), y_mm = UnitConvert.FeetToMm(p.Y), z_mm = UnitConvert.FeetToMm(p.Z) };
            case IDictionary dict when depth < 3:
                var map = new Dictionary<string, object?>();
                foreach (DictionaryEntry kv in dict)
                {
                    if (map.Count >= MaxItems) break;
                    map[kv.Key.ToString() ?? ""] = Simplify(kv.Value, depth + 1);
                }
                return map;
            case IEnumerable items when depth < 3:
                return items.Cast<object?>().Take(MaxItems).Select(i => Simplify(i, depth + 1)).ToList();
        }

        // Kiểu ẩn danh (new { a, b }) thì giữ cấu trúc, kiểu khác thì lấy ToString để tránh duyệt đồ thị Revit khổng lồ.
        if (depth < 3 && value.GetType().Name.Contains("AnonymousType"))
            return value.GetType().GetProperties().ToDictionary(p => p.Name, p => Simplify(p.GetValue(value), depth + 1));
        return value.ToString();
    }
}
