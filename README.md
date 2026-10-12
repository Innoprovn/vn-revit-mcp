# vn-revit-mcp

**Cho AI điều khiển Autodesk Revit.** Mã nguồn mở, miễn phí, do [InnoPro](https://innopro.vn) phát triển.

vn-revit-mcp đưa các lệnh Revit thành công cụ theo chuẩn [Model Context Protocol (MCP)](https://modelcontextprotocol.io),
nên dùng được với mọi AI client hỗ trợ MCP qua stdio: Claude (Desktop, Code), Cursor, VS Code, LM Studio...,
kể cả model chạy local trên máy.

> 🚧 **Đang ở giai đoạn P0 (nền móng).** Chưa có khung chat trong Revit. Xem [lộ trình](#lộ-trình).

## Tính năng

- **Tab InnoPro** riêng trong Revit, bật/tắt quyền điều khiển của AI bằng một nút.
- **CommandSet dạng plugin:** mỗi bộ lệnh là một DLL, thêm lệnh không cần sửa MCP server.
- **Chạy hoàn toàn trên máy bạn:** add-in và MCP server nói chuyện qua named pipe, chỉ cùng tài khoản
  Windows mới kết nối được, không mở cổng mạng nào.
- **An toàn khi sửa mô hình:** lệnh sửa mô hình được bọc trong Transaction, lỗi thì rollback, Undo được trong Revit.
- Hỗ trợ **Revit 2025 trở lên**.

## Kiến trúc

```
AI client ──MCP (stdio)──▶ vn-revit-mcp.exe ──named pipe──▶ Add-in InnoPro (trong Revit) ──▶ Revit API
                                                               └─ CommandSet: Core, Rebar, QA-QC, ...
```

| Thành phần | Thư mục | Vai trò |
|---|---|---|
| `InnoPro.Revit.Sdk` | `src/InnoPro.Revit.Sdk` | Interface để viết lệnh (`IRevitCommand`, `Args`, `UnitConvert`) |
| `InnoPro.Revit.Addin` | `src/InnoPro.Revit.Addin` | Add-in: tab InnoPro, nạp CommandSet, chạy lệnh trên luồng Revit |
| `InnoPro.Bridge` | `src/InnoPro.Bridge` | Giao thức named pipe dùng chung |
| `InnoPro.Mcp.Server` | `src/InnoPro.Mcp.Server` | MCP server, công bố lệnh thành tool |
| `InnoPro.Revit.Commands.Core` | `src/InnoPro.Revit.Commands.Core` | CommandSet cơ bản |

Chi tiết: [docs/kien-truc.md](docs/kien-truc.md).

## Lệnh hiện có (CommandSet Core)

| Tool | Việc làm |
|---|---|
| `revit_status` | Kiểm tra kết nối tới Revit |
| `get_document_info` | Thông tin tài liệu đang mở |
| `get_current_view_info` | Thông tin view đang mở |
| `get_selected_elements` | Phần tử đang chọn |
| `get_element_parameters` | Tham số của một phần tử |
| `list_levels` | Danh sách tầng và cao độ (mm) |
| `list_views` | Danh sách view, lọc theo loại và tên |
| `list_sheets` | Danh sách sheet và view đặt trên sheet |
| `find_elements` | Tìm phần tử theo category, tầng, view, giá trị tham số |
| `analyze_model_statistics` | Thống kê phần tử theo category |
| `select_elements` | Chọn và zoom tới phần tử theo id |
| `set_parameters` | Sửa một tham số trên nhiều phần tử (Undo được) |

**CommandSet Code**

| Tool | Việc làm |
|---|---|
| `run_csharp` | Chạy mã C# Revit API. Mặc định **chỉ đọc** (mọi thay đổi bị hủy); chế độ `write` bật hộp thoại để người dùng duyệt trong Revit. Không cần thì xóa thư mục `Commands\Code` để tắt hẳn. |

## Cài đặt

Cần: Windows, Revit 2025 trở lên. Không cần quyền admin, không cần đóng Revit
(nếu Revit đang mở thì bản mới có hiệu lực từ lần mở Revit sau).

Mở **PowerShell** và chạy:

```powershell
irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex
```

Hoặc tải file `vn-revit-mcp-<phiên bản>-win-x64.zip` ở mục [Releases](https://github.com/Innoprovn/vn-revit-mcp/releases),
giải nén rồi bấm đúp **`Cai-dat.cmd`**.

Bộ cài tự kết nối mọi AI client có trên máy: Claude Desktop, Claude Code, Cursor, VS Code (GitHub Copilot),
Windsurf, Gemini CLI, LM Studio, Codex CLI, Cline. Khởi động lại AI client, mở Revit (tab **InnoPro**),
mở một dự án rồi hỏi AI: *"Mô hình Revit đang mở có bao nhiêu phần tử?"*

| Việc | Lệnh |
|---|---|
| Xem AI client nào đã kết nối | `& "$env:LOCALAPPDATA\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe" clients` |
| Kết nối lại (sau khi cài thêm AI client) | `& "$env:LOCALAPPDATA\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe" setup` |
| Cập nhật lên bản mới | chạy lại lệnh cài một dòng |
| Gỡ cài đặt | `uninstall.ps1` trong file zip (thêm `-Purge` để xóa cả lệnh và script tự thêm) |

Với AI client khác, khai báo MCP server kiểu **stdio** với command
`%LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe` (không cần tham số).
Mở nhiều phiên Revit cùng lúc thì đặt biến môi trường `VN_REVIT_MCP_PID` để chọn phiên.

## Cho nhà phát triển

Cần thêm [.NET SDK 8](https://dotnet.microsoft.com/download).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-dev.ps1
```

Script build rồi gọi chính `install.ps1` với phiên bản `dev`, nên máy dev dùng đúng bố cục như người dùng
(`%LOCALAPPDATA%\InnoPro\vn-revit-mcp\app\dev`, junction `current`) và cũng tự kết nối AI client
(thêm `-NoClients` để bỏ qua). Đóng gói bản phát hành: `scripts\package.ps1` → `artifacts\release\*.zip`.

**Sửa lệnh không cần tắt Revit:** CommandSet được nạp từ bộ nhớ nên không khóa file. Sau khi sửa mã lệnh:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\dev-reload.ps1
```

Script build, chép CommandSet vào bản đã cài và bảo Revit đang chạy nạp lại (hoặc bấm **Nạp lại lệnh** trên tab InnoPro,
hoặc để AI gọi tool `reload_commands`). Chỉ khi sửa phần lõi (Addin, Sdk, Bridge) mới cần chạy lại `install-dev.ps1`
rồi mở lại Revit.

## Viết lệnh mới

Xem [docs/viet-lenh-moi.md](docs/viet-lenh-moi.md). Ví dụ tối thiểu:

```csharp
public sealed class CountWalls : RevitCommandBase
{
    public override string Name => "count_walls";
    public override string Description => "Đếm số tường trong mô hình.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments) =>
        new { count = new FilteredElementCollector(context.Document).OfClass(typeof(Wall)).GetElementCount() };
}
```

## Lộ trình

| Giai đoạn | Nội dung |
|---|---|
| **P0 (đang làm)** | Add-in, tab InnoPro, nạp CommandSet, cầu nối, MCP server, lệnh truy vấn cơ bản |
| P1 | Thêm CommandSet: Kiến trúc, Kết cấu, Rebar, QA-QC, Bản vẽ, Dữ liệu; chạy mã C# có kiểm soát |
| P2 | Khung chat trong Revit: Claude, OpenAI, Gemini, Ollama, LM Studio |
| P3 | Bộ cài một dòng, tự cập nhật, đăng MCP Registry |
| P4 | App chat độc lập ngoài Revit |

## Đóng góp

Mọi người đều có thể đóng góp CommandSet mới hoặc cải tiến lệnh. Mở issue hoặc pull request trên GitHub.

## License

[GPLv3](LICENSE) kèm [ngoại lệ plugin](LICENSE-EXCEPTION.md): CommandSet của bạn được giữ license riêng
(kể cả đóng mã nguồn), và được phép chạy cùng Autodesk Revit.

Một số lệnh tham khảo đặc tả từ [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit) (MIT).

---

# English

**vn-revit-mcp** lets any MCP-capable AI client (Claude, Cursor, VS Code, local LLMs...) drive Autodesk Revit 2025+.
A Revit add-in adds an **InnoPro** ribbon tab, loads plugin **Command Sets**, and talks to a local MCP server
over a per-user named pipe. Licensed GPLv3 with a plugin exception.
Install (no admin rights): `irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex`,
or download the release zip and double-click `Cai-dat.cmd`. The installer registers the server with every detected
AI client (Claude Desktop/Code, Cursor, VS Code, Windsurf, Gemini CLI, LM Studio, Codex CLI, Cline).
Developers: `scripts/install-dev.ps1` (same layout, version `dev`) and `scripts/dev-reload.ps1`.
