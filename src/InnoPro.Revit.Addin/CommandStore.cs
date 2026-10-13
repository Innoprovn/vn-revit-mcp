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
/// Cửa hàng: tiện ích marketplace (bộ lệnh .zip, xem Marketplace) và lệnh cộng đồng (file .cs) trong community/ của repo.
/// Lệnh .cs: tải vào thư mục Scripts rồi nạp lại (Revit tự biên dịch), luôn cho xem mã trước khi cài. Chỉ gọi mạng khi mở cửa hàng.
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

    /// <summary>Một dòng trong cửa hàng: tiện ích marketplace (bộ lệnh .zip) hoặc lệnh lẻ (file .cs).</summary>
    private sealed class Item
    {
        public Entry? Script { get; init; }
        public Marketplace.Extension? Extension { get; init; }
        public string Kind => Extension != null ? "Tiện ích" : "Lệnh";
        public string Title => Extension?.Name ?? Script!.Title;
        public string Category => Extension?.Category ?? Script!.Category;
        public string Author => Extension?.Author ?? Script!.Author;
        public string Description => Extension != null
            ? $"{Extension.Description}\nPhiên bản {Extension.Version} · {Extension.SizeKb} KB · lệnh: {string.Join(", ", Extension.Commands)}"
            : Script!.Description;
        public bool Installed => Extension != null ? Extension.InstalledVersion != null : Script!.Installed;
        public string Status => Extension == null ? Script!.Status
            : Extension.UpdateAvailable ? "Có bản mới" : Extension.InstalledVersion != null ? "Đã cài" : "";
    }

    private static void ShowWindow(UIApplication app, List<Entry> entries)
    {
        List<Marketplace.Extension> extensions;
        try { extensions = Marketplace.Fetch(); }
        catch (Exception ex) { Log.Error("Không tải được danh mục tiện ích", ex); extensions = []; }
        var items = extensions.Select(e => new Item { Extension = e })
            .Concat(entries.Select(e => new Item { Script = e })).ToList();

        var window = new InnoWindow("Cửa hàng", app.MainWindowHandle, width: 720, resizable: true) { Height = 460 };

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Tên", Width = 240, DisplayMemberBinding = new System.Windows.Data.Binding("Title") });
        grid.Columns.Add(new GridViewColumn { Header = "Loại", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding("Kind") });
        grid.Columns.Add(new GridViewColumn { Header = "Nhóm", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Category") });
        grid.Columns.Add(new GridViewColumn { Header = "Tác giả", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding("Author") });
        grid.Columns.Add(new GridViewColumn { Header = "", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Status") });
        var list = new ListView { View = grid, ItemsSource = items, Margin = new Thickness(10, 8, 10, 0) };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(14, 8, 14, 0) };

        var body = new DockPanel();
        DockPanel.SetDock(detail, Dock.Bottom);
        body.Children.Add(detail);
        body.Children.Add(list);
        window.Body = body;

        Button? remove = null, install = null, view = null;
        var owner = new Func<IntPtr>(() => new WindowInteropHelper(window).Handle);
        void Refresh()
        {
            list.Items.Refresh();
            var it = list.SelectedItem as Item;
            detail.Text = it?.Description ?? "";
            install!.IsEnabled = it != null;
            install.Content = it?.Extension?.UpdateAvailable == true ? "Cập nhật" : it?.Installed == true ? "Cài lại" : "Cài";
            remove!.IsEnabled = it?.Installed == true;
            view!.Visibility = it?.Script != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        view = window.AddButton("Xem mã", onClick: () =>
        {
            if (list.SelectedItem is Item { Script: { } e }) ShowCode(window, e, Fetch(e.File), allowInstall: false);
        });
        remove = window.AddButton("Gỡ", onClick: () =>
        {
            if (list.SelectedItem is not Item { Installed: true } it) return;
            try
            {
                if (it.Extension != null) Marketplace.Remove(it.Extension.Id);
                else System.IO.File.Delete(it.Script!.LocalPath);
                App.Instance!.ReloadCommands();
            }
            catch (Exception ex) { InnoWindow.Message("Cửa hàng", $"Không gỡ được: {ex.Message}", owner()); }
            Refresh();
        });
        install = window.AddButton("Cài", primary: true, onClick: () =>
        {
            if (list.SelectedItem is not Item it) return;
            try
            {
                if (it.Extension is { } ext)
                {
                    Marketplace.Install(ext);
                    var result = App.Instance!.ReloadCommands();
                    InnoWindow.Message("Cửa hàng", $"Đã cài {ext.Name} {ext.Version}: {ext.Commands.Length} lệnh mới trên tab InnoPro.", owner());
                }
                else
                {
                    var e = it.Script!;
                    var code = Fetch(e.File);
                    // Luôn cho xem mã trước khi cài: mã này sẽ chạy trong Revit.
                    if (!ShowCode(window, e, code, allowInstall: true)) return;
                    Directory.CreateDirectory(StoreDir);
                    System.IO.File.WriteAllText(e.LocalPath, code, new UTF8Encoding(false));
                    var result = App.Instance!.ReloadCommands();
                    var errors = result.Errors.Where(x => x.Source.Contains(e.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
                    InnoWindow.Message("Cửa hàng", errors.Count == 0
                        ? $"Đã cài: {e.Title}"
                        : "Lỗi biên dịch:\n" + string.Join("\n", errors.Select(x => x.Message)), owner());
                }
            }
            catch (Exception ex)
            {
                InnoWindow.Message("Cửa hàng", $"Không cài được: {ex.Message}", owner());
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
