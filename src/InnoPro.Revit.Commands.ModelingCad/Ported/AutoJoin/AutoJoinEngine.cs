using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.AutoJoin;

public static class AutoJoinEngine
{
    private const double ToleranceFeet = 0.1;

    public static JoinRunResult Run(Document doc, View activeView,
        IReadOnlyList<CategoryOption> orderedCategories, JoinScope scope, JoinOperation operation)
    {
        var result = new JoinRunResult();
        var elementsByCategory = orderedCategories.ToDictionary(
            category => category.Key,
            category => Collect(doc, activeView, category.Category, scope));

        for (int high = 0; high < orderedCategories.Count; high++)
        {
            for (int low = high + 1; low < orderedCategories.Count; low++)
            {
                ProcessCategoryPair(doc, activeView,
                    elementsByCategory[orderedCategories[high].Key],
                    elementsByCategory[orderedCategories[low].Key],
                    scope, operation, result);
            }
        }

        return result;
    }

    private static List<Element> Collect(Document doc, View activeView,
        BuiltInCategory category, JoinScope scope)
    {
        FilteredElementCollector collector = scope == JoinScope.ActiveView
            ? new FilteredElementCollector(doc, activeView.Id)
            : new FilteredElementCollector(doc);

        return collector.OfCategory(category).WhereElementIsNotElementType().ToList();
    }

    private static void ProcessCategoryPair(Document doc, View activeView,
        IReadOnlyList<Element> higher, IReadOnlyList<Element> lower,
        JoinScope scope, JoinOperation operation, JoinRunResult result)
    {
        View? boxView = scope == JoinScope.ActiveView ? activeView : null;
        foreach (Element first in higher)
        {
            BoundingBoxXYZ? firstBox = first.get_BoundingBox(boxView);
            if (firstBox == null) continue;

            foreach (Element second in lower)
            {
                BoundingBoxXYZ? secondBox = second.get_BoundingBox(boxView);
                if (secondBox == null || !Overlaps(firstBox, secondBox)) continue;

                result.CandidatePairs++;
                try
                {
                    Apply(doc, first, second, operation, result);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { result.Failed++; }
                catch (InvalidOperationException) { result.Failed++; }
            }
        }
    }

    private static void Apply(Document doc, Element higher, Element lower,
        JoinOperation operation, JoinRunResult result)
    {
        bool joined = JoinGeometryUtils.AreElementsJoined(doc, higher, lower);

        if (operation == JoinOperation.Unjoin)
        {
            if (!joined) { result.Skipped++; return; }
            JoinGeometryUtils.UnjoinGeometry(doc, higher, lower);
            result.Changed++;
            return;
        }

        if (operation == JoinOperation.Switch)
        {
            if (!joined) { result.Skipped++; return; }
            JoinGeometryUtils.SwitchJoinOrder(doc, higher, lower);
            result.Changed++;
            return;
        }

        bool created = false;
        if (!joined)
        {
            JoinGeometryUtils.JoinGeometry(doc, higher, lower);
            created = true;
            result.Changed++;
        }

        if (!JoinGeometryUtils.IsCuttingElementInJoin(doc, higher, lower))
        {
            JoinGeometryUtils.SwitchJoinOrder(doc, higher, lower);
            if (!created) result.Changed++;
        }
        else if (!created)
        {
            result.AlreadyCorrect++;
        }
    }

    private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
        a.Min.X - ToleranceFeet <= b.Max.X && b.Min.X - ToleranceFeet <= a.Max.X &&
        a.Min.Y - ToleranceFeet <= b.Max.Y && b.Min.Y - ToleranceFeet <= a.Max.Y &&
        a.Min.Z - ToleranceFeet <= b.Max.Z && b.Min.Z - ToleranceFeet <= a.Max.Z;
}
