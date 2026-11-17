using AIEngineer.Core.Driving;

namespace AIEngineer.Core.Tests;

public class SpeedControllerTests
{
    private sealed record Run(double StopPosition, double MaxOverspeed, double Time, int ModeSwitches);

    /// <summary>Drives the sim train along a straight leg with one slow zone, to a stop.</summary>
    private static Run Drive(SimTrain train, ControlCapabilities caps, double stopAt, double maxSpeed, SpeedZone? zone = null, BrakingModel? braking = null, double settle = 5)
    {
        var planner = new SpeedPlanner();
        if (zone.HasValue)
            planner.Add(zone.Value);
        var model = braking ?? new BrakingModel(0.25, 3);
        var controller = new SpeedController();
        const double physicsDt = 0.02;
        const double tick = 0.2;
        var output = new ControlOutput();
        var maxOver = 0.0;
        var switches = 0;
        var wasBraking = false;
        var stillFor = 0.0;
        var time = 0.0;
        for (var since = tick; time < 1800; time += physicsDt, since += physicsDt)
        {
            if (since >= tick)
            {
                since = 0;
                var target = planner.TargetSpeed(train.Position, maxSpeed, stopAt, model);
                output = controller.Update(new ControlInput
                {
                    Speed = train.Speed,
                    Target = target,
                    ToStop = stopAt - train.Position,
                    Dt = tick,
                }, caps);
                if (controller.Braking != wasBraking && train.Speed > 0.5)
                    switches++;
                wasBraking = controller.Braking;
                maxOver = Math.Max(maxOver, train.Speed - planner.LimitAt(train.Position, maxSpeed));
            }
            train.Step(output.Throttle, Math.Max(output.TrainBrake, output.DynamicBrake * 0.6), output.LocoBrake, physicsDt);
            stillFor = Math.Abs(train.Speed) < 0.01 && train.Position > 1 ? stillFor + physicsDt : 0;
            if (stillFor > settle)
                break;
        }
        return new Run(train.Position, maxOver, time, switches);
    }

    private static readonly ControlCapabilities Freight = new() { HasTrainBrake = true };

    [Fact]
    public void AFreightStopsAtTheMark()
    {
        var run = Drive(new SimTrain(), Freight, stopAt: 2000, maxSpeed: 20);
        Assert.InRange(run.StopPosition, 1997, 2000.5);
        Assert.True(run.MaxOverspeed < 1.0, $"overspeed {run.MaxOverspeed:F2}");
        Assert.True(run.Time < 240, $"took {run.Time:F0} s");
    }

    [Fact]
    public void AFreightSlowsForAZoneAndHoldsIt()
    {
        var run = Drive(new SimTrain(), Freight, stopAt: 3000, maxSpeed: 20, zone: new SpeedZone(1200, 1600, 8));
        Assert.InRange(run.StopPosition, 2997, 3000.5);
        Assert.True(run.MaxOverspeed < 1.5, $"overspeed {run.MaxOverspeed:F2}");
        Assert.True(run.ModeSwitches <= 8, $"{run.ModeSwitches} switches between power and brake");
    }

    [Fact]
    public void ALightEngineStopsOnItsOwnBrake()
    {
        var engine = new SimTrain { Mass = 120_000, TrainBrakeDecel = 0, LocoBrakeDecel = 0.9 };
        var run = Drive(engine, new ControlCapabilities { HasTrainBrake = false }, stopAt: 600, maxSpeed: 12);
        Assert.InRange(run.StopPosition, 597, 600.5);
        Assert.True(run.MaxOverspeed < 1.0, $"overspeed {run.MaxOverspeed:F2}");
    }

    [Fact]
    public void AShortMoveCreepsToTheMark()
    {
        var run = Drive(new SimTrain(), Freight, stopAt: 12, maxSpeed: 4);
        Assert.InRange(run.StopPosition, 9, 12.5);
    }

