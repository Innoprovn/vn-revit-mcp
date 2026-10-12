using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>
/// AssemblyLoadContext cho plugin: assembly dùng chung với host (SDK, Revit API, Bridge, Roslyn) lấy từ host,
/// thư viện khác nạp từ thư mục plugin (đọc từ bộ nhớ, không khóa file).
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    // Các assembly phải dùng chung với host, nếu không kiểu IRevitCommand / Document sẽ khác nhau.
    private static readonly Dictionary<string, Assembly> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        [typeof(IRevitCommand).Assembly.GetName().Name!] = typeof(IRevitCommand).Assembly,
        [typeof(Document).Assembly.GetName().Name!] = typeof(Document).Assembly,
        [typeof(UIApplication).Assembly.GetName().Name!] = typeof(UIApplication).Assembly,
        [typeof(BridgeProtocol).Assembly.GetName().Name!] = typeof(BridgeProtocol).Assembly,
        // Roslyn của host: CommandSet cần biên dịch mã (ví dụ Code) dùng chung, không chép thêm ~10 MB.
        [typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Name!] = typeof(Microsoft.CodeAnalysis.Compilation).Assembly,
        [typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Name!] = typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly,
    };

    private readonly string _dir;

    public PluginLoadContext(string name, string dir, bool collectible = true) : base($"InnoPro.{name}", collectible)
    {
        _dir = dir;
    }

    public Assembly LoadFromFileBytes(string path)
    {
        using var assembly = new MemoryStream(File.ReadAllBytes(path));
        var pdbPath = Path.ChangeExtension(path, ".pdb");
        using var pdb = File.Exists(pdbPath) ? new MemoryStream(File.ReadAllBytes(pdbPath)) : null;
        return LoadFromStream(assembly, pdb);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name != null && Shared.TryGetValue(name.Name, out var shared))
            return shared;

        var local = Path.Combine(_dir, name.Name + ".dll");
        return File.Exists(local) ? LoadFromFileBytes(local) : null;
    }
}
