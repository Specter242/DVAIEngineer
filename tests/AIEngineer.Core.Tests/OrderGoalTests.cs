using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class OrderGoalTests
{
    /// <summary>
    /// A switch at the origin: stem (track 0) 600 m east, branch 0 (track 1)
    /// 600 m west, branch 1 (track 2) 600 m west-southwest. Dead ends beyond.
    /// </summary>
    private static (FakeWorld World, int Junction) Wye()
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 0));
        var straight = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, 0));
        var diverging = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, -60));
        var j = b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start), (diverging, TrackEnd.Start) }, new MapPoint(0, 0));
        return (new FakeWorld(b.Build()), j);
    }

    [Fact]
    public void ATrackIsEnteredByEachEndWithTrackBeyondIt()
    {
        var (world, _) = Wye();
        // The diverging branch: in by its switch end only (its far end is a buffer stop).
        var goals = OrderGoals.OnTrack(world.Graph, 2, trainLength: 80);
        var goal = Assert.Single(goals);
        Assert.Equal(new TrackPosition(2, 95), goal.Position);
        Assert.Equal(TrackDirection.Forward, goal.Arrive);
        // A train longer than the track: the leading end goes as near the buffer as it may.
        goal = Assert.Single(OrderGoals.OnTrack(world.Graph, 2, trainLength: 900));
        Assert.Equal(new TrackPosition(2, 600 - OrderGoals.BufferMargin), goal.Position);
    }

    [Fact]
    public void GoingToATrackBringsTheWholeTrainOnClearOfTheSwitch()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(OrderGoals.OnTrack(world.Graph, 2, train.Length)) { Label = "siding" });
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(1, world.Selected[j]);
        // Both ends on the siding, the rear clear of the points.
        Assert.Equal(2, train.EndA.Track);
        Assert.Equal(2, train.EndB.Track);
        Assert.True(Math.Min(train.EndA.Offset, train.EndB.Offset) > 10, $"rear at {train.EndB}");
    }

    [Fact]
    public void PickingUpACutCouplesFromTheEndItCanReach()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        // Four cars standing 300-360 m up the diverging branch; their far end faces the buffer stop.
        world.Occupancy.Add(2, new OccupiedSpan(300, 360, owner: 9));
        var ends = new[]
        {
            new CutEnd(new TrackPosition(2, 300), TrackDirection.Backward),
            new CutEnd(new TrackPosition(2, 360), TrackDirection.Forward),
        };
        var engineer = new Engineer();
        var waypoint = new Waypoint(OrderGoals.ToCouple(world.Graph, ends, world.SelectedBranch), WaypointAction.Couple);
        engineer.GoTo(waypoint);
        var coupled = false;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0, onOutput: output =>
        {
            if (coupled || output.CoupleAt != ConsistEnd.A)
                return;
            if (train.EndA.Track == 2 && 300 - train.EndA.Offset <= 1.5)
            {
                coupled = true;
                world.Occupancy.Clear();
                train.CoupleOn(ConsistEnd.A, 60, 4);
            }
        });
        Assert.True(coupled);
        Assert.Empty(engineer.Queue);
        Assert.Equal(10, train.CarCount);
        // It took the near end: the goal short of it, arriving up the branch.
        Assert.Equal(new TrackPosition(2, 300 - OrderGoals.ShortOfCut), waypoint.Position);
        Assert.Equal(TrackDirection.Forward, waypoint.Goal.Arrive);
    }

    [Fact]
    public void ACutIsCoupledOnTheEndThatLeadsItToItsDestination()
    {
        var (world, _) = Wye();
        // Cars on the stem, 300-360 m east of the switch.
        var west = new CutEnd(new TrackPosition(0, 300), TrackDirection.Backward);
        var east = new CutEnd(new TrackPosition(0, 360), TrackDirection.Forward);
        // Bound beyond the switch: the west end leads.
        Assert.Equal(0, OrderGoals.EndToward(world.Router, west, east, 80, new TrackPosition(1, 300)));
        Assert.Equal(1, OrderGoals.EndToward(world.Router, east, west, 80, new TrackPosition(1, 300)));
        // Bound for the stem's far end: the east end does.
        Assert.Equal(1, OrderGoals.EndToward(world.Router, west, east, 80, new TrackPosition(0, 550)));
    }

    [Fact]
    public void AnEndThatCanOnlyBeReachedThroughTheCutGivesWayToTheOther()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        // Cars 300-360 m up the diverging branch: their far end faces the buffer stop, past them.
        world.Occupancy.Add(2, new OccupiedSpan(300, 360, owner: 9));
        var ends = new[]
        {
            new CutEnd(new TrackPosition(2, 300), TrackDirection.Backward),
            new CutEnd(new TrackPosition(2, 360), TrackDirection.Forward),
        };
        var goals = OrderGoals.ToCouple(world.Graph, ends, preferred: 1, new[] { "near", "far" }, world.SelectedBranch);
        var waypoint = new Waypoint(goals, WaypointAction.Couple) { Label = "the cut" };
        var engineer = new Engineer();
        engineer.GoTo(waypoint);
        engineer.Tick(0.2, train.State(), world);
        Assert.NotNull(engineer.Route);
        Assert.Equal(new TrackPosition(2, 300 - OrderGoals.ShortOfCut), waypoint.Position);
        Assert.Contains("coupling on the near end: the far end can't be reached", engineer.Note);
    }

    [Fact]
    public void TheFarEndOfACutIsReachedTheLongWayRoundNotThroughIt()
    {
        // A loop: west, then a 250 m and a 400 m way side by side, then east (200 m, a buffer stop beyond).
        var b = new TrackGraphBuilder();
        var west = b.AddTrack(100, new MapPoint(-100, 0), new MapPoint(0, 0));
        var longWay = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(200, 0));
        var shortWay = b.AddTrack(250, new MapPoint(0, 0), new MapPoint(200, 0));
        var east = b.AddTrack(200, new MapPoint(200, 0), new MapPoint(400, 0));
        b.AddJunction(west, TrackEnd.End, new[] { (longWay, TrackEnd.Start), (shortWay, TrackEnd.Start) }, new MapPoint(0, 0));
        b.AddJunction(east, TrackEnd.Start, new[] { (longWay, TrackEnd.End), (shortWay, TrackEnd.End) }, new MapPoint(200, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(west, 90), TrackDirection.Forward, 20);
        world.Occupancy.Add(shortWay, new OccupiedSpan(100, 140, owner: 9));
        var ends = new[]
        {
            new CutEnd(new TrackPosition(shortWay, 100), TrackDirection.Backward),
            new CutEnd(new TrackPosition(shortWay, 140), TrackDirection.Forward),
        };
        var waypoint = new Waypoint(OrderGoals.ToCouple(world.Graph, ends, preferred: 1, new[] { "west", "east" }, world.SelectedBranch), WaypointAction.Couple);
        var engineer = new Engineer();
        engineer.GoTo(waypoint);
        engineer.Tick(0.2, train.State(), world);
        Assert.Equal(new TrackPosition(shortWay, 140 + OrderGoals.ShortOfCut), waypoint.Position);
        Assert.Null(engineer.Note);
        var route = engineer.Route!;
        // Round by the long way, reversing on the east track, never across the cars.
        Assert.Contains(route.Legs[0].Pieces, p => p.Track == longWay);
        for (var l = 0; l < route.Legs.Count - 1; l++)
            Assert.DoesNotContain(route.Legs[l].Pieces, p => p.Track == shortWay);
    }

    [Fact]
    public void CarsGoFirstIntoADeadEndTrackByDefault()
    {
        var (world, _) = Wye();
        var waypoint = OrderGoals.ToTrack(world.Graph, 2, trainLength: 80, label: "siding");
        Assert.True(waypoint.IntoDeadEnd);
        Assert.Equal(Arrival.CarsFirstIntoDeadEnd, waypoint.Arrival);
        var train = new TrainState { CarCount = 4, LocoEnd = ConsistEnd.A, LocoFront = ConsistEnd.A };
        Assert.Equal(ConsistEnd.B, Engineer.Leader(waypoint.Arrival, train, waypoint.IntoDeadEnd));
        // Through tracks, and a loco running light, go either way.
        Assert.Null(Engineer.Leader(Arrival.CarsFirstIntoDeadEnd, train, intoDeadEnd: false));
        var light = new TrainState { CarCount = 1, LocoEnd = ConsistEnd.A, LocoFront = ConsistEnd.A };
        Assert.Null(Engineer.Leader(Arrival.CarsFirstIntoDeadEnd, light, intoDeadEnd: true));
    }

    [Fact]
    public void OfSeveralGoalsTheCheapestIsTaken()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var far = new WaypointGoal(new TrackPosition(1, 550));
        var near = new WaypointGoal(new TrackPosition(0, 450));
        var waypoint = new Waypoint(new[] { far, near });
        var engineer = new Engineer();
        engineer.GoTo(waypoint);
        engineer.Tick(0.2, train.State(), world);
        Assert.Equal(near.Position, waypoint.Position);
        Assert.NotNull(engineer.Route);
        Assert.Equal(0, engineer.Route!.Reversals);
    }

    [Fact]
    public void ATrackOrderQueuedBehindAPickupStopsForTheLongerTrain()
    {
        var (world, _) = Wye();
        var waypoint = OrderGoals.ToTrack(world.Graph, 2, trainLength: 20, label: "siding");
        Assert.Equal(new TrackPosition(2, 35), waypoint.Position);
        // By the time it starts the train has picked up cars: 140 m long.
        waypoint.Resize(new TrainState { Length = 140 });
        Assert.Equal(new TrackPosition(2, 155), waypoint.Position);
    }
}
