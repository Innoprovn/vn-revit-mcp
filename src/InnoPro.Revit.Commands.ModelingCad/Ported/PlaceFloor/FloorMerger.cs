using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceFloor;

/// <summary>Gộp các Floor cùng FloorType (đồng nghĩa cùng độ dày) + cùng Height Offset trên 1 Level thành 1 Floor duy nhất bằng Boolean Union (BooleanOperationsUtils) — không cần các sàn tiếp giáp nhau vì Floor.Create nhận nhiều vùng biên rời rạc trong 1 profile.</summary>
internal static class FloorMerger
{
    public static MergeSummary Merge(Document doc, Level level)
    {
        var floors = new FilteredElementCollector(doc)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .Where(f => f.LevelId == level.Id)
            .ToList();

        var groups = floors
            .GroupBy(f => (TypeId: f.FloorType.Id, Offset: RoundOffset(f)))
            .Where(g => g.Count() >= 2)
            .ToList();

        var groupsMerged = 0;
        var floorsMerged = 0;
        var failed = 0;
        var errors = new List<string>();


        foreach (var group in groups)
        {
            var groupFloors = group.ToList();
            var typeName = groupFloors[0].FloorType.Name;

            var unioned = UnionGroup(groupFloors);
            if (unioned is null)
            {
                failed++;
                errors.Add($"Nhóm {groupFloors.Count} sàn (Type \"{typeName}\"): lỗi hình học khi gộp, bỏ qua.");
                continue;
            }

            var mergedLoops = FloorSolidHelper.ExtractTopLoops(unioned);
            if (mergedLoops.Count == 0)
            {
                failed++;
                errors.Add($"Nhóm {groupFloors.Count} sàn (Type \"{typeName}\"): không đọc lại được biên sau khi gộp, bỏ qua.");
                continue;
            }

            try
            {
                var newFloor = Floor.Create(doc, mergedLoops, group.Key.TypeId, level.Id, true, null, 0.0);
                var offsetFeet = groupFloors[0].get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
                newFloor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(offsetFeet);

                doc.Delete(groupFloors.Select(f => f.Id).ToList());

                floorsMerged += groupFloors.Count;
                groupsMerged++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Nhóm {groupFloors.Count} sàn (Type \"{typeName}\"): {ex.Message}");
            }
        }


        return new MergeSummary(groupsMerged, floorsMerged, failed, errors);
    }

    /// <summary>Union tuần tự từng Floor trong nhóm thành 1 solid duy nhất — dừng và trả về null nếu bất kỳ bước nào đọc biên/Boolean thất bại.</summary>
    private static Solid? UnionGroup(List<Floor> groupFloors)
    {
        Solid? unioned = null;

        foreach (var floor in groupFloors)
        {
            var top = FloorSolidHelper.GetTopLoops(floor);
            if (top is null)
                return null;

            var helper = FloorSolidHelper.BuildHelperSolid(top.Value.Loops);
            if (helper is null)
                return null;

            if (unioned is null)
            {
                unioned = helper;
                continue;
            }

            try
            {
                unioned = BooleanOperationsUtils.ExecuteBooleanOperation(unioned, helper, BooleanOperationsType.Union);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return unioned;
    }

    private static double RoundOffset(Floor floor)
    {
        var raw = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0.0;
        return Math.Round(raw, 4);
    }
}

/// <summary>Kết quả 1 lượt "Gộp sàn".</summary>
internal sealed record MergeSummary(int GroupsMerged, int FloorsMerged, int Failed, List<string> Errors);
