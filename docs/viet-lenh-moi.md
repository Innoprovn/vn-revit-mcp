# Viết lệnh mới

Mỗi lệnh dùng được theo **hai cách cùng lúc**:
- **người dùng bấm nút** trên tab InnoPro (form nhập tự sinh, kết quả hiện trong cửa sổ, chọn phần tử bằng một nút),
- **AI gọi** qua MCP (Claude, Cursor, Copilot, AI local...).

Bạn chỉ viết một lớp C#. Có ba cấp, từ dễ đến khó.

## Cấp 1: một file `.cs` (khuyên dùng)

Không cần Visual Studio, không cần project, không cần build.

1. Chép [examples/Scripts/DemCauKien.cs](../examples/Scripts/DemCauKien.cs) thành file mới, sửa theo ý.
2. Thả vào `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Scripts\` (thư mục con = tên nhóm, ví dụ `Scripts\Rebar\`).
3. Bấm **Nạp lại lệnh** trên tab InnoPro. Revit tự biên dịch; lỗi (nếu có) hiện ngay kèm số dòng.

Không biết code? Nói với AI đang kết nối Revit, ví dụ:

> Viết cho tôi lệnh vn-revit-mcp đánh số Mark cho cột theo thứ tự trục, lưu vào thư mục Scripts rồi nạp lại.

AI đọc [AGENTS.md](../AGENTS.md), ghi file, gọi `reload_commands`, đọc lỗi biên dịch và tự sửa.

## Cấp 2: CommandSet (project C#)

Cho bộ lệnh lớn, có thư viện riêng hoặc giao diện WPF riêng. Xem mẫu [`src/InnoPro.Revit.Commands.Core`](../src/InnoPro.Revit.Commands.Core):

- Class Library `net8.0-windows`, x64.
- Tham chiếu `InnoPro.Revit.Sdk` với `Private="false"` (host đã có, không chép kèm).
- Tham chiếu Revit API qua NuGet `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` với `ExcludeAssets="runtime"`.
- File `commandset.json` chép ra thư mục output:

```json
{ "name": "Rebar", "description": "Lệnh cốt thép.", "author": "Tên bạn", "assemblies": [ "MyCompany.Revit.Commands.Rebar.dll" ] }
```

Trong repo này: thêm project vào `vn-revit-mcp.sln`, chạy `scripts\dev-reload.ps1` là Revit đang mở nạp ngay.
Ngoài repo: chép thư mục output vào `%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Commands\<TênBộ>\` rồi bấm Nạp lại lệnh.

## Cấp 3: đóng góp vào repo

Mở pull request trên GitHub. Lệnh tốt sẽ được đưa vào bản cài chính thức cho mọi người.

## Một lệnh gồm những gì

```csharp
public sealed class SetMarkByGrid : RevitCommandBase
{
    public override string Name => "set_mark_by_grid";          // AI gọi bằng tên này: snake_case, duy nhất
    public override string Category => "Kết cấu";               // nhóm trên ribbon
    public override string Description => "...";                // viết cho AI: làm gì, khi nào dùng, trả về gì
    public override string InputSchema => """{ ... }""";        // JSON Schema tham số → form tự sinh cho người dùng
    public override TransactionMode TransactionMode => TransactionMode.Auto;  // sửa mô hình: host tự mở/commit/rollback
    public override RibbonButton Ribbon => new("Đánh số\ncột", "#");          // tùy chọn: nút lớn trên ribbon
    public override bool Pinned => false;                       // true: AI luôn thấy (chỉ cho lệnh rất hay dùng)

    public override object Execute(RevitCommandContext context, JsonElement arguments) { ... }
}
```

| Thành phần | Ghi chú |
|---|---|
| `TransactionMode` | `None` chỉ đọc; `Auto` host bọc Transaction (lỗi thì rollback, Undo được); `Manual` tự quản lý |
| `context.IsInteractive` | `true` khi người bấm nút. Khi AI gọi, **không** được bật hộp thoại chờ người dùng |
| `PromptArguments` | Tùy chọn: tự hỏi tham số khi bấm nút (chọn điểm, form riêng). Bỏ qua thì host sinh form từ schema |
| `context.Ui` | `SelectionOrPick`, `PickElement`, `PickPoint` cho người bấm nút |

### Form tự sinh hiểu các tên tham số này

| Tên tham số | Khi người dùng bấm nút |
|---|---|
| `element_ids` (mảng số) | Lấy vùng chọn hiện tại; chưa chọn thì cho quét chọn |
| `element_id` (số) | Phần tử đang chọn (nếu đúng 1), không thì cho chọn |
| `level`, `top_level` | Danh sách tầng |
| `category`, `category_a`, `category_b` | Danh sách category |
| `wall_type`, `floor_type`, `beam_type`, `family_type` | Danh sách type tương ứng |
| `schedule` | Danh sách schedule |
| có `enum` | Danh sách chọn |

## Quy ước để tiết kiệm token và AI dùng đúng

| Quy ước | Lý do |
|---|---|
| Lệnh **cấp cao, làm trọn việc** trong một lần gọi | Một lần gọi rẻ hơn mười lần gọi nhỏ |
| Danh sách trả về bằng `Table.Of(...)` | Bảng `{columns, rows}` tốn khoảng một nửa token so với mảng object |
| Có `limit`; với lệnh tìm, có `output=ids` | Không trả hàng nghìn dòng; id để chuyển sang lệnh khác |
| `Description` ngắn, câu đầu nói đủ ý | Câu đầu hiện trong `find_commands` |
| Chiều dài vào/ra bằng **mm**, dùng `UnitConvert` | Revit lưu feet |
| Lỗi do người dùng/AI: `throw new CommandException("...")` tiếng Việt rõ ràng | Thông điệp được trả nguyên |
| Tìm theo tên: `Lookup.Level`, `Lookup.Type<T>`, `CategoryResolver.Resolve` | Báo lỗi kèm gợi ý tên đúng |
| Tóm tắt phần tử: `ElementInfo.Summary`, lọc cấu kiện: `ElementInfo.IsPhysical` | Thống nhất giữa các lệnh |

## License

Nhờ [ngoại lệ plugin](../LICENSE-EXCEPTION.md), CommandSet hay file lệnh chỉ dùng interface của `InnoPro.Revit.Sdk`
được giữ license riêng của bạn. Đóng góp vào repo này thì theo GPLv3.
