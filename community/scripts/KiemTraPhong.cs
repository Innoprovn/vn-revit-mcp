using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using InnoPro.Revit.Sdk;

// Kiểm tra phòng lỗi: chưa đặt (unplaced), không khép kín (diện tích 0), trùng số phòng.
public sealed class CheckRooms : RevitCommandBase
{
    public override string Name => "check_rooms";
    public override string Category => "QA-QC";
    public override string Description =>
        "Kiểm tra phòng (Room) lỗi: chưa đặt vào mô hình, không khép kín (diện tích 0), và trùng số phòng. Trả về danh sách để sửa.";
    public override RibbonButton Ribbon => new("Kiểm tra\nphòng", "▢");

    public override object Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .Cast<Room>().ToList();

        var unplaced = rooms.Where(r => r.Location == null).ToList();
        var notEnclosed = rooms.Where(r => r.Location != null && r.Area <= 0).ToList();
        var duplicates = rooms.Where(r => r.Location != null && !string.IsNullOrWhiteSpace(r.Number))
            .GroupBy(r => r.Number).Where(g => g.Count() > 1).ToList();

        object Row(Room r) => new { element_id = r.Id.Value, number = r.Number, name = r.Name, level = r.Level?.Name };

        return new
        {
            total = rooms.Count,
            unplaced = Table.Of(unplaced.Select(Row), 100),
            not_enclosed = Table.Of(notEnclosed.Select(Row), 100),
            duplicate_numbers = Table.Of(duplicates.Select(g => new { number = g.Key, element_ids = g.Select(r => r.Id.Value) }), 100),
        };
    }
}
