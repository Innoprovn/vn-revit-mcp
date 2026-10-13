using System.Diagnostics;
using System.Reflection;

namespace InnoPro.Mcp.Server.Setup;

/// <summary>
/// Server chạy từ gói .mcpb (Claude Desktop → cài extension): gói mang theo add-in Revit và bộ cài.
/// Lần chạy đầu (hoặc khi gói mới hơn add-in đang cài) tự gọi Install-VnRevitMcp.ps1 -Source gói -NoClients,
/// chạy nền, không chặn MCP. Claude Desktop bản MSIX ảo hoá %LOCALAPPDATA% nên khi đó nhờ Explorer chạy bộ cài
/// ở ngoài, với đường dẫn vật lý của gói.
/// </summary>
internal static class BundleInstaller
{
    /// <summary>Đọc "--bundle &lt;thư mục&gt;" khỏi tham số; trả tham số còn lại.</summary>
    public static string[] TakeBundleArg(string[] args, out string? bundleDir)
    {
        bundleDir = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bundle" && i + 1 < args.Length) bundleDir = args[++i];
            else rest.Add(args[i]);
        }
        return rest.ToArray();
    }

    public static void EnsureInBackground(string bundleDir) => _ = Task.Run(() =>
    {
        try { Ensure(bundleDir); }
        catch (Exception ex) { Log($"Không tự cài được add-in: {ex.Message}"); }
    });

    private static void Ensure(string bundleDir)
    {
        var installer = Path.Combine(bundleDir, "Install-VnRevitMcp.ps1");
        if (!File.Exists(installer)) return;

        var bundleVersion = typeof(BundleInstaller).Assembly.GetName().Version ?? new Version(0, 0);
        var installedDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InnoPro", "vn-revit-mcp", "current", "addin", "InnoPro.Revit.Addin.dll");
        if (File.Exists(installedDll)
            && Version.TryParse(FileVersionInfo.GetVersionInfo(installedDll).FileVersion, out var installed)
            && installed >= bundleVersion)
            return;

        Log($"Cài add-in Revit từ gói (bản {bundleVersion.ToString(3)})...");
        if (InPackage())
            RunOutside(Physical(bundleDir), installer);
        else
            RunInstaller($"-NoProfile -ExecutionPolicy Bypass -File \"{installer}\" -Source \"{bundleDir}\" -NoClients");
    }

    private static void RunInstaller(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Log(process.ExitCode == 0 ? "Đã cài add-in. Mở lại Revit để dùng." : "Cài add-in lỗi:\n" + output);
    }

    /// <summary>Explorer chạy file .cmd ở ngoài vùng ảo hoá của ứng dụng MSIX.</summary>
    private static void RunOutside(string physicalBundle, string installer)
    {
        var physicalInstaller = Path.Combine(physicalBundle, Path.GetFileName(installer));
        var cmd = Path.Combine(physicalBundle, "install-addin.cmd");
        File.WriteAllLines(cmd,
        [
            "@echo off",
            "chcp 65001 >nul",
            $"powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{physicalInstaller}\" -Source \"{physicalBundle}\" -NoClients > \"{physicalBundle}\\install-addin.log\" 2>&1",
        ]);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{cmd}\"") { UseShellExecute = false });
        Log("Đã nhờ Explorer cài add-in (ứng dụng chạy dạng MSIX). Mở lại Revit sau ít phút.");
    }

    /// <summary>
    /// Thư mục mới tạo ngay dưới %LOCALAPPDATA% bị chuyển vào Packages\&lt;gói&gt;\LocalCache\Local khi chạy trong MSIX
    /// (đo thực tế; API gói không phản ánh đúng trường hợp này).
    /// </summary>
    internal static bool InPackage()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var name = "VnRevitMcpProbe-" + Guid.NewGuid().ToString("N");
        var probe = Path.Combine(local, name);
        Directory.CreateDirectory(probe);
        try
        {
            var packages = Path.Combine(local, "Packages");
            return Directory.Exists(packages) && Directory.EnumerateDirectories(packages)
                .Any(p => Directory.Exists(Path.Combine(p, "LocalCache", "Local", name)));
        }
        finally
        {
            try { Directory.Delete(probe, true); } catch (IOException) { }
        }
    }

    /// <summary>Đường dẫn thật trên đĩa của một thư mục mà ứng dụng MSIX nhìn thấy qua lớp ảo hoá AppData.</summary>
    private static string Physical(string dir)
    {
        var full = Path.GetFullPath(dir);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Packages");
        foreach (var (root, cache) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roaming"),
                     (local, "Local"),
                 })
        {
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(packages)) continue;
            var relative = full[(root.Length + 1)..];
            foreach (var package in Directory.EnumerateDirectories(packages))
            {
                var candidate = Path.Combine(package, "LocalCache", cache, relative);
                if (File.Exists(Path.Combine(candidate, "Install-VnRevitMcp.ps1"))) return candidate;
            }
        }
        return full;
    }

    private static void Log(string message) => Console.Error.WriteLine("[vn-revit-mcp] " + message);
}
