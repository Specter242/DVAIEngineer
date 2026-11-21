using System.Globalization;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class MotorHeatAndSteamTests
{
    private static readonly ControlCapabilities Light = new() { HasTrainBrake = false };

    private static double ThrottleAfter(double heat, double seconds = 20)
    {
        var controller = new SpeedController();
        var output = new ControlOutput();
        for (var t = 0.0; t < seconds; t += 0.2)
            output = controller.Update(new ControlInput { Speed = 0.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, MotorHeat = heat }, Light);
        return output.Throttle;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(90, 1)]
    [InlineData(100, 1)]
    public void CoolMotorsTakeFullThrottle(double heat, double expected) =>
        Assert.Equal(expected, ThrottleAfter(heat), 3);

    [Theory]
    [InlineData(105, 2.0 / 3)]
    [InlineData(110, 1.0 / 3)]
    [InlineData(115, 0.15)]
    [InlineData(130, 0.15)]
    public void HotMotorsGetLessThrottle(double heat, double expected) =>
        Assert.Equal(expected, ThrottleAfter(heat), 3);

    [Fact]
    public void TheThrottleComesBackAsTheMotorsCool()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = 0.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, MotorHeat = 112 };
        for (var t = 0.0; t < 20; t += 0.2)
            controller.Update(input, Light);
        input.MotorHeat = 80;
        var output = new ControlOutput();
        for (var t = 0.0; t < 20; t += 0.2)
            output = controller.Update(input, Light);
        Assert.Equal(1, output.Throttle, 3);
    }

    private static ControlOutput Step(SpeedController c, bool slipping = false, double seconds = 0.2)
    {
        var output = new ControlOutput();
        for (var t = 0.0; t < seconds - 1e-9; t += 0.2)
            output = c.Update(new ControlInput { Speed = 0.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, Slipping = slipping }, Light);
        return output;
    }

    [Fact]
    public void TheThrottleOpensANotchAtATime()
    {
        var c = new SpeedController();
        Assert.Equal(0.6, Step(c, seconds: 10).Throttle, 2);
        Assert.Equal(1, Step(c, seconds: 8).Throttle, 2);
    }

    [Fact]
    public void AfterASlipTheThrottleFeelsItsWayBackUp()
    {
        var c = new SpeedController();
        Step(c, seconds: 12);
        var bit = Step(c, slipping: true, seconds: 1).Throttle;
        Assert.True(bit < 0.3, $"throttle {bit:F2}");
        Assert.Equal(bit, Step(c, seconds: 3).Throttle, 2);
        var later = Step(c, seconds: 5).Throttle;
        Assert.InRange(later, bit + 0.2, bit + 0.34);
    }

    [Fact]
    public void ALoneSlipSpendsLittleSandAndRepeatedSlipsKeepItOn()
    {
        var c = new SpeedController();
        Step(c, seconds: 2);
        Assert.True(Step(c, slipping: true).Sand);
        Assert.True(Step(c, seconds: 3.6).Sand);
        Assert.False(Step(c, seconds: 0.6).Sand);
        // Again within seconds of the sand ending: twice as long.
        Step(c, seconds: 2);
        Step(c, slipping: true);
        Assert.True(Step(c, seconds: 7.6).Sand);
        Assert.False(Step(c, seconds: 0.6).Sand);
        // A long while with no slip: back to a short run.
        Step(c, seconds: 30);
        Step(c, slipping: true);
        Assert.False(Step(c, seconds: 4.2).Sand);
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

    private static BoilerReading Boiler(double pressure = 12, double glass = 0.6, bool? lit = true) =>
        new() { Pressure = pressure, StartPressure = 6.75, Glass = glass, FireLit = lit };

    [Fact]
    public void TheCrewWaitsForSteamAtAStandAndRunsOnWhileThereIsSome()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
        {
            var watch = new SteamWatch();
            Assert.Equal("Raising steam: 5.0 of 7 bar", watch.Hold(Boiler(5), standing: true, lighting: false));
            Assert.Null(watch.Hold(Boiler(5), standing: false, lighting: false));
            Assert.Equal("Lighting the fire", watch.Hold(Boiler(12), standing: true, lighting: true));
        });
    }

    [Fact]
    public void WithNoSteamTheTrainStopsEvenOnTheMove()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
        {
            var crew = new SteamWatch();
            Assert.StartsWith("Raising steam", crew.Hold(Boiler(1), standing: false, lighting: false));
            var player = new SteamWatch { PlayerFires = true };
            Assert.Equal("Waiting for steam: you're firing (1.0 of 7 bar)", player.Hold(Boiler(1), standing: false, lighting: false));
        });
    }

    [Fact]
    public void AFiringPlayersTrainGoesOnByItselfOnceThereIsSteam()
    {
        var watch = new SteamWatch { PlayerFires = true };
        Assert.NotNull(watch.Hold(Boiler(5), standing: true, lighting: false));
        Assert.Null(watch.Hold(Boiler(7), standing: true, lighting: false));
    }

    [Fact]
    public void TheEngineerNeverLightsAFiringPlayersFire()
    {
        var watch = new SteamWatch { PlayerFires = true };
        Assert.Null(watch.Hold(Boiler(12, lit: false), standing: true, lighting: true));
    }

    [Fact]
    public void WaterOutOfSightStopsTheTrainUntilItIsBackInTheGlass()
    {
        var watch = new SteamWatch { PlayerFires = true };
        Assert.Null(watch.Hold(Boiler(glass: 0.15), standing: false, lighting: false));
        Assert.StartsWith("Water out of sight", watch.Hold(Boiler(glass: 0.1), standing: false, lighting: false));
        Assert.StartsWith("Water out of sight", watch.Hold(Boiler(glass: 0.15), standing: true, lighting: false));
        Assert.Null(watch.Hold(Boiler(glass: 0.2), standing: true, lighting: false));
    }

    [Fact]
    public void TheCrewIsNotStoppedForTheGlassAndAnUnknownGlassStopsNobody()
    {
        Assert.Null(new SteamWatch().Hold(Boiler(glass: 0), standing: false, lighting: false));
        Assert.Null(new SteamWatch { PlayerFires = true }.Hold(Boiler(glass: double.NaN), standing: false, lighting: false));
    }

    [Fact]
    public void EachWarningIsGivenOnceAndAgainOnlyAfterThingsCameRight()
    {
        WithCulture(CultureInfo.InvariantCulture, () =>
        {
            var watch = new SteamWatch { PlayerFires = true };
            Assert.Null(watch.NextWarning(Boiler()));
            var bad = Boiler(5, 0.25, false);
            Assert.Equal("the fire is out, and it's yours to light", watch.NextWarning(bad));
            Assert.Equal("the water is low in the glass", watch.NextWarning(bad));
            Assert.StartsWith("steam is down to 5.0 bar", watch.NextWarning(bad));
            Assert.Null(watch.NextWarning(bad));
            Assert.Null(watch.NextWarning(Boiler(7, 0.35, true)));
            Assert.StartsWith("the fire is out", watch.NextWarning(Boiler(7, 0.35, false)));
            // The steam came back only to the start pressure, under the margin: not said again yet.
            Assert.Null(watch.NextWarning(Boiler(6, 0.35, false)));
            Assert.Null(watch.NextWarning(Boiler(8, 0.6, true)));
            Assert.StartsWith("steam is down", watch.NextWarning(Boiler(6, 0.6, true)));
        });
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void SteamDisplayFollowsNonEnglishCultureDecimalSeparator(string cultureName)
    {
        WithCulture(CultureInfo.GetCultureInfo(cultureName), () =>
        {
            var crew = new SteamWatch();
            Assert.Equal("Raising steam: 5,0 of 7 bar", crew.Hold(Boiler(5), standing: true, lighting: false));
            var player = new SteamWatch { PlayerFires = true };
            Assert.Equal("Waiting for steam: you're firing (1,0 of 7 bar)", player.Hold(Boiler(1), standing: false, lighting: false));
            Assert.StartsWith("steam is down to 5,0 bar", player.NextWarning(Boiler(5, 0.6, true)));
        });
    }

    [Fact]
    public void WaterOutOfSightIsSaidOnceAndTheCrewIsToldNothing()
    {
        var watch = new SteamWatch { PlayerFires = true };
        watch.Hold(Boiler(glass: 0.05), standing: false, lighting: false);
        Assert.StartsWith("the water is out of sight", watch.NextWarning(Boiler(glass: 0.05)));
        Assert.Null(watch.NextWarning(Boiler(glass: 0.05)));
        Assert.Null(new SteamWatch().NextWarning(Boiler(1, 0, false)));
    }

    [Fact]
    public void AnEngineerHeldOnARoadMoveStandsAndGoesOnWhenLet()
    {
        var b = new TrackGraphBuilder();
        b.AddTrack(3000, new MapPoint(0, 0), new MapPoint(3000, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Forward, 40);
        var engineer = new Engineer();
        engineer.SetRoad(ConsistEnd.A, 15);
        var moved = train.EndA.Offset;
        Drive.Run(engineer, train, world, maxSeconds: 20);
        Assert.True(train.SpeedTowardA > 1, $"under way at {train.SpeedTowardA:F1} m/s");
        engineer.Holding = "Waiting for steam: you're firing (1.0 of 7 bar)";
        var powered = false;
        Drive.Run(engineer, train, world, maxSeconds: 90, onOutput: o => powered |= o.Controls.Throttle > 0);
        Assert.False(powered, "took power while held");
        Assert.True(System.Math.Abs(train.SpeedTowardA) < 0.05, $"still at {train.SpeedTowardA:F1} m/s");
        Assert.Equal(engineer.Holding, engineer.Status);
        var held = train.EndA.Offset;
        engineer.Holding = null;
        Drive.Run(engineer, train, world, maxSeconds: 20);
        Assert.True(train.EndA.Offset > held + 5, "didn't go on");
        Assert.True(held > moved);
    }

    [Fact]
    public void AnEngineerHeldOnAYardMoveStandsToo()
    {
        var b = new TrackGraphBuilder();
        b.AddTrack(1000, new MapPoint(0, 0), new MapPoint(1000, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Forward, 40);
        var engineer = new Engineer { Holding = "Water out of sight: waiting for you to fill the boiler" };
        engineer.SetYard(ConsistEnd.A, 100);
        var start = train.EndA.Offset;
        Drive.Run(engineer, train, world, maxSeconds: 20);
        Assert.Equal(start, train.EndA.Offset, 1);
        Assert.Equal(engineer.Holding, engineer.Status);
    }
}

public class GradeStartTests
{
    private static readonly ControlCapabilities Freight = new() { HasTrainBrake = true };

    /// <summary>
    /// A heavy train held on a 2 % grade by its train brake, whose loco brake
    /// alone can't hold it: started as an order would start it, the cars'
    /// brakes "dragging" until their cylinders are nearly empty.
    /// </summary>
    private static (double LeastPosition, double Position, double Time, bool TookPowerFirst) Start(double uphill)
    {
        var train = new SimTrain { Mass = 1_000_000, MaxTractiveForce = 300_000, Power = 900_000, Grade = 0.02, LocoBrakeDecel = 0.1, TrainBrake = 0.5 };
        var controller = new SpeedController();
        double least = 0, time = 0;
        var poweredBeforeRelease = false;
        for (; time < 240 && train.Position < 100; time += 0.2)
        {
            var dragging = train.TrainBrake > 0.05;
            var output = controller.Update(new ControlInput { Speed = train.Speed, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = dragging, Uphill = uphill }, Freight);
            poweredBeforeRelease |= dragging && output.TrainBrake > 0.1 && output.Throttle > 0.1;
            for (var k = 0; k < 10; k++)
                train.Step(output.Throttle, output.TrainBrake, output.LocoBrake, 0.02);
            least = System.Math.Min(least, train.Position);
        }
        return (least, train.Position, time, poweredBeforeRelease);
    }

    [Fact]
    public void UphillItTakesUpPowerBeforeLettingTheBrakesOffAndDoesNotRollBack()
    {
        var run = Start(0.02);
        Assert.True(run.TookPowerFirst, "never powered against the brakes");
        Assert.True(run.LeastPosition > -0.5, $"rolled back {-run.LeastPosition:F1} m");
        Assert.True(run.Position >= 100, $"got {run.Position:F0} m in {run.Time:F0} s");
    }

    [Fact]
    public void ToldNothingOfTheGradeItRollsBackAsBefore()
    {
        var run = Start(0);
        Assert.False(run.TookPowerFirst);
        Assert.True(run.LeastPosition < -0.5, $"rolled back only {-run.LeastPosition:F1} m");
    }

    [Fact]
    public void OnTheLevelOrDownhillTheBrakesComeOffFirstAsBefore()
    {
        foreach (var uphill in new[] { 0.0, 0.003, -0.02 })
        {
            var controller = new SpeedController();
            var output = controller.Update(new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = uphill }, Freight);
            Assert.Equal(0, output.Throttle);
            Assert.Equal(0, output.TrainBrake);
            Assert.True(controller.WaitingForAir);
            Assert.False(controller.TakingUpPower);
        }
    }

    [Fact]
    public void TheTrainBrakeStaysOnWhilePowerIsTakenUpThenComesOff()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = 0.01 };
        var first = controller.Update(input, Freight);
        Assert.True(controller.TakingUpPower);
        Assert.True(first.TrainBrake > 0.4 && first.LocoBrake == 1 && first.Sand);
        ControlOutput output = first;
        for (var i = 0; i < 100 && controller.TakingUpPower; i++)
            output = controller.Update(input, Freight);
        Assert.False(controller.TakingUpPower);
        Assert.Equal(0.35, output.Throttle, 2);
        Assert.Equal(0, output.TrainBrake);
        Assert.Equal(1, output.LocoBrake);
        // The cars free: power carries on from there, not from nothing.
        input.BrakesDragging = false;
        output = controller.Update(input, Freight);
        Assert.True(output.Throttle >= 0.35, $"throttle {output.Throttle:F2}");
        Assert.Equal(0, output.LocoBrake);
    }

    [Fact]
    public void AHoldThatCreepsGetsTheWholeTrainBrakeUntilTheTrainTakesPower()
    {
        var controller = new SpeedController();
        Assert.Equal(0.5, controller.Held(Freight).TrainBrake);
        Assert.Equal(1, controller.Held(Freight, speed: -0.3).TrainBrake);
        Assert.Equal(1, controller.Held(Freight).TrainBrake);
        // Starting uphill, the power is taken up against the whole brake.
        var input = new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = 0.01 };
        Assert.Equal(1, controller.Update(input, Freight).TrainBrake);
        input.BrakesDragging = false;
        input.Speed = 0.5;
        controller.Update(input, Freight);
        Assert.Equal(0.5, controller.Held(Freight).TrainBrake);
    }

    [Fact]
    public void PullingHardUphillAtLowSpeedRunsTheSand()
    {
        var controller = new SpeedController();
        ControlOutput output = default;
        for (var i = 0; i < 100; i++)
            output = controller.Update(new ControlInput { Speed = 1.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, Uphill = 0.02 }, Freight);
        Assert.True(output.Sand);
        var level = new SpeedController();
        for (var i = 0; i < 100; i++)
            output = level.Update(new ControlInput { Speed = 1.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2 }, Freight);
        Assert.False(output.Sand);
    }

    [Fact]
    public void NearTheGeneratorsLimitTheThrottleOpensNoFurtherAndOverItComesBack()
    {
        var controller = new SpeedController();
        ControlOutput output = default;
        for (var i = 0; i < 50; i++)
            output = controller.Update(new ControlInput { Speed = 1, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2 }, Freight);
        var at = output.Throttle;
        for (var i = 0; i < 25; i++)
            output = controller.Update(new ControlInput { Speed = 1, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, AmpsShare = 0.85 }, Freight);
        Assert.Equal(at, output.Throttle, 6);
        output = controller.Update(new ControlInput { Speed = 1, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, AmpsShare = 0.95 }, Freight);
        Assert.True(output.Throttle < at, $"throttle {output.Throttle:F2}");
    }

    [Fact]
    public void AgainstTheBrakesPowerIsTakenUpOnlyAsFarAsTheGeneratorAllows()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = 0.02 };
        var output = controller.Update(input, Freight);
        for (var i = 0; i < 10; i++)
            output = controller.Update(input, Freight);
        var before = output.Throttle;
        Assert.True(controller.TakingUpPower);
        input.AmpsShare = 0.85;
        output = controller.Update(input, Freight);
        Assert.Equal(before, output.Throttle, 6);
        Assert.False(controller.TakingUpPower);
        Assert.Equal(0, output.TrainBrake);
    }

    [Fact]
    public void UphillWithNoSpeedAskedYetTheBrakesStayOn()
    {
        var controller = new SpeedController();
        var output = controller.Update(new ControlInput { Speed = 0, Target = 0, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = 0.015 }, Freight);
        Assert.Equal(0.5, output.TrainBrake);
        Assert.Equal(1, output.LocoBrake);
        Assert.Equal(0, output.Throttle);
        output = controller.Update(new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = 0.015 }, Freight);
        Assert.True(controller.TakingUpPower);
        Assert.Equal(0.5, output.TrainBrake);
    }

    [Fact]
    public void RestingTheMotorsTheTrainIsHeldOnItsBrakes()
    {
        var controller = new SpeedController();
        ControlOutput output = default;
        // Full throttle at a stand long enough to rest the motors.
        for (var t = 0.0; t < 60 && !controller.Resting; t += 0.2)
            output = controller.Update(new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2 }, Freight);
        Assert.True(controller.Resting);
        output = controller.Update(new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2 }, Freight);
        Assert.Equal(0, output.Throttle);
        Assert.Equal(0.5, output.TrainBrake);
        output = controller.Update(new ControlInput { Speed = -0.3, Target = 5, ToStop = 500, Dt = 0.2 }, Freight);
        Assert.Equal(1, output.TrainBrake);
    }

    [Fact]
    public void OnAGradeTheTrainIsHeldOnTheWholeTrainBrakeFromTheFirst()
    {
        Assert.Equal(1, new SpeedController().Held(Freight, grade: 0.01).TrainBrake);
        Assert.Equal(1, new SpeedController().Held(Freight, grade: -0.01).TrainBrake);
        Assert.Equal(0.5, new SpeedController().Held(Freight, grade: 0.002).TrainBrake);
    }

    [Fact]
    public void NearTheRailsGripTheThrottleHoldsAndJustUnderItEasesBack()
    {
        var controller = new SpeedController();
        ControlOutput output = default;
        for (var i = 0; i < 50; i++)
            output = controller.Update(new ControlInput { Speed = 1, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2 }, Freight);
        var at = output.Throttle;
        for (var i = 0; i < 25; i++)
            output = controller.Update(new ControlInput { Speed = 1, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, GripShare = 0.9 }, Freight);
        Assert.Equal(at, output.Throttle, 6);
        output = controller.Update(new ControlInput { Speed = 1, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, GripShare = 0.99 }, Freight);
        Assert.Equal(at - 0.03, output.Throttle, 6);
    }

    [Fact]
    public void AgainstTheBrakesPowerIsTakenUpOnlyToMostOfTheGrip()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.2, BrakesDragging = true, Uphill = 0.02 };
        for (var i = 0; i < 5; i++)
            controller.Update(input, Freight);
        Assert.True(controller.TakingUpPower);
        input.GripShare = 0.85;
        var output = controller.Update(input, Freight);
        Assert.False(controller.TakingUpPower);
        Assert.Equal(0, output.TrainBrake);
    }

    [Fact]
    public void ARoadMoveHeldOnAGradeHasTheWholeTrainBrake()
    {
        var controller = new SpeedController();
        var output = controller.Update(new ControlInput { Speed = 0, Target = 0, ToStop = double.PositiveInfinity, Halt = true, Dt = 0.2, Uphill = 0.011 }, Freight);
        Assert.Equal(1, output.TrainBrake);
    }

    [Fact]
    public void OverTheGripTheThrottleComesBackTowardWhatTheRailTakesNotToNothing()
    {
        var controller = new SpeedController();
        ControlOutput output = default;
        for (var i = 0; i < 4; i++)
            output = controller.Update(new ControlInput { Speed = 0.2, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2 }, Freight);
        var at = output.Throttle;
        Assert.True(at > 0.04);
        for (var i = 0; i < 50; i++)
            output = controller.Update(new ControlInput { Speed = 0.2, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, GripShare = 1.2 }, Freight);
        Assert.True(output.Throttle > 0, $"throttle {output.Throttle}");
    }

    [Fact]
    public void SlowAndNearTheGripTheSandRunsWhateverTheThrottle()
    {
        var controller = new SpeedController();
        var output = controller.Update(new ControlInput { Speed = 0.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2 }, Freight);
        Assert.False(output.Sand);
        output = controller.Update(new ControlInput { Speed = 0.5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, GripShare = 0.9 }, Freight);
        Assert.True(output.Sand);
        output = controller.Update(new ControlInput { Speed = 5, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.2, GripShare = 0.9 }, Freight);
        Assert.False(output.Sand);
    }

    [Fact]
    public void ATrainThatWontMoveIsGivenUpOnAfterAFairTryAndTheCauseIsTheGrip()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = 0.02, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.25, Slipping = true, GripShare = 1.05 };
        for (var i = 0; i < 4 * 170; i++)
            controller.Update(input, Freight);
        Assert.Equal(CantMove.No, controller.CantMove);
        for (var i = 0; i < 4 * 20; i++)
            controller.Update(input, Freight);
        Assert.Equal(CantMove.Grip, controller.CantMove);
        controller.ForgetTries();
        Assert.Equal(CantMove.No, controller.CantMove);
    }

    [Fact]
    public void ASlowTrainMakingWayIsNotGivenUpOn()
    {
        var controller = new SpeedController();
        // 0.4 m/s up a grade at the grip: 120 m in 300 s.
        var input = new ControlInput { Speed = 0.4, Target = 11, ToStop = double.PositiveInfinity, Dt = 0.25, GripShare = 0.95 };
        for (var i = 0; i < 4 * 300; i++)
            controller.Update(input, Freight);
        Assert.Equal(CantMove.No, controller.CantMove);
    }

    [Fact]
    public void StandingForAStopOrTheAirIsNotTryingToMove()
    {
        var controller = new SpeedController();
        for (var i = 0; i < 4 * 400; i++)
            controller.Update(new ControlInput { Speed = 0, Target = 0, ToStop = double.PositiveInfinity, Halt = true, Dt = 0.25 }, Freight);
        Assert.Equal(CantMove.No, controller.CantMove);
        for (var i = 0; i < 4 * 400; i++)
            controller.Update(new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.25, BrakesDragging = true }, Freight);
        Assert.Equal(CantMove.No, controller.CantMove);
    }

    [Fact]
    public void UphillAgainstTheBrakesTheTakeUpCountsAsTrying()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = 0, Target = 5, ToStop = 500, Dt = 0.25, BrakesDragging = true, Uphill = 0.016, GripShare = 0.9 };
        for (var i = 0; i < 4 * 200; i++)
            controller.Update(input, Freight);
        Assert.NotEqual(CantMove.No, controller.CantMove);
    }

    [Fact]
    public void AnEngineerThatCantMoveItsTrainSaysSoOnceAndHoldsItTillTheNextOrder()
    {
        var b = new TrackGraphBuilder();
        b.AddTrack(3000, new MapPoint(0, 0), new MapPoint(3000, 0));
        var world = new FakeWorld(b.Build());
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Forward, 40);
        train.Physics.MaxTractiveForce = 3_000;
        train.Physics.Power = 20_000;
        train.Physics.Grade = 0.02;
        var engineer = new Engineer();
        engineer.SetRoad(ConsistEnd.A, 15);
        var gaveUp = 0;
        CantMove cause = CantMove.No;
        var seen = new System.Collections.Generic.List<string>();
        Drive.Run(engineer, train, world, maxSeconds: 400, onOutput: o =>
        {
            var line = $"{engineer.Status} thr {o.Controls.Throttle:F2} v {train.SpeedTowardA:F2}";
            if (seen.Count == 0 || !seen[seen.Count - 1].StartsWith(engineer.Status))
                seen.Add(line);
            if (engineer.TakeGiveUp(out var c, out _, out _))
            {
                gaveUp++;
                cause = c;
            }
        });
        Assert.True(gaveUp == 1, $"gave up {gaveUp}; {string.Join(" | ", seen)}; at {train.EndA.Offset:F1} m, {train.SpeedTowardA:F2} m/s");
        Assert.NotEqual(CantMove.No, cause);
        Assert.Equal(EngineerMode.Waypoints, engineer.Mode);
        Assert.Empty(engineer.Queue);
        Assert.Equal("Can't get the train moving here", engineer.Status);
        engineer.SetRoad(ConsistEnd.A, 15);
        Drive.Run(engineer, train, world, maxSeconds: 5);
        Assert.NotEqual("Can't get the train moving here", engineer.Status);
    }

    [Fact]
    public void ATrainRollingBackWhileItTriesIsGivenUpOnAtOnce()
    {
        var controller = new SpeedController();
        var input = new ControlInput { Speed = -0.25, Target = 8, ToStop = double.PositiveInfinity, Dt = 0.25, Slipping = true };
        var ticks = 0;
        while (controller.CantMove == CantMove.No && ticks < 4 * 180)
        {
            controller.Update(input, Freight);
            ticks++;
        }
        Assert.Equal(CantMove.Grip, controller.CantMove);
        Assert.True(ticks < 4 * 30, $"{ticks / 4.0:F0} s");
    }
}
