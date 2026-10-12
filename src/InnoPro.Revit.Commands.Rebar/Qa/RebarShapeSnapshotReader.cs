namespace InnoPro.Revit.Commands.Rebar.Qa;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

/// <summary>
/// Đọc snapshot kích thước đoạn (segment) của 1 Rebar từ Rebar Shape API, dùng cho tính năng "Kiểm
/// tra thay đổi Rebar" (xem Kien-truc-Rebar-Change-Checker.md mục 4.2). Không hardcode tên tham số
/// ("A"/"B"/"Length 1"...) vì tên phụ thuộc ngôn ngữ/phiên bản Revit — đường dẫn đọc xác nhận qua
/// research thực tế (forum Autodesk, dev đã test thành công): GetShapeId() ->
/// RebarShape.GetRebarShapeDefinition() -> RebarShapeDefinitionBySegments.GetSegment(i)
/// .GetConstraints() -> RebarShapeConstraintSegmentLength.GetParamId() ->
/// SharedParameterElement.GuidValue -> Rebar.get_Parameter(Guid).
/// </summary>
internal static class RebarShapeSnapshotReader
{
    /// <summary>Trả null cho Rebar không có RebarShapeDefinitionBySegments (vd Free Form rebar không
    /// định nghĩa theo segment kiểu này) — không phải lỗi, chỉ là không áp dụng được tính năng này
    /// cho thanh đó (bỏ qua khi build snapshot, xem RebarChangeScope).</summary>
    public static RebarShapeSnapshotEntry? Read(Rebar rebar, Document doc)
    {
        if (doc.GetElement(rebar.GetShapeId()) is not RebarShape shape)
            return null;

        if (shape.GetRebarShapeDefinition() is not RebarShapeDefinitionBySegments definition)
            return null;

        var segments = new List<RebarSegmentValue>();
        for (var i = 0; i < definition.NumberOfSegments; i++)
        {
            var constraintIndex = 0;
            foreach (var constraint in definition.GetSegment(i).GetConstraints())
            {
                if (constraint is not RebarShapeConstraintSegmentLength lengthConstraint)
                    continue;

                if (doc.GetElement(lengthConstraint.GetParamId()) is not SharedParameterElement sharedParam)
                    continue;

                var parameter = rebar.get_Parameter(sharedParam.GuidValue);
                if (parameter is null)
                    continue;

                // Da lay duoc 1 constraint do dai cho segment i; 1 segment hiem khi co >1 constraint
                // do dai (vd hook con phu thuoc rieng) - danh them hau to de khong trung Label giua
                // 2 constraint cung 1 segment.
                var label = constraintIndex == 0 ? $"Đoạn {i + 1}" : $"Đoạn {i + 1}.{constraintIndex + 1}";
                segments.Add(new RebarSegmentValue
                {
                    SegmentIndex = i,
                    Label = label,
                    ValueMm = RevitUnits.FeetToMm(parameter.AsDouble())
                });
                constraintIndex++;
            }
        }

        var mark = rebar.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
        var hostId = rebar.GetHostId();
        var hostElement = hostId != ElementId.InvalidElementId ? doc.GetElement(hostId) : null;

        return new RebarShapeSnapshotEntry
        {
            ElementId = rebar.Id.Value,
            Mark = mark,
            HostElementId = hostId != ElementId.InvalidElementId ? hostId.Value : null,
            HostElementName = hostElement?.Name ?? "",
            RebarShapeTypeId = shape.Id.Value,
            RebarShapeTypeName = shape.Name,
            Segments = segments
        };
    }
}
