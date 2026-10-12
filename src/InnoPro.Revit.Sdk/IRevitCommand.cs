using System.Text.Json;

namespace InnoPro.Revit.Sdk;

/// <summary>
/// Cách host bọc lệnh trong Transaction của Revit.
/// </summary>
public enum TransactionMode
{
    /// <summary>Lệnh chỉ đọc mô hình hoặc chỉ thao tác giao diện (chọn, zoom...).</summary>
    None,

    /// <summary>Host tự mở một Transaction, commit khi thành công và rollback khi lỗi.</summary>
    Auto,

    /// <summary>Lệnh tự quản lý Transaction / TransactionGroup.</summary>
    Manual,
}

/// <summary>
/// Một lệnh Revit. Mỗi lệnh được công bố cho AI thành một tool MCP cùng tên.
/// Lớp cài đặt phải có constructor không tham số.
/// </summary>
public interface IRevitCommand
{
    /// <summary>Tên tool, dạng snake_case, duy nhất trong toàn bộ các CommandSet.</summary>
    string Name { get; }

    /// <summary>Mô tả cho AI: lệnh làm gì, khi nào dùng, kết quả trả về là gì.</summary>
    string Description { get; }

    /// <summary>Nhóm lệnh, dùng cho ribbon và danh mục (ví dụ "Truy vấn", "Rebar").</summary>
    string Category { get; }

    /// <summary>JSON Schema (kiểu object) mô tả tham số.</summary>
    string InputSchema { get; }

    TransactionMode TransactionMode { get; }

    /// <summary>
    /// Chạy trên luồng chính của Revit. Giá trị trả về được serialize sang JSON (snake_case)
    /// ngay trên luồng này, nên được phép trả về IEnumerable lười chạm vào Revit API.
    /// </summary>
    object? Execute(RevitCommandContext context, JsonElement arguments);
}

/// <summary>
/// Lớp cơ sở tiện dụng: mặc định không tham số, không Transaction.
/// </summary>
public abstract class RevitCommandBase : IRevitCommand
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Category => "Chung";
    public virtual string InputSchema => """{"type":"object","properties":{}}""";
    public virtual TransactionMode TransactionMode => TransactionMode.None;
    public abstract object? Execute(RevitCommandContext context, JsonElement arguments);
}
