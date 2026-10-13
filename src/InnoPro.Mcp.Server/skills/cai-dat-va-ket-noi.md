---
name: cai-dat-va-ket-noi
title: Cài đặt, kiểm tra và sửa kết nối vn-revit-mcp
keywords: cài, install, kết nối, lỗi, doctor, không thấy revit, mcp
verified: Windows 11, Claude Code MSIX, 13/10/2026
---
- Cài: irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex
- Kiểm tra: "%LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe" doctor --json; chạy từng fix,
  mục "NGƯỜI DÙNG:" (mở lại Revit...) thì báo người dùng.
- revit_status lỗi kết nối: Revit chưa mở dự án, hoặc đang bận (hộp thoại, add-in khác chạy việc nặng).
- Nhiều phiên Revit: đặt biến môi trường VN_REVIT_MCP_PID cho MCP server.

## Cập nhật
- Tự động: server (khi AI mở) và add-in (khi Revit mở) kiểm tra GitHub tối đa 12 giờ/lần, tải gói, kiểm SHA-256, cài bằng bộ cài. Tiện ích marketplace đã cài cũng tự lên bản mới và nạp lại ngay.
- Revit đang mở tự nạp add-in mới khi rảnh (từ 0.7.0, không cần mở lại Revit); server mới khi mở lại ứng dụng AI. Khi vừa cập nhật, kết quả tool kèm dòng "vn-revit-mcp vừa tự cập nhật": báo người dùng.
- Cập nhật ngay: `vn-revit-mcp.exe update`. Xem tình trạng: `vn-revit-mcp.exe doctor` (mục "Tự cập nhật"). Tắt: biến môi trường `VN_REVIT_MCP_AUTO_UPDATE=0`.

## Mở nhiều Revit cùng lúc
- Server tự làm việc với Revit người dùng thao tác gần nhất (cửa sổ trên cùng) và giữ nguyên phiên đó.
- revit_status liệt kê mọi phiên (pid, phiên bản, tài liệu). Người dùng muốn làm trên phiên khác: revit_status pid=... rồi làm tiếp.
