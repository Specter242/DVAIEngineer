using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class TurningTests
{
    /// <summary>
    /// A main line east-west with a wye on it: switches J1 (x = -100) and
    /// J2 (x = 100) send legs north to an apex switch J3, whose stem is a
    /// 300 m tail track ending in a buffer stop.
    /// Tracks: 0 west main, 1 centre, 2 east main, 3 west leg, 4 east leg, 5 tail.
    /// </summary>
    private static TrackGraph Wye()
    {
        var b = new TrackGraphBuilder();
        var west = b.AddTrack(900, new MapPoint(-1000, 0), new MapPoint(-100, 0));
        var centre = b.AddTrack(200, new MapPoint(-100, 0), new MapPoint(100, 0));
        var east = b.AddTrack(900, new MapPoint(100, 0), new MapPoint(1000, 0));
        var westLeg = b.AddTrack(180, new MapPoint(-100, 0), new MapPoint(0, 150));
        var eastLeg = b.AddTrack(180, new MapPoint(100, 0), new MapPoint(0, 150));
        var tail = b.AddTrack(300, new MapPoint(0, 150), new MapPoint(0, 450));
        b.AddJunction(west, TrackEnd.End, new[] { (centre, TrackEnd.Start), (westLeg, TrackEnd.Start) }, new MapPoint(-100, 0));
        b.AddJunction(east, TrackEnd.Start, new[] { (centre, TrackEnd.End), (eastLeg, TrackEnd.Start) }, new MapPoint(100, 0));
        b.AddJunction(tail, TrackEnd.Start, new[] { (westLeg, TrackEnd.End), (eastLeg, TrackEnd.End) }, new MapPoint(0, 150));
        return b.Build();
    }

    /// <summary>A train on the east main, end A west (toward the wye), 50 m long, bound west for the west main.</summary>
    private static RouteRequest Westbound(ConsistEnd? lastLeader) => new()
    {
        EndA = new TrackPosition(2, 400),
        OutwardA = TrackDirection.Backward,
        EndB = new TrackPosition(2, 450),
        OutwardB = TrackDirection.Forward,
        TrainLength = 50,
        Goal = new TrackPosition(0, 500),
        GoalDirection = TrackDirection.Backward,
        LastLeader = lastLeader,
    };

    [Fact]
    public void WithTheLeadingEndAsItIsTheTrainRunsStraightThrough()
    {
        var route = new Router(Wye()).Find(Westbound(ConsistEnd.A));
        Assert.NotNull(route);
        Assert.Equal(0, route!.Reversals);
        Assert.Equal(ConsistEnd.A, route.Legs[^1].Leader);
    }

    [Fact]
    public void ToArriveWithTheOtherEndLeadingItTurnsOnTheWye()
    {
        var route = new Router(Wye()).Find(Westbound(ConsistEnd.B), out var failure);
        Assert.Equal(RouteFailure.None, failure);
        Assert.NotNull(route);
        Assert.Equal(1, route!.Reversals);
        Assert.Equal(ConsistEnd.B, route.Legs[^1].Leader);
        // It reversed on the tail (track 5).
        Assert.Contains(route.Legs[0].Pieces, p => p.Track == 5);
        Assert.Equal(0, route.Legs[^1].Pieces[^1].Track);
    }

    [Fact]
    public void WithNoWayToTurnTheOtherEndCannotLead()
    {
        // One switch and dead ends: a train can switch back, but never turn.
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 0));
        var straight = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, 0));
        var diverging = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, -60));
        b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start), (diverging, TrackEnd.Start) }, new MapPoint(0, 0));
        var request = new RouteRequest
        {
            EndA = new TrackPosition(stem, 100),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(stem, 150),
            OutwardB = TrackDirection.Forward,
            TrainLength = 50,
            Goal = new TrackPosition(diverging, 300),
            GoalDirection = TrackDirection.Forward,
        };
        var router = new Router(b.Build());
        request.LastLeader = ConsistEnd.A;
        Assert.NotNull(router.Find(request));
        request.LastLeader = ConsistEnd.B;
        Assert.Null(router.Find(request, out var failure));
        Assert.Equal(RouteFailure.NoPath, failure);
    }

    [Fact]
    public void AnOrderForCarsFirstTurnsTheTrainOnTheWye()
    {
        var world = new FakeWorld(Wye());
        // The loco at end A (the west end), its front that way too: cars behind, to the east.
        var train = new NetworkTrain(world, new TrackPosition(2, 400), TrackDirection.Backward, 50);
        var engineer = new Engineer();
        var waypoint = new Waypoint(new[] { new WaypointGoal(new TrackPosition(0, 500), TrackDirection.Backward) })
        {
            Arrival = Arrival.CarsFirst,
            Label = "west main, cars first",
        };
        engineer.GoTo(waypoint);
        Drive.Run(engineer, train, world, maxSeconds: 1500, until: () => engineer.Queue.Count == 0,
            state: s => { s.LocoEnd = ConsistEnd.A; s.LocoFront = ConsistEnd.A; return s; });
        Assert.Empty(engineer.Queue);
        Assert.True(waypoint.ArrivalMet);
        // End B (the cars) led west onto the west main, stopping at the goal from the east.
        Assert.Equal(0, train.EndB.Track);
        Assert.InRange(train.EndB.Offset, 499.5, 505);
        Assert.True(train.EndA.Offset > train.EndB.Offset, $"A at {train.EndA}, B at {train.EndB}");
    }

    [Fact]
    public void WhenNoRouteArrivesAsAskedItGoesAnywayAndSaysSo()
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 0));
        var straight = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, 0));
        b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start) }, new MapPoint(0, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(stem, 300), TrackDirection.Backward, 50);
        var engineer = new Engineer();
        var waypoint = new Waypoint(new TrackPosition(straight, 200)) { Arrival = Arrival.CarsFirst };
        engineer.GoTo(waypoint);
        Drive.Run(engineer, train, world, maxSeconds: 600, until: () => engineer.Queue.Count == 0,
            state: s => { s.LocoEnd = ConsistEnd.A; s.LocoFront = ConsistEnd.A; return s; });
        Assert.Empty(engineer.Queue);
        Assert.False(waypoint.ArrivalMet);
        Assert.NotNull(engineer.Note);
    }
}
