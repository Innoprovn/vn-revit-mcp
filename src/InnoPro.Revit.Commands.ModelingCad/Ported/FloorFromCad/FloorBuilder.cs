using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.FloorFromCad;

/// <summary>Tạo Floor HOẶC Ceiling thật từ danh sách FloorNameGroup (tuỳ <see cref="SurfaceKind"/> chọn
/// chung cho cả lượt chạy) — 1 Transaction DUY NHẤT cho toàn bộ (đúng quy ước modal của repo — cửa sổ
/// WPF chỉ thu thập lựa chọn, transaction thật chạy sau khi đóng cửa sổ, xem DoorWindowPlacer/WallBuilder).
/// Mỗi RawHatchRegion trong 1 nhóm dùng CHUNG 1 Type + 1 cao độ. Quy tắc hình học/Type/offset HỆT như khi
/// vẽ Sàn — chỉ khác đối tượng Revit thật sự được tạo (Floor.Create/Ceiling.Create) và tham số cao độ
/// (FLOOR_HEIGHTABOVELEVEL_PARAM/CEILING_HEIGHTABOVELEVEL_PARAM).
///
/// NHÓM CHƯA CÓ TYPE KHỚP: người dùng chọn 1 Type GỐC (Floor hoặc Ceiling Type, tuỳ SurfaceKind) + nhập
/// bề dày TỔNG mong muốn (mm) thay vì bắt buộc chọn Type có sẵn — ngay ĐẦU vòng lặp mỗi nhóm (trước khi
/// tạo bất kỳ Floor/Ceiling nào), tool tự Duplicate 1 Type MỚI đúng bề dày đó (xem
/// DuplicateTypeWithThickness) rồi gán vào SelectedChoice, coi như nhóm đã "có Type" — mọi logic tạo
/// phía sau dùng chung.
///
/// CAO ĐỘ: sketch luôn dựng ở ĐÚNG mặt phẳng cao độ của Level (Level.ProjectElevation — khớp hệ toạ độ Z
/// dùng chung với mọi hình học khác trong model, xem ghi chú tương tự ở WallElevationToStructure), sau
/// đó gán tham số Height Offset From Level theo giá trị người dùng nhập cho từng nhóm — KHÔNG cộng
/// offset thẳng vào Z của sketch, để giữ đúng ý nghĩa "cao độ so với Level" hiển thị trong Properties
/// giống thao tác tay bình thường.</summary>
internal static class FloorBuilder
{
    public static List<FloorPlacementResult> Build(
        Document doc, Level level, List<FloorNameGroup> groups, SurfaceKind kind)
    {
        var results = new List<FloorPlacementResult>();
        var levelZFeet = level.ProjectElevation;


        foreach (var group in groups)
        {
            if (group.SelectedChoice is null && group.DuplicateBaseType is not null)
            {
                try
                {
                    var newType = DuplicateTypeWithThickness(doc, group.DuplicateBaseType, group.Name, group.CustomThicknessMm, kind);
                    group.SelectedChoice = new FloorTypeChoice(newType, newType.Name);
                }
                catch (Exception ex)
                {
                    foreach (var region in group.Regions)
                    {
                        results.Add(new FloorPlacementResult(
                            group, region, FloorPlacementOutcome.Failed, $"Không tạo được Type mới: {ex.Message}"));
                    }

                    continue;
                }
            }

            foreach (var region in group.Regions)
            {
                if (group.SelectedType is null)
                {
                    results.Add(new FloorPlacementResult(group, region, FloorPlacementOutcome.SkippedNoType));
                    continue;
                }

                try
                {
                    var edgeOffsetFeet = UnitUtils.ConvertToInternalUnits(group.EdgeOffsetMm, UnitTypeId.Millimeters);
                    var profile = BuildProfileAtZ(region, levelZFeet, edgeOffsetFeet);

                    Element surface = kind == SurfaceKind.Ceiling
                        ? Ceiling.Create(doc, profile, group.SelectedType.Id, level.Id)
                        : Floor.Create(doc, profile, group.SelectedType.Id, level.Id);

                    var offsetBip = kind == SurfaceKind.Ceiling
                        ? BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM
                        : BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM;
                    var offsetParam = surface.get_Parameter(offsetBip);
                    if (offsetParam is not null && !offsetParam.IsReadOnly)
                    {
                        var offsetFeet = UnitUtils.ConvertToInternalUnits(group.OffsetMm, UnitTypeId.Millimeters);
                        offsetParam.Set(offsetFeet);
                    }

                    results.Add(new FloorPlacementResult(group, region, FloorPlacementOutcome.Created));
                }
                catch (Exception ex)
                {
                    results.Add(new FloorPlacementResult(group, region, FloorPlacementOutcome.Failed, ex.Message));
                }
            }
        }

        return results;
    }

