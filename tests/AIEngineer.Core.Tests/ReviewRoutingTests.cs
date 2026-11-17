using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>Cases from the November 2026 review of the routing, and of the engineer where it meets the routes.</summary>
public class ReviewRoutingTests
{
    /// <summary>A switch at the origin: the stem (track 0) east, branch 0 (track 1) west, branch 1 (track 2) west-southwest; more track joined beyond the stem if asked.</summary>
    private static TrackGraph Wye(double stem = 300, double branch = 300, double beyond = 0)
    {
        var b = new TrackGraphBuilder();
        var s = b.AddTrack(stem, new MapPoint(0, 0), new MapPoint(stem, 0));
        var t1 = b.AddTrack(branch, new MapPoint(0, 0), new MapPoint(-branch, 0));
        var t2 = b.AddTrack(branch, new MapPoint(0, 0), new MapPoint(-branch, -30));
        b.AddJunction(s, TrackEnd.Start, new[] { (t1, TrackEnd.Start), (t2, TrackEnd.Start) }, new MapPoint(0, 0));
        if (beyond > 0)
        {
            var x = b.AddTrack(beyond, new MapPoint(stem, 0), new MapPoint(stem + beyond, 0));
            b.Join(s, TrackEnd.End, x, TrackEnd.Start);
        }
        return b.Build();
    }

