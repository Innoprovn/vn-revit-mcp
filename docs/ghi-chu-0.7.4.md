## vn-revit-mcp 0.7.4

- Lệnh mới `save_document`: lưu dự án, hoặc lưu thành file mới (vd sau khi nâng cấp Revit 2025 lên 2026), mặc định không ghi đè.
- `open_view` không còn lỗi "Setting active view is temporarily disabled": tự nhờ Revit đổi view ngay sau lượt lệnh.
- Đã chạy thử trên mô hình kết cấu thật 175 MB (34 tầng): nâng cấp lên 2026, lập 4D 34 công việc / 6.474 cấu kiện từ file mốc tiến độ, thống kê, kiểm tra mô hình, tô màu, dim trục, bàn tay ảo.
