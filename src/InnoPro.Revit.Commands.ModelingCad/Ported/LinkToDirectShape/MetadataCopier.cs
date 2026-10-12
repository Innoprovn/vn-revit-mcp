using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.LinkToDirectShape;

/// <summary>Chép metadata của element trong link sang DirectShape.
///
/// DirectShape (Generic Models) chỉ có sẵn vài parameter built-in nên muốn giữ dữ liệu của cấu kiện
/// gốc thì phải TẠO parameter trong file host. Cách làm:
///   1. Gom "schema": duyệt 1 element đại diện cho mỗi cặp (category, type) để biết có những
///      parameter tên gì, kiểu gì — không duyệt hết mọi element vì cùng type thì cùng bộ parameter.
///   2. Tạo shared parameter (file shared param tạm trong %TEMP%) rồi bind Instance vào category
///      Generic Models. GUID sinh xác định (hash từ tên) nên chạy lại/máy khác vẫn ra đúng 1
///      parameter, không đẻ trùng.
///   3. Ghi giá trị theo từng element.
///
/// Kiểu dữ liệu giữ nguyên theo parameter gốc (Length/Area/Volume/YesNo/Integer/Text) để bảng
/// schedule còn cộng/lọc được; parameter kiểu ElementId đổi thành Text (tên của element được trỏ).
/// Tên parameter có tiền tố (mặc định LNK_) để không đụng parameter thật của dự án.
/// </summary>
public sealed class MetadataCopier
{
    private const string DefinitionGroupName = "BIMTech LinkToDirectShape";
    private const string SharedParameterFileName = "BIMTech_LinkToDirectShape_SharedParams.txt";

    private static readonly string SharedParameterHeader =
        "# This is a Revit shared parameter file.\n" +
        "# Do not edit manually.\n" +
        "*META\tVERSION\tMINVERSION\n" +
        "META\t2\t1\n" +
        "*GROUP\tID\tNAME\n" +
        "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\n";

    /// <summary>Thông tin nhận dạng luôn ghi (kể cả khi chỉ chọn mức "nhận dạng").</summary>
    private static readonly string[] IdentityFields =
    {
        "Source Link", "Source Id", "Source UniqueId", "Source Category",
        "Source Family", "Source Type", "Source Level", "Source Workset", "Source Material"
    };

    private readonly Document _doc;
    private readonly ConvertOptions _options;
    private readonly string _prefix;

    /// <summary>Tên parameter đầy đủ (đã có tiền tố) -> kiểu dữ liệu. Tên trùng thì giữ kiểu gặp
    /// đầu tiên; element sau có cùng tên nhưng khác kiểu sẽ được ghi ép về kiểu đó hoặc bỏ qua.</summary>
    private readonly Dictionary<string, ForgeTypeId> _schema = new(StringComparer.Ordinal);

    /// <summary>Cặp (category, type) đã lấy mẫu — tránh duyệt lại parameter của mọi element.</summary>
    private readonly HashSet<string> _sampled = new(StringComparer.Ordinal);

    private readonly HashSet<string> _bound = new(StringComparer.Ordinal);

    public MetadataCopier(Document doc, ConvertOptions options)
    {
        _doc = doc;
        _options = options;
        _prefix = options.ParameterPrefix ?? "";
    }

    public int ParametersCreated { get; private set; }

    /// <summary>Tổng số parameter dùng được trên DirectShape (gồm cả cái đã có sẵn từ lần chạy trước).</summary>
    public int ParametersReady => _bound.Count;

    public int ValuesWritten { get; private set; }

    public string? LastError { get; private set; }

    public bool Enabled => _options.Metadata != MetadataScope.None;

    // ===================== 1. Gom schema =====================

    public void Sample(Element source)
    {
        if (!Enabled)
            return;

        foreach (var field in IdentityFields)
            Register(_prefix + field, SpecTypeId.String.Text);

        if (_options.Metadata != MetadataScope.All)
            return;

        var key = $"{source.Category?.Id.Value ?? 0}#{source.GetTypeId().Value}";
        if (!_sampled.Add(key))
            return;

        foreach (Parameter parameter in source.Parameters)
            RegisterFrom(parameter);

        if (_options.IncludeTypeParameters &&
            source.Document.GetElement(source.GetTypeId()) is { } type)
        {
            foreach (Parameter parameter in type.Parameters)
                RegisterFrom(parameter);
        }
    }

