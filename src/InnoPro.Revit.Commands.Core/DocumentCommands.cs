using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

public sealed class OpenDocument : RevitCommandBase
{
    public override string Name => "open_document";
    public override string Category => "Dữ liệu";
    public override string Description =>
        "Mở một file .rvt (hoặc chuyển sang file đang mở) và đưa lên làm tài liệu làm việc. Không truyền path thì liệt kê các tài liệu đang mở.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Đường dẫn file .rvt, hoặc tên một tài liệu đang mở." }
          }
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.None;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var app = context.UIApplication;
        var open = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
        var target = Args.GetStringOrDefault(arguments, "path");
        if (string.IsNullOrWhiteSpace(target))
            return new { active = app.ActiveUIDocument?.Document.Title, open = open.Select(d => new { title = d.Title, path = d.PathName }).ToList() };

        // Tên tài liệu đang mở → đường dẫn của nó.
        var byTitle = open.FirstOrDefault(d => d.Title.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase)
                                               || Path.GetFileNameWithoutExtension(d.PathName).Equals(target.Trim(), StringComparison.OrdinalIgnoreCase));
        var path = byTitle?.PathName ?? target;
        if (!File.Exists(path)) throw new CommandException($"Không thấy file {target}. Đang mở: {string.Join(", ", open.Select(d => d.Title))}.");
        var ui = app.OpenAndActivateDocument(path);
        return new { active = ui.Document.Title, path = ui.Document.PathName };
    }
}

public sealed class CloseDocument : RevitCommandBase
{
    public override string Name => "close_document";
    public override string Category => "Dữ liệu";
    public override string Description =>
        "Đóng một tài liệu đang mở (không phải tài liệu đang làm việc), theo tên hoặc đường dẫn. save=true để lưu trước khi đóng; mặc định không lưu.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Tên hoặc đường dẫn tài liệu." },
            "save": { "type": "boolean", "description": "Lưu trước khi đóng. Mặc định false." }
          },
          "required": ["name"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.None;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var app = context.UIApplication;
        var name = Args.GetString(arguments, "name").Trim();
        var doc = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked)
                      .FirstOrDefault(d => d.Title.Equals(name, StringComparison.OrdinalIgnoreCase) || d.PathName.Equals(name, StringComparison.OrdinalIgnoreCase))
                  ?? throw new CommandException($"Không có tài liệu '{name}' đang mở.");
        if (app.ActiveUIDocument?.Document.Equals(doc) == true)
            throw new CommandException("Không đóng được tài liệu đang làm việc: chuyển sang tài liệu khác trước (open_document).");
        var title = doc.Title;
        var save = Args.GetBoolOrDefault(arguments, "save", false);
        if (save && string.IsNullOrEmpty(doc.PathName)) throw new CommandException("Tài liệu chưa từng lưu: dùng save_document với path trước.");
        doc.Close(save);
        return new { closed = title, saved = save };
    }
}

public sealed class LinkCad : RevitCommandBase
{
    public override string Name => "link_cad";
    public override string Category => "Dữ liệu";
    public override string Description =>
        "Link file DWG/DXF vào một view (mặc định mặt bằng đang mở), đơn vị mm, đặt theo gốc tọa độ. Link giữ chữ gốc nên các lệnh Dựng hình CAD " +
        "đọc được tên cấu kiện (Import thì không). Sửa mô hình, Undo được.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Đường dẫn file .dwg hoặc .dxf." },
            "view": { "type": "string", "description": "Tên view nhận link; mặc định view đang mở." },
            "level": { "type": "string", "description": "Thay cho view: mặt bằng đầu tiên của tầng này." },
            "unit": { "type": "string", "enum": ["mm", "cm", "m", "auto"], "description": "Đơn vị bản vẽ, mặc định mm." },
            "placement": { "type": "string", "enum": ["origin", "center", "shared"], "description": "Gốc tọa độ (mặc định), tâm, hoặc tọa độ chung." },
            "this_view_only": { "type": "boolean", "description": "Chỉ hiện trong view này. Mặc định false." }
          },
          "required": ["path"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var path = Args.GetString(arguments, "path");
        if (!File.Exists(path)) throw new CommandException($"Không thấy file {path}.");
        View view = doc.ActiveView;
        if (Args.GetStringOrDefault(arguments, "level") is { Length: > 0 } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                       .FirstOrDefault(v => !v.IsTemplate && v.GenLevel?.Id == level.Id)
                   ?? throw new CommandException($"Tầng {levelName} chưa có mặt bằng.");
        }
        else if (Args.GetStringOrDefault(arguments, "view") is { Length: > 0 } viewName)
            view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                       .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new CommandException($"Không có view {viewName}.");

        var options = new DWGImportOptions
        {
            Unit = Args.GetStringOrDefault(arguments, "unit", "mm") switch
            {
                "cm" => ImportUnit.Centimeter, "m" => ImportUnit.Meter, "auto" => ImportUnit.Default, _ => ImportUnit.Millimeter,
            },
            Placement = Args.GetStringOrDefault(arguments, "placement", "origin") switch
            {
                "center" => ImportPlacement.Centered, "shared" => ImportPlacement.Shared, _ => ImportPlacement.Origin,
            },
            ThisViewOnly = Args.GetBoolOrDefault(arguments, "this_view_only", false),
        };
        if (!doc.Link(path, options, view, out var id))
            throw new CommandException("Revit không link được file này.");
        return new { cad_id = id.Value, view = view.Name, file = Path.GetFileName(path) };
    }
}

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
