using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.LinkToDirectShape;

public sealed class ConvertReport
{
    public int Created { get; set; }
    public int SkippedNoGeometry { get; set; }
    public int Failed { get; set; }
    public int MaterialsCopied { get; set; }
    public int ParametersCreated { get; set; }
    public int ParametersReady { get; set; }
    public int ValuesWritten { get; set; }
    public string? MetadataError { get; set; }

    /// <summary>Revit có chấp nhận cắt khối vừa tạo bằng void hay không. null = chưa kiểm tra được.</summary>
    public bool? VoidCuttable { get; set; }
    public List<string> Errors { get; } = new();
    public List<ElementId> CreatedIds { get; } = new();
}

/// <summary>Dựng DirectShape trong file host từ element của Revit link.
///
/// Hai đường đi hình học, chọn bằng ConvertOptions.Geometry:
///   - ExactSolid (mặc định): lấy thẳng Solid/Mesh đã transform sang toạ độ host. Giữ nguyên BRep —
///     mặt cong vẫn cong, lỗ/hốc còn nguyên. Material trên mặt trỏ về ElementId của LINK doc nên
///     sang host vô nghĩa; bù lại bằng cách gán material ở mức element + ghi tên vào metadata.
///   - MaterialTessellated: copy material sang host rồi dựng lại khối bằng TessellatedShapeBuilder,
///     gán material đúng cho từng mặt (TessellatedFace nhận materialId — đây là chỗ duy nhất API cho
///     đặt material khi tạo DirectShape). Đổi lại mặt cong bị chia thành mặt phẳng.
///
/// LỊCH SỬ - ĐỪNG LẶP LẠI: bản đầu mặc định chạy tessellate và gom CẢ element vào 1 builder. Chỉ cần
/// một khối chia mặt không khép kín là toàn bộ element rớt xuống Mesh: lỗ tròn hiện thành khe mảnh,
/// khối đặc thành vỏ mỏng, khác hẳn tool khác cùng chức năng. Nay mặc định là ExactSolid và đường
/// tessellate dựng riêng từng khối, hỏng khối nào thì khối đó lùi về solid gốc.
/// </summary>
public sealed class ShapeConverter
{
    private const double MinVolume = 1e-9;      // ft3 (~0.03 mm3) - chỉ loại solid rác/độ dày 0
    private const double MinTriangleArea = 1e-9; // ft2 - tam giác suy biến làm AddFace ném lỗi

    private readonly Document _hostDoc;
    private readonly ConvertOptions _options;
    private readonly MetadataCopier _metadata;
    private readonly Dictionary<string, ElementId> _materialMap = new();
    private Dictionary<string, ElementId>? _hostMaterialsByName;
    private int _materialsCopied;

    public ShapeConverter(Document hostDoc, ConvertOptions options)
    {
        _hostDoc = hostDoc;
        _options = options;
        _metadata = new MetadataCopier(hostDoc, options);
    }

    /// <summary>Chạy nguyên 1 transaction để user Ctrl+Z một phát là sạch.</summary>
    public ConvertReport Run(IReadOnlyList<(RevitLinkInstance Link, Element Element)> items)
    {
        var report = new ConvertReport();


        // Parameter phải được tạo + bind XONG trước khi tạo DirectShape thì element mới nhận được
        // ô để ghi. Lấy mẫu schema trước, tạo một lượt, rồi mới dựng hình.
        if (_metadata.Enabled)
        {
            foreach (var (_, element) in items)
                _metadata.Sample(element);

            _metadata.EnsureParameters();
        }

        foreach (var (link, element) in items)
        {
            try
            {
                if (ConvertOne(link, element, report))
                    report.Created++;
                else
                    report.SkippedNoGeometry++;
            }
            catch (Exception ex)
            {
                report.Failed++;
                if (report.Errors.Count < 10)
                    report.Errors.Add($"{Describe(element)}: {ex.Message}");
            }
        }

        report.MaterialsCopied = _materialsCopied;
        report.ParametersCreated = _metadata.ParametersCreated;
        report.ParametersReady = _metadata.ParametersReady;
        report.ValuesWritten = _metadata.ValuesWritten;
        report.MetadataError = _metadata.LastError;

        return report;
    }

    private bool ConvertOne(RevitLinkInstance link, Element element, ConvertReport report)
    {
        var linkDoc = link.GetLinkDocument();
        if (linkDoc is null)
            return false;

        var geometryOptions = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false,
            DetailLevel = ViewDetailLevel.Fine
        };

