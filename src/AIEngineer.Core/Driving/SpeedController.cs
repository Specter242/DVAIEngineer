using System;

namespace AIEngineer.Core.Driving
{
    /// <summary>The controls an engineer works, each 0 to 1.</summary>
    public struct ControlOutput
    {
        public double Throttle;
        public double TrainBrake;
        public double LocoBrake;
        public double DynamicBrake;

        /// <summary>Whether to sand the rails: the wheels are slipping, or just were.</summary>
        public bool Sand;

        public override string ToString() =>
            $"throttle {Throttle:F2} train {TrainBrake:F2} loco {LocoBrake:F2} dynamic {DynamicBrake:F2}";
    }

    /// <summary>What the controller knows each tick.</summary>
    public struct ControlInput
    {
        /// <summary>Speed toward the leading end, m/s; negative when rolling back.</summary>
        public double Speed;

        /// <summary>The planner's target now, m/s.</summary>
        public double Target;

        /// <summary>Meters to where the train must be stopped; infinity if nowhere ahead.</summary>
        public double ToStop;

        /// <summary>Hold the train stopped where it stands.</summary>
        public bool Hold;

        /// <summary>Ordered to stand (a speed of nothing): no creep toward a stop, a hold once stopped.</summary>
        public bool Halt;

        /// <summary>Seconds since the last tick.</summary>
        public double Dt;

        /// <summary>
        /// Brakes still on behind the loco (a car's cylinders not yet
        /// released, a hand brake, the train line not charged): standing,
        /// the controller takes no power until they're off.
        /// </summary>
        public bool BrakesDragging;

        /// <summary>
        /// The planner's target a brake delay ahead of the train, if
        /// <see cref="HasTargetAhead"/>: under slow air the brake goes on
        /// once that falls below the speed, so it bites as the target
        /// itself starts down.
        /// </summary>
        public double TargetAhead;
        public bool HasTargetAhead;

        /// <summary>
        /// Close work (the run in to a coupling): the target is kept to
        /// within a quarter of a meter a second, not the looser band that
        /// keeps a train on the road from see-sawing.
        /// </summary>
        public bool Tight;

        /// <summary>Whether a loco's driven wheels are spinning on the rail.</summary>
        public bool Slipping;
    }

    /// <summary>The train's controls as the engineer finds them.</summary>
    public struct ControlCapabilities
    {
        /// <summary>Whether cars behind the loco brake on the train brake (false for a light engine).</summary>
        public bool HasTrainBrake;

        public bool HasDynamicBrake;

        /// <summary>
        /// The train brake is Realistic Brakes': an application runs down
        /// the train over many seconds, and (American equipment) any rise in
        /// the pipe releases the cars all the way and they take minutes to
        /// recharge. So it is set and held, stepped up if need be, never
        /// eased while moving; the loco and dynamic brakes do the fine work.
        /// </summary>
        public bool SlowAir;
    }

    public sealed class ControlTuning
    {
        /// <summary>Throttle per m/s of shortfall.</summary>
        public double ThrottleGain = 0.15;

        /// <summary>Throttle added per second per m/s of steady shortfall.</summary>
        public double ThrottleIntegral = 0.03;

        /// <summary>Seconds ahead the controller looks at the current acceleration.</summary>
        public double AccelerationLead = 2.5;

        /// <summary>Within this shortfall, m/s, the throttle's integral builds; and no further than this.</summary>
        public double IntegralBand = 1.5;
        public double IntegralLimit = 0.7;

        /// <summary>The fastest the throttle opens, per second (a crew notches up, it doesn't slam).</summary>
        public double ThrottleRate = 0.12;

        /// <summary>
        /// Well under its target (past <see cref="IntegralBand"/>, or standing
        /// short of where it's going) and gaining
        /// less than <see cref="StallAcceleration"/>, m/s², the throttle keeps
        /// opening by this much a second, up to full: a heavy train, or one
        /// on a grade, that the proportional part alone can't start or keep
        /// going (a DE2 sat at a third throttle under two loaded hoppers for
        /// 47 minutes in game, 2026-11-05).
        /// </summary>
        public double StallRamp = 0.05;
        public double StallAcceleration = 0.03;

