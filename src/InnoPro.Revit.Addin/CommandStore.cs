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
using InnoPro.Ui;
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
            InnoWindow.Message("Cửa hàng lệnh", $"Không tải được danh mục (cần Internet): {ex.Message}", commandData.Application.MainWindowHandle);
            return Result.Cancelled;
        }

        ShowWindow(commandData.Application, entries);
        return Result.Succeeded;
    }

    private static void ShowWindow(UIApplication app, List<Entry> entries)
    {
        var window = new InnoWindow("Cửa hàng lệnh", app.MainWindowHandle, width: 680, resizable: true) { Height = 440 };

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Lệnh", Width = 250, DisplayMemberBinding = new System.Windows.Data.Binding("Title") });
        grid.Columns.Add(new GridViewColumn { Header = "Nhóm", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Category") });
        grid.Columns.Add(new GridViewColumn { Header = "Tác giả", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Author") });
        grid.Columns.Add(new GridViewColumn { Header = "", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Status") });
        var list = new ListView { View = grid, ItemsSource = entries, Margin = new Thickness(10, 8, 10, 0) };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(14, 8, 14, 0) };

        var body = new DockPanel();
        DockPanel.SetDock(detail, Dock.Bottom);
        body.Children.Add(detail);
        body.Children.Add(list);
        window.Body = body;

        Button? remove = null, install = null, view = null;
        void Refresh()
        {
            list.Items.Refresh();
            var e = list.SelectedItem as Entry;
            detail.Text = e?.Description ?? "";
            view!.IsEnabled = install!.IsEnabled = e != null;
            install.Content = e?.Installed == true ? "Cài lại" : "Cài";
            remove!.IsEnabled = e?.Installed == true;
        }

        view = window.AddButton("Xem mã", onClick: () =>
        {
            if (list.SelectedItem is Entry e) ShowCode(window, e, Fetch(e.File), allowInstall: false);
        });
        remove = window.AddButton("Gỡ", onClick: () =>
        {
            if (list.SelectedItem is not Entry e || !e.Installed) return;
            System.IO.File.Delete(e.LocalPath);
            App.Instance!.ReloadCommands();
            Refresh();
        });
        install = window.AddButton("Cài", primary: true, onClick: () =>
        {
            if (list.SelectedItem is not Entry e) return;
            try
            {
                var code = Fetch(e.File);
                // Luôn cho xem mã trước khi cài: mã này sẽ chạy trong Revit.
                if (!ShowCode(window, e, code, allowInstall: true)) return;
                Directory.CreateDirectory(StoreDir);
                System.IO.File.WriteAllText(e.LocalPath, code, new UTF8Encoding(false));
                var result = App.Instance!.ReloadCommands();
                var errors = result.Errors.Where(x => x.Source.Contains(e.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                InnoWindow.Message("Cửa hàng lệnh", errors.Count == 0
                    ? $"Đã cài: {e.Title}"
                    : "Lỗi biên dịch:\n" + string.Join("\n", errors.Select(x => x.Message)), new WindowInteropHelper(window).Handle);
            }
            catch (Exception ex)
            {
                InnoWindow.Message("Cửa hàng lệnh", $"Không cài được: {ex.Message}", new WindowInteropHelper(window).Handle);
            }
            Refresh();
        });

        list.SelectionChanged += (_, _) => Refresh();
        Refresh();
        window.ShowDialog();
    }

    /// <summary>Hiện mã nguồn; với allowInstall trả true nếu người dùng bấm Cài.</summary>
    private static bool ShowCode(Window owner, Entry entry, string code, bool allowInstall)
    {
        var window = new InnoWindow(entry.FileName, new WindowInteropHelper(owner).Handle, width: 780, resizable: true) { Height = 560 };
        window.Body = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var accepted = false;
        window.AddButton(allowInstall ? "Hủy" : "Đóng", primary: !allowInstall, closes: true);
        if (allowInstall) window.AddButton("Cài", primary: true, onClick: () => accepted = true, closes: true);
        window.ShowDialog();
        return accepted;
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
