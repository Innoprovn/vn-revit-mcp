namespace InnoPro.Revit.Addin;

internal static class Paths
{
    /// <summary>Thư mục chứa InnoPro.Revit.Addin.dll.</summary>
    public static string AddinDir { get; } = Path.GetDirectoryName(typeof(Paths).Assembly.Location)!;

    /// <summary>CommandSet đi kèm bộ cài.</summary>
    public static string BundledCommandsDir => Path.Combine(AddinDir, "Commands");

    /// <summary>Dữ liệu theo người dùng: CommandSet cài thêm, log, danh mục lệnh.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnoPro", "vn-revit-mcp");

    /// <summary>CommandSet do người dùng hoặc cộng đồng cài thêm.</summary>
    public static string UserCommandsDir => Path.Combine(DataDir, "Commands");

    /// <summary>File lệnh .cs của người dùng: thả file vào đây rồi bấm "Nạp lại lệnh".</summary>
    public static string UserScriptsDir => Path.Combine(DataDir, "Scripts");

    public static string[] CommandSetRoots => [BundledCommandsDir, UserCommandsDir];

    public static string[] ScriptRoots => [Path.Combine(AddinDir, "Scripts"), UserScriptsDir];

    /// <summary>DLL nút ribbon sinh lúc chạy.</summary>
    public static string RibbonStubDir => Path.Combine(DataDir, "ribbon");

    /// <summary>Danh mục lệnh ghi ra để MCP server liệt kê tool khi Revit chưa mở.</summary>
    public static string ManifestFile => Path.Combine(DataDir, "commands.json");

    public static string LogDir => Path.Combine(DataDir, "logs");
}
