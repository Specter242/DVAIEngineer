using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>Cases from the November 2026 review of the driving model.</summary>
public class ReviewDrivingTests
{
    private static FakeWorld Line(double length, SpeedLimitProfile? profile = null)
    {
        var b = new TrackGraphBuilder();
        b.AddTrack(length, new MapPoint(0, 0), new MapPoint(length, 0), profile);
        return new FakeWorld(b.Build());
    }

    private static void Aar(SimTrain p, double grade = 0)
    {
        p.Mass = 900_000;
        p.MaxTractiveForce = 300_000;
        p.Power = 2_000_000;
        p.TrainBrakeDecel = 0.18;
        p.LocoBrakeDecel = 0.04;
        p.ApplyLag = 7;
        p.ReleaseLag = 15;
        p.DirectRelease = true;
        p.Grade = grade;
    }

    private static TrainState Slow(TrainState s)
    {
        s.Controls = new ControlCapabilities { HasTrainBrake = true, SlowAir = true };
        s.LocoMassShare = 0.13;
        return s;
    }

    private static TrainState Light(TrainState s)
    {
        s.CarCount = 1;
        return s;
    }

    [Fact]
    public void AnOvershootOfTheWaypointStillArrives()
    {
        var world = Line(2000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 800)) { Label = "the mark" });
        engineer.Tick(0.2, train.State(), world);
        // Standing 6 m past the mark (a stop that ran long).
        train.Move(306);
        for (var i = 0; i < 100 && engineer.Queue.Count > 0; i++)
        {
            var state = train.State();
            state.Reverser = ConsistEnd.A;
            engineer.Tick(0.2, state, world);
        }
        Assert.Empty(engineer.Queue);
        Assert.Contains("6 m past the mark", engineer.Note);
    }

    [Theory]
    [InlineData("stock", 60, 0)]
    [InlineData("stock", 400, 0)]
    [InlineData("stock", 60, -0.01)]
    [InlineData("light", 60, 0)]
    [InlineData("slow", 60, 0)]
    [InlineData("slow", 400, 0)]
    [InlineData("slow", 60, -0.005)]
    public void ItTouchesAtCouplingSpeed(string kind, double from, double grade)
    {
        var world = Line(6000);
        var train = new NetworkTrain(world, new TrackPosition(0, 3000 - from), TrackDirection.Forward, kind == "light" ? 8 : 200);
        Func<TrainState, TrainState>? state = null;
        if (kind == "light")
        {
            var p = train.Physics;
            p.Mass = 38_000;
            p.MaxTractiveForce = 60_000;
            p.Power = 400_000;
            p.TrainBrakeDecel = 0;
            p.LocoBrakeDecel = 1.5;
            state = Light;
        }
        else if (kind == "slow")
        {
            Aar(train.Physics, grade);
            state = Slow;
        }
        else
        {
            train.Physics.Grade = grade;
        }
        // Cars standing from 3000 on; the order points 15 m short, as the dispatcher's does.
        world.Occupancy.Add(0, new OccupiedSpan(3000, 3060, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new[] { new WaypointGoal(new TrackPosition(0, 2985), TrackDirection.Forward) }, WaypointAction.Couple));
        var contact = double.NaN;
        Drive.Run(engineer, train, world, maxSeconds: 1500, state: state, hasTrainBrake: kind != "light",
            until: () => !double.IsNaN(contact), onOutput: _ =>
            {
                if (3000 - train.EndA.Offset <= 0.05)
                    contact = train.SpeedTowardA;
            });
        Assert.False(double.IsNaN(contact), $"never reached the cars: {engineer.Status}, {3000 - train.EndA.Offset:F1} m short");
        // The setting is 0.8 m/s; the game's couplers take up to about 1.4 m/s (5 km/h) without harm.
        Assert.True(contact <= 1.2, $"touched at {contact:F2} m/s");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALimitHoldsUntilTheTailIsClearFromAStandInIt(bool road)
    {
        // A 5 m/s curve from 300 to 600 m. The train starts with its front 10 m past it
        // and 190 m of train still in it.
        var slow = new SpeedLimitProfile(new[] { 0.0, 300, 600 }, new[] { double.PositiveInfinity, 5, double.PositiveInfinity });
        var world = Line(6000, slow);
        var train = new NetworkTrain(world, new TrackPosition(0, 610), TrackDirection.Forward, 200);
        var engineer = new Engineer();
        engineer.Settings.MaxSpeed = 20;
        if (road)
            engineer.SetRoad(ConsistEnd.A, 20);
        else
            engineer.GoTo(new Waypoint(new TrackPosition(0, 5000)));
        var fastest = 0.0;
        Drive.Run(engineer, train, world, maxSeconds: 400, until: () =>
        {
            if (train.EndB.Offset <= 600)
                fastest = Math.Max(fastest, Math.Abs(train.SpeedTowardA));
            return train.EndB.Offset > 700;
        });
        Assert.True(fastest <= 6, $"{fastest:F1} m/s with the tail still in a 5 m/s curve");
        // And it does get going once clear.
        Assert.True(Math.Abs(train.SpeedTowardA) > 6, "never sped up after the curve");
    }

    [Fact]
    public void RoadModeHoldsALimitUntilTheTailClears()
    {
        var slow = new SpeedLimitProfile(new[] { 0.0, 500, 600 }, new[] { double.PositiveInfinity, 5, double.PositiveInfinity });
        var world = Line(6000, slow);
        var train = new NetworkTrain(world, new TrackPosition(0, 250), TrackDirection.Forward, 200);
        var engineer = new Engineer();
        engineer.SetRoad(ConsistEnd.A, 20);
        var fastest = 0.0;
        Drive.Run(engineer, train, world, maxSeconds: 400, until: () =>
        {
            if (train.EndA.Offset >= 500 && train.EndB.Offset <= 600)
                fastest = Math.Max(fastest, Math.Abs(train.SpeedTowardA));
            return train.EndB.Offset > 620;
        });
        Assert.True(fastest <= 6, $"{fastest:F1} m/s with part of the train in a 5 m/s curve");
    }

    [Fact]
    public void AnOrderWithNoRouteBacksOffItsSearches()
    {
        var world = Line(3000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 80);
        // Cars across the only way there.
        world.Occupancy.Add(0, new OccupiedSpan(1000, 1100, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 2000)));
        var searches = 0;
        string? lastStatus = null;
        for (var t = 0.0; t < 600; t += 0.2)
        {
            var state = train.State();
            state.Reverser = ConsistEnd.A;
            var before = world.Router.Searches;
            engineer.Tick(0.2, state, world);
            searches += world.Router.Searches - before;
            lastStatus = engineer.Status;
        }
        Assert.StartsWith("No route", lastStatus);
        // Every 3 s would be 200 searches in ten minutes.
        Assert.InRange(searches, 5, 30);
        // The way clears: it finds it within a minute, without being told.
        world.Occupancy.Clear();
        Drive.Run(engineer, train, world, maxSeconds: 70, until: () => engineer.Route != null);
        Assert.NotNull(engineer.Route);
    }

    [Fact]
    public void ACouplingThatFindsNoCarsSaysSo()
    {
        var world = Line(3000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 800), WaypointAction.Couple) { Label = "B4" });
        string? note = null;
        Drive.Run(engineer, train, world, until: () =>
        {
            note = engineer.Note ?? note;
            return engineer.Queue.Count == 0;
        });
        Assert.NotNull(note);
        Assert.Contains("no cars to couple to", note);
    }

    [Fact]
    public void ASlippingEngineEasesOffAndSands()
    {
        var controller = new SpeedController();
        var caps = new ControlCapabilities { HasTrainBrake = true };
        ControlOutput output = default;
        // Starting a heavy train: the throttle opens.
        for (var i = 0; i < 40; i++)
            output = controller.Update(new ControlInput { Speed = 0.2, Target = 5, ToStop = 500, Dt = 0.2 }, caps);
        var opened = output.Throttle;
        Assert.True(opened > 0.5, $"throttle {opened:F2}");
        Assert.False(output.Sand);
        // The wheels let go for two seconds: it comes back, with sand.
        for (var i = 0; i < 10; i++)
            output = controller.Update(new ControlInput { Speed = 0.2, Target = 5, ToStop = 500, Dt = 0.2, Slipping = true }, caps);
        var eased = output.Throttle;
        Assert.True(eased < opened - 0.5, $"throttle {eased:F2} from {opened:F2}");
        Assert.True(output.Sand);
        // They bite: no more throttle for a moment, the sand still running; then up again, gently.
        for (var i = 0; i < 10; i++)
            output = controller.Update(new ControlInput { Speed = 0.3, Target = 5, ToStop = 500, Dt = 0.2 }, caps);
        Assert.True(output.Throttle <= eased + 1e-9, $"throttle {output.Throttle:F2} two seconds after a slip at {eased:F2}");
        Assert.True(output.Sand);
        for (var i = 0; i < 60; i++)
            output = controller.Update(new ControlInput { Speed = 0.5, Target = 5, ToStop = 500, Dt = 0.2 }, caps);
        Assert.True(output.Throttle > eased + 0.3, $"throttle {output.Throttle:F2} twelve seconds on");
        Assert.False(output.Sand);
    }

    [Fact]
    public void ACutOfNothingIsNotReportedDone()
    {
        var world = Line(3000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        var order = new Waypoint(new TrackPosition(0, 800), WaypointAction.Uncouple)
        {
            CutCount = () => 0,
            Label = "B4",
            DoneNote = "2 cars set out on B4",
        };
        engineer.GoTo(order);
        var notes = new List<string>();
        var cuts = 0;
        Drive.Run(engineer, train, world, until: () =>
        {
            if (engineer.Note != null)
                notes.Add(engineer.Note);
            engineer.Note = null;
            return engineer.Queue.Count == 0;
        }, onOutput: o => cuts += o.Cut.HasValue ? 1 : 0);
        Assert.Equal(0, cuts);
        Assert.Equal(OrderOutcome.NothingCut, order.Outcome);
        Assert.DoesNotContain("2 cars set out on B4", notes);
        Assert.Contains(notes, n => n.Contains("no cars to leave"));
    }

    [Fact]
    public void OrdersSayHowTheyEnded()
    {
        var world = Line(3000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        var stop = new Waypoint(new TrackPosition(0, 700));
        var couple = new Waypoint(new TrackPosition(0, 900), WaypointAction.Couple);
        engineer.GoTo(stop);
        engineer.Enqueue(couple);
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0);
        Assert.Equal(OrderOutcome.Done, stop.Outcome);
        Assert.Equal(OrderOutcome.NoCars, couple.Outcome);
    }

    [Fact]
    public void ASetOutThatCantArriveCarsFirstHasNoRoute()
    {
        // A stub, the train loco first toward it with no way to turn: cars cut off there would
        // shut the loco in behind them. The order has no route; nothing is cut.
        var world = Line(1000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 80);
        var engineer = new Engineer();
        var order = new Waypoint(new[] { new WaypointGoal(new TrackPosition(0, 900), TrackDirection.Forward) }, WaypointAction.Uncouple)
        {
            CarCount = 2,
            Arrival = Arrival.CarsFirst,
            ArrivalRequired = true,
        };
        engineer.GoTo(order);
        var cuts = 0;
        // End A leads toward the stub's end, and the loco is at end A: the cars are behind it.
        Drive.Run(engineer, train, world, maxSeconds: 120, onOutput: o => cuts += o.Cut.HasValue ? 1 : 0,
            state: s =>
            {
                s.LocoEnd = ConsistEnd.A;
                return s;
            });
        Assert.Equal(0, cuts);
        Assert.Equal("No route to the waypoint cars first", engineer.Status);
        Assert.InRange(train.EndA.Offset, 499, 501);
        // Without the requirement it goes anyway, as an order to stop there should.
        order.ArrivalRequired = false;
        engineer.GoTo(order);
        Drive.Run(engineer, train, world, maxSeconds: 400, until: () => engineer.Queue.Count == 0,
            state: s =>
            {
                s.LocoEnd = ConsistEnd.A;
                return s;
            });
        Assert.Empty(engineer.Queue);
    }

    [Fact]
    public void ACouplingIsntGivenUpWhileTheBrakesChargeWithinReach()
    {
        // The train has just coupled a cold cut and stands charging its line; the next cut
        // is a metre beyond its far end. It waits for the air, then closes up and couples.
        var world = Line(3000);
        var train = new NetworkTrain(world, new TrackPosition(0, 1000), TrackDirection.Forward, 80);
        world.Occupancy.Add(0, new OccupiedSpan(1001, 1040, owner: 9));
        var engineer = new Engineer();
        var order = new Waypoint(new[] { new WaypointGoal(new TrackPosition(0, 1000.5), TrackDirection.Forward) }, WaypointAction.Couple);
        engineer.GoTo(order);
        var time = 0.0;
        Drive.Run(engineer, train, world, maxSeconds: 120, until: () => engineer.Queue.Count == 0,
            state: s =>
            {
                time += 0.2;
                s.AirKnown = true;
                s.CarPipeMin = time < 25 ? 2.0 : 5.0;
                return s;
            },
            onOutput: o =>
            {
                if (o.CoupleAt == ConsistEnd.A && train.CarCount == 6 && 1001 - train.EndA.Offset <= 0.5)
                {
                    world.Occupancy.Clear();
                    train.CoupleOn(ConsistEnd.A, 39, 3);
                }
            });
        Assert.Equal(OrderOutcome.Done, order.Outcome);
        Assert.Equal(9, train.CarCount);
        Assert.True(time > 25);
    }
}
