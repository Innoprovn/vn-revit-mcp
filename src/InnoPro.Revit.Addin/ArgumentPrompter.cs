using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using InnoPro.Revit.Sdk;
using InnoPro.Ui;

namespace InnoPro.Revit.Addin;

/// <summary>
/// Hỏi tham số khi người dùng bấm nút, dựa trên InputSchema của lệnh:
/// element_ids lấy từ vùng chọn (hoặc cho quét chọn), element_id cho chọn một phần tử,
/// các trường đơn giản hiện trong một form tự sinh. Lệnh chỉ cần viết schema là có giao diện.
/// </summary>
internal static class ArgumentPrompter
{
    private sealed record Field(string Name, string Type, string? Description, bool Required, IReadOnlyList<string>? Options, bool Editable);

    /// <summary>null khi người dùng hủy.</summary>
    public static JsonElement? Prompt(RevitCommandContext context, IRevitCommand command)
    {
        var schema = JsonNode.Parse(command.InputSchema)?.AsObject() ?? new JsonObject();
        var properties = schema["properties"]?.AsObject() ?? new JsonObject();
        var required = schema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet() ?? [];

        var values = new JsonObject();
        var formFields = new List<Field>();
        foreach (var (name, node) in properties)
        {
            var prop = node?.AsObject() ?? new JsonObject();
            var type = TypeOf(prop);
            var isRequired = required.Contains(name);

            if (name == "element_ids" && type == "array")
            {
                var picked = context.Ui.SelectionOrPick($"{command.Name}: chọn phần tử rồi bấm Finish");
                values[name] = new JsonArray(picked.Select(e => (JsonNode)JsonValue.Create(e.Id.Value)!).ToArray());
            }
            else if (name == "element_id" && type == "integer")
            {
                var selected = context.UIDocument.Selection.GetElementIds();
                values[name] = selected.Count == 1
                    ? selected.First().Value
                    : context.Ui.PickElement($"{command.Name}: chọn một phần tử").Id.Value;
            }
            else if (type is "string" or "number" or "integer" or "boolean" || (type == "array" && ItemType(prop) == "string"))
            {
                var options = Options(context.Document, name, prop);
                formFields.Add(new Field(name, type, prop["description"]?.GetValue<string>(), isRequired, options,
                    Editable: prop["enum"] == null));
            }
            else if (isRequired)
            {
                throw new CommandException(
                    $"Lệnh '{command.Name}' cần tham số '{name}' dạng {type} phức tạp, hiện chỉ dùng qua AI. " +
                    "Hãy nhờ AI chạy lệnh này, hoặc tác giả lệnh viết PromptArguments riêng.");
            }
        }

        if (formFields.Count > 0 && !ShowForm(context, command, formFields, values)) return null;
        return JsonSerializer.SerializeToElement(values);
    }

    private static string TypeOf(JsonObject prop) => prop["type"] switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonArray a => a.Select(n => n!.GetValue<string>()).FirstOrDefault(t => t != "null") ?? "string",
        _ => prop["enum"] != null ? "string" : "object",
    };

    private static string? ItemType(JsonObject prop) => prop["items"]?["type"]?.GetValue<string>();

    /// <summary>Gợi ý giá trị theo tên tham số quen thuộc: tầng, category, type.</summary>
    private static IReadOnlyList<string>? Options(Document doc, string name, JsonObject prop)
    {
        if (prop["enum"] is JsonArray values) return values.Select(v => v!.ToString()).ToList();

        static List<string> Names<T>(Document d, Func<T, string> name, BuiltInCategory? category = null) where T : Element
        {
            var collector = new FilteredElementCollector(d).OfClass(typeof(T));
            if (category is { } c) collector.OfCategory(c);
            return collector.Cast<T>().Select(name).Distinct().OrderBy(n => n).ToList();
        }

        return name switch
        {
            "level" or "top_level" => new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).Select(l => l.Name).ToList(),
            "category" or "category_a" or "category_b" => doc.Settings.Categories.Cast<Category>()
                .Where(c => c.CategoryType == CategoryType.Model && c.AllowsBoundParameters)
                .Select(c => c.Name).OrderBy(n => n).ToList(),
            "wall_type" => Names<WallType>(doc, Lookup.FullName),
            "floor_type" => Names<FloorType>(doc, Lookup.FullName),
            "beam_type" => Names<FamilySymbol>(doc, Lookup.FullName, BuiltInCategory.OST_StructuralFraming),
            "family_type" => Names<FamilySymbol>(doc, Lookup.FullName),
            "schedule" => Names<ViewSchedule>(doc, s => s.Name),
            _ => null,
        };
    }

    private static bool ShowForm(RevitCommandContext context, IRevitCommand command, List<Field> fields, JsonObject values)
    {
        var window = new InnoWindow(Labels.CommandTitle(command), context.UIApplication.MainWindowHandle, width: 400);
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 0) };
        var summary = Labels.Short(command.Description, 110);
        if (summary.Length > 0) root.Children.Add(new TextBlock { Text = summary, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        var editors = new Dictionary<Field, Func<string>>();
        foreach (var field in fields)
        {
            root.Children.Add(new TextBlock
            {
                Text = Labels.Field(field.Name, field.Description) + (field.Required ? " *" : ""),
                ToolTip = string.IsNullOrWhiteSpace(field.Description) ? null : field.Description,
                Margin = new Thickness(0, 0, 0, 3),
            });

            FrameworkElement editor;
            if (field.Type == "boolean")
            {
                var check = new CheckBox { Content = "Có", IsChecked = Labels.Default(field.Description) is "true" or "có" };
                editor = check;
                editors[field] = () => check.IsChecked == true ? "true" : "false";
            }
            else if (field.Options is { Count: > 0 } options)
            {
                var combo = new ComboBox { IsEditable = field.Editable, ItemsSource = options };
                editor = combo;
                editors[field] = () => (field.Editable ? combo.Text : combo.SelectedItem?.ToString()) ?? "";
            }
            else
            {
                var text = new TextBox();
                editor = Labels.WithPlaceholder(text, Labels.Default(field.Description));
                editors[field] = () => text.Text;
            }
            editor.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(editor);
        }

        var error = new TextBlock { Foreground = Theme.Danger, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(error);
        window.Body = new ScrollViewer { Content = root, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var accepted = false;
        window.AddButton("Hủy", closes: true);
        window.AddButton("Chạy", primary: true, onClick: () =>
        {
            try
            {
                foreach (var (field, read) in editors)
                {
                    var raw = read().Trim();
                    if (raw.Length == 0 || (field.Type == "boolean" && raw == "false" && !field.Required))
                    {
                        if (field.Required && field.Type != "boolean") throw new FormatException($"Cần nhập: {Labels.Field(field.Name, field.Description)}.");
                        if (raw.Length == 0) continue;
                    }
                    values[field.Name] = Convert(field, raw);
                }
                accepted = true;
                window.Close();
            }
            catch (FormatException ex)
            {
                error.Text = ex.Message;
            }
        });

        window.ShowDialog();
        return accepted;
    }
    private static JsonNode Convert(Field field, string raw) => field.Type switch
    {
        "boolean" => JsonValue.Create(raw == "true"),
        "integer" => long.TryParse(raw, out var n) ? JsonValue.Create(n) : throw new FormatException($"{Labels.Field(field.Name, field.Description)}: phải là số nguyên."),
        "number" => double.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : throw new FormatException($"{Labels.Field(field.Name, field.Description)}: phải là số."),
        "array" => new JsonArray(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        _ => JsonValue.Create(raw)!,
    };
}
