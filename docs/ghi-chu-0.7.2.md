## vn-revit-mcp 0.7.2

- Mở nhiều Revit cùng lúc: không còn báo lỗi bắt đặt biến môi trường. Server tự làm việc với Revit bạn thao tác gần nhất và giữ nguyên phiên đó.
  `revit_status` liệt kê các phiên; nói với AI "làm trên Revit 2025" là AI tự chuyển (`revit_status pid=...`).
- Kết quả trả về giữ nguyên tiếng Việt (không mã hóa thành mã \u), đỡ token.
