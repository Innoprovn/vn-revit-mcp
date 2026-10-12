# Viết lệnh mới

Mỗi lệnh là một lớp C# cài đặt `IRevitCommand` (thường kế thừa `RevitCommandBase`). Add-in tự tìm lớp
trong CommandSet và MCP server tự công bố nó thành tool cùng tên. Không phải sửa server hay đăng ký thủ công.

## 1. Tạo CommandSet

Một CommandSet là một project Class Library (`net8.0-windows`, x64) gồm:

- tham chiếu `InnoPro.Revit.Sdk` với `Private="false"` (host đã có sẵn, không chép kèm),
- tham chiếu Revit API qua NuGet `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` với `ExcludeAssets="runtime"`,
- file `commandset.json` chép ra thư mục output.

Xem mẫu: [`src/InnoPro.Revit.Commands.Core`](../src/InnoPro.Revit.Commands.Core).

```json
{
  "name": "Rebar",
  "description": "Lệnh về cốt thép.",
  "author": "Tên bạn",
  "assemblies": [ "MyCompany.Revit.Commands.Rebar.dll" ]
}
```

## 2. Viết lệnh

```csharp
public sealed class SetParameter : RevitCommandBase
{
    public override string Name => "set_parameter";
    public override string Category => "Tham số";
    public override string Description =>
        "Gán giá trị chuỗi cho một tham số của phần tử. Dùng khi người dùng muốn sửa tham số dạng text.";

    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_id": { "type": "integer" },
            "parameter": { "type": "string", "description": "Tên tham số" },
            "value": { "type": "string" }
          },
          "required": ["element_id", "parameter", "value"]
        }
        """;

    // Host mở Transaction, commit khi xong, rollback khi lỗi.
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var element = doc.GetElement(new ElementId(Args.GetLong(arguments, "element_id")))
                      ?? throw new CommandException("Không tìm thấy phần tử.");
        var name = Args.GetString(arguments, "parameter");
        var parameter = element.LookupParameter(name) ?? throw new CommandException($"Không có tham số '{name}'.");
        if (parameter.IsReadOnly) throw new CommandException($"Tham số '{name}' chỉ đọc.");

        parameter.Set(Args.GetString(arguments, "value"));
        return new { updated = true };
    }
}
```

## 3. Quy ước

| Quy ước | Lý do |
|---|---|
| `Name` dạng `snake_case`, động từ đứng đầu (`get_`, `create_`, `set_`, `list_`) | AI chọn tool theo tên |
| `Description` viết cho AI: làm gì, khi nào dùng, trả về gì | Mô tả tốt thì AI gọi đúng lệnh |
| Chiều dài vào/ra bằng **mm**, dùng `UnitConvert` | Revit lưu feet, người dùng nghĩ theo mm |
| Lỗi do người dùng/AI thì ném `CommandException` với thông điệp tiếng Việt rõ ràng | Thông điệp được trả nguyên cho AI |
| Kết quả là object/anonymous object, đặt tên trường `snake_case` | Được serialize thành JSON |
| Danh sách dài phải có tham số giới hạn (`limit`) | Tránh trả về hàng chục nghìn phần tử |
| Lệnh chỉ đọc dùng `TransactionMode.None` | Không tạo Transaction thừa |

## 4. Cài thử

Chép thư mục output (DLL + `commandset.json`) vào
`%LOCALAPPDATA%\InnoPro\vn-revit-mcp\Commands\<TênBộ>\`, rồi khởi động lại Revit.

## License

Nhờ [ngoại lệ plugin](../LICENSE-EXCEPTION.md), CommandSet chỉ dùng interface của `InnoPro.Revit.Sdk`
được giữ license riêng của bạn. Đóng góp vào repo này thì theo GPLv3.
