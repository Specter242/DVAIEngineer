using System.Globalization;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class ConductorTests
{
    private const double Tick = 0.2;

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

    /// <summary>An 80 m train on the stem with end A toward the switch, its nose this far from the points.</summary>
    private static NetworkTrain OnStem(FakeWorld world, double fromPoints = 400) =>
        new(world, new TrackPosition(0, fromPoints), TrackDirection.Backward, 80);

    /// <summary>The train rolled by hand, the conductor ticking as it goes; stops early when asked.</summary>
    private static void Roll(Conductor conductor, NetworkTrain train, FakeWorld world, double towardA, double meters, Func<bool>? until = null)
    {
        var speed = Math.Abs(towardA);
        var left = meters;
        train.SetSpeed(towardA);
        while (left > 1e-6)
        {
            var step = Math.Min(left, speed * Tick);
            train.Move(Math.Sign(towardA) * step);
            left -= step;
            conductor.Tick(Tick, train.State(), world);
            if (until != null && until())
                break;
        }
        train.SetSpeed(0);
    }

    private static void Stand(Conductor conductor, NetworkTrain train, FakeWorld world, double seconds = Tick)
    {
        train.SetSpeed(0);
        for (var t = 0.0; t < seconds - 1e-6; t += Tick)
            conductor.Tick(Tick, train.State(), world);
    }

    private static Conductor Executing(FakeWorld world, NetworkTrain train, TrackPosition goal, bool anywhereOnTrack = false)
    {
        var conductor = new Conductor();
        Assert.True(conductor.Plan(new Waypoint(goal) { Label = "the siding" }, anywhereOnTrack, train.State(), world));
        Assert.Equal(ConductorExecution.Started, conductor.Execute(train.State(), world));
        return conductor;
    }

    private static List<ConductorCallKind> Notices(Conductor conductor)
    {
        var said = new List<ConductorCallKind>();
        while (conductor.TryTakeNotice(out var call))
            said.Add(call.Kind);
        return said;
    }

    [Fact]
    public void NothingIsThrownBeforeExecute()
    {
        var (world, _) = Wye();
        var train = OnStem(world, 100);
        var conductor = new Conductor();
        Assert.True(conductor.Plan(new Waypoint(new TrackPosition(2, 300)), false, train.State(), world));
        Assert.Equal(ConductorState.Planned, conductor.State);
        Assert.Equal(1, conductor.SwitchesToThrow);
        Assert.NotNull(conductor.Route);
        Stand(conductor, train, world, 2);
        Assert.Empty(world.Throws);
    }

    [Fact]
    public void WithNoRouteThereIsNoPlan()
    {
        var (world, j) = Wye();
        world.JammedSwitches.Add(j);
        var train = OnStem(world);
        var conductor = new Conductor();
        Assert.False(conductor.Plan(new Waypoint(new TrackPosition(2, 300)), false, train.State(), world));
        Assert.Equal(ConductorState.None, conductor.State);
        Assert.Equal(ConductorCallKind.NoRoute, conductor.Call.Kind);
    }

    [Fact]
    public void ARouteThatChangedIsShownBeforeItStarts()
    {
        var (world, j) = Wye();
        // On the straight branch: the siding is a reversal away while the stem is open.
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var conductor = new Conductor();
        Assert.True(conductor.Plan(new Waypoint(new TrackPosition(1, 500)), false, train.State(), world));
        var shown = conductor.Route!;
        // The train has moved a long way before the word is given.
        train.Move(-300);
        Assert.Equal(ConductorExecution.RouteChanged, conductor.Execute(train.State(), world));
        Assert.Equal(ConductorState.Planned, conductor.State);
        Assert.NotSame(shown, conductor.Route);
        Assert.Equal(ConductorCallKind.NewRoute, conductor.Call.Kind);
        Assert.Equal(ConductorExecution.Started, conductor.Execute(train.State(), world));
        Assert.Empty(world.Throws);
        Assert.Equal(0, world.Selected[j]);
    }

    [Fact]
    public void ASwitchIsLinedOnlyInReachAndAhead()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 400);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        Stand(conductor, train, world, 1);
        Assert.Empty(world.Throws);
        // At 5 m/s the reach is the 70 m braking distance and 100 m: 170 m.
        Roll(conductor, train, world, 5, 220);
        Assert.Empty(world.Throws);
        Roll(conductor, train, world, 5, 20);
        Assert.Equal(new[] { (j, 1) }, world.Throws);
        Assert.Equal(1, conductor.SwitchesThrown);
    }

    [Fact]
    public void AFastTrainGetsItsSwitchSooner()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 500);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        double? thrownAt = null;
        Roll(conductor, train, world, 20, 480, () =>
        {
            if (world.Throws.Count > 0)
                thrownAt ??= train.EndA.Offset;
            return thrownAt != null;
        });
        // 20 m/s with cars: the braking distance alone is 880 m, so it is thrown at once.
        Assert.NotNull(thrownAt);
        Assert.True(thrownAt > 400, $"thrown {thrownAt:F0} m out");
        Assert.Equal(1, world.Selected[j]);
    }

    [Fact]
    public void ATrailingSwitchIsLinedToo()
    {
        var (world, j) = Wye();
        // Coming off the diverging branch onto the stem, the switch lying for the other one.
        var train = new NetworkTrain(world, new TrackPosition(2, 100), TrackDirection.Backward, 80);
        var conductor = Executing(world, train, new TrackPosition(0, 300));
        Stand(conductor, train, world);
        Assert.Equal(new[] { (j, 1) }, world.Throws);
    }

    [Fact]
    public void NothingIsThrownUnderTheTrain()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        world.OwnCarsAcross = junction => junction == j;
        Stand(conductor, train, world, 1);
        Assert.Empty(world.Throws);
        Assert.Equal(0, world.ThrownUnderTheTrain);
        Assert.Equal(ConductorCallKind.PullClear, conductor.Call.Kind);
        Assert.Equal(j, conductor.Call.Junction);
    }

    [Fact]
    public void ASwitchItCantSetIsAStopCallWithRoomToStop()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 500);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        world.Locked.Add(j);
        ConductorCall call = default;
        double nose = 0;
        Roll(conductor, train, world, 10, 480, () =>
        {
            call = conductor.Call;
            nose = train.EndA.Offset;
            return call.Kind == ConductorCallKind.SwitchBlocked;
        });
        Assert.Equal(ConductorCallKind.SwitchBlocked, call.Kind);
        Assert.Equal(ConductorCallLevel.Stop, call.Level);
        Assert.Empty(world.Throws);
        // At 10 m/s with cars the planned stop takes 240 m.
        var stopping = new EngineerSettings().TrainBraking.DistanceToSlow(10, 0);
        Assert.True(nose >= stopping, $"called {nose:F0} m out, {stopping:F0} m to stop");
        Assert.Contains(ConductorCallKind.SwitchBlocked, Notices(conductor));
    }

    [Fact]
    public void ASwitchThePlayerThrewBackIsLeftAlone()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        Stand(conductor, train, world, 0.4);
        Assert.Single(world.Throws);
        Stand(conductor, train, world, 0.4);

        // The player puts it back, by hand or by radio.
        world.Selected[j] = 0;
        Stand(conductor, train, world, 6);
        Assert.Single(world.Throws);
        Assert.Equal(0, world.Selected[j]);
        Assert.Contains(ConductorCallKind.SwitchLeft, Notices(conductor));
        // The siding can't be reached that way.
        Assert.Equal(ConductorCallKind.NoRoute, conductor.Call.Kind);

        // Set again for the route, and the conductor is back at work.
        world.Selected[j] = 1;
        Stand(conductor, train, world, 8);
        Assert.NotNull(conductor.Route);
        Assert.Single(world.Throws);
    }

    [Fact]
    public void ANewRouteUsesTheSwitchAsThePlayerSetIt()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        Stand(conductor, train, world, 0.4);
        Stand(conductor, train, world, 0.4);
        world.Selected[j] = 0;
        Stand(conductor, train, world, 0.2);
        // The train goes on down the straight branch: a switchback gets it to the siding,
        // but only by the switch as the player set it, so there is none, and nothing is thrown back.
        Roll(conductor, train, world, 5, 200);
        Assert.Single(world.Throws);
        Assert.Equal(1, train.EndA.Track);
    }

    [Fact]
    public void ItFollowsADriverThroughAReversal()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var conductor = Executing(world, train, new TrackPosition(2, 400));
        Assert.Equal(2, conductor.Route!.Legs.Count);
        var pullLength = conductor.Route.Legs[0].Length;

        Roll(conductor, train, world, 3, pullLength - 20);
        Assert.Equal(ConductorCallKind.Reverse, conductor.Call.Kind);
        Assert.Equal(0, conductor.LegIndex);
        Roll(conductor, train, world, 3, 20);

        // Stood at the reversal: over to the next leg, whose switch is lined for the move back.
        Stand(conductor, train, world, 0.4);
        Assert.Equal(1, conductor.LegIndex);
        Assert.Equal(1, world.Selected[j]);

        Roll(conductor, train, world, -3, conductor.Route.Legs[1].Length, () => conductor.State == ConductorState.Arrived);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Arrived, conductor.State);
        Assert.Equal(2, train.EndB.Track);
        Assert.Contains(ConductorCallKind.Arrived, Notices(conductor));
    }

    [Fact]
    public void RunningPastTheReversalIsFine()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var conductor = Executing(world, train, new TrackPosition(2, 400));
        Roll(conductor, train, world, 3, conductor.Route!.Legs[0].Length + 40);
        Assert.NotEqual(ConductorCallKind.OffRoute, conductor.Call.Kind);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(1, conductor.LegIndex);
        Assert.Equal(1, world.Selected[j]);
    }

    [Fact]
    public void StoppedWellShortItStillAsksForTheReversal()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var conductor = Executing(world, train, new TrackPosition(2, 400));
        var leg = conductor.Route!.Legs[0];
        Roll(conductor, train, world, 3, leg.Length - 40);
        Assert.Equal(1, train.EndB.Track);
        Stand(conductor, train, world, 1);
        Assert.Equal(0, conductor.LegIndex);
        Assert.Equal(ConductorCallKind.Reverse, conductor.Call.Kind);
        Assert.InRange(conductor.Call.Distance, 39, 41);
        Assert.Empty(world.Throws);
        Assert.Equal(0, world.Selected[j]);
    }

    [Fact]
    public void StoppedWithTheRearOnTheSwitchItAsksToPullClear()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var conductor = Executing(world, train, new TrackPosition(2, 400));
        // The rear is 1 m past the points, the nose inside the reversal's window.
        Roll(conductor, train, world, 3, 231);
        world.OwnCarsAcross = junction => junction == j;
        Stand(conductor, train, world, 0.4);
        Assert.Equal(1, conductor.LegIndex);
        Assert.Equal(ConductorCallKind.PullClear, conductor.Call.Kind);
        Assert.Empty(world.Throws);
        Assert.Equal(0, world.ThrownUnderTheTrain);

        // Pulled ahead, the rear clears it and the switch is lined.
        Roll(conductor, train, world, 3, 10);
        world.OwnCarsAcross = null;
        Stand(conductor, train, world, 0.4);
        Assert.Equal(1, world.Selected[j]);
    }

    [Fact]
    public void OffTheRouteItFindsANewOne()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 400);
        var conductor = Executing(world, train, new TrackPosition(0, 500));
        // The stem's goal is behind the train; the driver heads the other way, through the switch.
        var first = conductor.Route!;
        Roll(conductor, train, world, 5, 500);
        Assert.Equal(1, train.EndA.Track);
        Stand(conductor, train, world, 4);
        Assert.Equal(ConductorState.Executing, conductor.State);
        Assert.NotNull(conductor.Route);
        Assert.NotSame(first, conductor.Route);
        Assert.Contains(ConductorCallKind.NewRoute, Notices(conductor));
        Assert.Equal(0, world.Selected[j]);
    }

    [Fact]
    public void WithNoWayOnItSaysSoAndKeepsLooking()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(1, 300), TrackDirection.Backward, 80);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        // Driven off to the far end, then the switch is jammed by another train's cars.
        world.JammedSwitches.Add(j);
        Roll(conductor, train, world, -5, 100);
        Stand(conductor, train, world, 8);
        Assert.Equal(ConductorCallKind.NoRoute, conductor.Call.Kind);
        Assert.Equal(ConductorState.Executing, conductor.State);
        Assert.Empty(world.Throws);

        world.JammedSwitches.Clear();
        Stand(conductor, train, world, 70);
        Assert.NotNull(conductor.Route);
        Assert.NotEqual(ConductorCallKind.NoRoute, conductor.Call.Kind);
    }

    [Fact]
    public void ACouplingMakesItPlanForTheNewTrain()
    {
        var (world, _) = Wye();
        var train = OnStem(world, 300);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        var before = conductor.Route!;
        Stand(conductor, train, world, 4);
        train.CoupleOn(ConsistEnd.B, 40, 3);
        Stand(conductor, train, world, 4);
        Assert.NotNull(conductor.Route);
        Assert.NotSame(before, conductor.Route);
        Assert.Equal(ConductorState.Executing, conductor.State);
    }

    [Fact]
    public void OffTheRailsItThrowsNothing()
    {
        var (world, _) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        var state = train.State();
        state.OnTrack = false;
        conductor.Tick(Tick, state, world);
        Assert.Equal(ConductorCallKind.Derailed, conductor.Call.Kind);
        Assert.Empty(world.Throws);
    }

    [Fact]
    public void ANamedTrackIsReachedAnywhereOnIt()
    {
        var (world, _) = Wye();
        var train = OnStem(world, 50);
        var conductor = Executing(world, train, new TrackPosition(2, 500), anywhereOnTrack: true);
        Roll(conductor, train, world, 3, 300);
        Assert.Equal(ConductorState.Executing, conductor.State);
        Assert.Equal(2, train.EndB.Track);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Arrived, conductor.State);
        Assert.Null(conductor.Route);
    }

    [Fact]
    public void APointIsReachedOnlyAtThePoint()
    {
        var (world, _) = Wye();
        var train = OnStem(world, 50);
        var conductor = Executing(world, train, new TrackPosition(2, 500));
        Roll(conductor, train, world, 3, 300);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Executing, conductor.State);
        Assert.Equal(ConductorCallKind.StopAtDestination, conductor.Call.Kind);
        Roll(conductor, train, world, 3, 240);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Arrived, conductor.State);
    }

    [Fact]
    public void StandingDownLeavesTheSwitchesAlone()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        Stand(conductor, train, world, 0.4);
        conductor.StandDown();
        world.Selected[j] = 0;
        Stand(conductor, train, world, 2);
        Assert.Equal(ConductorState.None, conductor.State);
        Assert.Null(conductor.Route);
        Assert.Single(world.Throws);
        Assert.Equal(0, world.Selected[j]);
    }

    [Fact]
    public void ACallIsNoticedOnce()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        world.Locked.Add(j);
        Stand(conductor, train, world, 5);
        Assert.Single(Notices(conductor), ConductorCallKind.SwitchBlocked);
    }

    private static void WithCulture(CultureInfo culture, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CallsReadAsThePlayerWouldSayThem()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
        {
            string Name(int j) => "S-0216";
            Assert.Equal("Stop: S-0216 can't be set, 240 m", new ConductorCall(ConductorCallKind.SwitchBlocked, 240, 3).Say(Name, "C3L"));
            Assert.Equal("Stop in 1.2 km, then reverse", new ConductorCall(ConductorCallKind.Reverse, 1200).Say(Name, "C3L"));
            Assert.Equal("Arrived at C3L", new ConductorCall(ConductorCallKind.Arrived).Say(Name, "C3L"));
            Assert.Equal("New route: 2.4 km, 1 reversal", new ConductorCall(ConductorCallKind.NewRoute, 2400, 1).Say(Name, "C3L"));
        });
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void CallsFollowNonEnglishCultureDecimalSeparator(string cultureName)
    {
        WithCulture(CultureInfo.GetCultureInfo(cultureName), () =>
        {
            string Name(int j) => "S-0216";
            Assert.Equal("1,2 km", ConductorCall.Far(1200));
            Assert.Equal("240 m", ConductorCall.Far(240));
            Assert.Equal("Stop in 1,2 km, then reverse", new ConductorCall(ConductorCallKind.Reverse, 1200).Say(Name, "C3L"));
            Assert.Equal("New route: 2,4 km, 1 reversal", new ConductorCall(ConductorCallKind.NewRoute, 2400, 1).Say(Name, "C3L"));
        });
    }

    [Fact]
    public void FouledAheadDetectsMovingTrainsAndUsesTrailingClearance()
    {
        // Facing switch: moving train on branch 1 within 35 m triggers SwitchFouled.
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(1, 300));
        world.Occupancy.Add(2, new OccupiedSpan(10, 30, owner: 9));
        world.Occupancy.MarkMoving(9);
        Stand(conductor, train, world);
        Assert.Equal(ConductorCallKind.SwitchFouled, conductor.Call.Kind);
        Assert.Equal(j, conductor.Call.Junction);
        Assert.InRange(conductor.Call.Distance, 100 - 8 - 1, 100 - 8 + 1);

        // Trailing switch: coming down branch 0, car on branch 1 at 12..34 m stops short of fouling point (35 + 8 m).
        var (w2, j2) = Wye();
        var t2 = new NetworkTrain(w2, new TrackPosition(1, 100), TrackDirection.Backward, 80);
        var c2 = Executing(w2, t2, new TrackPosition(0, 300));
        w2.Occupancy.Add(2, new OccupiedSpan(12, 34, owner: 9));
        w2.Occupancy.MarkMoving(9);
        Stand(c2, t2, w2);
        Assert.Equal(ConductorCallKind.SwitchFouled, c2.Call.Kind);
        Assert.Equal(j2, c2.Call.Junction);
        Assert.InRange(c2.Call.Distance, 100 - Fouling.Reach - 8 - 1, 100 - Fouling.Reach - 8 + 1);

        // Already inside the 35 m fouling reach (20 m from points): cars farther out (25..34 m) are behind us;
        // cars nearer the points (5..15 m) still trigger SwitchFouled.
        var (w3, _) = Wye();
        var t3 = new NetworkTrain(w3, new TrackPosition(1, 20), TrackDirection.Backward, 80);
        var c3 = Executing(w3, t3, new TrackPosition(0, 300));
        w3.Occupancy.Add(2, new OccupiedSpan(25, 34, owner: 9));
        Stand(c3, t3, w3);
        Assert.NotEqual(ConductorCallKind.SwitchFouled, c3.Call.Kind);
        w3.Occupancy.Clear();
        w3.Occupancy.Add(2, new OccupiedSpan(5, 15, owner: 9));
        Stand(c3, t3, w3);
        Assert.Equal(ConductorCallKind.SwitchFouled, c3.Call.Kind);
    }

    [Fact]
    public void NearestHazardWinsOverDistantHazards()
    {
        // Case 1: distant blocked switch (120 m, safe stop 105 m) + nearer cars ahead (40 m, safe stop 32 m) -> CarsAhead wins.
        var (w1, j1) = Wye();
        var t1 = OnStem(w1, 120);
        var c1 = Executing(w1, t1, new TrackPosition(2, 300));
        w1.Locked.Add(j1);
        w1.Occupancy.Add(0, new OccupiedSpan(70, 80, owner: 9)); // near edge at 80 -> 40 m ahead of 120
        Stand(c1, t1, w1);
        Assert.Equal(ConductorCallKind.CarsAhead, c1.Call.Kind);
        Assert.InRange(c1.Call.Distance, 31, 33);

        // Case 2: nearer blocked switch (40 m, safe stop 25 m) + more distant cars ahead (80 m, safe stop 72 m) -> SwitchBlocked wins.
        var (w2, j2) = Wye();
        var t2 = OnStem(w2, 40);
        var c2 = Executing(w2, t2, new TrackPosition(2, 300));
        w2.Locked.Add(j2);
        w2.Occupancy.Add(2, new OccupiedSpan(40, 50, owner: 9)); // 40 (to j2) + 40 = 80 m ahead
        Stand(c2, t2, w2);
        Assert.Equal(ConductorCallKind.SwitchBlocked, c2.Call.Kind);
        Assert.Equal(j2, c2.Call.Junction);
    }

    [Fact]
    public void PlayerSwitchOverrideImmediatelyAfterFirstThrowIsRespected()
    {
        var (world, j) = Wye();
        var train = OnStem(world, 100);
        var conductor = Executing(world, train, new TrackPosition(2, 300));
        // Single tick throws the switch.
        conductor.Tick(Tick, train.State(), world);
        Assert.Single(world.Throws);
        Assert.Equal(1, world.Selected[j]);

        // Player immediately throws it back before any second tick runs.
        world.Selected[j] = 0;
        conductor.Tick(Tick, train.State(), world);
        Assert.Single(world.Throws);
        Assert.Equal(0, world.Selected[j]);
        Assert.Contains(ConductorCallKind.SwitchLeft, Notices(conductor));
    }

    [Fact]
    public void NamedTrackArrivalRequiresWholeTrainOnDestinationTrack()
    {
        var (world, _) = Wye();
        // 80 m train on stem, nose 50 m from switch; goal is 100 m into track 2 (leg length 150 m).
        var train = OnStem(world, 50);
        var conductor = Executing(world, train, new TrackPosition(2, 100), anywhereOnTrack: true);
        // Move 125 m: progress is 125 >= 150 - ArrivalWindow(30), nose is at (2, 75),
        // but tail (EndB) is still on track 0 at offset 5. Must NOT declare Arrived.
        Roll(conductor, train, world, 3, 125);
        Assert.Equal(2, train.EndA.Track);
        Assert.Equal(0, train.EndB.Track);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Executing, conductor.State);

        // Pull another 15 m so the whole train is on track 2 (nose at 90, tail at 10).
        Roll(conductor, train, world, 3, 15);
        Assert.Equal(2, train.EndA.Track);
        Assert.Equal(2, train.EndB.Track);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Arrived, conductor.State);
    }

    [Fact]
    public void NamedTrackArrivalDoesNotTriggerOnWrongTrackDuringStartBack()
    {
        // Track 0 (200 m) joined to Track 1 (200 m). A 100 m train straddles the joint:
        // EndB at track 0 offset 190 (outward backward), EndA at track 1 offset 90 (outward forward).
        // Goal is track 0 offset 198 (under the train behind EndB): Router chooses StartBack,
        // so EndA leads forward onto track 1 (last route piece is on track 1) by 8 m.
        var b = new TrackGraphBuilder();
        var t0 = b.AddTrack(200, new MapPoint(0, 0), new MapPoint(200, 0));
        var t1 = b.AddTrack(200, new MapPoint(200, 0), new MapPoint(400, 0));
        b.Join(t0, TrackEnd.End, t1, TrackEnd.Start);
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(t1, 90), TrackDirection.Forward, 100);

        var conductor = new Conductor();
        var wp = new Waypoint(new TrackPosition(t0, 198)) { Label = "Yard 0" };
        Assert.True(conductor.Plan(wp, anywhereOnTrack: true, train.State(), world));
        Assert.Equal(ConductorExecution.Started, conductor.Execute(train.State(), world));
        Assert.Equal(t1, conductor.Route.Legs[^1].Pieces[^1].Track);

        // Overshoot slightly (15 m total, 7 m past the 8 m leg, still within OvershootLimit on the same leg)
        // so BOTH ends are on track 1 (EndB at t1:5, EndA at t1:105): must NOT count as arrived at track 0.
        Roll(conductor, train, world, 3, 15);
        Assert.Equal(t1, train.EndA.Track);
        Assert.Equal(t1, train.EndB.Track);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Executing, conductor.State);
        Assert.NotNull(conductor.Route);
        Assert.Equal(t1, conductor.Route.Legs[^1].Pieces[^1].Track);
    }

    [Fact]
    public void NamedTrackArrivalSucceedsWhenTrainIsLongerThanDestinationTrack()
    {
        // Stem 0 (200 m) -> junction -> turnout branch 1 (24 m) -> plain joint -> yard track 2 (60 m).
        // A 70 m train cannot fit wholly on the 60 m track: once standing near the end with points clear
        // and its nose on track 2, the conductor must announce Arrived.
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(200, new MapPoint(0, 0), new MapPoint(200, 0));
        var branch = b.AddTrack(24, new MapPoint(200, 0), new MapPoint(224, 0));
        var other = b.AddTrack(100, new MapPoint(200, 0), new MapPoint(300, 30));
        var yard = b.AddTrack(60, new MapPoint(224, 0), new MapPoint(284, 0));
        b.AddJunction(stem, TrackEnd.End, new[] { (branch, TrackEnd.Start), (other, TrackEnd.Start) }, new MapPoint(200, 0));
        b.Join(branch, TrackEnd.End, yard, TrackEnd.Start);
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(stem, 100), TrackDirection.Forward, 70);

        var conductor = new Conductor();
        var wp = new Waypoint(new TrackPosition(yard, 55)) { Label = "Short Yard" };
        Assert.True(conductor.Plan(wp, anywhereOnTrack: true, train.State(), world));
        Assert.Equal(ConductorExecution.Started, conductor.Execute(train.State(), world));

        // Drive 178 m so nose is at yard offset 54 (tail at branch offset 8, past the points at 0).
        Roll(conductor, train, world, 4, 178);
        Assert.Equal(yard, train.EndA.Track);
        Assert.Equal(branch, train.EndB.Track);
        Stand(conductor, train, world, 0.4);
        Assert.Equal(ConductorState.Arrived, conductor.State);
    }

    [Theory]
    [InlineData(0.2, 100)]
    [InlineData(-0.2, 500)]
    [InlineData(5.0, 100)]
    public void MovingTrainKeepsFirstLeaderConstraintInsteadOfReversingPlan(double speedTowardA, double goalOffset)
    {
        // Straight dead-end track 0 (600 m). Train at 260..340 moving slowly (+0.2 or -0.2 m/s) or fast (+5 m/s)
        // away from the goal: no route starts in its direction of travel, so Plan must refuse until stopped.
        var b = new TrackGraphBuilder();
        b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(0, 340), TrackDirection.Forward, 80);
        train.SetSpeed(speedTowardA);

        var conductor = new Conductor();
        Assert.False(conductor.Plan(new Waypoint(new TrackPosition(0, goalOffset)), false, train.State(), world));
        Assert.Equal(ConductorCallKind.NoRoute, conductor.Call.Kind);

        // Once stopped, planning succeeds in the opposite direction.
        train.SetSpeed(0);
        Assert.True(conductor.Plan(new Waypoint(new TrackPosition(0, goalOffset)), false, train.State(), world));
    }

    [Fact]
    public void ReplanIntervalZeroOffRouteReplansAndResumes()
    {
        var (world, _) = Wye();
        var train = OnStem(world, 400);
        var settings = new EngineerSettings { ReplanInterval = 0 };
        var conductor = new Conductor(settings);
        Assert.True(conductor.Plan(new Waypoint(new TrackPosition(0, 500)), false, train.State(), world));
        Assert.Equal(ConductorExecution.Started, conductor.Execute(train.State(), world));

        // Jump the train off the planned leg and tick with ReplanInterval == 0:
        // Follow is non-recursive and Tick replans once and resumes Executing cleanly.
        train.Move(450);
        conductor.Tick(Tick, train.State(), world);
        Assert.Equal(ConductorState.Executing, conductor.State);
        Assert.NotNull(conductor.Route);
    }
}
