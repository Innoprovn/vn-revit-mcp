using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class SaveDocument : RevitCommandBase
{
    public override string Name => "save_document";
    public override string Category => "Dữ liệu";
    public override string Description =>
        "Lưu dự án đang mở; có path thì lưu thành file mới (vd sau khi nâng cấp phiên bản Revit), mặc định không ghi đè file đã có. " +
        "path chỉ có tên file thì lưu cùng thư mục với file hiện tại.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Đường dẫn hoặc tên file .rvt mới. Bỏ trống: lưu đè file hiện tại." },
            "overwrite": { "type": "boolean", "description": "Cho ghi đè nếu file đã có. Mặc định false." },
            "compact": { "type": "boolean", "description": "Nén file khi lưu. Mặc định false." }
          }
        }
        """;

    // Revit cấm lưu khi đang có transaction: lệnh tự quản lý (không để host mở transaction).
    public override TransactionMode TransactionMode => TransactionMode.None;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var path = Args.GetStringOrDefault(arguments, "path");
        var compact = Args.GetBoolOrDefault(arguments, "compact", false);
        if (string.IsNullOrWhiteSpace(path))
        {
            if (string.IsNullOrEmpty(doc.PathName)) throw new CommandException("Dự án chưa từng lưu: truyền path.");
            doc.Save(new SaveOptions { Compact = compact });
            return new { saved = doc.PathName };
        }

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Path.GetDirectoryName(doc.PathName) ?? throw new CommandException("Truyền đường dẫn đầy đủ."), path);
        if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) path += ".rvt";
        var overwrite = Args.GetBoolOrDefault(arguments, "overwrite", false);
        if (File.Exists(path) && !overwrite) throw new CommandException($"Đã có file {path}. Đặt tên khác hoặc overwrite=true.");
        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = overwrite, Compact = compact, MaximumBackups = 1 });
        return new { saved = doc.PathName, size_mb = Math.Round(new FileInfo(doc.PathName).Length / 1048576.0, 1) };
    }
}
