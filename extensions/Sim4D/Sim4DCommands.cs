using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;

namespace BIMTech4D
{
    /// <summary>Phần dùng chung giữa các lệnh 4D.</summary>
    internal static class Sim4D
    {
        public const string Category = "Mô phỏng 4D";
        public static RevitBridge Bridge;
        public static MainWindow Window;
        public static SimSession Session;

        public static string D(DateTime d) => d.ToString("yyyy-MM-dd");

        public static DateTime ParseDate(string s, string name) =>
            DateTime.TryParseExact(s?.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.Date
                : throw new CommandException($"'{name}' phải là ngày dạng yyyy-MM-dd (nhận được: {s}).");

        public static object Summary(IEnumerable<Task4D> tasks) => Table.Of(tasks.Select((t, i) => new
        {
            index = i, name = t.Name, start = D(t.Start), end = D(t.End), type = Task4D.TypeLabel(t.Type), elements = t.Count,
        }));
    }

    /// <summary>Nút ribbon: mở cửa sổ tiến độ + mô phỏng (dùng song song với mô hình).</summary>
    public sealed class Sim4DOpen : RevitCommandBase
    {
        public override string Name => "sim4d_open";
        public override string Category => Sim4D.Category;
        public override RibbonButton Ribbon => new("Mô phỏng\n4D", "layers");
        public override string Description => "Mở cửa sổ mô phỏng 4D: bảng tiến độ, Gantt, chạy mô phỏng theo ngày ngay trong Revit.";

        public override object PromptArguments(RevitCommandContext context) => new { };

        public override object Execute(RevitCommandContext context, JsonElement arguments)
        {
            var doc = context.Document;
            if (doc.IsFamilyDocument) throw new CommandException("Hãy mở một file dự án (.rvt).");
            if (Sim4D.Window != null)
            {
                if (!Sim4D.Window.Doc.Equals(doc))
                    throw new CommandException($"Cửa sổ 4D đang mở cho \"{Sim4D.Window.Doc.Title}\"; đóng cửa sổ đó trước.");
                Sim4D.Window.Activate();
                return new { opened = true, tasks = TaskStore.Load(doc).Count };
            }
            Sim4D.Bridge ??= new RevitBridge();
            var tasks = TaskStore.Load(doc);
            var window = new MainWindow(Sim4D.Bridge, doc, tasks, AutoGen.ParameterNames(doc, AutoGen.ModelElements(doc)), AutoGen.Filters(doc));
            new WindowInteropHelper(window).Owner = context.UIApplication.MainWindowHandle;
            Sim4D.Window = window;
            window.Closed += (s, e) => Sim4D.Window = null;
            window.Show();
            return new { opened = true, tasks = tasks.Count };
        }
    }

    public sealed class Sim4DTasks : RevitCommandBase
    {
        public override string Name => "sim4d_tasks";
        public override string Category => Sim4D.Category;
        public override string Description => "Danh sách công việc tiến độ 4D đang lưu trong file: tên, ngày bắt đầu/kết thúc, loại, số phần tử.";

        public override object Execute(RevitCommandContext context, JsonElement arguments)
        {
            var tasks = TaskStore.Load(context.Document);
            if (tasks.Count == 0) return new { tasks = 0, hint = "Chưa có tiến độ. Dùng sim4d_generate (tự sinh) hoặc sim4d_set_tasks." };
            return new { start = Sim4D.D(tasks.Min(t => t.Start)), end = Sim4D.D(tasks.Max(t => t.End)), tasks = Sim4D.Summary(tasks) };
        }
    }

