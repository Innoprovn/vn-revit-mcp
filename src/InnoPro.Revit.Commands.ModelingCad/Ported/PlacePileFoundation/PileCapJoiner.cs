using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Tự động Join Geometry cọc cắt vào đài/lớp lót — cọc luôn là phần tử cắt.</summary>
internal static class PileCapJoiner
{
    /// <summary>Bounding box mở rộng ~30mm trước khi so giao nhau — copy nguyên dung sai từ AutoJoin.</summary>
    private const double ToleranceFeet = 0.1;

    /// <summary>Join MỌI cọc cùng Family (mới lẫn có sẵn) với MỌI Foundation Slab liên quan — đài (capFloorTypeId) VÀ lớp lót (lotFloorTypeId), mới tạo lẫn có sẵn — mặc định cọc luôn ăn slab foundation, không phân biệt đài/lót hay mới/cũ. Cả 2 id null thì bỏ qua.</summary>
    public static JoinSummary JoinPilesToFoundationSlabs(Document doc, View view, IReadOnlyCollection<string> pileFamilyNames, ElementId? capFloorTypeId, ElementId? lotFloorTypeId)
    {
        if (pileFamilyNames.Count == 0 || (capFloorTypeId is null && lotFloorTypeId is null))
            return new JoinSummary(0, 0, 0);

        var allPileIds = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.Symbol?.Family?.Name is { } n && pileFamilyNames.Contains(n))
            .Select(fi => fi.Id)
            .ToList();

        // Dai gio la Floor (v7 Job S) - nhung van quet ca FamilyInstance de khong bo sot dai kieu cu (truoc v7).
        var familyCapIds = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.Symbol?.Family?.Name is { } n && !pileFamilyNames.Contains(n))
            .Select(fi => fi.Id);

        // Dai va lop lot deu la Floor cung category (OST_StructuralFoundation) - loc dung theo FloorType.Id
        // ca 2 loai thay vi quet ca category, tranh lan lon dai/lot nhung van gom du ca mo, ca moi tao.
        var slabFloorIds = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .Where(f => (capFloorTypeId is not null && f.FloorType.Id == capFloorTypeId) || (lotFloorTypeId is not null && f.FloorType.Id == lotFloorTypeId))
            .Select(f => f.Id);

        var allSlabIds = familyCapIds.Concat(slabFloorIds).ToList();

        return JoinPairs(doc, view, allPileIds, allSlabIds, "Auto Join Cọc - Foundation Slab");
    }

    /// <summary>Join từng cặp (cuttingIds × otherIds) có bounding box chồng nhau — đã xác nhận qua test thực tế: giữa cọc (FamilyInstance) và đài/lót (Floor), dù cùng category Structural Foundation, Revit luôn mặc định để Floor là phần tử cắt bất kể chiều gọi Join. Vì vậy không còn đo thể tích để "đoán" chiều nữa (từng đo không ổn định) — luôn Join rồi SwitchJoinOrder 1 lần dứt điểm.</summary>
    private static JoinSummary JoinPairs(Document doc, View view, IReadOnlyList<ElementId> cuttingIds, IReadOnlyList<ElementId> otherIds, string transactionName)
    {
        if (cuttingIds.Count == 0 || otherIds.Count == 0)
            return new JoinSummary(0, 0, 0);

        var joined = 0;
        var alreadyJoined = 0;
        var failed = 0;


        foreach (var cuttingId in cuttingIds)
        {
            var cuttingElement = doc.GetElement(cuttingId);
            if (cuttingElement is null)
                continue;

            foreach (var otherId in otherIds)
            {
                var otherElement = doc.GetElement(otherId);
                if (otherElement is null || !BoundingBoxesOverlap(cuttingElement, otherElement, view))
                    continue;

                try
                {
                    // Da join tu truoc (co the tu ban chay bug cu, chieu sai) - unjoin roi join+switch lai tu dau
                    // thay vi tin vao trang thai cu, dam bao luon ket thuc dung chieu.
                    var wasAlreadyJoined = JoinGeometryUtils.AreElementsJoined(doc, cuttingElement, otherElement);
                    if (wasAlreadyJoined)
                    {
                        JoinGeometryUtils.UnjoinGeometry(doc, cuttingElement, otherElement);
                        doc.Regenerate();
                    }

                    JoinGeometryUtils.JoinGeometry(doc, cuttingElement, otherElement);
                    JoinGeometryUtils.SwitchJoinOrder(doc, cuttingElement, otherElement);
                    doc.Regenerate();

                    if (wasAlreadyJoined)
                        alreadyJoined++;
                    else
                        joined++;
                }
                catch (Exception)
                {
                    // Revit tu choi join nhieu truong hop hop le - bo qua tung cap loi.
                    failed++;
                }
            }
        }

        return new JoinSummary(joined, alreadyJoined, failed);
    }

    /// <summary>So bounding box (dung sai ~30mm) — copy nguyên từ AutoJoin để chỉ join cặp thực sự chồng nhau.</summary>
    private static bool BoundingBoxesOverlap(Element a, Element b, View view)
    {
        var boxA = a.get_BoundingBox(view);
        var boxB = b.get_BoundingBox(view);
        if (boxA is null || boxB is null)
            return false;

        return boxA.Min.X - ToleranceFeet <= boxB.Max.X && boxB.Min.X - ToleranceFeet <= boxA.Max.X &&
               boxA.Min.Y - ToleranceFeet <= boxB.Max.Y && boxB.Min.Y - ToleranceFeet <= boxA.Max.Y &&
               boxA.Min.Z - ToleranceFeet <= boxB.Max.Z && boxB.Min.Z - ToleranceFeet <= boxA.Max.Z;
    }
}

/// <summary>Kết quả 1 lượt Auto Join.</summary>
internal sealed record JoinSummary(int Joined, int AlreadyJoined, int Failed);
