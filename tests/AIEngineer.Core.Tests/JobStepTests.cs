using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>
/// A job run's plan worked out off the game's thread: the world frozen at
/// one moment (cars, switches, holds), the reach checks and the planner as
/// one call that reads nothing else, and the engineer standing meanwhile.
/// </summary>
public class JobStepTests
{
    // The yard of JobTests: a lead (0) whose far end splits into the stubs Y1 (1) and Y2 (2);
    // the main (3) runs from the lead's Start to the station's stubs D1 (4) and D2 (5).
    private const int Lead = 0, Y1 = 1, Y2 = 2, Main = 3, D1 = 4, D2 = 5;

    private static TrackGraph Network(out int yard, out int station)
    {
        var b = new TrackGraphBuilder();
        var lead = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(400, 0));
        var y1 = b.AddTrack(300, new MapPoint(400, 0), new MapPoint(700, 0));
        var y2 = b.AddTrack(300, new MapPoint(400, 0), new MapPoint(700, 40));
        yard = b.AddJunction(lead, TrackEnd.End, new[] { (y1, TrackEnd.Start), (y2, TrackEnd.Start) }, new MapPoint(400, 0));
        var main = b.AddTrack(3000, new MapPoint(0, 0), new MapPoint(-3000, 0));
        b.Join(lead, TrackEnd.Start, main, TrackEnd.Start);
        var d1 = b.AddTrack(300, new MapPoint(-3000, 0), new MapPoint(-3300, 0));
        var d2 = b.AddTrack(300, new MapPoint(-3000, 0), new MapPoint(-3300, -40));
        station = b.AddJunction(main, TrackEnd.End, new[] { (d1, TrackEnd.Start), (d2, TrackEnd.Start) }, new MapPoint(-3000, 0));
        return b.Build();
    }

    /// <summary>A light engine on the lead, 100 m in, its end A toward the yard.</summary>
    private static TrainState LightEngine() => new()
    {
        EndA = new TrackPosition(Lead, 100),
        OutwardA = TrackDirection.Forward,
        EndB = new TrackPosition(Lead, 85),
        OutwardB = TrackDirection.Backward,
        Length = 15,
        CarCount = 1,
        LocoEnd = ConsistEnd.A,
        Owner = 1,
        OnTrack = true,
        CanMove = true,
    };

    /// <summary>Four cars on Y1 bound for D1, end A toward the entry; the buffer end barred.</summary>
    private static JobCut CutOnY1() => new()
    {
        Label = "J-1 (4 cars) on Y1",
        Ends = new[]
        {
            new CutEnd(new TrackPosition(Y1, 60), TrackDirection.Backward),
            new CutEnd(new TrackPosition(Y1, 120), TrackDirection.Forward),
        },
        Destinations = new[] { D1, D1, D1, D1 },
        Home = Y1,
        Length = 60,
        Barred = new[] { false, true },
    };

    private static JobStepRequest Request(IEngineerWorld world) => new()
    {
        World = world,
        Train = LightEngine(),
        Start = new TrainPose(new TrackPosition(Lead, 100), null),
        Cuts = new List<JobCut> { CutOnY1() },
        JobCuts = 1,
        InTrain = Array.Empty<int>(),
        TrackName = t => t == D1 ? "D1" : null,
    };

    private static void StandCut(OccupancyIndex occupancy) => occupancy.Add(Y1, new OccupiedSpan(60, 120, owner: 20));

    [Fact]
    public void AStepChecksTheWaysThenPlansThePulls()
    {
        var world = new FakeWorld(Network(out _, out _));
        StandCut(world.Occupancy);
        var step = JobStep.Plan(Request(world));
        Assert.Empty(step.Notes);
        Assert.Empty(step.Plan.Problems);
        var pickup = Assert.Single(step.Plan.Pickups);
        Assert.Equal((0, 0), (pickup.Cut, pickup.End));
        Assert.Equal(new[] { D1 }, step.Plan.Drops);
        Assert.True(step.Milliseconds >= 0);
    }

    [Fact]
    public void AnEndNoRouteReachesIsBarredAndSaidSo()
    {
        var world = new FakeWorld(Network(out _, out _));
        StandCut(world.Occupancy);
        // Cars at rest on the lead between the engine and the yard: no way in.
        world.Occupancy.Add(Lead, new OccupiedSpan(300, 350, owner: 30));
        var request = Request(world);
        var step = JobStep.Plan(request);
        Assert.Contains(step.Notes, n => n.Contains("no route reaches its A end"));
        Assert.True(request.Cuts[0].Barred[0]);
        Assert.Empty(step.Plan.Pickups);
        Assert.Contains(step.Plan.Problems, p => p.Contains("no loco can get at it"));
    }

    [Fact]
    public void AnEndWithNoWayOnToWhereItsCarsGoIsBarred()
    {
        var world = new FakeWorld(Network(out _, out _));
        StandCut(world.Occupancy);
        // Cars at rest across the station's entry: the cut can be had, and taken nowhere.
        world.Occupancy.Add(Main, new OccupiedSpan(2900, 2990, owner: 30));
        var request = Request(world);
        var step = JobStep.Plan(request);
        Assert.Contains(step.Notes, n => n.Contains("no route takes it on to D1"));
        Assert.True(request.Cuts[0].Barred[0]);
    }

    private static FrozenWorld Freeze(TrackGraph graph, OccupancyIndex occupancy, Interlocking interlocking, int[] selected, double now) =>
        new(graph, occupancy, interlocking, j => selected[j], now);

    [Fact]
    public void AFrozenWorldKeepsAnsweringAsThingsStoodWhenItWasMade()
    {
        var graph = Network(out var yard, out var station);
        var occupancy = new OccupancyIndex();
        var interlocking = new Interlocking(graph, occupancy);
        var selected = new int[graph.JunctionCount];
        selected[yard] = 1;
        // Another train's cars at rest across the yard switch's points; another engineer holds the station's.
        occupancy.Add(Lead, new OccupiedSpan(380, 400, owner: 30));
        interlocking.Hold(station, owner: 5, now: 100);
        var frozen = Freeze(graph, occupancy, interlocking, selected, now: 100);

        // The game goes on: the cars leave, the switch is thrown, the hold runs out and another takes it.
        occupancy.Clear();
        selected[yard] = 0;
        interlocking.Expire(now: 200);
        interlocking.Hold(yard, owner: 6, now: 200);

        Assert.True(frozen.Jammed(yard, owner: 1));
        Assert.True(frozen.PointsOccupied(yard));
        Assert.False(frozen.CanThrow(yard, owner: 1));
        Assert.False(frozen.CanThrow(station, owner: 1));
        Assert.True(frozen.CanThrow(station, owner: 5));
        Assert.Equal(1, frozen.SelectedBranch(yard));
        Assert.True(frozen.Occupancy.IsStanding(Lead, 0, 400, ignoreOwner: 1));
        // The live answers moved on.
        Assert.False(interlocking.Jammed(yard, owner: 1));
        Assert.True(interlocking.CanThrow(station, owner: 1, now: 200));
        // It is for planning: it throws and holds nothing.
        Assert.Throws<InvalidOperationException>(() => frozen.Throw(yard, 0, owner: 1));
        Assert.Throws<InvalidOperationException>(() => frozen.Reserve(yard, owner: 1));
        Assert.NotSame(new FakeWorld(graph).Router, frozen.Router);
    }

    [Fact]
    public void AStepPlannedOnAFrozenWorldIgnoresWhatHappensAfter()
    {
        var graph = Network(out _, out _);
        var occupancy = new OccupancyIndex();
        var interlocking = new Interlocking(graph, occupancy);
        var selected = new int[graph.JunctionCount];
        StandCut(occupancy);
        occupancy.Add(Lead, new OccupiedSpan(300, 350, owner: 30));
        var frozen = Freeze(graph, occupancy, interlocking, selected, now: 0);
        // The cars in the way are gone a moment later: the plan still reads the moment it was asked for.
        occupancy.Clear();
        var step = JobStep.Plan(Request(frozen));
        Assert.Contains(step.Notes, n => n.Contains("no route reaches its A end"));
        // Asked again as things stand now, it finds the way.
        StandCut(occupancy);
        var again = JobStep.Plan(Request(Freeze(graph, occupancy, interlocking, selected, now: 1)));
        Assert.Single(again.Plan.Pickups);
    }

    [Fact]
    public async Task AStepPlannedOnAnotherThreadWhileTheCarsAreRescannedComesOutTheSame()
    {
        var graph = Network(out _, out _);
        var occupancy = new OccupancyIndex();
        var interlocking = new Interlocking(graph, occupancy);
        var selected = new int[graph.JunctionCount];
        void Scan(int shift)
        {
            occupancy.Clear();
            StandCut(occupancy);
            occupancy.Add(Y2, new OccupiedSpan(40 + shift % 50, 100 + shift % 50, owner: 40));
        }
        Scan(0);
        var expected = JobStep.Plan(Request(Freeze(graph, occupancy, interlocking, selected, now: 0)));
        for (var round = 0; round < 20; round++)
        {
            Scan(0);
            var request = Request(Freeze(graph, occupancy, interlocking, selected, now: round));
            var planning = Task.Run(() => JobStep.Plan(request));
            // The game's thread meanwhile: scans, throws, holds.
            for (var k = 1; !planning.IsCompleted && k < 100000; k++)
            {
                Scan(k);
                selected[0] = k % 2;
                interlocking.Hold(k % 2, owner: 6, now: k);
            }
            var step = await planning;
            Assert.Equal(expected.Plan.Cost, step.Plan.Cost, 6);
            Assert.Equal(expected.Plan.Pickups.Count, step.Plan.Pickups.Count);
            Assert.Empty(step.Notes);
        }
    }

    [Fact]
    public void ASwitchIsHeldForItsEngineerAndFreeAgainOnceTheHoldRunsOut()
    {
        var graph = Network(out var yard, out _);
        var interlocking = new Interlocking(graph, new OccupancyIndex());
        interlocking.Hold(yard, owner: 5, now: 10);
        Assert.True(interlocking.CanThrow(yard, owner: 5, now: 11));
        Assert.False(interlocking.CanThrow(yard, owner: 1, now: 11));
        // Another's asking doesn't take it over; the holder's keeps it.
        interlocking.Reserve(yard, owner: 1, now: 12);
        Assert.False(interlocking.CanThrow(yard, owner: 1, now: 12));
        interlocking.Reserve(yard, owner: 5, now: 15);
        Assert.False(interlocking.CanThrow(yard, owner: 1, now: 15 + Interlocking.HoldSeconds - 0.1));
        Assert.True(interlocking.CanThrow(yard, owner: 1, now: 15 + Interlocking.HoldSeconds + 0.1));
    }

    [Fact]
    public void HoldsFollowATrainToTheIdACouplingGivesIt()
    {
        var graph = Network(out var yard, out _);
        var interlocking = new Interlocking(graph, new OccupancyIndex());
        interlocking.Hold(yard, owner: 5, now: 10);
        interlocking.HandOn(from: 5, to: 8);
        Assert.True(interlocking.CanThrow(yard, owner: 8, now: 11));
        Assert.False(interlocking.CanThrow(yard, owner: 5, now: 11));
    }

    [Fact]
    public void CarsAcrossThePointsStopAThrowButNotTheEngineersOwn()
    {
        var graph = Network(out var yard, out _);
        var occupancy = new OccupancyIndex();
        var interlocking = new Interlocking(graph, occupancy);
        // A car over the points from the lead: within 2.5 m of them.
        occupancy.Add(Lead, new OccupiedSpan(385, 399, owner: 30));
        Assert.False(interlocking.CanThrow(yard, owner: 1, now: 0));
        Assert.True(interlocking.CanThrow(yard, owner: 30, now: 0));
        Assert.True(interlocking.Jammed(yard, owner: 1));
        Assert.False(interlocking.Jammed(yard, owner: 30));
        Assert.True(interlocking.IsFouled(yard));
        // Clear of the points by more than that: free.
        occupancy.Clear();
        occupancy.Add(Lead, new OccupiedSpan(380, 395, owner: 30));
        Assert.True(interlocking.CanThrow(yard, owner: 1, now: 0));
        Assert.False(interlocking.IsFouled(yard));
    }

    [Fact]
    public void ATrainMovingNearASwitchKeepsOthersOffItButDoesntJamIt()
    {
        var graph = Network(out var yard, out _);
        var occupancy = new OccupancyIndex();
        var interlocking = new Interlocking(graph, occupancy);
        // 30 m short of the points on a branch, and moving: it may be about to run onto them.
        occupancy.Add(Y1, new OccupiedSpan(30, 90, owner: 30));
        Assert.True(interlocking.CanThrow(yard, owner: 1, now: 0));
        occupancy.MarkMoving(30);
        Assert.False(interlocking.CanThrow(yard, owner: 1, now: 0));
        Assert.True(interlocking.CanThrow(yard, owner: 30, now: 0));
        Assert.False(interlocking.Jammed(yard, owner: 1));
        // Farther off than the 50 m it is watched within: free.
        occupancy.Clear();
        occupancy.Add(Y1, new OccupiedSpan(60, 120, owner: 30));
        occupancy.MarkMoving(30);
        Assert.True(interlocking.CanThrow(yard, owner: 1, now: 0));
    }

    [Fact]
    public void AnEngineerToldToHoldStartsNoOrderUntilLetGo()
    {
        var graph = Network(out _, out _);
        var world = new FakeWorld(graph);
        var train = new NetworkTrain(world, new TrackPosition(Lead, 100), TrackDirection.Forward, 15) { CarCount = 1 };
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(Lead, 300)) { Label = "up the lead" });
        engineer.Holding = "Planning the jobs' next moves";
        Drive.Run(engineer, train, world, maxSeconds: 20);
        Assert.Equal("Planning the jobs' next moves", engineer.Status);
        Assert.Single(engineer.Queue);
        Assert.InRange(train.EndA.Offset, 99.9, 100.1);
        Assert.Null(engineer.Route);
        engineer.Holding = null;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.InRange(train.EndA.Offset, 295, 300.5);
    }
}
