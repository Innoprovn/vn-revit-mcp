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
/// Nút lớn trên tab InnoPro. Lệnh không khai báo vẫn có mặt trong menu thả xuống của nhóm.
/// </summary>
/// <param name="Text">Chữ trên nút, dùng \n để xuống dòng.</param>
/// <param name="Glyph">Tên icon vector trong bộ InnoPro (ví dụ "wall", "warning", "table"); bỏ trống thì dùng icon của nhóm.</param>
/// <param name="Panel">Nhóm trên ribbon, mặc định là Category của lệnh.</param>
public sealed record RibbonButton(string Text, string? Glyph = null, string? Panel = null);

/// <summary>
/// Một lệnh Revit, dùng được theo hai cách: người bấm nút trên ribbon, hoặc AI gọi qua MCP.
/// Lớp cài đặt phải có constructor không tham số.
/// </summary>
public interface IRevitCommand
{
    /// <summary>Tên tool, dạng snake_case, duy nhất trong toàn bộ các CommandSet.</summary>
    string Name { get; }

    /// <summary>Mô tả cho AI và tooltip cho người dùng: lệnh làm gì, khi nào dùng, trả về gì.</summary>
    string Description { get; }

    /// <summary>Nhóm lệnh, dùng cho ribbon và danh mục (ví dụ "Truy vấn", "Rebar").</summary>
    string Category { get; }

    /// <summary>JSON Schema (kiểu object) mô tả tham số. Host tự sinh form nhập từ schema này khi người dùng bấm nút.</summary>
    string InputSchema { get; }

    TransactionMode TransactionMode { get; }

    /// <summary>Nút lớn trên ribbon; null thì lệnh chỉ nằm trong menu thả xuống.</summary>
    RibbonButton? Ribbon => null;

    /// <summary>
    /// Lệnh hay dùng: luôn hiện thẳng cho AI. Lệnh khác AI tra qua find_commands để tiết kiệm token.
    /// </summary>
    bool Pinned => false;

    /// <summary>
    /// Khi người dùng bấm nút: tự hỏi tham số (chọn phần tử, chọn điểm, form riêng...).
    /// Trả null để host tự sinh form từ InputSchema. Ném OperationCanceledException nếu người dùng hủy.
    /// </summary>
    object? PromptArguments(RevitCommandContext context) => null;

    /// <summary>
    /// Chạy trên luồng chính của Revit. Giá trị trả về được serialize sang JSON (snake_case)
    /// ngay trên luồng này, nên được phép trả về IEnumerable lười chạm vào Revit API.
    /// </summary>
    object? Execute(RevitCommandContext context, JsonElement arguments);
}

/// <summary>
/// Lớp cơ sở tiện dụng: mặc định không tham số, không Transaction, không nút lớn.
/// </summary>
public abstract class RevitCommandBase : IRevitCommand
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Category => "Chung";
    public virtual string InputSchema => """{"type":"object","properties":{}}""";
    public virtual TransactionMode TransactionMode => TransactionMode.None;
    public virtual RibbonButton? Ribbon => null;
    public virtual bool Pinned => false;
    public virtual object? PromptArguments(RevitCommandContext context) => null;
    public abstract object? Execute(RevitCommandContext context, JsonElement arguments);
}