    public sealed class Sim4DSetTasks : RevitCommandBase
    {
        public override string Name => "sim4d_set_tasks";
        public override string Category => Sim4D.Category;
        public override TransactionMode TransactionMode => TransactionMode.Manual;
        public override string Description =>
            "Ghi tiến độ 4D vào file: danh sách công việc {name, start, end (yyyy-MM-dd), type (xây dựng/phá dỡ/tạm thời), element_ids}. " +
            "mode=replace thay toàn bộ, append thêm vào cuối. Lấy element_ids bằng find_elements output=ids.";
        public override string InputSchema => """
            {
              "type": "object",
              "properties": {
                "tasks": { "type": "array", "items": { "type": "object", "properties": {
                  "name": { "type": "string" }, "start": { "type": "string" }, "end": { "type": "string" },
                  "type": { "type": "string" }, "element_ids": { "type": "array", "items": { "type": "integer" } } },
                  "required": ["name", "start", "end"] } },
                "mode": { "type": "string", "enum": ["replace", "append"], "description": "Mặc định replace." }
              },
              "required": ["tasks"]
            }
            """;

        public override object Execute(RevitCommandContext context, JsonElement arguments)
        {
            var doc = context.Document;
            var incoming = new List<Task4D>();
            var missing = 0;
            foreach (var t in arguments.GetProperty("tasks").EnumerateArray())
            {
                var task = new Task4D
                {
                    Name = t.GetProperty("name").GetString(),
                    Start = Sim4D.ParseDate(t.GetProperty("start").GetString(), "start"),
                    End = Sim4D.ParseDate(t.GetProperty("end").GetString(), "end"),
                    Type = Task4D.ParseType(t.TryGetProperty("type", out var ty) ? ty.GetString() : null),
                };
                if (task.End < task.Start) throw new CommandException($"'{task.Name}': ngày kết thúc trước ngày bắt đầu.");
                if (t.TryGetProperty("element_ids", out var ids))
                    foreach (var id in ids.EnumerateArray().Select(x => x.GetInt64()))
                    {
                        if (doc.GetElement(new ElementId(id)) == null) { missing++; continue; }
                        task.ElementIds.Add(id);
                    }
                incoming.Add(task);
            }
            var all = Args.GetStringOrDefault(arguments, "mode", "replace") == "append" ? TaskStore.Load(doc).Concat(incoming).ToList() : incoming;
            TaskStore.Save(doc, all);
            return new { saved = all.Count, added = incoming.Count, missing_ids = missing > 0 ? missing : (int?)null, tasks = Sim4D.Summary(all) };
        }
    }

    public sealed class Sim4DGenerate : RevitCommandBase
    {
        public override string Name => "sim4d_generate";
        public override string Category => Sim4D.Category;
        public override TransactionMode TransactionMode => TransactionMode.Manual;
        public override string Description =>
            "Tự sinh tiến độ 4D theo hạng mục (category), theo một tham số, hoặc theo filter mẫu; chia theo tầng " +
            "(dầm sàn tầng dưới trước, cột vách lên tầng trên). Mỗi công việc kéo dài 'days' ngày nối tiếp từ 'start'. Thay tiến độ cũ.";
        public override string InputSchema => """
            {
              "type": "object",
              "properties": {
                "group_by": { "type": "string", "enum": ["category", "parameter", "filter"], "description": "Mặc định category." },
                "parameter": { "type": "string", "description": "Tên tham số khi group_by=parameter." },
                "filters": { "type": "array", "items": { "type": "string" }, "description": "Tên filter mẫu theo thứ tự thi công khi group_by=filter." },
                "by_level": { "type": "boolean", "description": "Chia theo tầng, mặc định true." },
                "start": { "type": "string", "description": "Ngày bắt đầu yyyy-MM-dd, mặc định hôm nay." },
                "days": { "type": "integer", "description": "Số ngày mỗi công việc, mặc định 7." },
                "dry_run": { "type": "boolean", "description": "Chỉ xem trước, không lưu." }
              }
            }
            """;

