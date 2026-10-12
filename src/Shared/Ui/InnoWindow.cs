using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace InnoPro.Ui;

/// <summary>
/// Cửa sổ tự vẽ: thanh tiêu đề, viền, nút đóng theo phong cách InnoPro (không dùng khung mặc định của Windows).
/// Đặt nội dung vào Body và các nút vào Footer.
/// </summary>
internal sealed class InnoWindow : Window
{
    private readonly ContentControl _body = new();
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _footerHost;

    public InnoWindow(string title, IntPtr owner = default, double width = 440, bool resizable = false)
    {
        Title = title;
        Width = width;
        SizeToContent = resizable ? SizeToContent.Manual : SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Theme.Surface;
        WindowStartupLocation = owner == default ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner != default) new WindowInteropHelper(this) { Owner = owner };
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 34,
            ResizeBorderThickness = new Thickness(resizable ? 5 : 0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Theme.Apply(this);

        var close = new Button { Content = "✕", Style = (Style)FindResource("Icon"), FontSize = 12, Padding = new Thickness(10, 2, 10, 2) };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.Click += (_, _) => Close();

        var header = new DockPanel { Height = 34, Background = Theme.SurfaceAlt };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new Border
        {
            Width = 4, Height = 14, CornerRadius = new CornerRadius(2), Background = Theme.Brand,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        _footerHost = new Border { Child = _footer, Padding = new Thickness(14, 10, 14, 12), Visibility = Visibility.Collapsed };
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_footerHost, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(_footerHost);
        layout.Children.Add(_body);

        Content = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Child = layout };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    public UIElement? Body
    {
        get => _body.Content as UIElement;
        set => _body.Content = value;
    }

    /// <summary>Thêm nút ở chân cửa sổ. primary: nút chính màu thương hiệu.</summary>
    public Button AddButton(string text, bool primary = false, Action? onClick = null, bool closes = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), MinWidth = 72 };
        if (primary) { button.Style = (Style)FindResource("Primary"); button.IsDefault = true; }
        button.Click += (_, _) =>
        {
            onClick?.Invoke();
            if (closes) Close();
        };
        _footer.Children.Add(button);
        _footerHost.Visibility = Visibility.Visible;
        return button;
    }

    /// <summary>Phần tử chen vào bên trái hàng nút (ví dụ dòng trạng thái).</summary>
    public void AddFooterLeft(UIElement element)
    {
        _footer.Children.Insert(0, element);
        _footerHost.Visibility = Visibility.Visible;
    }

    // ---- Hộp thoại dựng sẵn, thay cho TaskDialog mặc định ----

    public static void Message(string title, string text, IntPtr owner = default)
    {
        var w = new InnoWindow(title, owner);
        w.Body = Paragraph(text);
        w.AddButton("Đóng", primary: true, closes: true);
        w.ShowDialog();
    }

    /// <summary>Cho chọn một trong các lựa chọn; trả chỉ số, -1 nếu đóng.</summary>
    public static int Choose(string title, string text, IntPtr owner, params string[] options)
    {
        var w = new InnoWindow(title, owner);
        w.Body = Paragraph(text);
        var choice = -1;
        for (var i = options.Length - 1; i >= 0; i--)
        {
            var index = i;
            w.AddButton(options[i], primary: i == 0, onClick: () => choice = index, closes: true);
        }
        w.ShowDialog();
        return choice;
    }

    public static UIElement Paragraph(string text) => new ScrollViewer
    {
        MaxHeight = 460,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 14, 16, 4), LineHeight = 18 },
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
    };
}