    /// <summary>
    /// West lead W (0), then sidings A (1) and B (2) between switches J1
    /// and J2, then E (3). W's west end is switch J0: a stub X (4) and a
    /// 1.6 km bypass Y (5) round to E's east end.
    /// </summary>
    private static (FakeWorld World, int J0, int J1, int J2) Sidings()
    {
        var b = new TrackGraphBuilder();
        var w = b.AddTrack(600, new MapPoint(-600, 0), new MapPoint(0, 0));
        var a = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(400, 0));
        var bb = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(400, -1));
        var e = b.AddTrack(300, new MapPoint(400, 0), new MapPoint(700, 0));
        var x = b.AddTrack(300, new MapPoint(-600, 0), new MapPoint(-900, 0));
        var y = b.AddTrack(1600, new MapPoint(-600, 0), new MapPoint(700, 0));
        var j1 = b.AddJunction(w, TrackEnd.End, new[] { (a, TrackEnd.Start), (bb, TrackEnd.Start) }, new MapPoint(0, 0));
        var j2 = b.AddJunction(e, TrackEnd.Start, new[] { (a, TrackEnd.End), (bb, TrackEnd.End) }, new MapPoint(400, 0));
        var j0 = b.AddJunction(w, TrackEnd.Start, new[] { (x, TrackEnd.Start), (y, TrackEnd.Start) }, new MapPoint(-600, 0));
        b.Join(y, TrackEnd.End, e, TrackEnd.End);
        return (new FakeWorld(b.Build()), j0, j1, j2);
    }

    [Theory]
    [InlineData(300, 0)]
    [InlineData(2000, 0)]
    [InlineData(2000, 500)]
    public void ATrainAcrossASwitchPullsClearOnTheLeadItStandsOn(double stem, double beyond)
    {
        // A 50 m train: end A 20 m up branch 0, end B 30 m onto the stem. To reach branch 1,
        // B pulls 35 m along the lead (A then 15 m clear of the points) and A leads back.
        var request = new RouteRequest
        {
            EndA = new TrackPosition(1, 20),
            OutwardA = TrackDirection.Forward,
            EndB = new TrackPosition(0, 30),
            OutwardB = TrackDirection.Forward,
            TrainLength = 50,
            Goal = new TrackPosition(2, 200),
        };
        var route = new Router(Wye(stem, beyond: beyond)).Find(request, out var failure);
        Assert.Equal(RouteFailure.None, failure);
        Assert.Equal(2, route.Legs.Count);
        Assert.Equal(ConsistEnd.B, route.Legs[0].Leader);
        Assert.Equal(35, route.Legs[0].Length, 3);
        Assert.Equal(ConsistEnd.A, route.Legs[1].Leader);
        // The second leg starts under end A as it then stands (15 m onto the stem) and ends on branch 1.
        Assert.True(route.Legs[1].TryLocate(new TrackPosition(0, 15), 0, 0.5, out var at, out _));
        Assert.Equal(215, route.Legs[1].Length - at, 3);
        Assert.Equal(2, route.Legs[1].PositionAt(route.Legs[1].Length).Track);
        var sw = Assert.Single(route.Legs[1].Switches);
        Assert.Equal(1, sw.Branch);
    }

    [Fact]
    public void ATailBeyondAJointPastTheSwitchIsFollowedBack()
    {
        // As in game, where a turnout is a short track of its own: branch 0 is 24 m of
        // turnout joined to the yard track beyond, and the train's tail is on the yard track.
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(300, 0));
        var turnout0 = b.AddTrack(24, new MapPoint(0, 0), new MapPoint(-24, 0));
        var turnout1 = b.AddTrack(24, new MapPoint(0, 0), new MapPoint(-24, -3));
        var yard0 = b.AddTrack(300, new MapPoint(-24, 0), new MapPoint(-324, 0));
        var yard1 = b.AddTrack(300, new MapPoint(-24, -3), new MapPoint(-324, -30));
        b.AddJunction(stem, TrackEnd.Start, new[] { (turnout0, TrackEnd.Start), (turnout1, TrackEnd.Start) }, new MapPoint(0, 0));
        b.Join(turnout0, TrackEnd.End, yard0, TrackEnd.Start);
        b.Join(turnout1, TrackEnd.End, yard1, TrackEnd.Start);
        var request = new RouteRequest
        {
            // 80 m of train: A 26 m onto the yard track, B 30 m onto the stem.
            EndA = new TrackPosition(yard0, 26),
            OutwardA = TrackDirection.Forward,
            EndB = new TrackPosition(stem, 30),
            OutwardB = TrackDirection.Forward,
            TrainLength = 80,
            Goal = new TrackPosition(yard1, 150),
        };
        var route = new Router(b.Build()).Find(request, out var failure);
        Assert.Equal(RouteFailure.None, failure);
        Assert.Equal(2, route.Legs.Count);
        // 26 m of yard track and 24 m of turnout to the points, and 15 m clear.
        Assert.Equal(65, route.Legs[0].Length, 3);
        Assert.Equal(yard1, route.Legs[1].PositionAt(route.Legs[1].Length).Track);
    }

    [Fact]
    public void APullClearNeedsRoomOnTheLead()
    {
        // The lead is 40 m: with the train's 30 m on it, 35 m more isn't there.
        var request = new RouteRequest
        {
            EndA = new TrackPosition(1, 20),
            OutwardA = TrackDirection.Forward,
            EndB = new TrackPosition(0, 30),
            OutwardB = TrackDirection.Forward,
            TrainLength = 50,
            Goal = new TrackPosition(2, 200),
        };
        Assert.Null(new Router(Wye(stem: 40)).Find(request, out _));
    }

    [Fact]
    public void AnOrderGivenAcrossASwitchIsCarriedOut()
    {
        var world = new FakeWorld(Wye(stem: 600, branch: 600));
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        // Stop 30 m onto the lead, the tail still on branch 0; then to branch 1.
        engineer.GoTo(new Waypoint(new TrackPosition(0, 30)));
        engineer.Enqueue(new Waypoint(new TrackPosition(2, 300)));
        Drive.Run(engineer, train, world, maxSeconds: 900, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(2, train.EndB.Track);
        Assert.InRange(train.EndB.Offset, 295, 301);
        // It pulled clear on the lead; it didn't run to its far end.
        Assert.True(train.Physics.Position < 700, $"ran {train.Physics.Position:F0} m");
    }

    [Fact]
    public void ItGoesRoundASwitchFouledByStandingCars()
    {
        var (world, _, _, _) = Sidings();
        // Cars on B, 5 to 25 m from J1's points: they foul the way onto A from the west.
        world.Occupancy.Add(2, new OccupiedSpan(5, 25, owner: 9));
        var train = new NetworkTrain(world, new TrackPosition(0, 380), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(1, 200)));
        Drive.Run(engineer, train, world, maxSeconds: 1500, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(1, train.EndA.Track);
    }

    [Fact]
    public void WithNoWayRoundItSaysTheSwitchIsFouled()
    {
        var world = new FakeWorld(Wye(stem: 600, branch: 600));
        world.Occupancy.Add(2, new OccupiedSpan(5, 25, owner: 9));
        var train = new NetworkTrain(world, new TrackPosition(0, 380), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(1, 200)));
        Drive.Run(engineer, train, world, maxSeconds: 30);
        Assert.Equal("No route to the waypoint: cars foul a switch on the way", engineer.Status);
        Assert.InRange(train.EndA.Offset, 379, 381);
    }

    [Fact]
    public void ASwitchHeldForAMomentIsWaitedFor()
    {
        var (world, _, j1, _) = Sidings();
        world.Selected[j1] = 1;
        // Another engineer's hold, or a train moving near the points: gone a few seconds later.
        world.Locked.Add(j1);
        var train = new NetworkTrain(world, new TrackPosition(0, 380), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(1, 200)));
        var time = 0.0;
        Drive.Run(engineer, train, world, maxSeconds: 1500, until: () =>
        {
            time += 0.2;
            if (time > 5)
                world.Locked.Clear();
            return engineer.Queue.Count == 0;
        });
        Assert.Empty(engineer.Queue);
        // 420 m direct; 2,400 m round the bypass.
        Assert.True(train.Physics.Position < 500, $"ran {train.Physics.Position:F0} m");
    }

    [Fact]
    public void ASwitchJammedByStandingCarsIsNotCountedOn()
    {
        var (world, _, j1, _) = Sidings();
        // Cars at rest across J1's points, which lie for siding B: it won't be thrown for A.
        world.Selected[j1] = 1;
        world.JammedSwitches.Add(j1);
        var train = new NetworkTrain(world, new TrackPosition(0, 380), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(1, 200)));
        Drive.Run(engineer, train, world, maxSeconds: 1500, until: () => engineer.Queue.Count == 0);
        // Through B as the switch lies, and back into A from the east.
        Assert.Empty(engineer.Queue);
        Assert.Equal(1, train.EndB.Track);
        Assert.DoesNotContain(world.Throws, t => t.Junction == j1);
    }

    [Theory]
    [InlineData(120, 60, 50)]
    [InlineData(300, 60, 50)]
    [InlineData(300, 200, 50)]
    [InlineData(300, 200, 12)]
    [InlineData(1000, 200, 50)]
    [InlineData(963, 400, 50)]
    public void TurningOnALoopNeverThrowsTheSwitchUnderTheTrain(double loop, double trainLength, double maxKmh)
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(800, new MapPoint(0, 0), new MapPoint(800, 0));
        var round = b.AddTrack(loop, new MapPoint(800, 0), new MapPoint(800, 0.001));
        var j = b.AddJunction(stem, TrackEnd.End, new[] { (round, TrackEnd.Start), (round, TrackEnd.End) }, new MapPoint(800, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(stem, 300), TrackDirection.Forward, trainLength);
        // Across the points: an end on each track, or an end within a few meters of them.
        world.OwnCarsAcross = _ => train.EndA.Track != train.EndB.Track
            || (train.EndA.Track == round && (train.EndA.Offset < 2.5 || train.EndA.Offset > loop - 2.5))
            || (train.EndB.Track == round && (train.EndB.Offset < 2.5 || train.EndB.Offset > loop - 2.5));
        var engineer = new Engineer();
        engineer.Settings.MaxSpeed = maxKmh / 3.6;
        // Back on the stem at 100 m, the same end leading, heading away from the loop: only the loop turns it.
        engineer.GoTo(new Waypoint(new[] { new WaypointGoal(new TrackPosition(stem, 100), TrackDirection.Backward) }) { Arrival = Arrival.FrontFirst });
        Drive.Run(engineer, train, world, maxSeconds: 1500, until: () => engineer.Queue.Count == 0,
            state: s =>
            {
                s.LocoEnd = ConsistEnd.A;
                s.LocoFront = ConsistEnd.A;
                return s;
            });
        Assert.Empty(engineer.Queue);
        Assert.Equal(0, world.ThrownUnderTheTrain);
        Assert.True(world.Throws.Count <= 4, $"{world.Throws.Count} throws of one switch");
        Assert.Equal(stem, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, 97, 103);
    }

    [Fact]
    public void ComingDownABranchItStopsClearOfCarsFoulingTheOther()
    {
        // Down branch 0 toward the points; cars on branch 1 from 12 to 34 m from them. On Road,
        // which runs on the switches as they lie: it must stop before the branches close up,
        // 35 m from the points and a gap, not 8 m from the points alongside the cars.
        var world = new FakeWorld(Wye(stem: 600, branch: 600));
        world.Occupancy.Add(2, new OccupiedSpan(12, 34, owner: 9));
        var train = new NetworkTrain(world, new TrackPosition(1, 300), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.SetRoad(ConsistEnd.A, 8);
        Drive.Run(engineer, train, world, maxSeconds: 300, until: () => engineer.Status.StartsWith("Stopped"));
        Assert.Equal("Stopped: cars fouling a switch", engineer.Status);
        Assert.Equal(1, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, 41, 48);
    }

    [Fact]
    public void StandingWithinTheReachItIsHeldOnlyByCarsNearerThePoints()
    {
        // The train's front stands 20 m from the points on branch 0. Cars on branch 1 at 25 to
        // 34 m are behind it as it leaves: it goes. Cars at 5 to 15 m are between it and the
        // points: it stays.
        foreach (var (from, to, goes) in new[] { (25.0, 34.0, true), (5.0, 15.0, false) })
        {
            var world = new FakeWorld(Wye(stem: 600, branch: 600));
            world.Occupancy.Add(2, new OccupiedSpan(from, to, owner: 9));
            var train = new NetworkTrain(world, new TrackPosition(1, 20), TrackDirection.Backward, 80);
            var engineer = new Engineer();
            engineer.SetRoad(ConsistEnd.A, 5);
            Drive.Run(engineer, train, world, maxSeconds: 60);
            Assert.Equal(goes, train.EndA.Track == 0);
        }
    }
}
