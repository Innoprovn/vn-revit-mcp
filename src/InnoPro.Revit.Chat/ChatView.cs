using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InnoPro.Bridge;

namespace InnoPro.Revit.Chat;

/// <summary>Điểm vào của module: host gọi qua reflection để lấy giao diện đặt vào khung (dockable pane).</summary>
public static class ChatModule
{
    public static FrameworkElement CreateView(IRevitHost host) => new ChatView(host);
}

/// <summary>Khung chat AI trong Revit. Dựng bằng mã (không XAML) cho gọn.</summary>
internal sealed class ChatView : UserControl
{
    private static readonly Brush UserBubble = new SolidColorBrush(Color.FromRgb(0xDC, 0xEB, 0xFA));
    private static readonly Brush AssistantBubble = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));

    private readonly IRevitHost _host;
    private readonly ChatEngine _engine;
    private readonly ChatSettings _settings;

    private readonly ComboBox _providerBox = new() { MinWidth = 150 };
    private readonly ComboBox _modelBox = new() { IsEditable = true };
    private readonly TextBox _baseUrlBox = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _baseUrlRow = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _transcript = new() { Margin = new Thickness(6) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _send = new() { Content = "Gửi", Width = 64, Margin = new Thickness(4, 0, 0, 0) };
    private readonly Button _stop = new() { Content = "Dừng", Width = 64, Margin = new Thickness(4, 4, 0, 0), IsEnabled = false };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = Muted, Margin = new Thickness(6, 2, 6, 2) };

    private CancellationTokenSource? _running;
    private bool _providerReady;

    public ChatView(IRevitHost host)
    {
        _host = host;
        _engine = new ChatEngine(host);
        _settings = ChatSettings.Load(host.DataDir);
        Content = Build();

        foreach (var preset in ProviderPresets.All) _providerBox.Items.Add(preset.Name);
        _providerBox.SelectedIndex = Math.Max(0, ProviderPresets.All.ToList().FindIndex(p => p.Id == _settings.Provider));
        _providerBox.SelectionChanged += (_, _) => OnProviderChanged();
        _modelBox.LostFocus += (_, _) => ApplyProvider();
        _modelBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyProvider);
        _baseUrlBox.LostFocus += (_, _) => ApplyProvider();
        _send.Click += (_, _) => Send();
        _stop.Click += (_, _) => _running?.Cancel();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Send(); }
        };

        OnProviderChanged();
        AddLine($"vn-revit-mcp {host.Version}. Hỏi bằng tiếng Việt, ví dụ: \"Mô hình có bao nhiêu cột? Cột nào bị trùng?\"", Muted);
    }

    private ProviderPreset Preset => ProviderPresets.All[Math.Max(0, _providerBox.SelectedIndex)];

    private UIElement Build()
    {
        var root = new DockPanel { Background = Brushes.White };

        var top = new StackPanel { Margin = new Thickness(6) };
        var row = new DockPanel();
        var newChat = SmallButton("Mới", "Bắt đầu cuộc trò chuyện mới");
        newChat.Click += (_, _) => { _engine.Reset(); _transcript.Children.Clear(); _status.Text = "Cuộc trò chuyện mới."; };
        var key = SmallButton("Key", "Nhập hoặc xóa API key (lưu trong Windows Credential Manager)");
        key.Click += (_, _) => EditKey();
        var models = SmallButton("↻", "Tải danh sách model từ nhà cung cấp");
        models.Click += async (_, _) => await LoadModels();
        DockPanel.SetDock(newChat, Dock.Right);
        DockPanel.SetDock(key, Dock.Right);
        DockPanel.SetDock(models, Dock.Right);
        row.Children.Add(newChat);
        row.Children.Add(key);
        row.Children.Add(models);
        row.Children.Add(_providerBox);
        top.Children.Add(row);
        var modelRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var modelLabel = new TextBlock { Text = "Model", Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, FontSize = 11 };
        modelRow.Children.Add(modelLabel);
        modelRow.Children.Add(_modelBox);
        top.Children.Add(modelRow);
        _baseUrlRow.Children.Add(new TextBlock { Text = "Base URL", FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });
        _baseUrlRow.Children.Add(_baseUrlBox);
        top.Children.Add(_baseUrlRow);
        top.Children.Add(_hint);
        if (!_settings.DataNoticeAccepted) top.Children.Add(DataNotice(top));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(6) };
        var buttons = new StackPanel();
        buttons.Children.Add(_send);
        buttons.Children.Add(_stop);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_input);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _scroll.Content = _transcript;
        root.Children.Add(_scroll);
        return root;
    }

    /// <summary>Nói rõ dữ liệu đi đâu trước khi dùng (quy tắc: luồng dữ liệu ra ngoài phải được nói rõ).</summary>
    private UIElement DataNotice(Panel parent)
    {
        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xE0)),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Text = "Câu hỏi của bạn và kết quả lệnh (tên, tham số phần tử trong mô hình) được gửi tới nhà cung cấp AI bạn chọn. " +
                   "Muốn dữ liệu không rời khỏi máy, hãy chọn Ollama hoặc LM Studio.",
        });
        var ok = new Button { Content = "Đã hiểu", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 4, 0, 0) };
        ok.Click += (_, _) =>
        {
            _settings.DataNoticeAccepted = true;
            _settings.Save(_host.DataDir);
            parent.Children.Remove(box);
        };
        panel.Children.Add(ok);
        box.Child = panel;
        return box;
    }

    private static Button SmallButton(string text, string tooltip) =>
        new() { Content = text, ToolTip = tooltip, MinWidth = 30, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1) };

    private void OnProviderChanged()
    {
        var preset = Preset;
        _settings.Provider = preset.Id;
        _modelBox.Items.Clear();
        _modelBox.Text = _settings.ModelFor(preset);
        _baseUrlBox.Text = _settings.BaseUrlFor(preset);
        _baseUrlRow.Visibility = preset.IsAnthropic ? Visibility.Collapsed : Visibility.Visible;
        _hint.Text = preset.Hint;
        ApplyProvider();
    }

    /// <summary>Tạo provider theo lựa chọn hiện tại. Đổi provider/model thì bắt đầu cuộc trò chuyện mới.</summary>
    private void ApplyProvider()
    {
        var preset = Preset;
        var model = _modelBox.Text.Trim();
        _settings.Models[preset.Id] = model;
        if (!preset.IsAnthropic) _settings.BaseUrls[preset.Id] = _baseUrlBox.Text.Trim();
        TrySaveSettings();

        var key = preset.NeedsKey || preset.Id == "custom" ? CredentialStore.Read(preset.Id) : null;
        _providerReady = false;
        if (preset.NeedsKey && string.IsNullOrEmpty(key)) { _status.Text = $"Cần API key cho {preset.Name}: bấm nút Key."; return; }
        if (model.Length == 0) { _status.Text = "Chọn model: bấm ↻ để tải danh sách, hoặc gõ tên model."; return; }

        _engine.Use(preset.IsAnthropic
            ? new AnthropicProvider(key!, model)
            : new OpenAiCompatibleProvider(_settings.BaseUrlFor(preset), key, model));
        _providerReady = true;
        _status.Text = $"{preset.Name} · {model}" + (preset.Local ? " · chạy trên máy" : "");
    }

    private async Task LoadModels()
    {
        var preset = Preset;
        var key = CredentialStore.Read(preset.Id);
        if (preset.NeedsKey && string.IsNullOrEmpty(key)) { _status.Text = "Nhập API key trước (nút Key)."; return; }
        try
        {
            _status.Text = "Đang tải danh sách model…";
            IChatProvider probe = preset.IsAnthropic
                ? new AnthropicProvider(key!, "")
                : new OpenAiCompatibleProvider(_baseUrlBox.Text.Trim(), key, "");
            var models = await probe.ListModelsAsync(CancellationToken.None);
            var current = _modelBox.Text;
            _modelBox.Items.Clear();
            foreach (var m in models) _modelBox.Items.Add(m);
            _modelBox.Text = current.Length > 0 ? current : models.FirstOrDefault() ?? "";
            _status.Text = $"{models.Count} model. Chọn trong danh sách.";
            _modelBox.IsDropDownOpen = models.Count > 0;
        }
        catch (Exception ex)
        {
            _status.Text = ex is ChatException ? ex.Message : $"Không tải được danh sách model: {ex.Message}";
        }
    }

    private void EditKey()
    {
        var preset = Preset;
        var window = new Window
        {
            Title = $"API key: {preset.Name}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = preset.Hint, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) });
        var box = new PasswordBox();
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "Key được lưu trong Windows Credential Manager của tài khoản này, không ghi ra file.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 6, 0, 8),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Lưu", Width = 70, IsDefault = true };
        var delete = new Button { Content = "Xóa key", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            if (box.Password.Trim().Length > 0) CredentialStore.Write(preset.Id, box.Password.Trim());
            window.Close();
        };
        delete.Click += (_, _) => { CredentialStore.Delete(preset.Id); window.Close(); };
        buttons.Children.Add(save);
        buttons.Children.Add(delete);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        ApplyProvider();
    }

    private async void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _running != null) return;
        if (!_providerReady) { ApplyProvider(); if (!_providerReady) return; }

        _input.Clear();
        AddBubble(text, user: true);
        _running = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await _engine.RunAsync(text, (kind, content) => Dispatcher.Invoke(() =>
            {
                switch (kind)
                {
                    case "assistant": AddBubble(content, user: false); break;
                    case "tool": AddLine("⚙ " + content, Muted); break;
                    case "tool_error": AddLine("⚠ " + content, ErrorBrush); break;
                }
            }), _running.Token);
            _status.Text = "Xong.";
        }
        catch (OperationCanceledException)
        {
            AddLine("Đã dừng.", Muted);
            _engine.Reset();
            _status.Text = "Đã dừng; bắt đầu lại cuộc trò chuyện để tránh lịch sử dang dở.";
        }
        catch (Exception ex)
        {
            AddLine(ex is ChatException ? ex.Message : $"Lỗi: {ex.Message}", ErrorBrush);
            _status.Text = "Có lỗi. Kiểm tra key, model hoặc kết nối rồi gửi lại.";
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _send.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        if (busy) _status.Text = "Đang xử lý…";
    }

    private void AddBubble(string text, bool user)
    {
        var bubble = new Border
        {
            Background = user ? UserBubble : AssistantBubble,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(user ? 30 : 0, 4, user ? 0 : 30, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            },
        };
        _transcript.Children.Add(bubble);
        _scroll.ScrollToEnd();
    }

    private void AddLine(string text, Brush brush)
    {
        _transcript.Children.Add(new TextBlock { Text = text, Foreground = brush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1, 2, 1) });
        _scroll.ScrollToEnd();
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(_host.DataDir); } catch (IOException) { }
    }
}
