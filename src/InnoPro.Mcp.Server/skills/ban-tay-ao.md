---
name: ban-tay-ao
title: Bàn tay ảo: chạy lệnh có sẵn của Revit và bấm vào view như người dùng
keywords: bàn tay ảo, click, bấm, chuột, view, dim, dimension, vẽ, tool, không có api, ui, giao diện, aligned, tab
verified: Revit 2026, 13/10/2026 (Aligned Dimension giữa 2 tường: 3041 mm bám đúng 2 tường; vẽ tường 2 điểm đúng tọa độ; Revit bị cửa sổ khác che vẫn bấm được)
---
## Khi nào dùng
Việc mà API không làm được hoặc làm rất vòng vèo, nhưng người dùng làm được bằng vài cú bấm trên view
(dim kiểu đặc biệt, công cụ chỉ có trên giao diện...). Có lệnh InnoPro hoặc API thì dùng cái đó trước.

## Cách làm
1. Mở đúng view (open_view). Bàn tay ảo bấm trên view đang mở.
2. revit_ui_find để lấy mã lệnh (hoặc dùng tên PostableCommand: AlignedDimension, ArchitecturalWall, DetailLine...).
3. Xem trước điểm sẽ bấm: revit_ui_look marks=[...] trả ảnh view đúng như người dùng thấy, điểm đánh số đỏ.
4. revit_ui_post id=... clicks=[...]:
   - Điểm bấm vào phần tử: {element_id} — tự chọn chỗ "thoáng" trên phần tử (xa cửa, lỗ mở, tường giao, trục cắt ngang)
     để Revit bắt đúng phần tử. Muốn đúng vị trí: {element_id, at: "center"|"start"|"end"} hoặc {x, y, z} mm.
   - Điểm đặt (vd vị trí đường dim): {x, y} mm ở chỗ trống.
   - Tự phóng to vùng các điểm (zoom, mặc định bật) cho mỗi pixel ~10 mm, đỡ bắt nhầm.
   - Xong tự bấm Esc 2 lần (finish) để kết thúc lệnh.
5. revit_ui_result: phần tử đã tạo/sửa/xóa; dim có kèm giá trị và phần tử được bám để tự kiểm tra.

## Bẫy
- Bấm vào tọa độ trùng chỗ có lỗ mở/cửa: dim bám vào lỗ mở thay vì tường. Dùng {element_id} (tự né) hoặc thêm tab: 1 vào
  điểm đó (như người dùng bấm Tab để đổi đối tượng được bắt), rồi xem lại bằng revit_ui_result.
- Bấm sai: xóa phần tử vừa tạo (delete_elements) rồi làm lại, không để rác trong mô hình.
- View thu nhỏ quá (mỗi pixel vài chục mm) thì hay bắt nhầm: để zoom mặc định.
- Mặc định gửi thẳng vào cửa sổ view (mode "message"): không chiếm chuột người dùng, Revit bị che vẫn bấm được.
  Chỉ khi lệnh không phản ứng mới thử mode "input" (dùng chuột thật, phải báo người dùng trước vì chuột sẽ tự di chuyển).
- Hộp thoại hiện ra trong lúc chạy: revit_ui_result liệt kê; muốn tự trả lời thì truyền dialog: "ok"|"cancel"|"yes"|"no".

## Kiểm tra
revit_ui_result (giá trị, phần tử bám) và revit_ui_look (ảnh view thật) hoặc capture_view (ảnh khoanh vùng, tô đỏ).
