using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace InnoPro.Mcp.Server.Knowledge;

/// <summary>
/// Chỉ mục Revit API lập ngay trên máy người dùng từ bản Revit đã cài (RevitAPI.dll, RevitAPIUI.dll + XML doc đi kèm):
/// không phân phối tài liệu của Autodesk, luôn khớp đúng phiên bản trên máy. Đọc DLL bằng MetadataLoadContext (không nạp Revit).
/// Mỗi dòng: kind \t docId \t chữ ký gọn \t năm có mặt \t mô tả. kind: T kiểu, C constructor, M method, P property, F field/enum, E event.
/// </summary>
public static class RevitApiIndex
{
    private static readonly string[] Assemblies = ["RevitAPI", "RevitAPIUI"];

    /// <summary>Thư mục Revit 2025+ có RevitAPI.dll trên máy.</summary>
    public static IReadOnlyList<string> InstalledRevitDirs() =>
        Enumerable.Range(2025, 6)
            .Select(y => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", $"Revit {y}"))
            .Where(d => File.Exists(Path.Combine(d, "RevitAPI.dll")))
            .ToList();

    public static string CacheFile(IReadOnlyList<string> revitDirs) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp", "cache",
        "revit-api-" + string.Join("-", revitDirs.Select(d => Regex.Match(d, @"20\d\d").Value)) + ".tsv.gz");

