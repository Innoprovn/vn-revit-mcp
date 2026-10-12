# Hướng dẫn cho AI lập trình (Claude Code, Cursor, Copilot, Codex, Gemini...)

Repo: vn-revit-mcp, add-in Revit 2025+ (.NET 8) cho AI điều khiển Revit qua MCP. Người dùng chủ yếu nói tiếng Việt;
trả lời và viết thông điệp lỗi, mô tả lệnh bằng tiếng Việt có dấu.

## Việc hay gặp nhất: thêm một lệnh Revit

Cách nhanh nhất là **một file `.cs`**, không cần build:

1. Viết một lớp kế thừa `InnoPro.Revit.Sdk.RevitCommandBase` (mẫu: `examples/Scripts/DemCauKien.cs`, quy ước: `docs/viet-lenh-moi.md`).
2. Ghi vào `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Scripts\<Nhóm>\<TenLenh>.cs`.
3. Gọi tool MCP `reload_commands` (hoặc bảo người dùng bấm "Nạp lại lệnh"). Kết quả có `errors` kèm số dòng: sửa và nạp lại tới khi hết lỗi.
4. Chạy thử lệnh bằng tool của nó (hoặc `run_command`).

Quy tắc bắt buộc khi viết lệnh:
- `Name` snake_case, duy nhất; `Description` câu đầu nói đủ ý (AI khác sẽ đọc câu này để chọn lệnh).
- `InputSchema` là JSON Schema object; dùng tên tham số quen thuộc (`element_ids`, `level`, `category`, `*_type`) để form tự sinh có gợi ý.
- Sửa mô hình: `TransactionMode.Auto`, **không** tự tạo Transaction.
- Trả danh sách bằng `Table.Of(...)`, có `limit`. Chiều dài vào/ra bằng mm (`UnitConvert`).
- Lỗi người dùng: `throw new CommandException("...")`.
- Khi `context.IsInteractive == false` (AI gọi) không được bật hộp thoại chờ người dùng.
- Chỉ dùng API trong `InnoPro.Revit.Sdk` và Revit API; không truy cập mạng, không ghi file ngoài thư mục người dùng chỉ định.

## Cấu trúc repo

| Thư mục | Nội dung |
|---|---|
| `src/InnoPro.Revit.Sdk` | Interface cho người viết lệnh. **Đổi ở đây là đổi phần lõi**: cần cài lại và khởi động lại Revit |
| `src/InnoPro.Revit.Addin` | Host trong Revit: ribbon, nạp lệnh, form tự sinh, cầu nối named pipe |
| `src/InnoPro.Bridge` | Giao thức pipe giữa add-in và MCP server |
| `src/InnoPro.Mcp.Server` | MCP server (stdio) và lệnh `setup` cấu hình AI client |
| `src/InnoPro.Revit.Commands.*` | Các CommandSet đi kèm (nạp nóng được) |
| `examples/Scripts` | Lệnh mẫu một file |
| `scripts/` | `build.ps1`, `install-dev.ps1`, `dev-reload.ps1`, `package.ps1` |

## Build và thử

```powershell
dotnet build vn-revit-mcp.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts\install-dev.ps1   # lần đầu hoặc khi đổi phần lõi
powershell -ExecutionPolicy Bypass -File scripts\dev-reload.ps1    # đổi CommandSet: Revit đang mở nạp ngay
```

PowerShell script phải chạy được trên Windows PowerShell 5.1 và lưu UTF-8 có BOM.
