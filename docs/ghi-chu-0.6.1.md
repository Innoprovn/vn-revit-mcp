## vn-revit-mcp 0.6.1

**Bàn tay ảo**: AI dùng được cả những việc chỉ làm được trên giao diện Revit.
- `revit_ui_post` thêm `clicks`: chạy lệnh có sẵn của Revit rồi bấm vào view như người dùng (điểm mm hoặc element_id), kết thúc bằng Esc.
  - Bấm theo phần tử tự chọn chỗ thoáng (né cửa, lỗ mở, tường giao, trục) để Revit bắt đúng phần tử.
  - Tự phóng to vùng bấm; `tab` để đổi đối tượng được bắt như bấm phím Tab; tự trả lời hộp thoại nếu muốn.
  - Gửi thẳng vào cửa sổ view: không chiếm chuột người dùng, Revit bị che vẫn chạy.
- `revit_ui_result`: phần tử đã tạo/sửa/xóa, dim kèm giá trị và phần tử được bám, hộp thoại đã hiện.
- `revit_ui_look`: ảnh view đúng như người dùng đang thấy (kể cả khi bị che), đánh dấu điểm sắp bấm.
- Kỹ năng mới `ban-tay-ao` trong `revit_skill`.

Đã kiểm chứng trên Revit 2026: Aligned Dimension giữa hai tường (3041 mm, bám đúng hai tường), vẽ tường đúng tọa độ.
Máy đã có 0.6.0 sẽ tự cập nhật lên bản này.
