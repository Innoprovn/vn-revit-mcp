using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace BIMTech4D
{
    public enum TaskType { Construct = 0, Demolish = 1, Temporary = 2 }

    /// <summary>Một công việc trong tiến độ, gắn với danh sách phần tử Revit.</summary>
    public class Task4D : INotifyPropertyChanged
    {
        string _name = "Công việc mới";
        DateTime _start = DateTime.Today;
        DateTime _end = DateTime.Today.AddDays(6);
        TaskType _type = TaskType.Construct;

        public string Name { get => _name; set { _name = value; On(); } }
        public DateTime Start { get => _start; set { _start = value.Date; On(); } }
        public DateTime End { get => _end; set { _end = value.Date; On(); } }
        public TaskType Type { get => _type; set { _type = value; On(); } }
        public List<long> ElementIds { get; set; } = new List<long>();

        [JsonIgnore] public int Count => ElementIds?.Count ?? 0;

        public void TouchElements() { On(nameof(ElementIds)); On(nameof(Count)); }

        public event PropertyChangedEventHandler PropertyChanged;
        void On([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public static string TypeLabel(TaskType t) => t switch
        {
            TaskType.Demolish => "Phá dỡ",
            TaskType.Temporary => "Tạm thời",
            _ => "Xây dựng"
        };

        public static TaskType ParseType(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Contains("phá") || s.Contains("pha") || s.Contains("demol")) return TaskType.Demolish;
            if (s.Contains("tạm") || s.Contains("tam") || s.Contains("temp")) return TaskType.Temporary;
            return TaskType.Construct;
        }
    }

    /// <summary>Lưu / đọc tiến độ trong chính file .rvt (Extensible Storage).</summary>
    public static class TaskStore
    {
        static readonly Guid SchemaGuid = new Guid("7c1e3c52-4d2a-4f8e-9b61-3a4d5e6f7a81");
        const string FieldName = "Json";

        class Payload
        {
            public int Version { get; set; } = 1;
            public List<Task4D> Tasks { get; set; } = new List<Task4D>();
        }

        static Schema GetOrCreateSchema()
        {
            var s = Schema.Lookup(SchemaGuid);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName("BIMTech4DTasks");
            b.SetVendorId("BIMT");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(FieldName, typeof(string));
            return b.Finish();
        }

        static DataStorage FindStorage(Document doc, Schema schema)
        {
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
            {
                var ent = ds.GetEntity(schema);
                if (ent != null && ent.IsValid()) return ds;
            }
            return null;
        }

        public static List<Task4D> Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new List<Task4D>();
            var ds = FindStorage(doc, schema);
            if (ds == null) return new List<Task4D>();
            var json = ds.GetEntity(schema).Get<string>(FieldName);
            if (string.IsNullOrWhiteSpace(json)) return new List<Task4D>();
            return JsonSerializer.Deserialize<Payload>(json)?.Tasks ?? new List<Task4D>();
        }

        /// <summary>Xoá tiến độ lưu trong model và các view mô phỏng do tool tạo.</summary>
        public static int DeleteAll(Document doc, IEnumerable<string> viewNames)
        {
            var ids = new List<ElementId>();
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
                foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
                {
                    var ent = ds.GetEntity(schema);
                    if (ent != null && ent.IsValid()) ids.Add(ds.Id);
                }
            var names = new HashSet<string>(viewNames);
            int views = 0;
            foreach (View3D v in new FilteredElementCollector(doc).OfClass(typeof(View3D)))
                if (!v.IsTemplate && names.Contains(v.Name)) { ids.Add(v.Id); views++; }
            if (ids.Count == 0) return 0;
            using var t = new Transaction(doc, "BIMTech 4D - Xoá tiến độ");
            t.Start();
            doc.Delete(ids);
            t.Commit();
            return views;
        }

        public static void Save(Document doc, IEnumerable<Task4D> tasks)
        {
            var json = JsonSerializer.Serialize(new Payload { Tasks = tasks.ToList() });
            var schema = GetOrCreateSchema();
            using var t = new Transaction(doc, "BIMTech 4D - Lưu tiến độ");
            t.Start();
            var ds = FindStorage(doc, schema) ?? DataStorage.Create(doc);
            var ent = new Entity(schema);
            ent.Set(FieldName, json);
            ds.SetEntity(ent);
            t.Commit();
        }
    }

    /// <summary>Nhập / xuất CSV để sửa tiến độ bằng Excel.</summary>
    public static class Csv
    {
        const string Header = "TenCongViec,BatDau,KetThuc,Loai,ElementIds";
        static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy/MM/dd", "M/d/yyyy" };

        public static void Export(string path, IEnumerable<Task4D> tasks)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var t in tasks)
            {
                sb.Append(Quote(t.Name)).Append(',')
                  .Append(t.Start.ToString("yyyy-MM-dd")).Append(',')
                  .Append(t.End.ToString("yyyy-MM-dd")).Append(',')
                  .Append(Quote(Task4D.TypeLabel(t.Type))).Append(',')
                  .Append(string.Join(" ", t.ElementIds)).AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static List<Task4D> Import(string path)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count == 0) return new List<Task4D>();
            // Excel bản tiếng Việt hay lưu CSV bằng dấu ';'
            char sep = lines[0].Contains(';') && !lines[0].Contains(',') ? ';' : ',';
            var result = new List<Task4D>();
            for (int i = 1; i < lines.Count; i++)
            {
                var f = Split(lines[i], sep);
                if (f.Count < 3) throw new FormatException($"Dòng {i + 1}: cần ít nhất 3 cột (tên, bắt đầu, kết thúc).");
                var t = new Task4D
                {
                    Name = f[0],
                    Start = ParseDate(f[1], i + 1),
                    End = ParseDate(f[2], i + 1),
                    Type = f.Count > 3 ? Task4D.ParseType(f[3]) : TaskType.Construct
                };
                if (f.Count > 4)
                    t.ElementIds = f[4].Split(new[] { ' ', '|', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(long.Parse).Distinct().ToList();
                result.Add(t);
            }
            return result;
        }

        static DateTime ParseDate(string s, int line)
        {
            s = s.Trim();
            if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("vi-VN"), DateTimeStyles.None, out d)) return d;
            throw new FormatException($"Dòng {line}: không đọc được ngày \"{s}\" (dùng dạng 2026-10-31 hoặc 31/10/2026).");
        }

        static string Quote(string s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', ';', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static List<string> Split(string line, char sep)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') q = false;
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == sep) { res.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            res.Add(sb.ToString());
            return res;
        }
    }
}