        /// <summary>
        /// At this throttle or more with the train not moving for
        /// <see cref="StallLimit"/> s, the throttle comes off for
        /// <see cref="StallRest"/> s: a traction motor at full current and no
        /// speed overheats, and in Derail Valley an overheated motor dies or
        /// blows its fuse.
        /// </summary>
        public double StallThrottle = 0.6;
        public double StallLimit = 20;
        public double StallRest = 60;

        /// <summary>
        /// With the wheels slipping the throttle comes back by this much a
        /// second until they bite, stays at or under where they did for
        /// <see cref="SlipHold"/> s, and the sand runs for
        /// <see cref="SandFor"/> s after: a crew's hand on a slipping
        /// engine. Notching on up through a slip only polishes the rail.
        /// </summary>
        public double SlipCut = 0.5;
        public double SlipHold = 3;
        public double SandFor = 10;

        /// <summary>Under slow air, the most throttle worked against a set train brake: past it, the brake comes off rather than fight the power.</summary>
        public double AirStretchMax = 0.35;

        /// <summary>Overspeed, m/s, at which the engineer starts braking.</summary>
        public double BrakeOnOverspeed = 0.7;

        /// <summary>Overspeed, m/s, below which braking stops.</summary>
        public double BrakeOffOverspeed = 0.15;

        /// <summary>The same two for close work (<see cref="ControlInput.Tight"/>), and the overspeed at which slow air's train brake is set there.</summary>
        public double TightBrakeOnOverspeed = 0.25;
        public double TightBrakeOffOverspeed = 0.05;
        public double TightAirApplyOverspeed = 0.4;

        /// <summary>Brake per m/s of overspeed while braking.</summary>
        public double BrakeGain = 0.25;

        /// <summary>The brake held whenever braking, before the gain adds to it.</summary>
        public double BrakeBase = 0.15;

        /// <summary>The speed the train creeps at over the last meters to a stop, m/s.</summary>
        public double CreepSpeed = 1.0;

        /// <summary>Within this distance of the stop, at creep speed or less, the brakes go on hard, m.</summary>
        public double FinalStopDistance = 1.5;

        /// <summary>Seconds the final brake application takes to bite.</summary>
        public double FinalBrakeLead = 1.5;

        /// <summary>The deceleration counted on for the final stop, m/s².</summary>
        public double FinalDeceleration = 0.5;

        /// <summary>The train brake held while standing.</summary>
        public double HoldTrainBrake = 0.5;

        /// <summary>
        /// Under slow air a hold keeps only what is set, the loco brake
        /// doing the rest; but asked for with the train moving faster than
        /// <see cref="HoldSetAbove"/>, m/s, the train brake is set, and
        /// faster than <see cref="HoldFullAbove"/> it is full service: the
        /// loco brake alone won't stop a train, or hold one on a grade.
        /// </summary>
        public double HoldSetAbove = 1.0;
        public double HoldFullAbove = 3.0;

        /// <summary>
        /// Under slow air, seconds the loco brake may have the last meters
        /// to itself without slowing the train before the train brake goes
        /// on: a grade it can't hold.
        /// </summary>
        public double AirFinalPatience = 2.0;

        // ---- Slow air (Realistic Brakes) ----

        /// <summary>Overspeed, m/s, at which the train brake is set with the target steady (an overshoot the loco brake didn't catch).</summary>
        public double AirApplyOverspeed = 1.0;

        /// <summary>The first application: a little over the minimum reduction (about 0.5 bar).</summary>
        public double AirFirstSet = 0.35;

        /// <summary>What each step adds, and how long after the last before another, s (about the time an application takes to show in the speed).</summary>
        public double AirStep = 0.15;
        public double AirStepWait = 8;

        /// <summary>
        /// A step is made while the train is this far over its target, m/s,
        /// and slowing less than it must: as fast as the target falls, plus
        /// the overspeed closed over <see cref="AirCloseOver"/> seconds.
        /// </summary>
        public double AirStepOverspeed = 0.2;
        public double AirCloseOver = 20;

        /// <summary>This far over, m/s, and still gaining, steps come every <see cref="AirUrgentWait"/> s.</summary>
        public double AirUrgentOverspeed = 2.0;
        public double AirUrgentWait = 3;

