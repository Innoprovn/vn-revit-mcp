using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

internal sealed record LoadedCommand(IRevitCommand Command, string CommandSet);

internal sealed record CommandSetManifest(string Name, string? Description, string? Author, string[] Assemblies);

/// <summary>
/// Nạp các CommandSet. Mỗi CommandSet là một thư mục có commandset.json, nạp vào AssemblyLoadContext
/// riêng để thư viện phụ thuộc của các bộ khác nhau không xung đột.
/// </summary>
internal sealed class CommandRegistry
{
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, LoadedCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<LoadedCommand> Commands => _commands.Values;

    public bool TryGet(string name, out LoadedCommand command) => _commands.TryGetValue(name, out command!);

    public static CommandRegistry Load(params string[] roots)
    {
        var registry = new CommandRegistry();
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var dir in Directory.GetDirectories(root))
            registry.LoadSet(dir);
        Log.Info($"Đã nạp {registry._commands.Count} lệnh.");
        return registry;
    }

    private void LoadSet(string dir)
    {
        var manifestPath = Path.Combine(dir, "commandset.json");
        if (!File.Exists(manifestPath)) return;

        try
        {
            var manifest = JsonSerializer.Deserialize<CommandSetManifest>(File.ReadAllText(manifestPath), ManifestJson)
                           ?? throw new InvalidDataException("commandset.json rỗng");
            var context = new CommandSetLoadContext(manifest.Name, dir);

            foreach (var file in manifest.Assemblies)
            {
                var assembly = context.LoadFromAssemblyPath(Path.Combine(dir, file));
                foreach (var type in CommandTypes(assembly))
                    Register((IRevitCommand)Activator.CreateInstance(type)!, manifest.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Không nạp được CommandSet ở {dir}", ex);
        }
    }

    private static IEnumerable<Type> CommandTypes(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        return types.OfType<Type>().Where(t =>
            t is { IsAbstract: false, IsInterface: false }
            && typeof(IRevitCommand).IsAssignableFrom(t)
            && t.GetConstructor(Type.EmptyTypes) != null);
    }

    private void Register(IRevitCommand command, string commandSet)
    {
        if (_commands.TryGetValue(command.Name, out var existing))
        {
            Log.Error($"Bỏ qua lệnh '{command.Name}' của {commandSet}: trùng tên với lệnh của {existing.CommandSet}.");
            return;
        }
        _commands[command.Name] = new LoadedCommand(command, commandSet);
    }

    private sealed class CommandSetLoadContext : AssemblyLoadContext
    {
        // Các assembly phải dùng chung với host, nếu không kiểu IRevitCommand / Document sẽ khác nhau.
        private static readonly Dictionary<string, Assembly> Shared = new(StringComparer.OrdinalIgnoreCase)
        {
            [typeof(IRevitCommand).Assembly.GetName().Name!] = typeof(IRevitCommand).Assembly,
            [typeof(Document).Assembly.GetName().Name!] = typeof(Document).Assembly,
            [typeof(UIApplication).Assembly.GetName().Name!] = typeof(UIApplication).Assembly,
        };

        private readonly string _dir;

        public CommandSetLoadContext(string name, string dir) : base($"InnoPro.CommandSet.{name}")
        {
            _dir = dir;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name != null && Shared.TryGetValue(name.Name, out var shared))
                return shared;

            var local = Path.Combine(_dir, name.Name + ".dll");
            return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
        }
    }
}
