using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Tự tạo lớp bê tông lót — cũng là Foundation Slab (OST_StructuralFoundation) giống đài móng — ngay dưới đáy MỖI đài đã xử lý (mới lẫn đã có sẵn), offset đúng theo biên dạng THẬT của đài (kể cả đài xoay/không chữ nhật).</summary>
internal static class PileLotFloorPlacer
{
    /// <summary>Ngưỡng mở rộng bounding box (X/Y) khi chống đặt trùng lớp lót — giống chuẩn chống trùng cọc/đài.</summary>
    private const double DuplicateExpandMm = 5.0;

    /// <summary>Đặt 1 Foundation Slab lớp lót cho từng ElementId đài trong resolvedCapIds — bỏ qua đài null (lỗi/không đặt được) và đài đã có lớp lót từ trước. knownCapProfiles (đài mới tạo cùng lượt) ưu tiên dùng để offset — tránh đọc lại qua Sketch.Profile gây lệch/lẻ số.</summary>
    public static LotFloorSummary Place(
        Document doc, IReadOnlyList<ElementId?> resolvedCapIds, FloorType lotType, double offsetMm, Level capLevel,
        IReadOnlyDictionary<ElementId, CurveLoop> knownCapProfiles)
    {
        var offsetFeet = UnitUtils.ConvertToInternalUnits(offsetMm, UnitTypeId.Millimeters);
        var created = 0;
        var skipped = 0;
        var failed = 0;
        var createdIds = new List<ElementId>();

        var existingLots = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .Where(f => f.FloorType.Id == lotType.Id);
        var existingBoxes = PileFoundationPlacer.BuildExistingBoxes(existingLots, DuplicateExpandMm);

        doc.Regenerate(); // Dam bao Sketch cua dai vua tao trong cung lot Dat da san sang de doc profile that.

        foreach (var capId in resolvedCapIds)
        {
            if (capId is null)
                continue;

            var capElement = doc.GetElement(capId);
            if (capElement is null)
            {
                failed++;
                continue;
            }

            BoundingBoxXYZ? bb;
            try
            {
                bb = capElement.get_BoundingBox(null);
            }
            catch (Exception)
            {
                bb = null;
            }

            if (bb is null)
            {
                failed++;
                continue;
            }

            var topZ = bb.Min.Z;
            var center = new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, 0);

            if (PileFoundationPlacer.FindDuplicate(center, existingBoxes) is not null)
            {
                skipped++;
                continue;
            }

            try
            {
                var loop = BuildLoop(doc, capId, capElement, bb, offsetFeet, topZ, knownCapProfiles);
                var floor = Floor.Create(doc, new List<CurveLoop> { loop }, lotType.Id, capLevel.Id, true, null, 0.0);

                var offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                offsetParam?.Set(topZ - capLevel.Elevation);

                createdIds.Add(floor.Id);
                created++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        return new LotFloorSummary(created, skipped, failed, createdIds);
    }

    /// <summary>Offset đúng theo biên dạng thật của đài (kể cả xoay/không chữ nhật). Ưu tiên loop sạch đã biết (đài mới tạo cùng lượt, knownCapProfiles) — chỉ đọc qua Sketch.Profile cho đài có sẵn từ trước, fallback hình chữ nhật bbox nếu cả 2 đều không có/không đọc được.</summary>
    private static CurveLoop BuildLoop(
        Document doc, ElementId capId, Element capElement, BoundingBoxXYZ bb, double offsetFeet, double topZ,
        IReadOnlyDictionary<ElementId, CurveLoop> knownCapProfiles)
    {
        var profile = knownCapProfiles.TryGetValue(capId, out var known)
            ? known
            : capElement is Floor floor ? TryGetFloorProfileLoop(doc, floor) : null;

        if (profile is not null)
        {
            var offset = OffsetOutward(profile, offsetFeet);
            if (offset is not null)
                return AtZ(offset, topZ);
        }

        return BuildRectangleLoop(bb.Min.X - offsetFeet, bb.Max.X + offsetFeet, bb.Min.Y - offsetFeet, bb.Max.Y + offsetFeet, topZ);
    }

    /// <summary>Đọc vòng biên ngoài (outer loop) từ Sketch thật của Floor — null nếu không đọc được.</summary>
    private static CurveLoop? TryGetFloorProfileLoop(Document doc, Floor floor)
    {
        try
        {
            if (doc.GetElement(floor.SketchId) is not Sketch sketch)
                return null;

            var profile = sketch.Profile;
            if (profile.IsEmpty)
                return null;

            var loop = new CurveLoop();
            foreach (Curve curve in profile.get_Item(0))
                loop.Append(curve);

            return loop;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>CreateViaOffset có thể co vào trong tuỳ chiều loop — thử cả 2 dấu rồi lấy đúng hướng phình to hơn (diện tích bbox lớn hơn).</summary>
    private static CurveLoop? OffsetOutward(CurveLoop loop, double offsetFeet)
    {
        var outward = TryOffset(loop, offsetFeet);
        var inward = TryOffset(loop, -offsetFeet);

        if (outward is null)
            return inward;
        if (inward is null)
            return outward;

        return BoundingArea(outward) >= BoundingArea(inward) ? outward : inward;
    }

    private static CurveLoop? TryOffset(CurveLoop loop, double offsetFeet)
    {
        try
        {
            return CurveLoop.CreateViaOffset(loop, offsetFeet, XYZ.BasisZ);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double BoundingArea(CurveLoop loop)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var curve in loop)
        {
            xs.Add(curve.GetEndPoint(0).X);
            ys.Add(curve.GetEndPoint(0).Y);
        }

        if (xs.Count == 0)
            return 0;

        return (xs.Max() - xs.Min()) * (ys.Max() - ys.Min());
    }

    /// <summary>Dịch cả loop về đúng cao độ Z cho trước, giữ nguyên hình dạng X/Y và loại curve.</summary>
    private static CurveLoop AtZ(CurveLoop loop, double z)
    {
        var currentZ = loop.First().GetEndPoint(0).Z;
        var translate = Transform.CreateTranslation(new XYZ(0, 0, z - currentZ));

        var result = new CurveLoop();
        foreach (var curve in loop)
            result.Append(curve.CreateTransformed(translate));

        return result;
    }

    /// <summary>Dựng CurveLoop chữ nhật world-aligned tại cao độ Z cho trước — fallback khi không đọc được biên dạng thật.</summary>
    private static CurveLoop BuildRectangleLoop(double minX, double maxX, double minY, double maxY, double z)
    {
        var p0 = new XYZ(minX, minY, z);
        var p1 = new XYZ(maxX, minY, z);
        var p2 = new XYZ(maxX, maxY, z);
        var p3 = new XYZ(minX, maxY, z);

        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
        return loop;
    }
}

/// <summary>Kết quả 1 lượt đặt lớp lót cho toàn bộ đài đã xử lý trong lượt Đặt.</summary>
internal sealed record LotFloorSummary(int Created, int Skipped, int Failed, IReadOnlyList<ElementId> CreatedLotFloorIds);
