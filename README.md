# vn-revit-mcp

**Cho AI làm việc với Autodesk Revit.** Mã nguồn mở, miễn phí, do [InnoPro](https://innopro.vn) phát triển.

- **Một lệnh cài**, tự kết nối mọi AI trên máy: Claude, Cursor, VS Code Copilot, Gemini CLI, Codex, LM Studio...
- **Chat ngay trong Revit** với Claude, OpenAI, Gemini, DeepSeek, Qwen, Kimi, GLM..., hoặc AI chạy trên máy; dùng được cả gói Claude Pro/Max.
- **Mỗi lệnh dùng được hai cách:** người bấm nút trên tab **InnoPro** (form nhập tự sinh), hoặc AI gọi.
- **Viết lệnh mới bằng một file `.cs`**, không cần build: thả vào thư mục, bấm *Nạp lại lệnh*. Hoặc nhờ AI viết.
- **Tiết kiệm token:** AI chỉ thấy vài lệnh hay dùng, tra thêm khi cần; kết quả trả dạng bảng gọn.
- Chạy trên máy bạn, không mở cổng mạng. Lệnh sửa mô hình Undo được. Revit **2025 trở lên**.

## Cài đặt

Mở **PowerShell** và chạy (không cần quyền admin, Revit đang mở cũng được):

```powershell
irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex
```

Hoặc tải `vn-revit-mcp-<phiên bản>-win-x64.zip` ở [Releases](https://github.com/Innoprovn/vn-revit-mcp/releases),
giải nén rồi bấm đúp **`Cai-dat.cmd`**.

Xong: mở Revit, tab **InnoPro**. Dùng theo một trong ba cách:
- **Chat trong Revit:** bấm **Chat AI**, chọn nhà cung cấp, nhập API key. Có Claude, OpenAI, Gemini, DeepSeek, Qwen, Kimi,
  GLM, MiniMax, OpenRouter, hoặc AI chạy trên máy (Ollama, LM Studio) không cần key.
- **Gói Claude Pro/Max:** trong khung chat chọn *Claude (gói Pro/Max)* → *Kết nối và mở Claude*.
- **AI bên ngoài** (Claude Desktop, Cursor, VS Code...): đã tự kết nối khi cài; khởi động lại ứng dụng AI rồi hỏi.

Hướng dẫn chi tiết: [docs/huong-dan-su-dung.md](docs/huong-dan-su-dung.md).

| Việc | Cách làm |
|---|---|
| Kết nối thêm AI client | nút **Hệ thống → Kết nối AI** trong Revit |
| Cập nhật | nút **Hệ thống → Cập nhật**, hoặc chạy lại lệnh cài |
| Gỡ cài đặt | `uninstall.ps1` trong file zip (thêm `-Purge` để xóa cả lệnh tự thêm) |

AI client khác: khai báo MCP server kiểu **stdio**, command `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\current\server\vn-revit-mcp.exe`.
Mở nhiều Revit cùng lúc thì đặt biến môi trường `VN_REVIT_MCP_PID` để chọn phiên.

## Có những gì (82 lệnh)

| Nhóm | Ví dụ |
|---|---|
| Truy vấn, Tham số | thông tin dự án/view, phần tử đang chọn, tìm phần tử, tham số, thống kê; sửa tham số hàng loạt |
| QA-QC | cảnh báo, phần tử trùng (tìm/xóa), tham số trống, va chạm, va chạm với link, sức khỏe mô hình, dim tới trục |
| Dựng hình | tầng, lưới trục, tường, dầm, sàn, đặt family; sàn theo dầm, gộp/tách sàn, tự join, lớp hoàn thiện tường |
| Dựng hình CAD | tường, sàn/trần, cột, dầm, cọc + đài, cửa từ bản vẽ CAD; tạo type dầm từ mặt cắt |
| Rebar | thống kê kiểu BBS, chọn nhanh, ngắt/gắn host, dịch thép, tránh va chạm, QA thép, so sánh thay đổi, thư viện hình dạng |
| Bản vẽ | tạo sheet, đặt view, nhân bản view, gán template, tag tự động, dim trục, xuất PDF |
| Dữ liệu | đọc/xuất schedule, khối lượng vật liệu, chiều dài tường, danh sách sheet, tìm phần tử trong link |
| Nâng cao | `run_csharp`: AI chạy mã C#; mặc định chỉ đọc, sửa mô hình phải được duyệt |

Ribbon gọn: vài nút nhỏ mỗi nhóm, mọi lệnh khác trong menu **Tất cả**. Thêm lệnh từ **Cửa hàng** (cộng đồng đóng góp).

**Công thức** (MCP prompts, ví dụ gõ `/` trong Claude): `kiem-tra-chat-luong`, `boc-khoi-luong`, `dung-tang-luoi-truc`,
`viet-lenh-moi`. Mỗi công thức là một file Markdown, ai cũng viết thêm được.
## Viết lệnh mới

Cách nhanh nhất: một file `.cs`, không cần project hay build.

```csharp
public sealed class CountWalls : RevitCommandBase
{
    public override string Name => "count_walls";
    public override string Category => "Của tôi";
    public override string Description => "Đếm số tường trong mô hình.";
    public override RibbonButton Ribbon => new("Đếm\ntường", "#");   // có nút trên ribbon

    public override object Execute(RevitCommandContext context, JsonElement arguments) =>
        new { count = new FilteredElementCollector(context.Document).OfClass(typeof(Wall)).GetElementCount() };
}
```

Thả vào `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Scripts\`, bấm **Nạp lại lệnh**. Lệnh có nút, có form nhập, và AI gọi được ngay.
Không biết code: dùng công thức `viet-lenh-moi` hoặc nói với AI *"viết cho tôi lệnh vn-revit-mcp ..."*.

Hướng dẫn đầy đủ (3 cấp: file `.cs`, CommandSet, đóng góp): [docs/viet-lenh-moi.md](docs/viet-lenh-moi.md).
AI lập trình (Claude Code, Cursor, Copilot, Codex...) đọc [AGENTS.md](AGENTS.md).

## Kiến trúc

```
AI client ──MCP (stdio)──▶ vn-revit-mcp.exe ──named pipe──▶ Add-in InnoPro (trong Revit) ──▶ Revit API
Khung chat trong Revit ──────────────────────────────────────▲      ├─ CommandSet (DLL)
                                                                    └─ Scripts (*.cs, tự biên dịch)
```

| Thành phần | Vai trò |
|---|---|
| `src/InnoPro.Revit.Sdk` | Interface viết lệnh: `RevitCommandBase`, `Args`, `Table`, `Lookup`, `UnitConvert` |
| `src/InnoPro.Revit.Addin` | Host trong Revit: ribbon tự sinh, form tự sinh, nạp lệnh nóng, cầu nối, khung chat |
| `src/InnoPro.Revit.Chat` | Module chat (nạp riêng): Claude qua SDK Anthropic, các AI khác qua API OpenAI-compatible |
| `src/InnoPro.Bridge` | Giao thức named pipe, tra cứu lệnh dùng chung |
| `src/InnoPro.Mcp.Server` | MCP server, công thức, lệnh `setup` cấu hình AI client |
| `src/InnoPro.Revit.Commands.*` | CommandSet đi kèm: Core, QA-QC, Modeling, Data, Code |

Chi tiết: [docs/kien-truc.md](docs/kien-truc.md).

## Cho nhà phát triển

Cần [.NET SDK 8](https://dotnet.microsoft.com/download).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-dev.ps1   # build + cài bản "dev", cùng bố cục với người dùng
powershell -ExecutionPolicy Bypass -File scripts\dev-reload.ps1    # sửa CommandSet: Revit đang mở nạp lại ngay
powershell -ExecutionPolicy Bypass -File scripts\package.ps1       # đóng gói artifacts\release\*.zip
```

Chỉ khi sửa phần lõi (Addin, Sdk, Bridge) mới cần chạy lại `install-dev.ps1` rồi mở lại Revit.

## Lộ trình

| Giai đoạn | Nội dung | Trạng thái |
|---|---|---|
| P0 | Add-in, tab InnoPro, cầu nối, MCP server | ✅ |
| P1 | CommandSet Core, QA-QC, Modeling, Data, Code; lệnh từ file `.cs`; nút và form tự sinh; tiết kiệm token | ✅ (đang mở rộng) |
| P2 | Khung chat trong Revit đa nhà cung cấp | ✅ bản đầu |
| P3 | Bộ cài một dòng, tự cấu hình AI client; phát hành GitHub, MCP Registry, tự cập nhật | 🔄 |
| P4 | Bộ lệnh chuyên sâu (Kết cấu, Rebar, Bản vẽ, vẽ từ CAD); cửa hàng lệnh cộng đồng | ⏳ |

## Đóng góp

Mọi người đều có thể đóng góp lệnh, CommandSet, công thức. Mở issue hoặc pull request.

## License

[GPLv3](LICENSE) kèm [ngoại lệ plugin](LICENSE-EXCEPTION.md): CommandSet và file lệnh của bạn được giữ license riêng
(kể cả đóng mã nguồn), và được phép chạy cùng Autodesk Revit.

Một số lệnh tham khảo đặc tả từ [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit) (MIT).

---

# English

**vn-revit-mcp** lets AI work with Autodesk Revit 2025+. One-line install (no admin):
`irm https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/install.ps1 | iex` — it registers the MCP server
with every detected AI client (Claude Desktop/Code, Cursor, VS Code, Windsurf, Gemini CLI, LM Studio, Codex CLI, Cline).
Inside Revit, an **InnoPro** tab offers a chat pane (Claude via the Anthropic SDK; OpenAI, Gemini, OpenRouter, Ollama,
LM Studio via OpenAI-compatible APIs) and a button for every command with an auto-generated input form.
Add a command by dropping a single `.cs` file into the Scripts folder — it is compiled in Revit and hot-reloaded.
Licensed GPLv3 with a plugin exception. See [AGENTS.md](AGENTS.md) for AI coding agents.
