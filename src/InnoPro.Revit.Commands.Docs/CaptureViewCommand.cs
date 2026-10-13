using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

/// <summary>
/// "Đôi mắt" cho AI: xuất ảnh PNG của một view, có thể khoanh quanh và tô đỏ các phần tử cần xem.
/// Làm trên bản sao view (giữ chú thích, dim) rồi xóa bản sao, nên view gốc không đổi. MCP server trả ảnh thẳng cho AI.
/// Rẻ hơn chụp màn hình: chỉ đúng vùng cần xem, không phụ thuộc cửa sổ Revit đang hiện gì.
/// </summary>
public sealed class CaptureViewCommand : RevitCommandBase
{
    public override string Name => "capture_view";
    public override string Category => "Bản vẽ";
    public override bool Pinned => true;
    public override TransactionMode TransactionMode => TransactionMode.Manual;
    public override string Description =>
        "Chụp ảnh một view (mặc định view đang mở) để XEM kết quả: truyền element_ids để khoanh vùng quanh và tô đỏ các phần tử đó. " +
        "Dùng sau khi tạo/sửa để tự kiểm tra (dim, tag, vị trí cấu kiện). Không đổi view gốc.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "view": { "type": "string", "description": "Tên view; mặc định view đang mở." },
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Phần tử cần xem: khoanh vùng quanh và tô đỏ." },
            "padding_mm": { "type": "number", "description": "Lề quanh phần tử, mặc định 1500." },
            "highlight": { "type": "boolean", "description": "Tô đỏ phần tử, mặc định true." },
            "pixel_size": { "type": "integer", "description": "Cạnh dài ảnh (px), mặc định 1000, tối đa 2000." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var source = Args.GetStringOrDefault(arguments, "view") is { Length: > 0 } name
            ? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new CommandException($"Không có view '{name}'. Gọi list_views để xem tên.")
            : doc.ActiveView;
        var ids = Args.Has(arguments, "element_ids") ? Args.GetElementIds(arguments, "element_ids").Where(id => doc.GetElement(id) != null).ToList() : [];
        var padding = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "padding_mm", 1500));
        var highlight = Args.GetBoolOrDefault(arguments, "highlight", true) && ids.Count > 0;
        var pixels = (int)Math.Clamp(Args.GetLongOrDefault(arguments, "pixel_size", 1000), 300, 2000);

        var duplicate = ElementId.InvalidElementId;
        var target = source;
        if (source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing) && (ids.Count > 0 || source is View3D))
        {
            using var t = new Transaction(doc, "InnoPro: chụp view");
            t.Start();
            duplicate = source.Duplicate(ViewDuplicateOption.WithDetailing);
            target = (View)doc.GetElement(duplicate);
            if (ids.Count > 0) Frame(doc, target, ids, padding);
            if (highlight) Highlight(doc, target, ids);
            t.Commit();
        }

        var folder = Path.Combine(Path.GetTempPath(), "InnoPro", "capture");
        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, "view-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews, FilePath = stem, ZoomType = ZoomFitType.FitToPage, PixelSize = pixels,
                ImageResolution = ImageResolution.DPI_150, FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
            };
            options.SetViewsAndSheets([target.Id]);
            doc.ExportImage(options);
        }
        finally
        {
            if (duplicate != ElementId.InvalidElementId)
            {
                using var t = new Transaction(doc, "InnoPro: xóa bản sao view");
                t.Start();
                doc.Delete(duplicate);
                t.Commit();
            }
        }

        var file = Directory.GetFiles(folder, Path.GetFileName(stem) + "*.png").FirstOrDefault()
                   ?? throw new CommandException("Revit không xuất được ảnh cho view này.");
        return new { image_file = file, view = source.Name, view_type = source.ViewType.ToString(), framed = ids.Count, highlighted = highlight ? ids.Count : 0 };
    }

    /// <summary>Khoanh vùng quanh phần tử: view 3D dùng section box, view phẳng dùng crop box (theo hệ toạ độ của view).</summary>
    private static void Frame(Document doc, View view, List<ElementId> ids, double padding)
    {
        var boxes = ids.Select(id => doc.GetElement(id).get_BoundingBox(null)).Where(b => b != null).ToList();
        if (boxes.Count == 0) return;
        var min = new XYZ(boxes.Min(b => b!.Min.X), boxes.Min(b => b!.Min.Y), boxes.Min(b => b!.Min.Z));
        var max = new XYZ(boxes.Max(b => b!.Max.X), boxes.Max(b => b!.Max.Y), boxes.Max(b => b!.Max.Z));
        var pad = new XYZ(padding, padding, padding);

        if (view is View3D view3D)
        {
            view3D.SetSectionBox(new BoundingBoxXYZ { Min = min - pad, Max = max + pad });
            return;
        }
        if (!view.CanBePrinted || view.ViewType is ViewType.Schedule or ViewType.DrawingSheet) return;

        var crop = view.CropBox;
        var inverse = crop.Transform.Inverse;
        var corners = new[] { min, max, new XYZ(min.X, max.Y, min.Z), new XYZ(max.X, min.Y, min.Z), new XYZ(min.X, min.Y, max.Z),
            new XYZ(max.X, max.Y, min.Z), new XYZ(min.X, max.Y, max.Z), new XYZ(max.X, min.Y, max.Z) }.Select(inverse.OfPoint).ToList();
        crop.Min = new XYZ(corners.Min(p => p.X) - padding, corners.Min(p => p.Y) - padding, crop.Min.Z);
        crop.Max = new XYZ(corners.Max(p => p.X) + padding, corners.Max(p => p.Y) + padding, crop.Max.Z);
        view.CropBoxActive = true;
        view.CropBox = crop;
        view.CropBoxVisible = false;
        // Cắt luôn chú thích (lưới trục, ký hiệu) để ảnh gọn đúng vùng cần xem.
        view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(1);
    }

    private static void Highlight(Document doc, View view, List<ElementId> ids)
    {
        var red = new Color(220, 30, 30);
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
        var settings = new OverrideGraphicSettings().SetProjectionLineColor(red).SetCutLineColor(red).SetProjectionLineWeight(6);
        if (solid != null)
            settings = settings.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(red)
                .SetCutForegroundPatternId(solid.Id).SetCutForegroundPatternColor(red);
        foreach (var id in ids)
        {
            try { view.SetElementOverrides(id, settings); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { } // phần tử không thuộc view này
        }
    }
}
