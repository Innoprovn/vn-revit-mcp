---
name: boc-khoi-luong
description: Bóc khối lượng vật liệu (m³, m²) theo category và tầng, trình bày thành bảng.
arguments:
  - category: Category cần bóc (ví dụ OST_StructuralColumns, OST_Floors). Bỏ trống = mọi cấu kiện.
  - level: Tầng cần bóc. Bỏ trống = toàn bộ, chia theo từng tầng.
  - csv_path: Đường dẫn file CSV muốn xuất (tùy chọn).
---
Bóc khối lượng vật liệu trong mô hình Revit đang mở. Category: {{category}}. Tầng: {{level}}.

1. Nếu không chỉ định tầng: gọi list_levels, rồi get_material_quantities cho từng tầng (cùng category).
   Nếu có chỉ định tầng: gọi get_material_quantities một lần.
2. Trình bày bảng: tầng, vật liệu, thể tích (m³), diện tích (m²), số cấu kiện; thêm dòng tổng cho mỗi vật liệu.
3. Ghi chú các giả định: khối lượng lấy từ hình học Revit, chưa trừ giao cắt nếu mô hình chưa join.
4. Nếu có csv_path ({{csv_path}}): đề nghị người dùng tạo schedule tương ứng rồi dùng read_schedule với csv_path để xuất,
   hoặc trình bày bảng dạng CSV để người dùng tự lưu.
