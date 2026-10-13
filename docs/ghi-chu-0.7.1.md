## vn-revit-mcp 0.7.1

- Bộ cài ghi sẵn lựa chọn "Always Load" cho vn-revit-mcp trên các bản Revit trong máy: Revit không còn hỏi "Load Once" khi mở lần đầu
  (add-in chưa ký số). Bộ cài báo rõ việc này; muốn Revit hỏi như cũ thì cài với `-NoTrust`. Gỡ cài đặt sẽ xóa lựa chọn này.
- Chuẩn bị ký số miễn phí qua SignPath Foundation: chính sách ký số (docs/code-signing-policy.md), workflow ký trên GitHub Actions
  (tự bật khi được duyệt).
