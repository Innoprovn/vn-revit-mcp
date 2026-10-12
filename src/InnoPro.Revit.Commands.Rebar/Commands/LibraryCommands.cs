namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

internal sealed class LibraryPackage
{
    public int FormatVersion { get; set; } = 1;
    public string SourceDocument { get; set; } = "";
    public DateTime CollectedAt { get; set; }
    public List<LibraryImage> Images { get; set; } = [];
    public Dictionary<string, string> ShapeMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LibraryRebar> RebarSamples { get; set; } = [];
}

internal sealed class LibraryImage
{
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string MirrorRelativePath { get; set; } = "";
    public string Source { get; set; } = "";
    public string PathType { get; set; } = "";
}

internal sealed class LibraryRebar
{
    public long ElementId { get; set; }
    public string UniqueId { get; set; } = "";
    public string Shape { get; set; } = "";
    public string ShapeCode { get; set; } = "";
    public string BarType { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = [];
}

internal static class RebarLibrary
{
    public const string ManifestName = "library.json";
    public const string CarrierName = "image-library.rvt";

    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "RebarLibrary");

    public static string Folder(JsonElement args) => ArgsX.String(args, "folder") ?? DefaultFolder;

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}

public sealed class RebarLibraryExport : RevitCommandBase
{
    public override string Name => "rebar_library_export";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Manual;
    public override string Description =>
        "Thu thư viện thép mẫu từ dự án hiện tại ra thư mục: ảnh (ImageType, giữ path gốc), ánh xạ shape → shape code và tham số các thanh mẫu.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "folder": { "type": "string", "description": "Thư mục thư viện; trống = mặc định." },
            "shape_code_parameters": { "type": "array", "items": { "type": "string" }, "description": "Tham số chứa shape code, mặc định Shape Code." },
            "create_carrier": { "type": "boolean", "description": "Tạo RVT giữ nguyên ImageType gốc, mặc định true." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var app = context.UIApplication.Application;
        var root = RebarLibrary.Folder(arguments);
        Directory.CreateDirectory(root);
        var codeParams = Args.GetStringsOrEmpty(arguments, "shape_code_parameters");
        if (codeParams.Count == 0) codeParams = ["Shape Code"];

        var package = new LibraryPackage { SourceDocument = doc.PathName.Length > 0 ? doc.PathName : doc.Title, CollectedAt = DateTime.Now };
        var warnings = new List<string>();
        var images = new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>().ToList();
        foreach (var image in images) package.Images.Add(CaptureImage(root, image, warnings));

        var carrier = false;
        if (Args.GetBoolOrDefault(arguments, "create_carrier", true) && images.Count > 0)
        {
            try
            {
                CreateCarrier(app, doc, images, Path.Combine(root, RebarLibrary.CarrierName), warnings);
                carrier = true;
            }
            catch (Exception ex)
            {
                warnings.Add($"Không tạo được RVT mang ảnh: {ex.Message}");
            }
        }

        foreach (var rebar in new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>())
        {
            var shape = RebarInfo.ShapeName(doc, rebar);
            var code = codeParams.Select(p => RebarInfo.ParameterText(doc, rebar, p)).FirstOrDefault(v => v.Length > 0) ?? shape;
            if (shape.Length > 0 && code.Length > 0) package.ShapeMappings[shape] = code;

            var sample = new LibraryRebar
            {
                ElementId = rebar.Id.Value,
                UniqueId = rebar.UniqueId,
                Shape = shape,
                ShapeCode = code,
                BarType = RebarInfo.BarType(doc, rebar)?.Name ?? "",
            };
            foreach (Parameter p in rebar.Parameters)
            {
                if (p.Definition == null || !p.HasValue) continue;
                string? value;
                try { value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(); }
                catch (Exception) { value = null; }
                if (!string.IsNullOrEmpty(value)) sample.Parameters[p.Definition.Name] = value;
            }
            package.RebarSamples.Add(sample);
        }

        File.WriteAllText(Path.Combine(root, RebarLibrary.ManifestName), JsonSerializer.Serialize(package, RebarLibrary.Json));
        return new
        {
            folder = root,
            images = package.Images.Count,
            shape_mappings = package.ShapeMappings.Count,
            rebar_samples = package.RebarSamples.Count,
            carrier_created = carrier,
            warnings,
        };
    }

