using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using DB = Autodesk.Revit.DB;
using RUI = Autodesk.Revit.UI;

namespace BIMTech4D
{
    /// <summary>Cửa sổ 4D (modeless): ① gán tự động, ② xem tiến độ, ③ chạy mô phỏng.</summary>
    public class MainWindow : Window
    {
        public DB.Document Doc { get; }
        readonly RevitBridge _bridge;
        readonly ObservableCollection<Task4D> _tasks = new ObservableCollection<Task4D>();
        readonly ObservableCollection<FilterInfo> _filters;
        readonly List<string> _paramNames;
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Stopwatch _clock = new Stopwatch();

        static readonly Color Accent = Color.FromRgb(37, 99, 235);
        static readonly Brush PanelBg = new SolidColorBrush(Color.FromRgb(249, 250, 251));
        static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        static readonly Brush Line = new SolidColorBrush(Color.FromRgb(229, 231, 235));
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        const string IconPlay = "", IconPause = "";
        const double OrbitDegPerSec = 12;
        bool _themed;
        CheckBox _ckCamera;
        Slider _slDist;
        double _angle = 225;

        RadioButton _rbCat, _rbParam, _rbFilter;
        FrameworkElement _pnlCat, _pnlParam, _pnlFilter;
        ComboBox _cbParam, _cbSpeed;
        ListBox _lbFilters;
        CheckBox _ckLevel;
        DatePicker _dpStart;
        TextBox _tbDays;
        DataGrid _grid;
        TextBlock _summary, _dateText, _statusText;
        Slider _slider;
        Button _btnPlay, _btnExit;

        DateTime _min = DateTime.Today, _max = DateTime.Today, _cur = DateTime.Today;
        double _carry;
        bool _simOn, _busy, _pendingApply, _changed, _suppressSlider;
        SimSession _session; // chỉ dùng trong ngữ cảnh Revit

        static readonly (string label, double daysPerSec)[] Speeds = { ("1 ngày/giây", 1), ("3 ngày/giây", 3), ("1 tuần/giây", 7), ("2 tuần/giây", 14), ("1 tháng/giây", 30) };

        public MainWindow(RevitBridge bridge, DB.Document doc, List<Task4D> tasks, List<string> paramNames, List<FilterInfo> filters)
        {
            _bridge = bridge;
            Doc = doc;
            _paramNames = paramNames;
            _filters = new ObservableCollection<FilterInfo>(filters);
            Language = XmlLanguage.GetLanguage("vi-VN");
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Width = 1120; Height = 640; MinWidth = 820; MinHeight = 440;
            Background = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "4D Simulation - " + doc.Title;
            _themed = Theme.TryApply(this);

            Content = BuildUi();
            foreach (var t in tasks) { Hook(t); _tasks.Add(t); }
            _tasks.CollectionChanged += (s, e) => { _changed = true; UpdateRange(); };
            _timer.Tick += OnTick;
            Closing += OnClosing;
            _cur = _tasks.Count > 0 ? _tasks.Min(t => t.Start) : DateTime.Today;
            UpdateRange();
            Status(_tasks.Count > 0 ? $"Đã đọc tiến độ lưu trong model. Bấm ▶ để xem." : "Chọn cách chia ở bên trái rồi bấm \"Tạo tiến độ\".");
        }

        // ───────────────────────── Giao diện ─────────────────────────

        UIElement BuildUi()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var left = BuildRulePanel();
            root.Children.Add(left);

            var right = BuildSchedulePanel();
            Grid.SetColumn(right, 1);
            root.Children.Add(right);

            var bottom = BuildPlayBar();
            Grid.SetRow(bottom, 1);
            Grid.SetColumnSpan(bottom, 2);
            root.Children.Add(bottom);
            return root;
        }

