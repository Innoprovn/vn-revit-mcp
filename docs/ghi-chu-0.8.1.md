## vn-revit-mcp 0.8.1

- run_csharp: AI gửi mã sửa mô hình (mode=write) là chạy ngay, không còn hộp thoại bắt bấm "Cho chạy". Mỗi lần chạy là một bước Undo mang tên việc AI làm, bấm Ctrl+Z để hoàn tác.
- run_csharp: viết được dòng `using` ở đầu mã và dùng được mọi thư viện đang nạp trong Revit (WPF, IFC, add-in khác...), không chỉ System và Revit API.
