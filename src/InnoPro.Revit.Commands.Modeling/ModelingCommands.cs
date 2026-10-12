using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Modeling;

/// <summary>Chạy từng mục trong một lô, mục lỗi được ghi lại thay vì làm hỏng cả lô.</summary>
internal sealed class BatchResult
{
    public List<long> Created { get; } = [];
    public List<object> Failed { get; } = [];

    public void Run(int index, Func<Element> create)
    {
        try { Created.Add(create().Id.Value); }
        catch (Exception ex) when (ex is CommandException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            Failed.Add(new { index, error = ex.Message });
        }
    }

    public object ToResult() => new { created = Created.Count, element_ids = Created, failed = Failed };
}

public sealed class ListFamilyTypes : RevitCommandBase
{
    public override string Name => "list_family_types";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Liệt kê các type có trong dự án theo category (type tường, sàn, cột, dầm, cửa...). " +
        "Gọi trước khi tạo phần tử để lấy đúng tên type ('Family: Type').";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "category": { "type": "string", "description": "Ví dụ OST_Walls, OST_Floors, OST_StructuralColumns, OST_StructuralFraming, OST_Doors." },
            "name_contains": { "type": "string" },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          },
          "required": ["category"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var category = CategoryResolver.Resolve(doc, Args.GetString(arguments, "category"));
        var part = Args.GetStringOrDefault(arguments, "name_contains");
        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);

        return new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsElementType()
            .Cast<ElementType>()
            .Select(t => new { element_id = t.Id.Value, name = Lookup.FullName(t), is_active = (t as FamilySymbol)?.IsActive })
            .Where(t => part == null || t.name.Contains(part, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.name)
            .Take(limit);
    }
}

public sealed class CreateLevels : RevitCommandBase
{
    public override string Name => "create_levels";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Tạo nhiều tầng (Level) theo tên và cao độ (mm), tùy chọn tạo kèm mặt bằng. Tầng trùng tên sẽ báo lỗi riêng mục đó.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "levels": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "name": { "type": "string" }, "elevation_mm": { "type": "number" } },
                "required": ["name", "elevation_mm"]
              }
            },
            "plan_view": { "type": "string", "enum": ["none", "floor", "structural"], "description": "Loại mặt bằng tạo kèm, mặc định floor." }
          },
          "required": ["levels"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var planKind = Args.GetStringOrDefault(arguments, "plan_view", "floor");
        var viewFamily = planKind switch
        {
            "floor" => ViewFamily.FloorPlan,
            "structural" => ViewFamily.StructuralPlan,
            "none" => (ViewFamily?)null,
            _ => throw new CommandException("plan_view phải là none, floor hoặc structural."),
        };
        var viewType = viewFamily == null ? null : new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == viewFamily);

        var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Select(l => l.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batch = new BatchResult();
        var items = Args.GetArray(arguments, "levels");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            batch.Run(i, () =>
            {
                var name = Args.GetString(item, "name");
                if (!existing.Add(name)) throw new CommandException($"Đã có tầng '{name}'.");
                var level = Level.Create(doc, UnitConvert.MmToFeet(Args.GetDoubleOrDefault(item, "elevation_mm", double.NaN)
                    is var mm && !double.IsNaN(mm) ? mm : throw new CommandException("Thiếu elevation_mm.")));
                level.Name = name;
                if (viewType != null) ViewPlan.Create(doc, viewType.Id, level.Id);
                return level;
            });
        }
        return batch.ToResult();
    }
}

public sealed class CreateGrids : RevitCommandBase
{
    public override string Name => "create_grids";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Tạo lưới trục thẳng từ điểm đầu đến điểm cuối (mm, mặt bằng XY). Tên trục tùy chọn, phải duy nhất.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "grids": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "start": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } },
                  "end": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } }
                },
                "required": ["start", "end"]
              }
            }
          },
          "required": ["grids"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var batch = new BatchResult();
        var items = Args.GetArray(arguments, "grids");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            batch.Run(i, () =>
            {
                var grid = Grid.Create(doc, Line.CreateBound(Flat(Args.GetPointMm(item, "start")), Flat(Args.GetPointMm(item, "end"))));
                if (Args.GetStringOrDefault(item, "name") is { } name) grid.Name = name;
                return grid;
            });
        }
        return batch.ToResult();
    }

    private static XYZ Flat(XYZ p) => new(p.X, p.Y, 0);
}

