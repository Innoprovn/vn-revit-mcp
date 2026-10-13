using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Docs;

public sealed class FindElementInLinks : RevitCommandBase
{
    private const double PaddingFeet = 1.0;

    public override string Name => "find_element_in_links";
    public override string Category => "Dữ liệu";
    // Tự quản lý Transaction: tạo view 3D + section box, rồi chuyển view (không được đổi view khi Transaction đang mở).
    public override TransactionMode TransactionMode => TransactionMode.Manual;
    public override string Description =>
        "Tìm phần tử trong link (IFC/Revit) theo giá trị tham số, tùy chọn mở view 3D khoanh vùng kết quả.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "value": { "type": "string", "description": "Giá trị cần tìm." },
            "param_name": { "type": "string", "description": "Tên tham số (nên có, nhanh và an toàn)." },
            "exact": { "type": "boolean", "description": "Khớp nguyên giá trị." },
            "link": { "type": "string", "description": "Tên link chứa chuỗi này." },
            "section_box": { "type": "boolean", "description": "Mở view 3D khoanh kết quả." },
            "limit": { "type": "integer", "description": "Mặc định 50." }
          },
          "required": ["value"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var value = Args.GetString(arguments, "value").Trim();
        if (value.Length == 0) throw new CommandException("'value' không được trống.");
        var paramName = Args.GetStringOrDefault(arguments, "param_name");
        if (string.IsNullOrWhiteSpace(paramName)) paramName = null;
        var exact = Args.GetBoolOrDefault(arguments, "exact", false);
        var linkFilter = Args.GetStringOrDefault(arguments, "link");
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 50);
        var sectionBox = Args.GetBoolOrDefault(arguments, "section_box", context.IsInteractive);

        bool Match(string? text) => text != null && (exact
            ? string.Equals(text.Trim(), value, StringComparison.OrdinalIgnoreCase)
            : text.Contains(value, StringComparison.OrdinalIgnoreCase));

        var matches = new List<(Element Element, RevitLinkInstance Link, string Param, string Value)>();
        var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => linkFilter == null || l.Name.Contains(linkFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (links.Count == 0) throw new CommandException("Không có link nào phù hợp.");

        foreach (var link in links)
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) continue;
            foreach (var e in new FilteredElementCollector(linkDoc).WhereElementIsNotElementType())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (matches.Count > limit) break;
                try
                {
                    if (paramName != null)
                    {
                        var p = e.LookupParameter(paramName);
                        var text = p is { StorageType: StorageType.String, HasValue: true } ? p.AsString() : null;
                        if (Match(text)) matches.Add((e, link, paramName, text!));
                        continue;
                    }
                    // Chỉ đọc tham số kiểu chữ: AsValueString hàng loạt trên link lớn có thể làm sập Revit.
                    foreach (Parameter p in e.Parameters)
                    {
                        if (p.StorageType != StorageType.String || !p.HasValue) continue;
                        var text = p.AsString();
                        if (!Match(text)) continue;
                        matches.Add((e, link, p.Definition.Name, text));
                        break;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Phần tử lỗi tham số: bỏ qua riêng nó.
                }
            }
        }

        if (matches.Count == 0)
            throw new CommandException($"Không tìm thấy phần tử nào có tham số {(exact ? "bằng" : "chứa")} \"{value}\" trong link đã nạp.");

        string? viewName = null;
        if (sectionBox && matches[0].Element.get_BoundingBox(null) is { } box)
        {
            var (min, max) = Geo.Transform(box.Min, box.Max, matches[0].Link.GetTotalTransform(), PaddingFeet);
            var view = SectionBoxView(doc, DocLookup.SafeName(value, "Tim trong link"), min, max);
            viewName = view.Name;
            try
            {
                context.UIDocument.ActiveView = view;
                context.UIDocument.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id)?.ZoomToFit();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Không chuyển được view thì vẫn trả kết quả.
            }
        }

        var shown = matches.Take(limit).ToList();
        return new
        {
            found = matches.Count > limit ? $">{limit}" : matches.Count.ToString(),
            section_box_view = viewName,
            elements = Table.Of(shown.Select(m => new
            {
                link = m.Link.Name,
                link_instance_id = m.Link.Id.Value,
                linked_element_id = m.Element.Id.Value,
                category = m.Element.Category?.Name,
                parameter = m.Param,
                value = m.Value,
            }).ToList()),
        };
    }

    /// <summary>View 3D đặt theo tên từ khóa: có thì dùng lại, chưa có thì tạo.</summary>
    private static View3D SectionBoxView(Document doc, string name, XYZ min, XYZ max)
    {
        using var transaction = new Transaction(doc, "InnoPro: section box phần tử link");
        TransactionHelper.SuppressWarnings(transaction);
        transaction.Start();
        var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        if (view == null)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new CommandException("Dự án không có loại view 3D.");
            view = View3D.CreateIsometric(doc, type.Id);
            var used = DocLookup.AllViews(doc).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = name;
            for (var i = 2; used.Contains(unique); i++) unique = $"{name} ({i})";
            view.Name = unique;
        }
        view.IsSectionBoxActive = true;
        view.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
        if (transaction.Commit() != TransactionStatus.Committed)
            throw new CommandException("Revit không cho tạo view 3D section box.");
        return view;
    }
}

