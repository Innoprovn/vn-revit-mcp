namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Commands.Rebar.Cover;
using InnoPro.Revit.Sdk;

public sealed class RebarCoverBlock : RevitCommandBase
{
    public override string Name => "rebar_cover_block";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Tạo khối DirectShape tượng trưng vùng đặt thép: cấu kiện co vào trong theo chiều dày lớp bảo vệ (đáy và các mặt còn lại).";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Cấu kiện cần tạo khối cover." },
            "bottom_cover_mm": { "type": "number", "description": "Cover mặt đáy, mặc định 50." },
            "side_cover_mm": { "type": "number", "description": "Cover các mặt khác, mặc định 40." },
            "bottom_direction": { "type": "string", "enum": ["down", "up", "none"], "description": "Hướng mặt đáy, mặc định down." },
            "bottom_faces": { "type": "array", "items": { "type": "string" }, "description": "Mặt đáy (stable reference), thay cho bottom_direction." },
            "hole_faces": { "type": "array", "items": { "type": "string" }, "description": "Mặt lỗ cần bỏ qua (stable reference)." },
            "apply_to_parallel_faces": { "type": "boolean", "description": "Mặt song song cùng chiều cũng dùng cover đáy, mặc định true." },
            "fill_holes": { "type": "boolean", "description": "Lấp lỗ, chỉ lấy vỏ ngoài, mặc định true." },
            "allow_rebar_host": { "type": "boolean", "description": "Cho khối làm host thép (đổi sang category kết cấu)." },
            "host_category": { "type": "string", "enum": ["OST_StructuralFoundation", "OST_StructuralFraming", "OST_StructuralColumns", "OST_Walls", "OST_Floors"], "description": "Category khi làm host." }
          },
          "required": ["element_ids"]
        }
        """;

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var bottomMm = ArgsX.Double(arguments, "bottom_cover_mm") ?? 50;
        var sideMm = ArgsX.Double(arguments, "side_cover_mm") ?? 40;
        if (bottomMm <= 0 || sideMm <= 0) throw new CommandException("Chiều dày cover phải lớn hơn 0.");
        var direction = ArgsX.String(arguments, "bottom_direction") ?? "down";
        var allowHost = Args.GetBoolOrDefault(arguments, "allow_rebar_host", false);
        var hostCategory = allowHost && ArgsX.String(arguments, "host_category") is { } cat
            ? Enum.Parse<BuiltInCategory>(cat, ignoreCase: true)
            : BuiltInCategory.OST_StructuralFoundation;

        // Mặt đáy và mặt lỗ chỉ định bằng tham chiếu ổn định (pick trong Revit hoặc AI truyền lại).
        var pickedBottoms = new Dictionary<ElementId, List<BottomPlane>>();
        foreach (var text in Args.GetStringsOrEmpty(arguments, "bottom_faces"))
        {
            var (element, face) = ParseFace(doc, text);
            if (element == null || face == null) continue;
            if (!pickedBottoms.TryGetValue(element.Id, out var list)) pickedBottoms[element.Id] = list = [];
            list.Add(PlaneOf(element, face));
        }
        var excluded = new HashSet<string>();
        foreach (var text in Args.GetStringsOrEmpty(arguments, "hole_faces"))
            if (ParseFace(doc, text).Face is { } f && CoverBuilder.Signature(f) is { } sig) excluded.Add(sig);

        var options = new CoverOptions
        {
            BottomCover = UnitConvert.MmToFeet(direction == "none" && !pickedBottoms.Any() ? sideMm : bottomMm),
            SideCover = UnitConvert.MmToFeet(sideMm),
            ApplyToParallelFaces = Args.GetBoolOrDefault(arguments, "apply_to_parallel_faces", true),
            FillHoles = Args.GetBoolOrDefault(arguments, "fill_holes", true),
            AllowRebarHost = allowHost,
            HostCategory = hostCategory,
            ExcludedFaces = excluded,
        };

        var categoryId = new ElementId(allowHost ? hostCategory : BuiltInCategory.OST_GenericModel);
        if (!DirectShape.IsValidCategoryId(categoryId, doc)) categoryId = new ElementId(BuiltInCategory.OST_GenericModel);

        var ids = Args.GetElementIds(arguments, "element_ids").Concat(pickedBottoms.Keys).Distinct().ToList();
        var created = new List<object>();
        var failed = new List<object>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element == null)
            {
                failed.Add(new { element_id = id.Value, reason = "không tồn tại" });
                continue;
            }

            var solids = CollectSolids(element);
            if (solids.Count == 0)
            {
                failed.Add(new { element_id = id.Value, reason = "không có khối hình học" });
                continue;
            }

            var bottoms = pickedBottoms.TryGetValue(id, out var picked) ? picked : AutoBottom(solids, direction);
            var ds = DirectShape.CreateElement(doc, categoryId);
            var shape = BuildShape(ds, solids, bottoms, options, out var result, out var reason);
            if (shape == null)
            {
                doc.Delete(ds.Id);
                failed.Add(new { element_id = id.Value, reason });
                continue;
            }
            ds.SetShape(shape);
            try
            {
                var shapeOptions = ds.GetOptions();
                shapeOptions.ReferencingOption = DirectShapeReferencingOption.Referenceable;
                if (ds.AreOptionsValid(shapeOptions)) ds.SetOptions(shapeOptions);
                ds.ApplicationId = "InnoPro.Rebar.CoverBlock";
                ds.ApplicationDataId = element.UniqueId;
                ds.Name = $"{(string.IsNullOrWhiteSpace(element.Name) ? element.Category?.Name : element.Name)} - Cover";
            }
            catch (Exception)
            {
                // Tên và tùy chọn tham chiếu chỉ là phụ.
            }
            SetText(ds, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, $"Cover: đáy {bottomMm:0.##} mm, mặt khác {sideMm:0.##} mm | Nguồn: {element.Id.Value}");
            SetText(ds, BuiltInParameter.ALL_MODEL_MARK, $"{element.Id.Value}-COVER");

            bool? hostable = null;
            if (allowHost)
            {
                try { hostable = RebarHostData.GetRebarHostData(ds) != null; }
                catch (Exception) { hostable = false; }
            }

            created.Add(new
            {
                source_id = id.Value,
                cover_id = ds.Id.Value,
                method = result!.OffsetSolids > 0 ? "offset" : result.ErodedSolids > 0 ? "erosion" : "boolean",
                source_faces = result.SourceFaces,
                broken_edges = result.SourceBrokenEdges,
                rebar_hostable = hostable,
            });
        }

        if (created.Count == 0)
            throw new CommandException("Không tạo được khối cover nào. " + string.Join("; ", failed.Select(f => f.ToString())));

        return new
        {
            created = created.Count,
            category = Autodesk.Revit.DB.Category.GetCategory(doc, categoryId)?.Name,
            covers = Table.Of(created),
            failed = Table.Of(failed),
            hint = created.Count > 0 && failed.Count == 0 ? null : "Hình gốc vụn (đã tessellate) thường không dựng được; giảm cover hoặc dùng solid BRep.",
        };
    }

    /// <summary>Dựng hình; khối offset bị DirectShape từ chối thì thử lại bằng cắt boolean.</summary>
    private static List<GeometryObject>? BuildShape(DirectShape ds, List<Solid> solids, List<BottomPlane> bottoms,
        CoverOptions options, out CoverBuildResult? result, out string reason)
    {
        reason = "";
        result = null;
        foreach (var attempt in new[] { options, options with { SkipPlaneOffset = true } })
        {
            result = CoverBuilder.Build(solids, bottoms, attempt);
            if (result.TooComplex)
            {
                reason = $"hình quá vụn ({result.SourceFaces} mặt)";
                return null;
            }
            if (result.Solids.Count == 0)
            {
                reason = result.Escaped > 0 ? "khối dựng ra lòi ngoài cấu kiện" : "cấu kiện mỏng hơn hai lần cover hoặc hình quá phức tạp";
                continue;
            }

            var shape = result.Solids.Cast<GeometryObject>().ToList();
            if (ds.IsValidShape(shape)) return shape;
            reason = "Revit từ chối hình (cạnh quá ngắn hoặc mặt suy biến)";
        }
        return null;
    }

    private static List<BottomPlane> AutoBottom(List<Solid> solids, string direction)
    {
        var dir = direction == "up" ? XYZ.BasisZ : XYZ.BasisZ.Negate();
        PlanarFace? best = null;
        foreach (var solid in solids)
            foreach (Face f in solid.Faces)
                if (f is PlanarFace pf && pf.FaceNormal.DotProduct(dir) > 0.95
                    && (best == null || pf.Origin.DotProduct(dir) > best.Origin.DotProduct(dir)))
                    best = pf;

        // Không có mặt nằm ngang: vẫn cần một mặt phẳng tham chiếu, dùng đáy hộp bao (không khớp mặt nào thì chỉ áp cover mặt khác).
        return best == null ? [new BottomPlane(dir, XYZ.Zero)] : [new BottomPlane(best.FaceNormal, best.Origin)];
    }

    private static (Element? Element, Face? Face) ParseFace(Document doc, string stable)
    {
        try
        {
            var reference = Reference.ParseFromStableRepresentation(doc, stable);
            var element = doc.GetElement(reference.ElementId);
            return (element, element?.GetGeometryObjectFromReference(reference) as Face);
        }
        catch (Exception)
        {
            throw new CommandException($"Tham chiếu mặt không hợp lệ: {stable}");
        }
    }

    private static BottomPlane PlaneOf(Element element, Face face)
    {
        XYZ normal, origin;
        if (face is PlanarFace planar)
        {
            normal = planar.FaceNormal;
            origin = planar.Origin;
        }
        else
        {
            var box = face.GetBoundingBox();
            var uv = new UV((box.Min.U + box.Max.U) / 2, (box.Min.V + box.Max.V) / 2);
            normal = face.ComputeNormal(uv);
            origin = face.Evaluate(uv);
        }
        if (element is FamilyInstance fi)
        {
            var t = fi.GetTotalTransform();
            normal = t.OfVector(normal);
            origin = t.OfPoint(origin);
        }
        return new BottomPlane(normal.Normalize(), origin);
    }

    private static List<Solid> CollectSolids(Element element)
    {
        var solids = new List<Solid>();
        var geometry = element.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine });
        if (geometry != null) Collect(geometry, solids, 0);
        return solids;
    }

    private static void Collect(GeometryElement geometry, List<Solid> solids, int depth)
    {
        if (depth > 8) return;
        foreach (var obj in geometry)
        {
            if (obj is Solid solid && solid.Volume > 1e-9 && solid.Faces.Size > 0) solids.Add(solid);
            else if (obj is GeometryInstance instance) Collect(instance.GetInstanceGeometry(), solids, depth + 1);
        }
    }

    private static void SetText(Element element, BuiltInParameter parameter, string value)
    {
        var p = element.get_Parameter(parameter);
        if (p is { IsReadOnly: false, StorageType: StorageType.String })
        {
            try { p.Set(value); }
            catch (Exception) { /* tham số bị khóa */ }
        }
    }
}
