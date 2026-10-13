using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using InnoPro.Bridge;

namespace InnoPro.Mcp.Server;

public sealed class RevitBridgeException(string message) : Exception(message);

/// <summary>
/// Kết nối tới add-in trong Revit qua named pipe. Mỗi yêu cầu mở một kết nối riêng
/// nên không cần giữ trạng thái khi Revit tắt/mở lại.
/// </summary>
public sealed class RevitBridgeClient
{
    /// <summary>Chọn phiên Revit cụ thể theo PID khi mở nhiều Revit cùng lúc.</summary>
    private static readonly string? PinnedPid = Environment.GetEnvironmentVariable("VN_REVIT_MCP_PID");

    public static IReadOnlyList<int> FindRevitProcesses() => Directory.GetFiles(@"\\.\pipe\")
        .Select(Path.GetFileName)
        .Where(n => n != null && n.StartsWith(BridgeProtocol.PipePrefix, StringComparison.Ordinal))
        .Select(n => int.TryParse(n![BridgeProtocol.PipePrefix.Length..], out var pid) ? pid : 0)
        .Where(pid => pid > 0)
        .Distinct()
        .ToList();

    /// <summary>
    /// Phiên Revit đang làm việc. Nhiều phiên: lần đầu tự chọn Revit người dùng thao tác gần nhất (cửa sổ trên cùng),
    /// rồi giữ nguyên để id phần tử không lẫn giữa các mô hình; AI đổi bằng revit_status pid=...
    /// </summary>
    public static int? Selected { get; set; }

    public async Task<JsonElement> SendAsync(string method, object? parameters, TimeSpan timeout, CancellationToken token, int? pid = null)
    {
        var pipeName = pid is int target ? BridgeProtocol.PipeName(target) : ResolvePipe();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(3000, cts.Token);
        }
        catch (TimeoutException)
        {
            throw new RevitBridgeException("Không kết nối được tới Revit. Kiểm tra nút MCP trên tab InnoPro đang bật.");
        }

        using var reader = new StreamReader(pipe, BridgeProtocol.Encoding, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, BridgeProtocol.Encoding, 4096, leaveOpen: true) { AutoFlush = true };

        var request = new BridgeRequest(Guid.NewGuid().ToString("N"), method,
            parameters == null ? null : JsonSerializer.SerializeToElement(parameters, BridgeProtocol.Json));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, BridgeProtocol.Json));

        string? line;
        try
        {
            line = await reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new RevitBridgeException("Revit không phản hồi kịp (có thể đang bận hoặc mở hộp thoại).");
        }

        var response = line == null ? null : JsonSerializer.Deserialize<BridgeResponse>(line, BridgeProtocol.Json);
        if (response == null) throw new RevitBridgeException("Revit đóng kết nối mà không trả lời.");
        if (!response.Ok) throw new RevitBridgeException(response.Error ?? "Lỗi không rõ.");
        return response.Result ?? default;
    }

    private static string ResolvePipe()
    {
        if (int.TryParse(PinnedPid, out var pinned)) return BridgeProtocol.PipeName(pinned);

        var running = FindRevitProcesses();
        if (running.Count == 0)
            throw new RevitBridgeException(
                "Chưa thấy Revit nào đang chạy vn-revit-mcp. Hãy mở Revit (2025 trở lên) và bật nút MCP trên tab InnoPro.");
        if (Selected is not int chosen || !running.Contains(chosen))
            Selected = chosen = running.Count == 1 ? running[0] : TopMost(running) ?? running.Max();
        return BridgeProtocol.PipeName(chosen);
    }

    /// <summary>Phiên Revit có cửa sổ nằm trên cùng (EnumWindows đi từ trên xuống theo thứ tự Z).</summary>
    private static int? TopMost(IReadOnlyList<int> pids)
    {
        int? found = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains((int)pid)) return true;
            found = (int)pid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
