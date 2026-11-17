using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class SpeedPlannerTests
{
    private static readonly BrakingModel Gentle = new(0.25, 2);

    [Fact]
    public void BrakingCurveMeetsTheStopExactly()
    {
        // From the speed it gives, braking after the delay stops in the distance.
        foreach (var d in new[] { 10.0, 100, 1000 })
        {
            var v = Gentle.SpeedToMeet(0, d);
            Assert.Equal(d, Gentle.DistanceToSlow(v, 0), 6);
        }
        Assert.Equal(5, Gentle.SpeedToMeet(5, 0));
    }

    [Fact]
    public void AZoneHoldsUntilTheRearClearsIt()
    {
        var b = new TrackGraphBuilder();
        var slow = new SpeedLimitProfile(new[] { 0.0, 100, 200 }, new[] { double.PositiveInfinity, 10, double.PositiveInfinity });
        var t = b.AddTrack(1000, new MapPoint(0, 0), new MapPoint(1000, 0), slow);
        var g = b.Build();
        var leg = new RouteLeg(new[] { new RoutePiece(t, 0, 1000) }, null, ConsistEnd.A, false);
        var planner = SpeedPlanner.ForLeg(g, leg, trainLength: 80, junctionLimit: 0);

        Assert.Equal(10, planner.LimitAt(150, 30));
        Assert.Equal(10, planner.LimitAt(270, 30));
        Assert.Equal(30, planner.LimitAt(290, 30));
        // Ahead of the zone, the curve comes down to meet it.
        var before = planner.TargetSpeed(50, 30, 1000, Gentle);
        Assert.True(before > 10 && before < 30, $"{before}");
        Assert.Equal(Gentle.SpeedToMeet(10, 50), before, 6);
    }

    [Fact]
    public void ABackwardPieceMapsZonesFromItsFarEnd()
    {
        var b = new TrackGraphBuilder();
        var slow = new SpeedLimitProfile(new[] { 0.0, 100, 200 }, new[] { double.PositiveInfinity, 10, double.PositiveInfinity });
        var t = b.AddTrack(1000, new MapPoint(0, 0), new MapPoint(1000, 0), slow);
        var leg = new RouteLeg(new[] { new RoutePiece(t, 300, 0) }, null, ConsistEnd.A, false);
        var planner = SpeedPlanner.ForLeg(b.Build(), leg, trainLength: 0, junctionLimit: 0);
        var zone = Assert.Single(planner.Zones);
        Assert.Equal(100, zone.From, 6);
        Assert.Equal(200, zone.To, 6);
    }

    [Fact]
    public void TheTargetFallsToZeroAtTheStop()
    {
        var planner = new SpeedPlanner();
        Assert.Equal(0, planner.TargetSpeed(500, 20, 500, Gentle));
        Assert.Equal(0, planner.TargetSpeed(510, 20, 500, Gentle));
        Assert.Equal(20, planner.TargetSpeed(0, 20, 5000, Gentle));
    }
}