    [Fact]
    public void ADowngradeIsHeldBack()
    {
        var run = Drive(new SimTrain { Grade = -0.012 }, Freight, stopAt: 2500, maxSpeed: 15);
        Assert.InRange(run.StopPosition, 2496, 2500.5);
        Assert.True(run.MaxOverspeed < 1.5, $"overspeed {run.MaxOverspeed:F2}");
    }

    [Fact]
    public void AHeavyTrainOnAnUpgradeGetsGoing()
    {
        // A DE2-like loco under loaded hoppers on a 0.8 % rise: the proportional part alone
        // (a third of the throttle at a yard target) can't start it (in game, 2026-11-05).
        var train = new SimTrain { Mass = 900_000, MaxTractiveForce = 120_000, Power = 500_000, Grade = 0.008 };
        // Standing short, it must get going again: a stand of 30 s ends the run.
        var run = Drive(train, Freight, stopAt: 200, maxSpeed: 8.4 / 3.6, braking: new BrakingModel(0.12, 10), settle: 30);
        Assert.InRange(run.StopPosition, 195, 200.5);
        Assert.True(run.Time < 600, $"took {run.Time:F0} s");
    }

    [Fact]
    public void ATrainThatWontMoveRestsTheMotorsInsteadOfBurningThem()
    {
        // Far too heavy for the loco on this rise: full throttle never moves it.
        var train = new SimTrain { Mass = 2_000_000, MaxTractiveForce = 60_000, Power = 300_000, Grade = 0.01 };
        var controller = new SpeedController();
        var fullFor = 0.0;
        var longestFull = 0.0;
        var rested = false;
        for (var t = 0.0; t < 300; t += 0.2)
        {
            var output = controller.Update(new ControlInput { Speed = train.Speed, Target = 2.3, ToStop = 200 - train.Position, Dt = 0.2 }, Freight);
            for (var k = 0; k < 10; k++)
                train.Step(output.Throttle, output.TrainBrake, output.LocoBrake, 0.02);
            fullFor = output.Throttle >= 0.6 ? fullFor + 0.2 : 0;
            longestFull = Math.Max(longestFull, fullFor);
            rested |= controller.Resting;
        }
        Assert.True(rested, "never rested the motors");
        Assert.True(longestFull <= controller.Tuning.StallLimit + 1, $"held high throttle {longestFull:F0} s at a stand");
        Assert.True(controller.Stalls >= 2);
    }

    [Fact]
    public void ALightShunterStopsWithoutSeeSawing()
    {
        // A DE2-like light engine, as on the first runs in game (2026-10-19): strong loco
        // brake, brisk acceleration. A 24 m move at 12 km/h used to alternate a full loco
        // brake with throttle every tick on the way in.
        var shunter = new SimTrain { Mass = 38_000, MaxTractiveForce = 60_000, Power = 400_000, TrainBrakeDecel = 0, LocoBrakeDecel = 1.5 };
        var run = Drive(shunter, new ControlCapabilities { HasTrainBrake = false }, stopAt: 24.4, maxSpeed: 12 / 3.6,
            braking: new BrakingModel(0.45, 1.5));
        Assert.InRange(run.StopPosition, 22.4, 24.9);
        Assert.True(run.ModeSwitches <= 2, $"{run.ModeSwitches} switches between power and brake");
    }

    [Fact]
    public void ALightShunterHoldsItsCapWithoutOvershooting()
    {
        // In game a DE2 running light overshot a 30 km/h cap to 33.7 (2026-10-19).
        var shunter = new SimTrain { Mass = 38_000, MaxTractiveForce = 60_000, Power = 400_000, TrainBrakeDecel = 0, LocoBrakeDecel = 1.5 };
        var run = Drive(shunter, new ControlCapabilities { HasTrainBrake = false }, stopAt: 1500, maxSpeed: 30 / 3.6,
            braking: new BrakingModel(0.45, 1.5));
        Assert.True(run.MaxOverspeed < 1.5 / 3.6, $"overspeed {run.MaxOverspeed * 3.6:F1} km/h");
        Assert.InRange(run.StopPosition, 1497, 1500.5);
    }
}
