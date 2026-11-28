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

        /// <summary>Worst loco wheelslip severity (0..1); 0 when unknown or not slipping.</summary>
        public double SlipLevel;

        /// <summary>Traction motor temperature, °C; 0 for a loco without traction motors.</summary>
        public double MotorHeat;

        /// <summary>The grade the train stands on, rise over run the way it is to go: positive uphill.</summary>
        public double Uphill;

        /// <summary>The traction generator's current as a share of its limit; 0 when unknown.</summary>
        public double AmpsShare;

        /// <summary>The most grip-limited loco's driving force as a share of what the rail will take before its wheels slip; 0 when unknown.</summary>
        public double GripShare;

        /// <summary>Optional cap on the final-stop deceleration counted on, m/s² (0 uses the tuning default); used when braking a short cut on the independent alone.</summary>
        public double FinalDeceleration;
        /// <summary>
        /// The same driving force as a share of what the rail would take without
        /// sand (0 when unknown, in which case <see cref="GripShare"/> stands in):
        /// lets the sander turn on before the rail is exceeded and stay on without
        /// chattering once sand raises adhesion.
        /// </summary>
        public double UnsandedGripShare;
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

        /// <summary>
        /// Short cut under slow air: work the loco's independent brake alone while it holds
        /// the braking curve, and bring in the train brake automatically if the independent
        /// brake saturates and the train still falls behind the curve before a stop.
        /// </summary>
        public bool PreferIndependent;

        /// <summary>
        /// The loco's independent brake is manual-lap (Realistic Brakes' American
        /// E-T S-6: running, lap, slow application, quick application), so it
        /// builds pressure more slowly than a self-lapping independent.
        /// </summary>
        public bool ManualLapLocoBrake;
    }

    /// <summary>
    /// Maps a desired independent brake share (0 to 1) onto Realistic Brakes'
    /// S-6 manual-lap independent positions (0.25 running, 0.50 lap, 0.75 slow
    /// application) from the engine's current brake cylinder gauge pressure.
    /// </summary>
    public static class ManualLapIndependent
    {
        public const double Running = 0.25;
        public const double Lap = 0.50;
        public const double SlowApplication = 0.75;
        public const double MaxBar = 3.1;

        public static double Handle(double level, double cylinderBar, double maxBar = MaxBar)
        {
            if (level <= 0.01)
                return Running;
            if (level >= 0.98)
                return cylinderBar < maxBar - 0.15 ? SlowApplication : Lap;
            var target = Math.Max(0, Math.Min(1, level)) * maxBar;
            if (cylinderBar < target - 0.12)
                return SlowApplication;
            if (cylinderBar > target + 0.15)
                return Running;
            return Lap;
        }
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

        /// <summary>
        /// The fastest the throttle opens, per second: about a notch every 1.4 s
        /// (a crew notches up, it doesn't slam). At twice this a DE2 under
        /// 300 t blew its motors' fuse on a grade (in game, 2026-11-18).
        /// </summary>
        public double ThrottleRate = 0.06;

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
        /// With the wheels slipping the throttle drops by
        /// <see cref="SlipInitialCut"/> at once and <see cref="SlipCut"/> a
        /// second until they bite, stays at or under where they did for
        /// <see cref="SlipHold"/> s (doubling up to <see cref="SlipHoldMost"/>
        /// when another slip comes within <see cref="SlipRepeatWithin"/> s of
        /// the last hold ending, so the engineer stops slamming back into a
        /// notch the rail won't take), then rises only by
        /// <see cref="SlipRecovery"/> a second. The sand runs for
        /// <see cref="SandFor"/> s after a slip; a slip within
        /// <see cref="SandRepeatWithin"/> s of the sand ending doubles that,
        /// up to <see cref="SandMost"/>.
        /// </summary>
        public double SlipInitialCut = 0.08;
        public double SlipCut = 0.6;
        public double SlipHold = 4;
        public double SlipHoldMost = 16;
        public double SlipRepeatWithin = 15;
        public double SlipRecovery = 0.025;
        public double SandFor = 5;
        public double SandRepeatWithin = 8;
        public double SandMost = 30;

        /// <summary>
        /// Proactive sanding on the unsanded grip share (at any speed while
        /// under power): turns on at <see cref="SandGripOn"/> before a notch
        /// breaks adhesion and stays on until it falls under
        /// <see cref="SandGripOff"/>, so sand neither chatters nor quits while
        /// pulling hard at road speed.
        /// </summary>
        public double SandGripOn = 0.72;
        public double SandGripOff = 0.55;

        /// <summary>
        /// Traction motors over <see cref="MotorEaseFrom"/>, °C, get less
        /// throttle, falling to <see cref="MotorEaseFloor"/> over the next
        /// <see cref="MotorEaseSpan"/> degrees: the game blows their fuse after
        /// 5 s over 120 °C, and a heavy train at low speed gets there at full
        /// throttle in under half a minute (a DE2 under three loaded hoppers on
        /// a 1.56 % grade, in game, 2026-11-18).
        /// </summary>
        public double MotorEaseFrom = 100;
        public double MotorEaseSpan = 15;
        public double MotorEaseFloor = 0.15;

        /// <summary>
        /// Over this share of the generator's current limit the throttle opens
        /// no further, and over <see cref="AmpsMost"/> it comes back: the game
        /// blows the generator's fuse the moment the limit is passed (a DE2
        /// powering against its brakes on a grade, in game, 2026-11-19).
        /// </summary>
        public double AmpsEaseFrom = 0.8;
        public double AmpsMost = 0.92;

        /// <summary>
        /// Over this share of the rail's grip the throttle opens no further,
        /// and over <see cref="GripMost"/> it comes back by
        /// <see cref="GripCut"/> a second and holds for <see cref="GripHold"/>
        /// s so a notched throttle doesn't bounce right back into the notch
        /// that exceeded grip: a crew feels for the rail rather than slipping
        /// and shutting off. On wet rail a DE2 starting 302 t put 150 kN
        /// against 84 kN of grip, slipped, shut off, and rolled back (in game,
        /// 2026-11-19).
        /// </summary>
        public double GripEaseFrom = 0.78;
        public double GripMost = 0.90;
        public double GripCut = 0.25;
        public double GripHold = 3;

        /// <summary>
        /// Size of one throttle step assumed when checking whether the next
        /// notch would exceed <see cref="GripNextMost"/> once moving and past
        /// notch 1: on a notched diesel (DE2 1/11, DE6/DH4 1/8) going from
        /// notch 2 to notch 3 adds half as much force again, so holding only
        /// at a fixed share still lets the next notch jump straight into wheel
        /// slip. Not applied at standstill or notch 1, where a diesel's base
        /// step isn't proportional to throttle (a DE2 at 50 % grip on notch 1
        /// would otherwise freeze at notch 1 and roll back on wet 1.6 %).
        /// </summary>
        public double GripStep = 0.09;
        public double GripNextMost = 0.98;

        /// <summary>Taking up power against the brakes stops at this share of the grip: past it the wheels would slip once the brakes are off.</summary>
        public double GripTakeUp = 0.75;

        /// <summary>
        /// Starting uphill from at least this grade with the train brake on,
        /// the engineer takes up power against the brakes first, then lets
        /// the train brake off while the loco brake and the power hold it,
        /// and lets the loco brake off once the cars are free. The other way
        /// round (brakes off, then power) a heavy train rolls back on the loco
        /// brake alone, and on wet rail never gets going (a DE2 under 302 t on
        /// 0.98 %, in game, 2026-11-18).
        /// </summary>
        public double GradeStartFrom = 0.004;

        /// <summary>The throttle taken up against the brakes: this, and <see cref="GradeStartPerGrade"/> per unit of grade, to at most <see cref="GradeStartMost"/>.</summary>
        public double GradeStartPower = 0.2;
        public double GradeStartPerGrade = 15;
        public double GradeStartMost = 0.6;

        /// <summary>The sand runs through a grade start until the train has this speed, m/s, or for at most <see cref="GradeStartSandFor"/> s from the release.</summary>
        public double GradeStartSandBelow = 1.5;
        public double GradeStartSandFor = 30;

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
        /// With cars behind the loco, the train brake does the slowing and
        /// stopping and the loco brake stays at or under this while the train
        /// moves (Ender Voidwalker, 2026-11-19: with ten cars behind, "the
        /// Engineer really wants to use the Independent at full tilt"); under
        /// <see cref="StandingBelow"/> m/s it goes full to hold the train.
        /// A light engine brakes on the loco brake as before.
        /// </summary>
        public double LocoBrakeWithCars = 0.2;
        public double StandingBelow = 0.1;

        /// <summary>Under slow air with cars behind, the train brake's first application at this overspeed, m/s, so it leads (alone, <see cref="AirApplyOverspeed"/>).</summary>
        public double AirApplyOverspeedWithCars = 0.5;

        /// <summary>On stock air with cars, the train brake for the last meters to the mark (the loco brake light): 0.7 with the loco brake full, before.</summary>
        public double FinalTrainBrakeWithCars = 0.85;

        /// <summary>On stock air with cars, this far over the target in the last meters, m/s, the loco brake goes full with the train brake.</summary>
        public double AllBrakeBehind = 2.0;

        /// <summary>Under slow air with cars, seconds between steps of the train brake in the last meters, when the train isn't slowing enough to stop at the mark.</summary>
        public double AirFinalStepWait = 3;

        /// <summary>
        /// A held train moving faster than this, m/s, gets the whole train
        /// brake, kept until it next takes power; and so does one held on a
        /// grade of <see cref="GradeStartFrom"/> or more, from the first. On
        /// wet rail on 1.08 %, 302 t held at 0.3 or 0.5 of the train brake
        /// slid (the game's wheel slide, a fifth of the grip) and crept back;
        /// at 0.7 or all of it, it stood (in game, 2026-11-19).
        /// </summary>
        public double HoldCreep = 0.05;

        /// <summary>
        /// Pulling uphill from this grade under this speed, m/s, with this much
        /// throttle or more, the sand runs: a crew sands a heavy train up a
        /// bank. Under that speed with the pull near the rail's grip
        /// (<see cref="GripEaseFrom"/>) it runs too, whatever the throttle: on
        /// wet rail the grip runs out long before the throttle is open.
        /// </summary>
        public double SandUphillFrom = 0.01;
        public double SandUphillBelow = 3;
        public double SandUphillThrottle = 0.5;

        /// <summary>
        /// Trying to move the train for <see cref="GiveUpAfter"/> s (wanting
        /// <see cref="GiveUpTarget"/> m/s or more, the stop at least
        /// <see cref="GiveUpStopClear"/> m off; waits for air, steam, a
        /// switch, or a stop not counted) and making less than
        /// <see cref="GiveUpRun"/> m of it, the engineer gives up: it can't
        /// start or keep the train moving here. A DE2 and 302 t on wet rail
        /// at 1.6 % under the train made 0 to 35 m in its first 180 s each
        /// time it failed, and 76 m or more each time it got away; once
        /// only 10 m in the first 120 s (in game, 2026-11-19).
        /// </summary>
        public double GiveUpAfter = 180;
        public double GiveUpRun = 30;
        public double GiveUpTarget = 0.3;
        public double GiveUpStopClear = 15;

        /// <summary>
        /// Rolled back this far, m, over its tries (net of the way made), it
        /// gives up at once: a start that got away never rolled back at all
        /// (2026-11-19), and a DE2 and 678 t on wet 1.8 % lost 46 m in the 180
        /// s it took to give up.
        /// </summary>
        public double GiveUpRollBack = 5;

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
        // Under PreferIndependent, how long the loco brake has been saturated at full.
        private double independentSaturatedFor;
        // The throttle the wheels last bit at after slipping, until when it is kept to, and until when the sand runs.
        private double slipCeiling = 1;
        private double slipHold;
        private double sandFor;
        private double lastSlipLevel;
        private bool wasSlipping;
        private bool gripSanding;
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

        /// <summary>Set once trying hasn't moved the train far enough (<see cref="ControlTuning.GiveUpAfter"/>); cleared by <see cref="ForgetTries"/>.</summary>
        public CantMove CantMove { get; private set; }

        /// <summary>Meters the train has made good while trying, since the last <see cref="ForgetTries"/>.</summary>
        public double Progress => odometer;

        // The watch on a train that won't move: time spent trying, the meters made good, and a
        // sample of both every few seconds (a fixed ring: no allocation as it runs).
        private const int Samples = 64;
        private const double SampleEvery = 5;
        private readonly double[] sampleAt = new double[Samples];
        private readonly double[] sampleRun = new double[Samples];
        private int sampleCount;
        private int sampleNext;
        private double tryClock;
        private double odometer;
        private double nextSample;
        private double gripBound;
        private double powerBound;

        /// <summary>
        /// Starts the watch on a train that won't move afresh: a new order.
        /// Not <see cref="Reset"/>, which a hold calls every tick: a train
        /// rolling back, stopped, and tried again is still the same try.
        /// </summary>
        public void ForgetTries()
        {
            CantMove = CantMove.No;
            sampleCount = 0;
            sampleNext = 0;
            tryClock = 0;
            odometer = 0;
            nextSample = 0;
            gripBound = 0;
            powerBound = 0;
        }

        /// <summary>
        /// One tick of trying to move the train: the meters made good, what
        /// held it back (the rail's grip, or the loco's power), and whether
        /// it has made too little of a fair try.
        /// </summary>
        private void Watch(ControlInput input, double speed)
        {
            var t = Tuning;
            if (CantMove != CantMove.No || input.Target < t.GiveUpTarget || input.ToStop < t.GiveUpStopClear || input.Dt <= 0)
                return;
            tryClock += input.Dt;
            odometer += speed * input.Dt;
            var gripProjected = speed >= 0.25 && throttle >= 0.15 && input.GripShare > 0
                ? input.GripShare * (throttle + t.GripStep) / throttle
                : input.GripShare;
            if (input.Slipping || input.GripShare > t.GripEaseFrom || gripProjected > t.GripNextMost)
                gripBound += input.Dt;
            else if (Resting || throttle >= 0.9 || input.AmpsShare > t.AmpsEaseFrom || input.MotorHeat > t.MotorEaseFrom)
                powerBound += input.Dt;
            if (tryClock >= nextSample)
            {
                sampleAt[sampleNext] = tryClock;
                sampleRun[sampleNext] = odometer;
                sampleNext = (sampleNext + 1) % Samples;
                sampleCount = Math.Min(Samples, sampleCount + 1);
                nextSample = tryClock + SampleEvery;
            }
            // The newest sample at least the window back.
            var since = double.NaN;
            for (var i = 0; i < sampleCount; i++)
            {
                var k = (sampleNext - 1 - i + Samples) % Samples;
                if (sampleAt[k] <= tryClock - t.GiveUpAfter)
                {
                    since = sampleRun[k];
                    break;
                }
            }
            if ((!double.IsNaN(since) && odometer - since < t.GiveUpRun) || odometer < -t.GiveUpRollBack)
                CantMove = gripBound > 0 && gripBound >= powerBound ? CantMove.Grip : powerBound > 0 ? CantMove.Power : CantMove.Unknown;
        }

        /// <summary>Set by the last update when it held the train to let the brakes come off before taking power.</summary>
        public bool WaitingForAir { get; private set; }

        /// <summary>Set by the last update when, starting uphill, it took up power against the brakes still on.</summary>
        public bool TakingUpPower { get; private set; }

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
            independentSaturatedFor = 0;
            slipCeiling = 1;
            slipHold = 0;
            lastSlipLevel = 0;
            wasSlipping = false;
            gripSanding = false;
            gradeStart = GradeStart.None;
        }

        /// <summary>
        /// The controls for holding the train where it stands: the loco
        /// brake, and the train brake as tuned. Under slow air that is the
        /// brake as it is set, or more: <paramref name="firm"/> for a long
        /// stand, and whatever stops a train still moving at
        /// <paramref name="speed"/>, m/s.
        /// </summary>
        public ControlOutput Held(ControlCapabilities caps, bool firm = false, double speed = 0, double grade = 0)
        {
            // On a grade, the whole train brake from the first: on wet rail half of it lets the
            // wheels slide and the train creep, and once sliding no more brake stops it.
            if (Math.Abs(grade) >= Tuning.GradeStartFrom)
                holdHard = true;
            if (!caps.HasTrainBrake)
                airSet = 0;
            double train = 0;
            if (caps.HasTrainBrake)
            {
                if (caps.PreferIndependent && !firm && Math.Abs(speed) <= Tuning.HoldSetAbove && airSet <= 0)
                {
                    // Standing or nearly stopped on an independent-only cut: hold on the loco brake alone.
                    train = 0;
                }
                else if (caps.SlowAir)
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
                    // Creeping on the hold (wet rail on a grade): all the brake, until it next takes power.
                    if (Math.Abs(speed) > Tuning.HoldCreep)
                        holdHard = true;
                    train = holdHard ? 1 : Tuning.HoldTrainBrake;
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
            TakingUpPower = false;
            var slow = caps.SlowAir && caps.HasTrainBrake && !caps.PreferIndependent;
            if (!caps.HasTrainBrake)
                airSet = 0;
            // When PreferIndependent is active, stay on the loco brake alone unless the train falls
            // clearly behind a braking curve (independent brake couldn't hold the deceleration); once
            // airSet is engaged for safety, hold it under slow-air rules until stopped/clear.
            if (caps.PreferIndependent && caps.HasTrainBrake && airSet > 0)
                slow = true;
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
                independentSaturatedFor = 0;
                return HoldStopped(caps, speed, input.Uphill);
            }

            // Standing with brakes still on behind the loco: let them off (the train brake
            // released, the loco brake holding) and take no power until they are.
            if (input.BrakesDragging && Math.Abs(speed) < 0.05)
            {
                braking = false;
                WaitingForAir = true;
                if (caps.HasTrainBrake && input.Uphill >= t.GradeStartFrom)
                {
                    // No speed asked for yet (an order's first tick): the brakes stay on, never let off unpowered.
                    if (input.Target <= 0.01)
                        return new ControlOutput { LocoBrake = 1, TrainBrake = slow ? airSet : holdHard ? 1 : t.HoldTrainBrake };
                    return TakeUpPower(input, slow);
                }
                throttle = 0;
                integral = 0;
                airSet = 0;
                gradeStart = GradeStart.None;
                return new ControlOutput { LocoBrake = 1 };
            }
            if (gradeStart != GradeStart.None)
            {
                // The cars are free: the power taken up carries on from where it stands.
                integral = Math.Max(integral, throttle);
                gradeStart = GradeStart.None;
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
            var creep = slow || caps.PreferIndependent ? t.AirCreepSpeed : t.CreepSpeed;
            var finalDecel = slow ? t.AirFinalDeceleration : t.FinalDeceleration;
            if (input.FinalDeceleration > 0 && input.FinalDeceleration < finalDecel)
                finalDecel = input.FinalDeceleration;
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
                gripSanding = false;
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
                    // With cars the loco brake is light: slowing less than it takes to stop at the mark,
                    // the train brake is stepped up (or set), a step at a time.
                    else if (caps.HasTrainBrake && airSet < 1 && speed > 0.3 && clock - airSetAt > t.AirFinalStepWait
                        && -acceleration < speed * speed / (2 * Math.Max(0.5, input.ToStop - t.FinalStopDistance)))
                    {
                        airSet = airSet > 0 ? Math.Min(1, airSet + t.AirStep) : t.AirFirstSet;
                        airSetAt = clock;
                    }
                }
                output.TrainBrake = slow ? airSet : caps.HasTrainBrake && !caps.PreferIndependent ? (behindTheCurve ? 1 : t.FinalTrainBrakeWithCars) : 0;
                // Under PreferIndependent, give the independent brake a moment to bite; if it stays
                // saturated without slowing enough to stop at the mark (or we already passed it),
                // bring in the train brake automatically before overshooting the track.
                if (caps.PreferIndependent && caps.HasTrainBrake && !slow)
                {
                    independentSaturatedFor += input.Dt;
                    var neededDecel = speed > 0 ? speed * speed / (2 * Math.Max(1.0, input.ToStop - t.FinalStopDistance)) : 0;
                    if (speed > 0.5 && (input.ToStop <= 0
                        || (independentSaturatedFor >= 1.5 && (-acceleration < neededDecel || behindTheCurve))))
                    {
                        airSet = Math.Max(airSet, behindTheCurve || input.ToStop <= 0 ? 1 : t.AirFirstSet);
                        airSetAt = clock;
                        output.TrainBrake = airSet;
                    }
                }
                // With cars, the train brake stops the train and the loco brake holds it once it stands;
                // a stop the train brake isn't making in time (behind the curve, past the mark, not
                // slowing) gets all the brake there is.
                // (Stock air: the train brake's full service comes on at once, so only past the mark or
                // well behind the curve; in game a train came into the last meters a little behind it
                // at every stop, and the loco brake went full for 7 s.)
                var short_ = slow
                    ? behindTheCurve || input.ToStop <= 0 || notSlowingFor > t.AirFinalPatience
                    : input.ToStop <= 0 || speed - input.Target > t.AllBrakeBehind;
                output.LocoBrake = caps.HasTrainBrake && !caps.PreferIndependent && Math.Abs(speed) >= t.StandingBelow && !short_ ? t.LocoBrakeWithCars : 1;
                return output;
            }
            notSlowingFor = 0;

            var shortfall = target - speed;
            var brakeOn = input.Tight ? t.TightBrakeOnOverspeed : t.BrakeOnOverspeed;
            var brakeOff = input.Tight ? t.TightBrakeOffOverspeed : t.BrakeOffOverspeed;
            if (braking ? -shortfall < brakeOff : -shortfall < brakeOn)
            {
                braking = false;
                independentSaturatedFor = 0;
                Watch(input, speed);
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
                    // Once wheelslip is decaying quickly and pull is under the rail limit, the
                    // wheels are already catching (just smoothing down): hold the cut throttle
                    // rather than chopping all the way to idle.
                    var catching = wasSlipping && input.SlipLevel > 0 && lastSlipLevel > 0
                        && input.SlipLevel < lastSlipLevel * 0.85 && input.GripShare < 1;
                    if (catching)
                        throttle = Math.Min(throttle, throttleWas);
                    else
                    {
                        var cut = t.SlipCut * input.Dt + (wasSlipping ? 0 : t.SlipInitialCut);
                        throttle = Math.Max(0, Math.Min(throttle, throttleWas) - cut);
                    }
                    slipCeiling = throttle;
                    if (!wasSlipping)
                    {
                        slipHold = clock - slipUntil < t.SlipRepeatWithin
                            ? Math.Min(t.SlipHoldMost, Math.Max(t.SlipHold, slipHold * 2))
                            : t.SlipHold;
                        sandFor = clock - sandUntil < t.SandRepeatWithin
                            ? Math.Min(t.SandMost, Math.Max(t.SandFor, sandFor * 2))
                            : t.SandFor;
                    }
                    // If it had to close all the way to idle to catch, don't hold idle long on a grade.
                    var hold = slipCeiling < 0.05 ? Math.Min(slipHold, 2.0) : slipHold;
                    slipUntil = clock + hold;
                    sandUntil = clock + sandFor;
                }
                else if (slipCeiling < 1)
                {
                    if (clock >= slipUntil)
                        slipCeiling = Math.Min(1, slipCeiling + t.SlipRecovery * input.Dt);
                    throttle = Math.Min(throttle, slipCeiling);
                    integral = Math.Min(integral, slipCeiling);
                }
                wasSlipping = input.Slipping;
                lastSlipLevel = input.Slipping ? input.SlipLevel : 0;
                // Hot motors: as much as they can take and no more, however far short the train is.
                if (input.MotorHeat > t.MotorEaseFrom)
                {
                    var most = Math.Max(t.MotorEaseFloor, 1 - (input.MotorHeat - t.MotorEaseFrom) / t.MotorEaseSpan);
                    if (throttle > most)
                    {
                        throttle = most;
                        integral = Math.Min(integral, throttle);
                    }
                }
                // Near the rail's grip (when not already cutting for active slip): no more
                // (including when the next notch would overshoot GripNextMost once moving),
                // and past GripMost, ease back and hold so a notched lever doesn't bounce back.
                if (!input.Slipping)
                {
                    var nextGrip = speed >= 0.25 && throttleWas >= 0.15 && input.GripShare > 0
                        ? input.GripShare * (throttleWas + t.GripStep) / throttleWas
                        : input.GripShare;
                    var gripNear = input.GripShare > t.GripEaseFrom || nextGrip > t.GripNextMost;
                    if (gripNear && throttle > throttleWas)
                        throttle = throttleWas;
                    // Back only toward what the rail will take, never all the way off: shut off, a train
                    // on a wet grade rolls back and starts again, and again (in game, 2026-11-19).
                    if (input.GripShare > t.GripMost)
                    {
                        var backed = Math.Max(throttleWas * t.GripEaseFrom / input.GripShare, Math.Min(throttle, throttleWas) - t.GripCut * input.Dt);
                        throttle = Math.Min(throttle, backed);
                        slipCeiling = Math.Min(slipCeiling, throttle);
                        if (slipUntil < clock + t.GripHold)
                            slipUntil = clock + t.GripHold;
                    }
                    if (gripNear)
                        integral = Math.Min(integral, throttle);
                }
                // Near the generator's limit: no more, and over it, less.
                if (input.AmpsShare > t.AmpsEaseFrom && throttle > throttleWas)
                    throttle = throttleWas;
                if (input.AmpsShare > t.AmpsMost)
                    throttle = Math.Max(0, Math.Min(throttle, throttleWas) - t.SlipCut * input.Dt);
                if (input.AmpsShare > t.AmpsEaseFrom)
                    integral = Math.Min(integral, throttle);
                if (clock < slipUntil)
                    integral = Math.Min(integral, throttle);
                var unsandedGrip = Math.Max(input.GripShare, input.UnsandedGripShare);
                var unsandedNext = throttle >= t.GripStep && unsandedGrip > 0 && wanted > throttle + 0.01
                    ? unsandedGrip * (throttle + t.GripStep) / throttle
                    : unsandedGrip;
                if (throttle <= 0.01)
                    gripSanding = false;
                else if (unsandedGrip >= t.SandGripOn || unsandedNext >= t.GripMost)
                    gripSanding = true;
                else if (unsandedGrip < t.SandGripOff && unsandedNext < t.SandGripOn)
                    gripSanding = false;
                output.Sand = clock < sandUntil || (clock < gradeSandUntil && speed < t.GradeStartSandBelow)
                    || (input.Uphill >= t.SandUphillFrom && speed < t.SandUphillBelow && throttle >= t.SandUphillThrottle)
                    || gripSanding;
                if (!Resting)
                    holdHard = false;
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
                    // Resting on a grade, held on the brakes: on the loco brake alone a heavy train rolls back.
                    throttle = 0;
                    integral = 0;
                    output.Throttle = 0;
                    output.LocoBrake = 1;
                    if (Math.Abs(speed) > t.HoldCreep)
                        holdHard = true;
                    output.TrainBrake = !caps.HasTrainBrake ? 0 : slow ? Math.Max(airSet, holdHard ? 1 : 0) : holdHard ? 1 : t.HoldTrainBrake;
                    if (slow && output.TrainBrake > airSet)
                    {
                        airSet = output.TrainBrake;
                        airSetAt = clock;
                    }
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
            gripSanding = false;
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
                    if (over > (input.Tight ? t.TightAirApplyOverspeed : caps.HasTrainBrake ? t.AirApplyOverspeedWithCars : t.AirApplyOverspeed))
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
                output.LocoBrake = caps.HasTrainBrake ? Math.Min(brake, t.LocoBrakeWithCars) : brake;
                if (caps.HasDynamicBrake)
                    output.DynamicBrake = Clamp(brake * 1.5, 0, 1);
                return output;
            }
            if (caps.HasDynamicBrake)
                output.DynamicBrake = Clamp(brake * 1.5, 0, 1);
            if (caps.HasTrainBrake && !caps.PreferIndependent)
                output.TrainBrake = caps.HasDynamicBrake ? Clamp((brake - 0.3) * 1.4, 0, 1) : brake;
            else
            {
                output.LocoBrake = brake;
                // Safety fallback: if the independent brake stays maxed out without matching the
                // braking curve approaching a stop, engage the train brake early enough to stop on mark.
                if (caps.PreferIndependent && caps.HasTrainBrake && brake >= 0.99)
                {
                    independentSaturatedFor += input.Dt;
                    var neededDecel = !double.IsPositiveInfinity(input.ToStop) && speed > 0
                        ? speed * speed / (2 * Math.Max(2.0, input.ToStop - t.FinalStopDistance))
                        : 0;
                    var curveShort = -shortfall > 1.0 && -acceleration < Math.Max(finalDecel * 0.85, neededDecel);
                    var stopShort = !double.IsPositiveInfinity(input.ToStop) && input.ToStop < 150 && -acceleration < neededDecel;
                    if (independentSaturatedFor >= 1.5 && speed > 1.2 && (curveShort || stopShort))
                    {
                        airSet = Math.Max(airSet, -shortfall > 2.0 ? 1 : t.AirFirstSet);
                        airSetAt = clock;
                        output.TrainBrake = airSet;
                    }
                }
                else
                {
                    independentSaturatedFor = 0;
                }
            }
            return output;
        }

        private enum GradeStart
        {
            None,
            Against,
            Releasing,
        }

        private GradeStart gradeStart;
        private bool holdHard;
        private double gradeSandUntil = double.NegativeInfinity;

        /// <summary>
        /// A start uphill with the brakes still on: power up against them
        /// (train brake as set, loco brake full), then the train brake off
        /// with the loco brake and the power holding, the sand running.
        /// </summary>
        private ControlOutput TakeUpPower(ControlInput input, bool slow)
        {
            var t = Tuning;
            Watch(input, input.Speed);
            var want = Math.Min(t.GradeStartMost, t.GradeStartPower + t.GradeStartPerGrade * input.Uphill);
            if (input.MotorHeat > t.MotorEaseFrom)
                want = Math.Min(want, Math.Max(t.MotorEaseFloor, 1 - (input.MotorHeat - t.MotorEaseFrom) / t.MotorEaseSpan));
            if (gradeStart == GradeStart.None)
                gradeStart = GradeStart.Against;
            // Against locked wheels the current climbs fast: taken up as far as the generator allows.
            if (input.AmpsShare > t.AmpsMost || input.Slipping)
                throttle = Math.Max(0, throttle - t.SlipCut * input.Dt);
            else if (input.AmpsShare <= t.AmpsEaseFrom && input.GripShare <= t.GripTakeUp)
                throttle = Math.Min(want, throttle + t.ThrottleRate * input.Dt);
            integral = throttle;
            gradeSandUntil = clock + t.GradeStartSandFor;
            if (gradeStart == GradeStart.Against && (throttle >= want - 1e-9 || input.AmpsShare > t.AmpsEaseFrom || input.GripShare > t.GripTakeUp))
                gradeStart = GradeStart.Releasing;
            if (gradeStart == GradeStart.Against)
            {
                TakingUpPower = true;
                return new ControlOutput { Throttle = throttle, LocoBrake = 1, TrainBrake = slow ? airSet : holdHard ? 1 : t.HoldTrainBrake, Sand = true };
            }
            airSet = 0;
            return new ControlOutput { Throttle = throttle, LocoBrake = 1, Sand = true };
        }

        private ControlOutput HoldStopped(ControlCapabilities caps, double speed, double grade)
        {
            throttle = 0;
            integral = 0;
            braking = true;
            gripSanding = false;
            return Held(caps, speed: speed, grade: grade);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>Whether the engineer can move its train, and if not, what held it back.</summary>
    public enum CantMove
    {
        No,
        /// <summary>The wheels slipped, or the pull stood at the rail's grip.</summary>
        Grip,
        /// <summary>The loco's power: the throttle open, the current or the motors' heat at their limit, or resting after a stall.</summary>
        Power,
        /// <summary>Neither: something else holds it (a hand brake, a brake that won't come off).</summary>
        Unknown,
    }
}
