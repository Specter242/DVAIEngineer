using AIEngineer.Core.Driving;

namespace AIEngineer.Core.Tests;

/// <summary>
/// The controller on Realistic Brakes' air: slow to apply down the train,
/// American direct release, and the weak brake of loaded cars on Period
/// loading. It should stop at the mark without ever easing the train
/// brake on a moving train, and hold a downgrade by setting it.
/// </summary>
public class SlowAirTests
{
    private sealed record Run(double StopPosition, double MaxOverspeed, double Time, int EaseOffs, double LastTrainBrake, double StopAt, List<(double Position, double Speed)> Releases)
    {
        /// <summary>
        /// Easings of the brake on a moving train while braking for the stop
        /// (its last 600 m), other than the let-go slow and near it: far from
        /// a stop a release is fine, the cars recharging before they're needed.
        /// </summary>
        public int StrayEaseOffs => Releases.Count(r => StopAt - r.Position < 600 && !(StopAt - r.Position < 41 && r.Speed < 1.6));
    }

    /// <summary>A DE6 and a dozen loaded cars on American equipment: 0.18 m/s² at full service, a weak loco brake on that mass.</summary>
    private static SimTrain AarFreight(double grade = 0) => new()
    {
        Mass = 900_000,
        MaxTractiveForce = 300_000,
        Power = 2_000_000,
        TrainBrakeDecel = 0.18,
        LocoBrakeDecel = 0.04,
        ApplyLag = 7,
        ReleaseLag = 15,
        DirectRelease = true,
        Grade = grade,
    };

    private static readonly ControlCapabilities SlowFreight = new() { HasTrainBrake = true, SlowAir = true };

    private static Run Drive(SimTrain train, double stopAt, double maxSpeed, Func<double, bool>? dragging = null)
    {
        var planner = new SpeedPlanner();
        var baseModel = new BrakingModel(0.12, 10);
        var controller = new SpeedController();
        const double physicsDt = 0.02;
        const double tick = 0.2;
        var output = new ControlOutput();
        var maxOver = 0.0;
        var stillFor = 0.0;
        var time = 0.0;
        for (var since = tick; time < 3600; time += physicsDt, since += physicsDt)
        {
            if (since >= tick)
            {
                since = 0;
                // As the engineer does: the planned deceleration less what a grade takes, and the target a brake delay ahead.
                var model = Engineer.SlowAirModel(baseModel, controller.AirHolding);
                var target = planner.TargetSpeed(train.Position, maxSpeed, stopAt, model);
                var ahead = planner.TargetSpeed(train.Position + Math.Max(0, train.Speed) * model.Delay, maxSpeed, stopAt, model);
                output = controller.Update(new ControlInput
                {
                    Speed = train.Speed,
                    Target = target,
                    TargetAhead = ahead,
                    HasTargetAhead = true,
                    ToStop = stopAt - train.Position,
                    Dt = tick,
                    BrakesDragging = dragging?.Invoke(time) ?? false,
                }, SlowFreight);
                maxOver = Math.Max(maxOver, train.Speed - maxSpeed);
            }
            train.Step(output.Throttle, Math.Max(output.TrainBrake, output.DynamicBrake * 0.6), output.LocoBrake, physicsDt);
            stillFor = Math.Abs(train.Speed) < 0.01 && train.Position > 1 ? stillFor + physicsDt : 0;
            if (stillFor > 5)
                break;
        }
        return new Run(train.Position, maxOver, time, train.EaseOffsWhileMoving, output.TrainBrake, stopAt, train.EaseOffs);
    }

    [Fact]
    public void ItStopsAtTheMarkEasingTheBrakeOnlyToLetItGoAtTheEnd()
    {
        var run = Drive(AarFreight(), stopAt: 2000, maxSpeed: 40 / 3.6);
        Assert.InRange(run.StopPosition, 1985, 2000.5);
        Assert.True(run.StrayEaseOffs == 0, $"released at {string.Join(", ", run.Releases.Select(r => $"{r.Position:F0} m, {r.Speed:F1} m/s"))}; stopped at {run.StopPosition:F1}");
        Assert.True(run.MaxOverspeed < 1.0, $"overspeed {run.MaxOverspeed:F2}");
    }

    [Fact]
    public void AYardMoveStopsAtTheMark()
    {
        var run = Drive(AarFreight(), stopAt: 300, maxSpeed: 12 / 3.6);
        Assert.InRange(run.StopPosition, 295, 300.5);
        Assert.Equal(0, run.StrayEaseOffs);
    }

    [Fact]
    public void ADowngradeIsHeldBySettingTheBrake()
    {
        var run = Drive(AarFreight(grade: -0.008), stopAt: 3000, maxSpeed: 30 / 3.6);
        Assert.InRange(run.StopPosition, 2980, 3000.5);
        Assert.True(run.MaxOverspeed < 2.0, $"overspeed {run.MaxOverspeed * 3.6:F1} km/h");
    }

    [Fact]
    public void NearAStopAnOverbrakedTrainLetsTheBrakeGo()
    {
        var controller = new SpeedController();
        // Braking for a stop 300 m on: the brake goes on.
        ControlOutput output = default;
        for (var i = 0; i < 50; i++)
            output = controller.Update(new ControlInput { Speed = 8 - i * 0.05, Target = 6 - i * 0.1, ToStop = 300 - i, Dt = 0.2 }, SlowFreight);
        Assert.True(controller.AirSet > 0, "no application");
        // Then slow and under its target 30 m short: it comes off rather than stall the train there.
        output = controller.Update(new ControlInput { Speed = 1.0, Target = 1.4, ToStop = 30, Dt = 0.2 }, SlowFreight);
        Assert.Equal(0, controller.AirSet);
        Assert.Equal(0, output.TrainBrake);
    }

    [Fact]
    public void ItTakesNoPowerUntilTheBrakesAreOff()
    {
        var controller = new SpeedController();
        var output = controller.Update(new ControlInput { Speed = 0, Target = 3, ToStop = 500, Dt = 0.2, BrakesDragging = true }, SlowFreight);
        Assert.True(controller.WaitingForAir);
        Assert.Equal(0, output.Throttle);
        Assert.Equal(0, output.TrainBrake);
        Assert.Equal(1, output.LocoBrake);
        // Once they're off it starts.
        for (var i = 0; i < 20; i++)
            output = controller.Update(new ControlInput { Speed = 0, Target = 3, ToStop = 500, Dt = 0.2 }, SlowFreight);
        Assert.False(controller.WaitingForAir);
        Assert.True(output.Throttle > 0);
    }

    [Fact]
    public void AStartWaitsForTheCarsToRelease()
    {
        // The cars take 40 s to come off after the order: the train must not move before.
        var train = AarFreight();
        var run = Drive(train, stopAt: 400, maxSpeed: 12 / 3.6, dragging: t => t < 40);
        Assert.InRange(run.StopPosition, 395, 400.5);
        Assert.True(run.Time > 40);
    }

    [Fact]
    public void StockAirIsUnchanged()
    {
        // Without slow air, the stand is held on the train brake as before.
        var controller = new SpeedController();
        var held = controller.Held(new ControlCapabilities { HasTrainBrake = true });
        Assert.Equal(controller.Tuning.HoldTrainBrake, held.TrainBrake);
        // Under slow air a short stand keeps only what is set; a long one sets the hold.
        Assert.Equal(0, controller.Held(SlowFreight).TrainBrake);
        Assert.Equal(controller.Tuning.HoldTrainBrake, controller.Held(SlowFreight, firm: true).TrainBrake);
    }
}
