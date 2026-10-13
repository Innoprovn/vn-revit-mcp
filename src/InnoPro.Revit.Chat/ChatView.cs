using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InnoPro.Bridge;
using InnoPro.Ui;

namespace InnoPro.Revit.Chat;

/// <summary>Điểm vào của module: host gọi qua reflection để lấy giao diện đặt vào khung (dockable pane).</summary>
public static class ChatModule
{
    public static FrameworkElement CreateView(IRevitHost host) => new ChatView(host);
}

/// <summary>Khung chat AI trong Revit: gọn, tự vẽ theo theme InnoPro.</summary>
internal sealed class ChatView : UserControl
{
    private static readonly Brush UserBubble = new SolidColorBrush(Color.FromRgb(0xDC, 0xEB, 0xFA));
    private static readonly Brush AssistantBubble = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF7));

    private readonly IRevitHost _host;
    private readonly ChatEngine _engine;
    private readonly ClaudeCodeRunner _claude = new();
    private readonly ChatSettings _settings;

    private readonly ComboBox _providerBox = new();
    private readonly ComboBox _modelBox = new() { IsEditable = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _baseUrlBox = new() { Margin = new Thickness(0, 6, 0, 0), ToolTip = "Base URL" };
    private readonly TextBlock _hint = new() { FontSize = 11, Foreground = Theme.Muted, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _transcript = new() { Margin = new Thickness(8, 6, 8, 6) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _chatArea = new();
    private readonly StackPanel _desktopCard = new() { Margin = new Thickness(16, 24, 16, 0), Visibility = Visibility.Collapsed };
    private readonly DockPanel _inputBar = new() { Margin = new Thickness(8) };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _send = new() { Content = "Gửi", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = Theme.Muted, Margin = new Thickness(10, 0, 10, 4), TextTrimming = TextTrimming.CharacterEllipsis };

    private CancellationTokenSource? _running;
    private bool _providerReady;

    public ChatView(IRevitHost host)
    {
        _host = host;
        _engine = new ChatEngine(host);
        _settings = ChatSettings.Load(host.DataDir);
        Theme.Apply(this);
        Background = Theme.Surface;
        Content = Build();
        _send.Style = (Style)FindResource("Primary");

        foreach (var preset in ProviderPresets.All) _providerBox.Items.Add(preset.Name);
        _providerBox.SelectedIndex = Math.Max(0, ProviderPresets.All.ToList().FindIndex(p => p.Id == _settings.Provider));
        _providerBox.SelectionChanged += (_, _) => OnProviderChanged();
        _modelBox.LostFocus += (_, _) => ApplyProvider();
        _modelBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyProvider);
        _baseUrlBox.LostFocus += (_, _) => ApplyProvider();
        _send.Click += (_, _) => { if (_running != null) _running.Cancel(); else Send(); };
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Send(); }
        };

        OnProviderChanged();
    }

    private ProviderPreset Preset => ProviderPresets.All[Math.Max(0, _providerBox.SelectedIndex)];

    private UIElement Build()
    {
        var root = new DockPanel();

        var top = new StackPanel { Margin = new Thickness(8, 8, 8, 4) };
        var row = new DockPanel();
        foreach (var (icon, tip, action) in new (string, string, Action)[]
                 {
                     ("plus", "Cuộc trò chuyện mới", () => { _engine.Reset(); _claude.Reset(); _transcript.Children.Clear(); _status.Text = ""; ShowWelcome(); }),
                     ("key", "API key", EditKey),
                     ("reload", "Tải danh sách model", async () => await LoadModels()),
                 })
        {
            var b = new Button { Content = Icons.Shape(icon, 15), ToolTip = tip, Style = (Style)FindResource("Icon"), Margin = new Thickness(2, 0, 0, 0) };
            b.Click += (_, _) => action();
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
        }
        row.Children.Add(_providerBox);
        top.Children.Add(row);
        top.Children.Add(_modelBox);
        top.Children.Add(_baseUrlBox);
        top.Children.Add(_hint);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 4, 0, 0) }.Also(b => DockPanel.SetDock(b, Dock.Top)));

        DockPanel.SetDock(_send, Dock.Right);
        _inputBar.Children.Add(_send);
        _inputBar.Children.Add(_input);
        DockPanel.SetDock(_inputBar, Dock.Bottom);
        root.Children.Add(_inputBar);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        _scroll.Content = _transcript;
        _chatArea.Children.Add(_scroll);
        BuildDesktopCard();
        _chatArea.Children.Add(_desktopCard);
        root.Children.Add(_chatArea);
        return root;
    }

    /// <summary>Tài khoản Claude (Free/Pro/Max): không gọi API, chỉ nối Claude Desktop với Revit qua MCP rồi mở ứng dụng.</summary>
    private void BuildDesktopCard()
    {
        _desktopCard.Children.Add(new TextBlock { Text = "Claude Desktop", FontSize = 15, FontWeight = FontWeights.SemiBold });
        _desktopCard.Children.Add(new TextBlock
        {
            Text = "Hỏi trong Claude Desktop, Claude tự làm việc với Revit.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
        });
        var open = new Button { Content = "Kết nối và mở Claude", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += async (_, _) => await OpenClaudeDesktop();
        _desktopCard.Children.Add(open);
    }

    private async Task OpenClaudeDesktop()
    {
        _status.Text = "Đang kết nối…";
        var exe = Path.Combine(_host.DataDir, "current", "server", "vn-revit-mcp.exe");
        var restart = false;
        try
        {
            var output = await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "setup --clients claude-desktop --json")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
                })!;
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60_000);
                return text;
            });
            using var doc = JsonDocument.Parse(output);
            var client = doc.RootElement.GetProperty("clients").EnumerateArray().FirstOrDefault();
            if (client.ValueKind == JsonValueKind.Object && client.TryGetProperty("action", out var act) && act.GetString() == "not_found")
            {
                Process.Start(new ProcessStartInfo("https://claude.ai/download") { UseShellExecute = true });
                _status.Text = "Chưa có Claude Desktop: tải về, cài, rồi bấm lại.";
                return;
            }
            restart = client.ValueKind == JsonValueKind.Object && client.TryGetProperty("action", out var a) && a.GetString() is "configured" or "updated";
        }
        catch (Exception ex)
        {
            _status.Text = $"Không kết nối được: {ex.Message}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("claude://") { UseShellExecute = true });
            _status.Text = restart ? "Đã kết nối. Nếu Claude đang mở, thoát hẳn rồi mở lại." : "Đã mở Claude Desktop.";
        }
        catch
        {
            Process.Start(new ProcessStartInfo("https://claude.ai/download") { UseShellExecute = true });
            _status.Text = "Chưa có Claude Desktop: tải về, cài, rồi bấm lại.";
        }
    }

    private void OnProviderChanged()
    {
        var preset = Preset;
        _settings.Provider = preset.Id;
        _modelBox.Items.Clear();
        _modelBox.Text = _settings.ModelFor(preset);
        _baseUrlBox.Text = _settings.BaseUrlFor(preset);
        _hint.Text = preset.Hint;

        var desktop = preset.IsDesktop;
        _modelBox.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
        _baseUrlBox.Visibility = preset.Id == "custom" ? Visibility.Visible : Visibility.Collapsed;
        _desktopCard.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
        _inputBar.Visibility = desktop ? Visibility.Collapsed : Visibility.Visible;
        ApplyProvider();
    }

    /// <summary>Tạo provider theo lựa chọn hiện tại. Đổi provider/model thì bắt đầu cuộc trò chuyện mới.</summary>
    private void ApplyProvider()
    {
        var preset = Preset;
        _providerReady = false;
        _status.Text = "";
        TrySaveSettings();
        if (preset.IsDesktop) return;

        var model = _modelBox.Text.Trim();
        _settings.Models[preset.Id] = model;
        if (preset.IsClaudeCode)
        {
            TrySaveSettings();
            _claude.Reset();
            if (ClaudeCodeRunner.FindClaude() == null) { _status.Text = "Chưa có Claude Code trên máy"; ShowWelcome(); return; }
            if (model.Length == 0) _modelBox.Text = model = "sonnet";
            _providerReady = true;
            _status.Text = $"Claude Code · {model}";
            ShowWelcome();
            return;
        }
        if (preset.Id == "custom") _settings.BaseUrls[preset.Id] = _baseUrlBox.Text.Trim();
        TrySaveSettings();

        var key = CredentialStore.Read(preset.Id);
        if (preset.NeedsKey && string.IsNullOrEmpty(key)) { _status.Text = "Cần API key"; ShowWelcome(); return; }
        if (model.Length == 0) { _status.Text = "Chọn model (nút tải danh sách)"; ShowWelcome(); return; }

        _engine.Use(preset.IsAnthropic
            ? new AnthropicProvider(key!, model)
            : new OpenAiCompatibleProvider(_settings.BaseUrlFor(preset), key, model));
        _providerReady = true;
        _status.Text = preset.Local ? $"{model} · trên máy" : model;
        ShowWelcome();
    }

    /// <summary>Hướng dẫn 3 bước khi chưa có tin nhắn; tự biến mất khi bắt đầu hỏi.</summary>
    private void ShowWelcome()
    {
        if (_transcript.Children.Count > 0 && _transcript.Children[0] is not FrameworkElement { Tag: "welcome" }) return;
        _transcript.Children.Clear();
        var preset = Preset;
        var steps = new (string Icon, string Text, bool Done)[]
        {
            ("chat", "Chọn AI ở ô phía trên", true),
            ("key", preset.NeedsKey ? "Nhập API key (nút chìa khóa)" : "Không cần API key", !preset.NeedsKey || !string.IsNullOrEmpty(CredentialStore.Read(preset.Id))),
            ("send", "Hỏi bằng tiếng Việt, Enter để gửi", _providerReady),
        };
        var panel = new StackPanel { Tag = "welcome", Margin = new Thickness(6, 14, 6, 0) };
        foreach (var (icon, text, done) in steps)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(Icons.Shape(done ? "check" : icon, 16, done ? Theme.Brand : Theme.Muted));
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = done ? Theme.Text : Theme.Muted });
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "Ví dụ: Mô hình có bao nhiêu cột, cột nào bị trùng?", Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 4, 0, 0) });
        _transcript.Children.Add(panel);
    }

    private async Task LoadModels()
    {
        var preset = Preset;
        if (preset.IsDesktop) return;
        if (preset.IsClaudeCode)
        {
            _modelBox.Items.Clear();
            foreach (var m in ClaudeCodeRunner.Models) _modelBox.Items.Add(m);
            _modelBox.IsDropDownOpen = true;
            return;
        }
        var key = CredentialStore.Read(preset.Id);
        if (preset.NeedsKey && string.IsNullOrEmpty(key)) { _status.Text = "Cần API key"; return; }
        try
        {
            _status.Text = "Đang tải model…";
            IChatProvider probe = preset.IsAnthropic
                ? new AnthropicProvider(key!, "")
                : new OpenAiCompatibleProvider(_settings.BaseUrlFor(preset), key, "");
            var models = await probe.ListModelsAsync(CancellationToken.None);
            var current = _modelBox.Text;
            _modelBox.Items.Clear();
            foreach (var m in models) _modelBox.Items.Add(m);
            _modelBox.Text = current.Length > 0 ? current : models.FirstOrDefault() ?? "";
            _status.Text = $"{models.Count} model";
            _modelBox.IsDropDownOpen = models.Count > 0;
        }
        catch (Exception ex)
        {
            _status.Text = ex is ChatException ? ex.Message : $"Không tải được model: {ex.Message}";
        }
    }

    private void EditKey()
    {
        var preset = Preset;
        if (preset.IsDesktop) return;
        var window = new InnoWindow($"API key · {preset.Name}", width: 380);
        var box = new PasswordBox();
        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 4) };
        panel.Children.Add(InnoWindow.Hint(preset.Hint));
        panel.Children.Add(box.Also(b => b.Margin = new Thickness(0, 8, 0, 0)));
        window.Body = panel;
        window.AddButton("Xóa key", onClick: () => CredentialStore.Delete(preset.Id), closes: true);
        window.AddButton("Lưu", primary: true, onClick: () =>
        {
            if (box.Password.Trim().Length > 0) CredentialStore.Write(preset.Id, box.Password.Trim());
        }, closes: true);
        window.Loaded += (_, _) => box.Focus();
        window.ShowDialog();
        ApplyProvider();
    }

    private async void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _running != null) return;
        if (!_providerReady) { ApplyProvider(); if (!_providerReady) return; }

        _input.Clear();
        if (_transcript.Children.Count == 1 && _transcript.Children[0] is FrameworkElement { Tag: "welcome" }) _transcript.Children.Clear();
        if (_transcript.Children.Count == 0 && !Preset.Local)
            AddLine($"Câu hỏi và dữ liệu lệnh được gửi tới {Preset.Name}.", Theme.Muted);
        AddBubble(text, user: true);
        _running = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            Action<string, string> show = (kind, content) => Dispatcher.Invoke(() =>
            {
                switch (kind)
                {
                    case "assistant": AddBubble(PlainText.From(content), user: false); break;
                    case "tool": AddLine("· " + content, Theme.Muted); break;
                    case "tool_error": AddLine("! " + content, Theme.Danger); break;
                }
            });
            if (Preset.IsClaudeCode) await _claude.RunAsync(text, _modelBox.Text.Trim(), show, _running.Token);
            else await _engine.RunAsync(text, show, _running.Token);
            _status.Text = "";
        }
        catch (OperationCanceledException)
        {
            AddLine("Đã dừng.", Theme.Muted);
            _engine.Reset();
            _claude.Reset();
        }
        catch (Exception ex)
        {
            AddLine(ex is ChatException ? ex.Message : $"Lỗi: {ex.Message}", Theme.Danger);
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
        _send.Content = busy ? "Dừng" : "Gửi";
        _status.Text = busy ? "Đang xử lý…" : _status.Text;
    }

    private void AddBubble(string text, bool user)
    {
        _transcript.Children.Add(new Border
        {
            Background = user ? UserBubble : AssistantBubble,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(user ? 36 : 0, 4, user ? 0 : 36, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            },
        });
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

internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
