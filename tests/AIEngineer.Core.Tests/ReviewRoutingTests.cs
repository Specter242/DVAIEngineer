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

    [Fact]
    public void EngineerHoldsShortOfMisalignedSwitchWithinOneMeter()
    {
        var world = new FakeWorld(Wye(stem: 600, branch: 600));
        // Plan while the switch is free, then bring the nose to 0.5 m from the points with the switch misaligned.
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)));
        engineer.Tick(0.2, train.State(), world);
        Assert.NotNull(engineer.Route);

        // Move nose to 0.5 m before the switch (at offset 0), with switch misaligned back to branch 0.
        train.Move(199.5);
        world.Selected[0] = 0;
        world.Throws.Clear();

        var state = train.State();
        state.Reverser = ConsistEnd.A;
        var output = engineer.Tick(0.2, state, world);
        // Must not throw within 1 m, and must NOT ignore the misaligned switch.
        Assert.Empty(world.Throws);
        Assert.Equal(0, world.Selected[0]);
        Assert.True(engineer.StopAt < engineer.Progress, $"StopAt {engineer.StopAt} should be behind progress {engineer.Progress}");
        Assert.Equal(0, engineer.TargetSpeed, 6);
        Assert.Contains("switch that can't be set", engineer.Status);
        Assert.Equal(0, output.Controls.Throttle, 6);

        // Once aligned, the switch at 0.5 m is reserved and the train may proceed.
        world.Selected[0] = 1;
        engineer.Tick(0.2, state, world);
        Assert.True(engineer.TargetSpeed > 0);
    }

    [Fact]
    public void LoopSwitchIsNotThrownForSecondPassWhenWithinOneMeterOfFirstPass()
    {
        // A short loop (80 m < SwitchReachMin = 150 m) so both passes of junction j are within reach.
        // At 0.5 m before the first pass, WantedSooner must still see the nearer pass and keep j on branch 0.
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(400, 0));
        var round = b.AddTrack(80, new MapPoint(400, 0), new MapPoint(400, 0.001));
        var j = b.AddJunction(stem, TrackEnd.End, new[] { (round, TrackEnd.Start), (round, TrackEnd.End) }, new MapPoint(400, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(stem, 200), TrackDirection.Forward, 20);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new[] { new WaypointGoal(new TrackPosition(stem, 50), TrackDirection.Backward) }) { Arrival = Arrival.FrontFirst });
        var state = train.State();
        state.LocoEnd = ConsistEnd.A;
        state.LocoFront = ConsistEnd.A;
        state.Reverser = ConsistEnd.A;
        engineer.Tick(0.2, state, world);
        Assert.NotNull(engineer.Route);
        var firstBranch = engineer.Route.Legs[0].Switches[0].Branch;
        Assert.Equal(2, engineer.Route.Legs[0].Switches.Count);
        Assert.NotEqual(firstBranch, engineer.Route.Legs[0].Switches[1].Branch);

        // Place nose 0.5 m short of the first pass with switch lined for the first pass.
        train.Move(199.5);
        world.Selected[j] = firstBranch;
        world.Throws.Clear();
        state = train.State();
        state.LocoEnd = ConsistEnd.A;
        state.LocoFront = ConsistEnd.A;
        state.Reverser = ConsistEnd.A;
        engineer.Tick(0.2, state, world);
        Assert.Empty(world.Throws);
        Assert.Equal(firstBranch, world.Selected[j]);
    }

    [Fact]
    public void InterlockingApproachProtectionCrossesPlainJointsAndHandlesCycles()
    {
        // Stem is a 24 m turnout joined to a 100 m lead; branch 0 is a 24 m turnout joined to a 100 m track.
        var b = new TrackGraphBuilder();
        var stemTurnout = b.AddTrack(24, new MapPoint(0, 0), new MapPoint(24, 0));
        var stemLead = b.AddTrack(100, new MapPoint(24, 0), new MapPoint(124, 0));
        var b0Turnout = b.AddTrack(24, new MapPoint(0, 0), new MapPoint(-24, 0));
        var b0Lead = b.AddTrack(100, new MapPoint(-24, 0), new MapPoint(-124, 0));
        var b1 = b.AddTrack(100, new MapPoint(0, 0), new MapPoint(-100, -10));
        var j = b.AddJunction(stemTurnout, TrackEnd.Start, new[] { (b0Turnout, TrackEnd.Start), (b1, TrackEnd.Start) }, new MapPoint(0, 0));
        b.Join(stemTurnout, TrackEnd.End, stemLead, TrackEnd.Start);
        b.Join(b0Turnout, TrackEnd.End, b0Lead, TrackEnd.Start);
        var graph = b.Build();

        var occ = new OccupancyIndex();
        var locking = new Interlocking(graph, occ);

        // Moving train on stemLead at 20..30 m -> 44 m from points (within 50 m): cannot throw.
        occ.Add(stemLead, new OccupiedSpan(20, 30, owner: 2));
        occ.MarkMoving(2);
        Assert.False(locking.CanThrow(j, owner: 1, now: 0));

        // Moving train at 30..40 m -> 54 m from points (beyond 50 m): can throw.
        occ.Clear();
        occ.Add(stemLead, new OccupiedSpan(30, 40, owner: 2));
        occ.MarkMoving(2);
        Assert.True(locking.CanThrow(j, owner: 1, now: 0));

        // Moving train on branch 0 beyond its 24 m turnout, at 20..30 m -> 44 m from points: cannot throw.
        occ.Clear();
        occ.Add(b0Lead, new OccupiedSpan(20, 30, owner: 2));
        occ.MarkMoving(2);
        Assert.False(locking.CanThrow(j, owner: 1, now: 0));

        // Cycle of short tracks (< 50 m loop) terminates cleanly.
        var cb = new TrackGraphBuilder();
        var s = cb.AddTrack(10, new MapPoint(0, 0), new MapPoint(10, 0));
        var l1 = cb.AddTrack(10, new MapPoint(0, 0), new MapPoint(-10, 0));
        var l2 = cb.AddTrack(10, new MapPoint(0, 0), new MapPoint(-10, -5));
        var cj = cb.AddJunction(s, TrackEnd.Start, new[] { (l1, TrackEnd.Start), (l2, TrackEnd.Start) }, new MapPoint(0, 0));
        cb.Join(s, TrackEnd.End, l1, TrackEnd.End);
        var cGraph = cb.Build();
        var cOcc = new OccupancyIndex();
        cOcc.Add(l2, new OccupiedSpan(5, 9, owner: 9));
        cOcc.MarkMoving(9);
        var cLock = new Interlocking(cGraph, cOcc);
        Assert.False(cLock.CanThrow(cj, owner: 1, now: 0));
        cOcc.Clear();
        Assert.True(cLock.CanThrow(cj, owner: 1, now: 0));
    }

    [Fact]
    public void FoulingReachesPastFourShortPiecesAndHandlesCycles()
    {
        // Branch 1 consists of 5 short 6 m pieces (total 30 m < 35 m reach), then a 20 m piece.
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(200, new MapPoint(0, 0), new MapPoint(200, 0));
        var b0 = b.AddTrack(200, new MapPoint(0, 0), new MapPoint(-200, 0));
        var pieces = new int[6];
        for (var i = 0; i < 5; i++)
            pieces[i] = b.AddTrack(6, new MapPoint(-i * 6, -5), new MapPoint(-(i + 1) * 6, -5));
        pieces[5] = b.AddTrack(20, new MapPoint(-30, -5), new MapPoint(-50, -5));
        var j = b.AddJunction(stem, TrackEnd.Start, new[] { (b0, TrackEnd.Start), (pieces[0], TrackEnd.Start) }, new MapPoint(0, 0));
        for (var i = 0; i < 5; i++)
            b.Join(pieces[i], TrackEnd.End, pieces[i + 1], TrackEnd.Start);
        var graph = b.Build();

        var occ = new OccupancyIndex();
        // Car on piece[4] (24..30 m from points) -> within 35 m reach, across 4 joints (5th piece).
        occ.Add(pieces[4], new OccupiedSpan(2, 5, owner: 9));
        Assert.True(Fouling.OtherBranchesFouled(graph, occ, j, branch: 0, ignoreOwner: 1));
        Assert.True(Fouling.OtherBranchesFouled(graph, occ, j, branch: 0, ignoreOwner: 1, standingOnly: true));

        // Car on piece[5] at 10..15 m -> 40..45 m from points (beyond 35 m reach).
        occ.Clear();
        occ.Add(pieces[5], new OccupiedSpan(10, 15, owner: 9));
        Assert.False(Fouling.OtherBranchesFouled(graph, occ, j, branch: 0, ignoreOwner: 1));

        // Cycle of two 5 m tracks on branch 1 terminates cleanly.
        var cb = new TrackGraphBuilder();
        var cs = cb.AddTrack(100, new MapPoint(0, 0), new MapPoint(100, 0));
        var c0 = cb.AddTrack(100, new MapPoint(0, 0), new MapPoint(-100, 0));
        var c1a = cb.AddTrack(5, new MapPoint(0, 0), new MapPoint(-5, -5));
        var c1b = cb.AddTrack(5, new MapPoint(-5, -5), new MapPoint(0, 0));
        var cj = cb.AddJunction(cs, TrackEnd.Start, new[] { (c0, TrackEnd.Start), (c1a, TrackEnd.Start) }, new MapPoint(0, 0));
        cb.Join(c1a, TrackEnd.End, c1b, TrackEnd.Start);
        cb.Join(c1b, TrackEnd.End, c1a, TrackEnd.Start);
        Assert.False(Fouling.OtherBranchesFouled(cb.Build(), new OccupancyIndex(), cj, branch: 0, ignoreOwner: 1));
    }

    [Fact]
    public void RoutePlannerComparesMultiGoalCandidatesBySearchCost()
    {
        // Two candidate goals:
        // Goal 0 (d0b @ 10) goes through locked switch jh (HeldCost = 300 m): physical 160 m, SearchCost 470.
        // Goal 1 (d1 @ 200) uses free switch: physical 250 m, SearchCost 260.
        var b = new TrackGraphBuilder();
        var s = b.AddTrack(200, new MapPoint(0, 0), new MapPoint(200, 0));
        var d0 = b.AddTrack(100, new MapPoint(0, 0), new MapPoint(-100, 0));
        var d1 = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(-400, -10));
        var d0a = b.AddTrack(200, new MapPoint(-100, 0), new MapPoint(-300, 0));
        var d0b = b.AddTrack(200, new MapPoint(-100, 0), new MapPoint(-300, -5));
        var jm = b.AddJunction(s, TrackEnd.Start, new[] { (d0, TrackEnd.Start), (d1, TrackEnd.Start) }, new MapPoint(0, 0));
        var jh = b.AddJunction(d0, TrackEnd.End, new[] { (d0a, TrackEnd.Start), (d0b, TrackEnd.Start) }, new MapPoint(-100, 0));
        var world = new FakeWorld(b.Build());
        world.Selected[jm] = 0;
        world.Selected[jh] = 0;
        world.Locked.Add(jh);

        var train = new NetworkTrain(world, new TrackPosition(s, 50), TrackDirection.Backward, 40);
        var wp = new Waypoint(new[]
        {
            new WaypointGoal(new TrackPosition(d0b, 10)),
            new WaypointGoal(new TrackPosition(d1, 200)),
        });
        var plan = RoutePlanner.Plan(wp, train.State(), world, lastLeader: null);
        Assert.NotNull(plan.Route);
        Assert.Equal(1, plan.GoalIndex);
    }

    [Fact]
    public void RoutePlannerSoftFacingUsesSearchCostWithSwitchPenalties()
    {
        // Wye with legs of 180 m: normally a light loco turns on the wye to couple back-first
        // (turning costs < 1000 m extra). Locking all three wye switches against the turn adds
        // 3 * 300 = 900 m of HeldCost to SearchCost, pushing it over the 1000 m FacingAllowance.
        var b = new TrackGraphBuilder();
        var west = b.AddTrack(900, new MapPoint(-1000, 0), new MapPoint(-100, 0));
        var centre = b.AddTrack(200, new MapPoint(-100, 0), new MapPoint(100, 0));
        var east = b.AddTrack(900, new MapPoint(100, 0), new MapPoint(1000, 0));
        var westLeg = b.AddTrack(180, new MapPoint(-100, 0), new MapPoint(0, 150));
        var eastLeg = b.AddTrack(180, new MapPoint(100, 0), new MapPoint(0, 150));
        var tail = b.AddTrack(300, new MapPoint(0, 150), new MapPoint(0, 450));
        var jW = b.AddJunction(west, TrackEnd.End, new[] { (centre, TrackEnd.Start), (westLeg, TrackEnd.Start) }, new MapPoint(-100, 0));
        var jE = b.AddJunction(east, TrackEnd.Start, new[] { (centre, TrackEnd.End), (eastLeg, TrackEnd.Start) }, new MapPoint(100, 0));
        var jApex = b.AddJunction(tail, TrackEnd.Start, new[] { (westLeg, TrackEnd.End), (eastLeg, TrackEnd.End) }, new MapPoint(0, 150));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(east, 100), TrackDirection.Backward, 20) { CarCount = 1 };
        var state = train.State();
        state.LocoEnd = ConsistEnd.A;
        state.LocoFront = ConsistEnd.A;

        var wp = new Waypoint(new TrackPosition(west, 300), WaypointAction.Couple) { Arrival = Arrival.BackFirst };
        // Unheld: turns on wye to couple back-first (End B leading).
        var freePlan = RoutePlanner.Plan(wp, state, world, lastLeader: ConsistEnd.B);
        Assert.NotNull(freePlan.Route);
        Assert.Equal(ConsistEnd.B, freePlan.Route.Legs[^1].Leader);

        // Lock wye switches while they lie for straight-through (branch 0): turning incurs HeldCost,
        // so SearchCost exceeds direct + FacingAllowance and softFacing gives up the turn.
        world.Locked.Add(jW);
        world.Locked.Add(jE);
        world.Locked.Add(jApex);
        var heldPlan = RoutePlanner.Plan(wp, state, world, lastLeader: ConsistEnd.B);
        Assert.NotNull(heldPlan.Route);
        Assert.Equal(ConsistEnd.A, heldPlan.Route.Legs[^1].Leader);
        Assert.NotNull(heldPlan.FacingGivenUp);
    }

    [Fact]
    public void ShortTurnoutSafetyChecksDoNotAllocateOnRepeatedCalls()
    {
        // 24 m turnout branches joined to 100 m tracks: forces extended multi-piece traversal
        // in both Interlocking.CanThrow (50 m) and Fouling.OtherBranchesFouled (35 m).
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(24, new MapPoint(0, 0), new MapPoint(24, 0));
        var stemLead = b.AddTrack(100, new MapPoint(-100, 0), new MapPoint(0, 0));
        var br0 = b.AddTrack(24, new MapPoint(24, 0), new MapPoint(48, 0));
        var br1 = b.AddTrack(24, new MapPoint(24, 0), new MapPoint(48, 6));
        var ext0 = b.AddTrack(100, new MapPoint(48, 0), new MapPoint(148, 0));
        var ext1 = b.AddTrack(100, new MapPoint(48, 6), new MapPoint(148, 12));
        b.Join(stemLead, TrackEnd.End, stem, TrackEnd.Start);
        var j = b.AddJunction(stem, TrackEnd.End, new[] { (br0, TrackEnd.Start), (br1, TrackEnd.Start) }, new MapPoint(24, 0));
        b.Join(br0, TrackEnd.End, ext0, TrackEnd.Start);
        b.Join(br1, TrackEnd.End, ext1, TrackEnd.Start);
        var graph = b.Build();
        var occupancy = new OccupancyIndex();
        var interlocking = new Interlocking(graph, occupancy);

        // Warm up reusable scratch storage.
        Assert.True(interlocking.CanThrow(j, owner: 1, now: 0));
        Assert.False(Fouling.OtherBranchesFouled(graph, occupancy, j, branch: 0, ignoreOwner: 1));

        var beforeThrow = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            Assert.True(interlocking.CanThrow(j, owner: 1, now: i));
        var afterThrow = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterThrow - beforeThrow);

        var beforeFouling = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            Assert.False(Fouling.OtherBranchesFouled(graph, occupancy, j, branch: 0, ignoreOwner: 1));
        var afterFouling = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterFouling - beforeFouling);
    }
}
