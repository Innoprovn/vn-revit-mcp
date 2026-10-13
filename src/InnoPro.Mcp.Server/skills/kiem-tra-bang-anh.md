---
name: kiem-tra-bang-anh
title: Tự kiểm tra kết quả bằng ảnh view (thay cho chụp màn hình)
keywords: xem, kiểm tra, ảnh, hình, capture, screenshot, verify, nhìn
verified: Revit 2026, 13/10/2026 (ảnh mặt bằng 900 px ~ 600 token, phần tử tô đỏ)
---
## Cách làm
- Sau khi tạo/sửa (dim, tag, cột, cửa...): capture_view với view và element_ids vừa tạo. Ảnh trả thẳng về cho AI.
- Khoanh vùng tự động quanh phần tử (padding_mm, mặc định 1500) và tô đỏ chúng; view gốc không bị đổi.
- pixel_size 700–1000 là đủ đọc; ảnh lớn tốn token hơn (≈ rộng × cao / 750).

## Khi nào KHÔNG cần ảnh
Kiểm số liệu (đếm, kích thước, khoảng cách) bằng find_elements fields hoặc kết quả lệnh: rẻ hơn nhiều.
Dùng ảnh khi cần thấy bố cục: chữ có đè nhau, dim đặt đúng chỗ, cấu kiện có lệch.

## Bẫy
- View 3D: khoanh bằng section box; view mặt bằng/mặt cắt: crop box (cắt cả chú thích).
- Phần tử không nằm trên view đó thì không tô được (vd cửa ở tầng khác).