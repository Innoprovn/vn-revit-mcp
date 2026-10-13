using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace BIMTech4D
{
    /// <summary>Hàng đợi việc cần chạy trong ngữ cảnh Revit API (cửa sổ modeless không gọi API trực tiếp được).</summary>
    public class RevitBridge : IExternalEventHandler
    {
        readonly ConcurrentQueue<Action<UIApplication>> _q = new ConcurrentQueue<Action<UIApplication>>();
        readonly ExternalEvent _ev;

        public RevitBridge() { _ev = ExternalEvent.Create(this); }

        public void Run(Action<UIApplication> action)
        {
            _q.Enqueue(action);
            _ev.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_q.TryDequeue(out var a))
            {
                try { a(app); }
                catch (Exception ex) { TaskDialog.Show("4D Simulation", ex.Message); }
            }
        }

        public string GetName() => "4D Simulation";
    }
}