        /// <summary>
        /// Under slow air, within this of a stop, m, slower than
        /// <see cref="AirLetGoSpeed"/>, m/s, and under its target (the brake
        /// more than it needs), the train brake comes off and the loco brake
        /// takes the last meters: held on, it stalls the train short of the
        /// stop, which must then wait for the cars to release and creep on.
        /// Further out, within <see cref="AirLetGoFar"/> m, it comes off once
        /// the train is <see cref="AirLetGoUnder"/> m/s under its target at a
        /// yard pace: European brakes take most of a minute to release, so
        /// it must go early.
        /// </summary>
        public double AirLetGoDistance = 40;
        public double AirLetGoSpeed = 1.5;
        public double AirLetGoFar = 150;
        public double AirLetGoUnder = 1.0;

        /// <summary>Under slow air, the creep over the last meters, m/s, and the deceleration counted on for the final stop on the loco brake, m/s².</summary>
        public double AirCreepSpeed = 0.6;
        public double AirFinalDeceleration = 0.12;

        /// <summary>
        /// An application comes off once the train is this far under its
        /// target, m/s, with no stop within <see cref="AirReleaseClear"/> m
        /// plus <see cref="AirRechargeRun"/> seconds of running: the cars
        /// need about that long to recharge before they brake fully again.
        /// </summary>
        public double AirReleaseUnder = 1.0;
        public double AirReleaseClear = 250;
        public double AirRechargeRun = 60;

        /// <summary>The target falling faster than this, m/s per s, with the train over it at all, means the braking curve has begun: the brake goes on then rather than waiting for the overspeed.</summary>
        public double AirFalling = 0.04;
    }

    /// <summary>
    /// Works throttle and brakes to follow the planner's target: a PI on
    /// the throttle, brakes from overspeed with hysteresis so it doesn't
    /// see-saw between power and brake, a creep over the last meters, and
    /// a hold once stopped.
    /// </summary>
    public sealed class SpeedController
    {
        private double throttle;
        private double integral;
        private bool braking;
        private double lastSpeed = double.NaN;
        private double acceleration;

        // The train brake application held under slow air, 0 for none, and when it was last made or stepped.
        private double airSet;
        private double airSetAt = double.NegativeInfinity;
        private double clock;
        // The overspeed last tick and how fast it changes, m/s per s, smoothed over about two seconds.
        private double lastOver = double.NaN;
        private double overRate;
        // The target last tick and how fast it changes, m/s per s, smoothed the same.
        private double lastTarget = double.NaN;
        private double targetRate;
        private bool curveWas;
        private double steadyFor;
        private double stalledFor;
        private double restUntil = double.NegativeInfinity;
        // How long the loco brake has had the last meters without slowing the train.
        private double notSlowingFor;
        // The throttle the wheels last bit at after slipping, until when it is kept to, and until when the sand runs.
        private double slipCeiling;
        private double slipUntil = double.NegativeInfinity;
        private double sandUntil = double.NegativeInfinity;

        public SpeedController(ControlTuning tuning = null)
        {
            Tuning = tuning ?? new ControlTuning();
        }

        public ControlTuning Tuning { get; }

        public bool Braking => braking;

        /// <summary>The train brake held under slow air (0 for none): it stays across a <see cref="Reset"/>, as the handle does.</summary>
        public double AirSet => airSet;

        /// <summary>
        /// Under slow air, the application last needed just to hold a steady
        /// speed (0 on the level): the share of the brake a grade takes, which
        /// a stop can't count on.
        /// </summary>
        public double AirHolding { get; private set; }

        /// <summary>Whether the throttle is off to rest the motors after a stall.</summary>
        public bool Resting => clock < restUntil;

        /// <summary>How many times in a row the train wouldn't move under power: reset once it does.</summary>
        public int Stalls { get; private set; }

        /// <summary>Set by the last update when it held the train to let the brakes come off before taking power.</summary>
        public bool WaitingForAir { get; private set; }

        public void Reset()
        {
            throttle = 0;
            integral = 0;
            braking = false;
            lastSpeed = double.NaN;
            acceleration = 0;
            lastOver = double.NaN;
            overRate = 0;
            lastTarget = double.NaN;
            targetRate = 0;
            curveWas = false;
            notSlowingFor = 0;
        }