    /// <summary>Dựng profile Floor.Create/Ceiling.Create — mỗi loop trước tiên đưa lên đúng mặt phẳng Z
    /// của Level (xem OffsetLoopZ), rồi NẾU <paramref name="edgeOffsetFeet"/> khác 0 mới áp thêm offset
    /// trong mặt phẳng (XY) qua <see cref="OffsetLoopInPlane"/> — biên NGOÀI lùi VÀO TRONG (offset ÂM),
    /// biên LỖ KHOÉT (nếu có) nới RỘNG ra (offset DƯƠNG cùng độ lớn) — cả 2 chiều đều làm diện tích sàn/
    /// trần THẬT nhỏ đi đều <paramref name="edgeOffsetFeet"/> ở mọi cạnh, đúng nghĩa "lùi vào trong mỗi
    /// cạnh".</summary>
    private static List<CurveLoop> BuildProfileAtZ(RawHatchRegion region, double zFeet, double edgeOffsetFeet)
    {
        var outer = OffsetLoopZ(region.OuterLoop, zFeet);
        var inner = region.InnerLoops.Select(loop => OffsetLoopZ(loop, zFeet)).ToList();

        if (Math.Abs(edgeOffsetFeet) > 1e-9)
        {
            outer = OffsetLoopInPlane(outer, -edgeOffsetFeet) ?? outer;
            inner = inner.Select(loop => OffsetLoopInPlane(loop, edgeOffsetFeet) ?? loop).ToList();
        }

        var profile = new List<CurveLoop> { outer };
        profile.AddRange(inner);
        return profile;
    }

    /// <summary>Offset 1 CurveLoop PHẲNG (đã cùng Z) trong chính mặt phẳng của nó qua
    /// <see cref="CurveLoop.CreateViaOffset"/> — trả về null nếu offset thất bại (vd offset lớn hơn kích
    /// thước loop khiến hình tự giao nhau) để nơi gọi GIỮ NGUYÊN loop gốc thay vì làm hỏng cả sketch.
    /// GIỚI HẠN CHƯA KIỂM CHỨNG THỰC TẾ: chiều dấu (âm = co vào trong biên ngoài) dựa theo quy ước chuẩn
    /// hướng loop CCW/CW của Revit khi đọc từ Face — nếu chạy thực tế thấy NGƯỢC chiều (mép sàn tràn ra
    /// ngoài thay vì lùi vào), chỉ cần đảo dấu 2 lời gọi ở BuildProfileAtZ.</summary>
    private static CurveLoop? OffsetLoopInPlane(CurveLoop loop, double offsetFeet)
    {
        try
        {
            return CurveLoop.CreateViaOffset(loop, offsetFeet, XYZ.BasisZ);
        }
        catch
        {
            return null;
        }
    }

