using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.QaQc;

public sealed class GetWarnings : RevitCommandBase
{
    public override string Name => "get_warnings";
    public override string Category => "QA-QC";
    public override RibbonButton? Ribbon => new("Cảnh báo", "⚠");
    public override string Description =>
        "Liệt kê cảnh báo (Warnings) của mô hình, gom theo nội dung, sắp theo số lượng giảm dần, kèm vài id phần tử mẫu. " +
        "Dùng để đánh giá chất lượng mô hình hoặc tìm lỗi trùng, chồng, không nối.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "contains": { "type": "string", "description": "Chỉ lấy cảnh báo có nội dung chứa chuỗi này." },
            "sample_ids": { "type": "integer", "description": "Số id mẫu mỗi loại cảnh báo, mặc định 10." },
            "limit": { "type": "integer", "description": "Số loại cảnh báo tối đa, mặc định 50." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var contains = Args.GetStringOrDefault(arguments, "contains");
        var samples = (int)Args.GetLongOrDefault(arguments, "sample_ids", 10);
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 50);

        var warnings = context.Document.GetWarnings()
            .Where(w => contains == null || w.GetDescriptionText().Contains(contains, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var groups = warnings
            .GroupBy(w => w.GetDescriptionText())
            .Select(g => new
            {
                description = g.Key,
                severity = g.First().GetSeverity().ToString(),
                count = g.Count(),
                element_ids = g.SelectMany(w => w.GetFailingElements().Concat(w.GetAdditionalElements()))
                    .Select(id => id.Value).Distinct().Take(samples),
            })
            .OrderByDescending(g => g.count)
            .ToList();

        return new { total = warnings.Count, types = groups.Count, warnings = Table.Of(groups, limit) };
    }
}

public sealed class FindDuplicateElements : RevitCommandBase
{
    public override string Name => "find_duplicate_elements";
    public override string Category => "QA-QC";
    public override RibbonButton? Ribbon => new("Trùng lặp", "⧉");
    public override string Description =>
        "Tìm phần tử bị trùng: cùng category, cùng type, cùng vị trí (điểm đặt hoặc đường định vị) trong sai số cho trước. " +
        "Hay gặp với cột, dầm, tường copy chồng lên nhau. Mỗi nhóm trả về danh sách id, giữ id đầu và xóa các id còn lại nếu cần.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Giới hạn một category (OST_StructuralColumns...). Bỏ trống = mọi cấu kiện." },
            "tolerance_mm": { "type": "number", "description": "Sai số vị trí, mặc định 5 mm." },
            "limit": { "type": "integer", "description": "Số nhóm trùng tối đa trả về, mặc định 100." }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var tolerance = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "tolerance_mm", 5));
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 100);

        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (Args.GetStringOrDefault(arguments, "category") is { } category)
            collector.OfCategoryId(CategoryResolver.Resolve(doc, category).Id);

        var groups = collector
            .Where(ElementInfo.IsPhysical)
            .Select(e => (Element: e, Key: Key(e, tolerance)))
            .Where(x => x.Key != null)
            .GroupBy(x => x.Key!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.Element).ToList())
            .ToList();

        return new
        {
            duplicate_groups = groups.Count,
            redundant_elements = groups.Sum(g => g.Count - 1),
            groups = Table.Of(groups.Select(g => new
            {
                category = g[0].Category?.Name,
                type_name = ElementInfo.TypeName(doc, g[0]),
                element_ids = g.Select(e => e.Id.Value),
            }), limit),
        };
    }

    private static string? Key(Element e, double tolerance)
    {
        string Round(XYZ p) => $"{Math.Round(p.X / tolerance)},{Math.Round(p.Y / tolerance)},{Math.Round(p.Z / tolerance)}";

        var prefix = $"{e.Category.Id.Value}|{e.GetTypeId().Value}|";
        return e.Location switch
        {
            LocationPoint lp => prefix + Round(lp.Point),
            LocationCurve { Curve: { IsBound: true } curve } =>
                // Không phân biệt chiều của đường: sắp hai đầu mút theo thứ tự.
                prefix + string.Join("~", new[] { Round(curve.GetEndPoint(0)), Round(curve.GetEndPoint(1)) }.OrderBy(s => s, StringComparer.Ordinal)),
            _ => null,
        };
    }
}

