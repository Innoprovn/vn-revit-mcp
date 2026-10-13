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
        "Tạo dự án Revit mới từ template (mặc định: template hệ mét của Revit), lưu ra file rồi mở. Trả về đường dẫn file.";
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
        var template = Args.GetStringOrDefault(arguments, "template_path") ?? MetricTemplate(app.DefaultProjectTemplate);
        var path = Args.GetStringOrDefault(arguments, "save_path")
                   ?? Path.Combine(Path.GetTempPath(), $"vn-revit-mcp-{DateTime.Now:yyyyMMdd-HHmmss}.rvt");

        var doc = string.IsNullOrEmpty(template) || !File.Exists(template)
            ? app.NewProjectDocument(UnitSystem.Metric)
            : app.NewProjectDocument(template);
        try { doc.SaveAs(path); }
        catch
        {
            doc.Close(false); // lưu lỗi (đường dẫn sai, file đã có...): không để sót dự án chưa lưu trong Revit
            throw;
        }
        doc.Close(false);
        context.UIApplication.OpenAndActivateDocument(path);
        return new { path, template = File.Exists(template ?? "") ? template : "(không có template, dự án trống hệ mét)" };
    }

    // Revit cài mặc định nhiều nơi là template Imperial (feet): ưu tiên template hệ mét cùng bộ cài.
    private static string MetricTemplate(string defaultTemplate)
    {
        if (!string.IsNullOrEmpty(defaultTemplate) && File.Exists(defaultTemplate)
            && defaultTemplate.IndexOf("Imperial", StringComparison.OrdinalIgnoreCase) < 0
            && defaultTemplate.IndexOf("US-Canada", StringComparison.OrdinalIgnoreCase) < 0)
            return defaultTemplate;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(defaultTemplate ?? ""));
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return defaultTemplate;
        foreach (var candidate in new[] { @"English\DefaultMetric.rte", @"English\Default-Multi-Discipline_Metric.rte", "Default_M_ENU.rte" })
            if (File.Exists(Path.Combine(root, candidate))) return Path.Combine(root, candidate);
        return defaultTemplate;
    }
}
