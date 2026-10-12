using System.Collections;
using System.Reflection;

namespace InnoPro.Revit.Sdk;

/// <summary>
/// Danh sách bản ghi dạng bảng gọn: tên cột ghi một lần, mỗi hàng là một mảng giá trị.
/// Tiết kiệm khoảng một nửa token so với mảng object khi trả về cho AI.
/// </summary>
public sealed class Table
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>Tổng số bản ghi trước khi cắt theo limit (null nếu không cắt).</summary>
    public int? Total { get; init; }

    /// <summary>
    /// Tạo bảng từ danh sách object (thường là kiểu ẩn danh). Cột lấy theo thuộc tính của phần tử đầu tiên.
    /// </summary>
    public static Table Of<T>(IEnumerable<T> items, int? limit = null)
    {
        var list = items.ToList();
        var properties = typeof(T) == typeof(object) && list.Count > 0
            ? list[0]!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            : typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var taken = limit is { } l && list.Count > l ? list.Take(l).ToList() : list;
        return new Table
        {
            Columns = properties.Select(p => p.Name).ToList(),
            Rows = taken.Select(item => properties.Select(p => Flatten(p.GetValue(item))).ToArray()).ToList(),
            Total = taken.Count < list.Count ? list.Count : null,
        };
    }

    // Danh sách lồng (ví dụ id phần tử) cần được liệt kê ngay để serialize ổn định.
    private static object? Flatten(object? value) =>
        value is IEnumerable sequence and not string ? sequence.Cast<object?>().ToList() : value;
}
