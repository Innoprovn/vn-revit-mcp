using System;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

// Tạo dự án mới từ template và mở ra. Lệnh viết bằng một file .cs (thử tính năng Scripts).
public sealed class NewProject : RevitCommandBase
{
    public override string Name => "new_project";
    public override string Category => "Tài liệu";
    public override string Description =>
        "Tạo dự án Revit mới từ template (mặc định: template dự án của Revit), lưu ra file rồi mở. Trả về đường dẫn file.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "template_path": { "type": "string", "description": "File template .rte. Bỏ trống = template mặc định." },
            "save_path": { "type": "string", "description": "Nơi lưu file .rvt. Bỏ trống = thư mục tạm." }
          }
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Manual;

    public override object Execute(RevitCommandContext context, JsonElement arguments)
    {
        var app = context.UIApplication.Application;
        var template = Args.GetStringOrDefault(arguments, "template_path") ?? app.DefaultProjectTemplate;
        var path = Args.GetStringOrDefault(arguments, "save_path")
                   ?? Path.Combine(Path.GetTempPath(), $"vn-revit-mcp-{DateTime.Now:yyyyMMdd-HHmmss}.rvt");

        var doc = string.IsNullOrEmpty(template) || !File.Exists(template)
            ? app.NewProjectDocument(UnitSystem.Metric)
            : app.NewProjectDocument(template);
        doc.SaveAs(path);
        doc.Close(false);
        context.UIApplication.OpenAndActivateDocument(path);
        return new { path, template = File.Exists(template ?? "") ? template : "(không có template, dự án trống hệ mét)" };
    }
}
