using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>
/// Realistic Brakes' air when the plan has gone wrong: a stop that turns
/// up inside the braking distance, a grade the loco brake can't hold, a
/// hold asked for at speed. The train brake must go on, and stay on.
/// </summary>
public class SlowAirSafetyTests
{
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

    private static SimTrain AarFreight(double grade = 0)
    {
        var train = new SimTrain();
        Aar(train, grade);
        return train;
    }

    private static readonly ControlCapabilities SlowFreight = new() { HasTrainBrake = true, SlowAir = true };

    private static TrainState Slow(TrainState s)
    {
        s.Controls = SlowFreight;
        s.LocoMassShare = 0.13;
        return s;
    }

    private static FakeWorld Line(double length)
    {
        var b = new TrackGraphBuilder();
        b.AddTrack(length, new MapPoint(0, 0), new MapPoint(length, 0));
        return new FakeWorld(b.Build());
    }

    /// <summary>Runs the bare controller to a stand; the stop may turn up late. Returns where it stopped and how fast it passed the mark (NaN if it never did).</summary>
    private static (double Stopped, double SpeedAtMark, bool Still) Run(SimTrain train, Func<double, double> stopAtFor, double maxSpeed, double maxTime = 1500)
    {
        var planner = new SpeedPlanner();
        var baseModel = new BrakingModel(0.12, 10);
        var controller = new SpeedController();
        const double physicsDt = 0.02;
        const double tick = 0.2;
        var output = new ControlOutput();
        var stillFor = 0.0;
        var speedAtMark = double.NaN;
        var stopAt = double.PositiveInfinity;
        for (double time = 0, since = tick; time < maxTime; time += physicsDt, since += physicsDt)
        {
            if (since >= tick)
            {
                since = 0;
                stopAt = stopAtFor(train.Position);
                var model = Engineer.SlowAirModel(baseModel, controller.AirHolding);
                output = controller.Update(new ControlInput
                {
                    Speed = train.Speed,
                    Target = planner.TargetSpeed(train.Position, maxSpeed, stopAt, model),
                    TargetAhead = planner.TargetSpeed(train.Position + Math.Max(0, train.Speed) * model.Delay, maxSpeed, stopAt, model),
                    HasTargetAhead = true,
                    ToStop = stopAt - train.Position,
                    Dt = tick,
                }, SlowFreight);
            }
            var before = train.Position;
            train.Step(output.Throttle, Math.Max(output.TrainBrake, output.DynamicBrake * 0.6), output.LocoBrake, physicsDt);
            if (before < stopAt && train.Position >= stopAt && double.IsNaN(speedAtMark))
                speedAtMark = train.Speed;
            stillFor = Math.Abs(train.Speed) < 0.01 && train.Position > 1 ? stillFor + physicsDt : 0;
            if (stillFor > 5)
                return (train.Position, speedAtMark, true);
        }
        return (train.Position, speedAtMark, false);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(500)]
    public void AStopInsideTheBrakingDistanceGetsAllTheBrake(double ahead)
    {
        // At 40 km/h the plan wants some 620 m; cars turn up nearer than that. Full service
        // (0.18 m/s²) needs 343 m and seven seconds to bite: the 300 m case can't be made, but
        // must be nearly stopped at the mark, not passing it at 7 m/s on a first set.
        var mark = double.NaN;
        var run = Run(AarFreight(), p =>
        {
            if (double.IsNaN(mark) && p >= 1000)
                mark = p + ahead;
            return double.IsNaN(mark) ? 1e7 : mark;
        }, 40 / 3.6);
        Assert.True(run.Still, "never stopped");
        var past = run.Stopped - mark;
        Assert.True(past < (ahead < 350 ? 90 : 3), $"stopped {past:F1} m past, passing the mark at {run.SpeedAtMark:F1} m/s");
    }

    [Theory]
    [InlineData(-0.008)]
    [InlineData(-0.010)]
    public void AYardMoveOnADowngradeStopsAndStaysStopped(double grade)
    {
        var run = Run(AarFreight(grade), _ => 300, 12 / 3.6);
        Assert.True(run.Still, $"rolled away: at {run.Stopped:F0} m");
        Assert.InRange(run.Stopped, 285, 306);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheEngineerHoldsAYardMoveOnADowngrade(bool yard)
    {
        var world = Line(30000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 200);
        Aar(train.Physics, -0.010);
        var engineer = new Engineer();
        engineer.Settings.MaxSpeed = 12 / 3.6;
        if (yard)
            engineer.SetYard(ConsistEnd.A, 300);
        else
            engineer.GoTo(new Waypoint(new TrackPosition(0, 800)));
        Drive.Run(engineer, train, world, maxSeconds: 900, state: Slow);
        Assert.True(Math.Abs(train.SpeedTowardA) < 0.01, $"still moving at {train.SpeedTowardA:F1} m/s, front at {train.EndA.Offset:F0}: {engineer.Status}");
        Assert.InRange(train.EndA.Offset, 785, 808);
        if (!yard)
            Assert.Empty(engineer.Queue);
    }

    [Fact]
    public void AHoldAtSpeedSetsTheTrainBrake()
    {
        var world = Line(3000);
        var train = new NetworkTrain(world, new TrackPosition(0, 500), TrackDirection.Forward, 200);
        train.SetSpeed(10);
        // The engine dies at 10 m/s.
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 2500)));
        var state = Slow(train.State());
        state.Reverser = ConsistEnd.A;
        state.CanMove = false;
        var output = engineer.Tick(0.2, state, world);
        Assert.Equal(1, output.Controls.TrainBrake);
        Assert.Equal(1, output.Controls.LocoBrake);
        Assert.Equal(0, output.Controls.Throttle);
    }

    [Fact]
    public void AHoldAtAStandKeepsOnlyWhatIsSet()
    {
        // Between orders at a stand the brake isn't set for nothing: it would only have to come off again.
        var controller = new SpeedController();
        Assert.Equal(0, controller.Held(SlowFreight).TrainBrake);
        // Rolling off down a grade on the loco brake alone: it goes on.
        Assert.Equal(controller.Tuning.HoldTrainBrake, controller.Held(SlowFreight, firm: true).TrainBrake);
    }
}