    private static CurveLoop OffsetLoopZ(CurveLoop loop, double zFeet)
    {
        var result = new CurveLoop();
        foreach (var curve in loop)
        {
            switch (curve)
            {
                case Line line:
                    result.Append(Line.CreateBound(WithZ(line.GetEndPoint(0), zFeet), WithZ(line.GetEndPoint(1), zFeet)));
                    break;

                case Arc arc:
                    result.Append(Arc.Create(
                        WithZ(arc.GetEndPoint(0), zFeet), WithZ(arc.GetEndPoint(1), zFeet), WithZ(arc.Evaluate(0.5, true), zFeet)));
                    break;
            }
        }

        return result;
    }

    private static XYZ WithZ(XYZ p, double z) => new(p.X, p.Y, z);

    /// <summary>Duplicate 1 Type (Floor hoặc Ceiling — cả 2 đều là HostObjAttributes, đều có Compound
    /// Structure) có sẵn, đổi bề dày TỔNG của nó bằng cách CHỈ chỉnh lớp có Function = Structure (giữ
    /// nguyên mọi lớp khác — hoàn thiện, lớp khí...) — đúng yêu cầu chỉ đổi bề dày, không đụng gì khác
    /// trong cấu tạo Type gốc. Nếu Type gốc chỉ có 1 lớp duy nhất (không khai Function) thì dùng thẳng
    /// lớp đó. Ném lỗi rõ ràng nếu không có Compound Structure, không tìm được lớp Structure (nhiều hơn 1
    /// lớp mà không lớp nào khai Structure), hoặc bề dày nhập nhỏ hơn tổng bề dày các lớp khác (không đủ
    /// chỗ cho lớp Structure).</summary>
    private static HostObjAttributes DuplicateTypeWithThickness(
        Document doc, HostObjAttributes baseType, string newTypeName, double totalThicknessMm, SurfaceKind kind)
    {
        var collectorType = kind == SurfaceKind.Ceiling ? typeof(CeilingType) : typeof(FloorType);
        var existingNames = new HashSet<string>(
            new FilteredElementCollector(doc).OfClass(collectorType).Cast<ElementType>().Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);
        var uniqueName = MakeUniqueTypeName(existingNames, newTypeName);

        var newType = (HostObjAttributes)baseType.Duplicate(uniqueName);

        var structure = newType.GetCompoundStructure();
        if (structure is null)
            throw new InvalidOperationException("Type gốc không có Compound Structure (lớp cấu tạo) để chỉnh bề dày.");

        var layers = structure.GetLayers();
        var structuralIndex = -1;
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Function == MaterialFunctionAssignment.Structure)
            {
                structuralIndex = i;
                break;
            }
        }

        if (structuralIndex < 0)
        {
            if (layers.Count == 1)
                structuralIndex = 0;
            else
                throw new InvalidOperationException("Không tìm thấy lớp \"Structure\" trong Type gốc để chỉnh bề dày.");
        }

        var otherLayersWidthFeet = 0.0;
        for (var i = 0; i < layers.Count; i++)
        {
            if (i != structuralIndex)
                otherLayersWidthFeet += layers[i].Width;
        }

        var totalThicknessFeet = UnitUtils.ConvertToInternalUnits(totalThicknessMm, UnitTypeId.Millimeters);
        var newStructuralWidthFeet = totalThicknessFeet - otherLayersWidthFeet;
        if (newStructuralWidthFeet <= 1e-6)
        {
            throw new InvalidOperationException(
                $"Bề dày nhập ({totalThicknessMm}mm) nhỏ hơn hoặc bằng tổng bề dày các lớp khác của Type gốc.");
        }

        structure.SetLayerWidth(structuralIndex, newStructuralWidthFeet);
        newType.SetCompoundStructure(structure);

        return newType;
    }

    private static string MakeUniqueTypeName(HashSet<string> existingNames, string desiredName)
    {
        if (!existingNames.Contains(desiredName))
            return desiredName;

        var suffix = 2;
        while (existingNames.Contains($"{desiredName} ({suffix})"))
            suffix++;

        return $"{desiredName} ({suffix})";
    }
}
