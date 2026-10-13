---
name: lenh-co-san-cua-revit
title: Dùng lệnh có sẵn của Revit khi không có API hoặc lệnh InnoPro
keywords: ribbon, nút, lệnh revit, post command, giao diện, ui, không có api, bấm nút
verified: Revit 2026, 13/10/2026 (đọc 1.123 nút ribbon; post Default 3D View đổi view thành công)
---
## Cách làm
1. revit_ui_find query="..." để tìm nút: trả tab, panel, mã lệnh (ID_...), có kích hoạt được không. Tìm được cả tiếng Việt không dấu.
2. revit_ui_post id="ID_..." (hoặc tên PostableCommand như "AlignedDimension"): Revit chạy lệnh ngay sau lượt hiện tại,
   như người dùng bấm nút. Lệnh cần người click trên màn hình thì báo người dùng thao tác tiếp.
3. revit_ui_state để xem view đang mở, vùng chọn, hộp thoại đang hiện (vài chục token).

## Thứ tự ưu tiên
Lệnh InnoPro (find_commands) → Revit API qua run_csharp (tra revit_api trước) → lệnh có sẵn của Revit (revit_ui_post).

## Bẫy
- CanPostCommand sai khi view hoặc vùng chọn không phù hợp (vd lệnh dim cần view có thể dim).
- Lệnh post chạy SAU lượt hiện tại: muốn đọc kết quả thì gọi lệnh khác ở lượt sau.