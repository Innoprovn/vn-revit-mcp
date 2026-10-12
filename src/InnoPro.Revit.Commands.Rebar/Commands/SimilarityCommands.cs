namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

internal static class ShapeValues
{
    public static readonly string[] Names = ["A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "O1", "O2", "O3", "O4", "O5"];

    /// <summary>Sai số coi như "giống hệt": chỉ khử nhiễu số thực của Revit.</summary>
    public const double ExactMm = 0.005;
    public const double ExactDeg = 0.0001;

    public static Dictionary<string, (double Value, bool IsAngle)> Read(Rebar rebar)
    {
        var result = new Dictionary<string, (double, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names)
        {
            var p = rebar.LookupParameter(name);
            if (p is { HasValue: true, StorageType: StorageType.Double })
            {
                var isAngle = p.Definition.GetDataType() == SpecTypeId.Angle;
                result[name] = (isAngle ? p.AsDouble() * 180 / Math.PI : UnitConvert.FeetToMm(p.AsDouble()), isAngle);
            }
        }
        return result;
    }

    public static double CenterlineMm(Rebar rebar)
    {
        try
        {
            return UnitConvert.FeetToMm(rebar.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0).Sum(c => c.Length));
        }
        catch (Exception)
        {
            return 0;
        }
    }
}