        FrameworkElement BuildRulePanel()
        {
            var p = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            p.Children.Add(Heading("① Gán tiến độ tự động"));

            p.Children.Add(Small("Chia công việc theo", 8));
            _rbCat = new RadioButton { Content = "Hạng mục (móng → dầm → sàn → cột…)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
            _rbParam = new RadioButton { Content = "Giá trị của một tham số", Margin = new Thickness(0, 4, 0, 0) };
            _rbFilter = new RadioButton { Content = "Filter mẫu của dự án", Margin = new Thickness(0, 4, 0, 0) };
            foreach (var rb in new[] { _rbCat, _rbParam, _rbFilter }) { rb.Checked += (s, e) => UpdateModePanels(); p.Children.Add(rb); }

            // Hạng mục
            _pnlCat = Small("Thứ tự: móng, dầm, sàn, cột, tường, thang, mái, vách kính, cửa, trần; các loại khác xếp sau theo tên.", 8);
            p.Children.Add(_pnlCat);

            // Tham số
            var pp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _cbParam = new ComboBox { ItemsSource = _paramNames, IsEditable = true, IsTextSearchEnabled = true };
            var pref = new[] { "Thứ tự thi công", "Thu tu thi cong", "Sequence", "Phase Created", "Giai đoạn tạo" };
            _cbParam.Text = pref.FirstOrDefault(n => _paramNames.Contains(n)) ?? "";
            pp.Children.Add(_cbParam);
            pp.Children.Add(Small("Mỗi giá trị là một công việc, xếp tăng dần (1, 2, … 10). Phần tử để trống tham số sẽ không vào tiến độ.", 4));
            _pnlParam = pp;
            p.Children.Add(pp);

            // Filter
            var fp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _lbFilters = new ListBox { ItemsSource = _filters, Height = 150, BorderBrush = Line };
            var item = new FrameworkElementFactory(typeof(CheckBox));
            item.SetBinding(ContentControl.ContentProperty, new Binding(nameof(FilterInfo.Name)));
            item.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(FilterInfo.Checked)) { Mode = BindingMode.TwoWay });
            _lbFilters.ItemTemplate = new DataTemplate { VisualTree = item };
            fp.Children.Add(_lbFilters);
            var mv = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            mv.Children.Add(Btn("↑ Lên", () => MoveFilter(-1)));
            mv.Children.Add(Btn("↓ Xuống", () => MoveFilter(1)));
            fp.Children.Add(mv);
            fp.Children.Add(Small(_filters.Count == 0
                ? "Dự án chưa có filter nào. Tạo trong Revit: View > Filters."
                : "Tick các filter cần dùng. Thứ tự trong danh sách là thứ tự thi công; phần tử thuộc nhiều filter lấy filter đứng trước.", 4));
            _pnlFilter = fp;
            p.Children.Add(fp);

            _ckLevel = new CheckBox { Content = "Chia theo tầng (từ dưới lên)", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
            p.Children.Add(_ckLevel);

            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            AddCell(g, Small("Bắt đầu", 0), 0, 0);
            _dpStart = new DatePicker { SelectedDate = DateTime.Today };
            AddCell(g, _dpStart, 0, 1);
            AddCell(g, Small("Mỗi việc", 0), 1, 0);
            var dp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _tbDays = new TextBox { Text = "7", Width = 50, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(3) };
            dp.Children.Add(_tbDays);
            dp.Children.Add(Small(" ngày", 0));
            AddCell(g, dp, 1, 1);
            foreach (FrameworkElement c in g.Children) c.VerticalAlignment = VerticalAlignment.Center;
            p.Children.Add(g);

            var gen = Primary("Tạo tiến độ", Generate);
            gen.Margin = new Thickness(0, 14, 0, 0);
            gen.HorizontalAlignment = HorizontalAlignment.Stretch;
            p.Children.Add(gen);

            UpdateModePanels();
            return new Border
            {
                Background = PanelBg,
                BorderBrush = Line,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            };
        }

