using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Cửa hàng lệnh cộng đồng: danh mục ở thư mục community/ của repo GitHub. Cài = tải một file .cs vào
/// thư mục Scripts rồi nạp lại (Revit tự biên dịch). Chỉ gọi mạng khi người dùng mở cửa hàng.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class StoreCommand : IExternalCommand
{
    private const string BaseUrl = "https://raw.githubusercontent.com/Innoprovn/vn-revit-mcp/main/community/";

    private sealed record Entry(string Id, string File, string Title, string Description, string Category, string Author)
    {
        public string FileName => Path.GetFileName(File);
        public string LocalPath => Path.Combine(StoreDir, FileName);
        public bool Installed => System.IO.File.Exists(LocalPath);
        public string Status => Installed ? "Đã cài" : "";
    }

    private static string StoreDir => Path.Combine(Paths.UserScriptsDir, "CongDong");

    private static readonly HttpClient Http = CreateHttp();

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        List<Entry> entries;
        try
        {
            var json = Fetch("index.json");
            using var doc = JsonDocument.Parse(json);
            entries = doc.RootElement.GetProperty("commands").EnumerateArray().Select(c => new Entry(
                c.GetProperty("id").GetString()!, c.GetProperty("file").GetString()!, c.GetProperty("title").GetString()!,
                c.GetProperty("description").GetString()!, c.GetProperty("category").GetString()!, c.GetProperty("author").GetString()!))
                .OrderBy(e => e.Category).ThenBy(e => e.Title).ToList();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("InnoPro: Cửa hàng lệnh", $"Không tải được danh mục (cần Internet): {ex.Message}");
            return Result.Cancelled;
        }

        ShowWindow(commandData.Application, entries);
        return Result.Succeeded;
    }

    private static void ShowWindow(UIApplication app, List<Entry> entries)
    {
        var window = new Window
        {
            Title = "InnoPro: Cửa hàng lệnh cộng đồng",
            Width = 760,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        new WindowInteropHelper(window) { Owner = app.MainWindowHandle };

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Lệnh", Width = 220, DisplayMemberBinding = new System.Windows.Data.Binding("Title") });
        grid.Columns.Add(new GridViewColumn { Header = "Nhóm", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Category") });
        grid.Columns.Add(new GridViewColumn { Header = "Tác giả", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Author") });
        grid.Columns.Add(new GridViewColumn { Header = "", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Status") });
        var list = new ListView { View = grid, ItemsSource = entries, Margin = new Thickness(8) };

        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0), Foreground = Brushes.DimGray };
        var view = new Button { Content = "Xem mã", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var install = new Button { Content = "Cài", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var remove = new Button { Content = "Gỡ", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var close = new Button { Content = "Đóng", Padding = new Thickness(10, 3, 10, 3), IsCancel = true };
        close.Click += (_, _) => window.Close();

        void Refresh()
        {
            list.Items.Refresh();
            var e = list.SelectedItem as Entry;
            detail.Text = e == null ? "Chọn một lệnh để xem mô tả." : $"{e.Description}\nAI gọi bằng: {e.Id}";
            view.IsEnabled = install.IsEnabled = e != null;
            install.Content = e?.Installed == true ? "Cài lại" : "Cài";
            remove.IsEnabled = e?.Installed == true;
        }
        list.SelectionChanged += (_, _) => Refresh();

        view.Click += (_, _) =>
        {
            if (list.SelectedItem is Entry e) ShowCode(window, e, Fetch(e.File), allowInstall: false);
        };
        install.Click += (_, _) =>
        {
            if (list.SelectedItem is not Entry e) return;
            try
            {
                var code = Fetch(e.File);
                // Luôn cho người dùng xem mã trước khi cài: mã này sẽ chạy trong Revit.
                if (!ShowCode(window, e, code, allowInstall: true)) return;
                Directory.CreateDirectory(StoreDir);
                System.IO.File.WriteAllText(e.LocalPath, code, new UTF8Encoding(false));
                var result = App.Instance!.ReloadCommands();
                var errors = result.Errors.Where(x => x.Source.Contains(e.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                MessageBox.Show(window, errors.Count == 0
                        ? $"Đã cài \"{e.Title}\". Lệnh có trong nhóm {e.Category} trên tab InnoPro, AI gọi bằng {e.Id}."
                        : "Đã tải nhưng biên dịch lỗi:\n" + string.Join("\n", errors.Select(x => x.Message)),
                    "InnoPro");
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, $"Không cài được: {ex.Message}", "InnoPro");
            }
            Refresh();
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not Entry e || !e.Installed) return;
            System.IO.File.Delete(e.LocalPath);
            App.Instance!.ReloadCommands();
            Refresh();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(view);
        buttons.Children.Add(install);
        buttons.Children.Add(remove);
        buttons.Children.Add(close);

        var footer = new StackPanel();
        footer.Children.Add(detail);
        footer.Children.Add(new TextBlock
        {
            Text = "Lệnh cộng đồng được duyệt qua pull request trên GitHub. Muốn đóng góp: xem community/README.md trong repo.",
            FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(8, 4, 8, 0), TextWrapping = TextWrapping.Wrap,
        });
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(list);
        window.Content = root;
        Refresh();
        window.ShowDialog();
    }

    /// <summary>Hiện mã nguồn; với allowInstall trả true nếu người dùng bấm Cài.</summary>
    private static bool ShowCode(Window owner, Entry entry, string code, bool allowInstall)
    {
        var window = new Window
        {
            Title = $"Mã nguồn: {entry.FileName}",
            Width = 820,
            Height = 600,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        if (allowInstall)
        {
            var ok = new Button { Content = "Cài lệnh này", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            ok.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(ok);
        }
        var cancel = new Button { Content = allowInstall ? "Hủy" : "Đóng", Padding = new Thickness(10, 3, 10, 3), IsCancel = true };
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        window.Content = root;
        return window.ShowDialog() == true;
    }

    private static string Fetch(string relative) =>
        Task.Run(() => Http.GetStringAsync(BaseUrl + relative)).GetAwaiter().GetResult();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("vn-revit-mcp/" + App.Version);
        return http;
    }
}
