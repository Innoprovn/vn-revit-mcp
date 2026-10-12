---
name: kiem-tra-chat-luong
description: Kiểm tra chất lượng mô hình Revit đang mở và lập báo cáo kèm đề xuất sửa.
arguments:
  - category: Chỉ kiểm tra một category (ví dụ OST_StructuralColumns). Bỏ trống = cả mô hình.
  - parameters: Các tham số bắt buộc phải có giá trị, cách nhau dấu phẩy (ví dụ Mark, Comments).
---
Kiểm tra chất lượng mô hình Revit đang mở. Phạm vi category: {{category}}. Tham số bắt buộc: {{parameters}}.

Làm theo thứ tự, mỗi bước một lần gọi, KHÔNG sửa gì trong mô hình:
1. get_document_info và analyze_model_statistics để nắm quy mô.
2. get_warnings: lấy 15 loại cảnh báo nhiều nhất.
3. find_duplicate_elements (theo category nếu có chỉ định).
4. Nếu có chỉ định tham số bắt buộc: check_missing_parameters cho category đó.

Sau đó viết báo cáo ngắn bằng tiếng Việt:
- Bảng tổng hợp: hạng mục, số lượng, mức độ (nghiêm trọng / nên sửa / chấp nhận được).
- 3 đến 5 việc nên làm trước, kèm lệnh vn-revit-mcp có thể dùng để sửa.
- Hỏi người dùng có muốn chọn (select_elements) các phần tử lỗi trong Revit hoặc sửa tự động không. Chỉ sửa khi người dùng đồng ý.
