namespace InnoPro.Revit.Commands.Rebar;

using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using InnoPro.Revit.Sdk;

public sealed class RebarSetUnobscured : RevitCommandBase
{
    public override string Name => "rebar_set_unobscured";
    public override string Category => "Rebar";
    public override TransactionMode TransactionMode => TransactionMode.Auto;
    public override RibbonButton? Ribbon => new("Unobscured", "◐");
    public override string Description =>
        "Bật/tắt hiển thị Unobscured của thép trong view hiện tại; toggle: tất cả đang bật thì tắt, còn lại thì bật.";
    public override string InputSchema => """
        {
          "type": "object",
          "properties": {
            "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Thép hoặc host; trống = cả view." },
            "mode": { "type": "string", "enum": ["toggle", "on", "off"], "description": "Mặc định toggle." }
          }
        }
        """;

    // Một cú bấm, không form: thép đang chọn, không chọn thì cả view.
    public override object? PromptArguments(RevitCommandContext context) =>
        new { element_ids = RebarScope.SelectedIds(context), mode = "toggle" };

    public override object? Execute(RevitCommandContext context, JsonElement arguments)
    {
        var view = RebarScope.ActiveModelView(context);
        var scope = RebarScope.Resolve(context, arguments, "active_view");
        if (scope.Elements.Count == 0)
            throw new CommandException(view.DetailLevel == ViewDetailLevel.Coarse
                ? "Không có thép nào. View đang Coarse, thép thường không hiện: đổi sang Medium/Fine."
                : $"Không có thép nào trong {scope.Scope}.");

        var mode = (ArgsX.String(arguments, "mode") ?? "toggle").ToLowerInvariant();
        bool turnOn;
        if (mode == "on") turnOn = true;
        else if (mode == "off") turnOn = false;
        else
        {
            var states = scope.Elements.Select(e => Get(e, view)).Where(s => s != null).ToList();
            turnOn = !(states.Count > 0 && states.All(s => s == true));
        }

        var changed = scope.Elements.Count(e => Set(e, view, turnOn));
        if (changed == 0) throw new CommandException("Không đổi được trạng thái thép nào (thép không thật sự hiển thị trong view này).");
        return new { view = view.Name, unobscured = turnOn, changed, skipped = scope.Elements.Count - changed };
    }

    private static bool? Get(Element e, View view)
    {
        try
        {
            return e switch
            {
                Rebar r => r.IsUnobscuredInView(view),
                RebarInSystem r => r.IsUnobscuredInView(view),
                RebarContainer c => c.IsUnobscuredInView(view),
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    private static bool Set(Element e, View view, bool value)
    {
        try
        {
            switch (e)
            {
                case Rebar r: r.SetUnobscuredInView(view, value); return true;
                case RebarInSystem r: r.SetUnobscuredInView(view, value); return true;
                case RebarContainer c: c.SetUnobscuredInView(view, value); return true;
                default: return false;
            }
        }
        catch (Exception) { return false; }
    }
}
