using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class EngineerTests
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
    public void ItRunsAFacingMoveIntoTheBranchAndStopsAtTheWaypoint()
    {
        var (world, j) = Wye();
        // On the stem, end A toward the switch.
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)) { Label = "siding" });
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(1, world.Selected[j]);
        Assert.Equal(2, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, 295, 300.5);
    }

    [Fact]
    public void ItMakesASwitchbackFromBranchToBranch()
    {
        var (world, j) = Wye();
        // On the straight branch, end A toward the switch; the waypoint is up the other branch.
        var train = new NetworkTrain(world, new TrackPosition(1, 150), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 400)));
        var legs = new HashSet<int>();
        var time = Drive.Run(engineer, train, world, until: () =>
        {
            legs.Add(engineer.LegIndex);
            return engineer.Queue.Count == 0;
        });
        // 150 m to the switch, 95 m past it, back 95 m and 400 m up the branch: minutes, not seconds.
        Assert.InRange(time, 90, 900);
        Assert.Contains(1, legs);
        Assert.Empty(engineer.Queue);
        Assert.Equal(1, world.Selected[j]);
        // End B led the second leg into the diverging branch.
        Assert.Equal(2, train.EndB.Track);
        Assert.InRange(train.EndB.Offset, 395, 400.5);
    }

    [Fact]
    public void ItStopsShortOfStandingCars()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.Settings.BlockedReplanAfter = 1e9;
        // Planned while the way was clear; then cars stand across it.
        engineer.GoTo(new Waypoint(new TrackPosition(1, 300)));
        engineer.Tick(0.2, train.State(), world);
        world.Occupancy.Add(0, new OccupiedSpan(100, 160, owner: 9));
        Drive.Run(engineer, train, world, maxSeconds: 300);
        Assert.NotEmpty(engineer.Queue);
        Assert.Equal(0, train.EndA.Track);
        // Stopped short of the cars' far end (160) by about the gap.
        Assert.InRange(train.EndA.Offset, 160 + engineer.Settings.CarGap - 1, 160 + engineer.Settings.CarGap + 4);
        Assert.StartsWith("Waiting", engineer.Status);
    }

    [Fact]
    public void ItPlansNoRouteThroughStandingCars()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Backward, 80);
        world.Occupancy.Add(0, new OccupiedSpan(100, 160, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(1, 300)));
        Drive.Run(engineer, train, world, maxSeconds: 60);
        Assert.Equal("No route to the waypoint", engineer.Status);
        Assert.InRange(train.EndA.Offset, 499, 501);
        // Moving cars don't bar it: they will likely have cleared by the time it gets there.
        world.Occupancy.MarkMoving(9);
        Drive.Run(engineer, train, world, maxSeconds: 5);
        Assert.NotNull(engineer.Route);
    }

    [Fact]
    public void ItWaitsAtASwitchItCannotSet()
    {
        var (world, j) = Wye();
        world.Locked.Add(j);
        var train = new NetworkTrain(world, new TrackPosition(0, 400), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.Settings.BlockedReplanAfter = 1e9;
        // Plan while the switch is free, then lock it: it must stop short of it.
        world.Locked.Clear();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)));
        engineer.Tick(0.2, train.State(), world);
        world.Locked.Add(j);
        world.Selected[j] = 0;
        Drive.Run(engineer, train, world, maxSeconds: 300);
        Assert.Equal(0, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, engineer.Settings.SwitchStandoff - 1, engineer.Settings.SwitchStandoff + 4);
        Assert.Equal(0, world.Selected[j]);
    }

    [Fact]
    public void RoadModeStopsBeforeTheBufferStop()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 100), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.SetRoad(ConsistEnd.A, 40 / 3.6);
        Drive.Run(engineer, train, world, maxSeconds: 400);
        Assert.Equal(0, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, 600 - engineer.Settings.BufferStandoff - 5, 600 - 0.5);
        Assert.StartsWith("Stopped", engineer.Status);
    }

    [Fact]
    public void YardModeMovesTheDistanceAsked()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 300), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.SetYard(ConsistEnd.B, 120);
        Drive.Run(engineer, train, world, maxSeconds: 300);
        // End B started at 220 and moved 120 m toward the switch.
        Assert.Equal(0, train.EndB.Track);
        Assert.InRange(train.EndB.Offset, 100 - 0.5, 104);
        Assert.Equal("Yard move done", engineer.Status);
    }

    [Fact]
    public void QueuedWaypointsRunInTurn()
    {
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.Enqueue(new Waypoint(new TrackPosition(2, 300)));
        engineer.Enqueue(new Waypoint(new TrackPosition(0, 400)));
        engineer.Enqueue(new Waypoint(new TrackPosition(1, 250)));
        Drive.Run(engineer, train, world, maxSeconds: 2400, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(0, world.Selected[j]);
        var end = train.EndA.Track == 1 ? train.EndA : train.EndB;
        Assert.Equal(1, end.Track);
        Assert.InRange(end.Offset, 245, 250.5);
    }

    [Fact]
    public void ItRunsInToCarsPastACoupleWaypointAndCouples()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        // Three cars standing up the diverging branch, 20 m past where the player clicked.
        world.Occupancy.Add(2, new OccupiedSpan(300, 345, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 280), WaypointAction.Couple));
        var touchedAt = double.NaN;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0, onOutput: output =>
        {
            if (output.CoupleAt != ConsistEnd.A || train.CarCount > 6)
                return;
            var gap = 300 - train.EndA.Offset;
            if (train.EndA.Track == 2 && gap <= 1.5)
            {
                touchedAt = Math.Abs(train.SpeedTowardA);
                world.Occupancy.Clear();
                train.CoupleOn(ConsistEnd.A, 45, 3);
            }
        });
        Assert.Empty(engineer.Queue);
        Assert.Equal(9, train.CarCount);
        // It came in at coupling speed, not at line speed.
        Assert.True(touchedAt <= 1.5, $"touched at {touchedAt:F2} m/s");
    }

    [Fact]
    public void ACoupleWaypointWithNoCarsStopsThere()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 280), WaypointAction.Couple) { WaitSeconds = 5 });
        string? status = null;
        Drive.Run(engineer, train, world, until: () =>
        {
            if (engineer.Status.StartsWith("No cars"))
                status = engineer.Status;
            return engineer.Queue.Count == 0;
        });
        Assert.NotNull(status);
        Assert.Equal(2, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, 275, 280.5);
    }

    [Fact]
    public void AnUncoupleWaypointAsksForTheCutOnArrival()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300), WaypointAction.Uncouple) { CarCount = 2 });
        CutRequest? cut = null;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0, onOutput: output =>
        {
            if (output.Cut.HasValue)
            {
                Assert.True(Math.Abs(train.SpeedTowardA) < 0.1, "cut while moving");
                cut = output.Cut;
            }
        });
        Assert.NotNull(cut);
        Assert.Equal(2, cut!.Value.Cars);
        Assert.True(cut.Value.SetHandbrakes);
    }

    [Fact]
    public void RoadAtNoSpeedStaysPut()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 300), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.SetRoad(ConsistEnd.A, 0);
        Drive.Run(engineer, train, world, maxSeconds: 60);
        Assert.Equal(new TrackPosition(0, 300), train.EndA);
    }

    [Fact]
    public void AnOrderGivenWhileRollingTheOtherWayStopsBeforeReversing()
    {
        var (world, _) = Wye();
        // Rolling at 6 m/s toward end B (away from the switch); the waypoint lies past the switch, end A's way.
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        train.SetSpeed(-6);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)));
        // The harness throws if the reverser changes while moving.
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(2, train.EndA.Track);
    }

    [Fact]
    public void AWaypointAmongStandingCarsIsReachedAlongsideThem()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        // Cars stand over the waypoint.
        world.Occupancy.Add(2, new OccupiedSpan(250, 330, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300), WaypointAction.Uncouple) { CarCount = 1 });
        CutRequest? cut = null;
        Drive.Run(engineer, train, world, maxSeconds: 600, until: () => engineer.Queue.Count == 0,
            onOutput: o => cut ??= o.Cut);
        Assert.Empty(engineer.Queue);
        Assert.NotNull(cut);
        Assert.Equal(2, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, 250 - engineer.Settings.CarGap - 3, 250 - engineer.Settings.CarGap + 1);
    }

    [Fact]
    public void AYardMoveIntoABufferStopKeepsItsDistance()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 560), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.SetYard(ConsistEnd.A, 100);
        Drive.Run(engineer, train, world, maxSeconds: 200);
        Assert.InRange(train.EndA.Offset, 600 - engineer.Settings.BufferStandoff - 3, 600 - 0.5);
    }

    [Fact]
    public void ItWontPassASwitchWithCarsFoulingTheOtherBranch()
    {
        // In game (2026-11-04) a DE2 side-swiped cars standing on a switch's other
        // branch near the frog, where the two branches run a car's width apart.
        var (world, j) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 300), TrackDirection.Backward, 20);
        var engineer = new Engineer();
        engineer.Settings.BlockedReplanAfter = 1e9;
        engineer.GoTo(new Waypoint(new TrackPosition(1, 300)));
        // Cars set out 8-30 m up the diverging branch, across its clearance point, once the
        // train is on its way (a route isn't planned past cars already standing there).
        var set = false;
        Drive.Run(engineer, train, world, maxSeconds: 300, until: () =>
        {
            if (!set && engineer.Route != null)
            {
                set = true;
                world.Occupancy.Add(2, new OccupiedSpan(8, 30, owner: 9));
            }
            return false;
        });
        // It stopped short of the switch's points (track 0, offset 0) and waits.
        Assert.Equal(0, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, engineer.Settings.CarGap - 1, engineer.Settings.CarGap + 4);
        Assert.StartsWith("Waiting", engineer.Status);
    }

    [Fact]
    public void CarsWellClearOfTheSwitchDontHoldItUp()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 300), TrackDirection.Backward, 20);
        // Cars far up the diverging branch, beyond the fouling reach.
        world.Occupancy.Add(2, new OccupiedSpan(200, 230, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(1, 300)));
        Drive.Run(engineer, train, world, maxSeconds: 400, until: () => engineer.Queue.Count == 0);
        Assert.Empty(engineer.Queue);
        Assert.Equal(1, train.EndA.Track);
    }
}
