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