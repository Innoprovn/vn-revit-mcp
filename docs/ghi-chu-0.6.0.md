## vn-revit-mcp 0.6.0

**Tự cập nhật**
- MCP server (khi AI mở) và add-in (khi Revit mở) tự kiểm tra bản mới trên GitHub, tối đa 12 giờ/lần, chạy ngầm.
- Tải gói, kiểm mã SHA-256 do GitHub công bố, cài bằng bộ cài (xử lý Revit đang mở, ứng dụng MSIX). Add-in mới có hiệu lực khi mở lại Revit.
- Tiện ích marketplace đã cài (vd Mô phỏng 4D) cũng tự lên bản mới, kiểm SHA-256 và nạp lại ngay trong Revit.
- Vừa cập nhật thì AI được báo ở lần gọi tool kế tiếp.
- `vn-revit-mcp.exe update` cập nhật ngay; `doctor` có mục "Tự cập nhật"; tắt bằng `VN_REVIT_MCP_AUTO_UPDATE=0`.

**Chat trong Revit**
- Có thêm `revit_skill` (cách làm đã kiểm chứng) và `revit_api` (tra Revit API + bài The Building Coder), dùng chung với MCP server.
- Lệnh dòng lệnh mới: `vn-revit-mcp.exe skill --query ...`, `vn-revit-mcp.exe api --query ...`.

Từ bản này trở đi không cần chạy lại lệnh cài để cập nhật.
