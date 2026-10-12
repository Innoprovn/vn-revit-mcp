# Kiến trúc

## Luồng một lệnh

```
AI client
  │  MCP qua stdio (JSON-RPC)
  ▼
vn-revit-mcp.exe (InnoPro.Mcp.Server)
  │  tools/list  → hỏi add-in danh sách lệnh (hoặc đọc commands.json nếu Revit chưa mở)
  │  tools/call  → gửi "invoke" qua named pipe
  ▼
\\.\pipe\InnoPro.VnRevitMcp.<pid>   (PipeOptions.CurrentUserOnly)
  ▼
Add-in InnoPro (luồng nền của Revit)
  │  xếp hàng lệnh, ExternalEvent.Raise()
  ▼
Luồng chính Revit (RevitDispatcher.Execute)
  │  CommandRunner: Transaction theo TransactionMode, chạy lệnh, serialize JSON
  ▼
Kết quả trả ngược lại theo cùng đường
```

## Vì sao

| Lựa chọn | Lý do |
|---|---|
| **Named pipe** thay vì cổng TCP | Không mở cổng mạng, không dính firewall, Windows tự giới hạn cùng tài khoản |
| **Mỗi Revit một pipe theo PID** | Mở nhiều phiên Revit không tranh nhau cổng |
| **ExternalEvent** | Revit API chỉ được gọi trên luồng chính, khi Revit rảnh |
| **Serialize kết quả trên luồng Revit** | Lệnh được phép trả LINQ lười chạm Revit API |
| **AssemblyLoadContext riêng cho mỗi CommandSet** | Thư viện phụ thuộc của các bộ khác nhau không xung đột; SDK và Revit API dùng chung với host |
| **commands.json** | AI client khởi động trước Revit vẫn biết có những tool nào |
| **Tool động** (`WithListToolsHandler`) | Thêm lệnh ở CommandSet là có tool, không sửa server |
| **Huỷ lệnh quá hạn trước khi chạy** | Client đã bỏ cuộc thì lệnh không còn sửa mô hình sau đó |

## Giao thức pipe

Mỗi dòng một JSON (UTF-8, snake_case).

```json
→ {"id":"…","method":"invoke","params":{"name":"list_levels","arguments":{}}}
← {"id":"…","ok":true,"result":[{"element_id":311,"name":"Level 1","elevation_mm":0}]}
← {"id":"…","ok":false,"error":"Không có lệnh 'abc'."}
```

| method | params | result |
|---|---|---|
| `ping` | | `RevitStatus` |
| `list_commands` | | `CommandDescriptor[]` |
| `invoke` | `{name, arguments}` | kết quả lệnh |

## Dữ liệu trên máy

| Đường dẫn | Nội dung |
|---|---|
| `%APPDATA%\Autodesk\Revit\Addins\<năm>\InnoPro.VnRevitMcp.addin` | Khai báo add-in, trỏ tới `current\addin\InnoPro.Revit.Addin.dll` |
| `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\app\<phiên bản>\addin\` | Add-in và CommandSet đi kèm |
| `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\app\<phiên bản>\server\` | MCP server `vn-revit-mcp.exe` |
| `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\current` | Junction trỏ tới phiên bản đang dùng; AI client gọi `current\server\vn-revit-mcp.exe` |
| `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Commands\` | CommandSet cài thêm |
| `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\commands.json` | Danh mục lệnh lần nạp gần nhất |
| `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\logs\` | Log add-in |

Cập nhật phiên bản chỉ cần chép vào `app\<phiên bản mới>` rồi trỏ lại junction `current`: làm được cả khi Revit
hoặc AI client đang giữ file của bản cũ, bản mới có hiệu lực từ lần mở sau. Dữ liệu người dùng
(`Commands\`, `Scripts\`, `logs\`, `commands.json`) không bao giờ bị xóa khi cập nhật.
