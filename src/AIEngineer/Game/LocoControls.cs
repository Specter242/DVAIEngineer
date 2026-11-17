using System;
using AIEngineer.Core.Driving;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>
    /// One cab control in the engineer's hand. It sets the control through
    /// the game's overrider (the lever follows) and remembers the value that
    /// took; any later change it didn't make is the player's hand. For a
    /// moment after each move a lever may still settle, so then only a
    /// bigger change counts.
    /// </summary>
    internal sealed class ControlHand
    {
        private const float SettleTime = 1.5f;
        private const float Moved = 0.06f;
        private const float MovedWhileSettling = 0.2f;
        private const float Deadband = 0.01f;

        private readonly OverridableBaseControl control;
        private float seen = float.NaN;
        private float settleUntil;

        public ControlHand(OverridableBaseControl control)
        {
            this.control = control;
        }

        public bool Present => control != null;

        public float Value => control != null ? control.Value : 0f;

        /// <summary>Whether the player moved the control since the engineer last set it.</summary>
        public bool TakenByPlayer(float now) =>
            control != null && !float.IsNaN(seen)
            && Mathf.Abs(control.Value - seen) > (now < settleUntil ? MovedWhileSettling : Moved);

        public void Set(float want, float now)
        {
            if (control == null)
                return;
            want = Mathf.Clamp01(want);
            // A notched lever settles on a notch: ask for one, or it snaps back
            // and the engineer would set it again every tick (and take the snap
            // for the player's hand).
            if (control.IsNotched && control.NotchCount >= 1f)
                want = Mathf.Round(want * control.NotchCount) / control.NotchCount;
            var value = control.Value;
            if (Mathf.Abs(value - want) > Deadband)
            {
                control.Set(want);
                settleUntil = now + SettleTime;
            }
            // What took (a blocked control keeps its value), not what was asked.
            seen = control.Value;
        }

        /// <summary>Whether the control now stands lower than the engineer left it.</summary>
        public bool Lowered => control != null && !float.IsNaN(seen) && control.Value < seen;

        /// <summary>Takes the control as it stands for the engineer's own setting.</summary>
        public void Adopt()
        {
            if (control != null)
                seen = control.Value;
        }

        /// <summary>Forget what it set, so the player's control isn't mistaken later.</summary>
        public void Release() => seen = float.NaN;
    }

    /// <summary>
    /// The loco's controls under the engineer: throttle, train and loco
    /// brakes, dynamic brake, and reverser. A manual-lap brake valve is
    /// worked to hold the brake pipe at the pressure a self-lapping valve
    /// would give for the same handle.
    /// </summary>
    internal sealed class LocoControls
    {
        private readonly TrainCar loco;
        private readonly BaseControlsOverrider overrider;
        private readonly ControlHand throttle;
        private readonly ControlHand trainBrake;
        private readonly ControlHand locoBrake;
        private readonly ControlHand dynamicBrake;
        private readonly ControlHand reverser;
        private readonly OverridableBaseControl sander;

        // After something else eased the throttle (wheel-slip protection): no more than this until then.
        private float throttleCeiling = 1f;
        private float ceilingUntil = float.NegativeInfinity;

        /// <summary>Seconds the throttle stays at or under where something else eased it to.</summary>
        private const float EasedHold = 4f;

        /// <summary>A steam loco: the reverser is its cutoff, notched up as it gathers speed.</summary>
        public bool Steam { get; set; }

        public LocoControls(TrainCar loco)
        {
            this.loco = loco;
            overrider = loco.SimController?.controlsOverrider;
            throttle = new ControlHand(overrider?.Throttle);
            trainBrake = new ControlHand(overrider?.Brake);
            locoBrake = new ControlHand(overrider?.IndependentBrake);
            dynamicBrake = new ControlHand(overrider?.DynamicBrake);
            reverser = new ControlHand(overrider?.Reverser);
            sander = overrider?.Sander;
        }

        public bool Usable => overrider != null && throttle.Present && reverser.Present && (trainBrake.Present || locoBrake.Present);

        public bool HasDynamicBrake => dynamicBrake.Present;

        public bool HasLocoBrake => locoBrake.Present;

        public bool HasTrainBrakeControl => trainBrake.Present;

        public bool EngineRunning
        {
            get
            {
                var reader = overrider?.EngineOnReader;
                return reader == null || reader.IsOn;
            }
        }

        /// <summary>The reverser, 0 back, 0.5 neutral, 1 forward.</summary>
        public float Reverser => reverser.Value;

        /// <summary>The throttle (a steam loco's regulator), 0 to 1.</summary>
        public float Throttle => throttle.Value;

        /// <summary>The control someone or something else has moved since the engineer set it, as the player reads it; null if none.</summary>
        public string MovedByAnother(float now) =>
            reverser.TakenByPlayer(now) ? "reverser"
            : trainBrake.TakenByPlayer(now) ? "train brake"
            : locoBrake.TakenByPlayer(now) ? "loco brake"
            : dynamicBrake.TakenByPlayer(now) ? "dynamic brake"
            : throttle.TakenByPlayer(now) ? "throttle"
            : null;

        /// <summary>
        /// Whether all that was moved is the throttle, eased: what the
        /// game's wheel-slip and overheat protection gadgets do. With no
        /// one at the controls the engineer follows it rather than let go.
        /// </summary>
        public bool OnlyThrottleEased(float now) =>
            throttle.TakenByPlayer(now) && throttle.Lowered && !reverser.TakenByPlayer(now) && !trainBrake.TakenByPlayer(now)
            && !locoBrake.TakenByPlayer(now) && !dynamicBrake.TakenByPlayer(now);

        /// <summary>Takes the eased throttle as its own, and opens it no further for a few seconds.</summary>
        public void FollowEasedThrottle(float now)
        {
            throttle.Adopt();
            throttleCeiling = throttle.Value;
            ceilingUntil = now + EasedHold;
        }

        public void Release()
        {
            if (cutoutBefore.HasValue)
            {
                try
                {
                    overrider?.BrakeCutout?.Set(cutoutBefore.Value);
                }
                catch (Exception e)
                {
                    Main.Warn($"a helper's brake valve couldn't be put back: {e.GetBaseException().Message}");
                }
                cutoutBefore = null;
            }
            throttle.Release();
            trainBrake.Release();
            locoBrake.Release();
            dynamicBrake.Release();
            reverser.Release();
        }

        /// <summary>The way the reverser drives the loco: true toward its front, false back, null in neutral.</summary>
        public bool? ReverserForward
        {
            get
            {
                if (!reverser.Present)
                    return null;
                var v = reverser.Value;
                return v > 0.6f ? true : v < 0.4f ? false : (bool?)null;
            }
        }

        /// <summary>
        /// Leaves the loco safe as the engineer lets go (not to the player):
        /// power off and the brakes on, so a train it was driving stops.
        /// </summary>
        public void Secure(float now)
        {
            // Each control by itself: one that throws (another mod's loco) mustn't leave the rest as they were.
            Try(() => throttle.Set(0f, now));
            Try(() => dynamicBrake.Set(0f, now));
            Try(() => locoBrake.Set(1f, now));
            Try(() =>
            {
                if (trainBrake.Present)
                {
                    var brakes = loco.brakeSystem;
                    trainBrake.Set(brakes == null || brakes.selfLappingController ? 0.8f : 0.7f, now);
                }
            });
            Try(() => sander?.Set(0f));
            Release();
        }

        private static void Try(Action set)
        {
            try
            {
                set();
            }
            catch (Exception e)
            {
                Main.Warn($"a control couldn't be set while securing a loco: {e.GetBaseException().Message}");
            }
        }

        /// <summary>Gets the loco ready to be driven: brake valve cut in, loco hand brake off.</summary>
        public void Prepare()
        {
            overrider?.BrakeCutout?.Set(1f);
            ReleaseHandbrake();
        }

        private float? cutoutBefore;

        /// <summary>
        /// A helper's levers, set as they come (no route, no speed control):
        /// its regulator, its reverser (null leaves it), its loco brake. Its
        /// automatic brake valve is cut out the first time, the lead's
        /// handle having the train, and put back as it was on release.
        /// </summary>
        public void Help(float regulator, float? reverserValue, float brake, float now)
        {
            var cutout = overrider?.BrakeCutout;
            if (cutout != null && cutoutBefore == null)
            {
                cutoutBefore = cutout.Value;
                cutout.Set(0f);
            }
            if (reverserValue.HasValue && Mathf.Abs(reverser.Value - reverserValue.Value) > 0.03f)
            {
                // A change of way only shut off.
                var turning = (reverser.Value > 0.5f) != (reverserValue.Value > 0.5f);
                if (turning)
                {
                    throttle.Set(0f, now);
                    regulator = 0f;
                }
                reverser.Set(reverserValue.Value, now);
            }
            throttle.Set(regulator, now);
            locoBrake.Set(brake, now);
        }

        /// <summary>Takes off the loco's own hand brake, by its cab control so the wheel follows.</summary>
        public void ReleaseHandbrake() => overrider?.Handbrake?.Set(0f);

        /// <param name="forward">Whether the loco moves toward its own front; null leaves the reverser.</param>
        /// <param name="standing">Whether the train stands: the reverser moves only then.</param>
        /// <param name="speed">Speed, m/s, either way: a steam loco's cutoff is notched up with it.</param>
        public void Apply(ControlOutput output, bool? forward, bool standing, float now, double speed = 0)
        {
            if (forward.HasValue && Steam)
            {
                // Full gear to start; notched up with speed, the way it is going, without shutting off.
                var v = Math.Abs(speed);
                var cutoff = v < 3 ? 1f : v < 8 ? 0.75f : v < 14 ? 0.5f : 0.35f;
                var want = forward.Value ? 0.5f + 0.5f * cutoff : 0.5f - 0.5f * cutoff;
                var sameWay = (reverser.Value > 0.5f) == forward.Value && Mathf.Abs(reverser.Value - 0.5f) > 0.1f;
                if (sameWay)
                {
                    if (Mathf.Abs(reverser.Value - want) > 0.04f)
                        reverser.Set(want, now);
                    forward = null;
                }
            }
            if (forward.HasValue)
            {
                var want = forward.Value ? 1f : 0f;
                if (Mathf.Abs(reverser.Value - want) > 0.01f)
                {
                    // The reverser moves only at a stand, and with the throttle shut
                    // (the game blocks it otherwise); until then, no power.
                    throttle.Set(0f, now);
                    output.Throttle = 0;
                    if (standing)
                        reverser.Set(want, now);
                }
            }
            var power = (float)output.Throttle;
            if (now < ceilingUntil)
                power = Mathf.Min(power, throttleCeiling);
            throttle.Set(power, now);
            if (sander != null)
            {
                var sand = output.Sand ? 1f : 0f;
                if (Mathf.Abs(sander.Value - sand) > 0.01f)
                    sander.Set(sand);
            }
            dynamicBrake.Set((float)output.DynamicBrake, now);
            locoBrake.Set((float)output.LocoBrake, now);
            WorkTrainBrake((float)output.TrainBrake, now);
        }

        private void WorkTrainBrake(float level, float now)
        {
            if (!trainBrake.Present)
                return;
            var brakes = loco.brakeSystem;
            if (brakes == null || brakes.selfLappingController)
            {
                trainBrake.Set(level, now);
                return;
            }
            // Manual lap: above 0.9 emergency, 0.5-0.9 apply, 0.1-0.5 lap, below release.
            var target = Mathf.Lerp(6f, 4.5f, level);
            var pipe = brakes.brakePipePressure;
            float handle;
            if (level <= 0.01f || pipe < target - 0.12f)
                handle = 0f;
            else if (pipe > target + 0.06f)
                handle = 0.7f;
            else
                handle = 0.3f;
            trainBrake.Set(handle, now);
        }
    }
}
