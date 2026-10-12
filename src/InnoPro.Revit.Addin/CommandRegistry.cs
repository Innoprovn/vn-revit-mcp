using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

internal sealed record LoadedCommand(IRevitCommand Command, string CommandSet);

internal sealed record CommandSetManifest(string Name, string? Description, string? Author, string[] Assemblies);

/// <summary>
/// Nạp lệnh từ hai nguồn:
/// - CommandSet: thư mục có commandset.json và DLL đã build.
/// - Script: file .cs đơn lẻ trong thư mục Scripts, Revit tự biên dịch. Cách dễ nhất để viết lệnh mới.
/// Mọi thứ nạp từ bộ nhớ vào AssemblyLoadContext thu hồi được, nên không khóa file và nạp lại được khi Revit đang chạy.
/// </summary>
internal sealed class CommandRegistry
{
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, LoadedCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly List<LoadError> _errors = [];

    public IReadOnlyCollection<LoadedCommand> Commands => _commands.Values;

    public IReadOnlyList<LoadError> Errors => _errors;

    public bool TryGet(string name, out LoadedCommand command) => _commands.TryGetValue(name, out command!);

    public static CommandRegistry Load(IEnumerable<string> commandSetRoots, IEnumerable<string> scriptRoots)
    {
        var registry = new CommandRegistry();
        foreach (var root in commandSetRoots.Where(Directory.Exists))
        foreach (var dir in Directory.GetDirectories(root))
            registry.LoadSet(dir);

        foreach (var root in scriptRoots.Where(Directory.Exists))
            registry.LoadScripts(root);

        Log.Info($"Đã nạp {registry._commands.Count} lệnh, {registry._errors.Count} lỗi.");
        return registry;
    }

    /// <summary>Có file lệnh .cs nào không (để quyết định có cần nạp Roslyn hay không).</summary>
    public static bool HasScripts(IEnumerable<string> scriptRoots) =>
        scriptRoots.Where(Directory.Exists).Any(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories).Any());

    /// <summary>Giải phóng. Lệnh đang chạy dở vẫn chạy xong vì Unload là cơ chế hợp tác.</summary>
    public void Unload()
    {
        // Không xóa _commands: luồng khác có thể vẫn đang đọc bản đăng ký cũ.
        foreach (var context in _contexts) context.Unload();
    }

    private void LoadSet(string dir)
    {
        var manifestPath = Path.Combine(dir, "commandset.json");
        if (!File.Exists(manifestPath)) return;

        try
        {
            var manifest = JsonSerializer.Deserialize<CommandSetManifest>(File.ReadAllText(manifestPath), ManifestJson)
                           ?? throw new InvalidDataException("commandset.json rỗng");
            var context = new PluginLoadContext(manifest.Name, dir);
            _contexts.Add(context);

            foreach (var file in manifest.Assemblies)
            {
                var assembly = context.LoadFromFileBytes(Path.Combine(dir, file));
                RegisterAll(assembly, manifest.Name, file);
            }
        }
        catch (Exception ex)
        {
            AddError(dir, ex.Message);
            Log.Error($"Không nạp được CommandSet ở {dir}", ex);
        }
    }

    /// <summary>
    /// Mỗi file .cs biên dịch riêng, nên một file lỗi không làm hỏng các file khác.
    /// Thư mục con đặt tên CommandSet (Scripts\Rebar\*.cs → "Rebar"); file ngay trong Scripts thuộc "Scripts".
    /// </summary>
    private void LoadScripts(string root)
    {
        var context = new PluginLoadContext("Scripts", root);
        _contexts.Add(context);

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var relative = Path.GetRelativePath(root, file);
            var folder = Path.GetDirectoryName(relative);
            var setName = string.IsNullOrEmpty(folder) ? "Scripts" : folder.Split(Path.DirectorySeparatorChar)[0];
            try
            {
                var bytes = CSharpCompiler.Compile("InnoProScript_" + Path.GetFileNameWithoutExtension(file),
                    File.ReadAllText(file), file, out var errors);
                if (bytes == null)
                {
                    foreach (var error in errors) AddError(relative, error);
                    continue;
                }

                using var stream = new MemoryStream(bytes);
                var count = RegisterAll(context.LoadFromStream(stream), setName, relative);
                if (count == 0) AddError(relative, "Không có lớp nào cài đặt IRevitCommand (hoặc kế thừa RevitCommandBase).");
            }
            catch (Exception ex)
            {
                AddError(relative, ex.Message);
            }
        }
    }

    private int RegisterAll(Assembly assembly, string commandSet, string source)
    {
        var count = 0;
        foreach (var type in CommandTypes(assembly))
        {
            try
            {
                Register((IRevitCommand)Activator.CreateInstance(type)!, commandSet, source);
                count++;
            }
            catch (Exception ex)
            {
                AddError($"{source}: {type.Name}", (ex.InnerException ?? ex).Message);
            }
        }
        return count;
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

    private void Register(IRevitCommand command, string commandSet, string source)
    {
        if (_commands.TryGetValue(command.Name, out var existing))
        {
            AddError(source, $"Lệnh '{command.Name}' trùng tên với lệnh của {existing.CommandSet}, đã bỏ qua.");
            return;
        }
        try { JsonDocument.Parse(command.InputSchema); }
        catch (JsonException ex)
        {
            AddError(source, $"InputSchema của '{command.Name}' không phải JSON hợp lệ: {ex.Message}");
            return;
        }
        _commands[command.Name] = new LoadedCommand(command, commandSet);
    }

    private void AddError(string source, string message)
    {
        _errors.Add(new LoadError(source, message));
        Log.Error($"{source}: {message}");
    }
}