    private void RegisterFrom(Parameter parameter)
    {
        var definition = parameter.Definition;
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name))
            return;
        if (parameter.StorageType == StorageType.None)
            return;

        Register(_prefix + definition.Name, DataTypeOf(parameter, definition));
    }

    private void Register(string fullName, ForgeTypeId dataType)
    {
        if (!_schema.ContainsKey(fullName))
            _schema[fullName] = dataType;
    }

    /// <summary>Kiểu để tạo shared parameter. Giữ spec gốc cho số (Length, Area, Volume...) để giá
    /// trị vẫn là số có đơn vị; ElementId thì chuyển sang Text vì id của link vô nghĩa ở host.</summary>
    private static ForgeTypeId DataTypeOf(Parameter parameter, Definition definition)
    {
        if (parameter.StorageType is StorageType.String or StorageType.ElementId)
            return SpecTypeId.String.Text;

        try
        {
            var dataType = definition.GetDataType();
            if (dataType is not null && SpecUtils.IsSpec(dataType))
                return dataType;
        }
        catch (Exception)
        {
            // Definition lạ (vd parameter nội bộ) — dùng kiểu mặc định bên dưới.
        }

        return parameter.StorageType == StorageType.Integer
            ? SpecTypeId.Int.Integer
            : SpecTypeId.Number;
    }

    // ===================== 2. Tạo + bind shared parameter =====================

    /// <summary>Phải gọi TRONG transaction, trước khi tạo DirectShape. Tự Regenerate() để parameter
    /// vừa bind có hiệu lực ngay cho element tạo sau đó.</summary>
    public void EnsureParameters()
    {
        if (!Enabled || _schema.Count == 0)
            return;

        var app = _doc.Application;
        var originalFile = app.SharedParametersFilename;

        try
        {
            var existing = ExistingBindings();

            var path = Path.Combine(Path.GetTempPath(), SharedParameterFileName);
            if (!File.Exists(path))
                File.WriteAllText(path, SharedParameterHeader, new UTF8Encoding(false));

            app.SharedParametersFilename = path;
            var definitionFile = app.OpenSharedParameterFile();
            if (definitionFile is null)
            {
                LastError = "Không mở được file shared parameter tạm.";
                return;
            }

            var group = definitionFile.Groups.get_Item(DefinitionGroupName)
                        ?? definitionFile.Groups.Create(DefinitionGroupName);

            var genericModels = Category.GetCategory(_doc, BuiltInCategory.OST_GenericModel);
            if (genericModels is null)
            {
                LastError = "Không lấy được category Generic Models.";
                return;
            }

            foreach (var (fullName, dataType) in _schema)
            {
                try
                {
                    if (existing.TryGetValue(fullName, out var bound))
                    {
                        // Parameter cùng tên đã có trong dự án: chỉ cần bảo đảm nó phủ Generic Models.
                        if (EnsureCategory(bound, genericModels))
                            _bound.Add(fullName);
                        continue;
                    }

                    var definition = group.Definitions.get_Item(fullName) as ExternalDefinition
                                     ?? CreateDefinition(group, fullName, dataType);
                    if (definition is null)
                        continue;

                    var categories = app.Create.NewCategorySet();
                    categories.Insert(genericModels);
                    var binding = app.Create.NewInstanceBinding(categories);

                    if (_doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Data))
                    {
                        _bound.Add(fullName);
                        ParametersCreated++;
                    }
                }
                catch (Exception ex)
                {
                    LastError ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            LastError ??= ex.Message;
        }
        finally
        {
            try
            {
                app.SharedParametersFilename = originalFile;
            }
            catch (Exception)
            {
                // Đường dẫn cũ có thể không còn hợp lệ — không phải lỗi của lần convert này.
            }
        }

        _doc.Regenerate();
    }

    private static ExternalDefinition? CreateDefinition(
        DefinitionGroup group, string fullName, ForgeTypeId dataType)
    {
        var options = new ExternalDefinitionCreationOptions(fullName, dataType)
        {
            GUID = DeterministicGuid(fullName),
            Visible = true,
            UserModifiable = true,
            Description = "Metadata chép từ Revit link bởi tool Link sang DirectShape."
        };

        return group.Definitions.Create(options) as ExternalDefinition;
    }

    /// <summary>GUID sinh từ tên nên mọi file/máy đều ra cùng 1 shared parameter — chạy lại tool
    /// không đẻ thêm parameter trùng tên.</summary>
    private static Guid DeterministicGuid(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes("BIMTech.LinkToDirectShape|" + name));
        return new Guid(bytes);
    }

    private Dictionary<string, (Definition Definition, Binding Binding)> ExistingBindings()
    {
        var result = new Dictionary<string, (Definition, Binding)>(StringComparer.Ordinal);
        var iterator = _doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is Definition definition && iterator.Current is Binding binding)
                result[definition.Name] = (definition, binding);
        }

        return result;
    }

    /// <summary>Parameter đã có sẵn trong dự án nhưng chưa bind Generic Models thì bổ sung category.</summary>
    private bool EnsureCategory((Definition Definition, Binding Binding) bound, Category category)
    {
        if (bound.Binding is not ElementBinding elementBinding)
            return false;

        if (elementBinding.Categories.Contains(category))
            return true;

        elementBinding.Categories.Insert(category);
        return _doc.ParameterBindings.ReInsert(bound.Definition, elementBinding, GroupTypeId.Data);
    }

    // ===================== 3. Ghi giá trị =====================

    public void Apply(
        DirectShape target, RevitLinkInstance link, Document linkDoc, Element source,
        string? materialName)
    {
        if (!Enabled || _bound.Count == 0)
            return;

        var slots = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (Parameter parameter in target.Parameters)
        {
            var name = parameter.Definition?.Name;
            if (name is not null && _bound.Contains(name))
                slots[name] = parameter;
        }

        if (slots.Count == 0)
            return;

        foreach (var (field, value) in IdentityValues(link, linkDoc, source, materialName))
            WriteText(slots, _prefix + field, value);

        if (_options.Metadata != MetadataScope.All)
            return;

        foreach (Parameter parameter in source.Parameters)
            WriteFrom(slots, parameter);

        if (_options.IncludeTypeParameters &&
            source.Document.GetElement(source.GetTypeId()) is { } type)
        {
            foreach (Parameter parameter in type.Parameters)
                WriteFrom(slots, parameter);
        }
    }

    private IEnumerable<(string Field, string? Value)> IdentityValues(
        RevitLinkInstance link, Document linkDoc, Element source, string? materialName)
    {
        var type = source.Document.GetElement(source.GetTypeId()) as ElementType;

        yield return ("Source Link", LinkScanner.DescribeLink(link, linkDoc));
        yield return ("Source Id", source.Id.Value.ToString());
        yield return ("Source UniqueId", source.UniqueId);
        yield return ("Source Category", source.Category?.Name);
        yield return ("Source Family", type?.FamilyName);
        yield return ("Source Type", type?.Name);
        yield return ("Source Level", LevelName(source));
        yield return ("Source Workset", WorksetName(source));
        yield return ("Source Material", materialName);
    }

    private static string? LevelName(Element source)
    {
        var levelId = source.LevelId;
        if (levelId is not null && levelId != ElementId.InvalidElementId)
            return source.Document.GetElement(levelId)?.Name;

        var parameter = source.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                        ?? source.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
        return parameter is null ? null : ElementName(parameter);
    }

    private static string? WorksetName(Element source)
    {
        var document = source.Document;
        if (!document.IsWorkshared)
            return null;

        try
        {
            return document.GetWorksetTable().GetWorkset(source.WorksetId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WriteFrom(IReadOnlyDictionary<string, Parameter> slots, Parameter source)
    {
        var name = source.Definition?.Name;
        if (name is null || !slots.TryGetValue(_prefix + name, out var target))
            return;
        if (target.IsReadOnly || !source.HasValue)
            return;

        try
        {
            switch (target.StorageType)
            {
                case StorageType.Double when source.StorageType == StorageType.Double:
                    if (target.Set(source.AsDouble()))
                        ValuesWritten++;
                    break;

                case StorageType.Integer when source.StorageType == StorageType.Integer:
                    if (target.Set(source.AsInteger()))
                        ValuesWritten++;
                    break;

                case StorageType.String:
                    var text = AsText(source);
                    if (!string.IsNullOrWhiteSpace(text) && target.Set(text))
                        ValuesWritten++;
                    break;
            }
        }
        catch (Exception)
        {
            // Kiểu không khớp (parameter cùng tên nhưng khác kiểu ở dự án này) — bỏ qua ô đó.
        }
    }

    private void WriteText(IReadOnlyDictionary<string, Parameter> slots, string fullName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!slots.TryGetValue(fullName, out var target) || target.IsReadOnly)
            return;
        if (target.StorageType != StorageType.String)
            return;

        try
        {
            if (target.Set(value))
                ValuesWritten++;
        }
        catch (Exception)
        {
            // Parameter bị khoá — bỏ qua.
        }
    }

    /// <summary>Giá trị dạng chữ, ưu tiên AsValueString() để giữ đúng đơn vị đang hiển thị.</summary>
    private static string? AsText(Parameter parameter) => parameter.StorageType switch
    {
        StorageType.String => parameter.AsString(),
        StorageType.Integer => parameter.AsValueString() ?? parameter.AsInteger().ToString(),
        StorageType.Double => parameter.AsValueString() ?? parameter.AsDouble().ToString("0.####"),
        StorageType.ElementId => ElementName(parameter),
        _ => null
    };

    private static string? ElementName(Parameter parameter)
    {
        var id = parameter.AsElementId();
        if (id is null || id == ElementId.InvalidElementId)
            return null;

        return parameter.Element?.Document.GetElement(id)?.Name ?? id.Value.ToString();
    }
}
