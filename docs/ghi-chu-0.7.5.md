## vn-revit-mcp 0.7.5

Các sửa lỗi và lệnh mới phát hiện khi chạy thử toàn bộ khóa học trên mô hình thật:

**Lệnh mới**
- `link_cad`: link DWG/DXF vào mặt bằng (đơn vị, vị trí đặt); Link giữ chữ gốc cho các lệnh dựng hình từ CAD.
- `open_document`, `close_document`: mở/chuyển tài liệu đang làm việc, đóng tài liệu thừa.

**Sửa lỗi**
- Lệnh chạy dự phòng trong sự kiện Idling chỉ khi đã chờ quá 2 giây: hết lỗi "Setting active view is temporarily disabled" và lỗi đổi tài liệu.
- `find_elements`: hỏi "Length" của dầm tự lấy "Cut Length"; tham số không có ghi rõ "(không có tham số này)".
- `find_clashes`: lọc theo tầng đúng với dầm (trước đây xét 0 dầm).
- Xuất PDF: tên file đúng "số hiệu - tên sheet" như lệnh báo lại.
- Sàn theo dầm: khép ô qua tâm cột với dầm dựng từ CAD (dầm chạy mặt cột tới mặt cột).
- Vách từ CAD: tự tạo type đúng bề dày khi không chỉ định type mẫu.
- Tên type tự tạo trung tính: "Tường 200mm", "Vách 250mm".
- `new_project`: ưu tiên template hệ mét; lưu lỗi thì không để sót dự án chưa lưu.
- Nút Cập nhật: cài nền bằng bộ tự cập nhật, Revit tự nạp bản mới không cần tắt.
- Doctor không còn báo nhầm phiên bản; bộ cài báo đúng việc nạp nóng.
