using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.AutoJoin;

public enum JoinScope { ActiveView, Project }
public enum JoinOperation { Join, Switch, Unjoin }

public sealed class CategoryOption
{
    public string Key { get; }
    public string DisplayName { get; }
    public BuiltInCategory Category { get; }

    public CategoryOption(string key, string displayName, BuiltInCategory category)
    {
        Key = key;
        DisplayName = displayName;
        Category = category;
    }

    public override string ToString() => DisplayName;
}

public static class CategoryCatalog
{
    public static readonly CategoryOption Floor = new("FLOOR", "Floors", BuiltInCategory.OST_Floors);
    public static readonly CategoryOption Mass = new("MASS", "Mass", BuiltInCategory.OST_Mass);
    public static readonly CategoryOption Column = new("COLUMN", "Structural Columns", BuiltInCategory.OST_StructuralColumns);
    public static readonly CategoryOption Foundation = new("FOUNDATION", "Structural Foundations", BuiltInCategory.OST_StructuralFoundation);
    public static readonly CategoryOption Framing = new("FRAMING", "Structural Framing", BuiltInCategory.OST_StructuralFraming);
    public static readonly CategoryOption Wall = new("WALL", "Walls", BuiltInCategory.OST_Walls);
    public static readonly CategoryOption Roof = new("ROOF", "Roofs", BuiltInCategory.OST_Roofs);

    public static readonly IReadOnlyList<CategoryOption> All =
        new[] { Floor, Mass, Column, Foundation, Framing, Wall, Roof };

    public static readonly IReadOnlyList<CategoryOption> Defaults =
        new[] { Floor, Mass, Column, Foundation, Framing, Wall };

    public static CategoryOption? ByKey(string? key) =>
        All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
}

public sealed class AutoJoinState
{
    public List<string> CategoryKeys { get; set; } = new();
    public JoinScope Scope { get; set; } = JoinScope.ActiveView;
}

public sealed class JoinRunResult
{
    public int CandidatePairs { get; set; }
    public int Changed { get; set; }
    public int AlreadyCorrect { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}
