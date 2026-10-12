using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.Rebar.Cover;

/// <summary>Dựng khối cover bằng cách GIAO khối với chính nó đã dời đi, theo từng hướng pháp tuyến.
///
/// Nguyên lý: giao khối S với bản sao của nó dời đi một đoạn `-n * d` thì gọt đúng mặt có pháp tuyến
/// `n` vào trong đoạn `d`, và không đụng tới mặt nào khác (mặt vuông góc với n không mất gì, mặt
/// quay lưng lại không mất gì). Làm lần lượt cho mọi hướng pháp tuyến của khối là ra đúng khối co
/// vào — chính là khối cover.
///
/// Vì sao chọn cách này sau rất nhiều lần thất bại với hai cách kia:
///   - KHÔNG dựng lại topology, nên không phụ thuộc BRepBuilder (thứ đã fail cả khi dựng lại đúng
///     topology có sẵn của khối).
///   - KHÔNG dựng dao theo từng mặt, nên hình gốc 12 mặt hay 149 mặt vụn đều chạy như nhau. Cách cắt
///     dao từng mặt hỏng 75/149 con dao là nguồn gốc của mớ hình vụn.
///   - Giao của các bản sao một khối hợp lệ thì LUÔN hợp lệ: không thể sinh vây, vấu hay mảnh rời.
///   - Chính xác tuyệt đối với mặt phẳng: mỗi mặt bị gọt đúng bằng cover của nó, không mặt nào bị
///     gọt quá (hàm tựa của bao lồi các điểm dời đạt cực đại đúng tại pháp tuyến của chính mặt đó).
///   - Góc tù, góc nhọn, góc lõm tự đúng, không cần xử lý riêng.
///
/// Đánh đổi: mỗi hướng là một phép boolean, nên chậm hơn hẳn - vài chục phép cho một cục.</summary>
internal static class TranslateErosion
{
    private const double MinVolume = 1e-9;
    private const double PointTolerance = 1e-6;

    /// <summary>Hai hướng coi là một khi tích vô hướng vượt ngưỡng này.</summary>
    private const double SameDirection = 0.9999;

    /// <summary>Mặt coi là "mặt đáy" khi pháp tuyến lệch dưới ~18 độ so với mặt đã pick.</summary>
    private const double BottomTolerance = 0.95;

    /// <summary>Trần số hướng, để khối cực vụn không kéo thời gian chạy đi quá xa.</summary>
    private const int MaxDirections = 160;

    /// <summary>Lệch ngang khi thử lại phép giao, ~0.003 mm — đủ phá thế trùng mặt, quá nhỏ để ảnh
    /// hưởng chiều dày cover.</summary>
    private const double Jitter = 1e-5;

    public static Solid? Build(
        Solid source, IReadOnlyList<BottomPlane> bottoms, CoverOptions options,
        Func<Face, bool> excluded, out string? error, out int directions)
    {
        error = null;

        var normals = CollectNormals(source, excluded);
        directions = normals.Count;

        if (normals.Count == 0)
        {
            error = "không đọc được hướng mặt nào của khối";
            return null;
        }

        if (normals.Count > MaxDirections)
        {
            error = $"khối có {normals.Count} hướng mặt, vượt trần {MaxDirections}";
            return null;
        }

        var bottomDirections = Distinct(bottoms.Select(b => b.Normal));
        var baseCover = Math.Min(options.BottomCover, options.SideCover);
        var failures = 0;
        string? reason = null;

        // Nhịp 1: gọt đều mọi mặt.
        var result = Erode(source, normals, baseCover, ref failures, ref reason);
        if (result is null)
        {
            error = $"{reason ?? "không rõ"} ({failures}/{normals.Count} hướng hỏng)";
            return null;
        }

        // Nhịp 2: gọt thêm phần chênh, chỉ theo hướng mặt đáy đã pick. Mặt bên vuông góc với hướng
        // này nên không mất gì thêm.
        var extraBottom = options.BottomCover - baseCover;
        if (extraBottom > PointTolerance && bottomDirections.Count > 0)
        {
            result = Erode(result, bottomDirections, extraBottom, ref failures, ref reason);
            if (result is null)
            {
                error = $"gọt thêm cover đáy hỏng: {reason ?? "không rõ"}";
                return null;
            }
        }

        // Nhịp 3: trường hợp hiếm - cover mặt bên lớn hơn cover đáy.
        var extraSide = options.SideCover - baseCover;
        if (extraSide > PointTolerance)
        {
            var sideDirections = normals
                .Where(n => !bottomDirections.Any(b => n.DotProduct(b) > BottomTolerance))
                .ToList();

            if (sideDirections.Count > 0)
            {
                result = Erode(result, sideDirections, extraSide, ref failures, ref reason);
                if (result is null)
                {
                    error = $"gọt thêm cover mặt bên hỏng: {reason ?? "không rõ"}";
                    return null;
                }
            }
        }

        // Gọt xong nhưng có hướng hỏng nghĩa là cover THIẾU ở những hướng đó - phải nói, không được
        // im lặng đưa ra khối thiếu cover.
        if (failures > 0)
            error = $"thiếu cover ở {failures}/{normals.Count} hướng ({reason})";

        return result;
    }

