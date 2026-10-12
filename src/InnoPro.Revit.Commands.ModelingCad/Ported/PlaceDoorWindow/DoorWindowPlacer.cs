using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace InnoPro.Revit.Commands.ModelingCad.PlaceDoorWindow;

/// <summary>Đặt Family Instance cửa đi/cửa sổ thật vào model — 1 transaction DUY NHẤT cho toàn bộ
/// danh sách, đúng quy ước modal của repo (cửa sổ WPF chỉ thu thập lựa chọn, transaction thật chạy
/// sau khi đóng cửa sổ — xem ConvertWindow/CutWindow, WallBuilder ở WallFromCad). Đặt TỪNG
/// OpeningInstance trong mỗi DoorNameGroup — Type + cao độ dùng chung của cả nhóm.
///
/// SAU KHI ĐẶT, TỰ CĂN LẠI CHO ĐÚNG TÂM (xem CenterOnWall): `NewFamilyInstance(point,...)` KHÔNG đảm
/// bảo mặt phẳng tham chiếu "Center (Left/Right)" của family trùng với "point" truyền vào — phụ thuộc
/// hoàn toàn vào cách family được dựng (đặc biệt family cửa đôi ghép từ 2 cánh KHÔNG đối xứng, ví dụ
/// Doors_ExtDbl_Flush khi 2 cánh không bằng nhau — "Center (Left/Right)" của family này là điểm 2 cánh
/// GẶP NHAU, không phải giữa hình học tổng thể, nên Bounding Box không đại diện đúng). Cách khắc phục
/// CHÍNH XÁC: lấy THẲNG vị trí thật của chính reference plane "Center (Left/Right)" qua
/// FamilyInstance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight) +
/// GetGeometryObjectFromReference — đúng plane hiện trong tooltip Revit khi rê chuột — rồi dịch chuyển
/// (Move) đúng phần chênh lệch dọc tường so với tâm mong muốn (đã tính từ CAD). Bounding Box chỉ dùng
/// làm DỰ PHÒNG khi family không khai báo reference này (family cũ/tự tạo không theo chuẩn).
///
/// CŨNG TỰ LẬT HƯỚNG MỞ (Facing) cho khớp CAD (xem MatchSwingDirection): hướng mở cửa model phải là
/// phía chứa cung xoay trong CAD — cụm không có cung (cửa sổ, cửa lùa...) thì bỏ qua bước này, không có
/// khái niệm hướng mở để khớp. Quyết định lật dựa trên việc ĐO TRỰC TIẾP cung xoay 2D thật của Family
/// Instance vừa đặt (FindPlacedSwingMidpoint), không dựa vào FacingOrientation — thuộc tính này không
/// đáng tin cậy, ý nghĩa phụ thuộc cách family được dựng.
///
/// SILL HEIGHT quy đổi theo SillHeightMode (chọn CHUNG cho cả lần đặt, xem PlaceDoorWindowWindow):
/// FromLevel dùng thẳng giá trị nhập; FromFinishedFloor cộng thêm offset Sàn→Level, và với cửa CÓ
/// SwingDirection thì tra offset của đúng Floor nằm NGAY PHÍA TRƯỚC cửa (điểm cách mép tường về phía
/// cung mở đúng FrontOfDoorMarginFeet) thay vì Floor lớn nhất của tầng — vì 1 tầng có thể có nhiều cao
/// độ hoàn thiện khác nhau ở 2 bên 1 cửa (vd sàn WC thấp hơn sàn phòng ngủ).
///
/// SAU CÙNG, LÀM TRÒN VỊ TRÍ THEO GRID (xem SnapToGrid): toạ độ gốc trong CAD thường lẻ 0.1mm/0.001mm
/// (sai số dựng hình CAD) khiến khoảng cách từ Grid THẬT của Revit tới cửa (đo dọc tường) ra số lẻ kiểu
/// "300.106" thay vì "300" — làm tròn về mm nguyên cho khớp ý đồ thiết kế. CHỈ áp dụng khi tường host
/// thẳng hàng với hệ Grid (có Grid vuông góc đủ gần phương tường) — tường XIÊN không có Grid nào vuông
/// góc thì bỏ qua, giữ nguyên vị trí đã đặt, đúng yêu cầu người dùng.
///
/// NHÓM CHƯA CÓ TYPE KHỚP: thay vì bắt buộc người dùng tự chọn 1 Type có sẵn, có thể chọn Family +
/// nhập Width/Height (xem DoorNameGroup.SelectedFamily/CustomWidthMm/CustomHeightMm) — ngay ĐẦU vòng
/// lặp mỗi nhóm (trước khi đặt bất kỳ instance nào), tool tự DUPLICATE 1 Type mới đúng kích thước từ
/// Family đó (xem DuplicateSizedSymbol) rồi gán vào SelectedChoice, coi như nhóm đã "có Type" — mọi
/// logic đặt cửa phía sau dùng chung, không cần biết Type đó vừa được tạo hay có sẵn từ trước. TÊN 2
/// tham số Width/Height cần gán do NGƯỜI DÙNG tự gõ trong UI (mặc định "Width"/"Height", sửa được —
/// xem PlaceDoorWindowWindow), KHÔNG còn dò qua danh sách tên cố định — Family nào thực sự không có
/// tham số đúng tên đó thì báo lỗi rõ ràng "không tạo được Type" cho nhóm đó, không đoán mò.</summary>
internal static class DoorWindowPlacer
{
    /// <summary>Khoảng lùi thêm (ngoài nửa bề dày tường) khi tính điểm "phía trước cửa" để tra Floor
    /// đúng — tránh điểm tra rơi ngay sát mép tường/Floor (dễ bị coi là "ngoài biên" do sai số dựng hình,
    /// hoặc lẫn sang Floor bên kia nếu 2 Floor khác cao độ chạm sát tường).</summary>
    private static readonly double FrontOfDoorMarginFeet =
        UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);

    /// <summary>Dung sai góc (độ) để coi 1 Grid là "vuông góc" với tường — dùng để lọc Grid hợp lệ cho
    /// SnapToGrid, đủ chặt để loại tường xiên thật sự, đủ rộng để chấp nhận sai số dựng hình nhỏ.</summary>
    private const double GridPerpendicularToleranceDegrees = 2.0;

    /// <summary>Làm tròn khoảng cách Grid→cửa về BỘI SỐ của giá trị này (mm), KHÔNG phải mm nguyên gần
    /// nhất — số liệu thiết kế kiến trúc luôn là bội của 10 hoặc ít nhất bội của 5 (vd 360, không bao
    /// giờ 361) — xem WallBuilder.SnapUnitMm (cùng nguyên tắc, áp dụng ở tool vẽ tường).</summary>
    private const double SnapUnitMm = 5.0;

    public static List<PlacementResult> Place(
        Document doc, View view, List<DoorNameGroup> groups, SillHeightMode sillHeightMode,
        IReadOnlyList<Grid> grids, string widthParameterName, string heightParameterName)
    {
        var results = new List<PlacementResult>();
        var level = view.GenLevel;


        var floorsByLevelId = new Dictionary<ElementId, List<Floor>>();

        foreach (var group in groups)
        {
            if (group.SelectedChoice is null && group.SelectedFamily is not null)
            {
                try
                {
                    var newSymbol = DuplicateSizedSymbol(
                        doc, group.SelectedFamily, group.Name, group.CustomWidthMm, group.CustomHeightMm,
                        widthParameterName, heightParameterName);
                    group.SelectedChoice = new SymbolChoice(newSymbol, $"{newSymbol.Family.Name} : {newSymbol.Name}");
                }
                catch (Exception ex)
                {
                    foreach (var instance in group.Instances)
                    {
                        results.Add(new PlacementResult(
                            group, instance, PlacementOutcome.Failed, $"Không tạo được Type mới: {ex.Message}"));
                    }

                    continue;
                }
            }

            foreach (var instance in group.Instances)
            {
                if (group.SelectedType is null)
                {
                    results.Add(new PlacementResult(group, instance, PlacementOutcome.SkippedNoType));
                    continue;
                }

                if (instance.Host is null)
                {
                    results.Add(new PlacementResult(group, instance, PlacementOutcome.SkippedNoHostWall));
                    continue;
                }

                try
                {
                    var symbol = group.SelectedType;
                    if (!symbol.IsActive)
                        symbol.Activate();

                    var hostLevel = level ?? doc.GetElement(instance.Host.Wall.LevelId) as Level;
                    if (hostLevel is null)
                    {
                        results.Add(new PlacementResult(
                            group, instance, PlacementOutcome.Failed, "Không xác định được Level."));
                        continue;
                    }

                    var familyInstance = doc.Create.NewFamilyInstance(
                        instance.Host.InsertionPoint, symbol, instance.Host.Wall, hostLevel,
                        StructuralType.NonStructural);

                    doc.Regenerate();
                    MatchSwingDirection(
                        familyInstance, view, instance.Host.InsertionPoint, instance.Opening.SwingDirection);

                    var centeringNote = CenterOnWall(
                        doc, familyInstance, instance.Host.Wall, instance.Host.InsertionPoint);
                    var gridSnapNote = SnapToGrid(doc, familyInstance, instance.Host.Wall, grids);

                    var sillParam = familyInstance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                    if (sillParam is not null && !sillParam.IsReadOnly)
                    {
                        var sillInputFeet = UnitUtils.ConvertToInternalUnits(
                            group.SillHeightMm, UnitTypeId.Millimeters);

                        var floorOffsetFeet = 0.0;
                        if (sillHeightMode == SillHeightMode.FromFinishedFloor)
                        {
                            if (!floorsByLevelId.TryGetValue(hostLevel.Id, out var floorsOnLevel))
                            {
                                floorsOnLevel = FinishedFloorFinder.GetFloorsOnLevel(doc, hostLevel);
                                floorsByLevelId[hostLevel.Id] = floorsOnLevel;
                            }

                            floorOffsetFeet = instance.Opening.SwingDirection is { } swingDirection
                                ? FinishedFloorFinder.GetOffsetFeetAtPoint(
                                    floorsOnLevel,
                                    instance.Host.InsertionPoint + swingDirection.Multiply(
                                        instance.Host.Wall.Width / 2.0 + FrontOfDoorMarginFeet),
                                    out _)
                                : FinishedFloorFinder.GetOffsetFeet(floorsOnLevel, out _);
                        }

                        sillParam.Set(sillInputFeet + floorOffsetFeet);
                    }

                    results.Add(new PlacementResult(
                        group, instance, PlacementOutcome.Created,
                        CenteringNote: centeringNote, GridSnapNote: gridSnapNote));
                }
                catch (Exception ex)
                {
                    results.Add(new PlacementResult(group, instance, PlacementOutcome.Failed, ex.Message));
                }
            }
        }

        return results;
    }

    /// <summary>Tạo Type MỚI cho <paramref name="family"/> đúng kích thước người dùng nhập — dùng khi
    /// nhóm cửa CHƯA CÓ Type khớp sẵn, người dùng chọn Family + nhập Width/Height thay vì chọn Type có
    /// sẵn (xem DoorNameGroup.SelectedFamily). Lấy Type ĐẦU TIÊN (theo tên, alphabet) của Family đó làm
    /// GỐC để Duplicate — CHỈ đổi Tên Type và giá trị 2 tham số kiểu tên
    /// <paramref name="widthParameterName"/>/<paramref name="heightParameterName"/> (do người dùng tự
    /// gõ trong UI, mặc định "Width"/"Height" — xem PlaceDoorWindowWindow), MỌI tham số khác giữ NGUYÊN
    /// từ Type gốc, đúng yêu cầu. Tên Type mới ƯU TIÊN dùng đúng tên nhóm cửa (vd "D4") — nếu đã trùng
    /// tên 1 Type khác trong CÙNG Family thì tự thêm hậu tố " (2)", " (3)"... Ném lỗi rõ ràng nếu Family
    /// không có Type nào để làm gốc, hoặc KHÔNG có tham số đúng tên đã nhập (family không theo đúng quy
    /// ước đặt tên này) — lỗi hiển thị thẳng cho người dùng biết "không tạo được Type" cho nhóm đó.</summary>
    private static FamilySymbol DuplicateSizedSymbol(
        Document doc, Family family, string newTypeName, double widthMm, double heightMm,
        string widthParameterName, string heightParameterName)
    {
        var symbolIds = family.GetFamilySymbolIds();
        if (symbolIds.Count == 0)
            throw new InvalidOperationException($"Family \"{family.Name}\" không có Type nào để làm gốc Duplicate.");

        var existingSymbols = symbolIds.Select(id => (FamilySymbol)doc.GetElement(id)).ToList();
        var baseSymbol = existingSymbols
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .First();

        var existingNames = new HashSet<string>(
            existingSymbols.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var uniqueName = MakeUniqueTypeName(existingNames, newTypeName);

        var newSymbol = (FamilySymbol)baseSymbol.Duplicate(uniqueName);

        SetSizeParameter(newSymbol, widthParameterName, widthMm);
        SetSizeParameter(newSymbol, heightParameterName, heightMm);

        if (!newSymbol.IsActive)
            newSymbol.Activate();

        return newSymbol;
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

    private static void SetSizeParameter(FamilySymbol symbol, string parameterName, double valueMm)
    {
        var param = symbol.LookupParameter(parameterName);
        if (param is null || param.IsReadOnly)
        {
            throw new InvalidOperationException(
                $"Family \"{symbol.Family.Name}\" không có tham số kiểu tên \"{parameterName}\" để gán kích thước.");
        }

        param.Set(UnitUtils.ConvertToInternalUnits(valueMm, UnitTypeId.Millimeters));
    }

    /// <summary>Lật hướng mở cửa (Facing) cho khớp với phía có cung xoay trong CAD — xem
    /// OpeningDetector.FindSwingDirection. Bỏ qua nếu <paramref name="desiredSwingDirection"/> null
    /// (cụm CAD không có cung — không có khái niệm hướng mở để khớp, đúng yêu cầu chỉ quan tâm khi CÓ
    /// cung) hoặc family không hỗ trợ lật Facing (CanFlipFacing false — vd 1 số family cố định).
    ///
    /// KHÔNG dùng FamilyInstance.FacingOrientation để quyết định lật (cách cũ, ĐÃ SAI với cửa đôi 2
    /// cánh — vd "D7"): ý nghĩa của FacingOrientation phụ thuộc cách family được dựng (giống hệt vấn đề
    /// đã gặp với "Center Left/Right" ở CenterOnWall), không đáng tin cậy làm tín hiệu quyết định lật.
    /// Thay vào đó ĐO TRỰC TIẾP hình học cung xoay THẬT của chính Family Instance vừa đặt
    /// (FindPlacedSwingMidpoint — quét get_Geometry tìm Arc lớn nhất, đúng ký hiệu cung xoay 2D của
    /// family cửa/cửa sổ) rồi so với phía mong muốn: cung đang phình sai phía thì lật, đúng phía rồi
    /// thì thôi — không cần đoán qua thuộc tính ngữ nghĩa nào cả.</summary>
    private static void MatchSwingDirection(
        FamilyInstance familyInstance, View view, XYZ insertionPoint, XYZ? desiredSwingDirection)
    {
        if (desiredSwingDirection is null)
            return;

        if (!familyInstance.CanFlipFacing)
            return;

        var placedMid = FindPlacedSwingMidpoint(familyInstance, view);
        if (placedMid is null)
        {
            // Du phong: khong tim thay cung nao trong hinh hoc da dat (family khong ve ky hieu cung
            // xoay bang Arc, hoac chi co geometry 3D) - dung FacingOrientation nhu truoc, con hon khong.
            if (familyInstance.FacingOrientation.DotProduct(desiredSwingDirection) < 0)
                familyInstance.flipFacing();
            return;
        }

        if ((placedMid - insertionPoint).DotProduct(desiredSwingDirection) < 0)
            familyInstance.flipFacing();
    }

    /// <summary>Quét hình học 2D (Symbolic, theo đúng view mặt bằng đang thao tác) của
    /// <paramref name="familyInstance"/> vừa đặt, tìm cung tròn (Arc) LỚN NHẤT — đúng ký hiệu cung xoay
    /// vẽ trong family cửa/cửa sổ chuẩn Revit — trả về điểm giữa cung đó (world space). Null nếu không
    /// tìm thấy Arc nào (family không vẽ ký hiệu cung xoay, hoặc lỗi đọc geometry).</summary>
    private static XYZ? FindPlacedSwingMidpoint(FamilyInstance familyInstance, View view)
    {
        GeometryElement? geometryElement;
        try
        {
            geometryElement = familyInstance.get_Geometry(
                new Options { View = view, IncludeNonVisibleObjects = false });
        }
        catch
        {
            return null;
        }

        if (geometryElement is null)
            return null;

        Arc? largestArc = null;
        WalkForLargestArc(geometryElement, ref largestArc);
        return largestArc?.Evaluate(0.5, true);
    }

    private static void WalkForLargestArc(GeometryElement geometryElement, ref Arc? largestArc)
    {
        foreach (var geomObj in geometryElement)
        {
            switch (geomObj)
            {
                case GeometryInstance geomInstance:
                    var nested = geomInstance.GetInstanceGeometry();
                    if (nested is not null)
                        WalkForLargestArc(nested, ref largestArc);
                    break;
                case Arc arc when arc.IsBound && (largestArc is null || arc.Radius > largestArc.Radius):
                    largestArc = arc;
                    break;
            }
        }
    }

    /// <summary>Tìm vị trí thật (theo phương dọc tường) của <paramref name="familyInstance"/> vừa đặt —
    /// ƯU TIÊN đọc thẳng reference plane "Center (Left/Right)" thật của family (TryGetCenterLeftRightPoint),
    /// dự phòng bằng tâm Bounding Box nếu family không khai báo reference đó — rồi dịch chuyển
    /// (Move) dọc tường đúng phần chênh lệch so với <paramref name="desiredCenter"/> (điểm mong muốn,
    /// đã tính từ CAD). Bỏ qua nếu không xác định được vị trí thật, hoặc chênh lệch quá nhỏ (&lt;= 1mm,
    /// coi như đã đúng, tránh Move thừa vì sai số làm tròn).</summary>
    private static string CenterOnWall(Document doc, FamilyInstance familyInstance, Wall wall, XYZ desiredCenter)
    {
        if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line wallLine)
            return "Không căn được: tường không phải Location Line.";

        var fromReference = TryGetCenterLeftRightPoint(familyInstance);
        var actualCenter = fromReference ?? GetBoundingBoxCenter(familyInstance);
        var source = fromReference is not null ? "reference Center (Left/Right)" : "Bounding Box (dự phòng)";

        if (actualCenter is null)
            return "Không căn được: không đọc được reference lẫn Bounding Box.";

        var wallP0 = wallLine.GetEndPoint(0);
        var wallDirection = (wallLine.GetEndPoint(1) - wallP0).Normalize();

        var actualT = (actualCenter - wallP0).DotProduct(wallDirection);
        var desiredT = (desiredCenter - wallP0).DotProduct(wallDirection);
        var deltaT = desiredT - actualT;
        var deltaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(deltaT, UnitTypeId.Millimeters), 1);

        var toleranceFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
        if (Math.Abs(deltaT) <= toleranceFeet)
            return $"Nguồn: {source}. Đã đúng tâm, không cần dịch chuyển.";

        ElementTransformUtils.MoveElement(doc, familyInstance.Id, wallDirection.Multiply(deltaT));
        return $"Nguồn: {source}. Đã dịch chuyển {deltaMm}mm dọc tường để căn tâm.";
    }

    /// <summary>Đọc thẳng vị trí thật của reference plane "Center (Left/Right)" — đúng plane hiện trong
    /// tooltip Revit khi rê chuột qua family instance. Trả về null nếu family không khai báo reference
    /// này, hoặc không resolve được thành geometry cụ thể (một vài kiểu family/1 số phiên bản Revit có
    /// thể trả về dạng geometry khác PlanarFace/Line — bỏ qua, dùng Bounding Box dự phòng).</summary>
    private static XYZ? TryGetCenterLeftRightPoint(FamilyInstance familyInstance)
    {
        try
        {
            var references = familyInstance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
            if (references is null || references.Count == 0)
                return null;

            var geomObj = familyInstance.GetGeometryObjectFromReference(references[0]);
            return geomObj switch
            {
                PlanarFace face => face.Origin,
                Line line => line.GetEndPoint(0),
                Curve curve => curve.GetEndPoint(0),
                _ => null
            };
        }
        catch
        {
            // Mot vai family/truong hop khong ho tro reference nay - du phong bang Bounding Box.
            return null;
        }
    }

    private static XYZ? GetBoundingBoxCenter(FamilyInstance familyInstance)
    {
        var bbox = familyInstance.get_BoundingBox(null);
        if (bbox is null)
            return null;

        return new XYZ(
            (bbox.Min.X + bbox.Max.X) / 2.0,
            (bbox.Min.Y + bbox.Max.Y) / 2.0,
            (bbox.Min.Z + bbox.Max.Z) / 2.0);
    }

    /// <summary>Làm tròn vị trí THẬT (đọc lại sau CenterOnWall — chính vị trí sẽ hiện trong Dimension
    /// thật của Revit, không phải điểm CAD gốc) của <paramref name="familyInstance"/> về số nguyên mm
    /// khi đo DỌC TƯỜNG từ Grid vuông góc GẦN NHẤT — xem doc-comment lớp. Bỏ qua (trả về lý do) nếu
    /// tường không phải Location Line, không đọc được vị trí thật, hoặc KHÔNG có Grid nào đủ vuông góc
    /// với phương tường trong <see cref="GridPerpendicularToleranceDegrees"/> (tường xiên — không có
    /// Grid nào hợp lệ để "DIM" theo kiểu vuông góc chuẩn, giữ nguyên vị trí).</summary>
    private static string SnapToGrid(Document doc, FamilyInstance familyInstance, Wall wall, IReadOnlyList<Grid> grids)
    {
        if (wall.Location is not LocationCurve locationCurve || locationCurve.Curve is not Line wallLine)
            return "Không làm tròn Grid: tường không phải Location Line.";

        var actualCenter = TryGetCenterLeftRightPoint(familyInstance) ?? GetBoundingBoxCenter(familyInstance);
        if (actualCenter is null)
            return "Không làm tròn Grid: không đọc được vị trí thật của cửa.";

        var wallP0 = wallLine.GetEndPoint(0);
        var wallDirection = (wallLine.GetEndPoint(1) - wallP0).Normalize();
        var currentT = (actualCenter - wallP0).DotProduct(wallDirection);

        var nearestGrid = FindNearestPerpendicularGrid(wallP0, wallDirection, currentT, grids, out var gridT);
        if (nearestGrid is null)
            return "Không làm tròn Grid: không có Grid vuông góc đủ gần (tường xiên).";

        var rawDistanceMm = UnitUtils.ConvertFromInternalUnits(currentT - gridT, UnitTypeId.Millimeters);
        var roundedMm = Math.Round(rawDistanceMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;
        var deltaMm = roundedMm - rawDistanceMm;
        var deltaFeet = UnitUtils.ConvertToInternalUnits(deltaMm, UnitTypeId.Millimeters);

        if (Math.Abs(deltaFeet) < 1e-6)
            return $"Grid {nearestGrid.Name}: {Math.Round(rawDistanceMm, 3)}mm — đã tròn, không cần chỉnh.";

        ElementTransformUtils.MoveElement(doc, familyInstance.Id, wallDirection.Multiply(deltaFeet));
        return $"Grid {nearestGrid.Name}: {Math.Round(rawDistanceMm, 3)}mm -> làm tròn {roundedMm}mm " +
               $"(dịch {Math.Round(deltaMm, 3)}mm).";
    }

    /// <summary>Tìm Grid THẲNG (bỏ qua Grid cung, hiếm gặp) VUÔNG GÓC với phương tường (trong dung sai
    /// GridPerpendicularToleranceDegrees) mà điểm giao với trục tường (kéo dài vô hạn cả 2 đường) GẦN
    /// <paramref name="currentT"/> nhất (toạ độ dọc tường) — đây đúng là Grid người dùng sẽ dùng để ghi
    /// Dimension cho cửa này trên bản vẽ thật. Trả về null nếu không Grid nào đủ vuông góc (tường xiên,
    /// không thẳng hàng với hệ Grid).</summary>
    private static Grid? FindNearestPerpendicularGrid(
        XYZ wallP0, XYZ wallDirection, double currentT, IReadOnlyList<Grid> grids, out double gridT)
    {
        var perpendicularSinTolerance = Math.Sin(GridPerpendicularToleranceDegrees * Math.PI / 180.0);

        Grid? best = null;
        var bestDistance = double.MaxValue;
        gridT = 0.0;

        foreach (var grid in grids)
        {
            if (grid.Curve is not Line gridLine)
                continue;

            var gridP0 = gridLine.GetEndPoint(0);
            var gridDirection = (gridLine.GetEndPoint(1) - gridP0).Normalize();

            if (Math.Abs(wallDirection.DotProduct(gridDirection)) > perpendicularSinTolerance)
                continue;

            var intersection = IntersectLines(wallP0, wallDirection, gridP0, gridDirection);
            if (intersection is null)
                continue;

            var t = (intersection - wallP0).DotProduct(wallDirection);
            var distance = Math.Abs(t - currentT);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = grid;
            gridT = t;
        }

        return best;
    }

    /// <summary>Giao điểm 2 đường thẳng (p1,d1) và (p2,d2), làm phẳng về Z=0 — null nếu song song (không
    /// nên xảy ra ở đây vì đã lọc vuông góc trước khi gọi, giữ lại để an toàn).</summary>
    private static XYZ? IntersectLines(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
    {
        var denom = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denom) < 1e-9)
            return null;

        var diff = p2 - p1;
        var t1 = (diff.X * d2.Y - diff.Y * d2.X) / denom;
        return new XYZ(p1.X + d1.X * t1, p1.Y + d1.Y * t1, 0);
    }
}
