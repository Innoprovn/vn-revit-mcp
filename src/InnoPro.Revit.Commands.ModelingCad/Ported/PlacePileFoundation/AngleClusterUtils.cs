using Autodesk.Revit.DB;

namespace InnoPro.Revit.Commands.ModelingCad.PlacePileFoundation;

/// <summary>Phát hiện phương "ngang/đứng" chủ đạo (θ) của 1 tập vector qua gom cụm góc mod 90° — dùng chung cho Job E (hệ Grid) và Job R (đường bao đài).</summary>
internal static class AngleClusterUtils
{
    /// <summary>Góc phương 1 vector, quy về [0°, 90°) — vector vuông góc/song song cho cùng giá trị sau mod 90.</summary>
    public static double Mod90Deg(XYZ direction) => Mod(Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI, 90);

    /// <summary>Cụm giá trị mod-90 đông nhất trong dung sai cho trước, kèm số thành viên cụm — null nếu danh sách rỗng.</summary>
    public static (double ThetaDeg, int Count)? DetectDominantAngle(IReadOnlyList<double> mod90Angles, double toleranceDegrees)
    {
        if (mod90Angles.Count == 0)
            return null;

        var bestAngle = mod90Angles[0];
        var bestCount = 0;
        foreach (var candidate in mod90Angles)
        {
            var count = mod90Angles.Count(a => CircularDistance90(a, candidate) <= toleranceDegrees);
            if (count > bestCount)
            {
                bestCount = count;
                bestAngle = candidate;
            }
        }

        var cluster = mod90Angles.Where(a => CircularDistance90(a, bestAngle) <= toleranceDegrees).ToList();
        var meanShift = cluster.Select(a => SignedCircularDiff90(a, bestAngle)).Average();
        var theta = Mod(bestAngle + meanShift, 90);
        return (theta, bestCount);
    }

    public static double Mod(double x, double m) => ((x % m) + m) % m;

    public static double CircularDistance90(double a, double b)
    {
        var diff = Mod(a - b, 90);
        return Math.Min(diff, 90 - diff);
    }

    public static double SignedCircularDiff90(double a, double reference) => Mod(a - reference + 45, 90) - 45;
}
