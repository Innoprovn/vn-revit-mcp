using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Commands.Core;

/// <summary>Phần tử theo id hoặc theo category, trong view đang mở.</summary>
internal static class Targets
{
    public static List<Element> InView(Document doc, View view, JsonElement arguments)
    {
        if (Args.Has(arguments, "element_ids"))
            return Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).Where(e => e != null).ToList()!;
        if (Args.GetStringOrDefault(arguments, "category") is { Length: > 0 } name)
        {
            var category = CategoryResolver.Resolve(doc, name);
            return new FilteredElementCollector(doc, view.Id).OfCategoryId(category.Id).WhereElementIsNotElementType().ToList();
        }
        throw new CommandException("Truyền element_ids hoặc category.");
    }
}

public sealed class ColorElements : RevitCommandBase
{
    private static readonly string[] Palette =
        ["#E6194B", "#3CB44B", "#4363D8", "#F58231", "#911EB4", "#42D4F4", "#F032E6", "#BFEF45", "#469990", "#9A6324", "#800000", "#000075"];

    public override string Name => "color_elements";
    public override string Category => "Giao diện";
    public override bool Pinned => true;
    public override string Description =>
        "Tô màu phần tử trên view đang mở (ghi đè đồ họa, không đổi mô hình): theo id hoặc category; có parameter thì mỗi giá trị một màu " +
        "và trả bảng chú giải. reset=true để bỏ tô màu.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" } },
            "category": { "type": "string", "description": "Tên category, vd Walls, Tường, Rooms." },
            "parameter": { "type": "string", "description": "Tô theo giá trị tham số này (vd Type Name, Level, Fire Rating)." },
            "color": { "type": "string", "description": "Một màu cho tất cả, dạng #RRGGBB. Mặc định đỏ." },
            "reset": { "type": "boolean", "description": "Bỏ tô màu các phần tử này." }
          }
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = doc.ActiveView;
        var elements = Targets.InView(doc, view, arguments);
        if (Args.GetBoolOrDefault(arguments, "reset", false))
        {
            foreach (var e in elements) view.SetElementOverrides(e.Id, new OverrideGraphicSettings());
            return new { view = view.Name, reset = elements.Count };
        }

        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
        OverrideGraphicSettings Settings(Color c)
        {
            var o = new OverrideGraphicSettings().SetProjectionLineColor(c).SetCutLineColor(c);
            if (solid != null)
                o.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(c)
                 .SetCutForegroundPatternId(solid.Id).SetCutForegroundPatternColor(c);
            return o;
        }

        if (Args.GetStringOrDefault(arguments, "parameter") is not { Length: > 0 } parameter)
        {
            var color = Parse(Args.GetStringOrDefault(arguments, "color", "#E6194B")!);
            foreach (var e in elements) view.SetElementOverrides(e.Id, Settings(color));
            return new { view = view.Name, colored = elements.Count };
        }

        var groups = elements.GroupBy(e => Value(doc, e, parameter)).OrderByDescending(g => g.Count()).ToList();
        var legend = new List<object>();
        for (var i = 0; i < groups.Count; i++)
        {
            var hex = Palette[i % Palette.Length];
            foreach (var e in groups[i]) view.SetElementOverrides(e.Id, Settings(Parse(hex)));
            legend.Add(new { value = groups[i].Key, color = hex, count = groups[i].Count() });
        }
        return new { view = view.Name, colored = elements.Count, parameter, legend = Table.Of(legend) };
    }

    private static string Value(Document doc, Element e, string name)
    {
        var p = e.LookupParameter(name) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name);
        if (name.Equals("Type Name", StringComparison.OrdinalIgnoreCase) || name.Equals("Loại", StringComparison.OrdinalIgnoreCase))
            return doc.GetElement(e.GetTypeId())?.Name ?? "(không có)";
        if (p == null || !p.HasValue) return "(trống)";
        return p.AsValueString() ?? p.AsString() ?? "(trống)";
    }

    private static Color Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new CommandException($"Màu '{hex}' không hợp lệ, dùng dạng #RRGGBB.");
        return new Color((byte)(v >> 16), (byte)(v >> 8 & 0xFF), (byte)(v & 0xFF));
    }
}