    /// <summary>Lập chỉ mục từ các thư mục Revit, ghi file .tsv.gz; trả số mục.</summary>
    public static int Build(string output, IEnumerable<string> revitDirs, Action<string>? log = null)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var revitDir in revitDirs)
        {
            var year = Regex.Match(revitDir, @"20\d\d").Value;
            var paths = Directory.GetFiles(revitDir, "*.dll")
                .Concat(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
                .Concat(DesktopRuntime().SelectMany(d => Directory.GetFiles(d, "*.dll")))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
            using var context = new MetadataLoadContext(new PathAssemblyResolver(paths));

            foreach (var name in Assemblies)
            {
                var dll = Path.Combine(revitDir, name + ".dll");
                if (!File.Exists(dll)) continue;
                Type[] types;
                try { types = context.LoadFromAssemblyPath(dll).GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                foreach (var type in types.Where(t => t.IsPublic || t.IsNestedPublic))
                {
                    try { Index(entries, type, year); }
                    catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException) { }
                }

                var xml = Path.Combine(revitDir, name + ".xml");
                if (File.Exists(xml)) LoadDocs(docs, xml);
            }
            log?.Invoke($"Revit {year}: {entries.Count} mục");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + ".tmp";
        using (var file = File.Create(temp))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            foreach (var e in entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                docs.TryGetValue((e.Kind == "C" ? "M" : e.Kind) + ":" + e.Id, out var summary);
                writer.Write($"{e.Kind}\t{e.Id}\t{e.Signature}\t{string.Join(",", e.Years)}\t{summary}\n");
            }
        }
        File.Move(temp, output, overwrite: true);
        return entries.Count;
    }

    private static void Add(Dictionary<string, Entry> entries, string kind, string id, string signature, string year)
    {
        if (!entries.TryGetValue(id, out var e)) entries[id] = e = new Entry(kind, id, signature);
        else e.Signature = signature; // bản mới nhất thắng
        e.Years.Add(year);
    }

    private static void Index(Dictionary<string, Entry> entries, Type type, string year)
    {
        var typeId = DocName(type);
        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class" : "class";
        var baseName = type.BaseType is { } b && b.FullName is not ("System.Object" or "System.Enum" or "System.ValueType") ? " : " + Short(b) : "";
        Add(entries, "T", typeId, $"{kind} {Short(type)}{baseName}", year);

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var c in type.GetConstructors(flags))
            Add(entries, "C", $"{typeId}.#ctor{Params(c.GetParameters())}", $"{Short(type)}({ParamList(c.GetParameters())})", year);
        foreach (var m in type.GetMethods(flags).Where(m => !m.IsSpecialName))
            Add(entries, "M", $"{typeId}.{m.Name}{(m.IsGenericMethod ? "``" + m.GetGenericArguments().Length : "")}{Params(m.GetParameters())}",
                $"{(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({ParamList(m.GetParameters())})", year);
        foreach (var p in type.GetProperties(flags))
        {
            var index = p.GetIndexParameters();
            var access = (p.CanRead ? "get;" : "") + (p.CanWrite && p.SetMethod is { IsPublic: true } ? " set;" : "");
            Add(entries, "P", $"{typeId}.{p.Name}{Params(index)}",
                $"{Short(p.PropertyType)} {p.Name}{(index.Length > 0 ? "[" + ParamList(index) + "]" : "")} {{ {access.Trim()} }}", year);
        }
        foreach (var f in type.GetFields(flags))
            Add(entries, "F", $"{typeId}.{f.Name}", type.IsEnum ? f.Name : $"{(f.IsStatic ? "static " : "")}{Short(f.FieldType)} {f.Name}", year);
        foreach (var ev in type.GetEvents(flags))
            Add(entries, "E", $"{typeId}.{ev.Name}", $"event {Short(ev.EventHandlerType!)} {ev.Name}", year);
    }

    // Assembly WPF/WinForms (vài kiểu Revit UI kế thừa từ đó): Microsoft.WindowsDesktop.App mới nhất.
    private static IEnumerable<string> DesktopRuntime()
    {
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)!)!)!;
        var desktop = Path.Combine(shared, "Microsoft.WindowsDesktop.App");
        return Directory.Exists(desktop) ? Directory.GetDirectories(desktop).OrderByDescending(d => d).Take(1) : [];
    }

    // Mã tài liệu chuẩn C# (khớp với file XML doc).
    private static string DocName(Type t)
    {
        if (t.IsByRef) return DocName(t.GetElementType()!) + "@";
        if (t.IsPointer) return DocName(t.GetElementType()!) + "*";
        if (t.IsArray) return DocName(t.GetElementType()!) + (t.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", t.GetArrayRank())) + "]");
        if (t.IsGenericParameter) return (t.DeclaringMethod != null ? "``" : "`") + t.GenericParameterPosition;
        var name = t.IsNested ? DocName(t.DeclaringType!) + "." + t.Name : (t.Namespace is { Length: > 0 } ns ? ns + "." : "") + t.Name;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            name = name[..name.IndexOf('`')] + "{" + string.Join(",", t.GetGenericArguments().Select(DocName)) + "}";
        return name;
    }

    private static string Params(ParameterInfo[] ps) => ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(p => DocName(p.ParameterType))) + ")";

    private static string ParamList(ParameterInfo[] ps) => string.Join(", ", ps.Select(p =>
        (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + Short(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name));

    // Tên gọn: bỏ namespace, generic dạng List<XYZ>.
    private static string Short(Type t)
    {
        if (t.IsByRef) return Short(t.GetElementType()!);
        if (t.IsArray) return Short(t.GetElementType()!) + "[]";
        var name = t.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        name = name switch
        {
            "Void" => "void", "Boolean" => "bool", "Int32" => "int", "Int64" => "long", "Double" => "double", "String" => "string",
            "Object" => "object", "Single" => "float", "Byte" => "byte", "Int16" => "short", "UInt32" => "uint", _ => name,
        };
        if (t.IsGenericType) name += "<" + string.Join(", ", t.GetGenericArguments().Select(Short)) + ">";
        return t.IsNested && !t.IsGenericParameter ? Short(t.DeclaringType!) + "." + name : name;
    }

    private static void LoadDocs(Dictionary<string, string> docs, string path)
    {
        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            if ((string?)member.Attribute("name") is not { } id) continue;
            var summary = Text(member.Element("summary"));
            var remarks = Text(member.Element("remarks"));
            var text = remarks.Length > 0 ? $"{summary} | {Cut(remarks, 500)}" : summary;
            if (text.Length > 0) docs[id] = Cut(text, 900);
        }
    }

    private static string Text(XElement? element)
    {
        if (element == null) return "";
        var sb = new StringBuilder();
        foreach (var node in element.DescendantNodes())
        {
            if (node is XText t && t.Parent is not { Name.LocalName: "see" or "seealso" }) sb.Append(t.Value);
            else if (node is XElement { Name.LocalName: "see" or "seealso" } see)
            {
                var cref = (string?)see.Attribute("cref") ?? (string?)see.Attribute("langword") ?? "";
                sb.Append(see.Value.Length > 0 ? see.Value : Regex.Replace(cref, @"^[A-Z]:|\(.*$", "").Split('.').Last());
            }
            else if (node is XElement { Name.LocalName: "para" }) sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed class Entry(string kind, string id, string signature)
    {
        public string Kind { get; } = kind;
        public string Id { get; } = id;
        public string Signature { get; set; } = signature;
        public SortedSet<string> Years { get; } = new(StringComparer.Ordinal);
    }
}
