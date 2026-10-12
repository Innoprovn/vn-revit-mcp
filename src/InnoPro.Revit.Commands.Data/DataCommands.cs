using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Data;

public sealed class ListSchedules : RevitCommandBase
{
    public override string Name => "list_schedules";
    public override string Category => "Dữ liệu";
    public override string Description => "Danh sách bảng thống kê (schedule) trong dự án và category của từng bảng.";

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        return Table.Of(new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                element_id = s.Id.Value,
                name = s.Name,
                category = Autodesk.Revit.DB.Category.GetCategory(doc, s.Definition.CategoryId)?.Name,
            }));
    }
}

public sealed class ReadSchedule : RevitCommandBase
{
    public override string Name => "read_schedule";
    public override string Category => "Dữ liệu";
    public override RibbonButton? Ribbon => new("Đọc / xuất\nschedule", "▦");
    public override string Description =>
        "Đọc nội dung một schedule như bảng (tiêu đề cột + các dòng, đúng như hiển thị trong Revit). " +
        "Tùy chọn xuất toàn bộ ra file CSV (UTF-8, mở được bằng Excel).";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "schedule": { "type": "string", "description": "Tên schedule (xem list_schedules)." },
            "limit": { "type": "integer", "description": "Số dòng tối đa trả về, mặc định 200." },
            "csv_path": { "type": "string", "description": "Đường dẫn file .csv để xuất toàn bộ bảng (tùy chọn)." }
          },
          "required": ["schedule"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var name = Args.GetString(arguments, "schedule");
        var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().ToList();
        var schedule = schedules.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                       ?? throw new CommandException($"Không có schedule '{name}'.");

        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var rows = new List<string[]>();
        for (var r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
        {
            var row = new string[body.NumberOfColumns];
            for (var c = 0; c < body.NumberOfColumns; c++)
                row[c] = schedule.GetCellText(SectionType.Body, r, c + body.FirstColumnNumber);
            if (row.Any(v => !string.IsNullOrWhiteSpace(v))) rows.Add(row);
        }

        string? csvPath = null;
        if (Args.GetStringOrDefault(arguments, "csv_path") is { } path)
        {
            csvPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            File.WriteAllLines(csvPath, rows.Select(r => string.Join(",", r.Select(Csv))), new UTF8Encoding(true));
        }

        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);
        return new
        {
            name = schedule.Name,
            total_rows = rows.Count,
            table = new Table
            {
                Columns = rows.FirstOrDefault() ?? [],
                Rows = rows.Skip(1).Take(limit).Select(r => r.Cast<object?>().ToArray()).ToList(),
                Total = rows.Count - 1 > limit ? rows.Count - 1 : null,
            },
            csv_path = csvPath,
        };
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}

public sealed class GetMaterialQuantities : RevitCommandBase
{
    public override string Name => "get_material_quantities";
    public override string Category => "Dữ liệu";
    public override RibbonButton? Ribbon => new("Khối lượng\nvật liệu", "m³");
    public override string Description =>
        "Bóc khối lượng theo vật liệu: thể tích (m³) và diện tích (m²) của từng vật liệu, có thể lọc theo category và tầng. " +
        "Ví dụ: khối lượng bê tông cột tầng 1.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Ví dụ OST_StructuralColumns. Bỏ trống = mọi cấu kiện." },
            "level": { "type": "string" }
          }
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        if (Args.GetStringOrDefault(arguments, "category") is { } category)
            collector.OfCategoryId(CategoryResolver.Resolve(doc, category).Id);
        IEnumerable<Element> elements = collector.Where(ElementInfo.IsPhysical);
        if (Args.GetStringOrDefault(arguments, "level") is { } levelName)
        {
            var level = Lookup.Level(doc, levelName);
            elements = elements.Where(e => ElementInfo.LevelName(doc, e) == level.Name);
        }

        var totals = new Dictionary<ElementId, (double Volume, double Area, int Count)>();
        var counted = 0;
        foreach (var e in elements)
        {
            counted++;
            foreach (var materialId in e.GetMaterialIds(false))
            {
                var current = totals.GetValueOrDefault(materialId);
                totals[materialId] = (current.Volume + e.GetMaterialVolume(materialId),
                                      current.Area + e.GetMaterialArea(materialId, false),
                                      current.Count + 1);
            }
        }

        return new
        {
            elements = counted,
            materials = Table.Of(totals
                .Select(kv => new
                {
                    material = doc.GetElement(kv.Key)?.Name,
                    volume_m3 = UnitConvert.CubicFeetToCubicMeters(kv.Value.Volume),
                    area_m2 = UnitConvert.SquareFeetToSquareMeters(kv.Value.Area),
                    element_count = kv.Value.Count,
                })
                .OrderByDescending(m => m.volume_m3)),
        };
    }
}
