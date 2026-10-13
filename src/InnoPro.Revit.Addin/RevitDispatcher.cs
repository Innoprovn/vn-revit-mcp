using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Revit API chỉ được gọi trên luồng chính. Lệnh từ cầu nối được xếp hàng ở đây
/// và chạy khi Revit rảnh, thông qua ExternalEvent.
/// </summary>
internal sealed class RevitDispatcher : IExternalEventHandler, IDisposable
{
    private sealed record WorkItem(Func<UIApplication, string> Work, TaskCompletionSource<string> Completion, CancellationToken Token, DateTime Queued);

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ExternalEvent _event;

    public RevitDispatcher()
    {
        _event = ExternalEvent.Create(this);
    }

    public Task<string> InvokeAsync(Func<UIApplication, string> work, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new WorkItem(work, completion, token, DateTime.UtcNow));

        var request = _event.Raise();
        if (request is not (ExternalEventRequest.Accepted or ExternalEventRequest.Pending))
            completion.TrySetException(new InvalidOperationException($"Revit từ chối yêu cầu ({request})."));

        return completion.Task;
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            // Phía gọi đã bỏ cuộc (quá thời gian chờ) thì không chạy nữa, tránh sửa mô hình ngoài ý muốn.
            if (item.Token.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.Token);
                continue;
            }

            try
            {
                item.Completion.TrySetResult(item.Work(app));
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }

    public bool HasPending => !_queue.IsEmpty;

    /// <summary>
    /// Có việc chờ quá lâu (ExternalEvent bị kẹt, vd Revit đang nạp mô hình). Chỉ khi đó mới chạy dự phòng trong Idling:
    /// trong sự kiện Idling Revit cấm đổi tài liệu, đổi view... nên không được giành việc của ExternalEvent.
    /// </summary>
    public bool HasStale => _queue.TryPeek(out var item) && DateTime.UtcNow - item.Queued > TimeSpan.FromSeconds(2);

    /// <summary>Đổi lõi: việc còn trong hàng đợi báo lỗi để phía gọi thử lại với lõi mới.</summary>
    public void Dispose()
    {
        while (_queue.TryDequeue(out var item))
            item.Completion.TrySetException(new InvalidOperationException("Revit đang nạp bản mới của add-in, thử lại sau vài giây."));
        _event.Dispose();
    }

    public string GetName() => "InnoPro vn-revit-mcp";
}