    /// <summary>Gọt khối theo từng hướng: giao với bản sao dời đi `-n * distance`.</summary>
    /// <summary>Gọt khối theo từng hướng: giao với bản sao dời đi `-n * distance`.
    ///
    /// Hai lệnh trong vòng lặp được tách try/catch RIÊNG và giữ lại thông điệp lỗi. Bản trước bọc
    /// chung rồi chỉ đếm số lần hỏng, nên không phân biệt được lỗi nằm ở khâu dời khối hay khâu giao
    /// khối — và tôi đã suy diễn thành "hình gốc hỏng" mà không có bằng chứng.</summary>
    private static Solid? Erode(
        Solid solid, IReadOnlyList<XYZ> normals, double distance, ref int failures, ref string? reason)
    {
        if (distance <= PointTolerance)
            return solid;

        var result = solid;

        foreach (var normal in normals)
        {
            // Dời thẳng theo pháp tuyến làm các mặt bên của hai bản sao TRÙNG KHÍT nhau, và đó chính
            // là tình huống Revit hay báo "slightly misaligned faces / two coincident edges" rồi bỏ
            // cuộc. Lệch nhẹ sang ngang vài phần nghìn milimét là hết trùng, sai số không đáng kể.
            var sideways = Perpendicular(normal);

            var done = false;
            for (var attempt = 0; attempt < 3 && !done; attempt++)
            {
                var jitter = attempt == 0 ? XYZ.Zero : sideways * (Jitter * attempt);

                try
                {
                    var moved = SolidUtils.CreateTransformed(
                        solid, Transform.CreateTranslation(-normal * distance + jitter));

                    var next = BooleanOperationsUtils.ExecuteBooleanOperation(
                        result, moved, BooleanOperationsType.Intersect);

                    if (next is null)
                        continue;

                    if (next.Volume <= MinVolume)
                    {
                        reason ??= "giao xong còn thể tích 0 ở hướng " +
                                   $"({normal.X:F2},{normal.Y:F2},{normal.Z:F2})";
                        return null;
                    }

                    result = next;
                    done = true;
                }
                catch (Exception ex)
                {
                    reason ??= $"phép giao lỗi: {ex.Message}";
                }
            }

            if (!done)
                failures++;
        }

        return ReferenceEquals(result, solid) ? null : result;
    }

    /// <summary>Mọi hướng pháp tuyến của khối, đã gộp trùng. Mặt cong lấy theo từng tam giác của lưới
    /// xấp xỉ để chỗ cong cũng được gọt.</summary>
    private static List<XYZ> CollectNormals(Solid solid, Func<Face, bool> excluded)
    {
        var normals = new List<XYZ>();

        foreach (Face face in solid.Faces)
        {
            // Mặt user chỉ là mặt lỗ: không gọt theo hướng của nó, tức cover không ăn theo lỗ.
            if (excluded(face))
                continue;

            if (face is PlanarFace planar)
            {
                Add(normals, planar.FaceNormal);
                continue;
            }

            Mesh? mesh;
            try
            {
                mesh = face.Triangulate(0.3);
            }
            catch (Exception)
            {
                continue;
            }

            if (mesh is null)
                continue;

            for (var i = 0; i < mesh.NumTriangles && normals.Count <= MaxDirections; i++)
            {
                var triangle = mesh.get_Triangle(i);
                var cross = (triangle.get_Vertex(1) - triangle.get_Vertex(0))
                    .CrossProduct(triangle.get_Vertex(2) - triangle.get_Vertex(0));

                if (cross.GetLength() > PointTolerance)
                    Add(normals, cross.Normalize());
            }
        }

        return normals;
    }

    private static XYZ Perpendicular(XYZ direction)
    {
        var candidate = direction.CrossProduct(XYZ.BasisZ);
        if (candidate.GetLength() < PointTolerance)
            candidate = direction.CrossProduct(XYZ.BasisX);
        return candidate.Normalize();
    }

    private static List<XYZ> Distinct(IEnumerable<XYZ> source)
    {
        var result = new List<XYZ>();
        foreach (var vector in source)
            Add(result, vector);
        return result;
    }

    private static void Add(List<XYZ> normals, XYZ normal)
    {
        if (normals.Any(existing => existing.DotProduct(normal) > SameDirection))
            return;

        normals.Add(normal);
    }
}
