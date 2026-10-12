using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.DirectShapeCut;

public sealed class CutOptions
{
    /// <summary>Thử cắt "sống" (quan hệ cắt do Revit quản lý, dời dao thì lỗ đi theo) trước khi trừ
    /// hình học vĩnh viễn.</summary>
    public bool PreferLiveCut { get; init; } = true;

    /// <summary>Xoá khối dao sau khi trừ hình học. Không áp dụng cho cắt sống vì xoá dao là mất lỗ.</summary>
    public bool DeleteCutterAfterSubtract { get; init; }
}

public enum CutMethod
{
    /// <summary>InstanceVoidCutUtils - void family cắt element, Revit quản lý quan hệ.</summary>
    InstanceVoid,

    /// <summary>SolidSolidCutUtils - hai khối đặc cắt nhau, Revit quản lý quan hệ.</summary>
    SolidSolid,

    /// <summary>Trừ thẳng hình học rồi ghi đè shape của DirectShape. Vĩnh viễn, không đi theo dao.</summary>
    GeometrySubtract,

    /// <summary>Không cắt được.</summary>
    Failed
}

public sealed record CutOutcome(CutMethod Method, string? Note);

/// <summary>Cắt khối DirectShape.
///
/// Bối cảnh: Revit KHÔNG cho dùng lệnh "Cut" trên Ribbon với DirectShape — đã kiểm tra metadata
/// RevitAPI 2025, DirectShapeOptions chỉ có ReferencingOption và RoomBoundingOption, không có tuỳ
/// chọn nào bật cuttable. Nên tool đi ba nấc, dừng ở nấc đầu tiên chạy được:
///   1. InstanceVoidCutUtils - dao là family instance chứa void và Revit chấp nhận cắt element đó.
///   2. SolidSolidCutUtils - hai khối đặc, Revit chấp nhận cắt nhau.
///   3. Trừ thẳng hình học (BooleanOperationsUtils) rồi SetShape lại cho DirectShape.
///
/// Nấc 1 và 2 là cắt "sống": dời/sửa dao thì lỗ đi theo, xoá dao thì lỗ mất. Nấc 3 là vĩnh viễn:
/// khối bị ghi đè hình mới, dao trở thành không liên quan (có thể xoá).</summary>
public static class CutService
{
    private const double MinVolume = 1e-9;

    public static CutOutcome Cut(Document doc, Element target, Element cutter, CutOptions options)
    {
        if (target.Id == cutter.Id)
            return new CutOutcome(CutMethod.Failed, "Khối cắt và khối bị cắt là cùng một element.");

        string? liveNote = null;

        if (options.PreferLiveCut)
        {
            var live = TryLiveCut(doc, target, cutter, out liveNote);
            if (live is not null)
                return new CutOutcome(live.Value, null);
        }

        return Subtract(doc, target, cutter, options, liveNote);
    }

    private static CutMethod? TryLiveCut(Document doc, Element target, Element cutter, out string? note)
    {
        note = null;

        try
        {
            if (InstanceVoidCutUtils.IsVoidInstanceCuttingElement(cutter) &&
                InstanceVoidCutUtils.CanBeCutWithVoid(target))
            {
                if (InstanceVoidCutUtils.InstanceVoidCutExists(target, cutter))
                    return CutMethod.InstanceVoid;

                InstanceVoidCutUtils.AddInstanceVoidCut(doc, target, cutter);
                return CutMethod.InstanceVoid;
            }
        }
        catch (Exception ex)
        {
            note = ex.Message;
        }

        try
        {
            if (SolidSolidCutUtils.IsAllowedForSolidCut(target) &&
                SolidSolidCutUtils.IsAllowedForSolidCut(cutter) &&
                SolidSolidCutUtils.CanElementCutElement(cutter, target, out _))
            {
                SolidSolidCutUtils.AddCutBetweenSolids(doc, target, cutter);
                return CutMethod.SolidSolid;
            }
        }
        catch (Exception ex)
        {
            note ??= ex.Message;
        }

        return null;
    }

    private static CutOutcome Subtract(
        Document doc, Element target, Element cutter, CutOptions options, string? liveNote)
    {
        if (target is not DirectShape directShape)
        {
            return new CutOutcome(CutMethod.Failed,
                "Revit không cho cắt sống element này, mà chỉ DirectShape mới ghi đè hình học được.");
        }

        var targetSolids = GeometryReader.Solids(target);
        if (targetSolids.Count == 0)
            return new CutOutcome(CutMethod.Failed, "Khối bị cắt không có hình học đặc.");

        var knives = GeometryReader.Solids(cutter);
        if (knives.Count == 0)
        {
            return new CutOutcome(CutMethod.Failed,
                "Khối dao không có hình học đặc để trừ. Nếu dao là family chỉ chứa void thì Revit " +
                "không trả về hình học void — dựng dao bằng khối đặc (generic model, mass, " +
                "DirectShape) rồi cắt lại.");
        }

        var before = targetSolids.Sum(solid => solid.Volume);
        var results = new List<GeometryObject>();
        var after = 0.0;

        foreach (var solid in targetSolids)
        {
            var current = solid;

            foreach (var knife in knives)
            {
                try
                {
                    var next = BooleanOperationsUtils.ExecuteBooleanOperation(
                        current, knife, BooleanOperationsType.Difference);
                    if (next is not null)
                        current = next;
                }
                catch (Exception ex)
                {
                    return new CutOutcome(CutMethod.Failed, $"Trừ hình học thất bại: {ex.Message}");
                }

                if (current.Volume <= MinVolume)
                    break;
            }

            if (current.Volume > MinVolume)
            {
                results.Add(current);
                after += current.Volume;
            }
        }

        if (results.Count == 0)
        {
            return new CutOutcome(CutMethod.Failed,
                "Dao cắt hết sạch khối - Revit không cho DirectShape rỗng nên tool không ghi đè.");
        }

        if (before - after <= MinVolume)
            return new CutOutcome(CutMethod.Failed, "Dao không giao với khối này nên không có gì bị cắt.");

        directShape.SetShape(results);

        if (options.DeleteCutterAfterSubtract)
        {
            try
            {
                doc.Delete(cutter.Id);
            }
            catch (Exception)
            {
                // Dao bị pin/khoá - giữ lại cũng không ảnh hưởng kết quả cắt.
            }
        }

        return new CutOutcome(CutMethod.GeometrySubtract, liveNote);
    }
}

public static class GeometryReader
{
    private const double MinVolume = 1e-9;

    /// <summary>Solid của element trong toạ độ model.</summary>
    public static List<Solid> Solids(Element element)
    {
        var solids = new List<Solid>();
        var geometry = element.get_Geometry(new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        });

        if (geometry is not null)
            Collect(geometry, solids, 0);

        return solids;
    }

    private static void Collect(GeometryElement geometry, List<Solid> solids, int depth)
    {
        if (depth > 8)
            return;

        foreach (var geometryObject in geometry)
        {
            switch (geometryObject)
            {
                case Solid solid when solid.Volume > MinVolume && solid.Faces.Size > 0:
                    solids.Add(solid);
                    break;
                case GeometryInstance instance:
                    Collect(instance.GetInstanceGeometry(), solids, depth + 1);
                    break;
            }
        }
    }
}