    private static LibraryImage CaptureImage(string root, ImageType image, List<string> warnings)
    {
        var original = image.Path ?? "";
        var relative = MirrorPath(original, image.Name);
        var target = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (original.Length > 0 && File.Exists(original)) File.Copy(original, target, true);
            else
            {
                // Ảnh Import nằm trong RVT: lấy bitmap kể cả khi server gốc không còn.
                using var bitmap = image.GetImage();
                bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{image.Name}: {ex.Message}");
        }

        return new LibraryImage
        {
            Name = image.Name,
            OriginalPath = original,
            MirrorRelativePath = relative,
            Source = image.Source.ToString(),
            PathType = image.PathType.ToString(),
        };
    }

    /// <summary>Lưu nguyên ImageType vào một RVT trung gian: copy element giữa hai Document giữ path gốc.</summary>
    private static void CreateCarrier(Autodesk.Revit.ApplicationServices.Application app, Document source, List<ImageType> images, string path, List<string> warnings)
    {
        Document? carrier = null;
        try
        {
            carrier = app.NewProjectDocument(UnitSystem.Metric);
            using (var tx = new Transaction(carrier, "Ảnh thép mẫu"))
            {
                tx.Start();
                ElementTransformUtils.CopyElements(source, images.Select(i => i.Id).ToList(), carrier, Transform.Identity, new CopyPasteOptions());

                // Ảnh Link sẽ mất khi rời server: chuyển thành Import nhưng vẫn dùng đúng path gốc.
                var byName = images.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var copied in new FilteredElementCollector(carrier).OfClass(typeof(ImageType)).Cast<ImageType>())
                {
                    if (!byName.TryGetValue(copied.Name, out var original) || original.Source != ImageTypeSource.Link) continue;
                    if (string.IsNullOrWhiteSpace(original.Path) || !File.Exists(original.Path))
                    {
                        warnings.Add($"{original.Name}: ảnh link không truy cập được để nhúng.");
                        continue;
                    }
                    using var options = new ImageTypeOptions(original.Path, false, ImageTypeSource.Import);
                    copied.ReloadFrom(options);
                }
                tx.Commit();
            }
            carrier.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, Compact = true });
        }
        finally
        {
            carrier?.Close(false);
        }
    }

    private static string MirrorPath(string originalPath, string imageName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string Clean(string v) => new(v.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var parts = originalPath.Trim().TrimStart('\\', '/').Replace(':', '_')
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Clean).ToList();
        if (parts.Count == 0) parts.Add(Clean(imageName) + ".png");
        return Path.Combine(["Mirror", .. parts]);
    }
}

public sealed class RebarLibraryImport : RevitCommandBase
{
    public override string Name => "rebar_library_import";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Manual;
    public override string Description =>
        "Bung thư viện thép mẫu vào dự án hiện tại: chép ảnh (giữ path gốc nếu có RVT mang ảnh), gán lại ảnh cho thép theo tên; trả ánh xạ shape code.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "folder": { "type": "string", "description": "Thư mục thư viện; trống = mặc định." },
            "image_parameter": { "type": "string", "description": "Tham số ảnh trên thép, mặc định Image." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var root = RebarLibrary.Folder(arguments);
        var manifest = Path.Combine(root, RebarLibrary.ManifestName);
        if (!File.Exists(manifest)) throw new CommandException($"Chưa có thư viện tại {manifest}. Chạy rebar_library_export ở dự án mẫu trước.");
        var package = JsonSerializer.Deserialize<LibraryPackage>(File.ReadAllText(manifest)) ?? throw new CommandException("library.json không hợp lệ.");
        var imageParameter = ArgsX.String(arguments, "image_parameter") ?? "Image";

        var carrierPath = Path.Combine(root, RebarLibrary.CarrierName);
        var result = File.Exists(carrierPath)
            ? FromCarrier(context.UIApplication.Application, doc, carrierPath, imageParameter)
            : FromFiles(doc, root, package);

        return new
        {
            folder = root,
            method = File.Exists(carrierPath) ? "carrier" : "files",
            result.Copied,
            result.Replaced,
            result.Kept,
            result.Failed,
            shape_mappings = package.ShapeMappings,
        };
    }