public sealed class ViewVisibility : RevitCommandBase
{
    public override string Name => "view_visibility";
    public override string Category => "Giao diện";
    public override string Description =>
        "Ẩn/hiện/cô lập phần tử trên view đang mở: isolate (cô lập tạm), hide (ẩn hẳn trong view), unhide, reset (bỏ cô lập/ẩn tạm). " +
        "Theo element_ids hoặc category.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["isolate", "hide", "unhide", "reset"] },
            "element_ids": { "type": "array", "items": { "type": "integer" } },
            "category": { "type": "string" }
          },
          "required": ["action"]
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var view = doc.ActiveView;
        var action = Args.GetString(arguments, "action");
        if (action == "reset")
        {
            view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            return new { view = view.Name, action };
        }
        var ids = Targets.InView(doc, view, arguments).Select(e => e.Id).ToList();
        if (action == "unhide" && !Args.Has(arguments, "element_ids") && Args.GetStringOrDefault(arguments, "category") is { } cat)
            ids = new FilteredElementCollector(doc).OfCategoryId(CategoryResolver.Resolve(doc, cat).Id).WhereElementIsNotElementType().ToElementIds().ToList();
        if (ids.Count == 0) return new { view = view.Name, action, count = 0 };
        switch (action)
        {
            case "isolate": view.IsolateElementsTemporary(ids); break;
            case "hide": view.HideElements(ids.Where(id => doc.GetElement(id).CanBeHidden(view)).ToList()); break;
            case "unhide": view.UnhideElements(ids); break;
            default: throw new CommandException("action là isolate, hide, unhide hoặc reset.");
        }
        return new { view = view.Name, action, count = ids.Count };
    }
}

public sealed class CreateRooms : RevitCommandBase
{
    public override string Name => "create_rooms";
    public override string Category => "Dựng hình";
    public override string Description =>
        "Tạo phòng tự động trong mọi vùng kín của một tầng (như Place Rooms Automatically), tùy chọn gắn tag trên mặt bằng đang mở. " +
        "Có names thì đặt tên lần lượt.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "level": { "type": "string", "description": "Tên tầng. Mặc định tầng của mặt bằng đang mở." },
            "tag": { "type": "boolean", "description": "Gắn tag phòng trên mặt bằng đang mở. Mặc định true." },
            "names": { "type": "array", "items": { "type": "string" }, "description": "Tên đặt lần lượt cho phòng mới." }
          }
        }
        """;
    public override TransactionMode TransactionMode => TransactionMode.Auto;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var plan = doc.ActiveView as ViewPlan;
        var level = Args.GetStringOrDefault(arguments, "level") is { Length: > 0 } name ? Lookup.Level(doc, name)
            : plan?.GenLevel ?? throw new CommandException("Mở mặt bằng của tầng cần tạo phòng hoặc truyền level.");
        // Phase phải trùng phase của view thì mới gắn tag được.
        var phase = (plan?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() is { } pid && doc.GetElement(pid) is Phase p ? p : null)
                    ?? doc.Phases.Cast<Phase>().LastOrDefault() ?? throw new CommandException("Dự án không có phase.");
        var ids = doc.Create.NewRooms2(level, phase);
        var names = Args.Has(arguments, "names") ? Args.GetStringsOrEmpty(arguments, "names") : [];
        var rooms = ids.Select(doc.GetElement).OfType<Room>().ToList();
        for (var i = 0; i < rooms.Count && i < names.Count; i++) rooms[i].Name = names[i];

        var tagged = 0;
        if (Args.GetBoolOrDefault(arguments, "tag", true) && plan != null && plan.GenLevel?.Id == level.Id)
            foreach (var room in rooms)
                if (room.Location is LocationPoint lp)
                {
                    doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(lp.Point.X, lp.Point.Y), plan.Id);
                    tagged++;
                }

        doc.Regenerate();
        return new
        {
            level = level.Name,
            created = rooms.Count,
            tagged,
            rooms = Table.Of(rooms.Select(r => new { id = r.Id.Value, name = r.Name, number = r.Number, area_m2 = Math.Round(r.Area * 0.09290304, 2) }).ToList()),
            note = rooms.Count == 0 ? "Không có vùng kín mới (đã có phòng hoặc tường chưa khép kín)." : null,
        };
    }
}
