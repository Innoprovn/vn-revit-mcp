---
name: viet-lenh-moi
description: Nhờ AI viết một lệnh Revit mới (có nút trên ribbon, AI khác cũng gọi được) chỉ từ mô tả bằng lời.
arguments:
  - yeu_cau*: Lệnh cần làm gì, ví dụ "đánh số Mark cho cột theo thứ tự trục A1, A2...".
  - nhom: Tên nhóm trên ribbon (ví dụ Kết cấu, Rebar). Mặc định "Của tôi".
---
Viết một lệnh vn-revit-mcp mới theo yêu cầu: {{yeu_cau}}. Nhóm: {{nhom}}.

Trước khi viết:
- Gọi find_commands với từ khóa liên quan. Nếu đã có lệnh làm được việc này, dùng luôn và dừng ở đây.

Viết lệnh dạng MỘT file C#:
- Lớp `public sealed class ... : RevitCommandBase` (namespace tùy ý), using `System.Linq`, `System.Text.Json`, `Autodesk.Revit.DB`, `InnoPro.Revit.Sdk`.
- `Name` snake_case duy nhất; `Category` = nhóm; `Description` tiếng Việt, câu đầu nói đủ ý.
- `InputSchema` JSON Schema; dùng tên tham số quen thuộc để form tự sinh có gợi ý: element_ids, element_id, level, category, wall_type, floor_type, family_type.
- Sửa mô hình thì `TransactionMode => TransactionMode.Auto` và KHÔNG tự tạo Transaction.
- Thêm `Ribbon => new("Tên\nnút", "ký hiệu")` để có nút lớn trên ribbon.
- Danh sách trả về dùng `Table.Of(...)`; chiều dài vào/ra bằng mm (`UnitConvert.MmToFeet`, `UnitConvert.FeetToMm`).
- Tìm theo tên: `Lookup.Level(doc, name)`, `Lookup.Type<WallType>(doc, name)`, `CategoryResolver.Resolve(doc, name)`.
- Lỗi do người dùng: `throw new CommandException("...")`.

Lưu file vào `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Scripts\<nhóm>\<TenLenh>.cs` (nếu không ghi file được, đưa toàn bộ mã cho người dùng
kèm đường dẫn để họ tự lưu). Gọi reload_commands; nếu có errors thì sửa và nạp lại tới khi sạch lỗi.
Cuối cùng chạy thử lệnh một lần ở chế độ an toàn (đọc, hoặc trên vài phần tử) và hướng dẫn người dùng nút mới nằm ở đâu trên tab InnoPro.
