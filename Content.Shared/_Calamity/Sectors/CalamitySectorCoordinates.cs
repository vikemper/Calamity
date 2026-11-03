using System;
using System.Numerics;

namespace Content.Shared._Calamity.Sectors;

public readonly record struct CalamitySectorCoordinates(int X, int Y)
{
    public const int GridSize = 11;
    public static CalamitySectorCoordinates Hub => new(GridSize / 2, GridSize / 2);

    public CalamitySectorCoordinates Wrap()
    {
        return new CalamitySectorCoordinates(WrapCoordinate(X), WrapCoordinate(Y));
    }

    public static CalamitySectorCoordinates Wrap(int x, int y)
    {
        return new CalamitySectorCoordinates(WrapCoordinate(x), WrapCoordinate(y));
    }

    public static int GetDistance(CalamitySectorCoordinates first, CalamitySectorCoordinates second)
    {
        var firstWrapped = first.Wrap();
        var secondWrapped = second.Wrap();
        var deltaX = Math.Abs(firstWrapped.X - secondWrapped.X);
        var deltaY = Math.Abs(firstWrapped.Y - secondWrapped.Y);
        deltaX = Math.Min(deltaX, GridSize - deltaX);
        deltaY = Math.Min(deltaY, GridSize - deltaY);
        return Math.Max(deltaX, deltaY);
    }

    public static (CalamitySectorCoordinates Sector, Vector2 Position) GetEdgeTransition(
        CalamitySectorCoordinates sector,
        Vector2 position,
        float sectorSize)
    {
        if (!float.IsFinite(sectorSize) || sectorSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sectorSize));

        var halfSize = sectorSize / 2f;
        var crossedX = (int) MathF.Floor((position.X + halfSize) / sectorSize);
        var crossedY = (int) MathF.Floor((position.Y + halfSize) / sectorSize);
        var destination = Wrap(sector.X + crossedX, sector.Y + crossedY);

        return (destination, new Vector2(
            position.X - crossedX * sectorSize,
            position.Y - crossedY * sectorSize));
    }

    private static int WrapCoordinate(int coordinate)
    {
        return (coordinate % GridSize + GridSize) % GridSize;
    }
}