public sealed class CheckMissingParameters : RevitCommandBase
{
    public override string Name => "check_missing_parameters";
    public override string Category => "QA-QC";
    public override RibbonButton? Ribbon => new("Tham số trống", "∅");
    public override string Description =>
        "Kiểm tra phần tử của một category có điền đủ các tham số yêu cầu không (ví dụ Mark, Comments, tham số dự án). " +
        "Trả về số phần tử thiếu theo từng tham số và id mẫu.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Category cần kiểm tra." },
            "parameters": { "type": "array", "items": { "type": "string" }, "description": "Tên các tham số bắt buộc có giá trị." },
            "sample_ids": { "type": "integer", "description": "Số id mẫu mỗi tham số, mặc định 20." }
          },
          "required": ["category", "parameters"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var category = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category"));
        var parameters = Args.GetStringsOrEmpty(arguments, "parameters");
        if (parameters.Count == 0) throw new CommandException("Cần ít nhất một tham số trong 'parameters'.");
        var samples = (int)Args.GetLongOrDefault(arguments, "sample_ids", 20);

        var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsNotElementType().ToList();
        return new
        {
            category = category.Name,
            checked_elements = elements.Count,
            results = parameters.Select(name =>
            {
                var missing = elements.Where(e => ElementInfo.ParameterText(e, name) == null).ToList();
                return new
                {
                    parameter = name,
                    missing = missing.Count,
                    element_ids = missing.Take(samples).Select(e => e.Id.Value),
                };
            }).ToList(),
        };
    }
}

public sealed class FindClashes : RevitCommandBase
{
    private const int MaxSourceElements = 5000;

    public override string Name => "find_clashes";
    public override string Category => "QA-QC";
    public override RibbonButton? Ribbon => new("Va chạm", "✕");
    public override string Description =>
        "Tìm va chạm hình học giữa hai category trong mô hình hiện tại (ví dụ ống gió với dầm). " +
        "Dùng giao khối thật (ElementIntersectsElementFilter), không chỉ hộp bao. Tối đa 5000 phần tử nhóm A.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category_a": { "type": "string", "description": "Ví dụ OST_DuctCurves." },
            "category_b": { "type": "string", "description": "Ví dụ OST_StructuralFraming." },
            "level": { "type": "string", "description": "Chỉ xét phần tử nhóm A thuộc tầng này." },
            "limit": { "type": "integer", "description": "Số cặp va chạm tối đa trả về, mặc định 200." }
          },
          "required": ["category_a", "category_b"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var categoryA = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category_a"));
        var categoryB = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category_b"));
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);

        IEnumerable<Element> sources = new FilteredElementCollector(doc).OfCategoryId(categoryA.Id).WhereElementIsNotElementType();
        if (Args.GetStringOrDefault(arguments, "level") is { } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            sources = sources.Where(e => e.LevelId == level.Id);
        }
        var sourceList = sources.Take(MaxSourceElements + 1).ToList();
        if (sourceList.Count > MaxSourceElements)
            throw new CommandException($"Nhóm A có hơn {MaxSourceElements} phần tử. Hãy giới hạn theo 'level'.");

        var clashes = new List<(long element_a, long element_b)>();
        var clashingA = 0;
        var skipped = 0;
        foreach (var a in sourceList)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var box = a.get_BoundingBox(null);
            if (box == null) continue;

            ICollection<ElementId> hits;
            try
            {
                hits = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryB.Id)
                    .WhereElementIsNotElementType()
                    .Excluding([a.Id])
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(box.Min, box.Max)))
                    .WherePasses(new ElementIntersectsElementFilter(a))
                    .ToElementIds();
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                skipped++; // phần tử không có khối đặc để giao
                continue;
            }
            if (hits.Count == 0) continue;

            clashingA++;
            foreach (var b in hits)
            {
                if (clashes.Count >= limit) break;
                clashes.Add((a.Id.Value, b.Value));
            }
        }

        return new
        {
            category_a = categoryA.Name,
            category_b = categoryB.Name,
            checked_a = sourceList.Count,
            skipped_no_solid = skipped,
            clashing_a = clashingA,
            clashes = Table.Of(clashes.Select(c => new { c.element_a, c.element_b })),
        };
    }
}