        /// <summary>
        /// The controls for holding the train where it stands: the loco
        /// brake, and the train brake as tuned. Under slow air that is the
        /// brake as it is set, or more: <paramref name="firm"/> for a long
        /// stand, and whatever stops a train still moving at
        /// <paramref name="speed"/>, m/s.
        /// </summary>
        public ControlOutput Held(ControlCapabilities caps, bool firm = false, double speed = 0)
        {
            if (!caps.HasTrainBrake)
                airSet = 0;
            double train = 0;
            if (caps.HasTrainBrake)
            {
                if (caps.SlowAir)
                {
                    var moving = Math.Abs(speed);
                    var wanted = moving > Tuning.HoldFullAbove ? 1
                        : firm || moving > Tuning.HoldSetAbove ? Tuning.HoldTrainBrake
                        : 0;
                    if (airSet < wanted)
                    {
                        airSet = wanted;
                        airSetAt = clock;
                    }
                    train = airSet;
                }
                else
                {
                    train = Tuning.HoldTrainBrake;
                }
            }
            return new ControlOutput { TrainBrake = train, LocoBrake = 1 };
        }

        public ControlOutput Update(ControlInput input, ControlCapabilities caps)
        {
            var t = Tuning;
            var output = new ControlOutput();
            var speed = input.Speed;
            clock += input.Dt;
            WaitingForAir = false;
            var slow = caps.SlowAir && caps.HasTrainBrake;
            if (!caps.HasTrainBrake)
                airSet = 0;
            var overNow = speed - input.Target;
            if (!double.IsNaN(lastOver) && input.Dt > 0)
                overRate += ((overNow - lastOver) / input.Dt - overRate) * Math.Min(1, input.Dt / 2.0);
            lastOver = overNow;
            if (!double.IsNaN(lastTarget) && input.Dt > 0)
                targetRate += ((input.Target - lastTarget) / input.Dt - targetRate) * Math.Min(1, input.Dt / 2.0);
            lastTarget = input.Target;
            var aheadTarget = input.HasTargetAhead ? input.TargetAhead : input.Target;
            var curveBegun = (targetRate < -t.AirFalling && overNow > 0.1) || (slow && aheadTarget < speed - 0.3 && input.Target >= speed - 0.5);
            // Holding a steady speed with the train brake set (a downgrade): what that takes is the brake not left for a stop.
            if (slow && Math.Abs(targetRate) < 0.01 && Math.Abs(overNow) < 0.6 && aheadTarget >= speed - 0.3)
            {
                steadyFor += input.Dt;
                if (steadyFor > 10)
                    AirHolding = airSet;
            }
            else
            {
                steadyFor = 0;
            }
            // As a braking curve begins, the brake goes on (or, already set to hold a grade, more goes on):
            // not when the train is under its target already, with power holding it up.
            if (slow && curveBegun && !curveWas && overNow > 0)
            {
                airSet = airSet > 0 ? Math.Min(1, airSet + t.AirStep) : t.AirFirstSet;
                airSetAt = clock;
            }
            curveWas = curveBegun;
            // Acceleration, smoothed over about a second, to see the target coming.
            if (!double.IsNaN(lastSpeed) && input.Dt > 0)
                acceleration += ((speed - lastSpeed) / input.Dt - acceleration) * Math.Min(1, input.Dt / 1.0);
            lastSpeed = speed;

            // Within 2 m of the stop and standing is there: the engineer counts it arrived too.
            if (input.Hold || (input.ToStop <= t.FinalStopDistance + 0.5 && Math.Abs(speed) < 0.3)
                || (input.Target <= 0.01 && Math.Abs(speed) < 0.3 && (input.Halt || double.IsPositiveInfinity(input.ToStop))))
            {
                notSlowingFor = 0;
                return HoldStopped(caps, speed);
            }

            // Standing with brakes still on behind the loco: let them off (the train brake
            // released, the loco brake holding) and take no power until they are.
            if (input.BrakesDragging && Math.Abs(speed) < 0.05)
            {
                throttle = 0;
                integral = 0;
                braking = false;
                airSet = 0;
                WaitingForAir = true;
                return new ControlOutput { LocoBrake = 1 };
            }

            // Never let the target fall below a creep while a stop is still ahead,
            // or the train stops short and must start again. Not when ordered to
            // stand, and not with no stop ahead (the target is the target).
            var target = input.Target;
            // Slow near the stop with more brake than it needs: let the train brake go before it stalls the train short.
            if (slow && airSet > 0 && speed > 0.05
                && ((input.ToStop < t.AirLetGoDistance && speed < t.AirLetGoSpeed && speed < input.Target - 0.2)
                    || (input.ToStop < t.AirLetGoFar && speed < 3 && speed < input.Target - t.AirLetGoUnder)))
            {
                airSet = 0;
                airSetAt = clock;
            }
            var creep = slow ? t.AirCreepSpeed : t.CreepSpeed;
            var finalDecel = slow ? t.AirFinalDeceleration : t.FinalDeceleration;
            if (!input.Halt && !double.IsPositiveInfinity(input.ToStop) && input.ToStop > t.FinalStopDistance)
                target = Math.Max(target, Math.Min(creep, input.ToStop));

            // Close enough to stop, allowing for the brakes to bite: on with them, at creep
            // speed or when behind the braking curve. On the curve and faster than a creep,
            // the curve does the slowing: a hard application then a release see-saws the
            // train (the first runs in game, 2026-10-19).
            var v = Math.Max(0, speed);
            var behindTheCurve = speed - input.Target > t.BrakeOnOverspeed;
            if ((v <= creep * 1.5 || behindTheCurve)
                && input.ToStop <= t.FinalStopDistance + v * t.FinalBrakeLead + v * v / (2 * finalDecel))
            {
                throttle = 0;
                integral = 0;
                braking = true;
                if (slow)
                {
                    // Slow air: the last meters are the loco brake's; a train application now would
                    // bite after the stop, and only have to come off again before it can start.
                    // Unless the loco brake isn't enough: the train well behind its curve (a stop
                    // that turned up inside the braking distance), past the mark and still
                    // moving, or not slowing at all (a grade). Then all the brake there is.
                    notSlowingFor = speed > 0.3 && acceleration > -0.01 ? notSlowingFor + input.Dt : 0;
                    if (airSet < 1 && speed > 0.3 && (behindTheCurve || input.ToStop <= 0 || notSlowingFor > t.AirFinalPatience))
                    {
                        airSet = 1;
                        airSetAt = clock;
                    }
                }
                output.TrainBrake = slow ? airSet : caps.HasTrainBrake ? 0.7 : 0;
                output.LocoBrake = 1;
                return output;
            }
            notSlowingFor = 0;

            var shortfall = target - speed;
            var brakeOn = input.Tight ? t.TightBrakeOnOverspeed : t.BrakeOnOverspeed;
            var brakeOff = input.Tight ? t.TightBrakeOffOverspeed : t.BrakeOffOverspeed;
            if (braking ? -shortfall < brakeOff : -shortfall < brakeOn)
            {
                braking = false;
                if (slow && airSet > 0)
                {
                    // The application stays on until the train is well under its target with no
                    // stop near; then it comes off all at once (an American valve can't ease off,
                    // and easing a European one takes most of a minute). Until then, when the
                    // brake is more than the curve wants, power makes up the difference
                    // (stretch braking): the throttle is the fine control the brake can't be.
                    var clear = t.AirReleaseClear + t.AirRechargeRun * Math.Max(0, speed);
                    if (speed < target - t.AirReleaseUnder && input.ToStop > clear)
                    {
                        airSet = 0;
                        airSetAt = clock;
                    }
                }
                // The shortfall a moment ahead at this acceleration: a brisk light engine eases
                // off before the target, not after (it overshot by 3 km/h in game, 2026-10-19).
                var ahead = target - (speed + Math.Max(0, acceleration) * t.AccelerationLead);
                // The integral only builds near the target, so it doesn't wind up on the way;
                // but a train well under it and not gaining needs more than the proportional part.
                var stalled = shortfall > 0.2 && acceleration < t.StallAcceleration && (shortfall > t.IntegralBand || Math.Abs(speed) < 0.3);
                if (stalled)
                    integral = Clamp(integral + t.StallRamp * input.Dt, 0, 1);
                else if (Math.Abs(ahead) < t.IntegralBand)
                    integral = Clamp(integral + t.ThrottleIntegral * ahead * input.Dt, 0, Math.Max(t.IntegralLimit, integral));
                var wanted = Clamp(t.ThrottleGain * ahead + integral, 0, 1);
                // Power against a set train brake only for a small correction: needing more, the train
                // is over-braked, and the brake comes off rather than fight it (the motors heat).
                if (slow && airSet > 0 && wanted > t.AirStretchMax)
                {
                    airSet = 0;
                    airSetAt = clock;
                }
                // Notch up gently; shut off at once.
                var throttleWas = throttle;
                throttle = wanted > throttle ? Math.Min(wanted, throttle + t.ThrottleRate * input.Dt) : wanted;
                if (input.Slipping)
                {
                    throttle = Math.Max(0, Math.Min(throttle, throttleWas) - t.SlipCut * input.Dt);
                    slipCeiling = throttle;
                    slipUntil = clock + t.SlipHold;
                    sandUntil = clock + t.SandFor;
                }
                else if (clock < slipUntil)
                {
                    throttle = Math.Min(throttle, slipCeiling);
                }
                if (clock < slipUntil)
                    integral = Math.Min(integral, throttle);
                output.Sand = clock < sandUntil;
                // A train that won't move under power: rest the motors rather than burn them.
                // Not moving forward under power: standing, or rolling back down the grade;
                // or its wheels only spinning.
                if (speed < 0.1 && (throttle >= t.StallThrottle || clock < slipUntil))
                {
                    stalledFor += input.Dt;
                    if (stalledFor > t.StallLimit)
                    {
                        stalledFor = 0;
                        Stalls++;
                        restUntil = clock + t.StallRest;
                    }
                }
                else if (speed > 0.3)
                {
                    stalledFor = 0;
                    Stalls = 0;
                }
                if (Resting)
                {
                    throttle = 0;
                    integral = 0;
                    output.Throttle = 0;
                    output.LocoBrake = 1;
                    output.TrainBrake = slow ? airSet : 0;
                    return output;
                }
                output.Throttle = throttle;
                if (slow)
                    output.TrainBrake = airSet;
                // Rolling back against the way it should go: hold it on the loco brake until power takes it.
                if (speed < -0.2)
                    output.LocoBrake = 1;
                return output;
            }

            braking = true;
            throttle = 0;
            integral = 0;
            var brake = Clamp(t.BrakeBase + t.BrakeGain * (-shortfall - brakeOff), 0, 1);
            if (slow)
            {
                // The loco and dynamic brakes follow the overspeed; the train brake is set,
                // then stepped up while the train still isn't slowing enough, never eased.
                var over = -shortfall;
                if (airSet <= 0)
                {
                    if (over > (input.Tight ? t.TightAirApplyOverspeed : t.AirApplyOverspeed))
                    {
                        airSet = t.AirFirstSet;
                        airSetAt = clock;
                    }
                }
                else
                {
                    // Not slowing as fast as it must (the target's fall, and the overspeed closed
                    // over a while), once the last step has had time to show: more air.
                    var needed = Math.Max(0, -targetRate) + over / t.AirCloseOver;
                    var slowing = -acceleration;
                    var due = clock - airSetAt > t.AirStepWait
                        || (over > t.AirUrgentOverspeed && overRate > 0 && clock - airSetAt > t.AirUrgentWait);
                    if (due && over > t.AirStepOverspeed && slowing < needed)
                    {
                        airSet = Math.Min(1, airSet + t.AirStep);
                        airSetAt = clock;
                    }
                }
                output.TrainBrake = airSet;
                output.LocoBrake = brake;
                if (caps.HasDynamicBrake)
                    output.DynamicBrake = Clamp(brake * 1.5, 0, 1);
                return output;
            }
            if (caps.HasDynamicBrake)
                output.DynamicBrake = Clamp(brake * 1.5, 0, 1);
            if (caps.HasTrainBrake)
                output.TrainBrake = caps.HasDynamicBrake ? Clamp((brake - 0.3) * 1.4, 0, 1) : brake;
            else
                output.LocoBrake = brake;
            return output;
        }

        private ControlOutput HoldStopped(ControlCapabilities caps, double speed)
        {
            throttle = 0;
            integral = 0;
            braking = true;
            return Held(caps, speed: speed);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