public sealed class CreateWalls : RevitCommandBase
{
    public override string Name => "create_walls";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Tạo tường thẳng theo các đoạn (mm, mặt bằng XY) trên một tầng. Chiều cao theo height_mm hoặc tới top_level.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "walls": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "start": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } },
                  "end": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } }
                },
                "required": ["start", "end"]
              }
            },
            "level": { "type": "string", "description": "Tầng đặt chân tường." },
            "wall_type": { "type": "string", "description": "Tên type tường (xem list_family_types OST_Walls)." },
            "height_mm": { "type": "number", "description": "Chiều cao, mặc định 3000. Bỏ qua nếu có top_level." },
            "top_level": { "type": "string", "description": "Tầng đỉnh tường." },
            "base_offset_mm": { "type": "number", "description": "Cao độ chân tường so với tầng, mặc định 0." },
            "structural": { "type": "boolean", "description": "Tường kết cấu, mặc định false." }
          },
          "required": ["walls", "level", "wall_type"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var level = Lookup.Level(doc, Args.GetString(arguments, "level"));
        var wallType = Lookup.Type<WallType>(doc, Args.GetString(arguments, "wall_type"));
        var topLevel = Args.GetStringOrDefault(arguments, "top_level") is { } top ? Lookup.Level(doc, top) : null;
        var height = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "height_mm", 3000));
        var offset = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(arguments, "base_offset_mm", 0));
        var structural = Args.GetBoolOrDefault(arguments, "structural", false);

        var batch = new BatchResult();
        var items = Args.GetArray(arguments, "walls");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            batch.Run(i, () =>
            {
                var start = Args.GetPointMm(item, "start");
                var end = Args.GetPointMm(item, "end");
                var line = Line.CreateBound(new XYZ(start.X, start.Y, level.Elevation), new XYZ(end.X, end.Y, level.Elevation));
                var wall = Wall.Create(doc, line, wallType.Id, level.Id, height, offset, false, structural);
                if (topLevel != null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(topLevel.Id);
                return wall;
            });
        }
        return batch.ToResult();
    }
}

public sealed class PlaceFamilyInstances : RevitCommandBase
{
    public override string Name => "place_family_instances";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Đặt family dạng điểm (cột, móng, thiết bị, đồ nội thất...) tại các điểm (mm) trên một tầng. " +
        "Cột kết cấu được đặt đúng kiểu cột; có thể đặt tầng đỉnh và góc xoay.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "family_type": { "type": "string", "description": "'Family: Type' hoặc tên type (xem list_family_types)." },
            "points": {
              "type": "array",
              "items": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number", "description": "Cao độ so với tầng, mm." } } }
            },
            "level": { "type": "string" },
            "top_level": { "type": "string", "description": "Tầng đỉnh (cho cột)." },
            "rotation_deg": { "type": "number", "description": "Góc xoay quanh trục Z, độ. Mặc định 0." }
          },
          "required": ["family_type", "points", "level"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var symbol = Lookup.Type<FamilySymbol>(doc, Args.GetString(arguments, "family_type"));
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        var level = Lookup.Level(doc, Args.GetString(arguments, "level"));
        var topLevel = Args.GetStringOrDefault(arguments, "top_level") is { } top ? Lookup.Level(doc, top) : null;
        var angle = Args.GetDoubleOrDefault(arguments, "rotation_deg", 0) * Math.PI / 180;
        var structuralType = symbol.Category?.BuiltInCategory switch
        {
            BuiltInCategory.OST_StructuralColumns => StructuralType.Column,
            BuiltInCategory.OST_StructuralFoundation => StructuralType.Footing,
            _ => StructuralType.NonStructural,
        };

        var batch = new BatchResult();
        var points = Args.GetArray(arguments, "points");
        for (var i = 0; i < points.Count; i++)
        {
            var item = points[i];
            batch.Run(i, () =>
            {
                var x = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(item, "x", double.NaN));
                var y = UnitConvert.MmToFeet(Args.GetDoubleOrDefault(item, "y", double.NaN));
                if (double.IsNaN(x) || double.IsNaN(y)) throw new CommandException("Điểm thiếu x hoặc y.");
                var z = level.Elevation + UnitConvert.MmToFeet(Args.GetDoubleOrDefault(item, "z", 0));
                var point = new XYZ(x, y, z);

                var instance = doc.Create.NewFamilyInstance(point, symbol, level, structuralType);
                if (topLevel != null)
                    instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
                if (angle != 0)
                    ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(point, point + XYZ.BasisZ), angle);
                return instance;
            });
        }
        return batch.ToResult();
    }
}