        FrameworkElement BuildSchedulePanel()
        {
            var dock = new DockPanel { Margin = new Thickness(14, 12, 14, 8) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var more = Btn("⋯", null, "Nhập / xuất CSV");
            more.Padding = new Thickness(10, 0, 10, 2);
            more.FontWeight = FontWeights.Bold;
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Xuất CSV (sửa ngày bằng Excel)…", ExportCsv));
            menu.Items.Add(MenuItem("Nhập CSV…", ImportCsv));
            more.Click += (s, e) => { menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            DockPanel.SetDock(more, Dock.Right);
            head.Children.Add(more);
            var clear = Btn("Xoá tiến độ", ClearSchedule, "Xoá toàn bộ tiến độ trong model và các view 4D Simulation");
            clear.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            DockPanel.SetDock(clear, Dock.Right);
            head.Children.Add(clear);
            var title = Heading("② Tiến độ");
            title.Margin = new Thickness(0);
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(title);
            _summary = Small("", 0);
            _summary.Margin = new Thickness(10, 3, 0, 0);
            head.Children.Add(_summary);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            var hint = Small("Bấm đúp vào ngày để sửa. Chuột phải để chọn phần tử trong Revit, đổi loại, hoặc xoá.", 6);
            DockPanel.SetDock(hint, Dock.Bottom);
            dock.Children.Add(hint);

            _grid = new DataGrid
            {
                ItemsSource = _tasks,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserSortColumns = false,
                CanUserResizeRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                EnableRowVirtualization = true,
            };
            if (!_themed)
            {
                _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
                _grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
                _grid.RowHeight = 26;
                _grid.Background = Brushes.White;
                _grid.BorderBrush = Line;
            }
            var center = new Style(typeof(TextBlock));
            center.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            center.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            var right = new Style(typeof(TextBlock), center);
            right.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            right.Setters.Add(new Setter(TextBlock.ForegroundProperty, Muted));
            _grid.Columns.Add(new DataGridTextColumn { Header = "Công việc", Binding = new Binding(nameof(Task4D.Name)), Width = 190, ElementStyle = center });
            _grid.Columns.Add(DateCol("Bắt đầu", nameof(Task4D.Start)));
            _grid.Columns.Add(DateCol("Kết thúc", nameof(Task4D.End)));
            _grid.Columns.Add(new DataGridTextColumn { Header = "Số PT", Binding = new Binding(nameof(Task4D.Count)), IsReadOnly = true, Width = 56, ElementStyle = right });
            var gantt = new FrameworkElementFactory(typeof(GanttBar));
            gantt.SetBinding(GanttBar.TaskProperty, new Binding("."));
            _grid.Columns.Add(new DataGridTemplateColumn { Header = new GanttHeader(), CellTemplate = new DataTemplate { VisualTree = gantt }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180, IsReadOnly = true });

            var cm = new ContextMenu();
            cm.Items.Add(MenuItem("Chọn phần tử trong Revit", SelectInRevit));
            cm.Items.Add(MenuItem("Thêm phần tử đang chọn vào việc này", () => AttachSelection(true)));
            cm.Items.Add(MenuItem("Bỏ phần tử đang chọn khỏi việc này", () => AttachSelection(false)));
            cm.Items.Add(new Separator());
            cm.Items.Add(MenuItem("Loại: Xây dựng", () => SetType(TaskType.Construct)));
            cm.Items.Add(MenuItem("Loại: Phá dỡ", () => SetType(TaskType.Demolish)));
            cm.Items.Add(MenuItem("Loại: Tạm thời (dựng rồi tháo)", () => SetType(TaskType.Temporary)));
            cm.Items.Add(new Separator());
            cm.Items.Add(MenuItem("Thêm việc trống bên dưới", AddTask));
            cm.Items.Add(MenuItem("Xoá việc", DeleteTasks));
            _grid.ContextMenu = cm;
            dock.Children.Add(_grid);
            return dock;
        }

        FrameworkElement BuildPlayBar()
        {
            var g = new Grid { Margin = new Thickness(14, 10, 14, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _btnPlay = Primary("", TogglePlay);
            if (_themed) _btnPlay.Style = (Style)FindResource("Round");
            _btnPlay.Content = new TextBlock { Text = IconPlay, FontFamily = IconFont, FontSize = 18, Foreground = Brushes.White, Margin = new Thickness(2, 0, 0, 0) };
            _btnPlay.Width = 46; _btnPlay.Height = 46;
            _btnPlay.Padding = new Thickness(0);
            _btnPlay.VerticalAlignment = VerticalAlignment.Center;
            _btnPlay.ToolTip = "Chạy / dừng mô phỏng";
            Grid.SetRowSpan(_btnPlay, 2);
            g.Children.Add(_btnPlay);

            var mid = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            _dateText = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold };
            mid.Children.Add(_dateText);
            _slider = new Slider { Minimum = 0, Maximum = 1, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 7, Margin = new Thickness(0, 4, 0, 0) };
            _slider.ValueChanged += (s, e) =>
            {
                if (_suppressSlider) return;
                _cur = _min.AddDays(Math.Round(_slider.Value));
                EnsureSim();
                RequestApply();
            };
            mid.Children.Add(_slider);
            Grid.SetColumn(mid, 1);
            Grid.SetRowSpan(mid, 2);
            g.Children.Add(mid);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _ckCamera = new CheckBox { Content = "Camera bay quanh", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Camera phối cảnh xoay quanh công trình và nâng dần theo phần đang thi công" };
            _ckCamera.Click += (s, e) => CameraToggled();
            right.Children.Add(_ckCamera);
            _slDist = new Slider { Minimum = 1.2, Maximum = 4, Value = 2.2, Width = 80, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Khoảng cách camera: trái gần, phải xa", Visibility = Visibility.Collapsed };
            _slDist.ValueChanged += (s, e) => { if (_ckCamera.IsChecked == true && !_timer.IsEnabled) RequestApply(); };
            right.Children.Add(_slDist);
            _cbSpeed = new ComboBox { ItemsSource = Speeds.Select(x => x.label).ToList(), SelectedIndex = 2, Width = 110, ToolTip = "Tốc độ chạy", VerticalContentAlignment = VerticalAlignment.Center };
            right.Children.Add(_cbSpeed);
            _btnExit = Btn("Thoát mô phỏng", ExitSim, "Trả view 3D về bình thường");
            _btnExit.Margin = new Thickness(8, 0, 0, 0);
            _btnExit.Visibility = Visibility.Collapsed;
            right.Children.Add(_btnExit);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);

            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            legend.Children.Add(Swatch(GanttBar.CConstruct, "Đang xây"));
            legend.Children.Add(Swatch(GanttBar.CDemolish, "Phá dỡ"));
            legend.Children.Add(Swatch(GanttBar.CTemporary, "Tạm thời"));
            legend.Children.Add(Small("   Chưa làm: ẩn", 0));
            Grid.SetColumn(legend, 2);
            Grid.SetRow(legend, 1);
            g.Children.Add(legend);

            var outer = new DockPanel();
            _statusText = Small("", 0);
            _statusText.Margin = new Thickness(14, 0, 14, 6);
            _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(_statusText, Dock.Bottom);
            outer.Children.Add(_statusText);
            outer.Children.Add(g);
            return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = outer, Background = Brushes.White };
        }

        static DataGridTemplateColumn DateCol(string header, string path)
        {
            var view = new FrameworkElementFactory(typeof(TextBlock));
            view.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = "dd/MM/yyyy" });
            view.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            var edit = new FrameworkElementFactory(typeof(DatePicker));
            edit.SetBinding(DatePicker.SelectedDateProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            return new DataGridTemplateColumn
            {
                Header = header,
                CellTemplate = new DataTemplate { VisualTree = view },
                CellEditingTemplate = new DataTemplate { VisualTree = edit },
                Width = 95
            };
        }

        static TextBlock Heading(string t) => new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        static TextBlock Small(string t, double top) => new TextBlock { Text = t, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };

        static void AddCell(Grid g, UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }

        Button Btn(string text, Action click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            if (!_themed) { b.Padding = new Thickness(10, 4, 10, 4); b.Background = Brushes.White; b.BorderBrush = Line; }
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        Button Primary(string text, Action click)
        {
            var b = Btn(text, click);
            b.Margin = new Thickness(0);
            if (_themed) { b.Style = (Style)FindResource("Primary"); return b; }
            b.Background = new SolidColorBrush(Accent);
            b.Foreground = Brushes.White;
            b.BorderBrush = new SolidColorBrush(Accent);
            b.FontWeight = FontWeights.SemiBold;
            b.Padding = new Thickness(12, 7, 12, 7);
            return b;
        }

        static MenuItem MenuItem(string header, Action click)
        {
            var m = new MenuItem { Header = header };
            m.Click += (s, e) => click();
            return m;
        }

        static UIElement Swatch(Color c, string text)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            p.Children.Add(new Border { Width = 11, Height = 11, Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Muted });
            return p;
        }

        void UpdateModePanels()
        {
            if (_pnlCat == null || _pnlParam == null || _pnlFilter == null) return;
            _pnlCat.Visibility = _rbCat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            _pnlParam.Visibility = _rbParam.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            _pnlFilter.Visibility = _rbFilter.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        void MoveFilter(int dir)
        {
            if (_lbFilters.SelectedItem is not FilterInfo f) return;
            int i = _filters.IndexOf(f), j = i + dir;
            if (j < 0 || j >= _filters.Count) return;
            _filters.Move(i, j);
            _lbFilters.SelectedItem = f;
        }

        // ───────────────────────── Dữ liệu ─────────────────────────

        void Hook(Task4D t) => t.PropertyChanged += OnTaskChanged;

        void OnTaskChanged(object s, PropertyChangedEventArgs e)
        {
            _changed = true;
            if (e.PropertyName == nameof(Task4D.Start) || e.PropertyName == nameof(Task4D.End)) UpdateRange();
            if (e.PropertyName != nameof(Task4D.Name)) RequestApply();
        }

        void ReplaceTasks(IEnumerable<Task4D> list)
        {
            foreach (var t in _tasks) t.PropertyChanged -= OnTaskChanged;
            _tasks.Clear();
            foreach (var t in list) { Hook(t); _tasks.Add(t); }
            _cur = _tasks.Count > 0 ? _tasks.Min(t => t.Start) : DateTime.Today;
            UpdateRange();
            RequestApply();
        }

        void Status(string s) { if (_statusText != null) _statusText.Text = s; }

        IEnumerable<Task4D> SelectedTasks => _grid.SelectedItems.OfType<Task4D>();

        void SetType(TaskType type) { foreach (var t in SelectedTasks.ToList()) t.Type = type; }

        void AddTask()
        {
            var sel = _grid.SelectedItem as Task4D;
            var start = sel != null ? sel.End.AddDays(1) : (_tasks.Count > 0 ? _tasks.Max(t => t.End).AddDays(1) : DateTime.Today);
            var t = new Task4D { Name = $"Công việc {_tasks.Count + 1}", Start = start, End = start.AddDays(6) };
            Hook(t);
            _tasks.Insert(sel != null ? _tasks.IndexOf(sel) + 1 : _tasks.Count, t);
            _grid.SelectedItem = t;
            _grid.ScrollIntoView(t);
        }

        void DeleteTasks()
        {
            var sel = SelectedTasks.ToList();
            if (sel.Count == 0) return;
            if (MessageBox.Show(this, $"Xoá {sel.Count} công việc?", "4D Simulation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var t in sel) { t.PropertyChanged -= OnTaskChanged; _tasks.Remove(t); }
            RequestApply();
        }

        // ───────────────────────── Lệnh Revit ─────────────────────────

        RUI.UIDocument UiDoc(RUI.UIApplication app)
        {
            var u = app.ActiveUIDocument;
            if (!Doc.IsValidObject) throw new InvalidOperationException("File model của cửa sổ này đã đóng.");
            if (u == null || !u.Document.Equals(Doc))
                throw new InvalidOperationException($"Hãy chuyển sang file \"{Doc.Title}\" trong Revit rồi thử lại.");
            return u;
        }

        void Generate()
        {
            if (!int.TryParse(_tbDays.Text, out int days) || days < 1) { Status("Số ngày mỗi việc phải là số nguyên ≥ 1."); return; }
            var o = new GenOptions
            {
                Mode = _rbParam.IsChecked == true ? GroupMode.Parameter : _rbFilter.IsChecked == true ? GroupMode.Filter : GroupMode.Category,
                ParamName = _cbParam.Text?.Trim(),
                FilterIds = _filters.Where(f => f.Checked).Select(f => f.Id).ToList(),
                ByLevel = _ckLevel.IsChecked == true,
                Start = _dpStart.SelectedDate ?? DateTime.Today,
                Days = days
            };
            if (o.Mode == GroupMode.Parameter && string.IsNullOrEmpty(o.ParamName)) { Status("Chọn tham số dùng để chia."); return; }
            if (o.Mode == GroupMode.Filter && o.FilterIds.Count == 0) { Status("Tick ít nhất một filter."); return; }
            if (_tasks.Count > 0 && MessageBox.Show(this, "Tạo mới sẽ thay toàn bộ tiến độ hiện tại. Tiếp tục?", "4D Simulation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            Status("Đang đọc model…");
            _bridge.Run(app =>
            {
                UiDoc(app);
                var (list, skipped) = AutoGen.Build(Doc, o);
                if (list.Count == 0) { Status("Không có phần tử nào khớp quy tắc."); return; }
                if (_simOn) { _session?.Reset(); _session = SimSession.Start(UiDoc(app), list.SelectMany(t => t.ElementIds).Distinct().ToList(), _ckCamera.IsChecked == true); }
                ReplaceTasks(list);
                TaskStore.Save(Doc, list);
                _changed = false;
                Status($"Đã tạo {list.Count} công việc, {list.Sum(t => t.Count)} phần tử" +
                       (skipped > 0 ? $"; {skipped} phần tử không khớp quy tắc nên luôn hiện" : "") + ". Đã lưu vào model, bấm ▶ để xem.");
            });
        }

        void ClearSchedule()
        {
            if (MessageBox.Show(this,
                    "Xoá toàn bộ tiến độ đang lưu trong model, kèm view \"4D Simulation\" và \"4D Simulation Camera\"?\n\nCó thể Undo (Ctrl+Z) trong Revit nếu lỡ tay.",
                    "4D Simulation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            StopPlay();
            var s = _simOn ? _session : null;
            _simOn = false;
            _session = null;
            _btnExit.Visibility = Visibility.Collapsed;
            _bridge.Run(app =>
            {
                var u = UiDoc(app);
                s?.Reset();
                // đang đứng ở view sắp xoá thì chuyển sang view 3D khác trước
                var names = new[] { SimSession.ViewName, SimSession.CameraViewName, SimSession.LegacyViewName, SimSession.LegacyCameraViewName };
                if (names.Contains(u.ActiveView.Name))
                {
                    var other = new DB.FilteredElementCollector(Doc).OfClass(typeof(DB.View3D)).Cast<DB.View3D>()
                        .FirstOrDefault(v => !v.IsTemplate && !names.Contains(v.Name));
                    if (other != null) u.ActiveView = other;
                }
                // không có view nào khác để chuyển sang thì giữ view (Revit không cho xoá view đang mở)
                int views = TaskStore.DeleteAll(Doc, names.Where(n => n != u.ActiveView.Name));
                ReplaceTasks(new List<Task4D>());
                _changed = false;
                Status($"Đã xoá tiến độ" + (views > 0 ? $" và {views} view mô phỏng." : "."));
            });
        }

        void AttachSelection(bool add)
        {
            if (_grid.SelectedItem is not Task4D task) return;
            _bridge.Run(app =>
            {
                var ids = UiDoc(app).Selection.GetElementIds().Select(i => i.Value).ToList();
                if (ids.Count == 0) { Status("Trong Revit chưa chọn phần tử nào."); return; }
                if (add)
                {
                    // một phần tử chỉ thuộc một việc: gỡ khỏi việc khác
                    var set = new HashSet<long>(ids);
                    foreach (var t in _tasks.Where(t => t != task && t.ElementIds.Any(set.Contains)))
                    { t.ElementIds = t.ElementIds.Where(i => !set.Contains(i)).ToList(); t.TouchElements(); }
                    task.ElementIds = task.ElementIds.Union(ids).ToList();
                    Status($"Đã chuyển {ids.Count} phần tử vào \"{task.Name}\".");
                }
                else
                {
                    var rm = new HashSet<long>(ids);
                    task.ElementIds = task.ElementIds.Where(i => !rm.Contains(i)).ToList();
                    Status($"Đã bỏ phần tử đang chọn khỏi \"{task.Name}\".");
                }
                task.TouchElements();
                _changed = true;
                RequestApply();
            });
        }

        void SelectInRevit()
        {
            var ids = SelectedTasks.SelectMany(t => t.ElementIds).Distinct().ToList();
            if (ids.Count == 0) { Status("Việc đang chọn chưa có phần tử."); return; }
            _bridge.Run(app =>
            {
                var u = UiDoc(app);
                var valid = ids.Select(i => new DB.ElementId(i)).Where(i => Doc.GetElement(i) != null).ToList();
                u.Selection.SetElementIds(valid);
                Status($"Đã chọn {valid.Count} phần tử trong Revit.");
            });
        }

        void ImportCsv()
        {
            var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", Title = "Nhập tiến độ CSV" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var list = Csv.Import(dlg.FileName);
                if (_tasks.Count > 0 && MessageBox.Show(this, $"Thay tiến độ hiện tại bằng {list.Count} công việc trong file?", "4D Simulation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                ReplaceTasks(list);
                _changed = true;
                Status($"Đã nhập {list.Count} công việc từ {Path.GetFileName(dlg.FileName)}.");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Lỗi nhập CSV", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void ExportCsv()
        {
            var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = Path.GetFileNameWithoutExtension(Doc.Title) + "_4D.csv" };
            if (dlg.ShowDialog(this) != true) return;
            try { Csv.Export(dlg.FileName, _tasks); Status($"Đã xuất ra {dlg.FileName}. Sửa ngày bằng Excel rồi Nhập CSV lại."); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Lỗi xuất CSV", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // ───────────────────────── Mô phỏng ─────────────────────────

        void UpdateRange()
        {
            if (_tasks.Count > 0)
            {
                _min = _tasks.Min(t => t.Start < t.End ? t.Start : t.End);
                _max = _tasks.Max(t => t.End > t.Start ? t.End : t.Start);
            }
            else _min = _max = DateTime.Today;
            if (_cur < _min) _cur = _min;
            if (_cur > _max) _cur = _max;
            GanttBar.Min = _min; GanttBar.Max = _max;
            SyncSlider();
            GanttBar.Current = _cur;
            GanttBar.Refresh();
            UpdateDateText();
            if (_summary != null)
                _summary.Text = _tasks.Count == 0 ? "" : $"{_tasks.Count} việc · {_tasks.Sum(t => t.Count)} phần tử · {_min:dd/MM/yyyy} → {_max:dd/MM/yyyy}";
        }

        void SyncSlider()
        {
            if (_slider == null) return;
            _suppressSlider = true;
            _slider.Maximum = Math.Max(1, (_max - _min).TotalDays);
            _slider.Value = (_cur - _min).TotalDays;
            _suppressSlider = false;
        }

        void UpdateDateText()
        {
            if (_dateText == null) return;
            if (_tasks.Count == 0) { _dateText.Text = "Chưa có tiến độ"; return; }
            int day = (int)(_cur - _min).TotalDays + 1, total = (int)(_max - _min).TotalDays + 1;
            var running = _tasks.Where(t => t.Start <= _cur && _cur <= t.End).Select(t => t.Name).ToList();
            string now = running.Count == 0 ? "" : running.Count == 1 ? "   ·   " + running[0] : $"   ·   {running[0]} (+{running.Count - 1})";
            _dateText.Text = $"{_cur:dd/MM/yyyy}   ·   ngày {day}/{total}{now}";
        }

        void GoTo(DateTime d)
        {
            _cur = d < _min ? _min : d > _max ? _max : d;
            SyncSlider();
            RequestApply();
        }

        void RequestApply()
        {
            GanttBar.Current = _cur;
            GanttBar.Refresh();
            UpdateDateText();
            if (!_simOn) return;
            _pendingApply = true;
            if (!_busy) PumpApply();
        }

        void PumpApply()
        {
            if (!_pendingApply || !_simOn) return;
            _pendingApply = false;
            _busy = true;
            var states = Sim.Compute(_tasks, _cur);
            var shot = _ckCamera.IsChecked == true ? new CameraShot { AngleDeg = _angle, Distance = _slDist.Value } : null;
            _bridge.Run(app =>
            {
                try
                {
                    if (_session != null)
                    {
                        _session.Apply(states, shot);
                        app.ActiveUIDocument?.RefreshActiveView();
                    }
                }
                catch (Exception ex)
                {
                    StopPlay();
                    Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this, ex.Message, "4D Simulation", MessageBoxButton.OK, MessageBoxImage.Warning)));
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() => { _busy = false; PumpApply(); }), DispatcherPriority.Background);
                }
            });
        }

        /// <summary>Bật mô phỏng nếu chưa bật (tạo/mở view 3D "4D Simulation").</summary>
        void EnsureSim()
        {
            if (_simOn || _busy) return;
            var ids = _tasks.SelectMany(t => t.ElementIds).Distinct().ToList();
            if (ids.Count == 0) { Status("Chưa có tiến độ. Bấm \"Tạo tiến độ\" trước."); return; }
            _busy = true;
            bool cam = _ckCamera.IsChecked == true;
            string name = cam ? SimSession.CameraViewName : SimSession.ViewName;
            Status($"Đang mở view 3D \"{name}\"…");
            _bridge.Run(app =>
            {
                try
                {
                    _session = SimSession.Start(UiDoc(app), ids, cam);
                    _simOn = true;
                    _pendingApply = true;
                    _btnExit.Visibility = Visibility.Visible;
                    Status($"Đang mô phỏng trên view \"{name}\". Các view khác không bị ảnh hưởng.");
                }
                catch { StopPlay(); throw; }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() => { _busy = false; PumpApply(); }), DispatcherPriority.Background);
                }
            });
        }

        void ExitSim()
        {
            StopPlay();
            if (!_simOn) return;
            _simOn = false;
            _btnExit.Visibility = Visibility.Collapsed;
            var s = _session;
            _session = null;
            _bridge.Run(app =>
            {
                s?.Reset();
                app.ActiveUIDocument?.RefreshActiveView();
                Status("Đã thoát mô phỏng, view trở lại bình thường.");
            });
        }

        void TogglePlay()
        {
            if (_timer.IsEnabled) { StopPlay(); return; }
            if (_tasks.Count == 0) { Status("Chưa có tiến độ. Bấm \"Tạo tiến độ\" trước."); return; }
            EnsureSim();
            if (_cur >= _max) GoTo(_min);
            _carry = 0;
            _clock.Restart();
            _timer.Start();
            SetPlayIcon(true);
        }

        void StopPlay()
        {
            _timer.Stop();
            _clock.Stop();
            SetPlayIcon(false);
        }

        void SetPlayIcon(bool playing)
        {
            if (_btnPlay?.Content is TextBlock tb) { tb.Text = playing ? IconPause : IconPlay; tb.Margin = new Thickness(playing ? 0 : 2, 0, 0, 0); }
        }

        /// <summary>Bật/tắt camera bay quanh: đổi sang view phối cảnh "4D Simulation Camera" hoặc về view thường.</summary>
        void CameraToggled()
        {
            bool cam = _ckCamera.IsChecked == true;
            _slDist.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
            if (!_simOn) return;
            if (_busy) { Dispatcher.BeginInvoke(new Action(CameraToggled), DispatcherPriority.Background); return; }
            var ids = _tasks.SelectMany(t => t.ElementIds).Distinct().ToList();
            var old = _session;
            _busy = true;
            _bridge.Run(app =>
            {
                try
                {
                    if (old != null && old.IsCamera == cam) return;
                    old?.Reset();
                    _session = SimSession.Start(UiDoc(app), ids, cam);
                    _pendingApply = true;
                    Status($"Đang mô phỏng trên view \"{_session.View.Name}\".");
                }
                finally
                {
                    Dispatcher.BeginInvoke(new Action(() => { _busy = false; PumpApply(); }), DispatcherPriority.Background);
                }
            });
        }

        // Chạy theo thời gian thật: model nặng vẽ chậm thì nhảy nhiều ngày hơn mỗi bước, tổng thời gian vẫn đúng tốc độ.
        void OnTick(object s, EventArgs e)
        {
            double dt = _clock.Elapsed.TotalSeconds;
            _clock.Restart();
            if (!_simOn) { _carry = 0; return; }
            bool cam = _ckCamera.IsChecked == true;
            _carry += dt * Speeds[Math.Max(0, _cbSpeed.SelectedIndex)].daysPerSec;
            if (cam) _angle = (_angle + dt * OrbitDegPerSec) % 360;
            if (_busy) return;
            int step = (int)_carry;
            if (step >= 1)
            {
                _carry -= step;
                if (_cur < _max) { GoTo(_cur.AddDays(step)); return; }
                if (!cam) { StopPlay(); return; } // có camera thì hết tiến độ vẫn bay vòng cho tới khi bấm dừng
            }
            if (cam) RequestApply();
        }

        void OnClosing(object s, CancelEventArgs e)
        {
            StopPlay();
            var snapshot = _changed ? _tasks.ToList() : null;
            var ss = _simOn ? _session : null;
            _simOn = false;
            _session = null;
            _bridge.Run(app =>
            {
                if (!Doc.IsValidObject) return;
                ss?.Reset();
                if (snapshot != null) TaskStore.Save(Doc, snapshot);
                app.ActiveUIDocument?.RefreshActiveView();
            });
        }
    }
}
