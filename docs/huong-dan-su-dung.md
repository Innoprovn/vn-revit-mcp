# Hướng dẫn sử dụng vn-revit-mcp

Dành cho người dùng Revit, không cần biết lập trình.

## 1. Cài đặt (2 phút)

1. Mở **PowerShell** (bấm phím Windows, gõ `powershell`, Enter).
2. Dán lệnh sau rồi Enter:

   ```powershell
   irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex
   ```

3. Mở Revit 2025 hoặc 2026 (đang mở thì đóng rồi mở lại). Trên ribbon có tab **InnoPro**.

Không muốn dùng PowerShell: tải file zip ở trang [Releases](https://github.com/Innoprovn/vn-revit-mcp/releases),
giải nén, bấm đúp **Cai-dat.cmd**.

Cập nhật về sau: bấm nút **Cập nhật** trên tab InnoPro.

## 2. Dùng các nút trên tab InnoPro

| Nhóm | Dùng để |
|---|---|
| **AI** | Chat AI, xem lệnh, bật/tắt MCP, nạp lại lệnh, kết nối AI, cập nhật |
| **Truy vấn** | Thống kê mô hình, xem tham số phần tử |
| **Tham số** | Sửa một tham số trên nhiều phần tử |
| **QA-QC** | Cảnh báo, phần tử trùng, tham số còn trống, va chạm |
| **Dữ liệu** | Đọc/xuất schedule ra CSV, khối lượng vật liệu |
| Các nhóm khác | Lệnh ít dùng hơn nằm trong nút **Lệnh khác** của mỗi nhóm |

Khi bấm một nút:
- Nếu lệnh cần chọn phần tử, nó dùng **phần tử đang chọn**; chưa chọn gì thì Revit cho bạn quét chọn (bấm **Finish** khi xong).
- Nếu lệnh cần thông số, một **form** hiện ra (tầng, category, type có sẵn danh sách để chọn). Ô để trống là dùng mặc định.
- Kết quả hiện trong một cửa sổ: **Chọn … phần tử trong Revit** để chọn và zoom tới các phần tử trong kết quả, **Sao chép** để dán sang Excel/Zalo.

Mọi thay đổi mô hình đều **Undo được** (Ctrl+Z).

## 3. Chat AI trong Revit

1. Mở một dự án, bấm **Chat AI**. Khung **InnoPro AI** hiện bên cạnh.
2. Chọn nhà cung cấp:

| Nhà cung cấp | Cần gì | Ghi chú |
|---|---|---|
| Claude (Anthropic) | API key từ [console.anthropic.com](https://console.anthropic.com) | Mạnh nhất cho việc nhiều bước |
| OpenAI | API key từ [platform.openai.com](https://platform.openai.com) | Bấm ↻ để chọn model |
| Google Gemini | API key từ [aistudio.google.com](https://aistudio.google.com) | Có gói miễn phí |
| OpenRouter | API key từ [openrouter.ai](https://openrouter.ai) | Một key dùng được nhiều model |
| Ollama / LM Studio | Cài chương trình trên máy | **Dữ liệu không rời khỏi máy**, không cần key |

3. Bấm **Key**, dán API key, **Lưu**. Key được cất trong Windows Credential Manager, không ghi ra file.
4. Gõ câu hỏi, Enter. Ví dụ:
   - *Mô hình có bao nhiêu cột, cột nào bị trùng?*
   - *Đặt Comments = "Kiểm tra" cho tất cả dầm tầng 3.*
   - *Bóc khối lượng bê tông sàn theo từng tầng.*

AI sẽ hỏi lại trước khi sửa mô hình với số lượng lớn.

## 4. Dùng AI bên ngoài (Claude Desktop, Cursor, VS Code...)

Bộ cài đã tự kết nối các AI có trên máy. Kiểm tra bằng nút **Kết nối AI** trên tab InnoPro.
Khởi động lại ứng dụng AI sau khi cài. Trong Claude Desktop, gõ `/` để thấy các **công thức** làm sẵn
(kiểm tra chất lượng mô hình, bóc khối lượng, dựng tầng và lưới trục, viết lệnh mới).

## 5. Gặp lỗi

| Hiện tượng | Cách xử lý |
|---|---|
| AI báo "Revit chưa mở dự án nào" | Revit chỉ nhận lệnh khi đã mở một dự án. Mở dự án rồi hỏi lại |
| AI báo "Revit không phản hồi kịp" | Revit đang bận hoặc đang mở hộp thoại. Đóng hộp thoại, đợi Revit rảnh rồi thử lại |
| AI báo "Đang mở nhiều phiên Revit" | Đóng bớt Revit, hoặc đặt biến môi trường `VN_REVIT_MCP_PID` |
| Không thấy tab InnoPro | Chạy lại lệnh cài, rồi mở lại Revit |
| AI không thấy Revit | Bấm **Kết nối AI** → *Kết nối tất cả*, rồi khởi động lại ứng dụng AI |
| Muốn tắt quyền điều khiển của AI | Bấm **Bật/Tắt MCP** |

Log của add-in: `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\logs`. Báo lỗi tại
[GitHub Issues](https://github.com/Innoprovn/vn-revit-mcp/issues).