        var geometry = element.get_Geometry(geometryOptions);
        if (geometry is null)
            return false;

        // Đưa hình học từ hệ toạ độ link sang hệ toạ độ host (link có thể lệch vị trí/xoay/shared
        // coordinates). GetTransformed xử lý luôn cả Solid lẫn Mesh nên không cần transform tay.
        var transformed = geometry.GetTransformed(link.GetTotalTransform());

        var solids = new List<Solid>();
        var meshes = new List<Mesh>();
        Collect(transformed, solids, meshes, 0);

        if (solids.Count == 0 && meshes.Count == 0)
            return false;

        IList<GeometryObject> shape = _options.Geometry == GeometryMode.MaterialTessellated
            ? BuildTessellated(linkDoc, solids, meshes)
            : Raw(solids, meshes);

        if (shape.Count == 0)
            return false;

        var directShape = DirectShape.CreateElement(
            _hostDoc, new ElementId(BuiltInCategory.OST_GenericModel));
        directShape.SetShape(shape);
        MakeReferenceable(directShape);
        report.VoidCuttable ??= CanBeCutWithVoid(directShape);

        try
        {
            directShape.ApplicationId = "BIMTech.LinkToDirectShape";
            directShape.ApplicationDataId = element.UniqueId;
            directShape.Name = Describe(element);
        }
        catch (Exception)
        {
            // Tên/ApplicationId chỉ là thông tin phụ, không đáng để huỷ cả element.
        }

        // Material chiếm nhiều mặt nhất của element. Ở chế độ ExactSolid đây là cách duy nhất còn
        // lại để giữ material: gán ở mức element (nếu có ô material) + ghi tên vào metadata.
        var dominant = DominantMaterial(linkDoc, solids, meshes);
        if (dominant.HostId != ElementId.InvalidElementId)
            TrySetElementMaterial(directShape, dominant.HostId);

        if (_options.WriteTracking)
            WriteTracking(directShape, link, linkDoc, element);

        _metadata.Apply(directShape, link, linkDoc, element, dominant.Name);

