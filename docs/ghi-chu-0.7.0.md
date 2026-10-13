## vn-revit-mcp 0.7.0

**Nạp nóng toàn bộ add-in, không cần tắt Revit**
- Add-in tách thành bộ nạp rất nhỏ (Revit nạp trực tiếp) và lõi (cầu nối, ribbon, chat, bộ chạy lệnh, SDK, Roslyn).
- Có bản mới (tự cập nhật, cài lại, hoặc build lại khi phát triển): Revit đang mở tự đổi sang lõi mới khi rảnh, vài giây, giữ nguyên ribbon và khung chat. Lõi mới lỗi thì tự quay lại lõi cũ.
- Từ bản này trở đi không cần mở lại Revit để cập nhật. Máy đang chạy bản cũ hơn cần mở lại Revit một lần.

**Lệnh mới**
- `color_elements`: tô màu phần tử trên view theo id, category hoặc theo giá trị tham số (kèm bảng chú giải).
- `view_visibility`: cô lập, ẩn, hiện, bỏ cô lập phần tử trên view.
- `create_rooms`: tạo phòng tự động trong mọi vùng kín của tầng, gắn tag, đặt tên.

**Sửa lỗi**
- Bộ cài chạy qua Explorer (ứng dụng MSIX) với đường dẫn `-Source` tương đối.