public sealed class GetWallLengths : RevitCommandBase
{
    public override string Name => "get_wall_lengths";
    public override string Category => "Dữ liệu";
    public override string Description =>
        "Tổng chiều dài, diện tích tường theo type hoặc tầng, cho tường đang chọn hoặc toàn mô hình.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Tường cần tính. Trống = toàn mô hình." },
            "level": { "type": "string" },
            "group_by": { "type": "string", "enum": ["type", "level", "type_level", "none"], "description": "Mặc định type." }
          }
        }
        """;

    // Người bấm nút: lấy tường đang chọn, không chọn thì tính toàn mô hình (không bắt quét chọn).
    public override object? PromptArguments(RevitCommandContext context)
    {
        var doc = context.Document;
        var walls = context.UIDocument.Selection.GetElementIds().Where(id => doc.GetElement(id) is Wall).Select(id => id.Value).ToList();
        return walls.Count > 0 ? new { element_ids = walls, group_by = "type" } : new { group_by = "type_level" };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        IEnumerable<Wall> walls = Args.Has(arguments, "element_ids")
            ? Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).OfType<Wall>()
            : new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();
        if (Args.GetStringOrDefault(arguments, "level") is { } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            walls = walls.Where(w => w.LevelId == level.Id);
        }
        var groupBy = DocLookup.Choice(arguments, "group_by", "type", "type", "level", "type_level", "none");

        var rows = walls
            .Where(w => w.Location is LocationCurve)
            .Select(w => new
            {
                Type = w.WallType.Name,
                Level = doc.GetElement(w.LevelId)?.Name ?? "",
                Length = ((LocationCurve)w.Location).Curve.Length,
                Area = w.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0,
            })
            .ToList();
        if (rows.Count == 0) throw new CommandException("Không có tường nào.");

        var groups = rows.GroupBy(r => groupBy switch
            {
                "type" => r.Type,
                "level" => r.Level,
                "type_level" => $"{r.Type} | {r.Level}",
                _ => "Tất cả",
            })
            .Select(g => new
            {
                group = g.Key,
                count = g.Count(),
                length_m = Math.Round(g.Sum(r => r.Length) * 0.3048, 2),
                area_m2 = Math.Round(UnitConvert.SquareFeetToSquareMeters(g.Sum(r => r.Area)), 2),
            })
            .OrderByDescending(g => g.length_m)
            .ToList();

        return new
        {
            scope = Args.Has(arguments, "element_ids") ? "selection" : "model",
            walls = rows.Count,
            total_length_m = Math.Round(rows.Sum(r => r.Length) * 0.3048, 2),
            groups = Table.Of(groups),
        };
    }
}

public sealed class ExportSheetList : RevitCommandBase
{
    public override string Name => "export_sheet_list";
    public override string Category => "Dữ liệu";
    public override string Description =>
        "Danh mục sheet (số hiệu, tên, revision, người vẽ, khung tên), tùy chọn xuất CSV.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "csv_path": { "type": "string", "description": "File .csv cần xuất (tùy chọn)." },
            "name_contains": { "type": "string" },
            "parameters": { "type": "array", "items": { "type": "string" }, "description": "Thêm tham số sheet." },
            "limit": { "type": "integer", "description": "Mặc định 500." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var contains = Args.GetStringOrDefault(arguments, "name_contains");
        var extra = Args.GetStringsOrEmpty(arguments, "parameters");
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => contains == null || s.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)
                        || s.SheetNumber.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? Text(Element e, BuiltInParameter p)
        {
            var parameter = e.get_Parameter(p);
            if (parameter == null || !parameter.HasValue) return null;
            var t = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }

        var columns = new List<string> { "sheet_number", "name", "current_revision", "revision_date", "issue_date", "drawn_by", "checked_by", "approved_by", "title_block", "views", "is_placeholder" };
        columns.AddRange(extra);
        var rows = sheets.Select(s =>
        {
            var row = new List<object?>
            {
                s.SheetNumber,
                s.Name,
                Text(s, BuiltInParameter.SHEET_CURRENT_REVISION),
                Text(s, BuiltInParameter.SHEET_CURRENT_REVISION_DATE),
                Text(s, BuiltInParameter.SHEET_ISSUE_DATE),
                Text(s, BuiltInParameter.SHEET_DRAWN_BY),
                Text(s, BuiltInParameter.SHEET_CHECKED_BY),
                Text(s, BuiltInParameter.SHEET_APPROVED_BY),
                DocLookup.TitleBlock(doc, s) is { } tb ? ElementInfo.TypeName(doc, tb) : null,
                s.GetAllPlacedViews().Count,
                s.IsPlaceholder,
            };
            row.AddRange(extra.Select(name => (object?)ElementInfo.ParameterText(s, name)));
            return row.ToArray();
        }).ToList();

        string? csvPath = null;
        if (Args.GetStringOrDefault(arguments, "csv_path") is { } path && !string.IsNullOrWhiteSpace(path))
        {
            csvPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            var lines = new[] { string.Join(",", columns.Select(Csv)) }
                .Concat(rows.Select(r => string.Join(",", r.Select(v => Csv(v?.ToString() ?? "")))));
            File.WriteAllLines(csvPath, lines, new UTF8Encoding(true));
        }

        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 500);
        return new
        {
            total = rows.Count,
            csv_path = csvPath,
            sheets = new Table { Columns = columns, Rows = rows.Take(limit).ToList(), Total = rows.Count > limit ? rows.Count : null },
        };
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

public sealed class ExportSheetsPdf : RevitCommandBase
{
    public override string Name => "export_sheets_pdf";
    public override string Category => "Dữ liệu";
    // Document.Export không chạy được khi Transaction đang mở.
    public override TransactionMode TransactionMode => TransactionMode.None;
    public override RibbonButton? Ribbon => new("Xuất PDF", "pdf");
    public override string Description =>
        "Xuất sheet ra PDF (từng file \"số hiệu - tên\" hoặc gộp một file) vào thư mục chỉ định.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "output_folder": { "type": "string", "description": "Thư mục lưu PDF." },
            "sheet_numbers": { "type": "array", "items": { "type": "string" }, "description": "Trống = mọi sheet." },
            "name_contains": { "type": "string", "description": "Lọc theo số hiệu/tên sheet." },
            "combine": { "type": "boolean", "description": "Gộp một file." },
            "file_name": { "type": "string", "description": "Tên file khi gộp." },
            "color": { "type": "string", "enum": ["color", "grayscale", "black_white"], "description": "Mặc định color." }
          },
          "required": ["output_folder"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(Args.GetString(arguments, "output_folder")));
        Directory.CreateDirectory(folder);

        var numbers = Args.GetStringsOrEmpty(arguments, "sheet_numbers").Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contains = Args.GetStringOrDefault(arguments, "name_contains");
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder && s.CanBePrinted)
            .Where(s => numbers.Count == 0 || numbers.Contains(s.SheetNumber))
            .Where(s => contains == null || s.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)
                        || s.SheetNumber.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sheets.Count == 0) throw new CommandException("Không có sheet nào phù hợp để xuất.");
        var missing = numbers.Except(sheets.Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase).ToList();

        var options = new PDFExportOptions
        {
            PaperFormat = ExportPaperFormat.Default,
            ZoomType = ZoomType.Zoom,
            ZoomPercentage = 100,
            RasterQuality = RasterQualityType.High,
            HideCropBoundaries = true,
            HideScopeBoxes = true,
            HideReferencePlane = true,
            HideUnreferencedViewTags = true,
            ColorDepth = DocLookup.Choice(arguments, "color", "color", "color", "grayscale", "black_white") switch
            {
                "grayscale" => ColorDepthType.GrayScale,
                "black_white" => ColorDepthType.BlackLine,
                _ => ColorDepthType.Color,
            },
        };

        var files = new List<object>();
        var failed = new List<object>();
        if (Args.GetBoolOrDefault(arguments, "combine", false))
        {
            var name = DocLookup.SafeName(Args.GetStringOrDefault(arguments, "file_name") ?? doc.Title, "Sheets");
            options.Combine = true;
            options.FileName = name;
            if (doc.Export(folder, sheets.Select(s => s.Id).ToList(), options))
                files.Add(new { sheet = $"{sheets.Count} sheet", file = Path.Combine(folder, name + ".pdf") });
            else
                failed.Add(new { sheet = "(gộp)", reason = "Revit báo xuất thất bại" });
        }
        else
        {
            // Mỗi lần xuất một sheet nhưng vẫn để Combine = true: Revit chỉ dùng FileName khi gộp,
            // còn không gộp thì tự đặt tên theo quy tắc riêng ("Sheet-<tên>") và lệch với tên báo lại.
            options.Combine = true;
            foreach (var sheet in sheets)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var name = DocLookup.SafeName($"{sheet.SheetNumber} - {sheet.Name}", sheet.SheetNumber);
                options.FileName = name;
                try
                {
                    if (doc.Export(folder, [sheet.Id], options))
                        files.Add(new { sheet = sheet.SheetNumber, file = Path.Combine(folder, name + ".pdf") });
                    else
                        failed.Add(new { sheet = sheet.SheetNumber, reason = "Revit báo xuất thất bại" });
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    failed.Add(new { sheet = sheet.SheetNumber, reason = ex.Message });
                }
            }
        }

        return new
        {
            folder,
            exported = files.Count,
            files = Table.Of(files),
            failed = failed.Count == 0 ? null : Table.Of(failed),
            not_found_numbers = missing.Count == 0 ? null : missing,
        };
    }
}
