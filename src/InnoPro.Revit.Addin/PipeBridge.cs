using System.IO.Pipes;
using System.Text.Json;
using InnoPro.Bridge;
using InnoPro.Revit.Sdk;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Named pipe chỉ cho cùng người dùng Windows kết nối, không mở cổng mạng nào.
/// </summary>
internal sealed class PipeBridge
{
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<CommandRegistry> _registry;
    private readonly Func<ReloadResult> _reload;
    private readonly RevitDispatcher _dispatcher;
    private readonly Func<RevitStatus> _status;
    private readonly Func<bool> _hasDocument;
    private CancellationTokenSource? _cts;

    public PipeBridge(Func<CommandRegistry> registry, Func<ReloadResult> reload, RevitDispatcher dispatcher, Func<RevitStatus> status, Func<bool> hasDocument)
    {
        _registry = registry;
        _reload = reload;
        _dispatcher = dispatcher;
        _status = status;
        _hasDocument = hasDocument;
    }

    public string PipeName { get; } = BridgeProtocol.PipeName(Environment.ProcessId);

    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => AcceptLoop(token));
        Log.Info($"Cầu nối đã bật: \\\\.\\pipe\\{PipeName}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Log.Info("Cầu nối đã tắt.");
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Lỗi chờ kết nối pipe", ex);
                await pipe.DisposeAsync();
                await Task.Delay(1000, CancellationToken.None);
                continue;
            }

            _ = Task.Run(() => Serve(pipe, token));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using var _ = pipe;
        using var reader = new StreamReader(pipe, BridgeProtocol.Encoding, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, BridgeProtocol.Encoding, 4096, leaveOpen: true) { AutoFlush = true };
        try
        {
            while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
            {
                var response = await Handle(line, token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, BridgeProtocol.Json));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // client đóng kết nối
        catch (Exception ex) { Log.Error("Lỗi phục vụ pipe", ex); }
    }

    private async Task<BridgeResponse> Handle(string line, CancellationToken token)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(line, BridgeProtocol.Json)
                      ?? throw new InvalidDataException("Yêu cầu rỗng.");

            return request.Method switch
            {
                BridgeProtocol.MethodPing => Ok(request, _status()),
                BridgeProtocol.MethodListCommands => Ok(request, DescribeCommands(_registry())),
                BridgeProtocol.MethodReloadCommands => Ok(request, _reload()),
                BridgeProtocol.MethodInvoke => await Invoke(request, token),
                _ => Fail(request.Id, $"Không hỗ trợ phương thức '{request.Method}'."),
            };
        }
        catch (Exception ex)
        {
            return Fail(request?.Id ?? "", ErrorMessage(ex));
        }
    }

    private async Task<BridgeResponse> Invoke(BridgeRequest request, CancellationToken token)
    {
        var invoke = request.Params?.Deserialize<InvokeParams>(BridgeProtocol.Json)
                     ?? throw new InvalidDataException("Thiếu params.");
        if (!_registry().TryGet(invoke.Name, out var loaded))
            return Fail(request.Id, $"Không có lệnh '{invoke.Name}'.");

        // Ở màn hình Home (chưa mở dự án) Revit không chạy lệnh API: báo ngay thay vì chờ hết giờ.
        if (!_hasDocument())
            return Fail(request.Id, "Revit chưa mở dự án nào. Hãy mở hoặc tạo một dự án trong Revit rồi thử lại.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(InvokeTimeout);

        Log.Info($"Chạy lệnh {invoke.Name}");
        var work = _dispatcher.InvokeAsync(app => CommandRunner.RunForBridge(app, loaded, invoke.Arguments, timeout.Token), timeout.Token);
        try
        {
            var json = await work.WaitAsync(timeout.Token);
            return new BridgeResponse(request.Id, true, JsonDocument.Parse(json).RootElement);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Fail(request.Id,
                "Revit không phản hồi kịp. Có thể Revit đang bận (nạp mô hình, đang chạy lệnh khác) hoặc đang mở một hộp thoại; hãy đóng hộp thoại rồi thử lại.");
        }
    }

    public static List<CommandDescriptor> DescribeCommands(CommandRegistry registry) => registry.Commands
        .OrderBy(c => c.Command.Category).ThenBy(c => c.Command.Name)
        .Select(c => new CommandDescriptor(
            c.Command.Name,
            c.Command.Description,
            c.Command.Category,
            c.CommandSet,
            c.Command.TransactionMode.ToString(),
            JsonDocument.Parse(c.Command.InputSchema).RootElement,
            c.Command.Pinned))
        .ToList();

    private static BridgeResponse Ok(BridgeRequest request, object result) =>
        new(request.Id, true, JsonSerializer.SerializeToElement(result, BridgeProtocol.Json));

    private static BridgeResponse Fail(string id, string error) => new(id, false, Error: error);

    private static string ErrorMessage(Exception ex) => ex switch
    {
        CommandException ce => ce.Message,
        Autodesk.Revit.Exceptions.ApplicationException re => $"Revit báo lỗi: {re.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
