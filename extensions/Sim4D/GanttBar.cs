using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BIMTech4D
{
    /// <summary>Thanh Gantt vẽ trong một ô của bảng. Thang thời gian dùng chung cho mọi dòng.</summary>
    public class GanttBar : FrameworkElement
    {
        public static DateTime Min = DateTime.Today, Max = DateTime.Today, Current = DateTime.Today;
        public static event Action Changed;
        public static void Refresh() => Changed?.Invoke();

        internal static readonly Color CConstruct = Color.FromRgb(16, 185, 129);
        internal static readonly Color CDemolish = Color.FromRgb(239, 68, 68);
        internal static readonly Color CTemporary = Color.FromRgb(245, 158, 11);
        static readonly Pen PNow = FrozenPen(Color.FromRgb(239, 68, 68), 1.2);

        public static readonly DependencyProperty TaskProperty = DependencyProperty.Register(
            nameof(Task), typeof(Task4D), typeof(GanttBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTaskChanged));

        public Task4D Task { get => (Task4D)GetValue(TaskProperty); set => SetValue(TaskProperty, value); }

        public GanttBar()
        {
            Loaded += (s, e) => Changed += InvalidateVisual;
            Unloaded += (s, e) => Changed -= InvalidateVisual;
        }

        static void OnTaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = (GanttBar)d;
            if (e.OldValue is Task4D o) o.PropertyChanged -= bar.OnTaskProp;
            if (e.NewValue is Task4D n) n.PropertyChanged += bar.OnTaskProp;
        }

        void OnTaskProp(object s, PropertyChangedEventArgs e) => InvalidateVisual();

        internal static double Span => Math.Max(1, (Max - Min).TotalDays + 1);
        internal static double X(DateTime d, double w) => (d - Min).TotalDays / Span * w;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var t = Task;
            if (t != null)
            {
                var c = t.Type == TaskType.Demolish ? CDemolish : t.Type == TaskType.Temporary ? CTemporary : CConstruct;
                double x1 = X(t.Start, w), x2 = Math.Max(X(t.End.AddDays(1), w), x1 + 3);
                double bh = 10, y = (h - bh) / 2;
                dc.DrawRoundedRectangle(Frozen(Color.FromArgb(70, c.R, c.G, c.B)), null, new Rect(x1, y, x2 - x1, bh), 3, 3);
                double xp = Math.Min(x2, X(Current.AddDays(1), w));
                if (xp > x1) dc.DrawRoundedRectangle(Frozen(c), null, new Rect(x1, y, xp - x1, bh), 3, 3);
            }
            double xc = (X(Current, w) + X(Current.AddDays(1), w)) / 2;
            dc.DrawLine(PNow, new Point(xc, 0), new Point(xc, h));
        }

        internal static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }
    }

    /// <summary>Thước tháng trên đầu cột Gantt.</summary>
    public class GanttHeader : FrameworkElement
    {
        static readonly Brush Text = GanttBar.Frozen(Color.FromRgb(107, 114, 128));
        static readonly Pen Tick = new Pen(GanttBar.Frozen(Color.FromRgb(209, 213, 219)), 1);
        static readonly Typeface Face = new Typeface("Segoe UI");

        public GanttHeader()
        {
            Height = 16;
            Loaded += (s, e) => GanttBar.Changed += InvalidateVisual;
            Unloaded += (s, e) => GanttBar.Changed -= InvalidateVisual;
            SizeChanged += (s, e) => InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            double pxPerDay = w / GanttBar.Span;
            int stepMonths = pxPerDay * 30 >= 46 ? 1 : pxPerDay * 30 >= 23 ? 2 : pxPerDay * 30 >= 12 ? 3 : 6;
            var m = new DateTime(GanttBar.Min.Year, GanttBar.Min.Month, 1);
            for (; m <= GanttBar.Max; m = m.AddMonths(stepMonths))
            {
                double x = GanttBar.X(m, w);
                if (x < 0) continue;
                dc.DrawLine(Tick, new Point(x, h - 5), new Point(x, h));
                var label = m.Month == 1 || x < 30 ? $"T{m.Month}/{m:yy}" : $"T{m.Month}";
                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, Text, 1.0);
                if (x + 3 + ft.Width <= w) dc.DrawText(ft, new Point(x + 3, 0));
            }
        }
    }
}