        public override object Execute(RevitCommandContext context, JsonElement arguments)
        {
            var doc = context.Document;
            var options = new GenOptions
            {
                Mode = Args.GetStringOrDefault(arguments, "group_by", "category") switch
                {
                    "parameter" => GroupMode.Parameter, "filter" => GroupMode.Filter, _ => GroupMode.Category,
                },
                ParamName = Args.GetStringOrDefault(arguments, "parameter"),
                ByLevel = Args.GetBoolOrDefault(arguments, "by_level", true),
                Start = Args.GetStringOrDefault(arguments, "start") is { } s ? Sim4D.ParseDate(s, "start") : DateTime.Today,
                Days = (int)Args.GetLongOrDefault(arguments, "days", 7),
            };
            if (options.Mode == GroupMode.Filter)
            {
                var available = AutoGen.Filters(doc);
                foreach (var name in Args.GetStringsOrEmpty(arguments, "filters"))
                    options.FilterIds.Add(available.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Id
                        ?? throw new CommandException($"Không có filter '{name}'. Có: {string.Join(", ", available.Select(f => f.Name))}."));
            }

            List<Task4D> tasks;
            int skipped;
            try { (tasks, skipped) = AutoGen.Build(doc, options); }
            catch (InvalidOperationException ex) { throw new CommandException(ex.Message); }
            var dry = Args.GetBoolOrDefault(arguments, "dry_run", false);
            if (!dry) TaskStore.Save(doc, tasks);
            return new
            {
                saved = !dry, count = tasks.Count, skipped_elements = skipped,
                start = tasks.Count > 0 ? Sim4D.D(tasks.Min(t => t.Start)) : null, end = tasks.Count > 0 ? Sim4D.D(tasks.Max(t => t.End)) : null,
                tasks = Sim4D.Summary(tasks),
            };
        }
    }

    public sealed class Sim4DShow : RevitCommandBase
    {
        public override string Name => "sim4d_show";
        public override string Category => Sim4D.Category;
        public override TransactionMode TransactionMode => TransactionMode.Manual;
        public override string Description =>
            "Hiển thị mô hình tại một ngày theo tiến độ 4D trong view 3D \"4D Simulation\": đã xong hiện bình thường, đang làm tô màu, chưa làm ẩn. " +
            "reset=true trả view về bình thường.";
        public override string InputSchema => """
            {
              "type": "object",
              "properties": {
                "date": { "type": "string", "description": "yyyy-MM-dd; mặc định hôm nay." },
                "reset": { "type": "boolean", "description": "Tắt mô phỏng, trả view về bình thường." }
              }
            }
            """;

        public override object Execute(RevitCommandContext context, JsonElement arguments)
        {
            var doc = context.Document;
            if (Args.GetBoolOrDefault(arguments, "reset", false))
            {
                Sim4D.Session?.Reset();
                Sim4D.Session = null;
                return new { reset = true };
            }

            var tasks = TaskStore.Load(doc);
            if (tasks.Count == 0) throw new CommandException("Chưa có tiến độ 4D. Dùng sim4d_generate hoặc sim4d_set_tasks trước.");
            var date = Args.GetStringOrDefault(arguments, "date") is { } s ? Sim4D.ParseDate(s, "date") : DateTime.Today;
            if (Sim4D.Session == null || !Sim4D.Session.Doc.Equals(doc) || !Sim4D.Session.View.IsValidObject)
                Sim4D.Session = SimSession.Start(context.UIDocument, tasks.SelectMany(t => t.ElementIds).Distinct().ToList(), camera: false);
            var states = Sim.Compute(tasks, date);
            Sim4D.Session.Apply(states);
            // Đổi view ngay trong lúc lệnh đang chạy có lúc bị Revit khoá: gửi yêu cầu, Revit đổi khi rảnh.
            if (context.UIDocument.ActiveView.Id != Sim4D.Session.View.Id) context.UIDocument.RequestViewChange(Sim4D.Session.View);

            var active = tasks.Where(t => t.Start <= date && date <= t.End).Select(t => t.Name).ToList();
            return new
            {
                date = Sim4D.D(date), view = Sim4D.Session.View.Name,
                states = states.GroupBy(kv => kv.Value).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                active_tasks = active,
            };
        }
    }
}