    private sealed record ImportResult(int Copied, int Replaced, int Kept, List<string> Failed);

    private static ImportResult FromCarrier(Autodesk.Revit.ApplicationServices.Application app, Document target, string carrierPath, string imageParameter)
    {
        Document? carrier = null;
        try
        {
            carrier = app.OpenDocumentFile(carrierPath);
            var carrierImages = new FilteredElementCollector(carrier).OfClass(typeof(ImageType)).Cast<ImageType>().ToList();

            // Ghi nhớ ảnh đang gán trên thép: xóa ImageType cũ thì Revit xóa tham chiếu, sau đó gán lại theo tên.
            var rebarImages = new List<(ElementId RebarId, string ImageName)>();
            foreach (var rebar in new FilteredElementCollector(target).OfClass(typeof(Rebar)))
            {
                var p = rebar.LookupParameter(imageParameter);
                if (p is { StorageType: StorageType.ElementId, HasValue: true } && target.GetElement(p.AsElementId()) is ImageType current)
                    rebarImages.Add((rebar.Id, current.Name));
            }

            int copied = 0, replaced = 0, kept = 0;
            using var tx = new Transaction(target, "Bung thư viện ảnh thép");
            TransactionHelper.SuppressWarnings(tx);
            tx.Start();
            var existing = new FilteredElementCollector(target).OfClass(typeof(ImageType)).Cast<ImageType>()
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var toCopy = new List<ElementId>();
            foreach (var image in carrierImages)
            {
                if (existing.TryGetValue(image.Name, out var current))
                {
                    if (string.Equals(current.Path, image.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        kept++;
                        continue;
                    }
                    target.Delete(current.Id);
                    replaced++;
                }
                toCopy.Add(image.Id);
            }
            if (toCopy.Count > 0)
                copied = ElementTransformUtils.CopyElements(carrier, toCopy, target, Transform.Identity, new CopyPasteOptions()).Count;

            var final = new FilteredElementCollector(target).OfClass(typeof(ImageType)).Cast<ImageType>()
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var (rebarId, name) in rebarImages)
            {
                if (target.GetElement(rebarId) is { } rebar && final.TryGetValue(name, out var image)
                    && rebar.LookupParameter(imageParameter) is { IsReadOnly: false, StorageType: StorageType.ElementId } p)
                    p.Set(image.Id);
            }
            tx.Commit();
            return new ImportResult(copied, replaced, kept, []);
        }
        finally
        {
            carrier?.Close(false);
        }
    }

    private static ImportResult FromFiles(Document doc, string root, LibraryPackage package)
    {
        var existing = new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>()
            .Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int copied = 0, kept = 0;
        var failed = new List<string>();

        using var tx = new Transaction(doc, "Bung thư viện ảnh thép");
        TransactionHelper.SuppressWarnings(tx);
        tx.Start();
        foreach (var item in package.Images)
        {
            if (existing.Contains(item.Name))
            {
                kept++;
                continue;
            }
            var file = Path.Combine(root, item.MirrorRelativePath);
            if (!File.Exists(file))
            {
                failed.Add($"{item.Name}: thiếu file");
                continue;
            }
            try
            {
                using var options = new ImageTypeOptions(file, false, ImageTypeSource.Import);
                var created = ImageType.Create(doc, options);
                if (!string.Equals(created.Name, item.Name, StringComparison.Ordinal)) created.Name = item.Name;
                copied++;
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Name}: {ex.Message}");
            }
        }
        tx.Commit();
        return new ImportResult(copied, 0, kept, failed);
    }
}
