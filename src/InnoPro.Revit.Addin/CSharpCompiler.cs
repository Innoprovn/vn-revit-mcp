using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Biên dịch mã C# trong Revit bằng Roslyn. Kết quả được lưu đệm theo nội dung mã,
/// nên lần mở Revit sau không phải biên dịch lại (và không phải nạp Roslyn).
/// </summary>
internal static class CSharpCompiler
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    private static string CacheDir => Path.Combine(Paths.DataDir, "cache");

    /// <summary>Biên dịch thành byte DLL. Lỗi biên dịch trả về trong errors (đã đổi số dòng theo file gốc).</summary>
    public static byte[]? Compile(string assemblyName, string source, string sourcePath, out List<string> errors)
    {
        var path = CompileToFile(assemblyName, source, sourcePath, out errors);
        return path == null ? null : File.ReadAllBytes(path);
    }

    /// <summary>Như Compile nhưng trả đường dẫn file DLL trong thư mục đệm (Revit cần file thật cho nút ribbon).</summary>
    public static string? CompileToFile(string assemblyName, string source, string sourcePath, out List<string> errors)
    {
        errors = [];
        var cacheFile = Path.Combine(CacheDir, Hash(assemblyName + "\n" + source) + ".dll");
        if (File.Exists(cacheFile)) return cacheFile;

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), sourcePath, Encoding.UTF8);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                platform: Platform.X64));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(10)
                .Select(d => $"dòng {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
                .ToList();
            return null;
        }

        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(cacheFile, stream.ToArray());
        return cacheFile;
    }

    // Đổi phiên bản add-in thì đệm cũ không còn dùng (SDK có thể đã đổi).
    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(App.Version + "\n" + source)))[..32];

    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
                    || name is "mscorlib.dll" or "netstandard.dll")
                    paths.Add(path);
            }
        }
        else
        {
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            foreach (var path in Directory.GetFiles(runtime, "System.*.dll")) paths.Add(path);
            paths.Add(Path.Combine(runtime, "netstandard.dll"));
        }

        paths.Add(typeof(object).Assembly.Location);
        paths.Add(typeof(Document).Assembly.Location);
        paths.Add(typeof(UIApplication).Assembly.Location);
        paths.Add(typeof(IRevitCommand).Assembly.Location);
        paths.Add(typeof(BridgeProtocol).Assembly.Location);

        var references = new List<MetadataReference>();
        foreach (var path in paths.Where(File.Exists))
        {
            try { references.Add(MetadataReference.CreateFromFile(path)); }
            catch (BadImageFormatException) { } // DLL native trong thư mục runtime
        }
        return references;
    }
}