public sealed class RebarSimilarityReview : RevitCommandBase
{
    public override string Name => "rebar_similarity_review";
    public override string Category => "Rebar";
    public override string Description =>
        "Tìm thép gần giống nhau (lệch kích thước A-H nhỏ, dễ là lỗi mô hình) và thép giống hệt nhưng khác Rebar Number trong cùng partition.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép hoặc host; trống = toàn mô hình." },
            "tolerance_mm": { "type": "number", "description": "Ngưỡng gần giống, mặc định 2." },
            "mark_parameter": { "type": "string", "description": "Tham số mark để kiểm tra cùng Rebar Number phải cùng mark." },
            "limit": { "type": "integer", "description": "Mặc định 200." }
          }
        }
        """;

    private sealed record Sample(Rebar Rebar, string Partition, string TypeName, string Shape, string Family,
        Dictionary<string, (double Value, bool IsAngle)> Values, double CenterlineMm, string Number, string Mark);

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        var tolerance = ArgsX.Double(arguments, "tolerance_mm") ?? 2.0;
        var markParameter = ArgsX.String(arguments, "mark_parameter");
        var scope = RebarScope.Resolve(context, arguments, "model");

        var samples = scope.ShapeRebars.Select(r =>
        {
            ElementId shapeId;
            try { shapeId = r.GetShapeId(); }
            catch (Exception) { shapeId = ElementId.InvalidElementId; }
            var partition = RebarInfo.Partition(r);
            var family = string.Join("|", partition, r.GetTypeId().Value, shapeId.Value, r.GetHookTypeId(0).Value, r.GetHookTypeId(1).Value);
            return new Sample(r, partition, RebarInfo.BarType(doc, r)?.Name ?? "", RebarInfo.ShapeName(doc, r), family,
                ShapeValues.Read(r), ShapeValues.CenterlineMm(r), RebarInfo.RebarNumber(r),
                markParameter == null ? "" : RebarInfo.ParameterText(doc, r, markParameter));
        }).ToList();

        var issues = new List<object>();
        void Add(string kind, List<Sample> a, List<Sample> b, string difference) => issues.Add(new
        {
            kind,
            partition = a[0].Partition,
            bar_type = a[0].TypeName,
            shape = a[0].Shape,
            ids_a = a.Select(s => s.Rebar.Id.Value).ToList(),
            ids_b = b.Select(s => s.Rebar.Id.Value).ToList(),
            difference,
        });

        foreach (var family in samples.GroupBy(s => s.Family))
        {
            var variants = GroupExact(family);
            for (var i = 0; i < variants.Count - 1; i++)
                for (var j = i + 1; j < variants.Count; j++)
                    if (Within(variants[i][0], variants[j][0], tolerance, 1.0))
                        Add("gần giống", variants[i], variants[j], Describe(variants[i][0], variants[j][0]));
        }

        foreach (var partition in samples.GroupBy(s => s.Partition, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var group in GroupExact(partition))
            {
                var byNumber = group.GroupBy(s => s.Number).Select(g => g.ToList()).ToList();
                for (var i = 0; i < byNumber.Count - 1; i++)
                    for (var j = i + 1; j < byNumber.Count; j++)
                        Add("khác số Revit", byNumber[i], byNumber[j],
                            $"Hình học giống hệt nhưng Rebar Number {byNumber[i][0].Number} ≠ {byNumber[j][0].Number}: kiểm tra hook, góc xoay hook, bend radius.");
            }

            if (markParameter == null) continue;
            foreach (var sameNumber in partition.Where(s => s.Number.Length > 0).GroupBy(s => s.Number))
            {
                var byMark = sameNumber.GroupBy(s => s.Mark, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
                for (var i = 0; i < byMark.Count - 1; i++)
                    for (var j = i + 1; j < byMark.Count; j++)
                        Add("lệch mark", byMark[i], byMark[j],
                            $"Cùng Rebar Number {sameNumber.Key} nhưng {markParameter} = '{byMark[i][0].Mark}' ≠ '{byMark[j][0].Mark}'.");
            }
        }

        var limit = (int)Args.GetLongOrDefault(arguments, "limit", 200);
        return new
        {
            scope = scope.Scope,
            rebars = samples.Count,
            issues = issues.Count,
            items = Table.Of(issues, limit),
            hint = issues.Count > 0 ? "Sửa cặp 'gần giống' bằng rebar_copy_shape_dimensions (source = một thanh nhóm A, đích = nhóm B)." : null,
        };
    }

    private static List<List<Sample>> GroupExact(IEnumerable<Sample> samples)
    {
        var groups = new List<List<Sample>>();
        foreach (var s in samples)
        {
            var group = groups.FirstOrDefault(g => g[0].Family.Split('|').Skip(1).SequenceEqual(s.Family.Split('|').Skip(1))
                                                   && Within(g[0], s, ShapeValues.ExactMm, ShapeValues.ExactDeg));
            if (group == null) groups.Add([s]);
            else group.Add(s);
        }
        return groups;
    }

    private static bool Within(Sample a, Sample b, double mm, double deg)
    {
        if (a.Values.Count != b.Values.Count) return false;
        foreach (var (name, (value, isAngle)) in a.Values)
        {
            if (!b.Values.TryGetValue(name, out var other)) return false;
            if (Math.Abs(value - other.Value) > (isAngle ? deg : mm)) return false;
        }
        return Math.Abs(a.CenterlineMm - b.CenterlineMm) <= mm;
    }

    private static string Describe(Sample a, Sample b)
    {
        var parts = a.Values
            .Where(kv => b.Values.TryGetValue(kv.Key, out var o) && Math.Abs(kv.Value.Value - o.Value) > (kv.Value.IsAngle ? ShapeValues.ExactDeg : ShapeValues.ExactMm))
            .Select(kv => $"{kv.Key}: {kv.Value.Value:0.###} ↔ {b.Values[kv.Key].Value:0.###}{(kv.Value.IsAngle ? "°" : " mm")}")
            .ToList();
        if (Math.Abs(a.CenterlineMm - b.CenterlineMm) > ShapeValues.ExactMm)
            parts.Add($"Chiều dài: {a.CenterlineMm:0.###} ↔ {b.CenterlineMm:0.###} mm");
        return parts.Count == 0 ? "Khác dữ liệu hình học nội bộ" : string.Join("; ", parts);
    }
}

public sealed class RebarCopyShapeDimensions : RevitCommandBase
{
    public override string Name => "rebar_copy_shape_dimensions";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override string Description =>
        "Chép kích thước đoạn (A-H, O1-O5) từ một thanh mẫu sang các thanh cùng shape; thanh nào không khớp sau khi chép thì hủy cả lệnh.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "source_id": { "type": "integer", "description": "Thanh mẫu." },
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thanh cần sửa." }
          },
          "required": ["source_id", "element_ids"]
        }
        """;

    public override object? PromptArguments(RevitCommandContext context)
    {
        var source = context.Ui.PickElement("Chọn thanh thép MẪU", BuiltInCategory.OST_Rebar);
        var targets = UiHelpers.Pick(() => context.UIDocument.Selection.PickObjects(
            Autodesk.Revit.UI.Selection.ObjectType.Element, "Chọn các thanh cần chép kích thước theo mẫu (Finish để xong)"));
        return new { source_id = source.Id.Value, element_ids = targets.Select(r => r.ElementId.Value).ToList() };
    }

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var doc = context.Document;
        if (doc.GetElement(new ElementId(Args.GetLong(arguments, "source_id"))) is not Rebar source)
            throw new CommandException("source_id không phải Rebar.");
        var targets = Args.GetElementIds(arguments, "element_ids").Select(doc.GetElement).OfType<Rebar>().Where(r => r.Id != source.Id).ToList();
        if (targets.Count == 0) throw new CommandException("Không có thanh Rebar đích nào.");

        var written = 0;
        foreach (var target in targets)
        {
            foreach (var name in ShapeValues.Names)
            {
                var from = source.LookupParameter(name);
                var to = target.LookupParameter(name);
                if (from is not { HasValue: true, StorageType: StorageType.Double } || to is not { IsReadOnly: false, StorageType: StorageType.Double })
                    continue;
                if (Math.Abs(from.AsDouble() - to.AsDouble()) < 1e-10) continue;
                to.Set(from.AsDouble());
                written++;
            }
        }
        doc.Regenerate();

        var expected = ShapeValues.Read(source);
        var mismatched = targets.Where(t =>
        {
            var values = ShapeValues.Read(t);
            return expected.Any(kv => !values.TryGetValue(kv.Key, out var v) || Math.Abs(v.Value - kv.Value.Value) > 0.01);
        }).Select(t => t.Id.Value).ToList();
        if (mismatched.Count > 0)
            throw new CommandException($"Không đồng bộ được {mismatched.Count}/{targets.Count} thanh (kích thước bị ràng buộc, hook hoặc khác shape): {string.Join(", ", mismatched.Take(20))}. Đã hủy, mô hình không đổi.");

        return new { source_id = source.Id.Value, updated = targets.Count, values_written = written };
    }
}
