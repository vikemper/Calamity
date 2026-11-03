using System.Numerics;
using Content.Shared._Calamity.Sectors;
using NUnit.Framework;

namespace Content.Tests.Server._Calamity.Sectors;

[TestFixture]
[TestOf(typeof(CalamitySectorCoordinates))]
public sealed class CalamitySectorCoordinatesTest
{
    [Test]
    public void CrossingPositiveEdgeWrapsGridAndPosition()
    {
        var (sector, position) = CalamitySectorCoordinates.GetEdgeTransition(
            new CalamitySectorCoordinates(10, 4),
            new Vector2(2050f, 20f),
            4096f);

        Assert.That(sector, Is.EqualTo(new CalamitySectorCoordinates(0, 4)));
        Assert.That(position, Is.EqualTo(new Vector2(-2046f, 20f)));
    }

    [Test]
    public void CrossingNegativeAndPositiveEdgesWrapsBothAxes()
    {
        var (sector, position) = CalamitySectorCoordinates.GetEdgeTransition(
            new CalamitySectorCoordinates(0, 0),
            new Vector2(-2050f, 2050f),
            4096f);

        Assert.That(sector, Is.EqualTo(new CalamitySectorCoordinates(10, 1)));
        Assert.That(position, Is.EqualTo(new Vector2(2046f, -2046f)));
    }

    [Test]
    public void PositionInsideSectorDoesNotTransition()
    {
        var source = new CalamitySectorCoordinates(3, 7);
        var position = new Vector2(200f, -300f);

        var (sector, translated) = CalamitySectorCoordinates.GetEdgeTransition(source, position, 4096f);

        Assert.That(sector, Is.EqualTo(source));
        Assert.That(translated, Is.EqualTo(position));
    }

    [Test]
    public void HubIsTheCenterOfTheElevenByElevenGrid()
    {
        Assert.That(CalamitySectorCoordinates.GridSize, Is.EqualTo(11));
        Assert.That(CalamitySectorCoordinates.Hub, Is.EqualTo(new CalamitySectorCoordinates(5, 5)));
        Assert.That(CalamitySectorCoordinates.Wrap(-1, 11), Is.EqualTo(new CalamitySectorCoordinates(10, 0)));
    }

    [Test]
    public void SectorDistanceUsesShortestWrappedChebyshevDistance()
    {
        Assert.That(
            CalamitySectorCoordinates.GetDistance(new CalamitySectorCoordinates(0, 0), new CalamitySectorCoordinates(10, 0)),
            Is.EqualTo(1));
        Assert.That(
            CalamitySectorCoordinates.GetDistance(new CalamitySectorCoordinates(5, 5), new CalamitySectorCoordinates(9, 3)),
            Is.EqualTo(4));
    }
}
