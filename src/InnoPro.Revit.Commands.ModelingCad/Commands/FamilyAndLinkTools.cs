using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using InnoPro.Revit.Commands.ModelingCad.Common;
using InnoPro.Revit.Commands.ModelingCad.LinkToDirectShape;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.ModelingCad.Commands;

internal static class FamilyFolder
{
    public static string[] Files(JsonElement args)
    {
        var folder = Args.GetString(args, "folder").Trim().Trim('"');
        if (!Directory.Exists(folder)) throw new CommandException($"Không có thư mục '{folder}'.");
        return Directory.GetFiles(folder, "*.rfa", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}

public sealed class ListFamilyFiles : RevitCommandBase
{
    public override string Name => "list_family_files";
    public override string Category => "Dựng hình";
    public override string Description => "Liệt kê file family (.rfa) trong một thư mục kèm category và các type bên trong.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "folder": { "type": "string", "description": "Thư mục chứa .rfa (không quét thư mục con)." },
            "limit": { "type": "integer", "description": "Mặc định 100." }
          },
          "required": ["folder"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var app = context.UIApplication.Application;
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 100);
        var files = FamilyFolder.Files(arguments);
        var rows = new List<object>();
        foreach (var path in files.Take(limit))
        {
            Document? famDoc = null;
            try
            {
                famDoc = app.OpenDocumentFile(path);
                var types = famDoc.FamilyManager.Types.Cast<FamilyType>().Select(t => t.Name.Trim()).Where(n => n.Length > 0).ToList();
                rows.Add(new
                {
                    file = Path.GetFileNameWithoutExtension(path),
                    category = famDoc.IsFamilyDocument ? famDoc.OwnerFamily?.FamilyCategory?.Name : null,
                    types = string.Join(", ", types.Take(20)) + (types.Count > 20 ? $"... (+{types.Count - 20})" : ""),
                    error = famDoc.IsFamilyDocument ? null : "Không phải file family",
                });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                rows.Add(new { file = Path.GetFileNameWithoutExtension(path), category = (string?)null, types = "", error = (string?)ex.Message });
            }
            finally
            {
                try { famDoc?.Close(false); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            }
        }
        return new { total = files.Length, families = Table.Of(rows) };
    }
}

public sealed class LoadFamiliesFromFolder : RevitCommandBase
{
    public override string Name => "load_families_from_folder";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description => "Nạp các file family (.rfa) trong một thư mục vào dự án, mặc định giữ nguyên family đã có.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "folder": { "type": "string", "description": "Thư mục chứa .rfa." },
            "files": { "type": "array", "items": { "type": "string" }, "description": "Tên file cần nạp; bỏ trống là tất cả." },
            "overwrite": { "type": "boolean", "description": "Ghi đè family đã có. Mặc định false." }
          },
          "required": ["folder"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var wanted = Args.GetStringsOrEmpty(arguments, "files")
            .Select(f => Path.GetFileNameWithoutExtension(f.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = FamilyFolder.Files(arguments)
            .Where(f => wanted.Count == 0 || wanted.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
        if (files.Count == 0) throw new CommandException("Không có file .rfa phù hợp trong thư mục.");

        var overwrite = Args.GetBoolOrDefault(arguments, "overwrite", false);
        var rows = new List<object>();
        foreach (var path in files)
        {
            var options = new LoadOptions(overwrite);
            string status;
            try
            {
                var ok = doc.LoadFamily(path, options, out _);
                status = options.Existed ? (overwrite ? "Đã ghi đè" : "Đã có, giữ nguyên") : ok ? "Đã nạp" : "Không nạp được";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                status = "Lỗi: " + ex.Message;
            }
            rows.Add(new { file = Path.GetFileNameWithoutExtension(path), status });
        }
        return Table.Of(rows);
    }

    private sealed class LoadOptions(bool overwrite) : IFamilyLoadOptions
    {
        public bool Existed { get; private set; }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            Existed = true;
            overwriteParameterValues = overwrite;
            return overwrite;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            Existed = true;
            source = overwrite ? FamilySource.Family : FamilySource.Project;
            overwriteParameterValues = overwrite;
            return overwrite;
        }
    }
}

public sealed class ConvertLinkToDirectShape : RevitCommandBase
{
    public override string Name => "convert_link_to_directshape";
    public override string Category => "Dựng hình";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Chép cấu kiện từ file link Revit sang dự án thành DirectShape (Generic Model), giữ vật liệu, tùy chọn chép thông số.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "link": { "type": "string", "description": "Tên link; mặc định link đầu tiên." },
            "linked_elements": { "type": "array", "items": { "type": "object", "properties": { "link_id": { "type": "integer" }, "element_id": { "type": "integer" } } }, "description": "Cấu kiện cụ thể trong link." },
            "categories": { "type": "array", "items": { "type": "string" }, "description": "Lọc category khi lấy cả link." },
            "active_view_only": { "type": "boolean", "description": "Chỉ cấu kiện thấy trong view." },
            "metadata": { "type": "string", "enum": ["none", "identity", "all"], "description": "Chép thông số. Mặc định none." },
            "parameter_prefix": { "type": "string", "description": "Mặc định LNK_." },
            "geometry": { "type": "string", "enum": ["exact", "tessellated"], "description": "Mặc định exact." },
            "limit": { "type": "integer", "description": "Tối đa số cấu kiện, mặc định 2000." }
          }
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        IList<Reference> refs;
        try { refs = context.UIDocument.Selection.PickObjects(ObjectType.LinkedElement, "Chọn cấu kiện trong link rồi bấm Finish"); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { throw new OperationCanceledException(); }
        return new
        {
            linked_elements = refs.Select(r => new { link_id = r.ElementId.Value, element_id = r.LinkedElementId.Value }).ToList(),
            metadata = "identity",
        };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 2000);
        var items = new List<(RevitLinkInstance, Element)>();
        RevitLinkInstance? wholeLink = null;

        if (Args.Has(arguments, "linked_elements"))
        {
            var seen = new HashSet<string>();
            foreach (var item in Args.GetArray(arguments, "linked_elements"))
            {
                var link = doc.GetElement(new ElementId(Args.GetLong(item, "link_id"))) as RevitLinkInstance
                           ?? throw new CommandException("link_id không phải link Revit.");
                var element = link.GetLinkDocument()?.GetElement(new ElementId(Args.GetLong(item, "element_id")));
                if (element != null && seen.Add($"{link.Id.Value}#{element.Id.Value}")) items.Add((link, element));
            }
        }
        else
        {
            var links = LinkScanner.CollectLoadedLinks(doc).Select(l => l.Instance).ToList();
            if (links.Count == 0) throw new CommandException("Chưa có file link Revit nào được nạp.");
            wholeLink = Args.GetStringOrDefault(arguments, "link") is { Length: > 0 } name
                ? links.FirstOrDefault(l => l.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                  ?? throw new CommandException($"Không có link '{name}'. Có: {Cad.Suggest(links.Select(l => l.Name))}.")
                : links[0];
            var categoryIds = Args.GetStringsOrEmpty(arguments, "categories")
                .Select(c => CategoryResolver.Resolve(doc, c).Id.Value).ToHashSet();
            items.AddRange(LinkScanner.CollectElements(doc, wholeLink, Args.GetBoolOrDefault(arguments, "active_view_only", false),
                doc.ActiveView, categoryIds).Select(e => (wholeLink, e)));
        }

        if (items.Count == 0) throw new CommandException("Không có cấu kiện nào phù hợp để chuyển.");
        if (items.Count > limit) throw new CommandException($"Có {items.Count} cấu kiện, vượt limit {limit}. Lọc bớt categories hoặc tăng limit.");

        var options = new ConvertOptions
        {
            Mode = wholeLink == null ? ConvertMode.PickElements : ConvertMode.WholeLink,
            Link = wholeLink,
            ActiveViewOnly = Args.GetBoolOrDefault(arguments, "active_view_only", false),
            Metadata = ArgsX.Enum(arguments, "metadata", "none", "none", "identity", "all") switch
            {
                "identity" => MetadataScope.Identity,
                "all" => MetadataScope.All,
                _ => MetadataScope.None,
            },
            ParameterPrefix = Args.GetStringOrDefault(arguments, "parameter_prefix", "LNK_")!,
            Geometry = ArgsX.Enum(arguments, "geometry", "exact", "exact", "tessellated") == "tessellated"
                ? GeometryMode.MaterialTessellated : GeometryMode.ExactSolid,
        };
        var report = new ShapeConverter(doc, options).Run(items);
        return new
        {
            total = items.Count, created = report.Created, no_geometry = report.SkippedNoGeometry, failed = report.Failed,
            materials_copied = report.MaterialsCopied, values_written = report.ValuesWritten, metadata_error = report.MetadataError,
            errors = report.Errors, element_ids = ArgsX.Ids(report.CreatedIds.Take(500)),
        };
    }
}
