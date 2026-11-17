using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class CurveSpeedTests
{
    [Theory]
    [InlineData(40, 10)]
    [InlineData(100, 40)]
    [InlineData(200, 60)]
    [InlineData(1000, 100)]
    [InlineData(5000, 120)]
    public void RadiusMapsToThePostedSpeed(double radius, double kmh) => Assert.Equal(kmh, CurveSpeed.KmhForRadius(radius));

    [Fact]
    public void ThreePointsOnACircleGiveItsRadius()
    {
        MapPoint On(double angle) => new(200 * Math.Cos(angle), 200 * Math.Sin(angle));
        Assert.Equal(200, CurveSpeed.Radius(On(0), On(0.1), On(0.2)), 6);
        Assert.True(double.IsPositiveInfinity(CurveSpeed.Radius(new(0, 0), new(1, 0), new(2, 0))));
    }

    [Fact]
    public void AStraightThenACurveGetsTheCurvesLimit()
    {
        // 200 m straight along x, then a quarter circle of radius 150 m (50 km/h).
        var offsets = new List<double>();
        var points = new List<MapPoint>();
        for (var s = 0.0; s <= 200; s += 0.5)
        {
            offsets.Add(s);
            points.Add(new MapPoint(s, 0));
        }
        var arc = Math.PI / 2 * 150;
        for (var s = 0.5; s <= arc; s += 0.5)
        {
            var a = s / 150;
            offsets.Add(200 + s);
            points.Add(new MapPoint(200 + 150 * Math.Sin(a), 150 - 150 * Math.Cos(a)));
        }
        var profile = CurveSpeed.Profile(offsets, points);
        Assert.Equal(120 / 3.6, profile.LimitAt(50), 6);
        Assert.Equal(50 / 3.6, profile.LimitAt(200 + arc / 2), 6);
        // The curve's limit holds from where the curve begins.
        Assert.Equal(50 / 3.6, profile.LimitAt(201), 6);
        Assert.Equal(50 / 3.6, profile.MinLimit(0, 200 + arc), 6);
    }

    [Fact]
    public void ACapHoldsOnStraightTrack()
    {
        var offsets = Enumerable.Range(0, 201).Select(i => i * 0.5).ToList();
        var points = offsets.Select(s => new MapPoint(s, 0)).ToList();
        Assert.Equal(30 / 3.6, CurveSpeed.Profile(offsets, points, capKmh: 30).LimitAt(40), 6);
    }
}
