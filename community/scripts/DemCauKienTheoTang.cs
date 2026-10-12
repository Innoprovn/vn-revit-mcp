// Ví dụ lệnh viết bằng MỘT file .cs, không cần project hay build.
// Cách dùng: chép file vào %LOCALAPPDATA%\InnoPro\vn-revit-mcp\Scripts\
// rồi bấm "Nạp lại lệnh" trên tab InnoPro (hoặc bảo AI gọi reload_commands).
// Lệnh xuất hiện thành nút trên ribbon, và AI gọi được bằng tên count_by_level.

using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

public sealed class CountByLevel : RevitCommandBase
{
    // Tên AI dùng để gọi: snake_case, duy nhất.
    public override string Name => "count_by_level";

    // Nhóm trên ribbon và trong danh mục lệnh.
    public override string Category => "Ví dụ";

    // Viết cho AI đọc: làm gì, khi nào dùng, trả về gì.
    public override string Description =>
        "Đếm cấu kiện theo tầng cho một category (ví dụ cột, dầm, tường). Trả về bảng tầng và số lượng.";

    // Tham số dạng JSON Schema. Người bấm nút sẽ thấy một form tự sinh từ đây
    // (tham số tên "category" và "level" còn có sẵn danh sách gợi ý).
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Ví dụ OST_StructuralColumns, OST_Walls, hoặc tên hiển thị." }
          },
          "required": ["category"]
        }
        """;

    // Có dòng này thì lệnh thành nút lớn trên ribbon; bỏ đi thì nằm trong menu "Lệnh khác".
    public override RibbonButton Ribbon => new("Đếm theo\ntầng", "#");

    public override object Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var category = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category"));

        var rows = new FilteredElementCollector(doc)
            .OfCategoryId(category.Id)
            .WhereElementIsNotElementType()
            .GroupBy(e => ElementInfo.LevelName(doc, e) ?? "(không có tầng)")
            .Select(g => new { level = g.Key, count = g.Count() })
            .OrderBy(r => r.level);

        // Table.Of trả bảng gọn {columns, rows}: tiết kiệm token cho AI, hiển thị đẹp cho người dùng.
        return Table.Of(rows);
    }
}
