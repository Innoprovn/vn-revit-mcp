namespace InnoPro.Revit.Commands.Rebar.Qa;

internal readonly record struct BoundingBoxMm(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public static BoundingBoxMm FromBar(BarGeometryDto bar, double expandMm)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (var seg in bar.CenterlineSegments)
        {
            foreach (var p in new[] { seg.Start, seg.End })
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }
        }

        return new BoundingBoxMm(minX - expandMm, minY - expandMm, minZ - expandMm, maxX + expandMm, maxY + expandMm, maxZ + expandMm);
    }

    public bool Intersects(in BoundingBoxMm other) =>
        MinX <= other.MaxX && MaxX >= other.MinX &&
        MinY <= other.MaxY && MaxY >= other.MinY &&
        MinZ <= other.MaxZ && MaxZ >= other.MinZ;
}
