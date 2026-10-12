using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Revit.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace InnoPro.Revit.Commands.Code;

internal delegate object? ScriptEntry(Document doc, UIDocument uidoc, UIApplication uiapp, Action<object?> print);

/// <summary>
/// Biên dịch đoạn mã của AI thành thân một phương thức, nạp vào AssemblyLoadContext thu hồi được
/// để chạy xong là giải phóng.
/// </summary>
internal static class ScriptCompiler
{
    private const string Prefix = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Text;
        using Autodesk.Revit.DB;
        using Autodesk.Revit.DB.Architecture;
        using Autodesk.Revit.DB.Structure;
        using Autodesk.Revit.UI;

        public static class __InnoProScript
        {
            public static object __Run(Document doc, UIDocument uidoc, UIApplication uiapp, Action<object> print)
            {
        #line 1
        """;

    private const string Suffix = """

        #line hidden
                return null;
            }
        }
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    public static (ScriptEntry Entry, AssemblyLoadContext Context) Compile(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(Prefix + "\n" + code + Suffix,
            new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "InnoProScript_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(10)
                .Select(d => $"dòng {d.Location.GetMappedLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}");
            throw new CommandException("Mã không biên dịch được:\n" + string.Join("\n", errors));
        }

        stream.Position = 0;
        var context = new AssemblyLoadContext("InnoProScript", isCollectible: true);
        var assembly = context.LoadFromStream(stream);
        var method = assembly.GetType("__InnoProScript")!.GetMethod("__Run")!;
        return ((ScriptEntry)Delegate.CreateDelegate(typeof(ScriptEntry), method), context);
    }

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
            paths.Add(Path.Combine(runtime, "mscorlib.dll"));
        }

        paths.Add(typeof(object).Assembly.Location);
        paths.Add(typeof(Document).Assembly.Location);
        paths.Add(typeof(UIApplication).Assembly.Location);
        paths.Add(typeof(IRevitCommand).Assembly.Location);

        var references = new List<MetadataReference>();
        foreach (var path in paths.Where(File.Exists))
        {
            try { references.Add(MetadataReference.CreateFromFile(path)); }
            catch (BadImageFormatException) { } // DLL native trong thư mục runtime
        }
        return references;
    }
}