public sealed class CreateBeams : RevitCommandBase
{
    public override string Name => "create_beams";
    public override string Category => "Dựng hình";
    public override string Description => "Tạo dầm kết cấu thẳng theo các đoạn (mm) trên một tầng, dùng type dầm (OST_StructuralFraming).";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "beams": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "start": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" } } },
                  "end": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" }, "z": { "type": "number" } } }
                },
                "required": ["start", "end"]
              }
            },
            "beam_type": { "type": "string" },
            "level": { "type": "string", "description": "Tầng tham chiếu; z của điểm tính so với tầng này." }
          },
          "required": ["beams", "beam_type", "level"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var symbol = Lookup.Type<FamilySymbol>(doc, Args.GetString(arguments, "beam_type"), BuiltInCategory.OST_StructuralFraming);
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        var level = Lookup.Level(doc, Args.GetString(arguments, "level"));
        var lift = new XYZ(0, 0, level.Elevation);

        var batch = new BatchResult();
        var items = Args.GetArray(arguments, "beams");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            batch.Run(i, () =>
            {
                var line = Line.CreateBound(Args.GetPointMm(item, "start") + lift, Args.GetPointMm(item, "end") + lift);
                return doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.Beam);
            });
        }
        return batch.ToResult();
    }
}

public sealed class CreateFloor : RevitCommandBase
{
    public override string Name => "create_floor";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Tạo một sàn từ đa giác biên (các đỉnh mm theo thứ tự, tự khép kín) trên một tầng.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "boundary": {
              "type": "array",
              "items": { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } } },
              "description": "Ít nhất 3 đỉnh, không lặp lại đỉnh đầu."
            },
            "level": { "type": "string" },
            "floor_type": { "type": "string" },
            "structural": { "type": "boolean", "description": "Mặc định false." }
          },
          "required": ["boundary", "level", "floor_type"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var level = Lookup.Level(doc, Args.GetString(arguments, "level"));
        var floorType = Lookup.Type<FloorType>(doc, Args.GetString(arguments, "floor_type"));
        var points = Args.GetArray(arguments, "boundary")
            .Select((p, i) => new XYZ(
                UnitConvert.MmToFeet(Args.GetDoubleOrDefault(p, "x", double.NaN)),
                UnitConvert.MmToFeet(Args.GetDoubleOrDefault(p, "y", double.NaN)),
                level.Elevation))
            .ToList();
        if (points.Count < 3 || points.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y)))
            throw new CommandException("boundary cần ít nhất 3 đỉnh có đủ x, y.");

        var loop = new CurveLoop();
        for (var i = 0; i < points.Count; i++)
            loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));

        var floor = Floor.Create(doc, [loop], floorType.Id, level.Id);
        if (Args.GetBoolOrDefault(arguments, "structural", false))
            floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.Set(1);
        return new { element_id = floor.Id.Value };
    }
}