        report.CreatedIds.Add(directShape.Id);
        return true;
    }

    // ===================== Hình học =====================

    private static void Collect(GeometryElement geometry, List<Solid> solids, List<Mesh> meshes, int depth)
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
                case Mesh mesh when mesh.NumTriangles > 0:
                    meshes.Add(mesh);
                    break;
                case GeometryInstance instance:
                    Collect(instance.GetInstanceGeometry(), solids, meshes, depth + 1);
                    break;
            }
        }
    }

    /// <summary>Mặc định hình học DirectShape là NotReferenceable: không snap, không dimension, không
    /// pick được mặt. Bật Referenceable để dùng như cấu kiện bình thường.</summary>
    private static void MakeReferenceable(DirectShape directShape)
    {
        try
        {
            var options = directShape.GetOptions();
            options.ReferencingOption = DirectShapeReferencingOption.Referenceable;
            if (directShape.AreOptionsValid(options))
                directShape.SetOptions(options);
        }
        catch (Exception)
        {
            // Không bật được thì hình khối vẫn đúng, chỉ kém tiện khi đo đạc.
        }
    }

    /// <summary>"Cut with void" KHÔNG phải thuộc tính gán được lúc tạo. Đã soi hết metadata RevitAPI
    /// 2025: DirectShapeOptions chỉ có ReferencingOption/RoomBoundingOption, DirectShapeTypeOptions
    /// chỉ có AllowDuplicateNames, Category.IsCuttable là read-only, còn FAMILY_ALLOW_CUT_WITH_VOIDS
    /// là parameter của FAMILY chứ không áp cho DirectShape. Quyền cắt do Revit quyết và chỉ đọc
    /// được qua hàm này — nên tool hỏi thẳng rồi báo lại cho người dùng thay vì hứa suông.</summary>
    private static bool? CanBeCutWithVoid(Element element)
    {
        try
        {
            return InstanceVoidCutUtils.CanBeCutWithVoid(element);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<GeometryObject> Raw(List<Solid> solids, List<Mesh> meshes)
    {
        var shape = new List<GeometryObject>(solids.Count + meshes.Count);
        shape.AddRange(solids);
        shape.AddRange(meshes);
        return shape;
    }

    /// <summary>Dựng lại khối bằng tessellation để gán được material của host cho từng mặt.
    ///
    /// Dựng RIÊNG từng solid: trước đây gom cả element vào một builder nên chỉ cần một khối chia
    /// mặt không khép kín là cả element rớt xuống Mesh (lỗ tròn thành khe, khối đặc thành vỏ mỏng).
    /// Nay khối nào tessellate hỏng thì chỉ khối đó lùi về solid BRep gốc, các khối khác vẫn giữ
    /// material.</summary>
    private List<GeometryObject> BuildTessellated(Document linkDoc, List<Solid> solids, List<Mesh> meshes)
    {
        var shape = new List<GeometryObject>();

        foreach (var solid in solids)
        {
            var built = BuildOne(builder =>
            {
                var added = 0;
                builder.OpenConnectedFaceSet(true);
                foreach (Face face in solid.Faces)
                {
                    var materialId = MapMaterial(linkDoc, face.MaterialElementId);
                    added += AddTriangles(builder, face.Triangulate(1.0), materialId);
                }
                builder.CloseConnectedFaceSet();
                return added;
            });

            if (built is not null)
                shape.AddRange(built);
            else
                shape.Add(solid); // Thà mất material còn hơn méo hình.
        }

        foreach (var mesh in meshes)
        {
            var built = BuildOne(builder =>
            {
                builder.OpenConnectedFaceSet(false);
                var added = AddTriangles(builder, mesh, MapMaterial(linkDoc, mesh.MaterialElementId));
                builder.CloseConnectedFaceSet();
                return added;
            });

            if (built is not null)
                shape.AddRange(built);
            else
                shape.Add(mesh);
        }

        return shape;
    }

    /// <summary>Chạy 1 lượt TessellatedShapeBuilder. Trả về null nếu không dựng được khối đặc —
    /// người gọi tự quyết định lùi về hình gốc.</summary>
    private static IList<GeometryObject>? BuildOne(Func<TessellatedShapeBuilder, int> fill)
    {
        try
        {
            // Solid + Abort: chỉ nhận khối đặc thật. Không dựng được thì Build() ném lỗi và người
            // gọi lùi về solid gốc — cố tình KHÔNG cho rớt xuống Mesh vì đó là thứ làm hình bị dẹt.
            var builder = new TessellatedShapeBuilder
            {
                GraphicsStyleId = ElementId.InvalidElementId,
                Target = TessellatedShapeBuilderTarget.Solid,
                Fallback = TessellatedShapeBuilderFallback.Abort
            };

            if (fill(builder) == 0)
                return null;

            builder.Build();
            var result = builder.GetBuildResult();
            if (result.Outcome is TessellatedShapeBuilderOutcome.Nothing
                or TessellatedShapeBuilderOutcome.Mesh)
            {
                // Mesh nghĩa là khối không khép kín được — đó chính là kiểu hình bị "mỏng dẹt".
                return null;
            }

            var objects = result.GetGeometricalObjects();
            return objects.Count > 0 ? objects : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Material xuất hiện trên nhiều mặt nhất của element (đếm mặt, không tính diện tích
    /// để khỏi phải giải tích từng face).</summary>
    private (ElementId HostId, string? Name) DominantMaterial(
        Document linkDoc, List<Solid> solids, List<Mesh> meshes)
    {
        var counter = new Dictionary<long, int>();

        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces)
            {
                var id = face.MaterialElementId;
                if (id == ElementId.InvalidElementId)
                    continue;

                counter[id.Value] = counter.GetValueOrDefault(id.Value) + 1;
            }
        }

        foreach (var mesh in meshes)
        {
            var id = mesh.MaterialElementId;
            if (id != ElementId.InvalidElementId)
                counter[id.Value] = counter.GetValueOrDefault(id.Value) + 1;
        }

        if (counter.Count == 0)
            return (ElementId.InvalidElementId, null);

        var linkMaterialId = new ElementId(counter.OrderByDescending(pair => pair.Value).First().Key);
        var name = (linkDoc.GetElement(linkMaterialId) as Material)?.Name;
        return (MapMaterial(linkDoc, linkMaterialId), name);
    }

    /// <summary>Gán material ở mức element nếu DirectShape có ô material (tuỳ category/template).
    /// Không có thì bỏ qua — tên material vẫn được ghi vào metadata.</summary>
    private static void TrySetElementMaterial(DirectShape directShape, ElementId materialId)
    {
        var target = directShape.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);

        if (target is null || target.IsReadOnly)
        {
            foreach (Parameter parameter in directShape.Parameters)
            {
                if (parameter.StorageType != StorageType.ElementId || parameter.IsReadOnly)
                    continue;
                if (parameter.Definition?.Name is not ("Material" or "Vật liệu"))
                    continue;

                target = parameter;
                break;
            }
        }

        if (target is null || target.IsReadOnly || target.StorageType != StorageType.ElementId)
            return;

        try
        {
            target.Set(materialId);
        }
        catch (Exception)
        {
            // Ô material không nhận id này — bỏ qua.
        }
    }

    private static int AddTriangles(TessellatedShapeBuilder builder, Mesh? mesh, ElementId materialId)
    {
        if (mesh is null)
            return 0;

        var added = 0;
        for (var i = 0; i < mesh.NumTriangles; i++)
        {
            var triangle = mesh.get_Triangle(i);
            var a = triangle.get_Vertex(0);
            var b = triangle.get_Vertex(1);
            var c = triangle.get_Vertex(2);

            // Tam giác suy biến làm TessellatedShapeBuilder ném lỗi và hỏng cả element.
            if ((b - a).CrossProduct(c - a).GetLength() < MinTriangleArea)
                continue;

            builder.AddFace(new TessellatedFace(new List<XYZ> { a, b, c }, materialId));
            added++;
        }

        return added;
    }

    // ===================== Material =====================

    /// <summary>Material id trong link doc -> material tương ứng trong host doc (tìm theo tên, không
    /// có thì copy sang). Trả về InvalidElementId nếu không map được (DirectShape sẽ dùng material
    /// mặc định của category).</summary>
    private ElementId MapMaterial(Document linkDoc, ElementId linkMaterialId)
    {
        if (linkMaterialId == ElementId.InvalidElementId)
            return ElementId.InvalidElementId;

        var key = $"{linkDoc.Title}#{linkMaterialId.Value}";
        if (_materialMap.TryGetValue(key, out var cached))
            return cached;

        var mapped = ElementId.InvalidElementId;
        try
        {
            if (linkDoc.GetElement(linkMaterialId) is Material linkMaterial)
            {
                var hostMaterials = HostMaterials();
                if (hostMaterials.TryGetValue(linkMaterial.Name, out var existing))
                {
                    mapped = existing;
                }
                else
                {
                    var copied = ElementTransformUtils.CopyElements(
                        linkDoc, new List<ElementId> { linkMaterialId }, _hostDoc,
                        Transform.Identity, new CopyPasteOptions());

                    mapped = copied.FirstOrDefault() ?? ElementId.InvalidElementId;
                    if (mapped != ElementId.InvalidElementId)
                    {
                        _materialsCopied++;
                        if (_hostDoc.GetElement(mapped) is Material copiedMaterial)
                            hostMaterials[copiedMaterial.Name] = mapped;
                    }
                }
            }
        }
        catch (Exception)
        {
            mapped = ElementId.InvalidElementId;
        }

        _materialMap[key] = mapped;
        return mapped;
    }

    private Dictionary<string, ElementId> HostMaterials()
    {
        if (_hostMaterialsByName is not null)
            return _hostMaterialsByName;

        _hostMaterialsByName = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in new FilteredElementCollector(_hostDoc)
                     .OfClass(typeof(Material)).Cast<Material>())
        {
            _hostMaterialsByName[material.Name] = material.Id;
        }

        return _hostMaterialsByName;
    }

    // ===================== Truy vết nguồn =====================

    private static void WriteTracking(
        DirectShape directShape, RevitLinkInstance link, Document linkDoc, Element source)
    {
        var linkName = LinkScanner.DescribeLink(link, linkDoc);
        var typeName = source.Document.GetElement(source.GetTypeId())?.Name ?? "";
        var comment = $"Link: {linkName} | Id: {source.Id.Value} | " +
                      $"Category: {source.Category?.Name ?? "-"}";
        if (!string.IsNullOrWhiteSpace(typeName))
            comment += $" | Type: {typeName}";

        SetText(directShape, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, comment);
        SetText(directShape, BuiltInParameter.ALL_MODEL_MARK, $"{linkDoc.Title}:{source.Id.Value}");
    }

    private static void SetText(Element element, BuiltInParameter parameter, string value)
    {
        var target = element.get_Parameter(parameter);
        if (target is null || target.IsReadOnly || target.StorageType != StorageType.String)
            return;

        try
        {
            target.Set(value);
        }
        catch (Exception)
        {
            // Parameter bị khoá bởi worksharing/template — bỏ qua, hình khối vẫn đúng.
        }
    }

    private static string Describe(Element element)
    {
        var category = element.Category?.Name;
        var name = element.Name;
        if (string.IsNullOrWhiteSpace(name))
            return category ?? $"Element {element.Id.Value}";

        return string.IsNullOrWhiteSpace(category) ? name : $"{category} - {name}";
    }
}
