using System;
using AIEngineer.Core.Driving;
using UnityEngine;

namespace AIEngineer.Game
{
    internal sealed class Helper
    {
        private readonly Session session;
        private readonly WhistleReader ear = new WhistleReader();
        private readonly DV.Simulation.Controllers.OverridableBaseControl leadThrottle, leadReverser;
        private readonly WhistleSound leadWhistle;
        private readonly DV.Simulation.Controllers.HornControl ownWhistle;
        // The way it works, relative to the lead (true: the lead's forward); null while it waits.
        private bool? workAhead;
        private bool lastAhead = true;
        private bool sameWay;
        private float answerFrom = -1;
        private bool heardGo;

        public Helper(Session session, TrainCar lead)
        {
            this.session = session;
            Lead = lead;
            var overrider = lead.SimController?.controlsOverrider;
            leadThrottle = overrider?.Throttle;
            leadReverser = overrider?.Reverser;
            leadWhistle = new WhistleSound(lead);
            ownWhistle = session.Loco.SimController?.controlsOverrider?.Horn;
            sameWay = Vector3.Dot(session.Loco.transform.forward, lead.transform.forward) > 0;
        }

        public TrainCar Lead { get; }

        /// <summary>Whether it can be a helper: a lead with a whistle and levers to read.</summary>
        public bool Usable => leadThrottle != null && leadReverser != null && leadWhistle.Usable;

        /// <summary>What the lead's whistle is heard from, for the log.</summary>
        public string HeardFrom => leadWhistle.Source;

        public string Status { get; private set; } = "Helper: waiting for two long";

        /// <summary>Each frame: the lead's whistle heard, and the answer blown.</summary>
        public void Listen(float now)
        {
            if (Lead == null)
                return;
            ear.Timing = Main.Settings != null ? Main.Settings.Whistle : WhistleTiming.Default;
            var signal = ear.Feed(now, leadWhistle.Level(now));
            if (signal.HasValue)
                Heard(signal.Value, now);
            Answer(now);
        }

        /// <summary>
        /// How hard a whistle sounds, 0 to 1, as the game's horn reads its
        /// lever: one neutral at 0 sounds as it is pulled; any other rests at
        /// 0.5 and sounds toward either end (Set(1) blows it, Set(0) rests it).
        /// </summary>
        internal static float Sounding(DV.Simulation.Controllers.HornControl whistle) =>
            whistle == null ? 0f : whistle.neutralAt0 ? whistle.Value : Mathf.Abs(whistle.Value * 2f - 1f);

        private void Heard(WhistleSignal signal, float now)
        {
            if (Main.Settings == null || Main.Settings.LogWhistle)
                Main.Log($"helper {session.Name}: heard {ear.Heard} ({signal}) from {leadWhistle.Source}; loudness every 0.1 s, %: {ear.Trace}");
            var standing = Math.Abs(session.HasState ? session.State.SpeedTowardA : 0) < Engineer.StandingSpeed;
            string said;
            var reverser = leadReverser.Value;
            bool? leadWay = reverser > 0.55f ? true : reverser < 0.45f ? false : (bool?)null;
            switch (signal)
            {
                case WhistleSignal.Go:
                    workAhead = leadWay ?? lastAhead;
                    lastAhead = workAhead.Value;
                    heardGo = true;
                    said = $"two long: releasing, working {Way(workAhead.Value)} with you";
                    break;
                case WhistleSignal.Back when standing:
                    workAhead = !lastAhead;
                    lastAhead = workAhead.Value;
                    heardGo = true;
                    said = $"three short: set back, working {Way(workAhead.Value)} with you";
                    break;
                case WhistleSignal.Back:
                    said = "three short, but we're moving: stop first (one short)";
                    break;
                case WhistleSignal.Stop:
                    workAhead = null;
                    said = "one short: shut off, brake applied";
                    break;
                case WhistleSignal.Acknowledge:
                    return;
                default:
                    var t = ear.Timing;
                    Main.Notify($"AI engineer: helper {session.Name} heard {ear.Heard}: no signal it knows. Two long go, one short stop, three short (standing) back up; " +
                        $"a short is {t.Shortest:0.##} to {t.ShortestLong:0.##} s, a long {t.ShortestLong:0.##} s or more.");
                    return;
            }
            Main.Notify($"AI engineer: helper {session.Name}: {said}.");
            Main.Log($"helper {session.Name}: {said}");
            // Two short back, in a moment.
            answerFrom = now + 0.6f;
        }

        private const float AnswerBlast = 0.7f, AnswerGap = 0.6f;

        /// <summary>
        /// Two short blasts on its own whistle, 0.7 s each and 0.6 s apart,
        /// set every frame so a cord that springs back can't cut one short.
        /// </summary>
        private void Answer(float now)
        {
            if (answerFrom < 0 || ownWhistle == null)
                return;
            var t = now - answerFrom;
            if (t < 0)
                return;
            const float second = AnswerBlast + AnswerGap, end = second + AnswerBlast;
            var on = t < AnswerBlast || (t >= second && t < end);
            ownWhistle.Set(on ? 1f : 0f);
            if (t >= end)
            {
                ownWhistle.Set(0f);
                answerFrom = -1;
            }
        }

        /// <summary>
        /// A few times a second: its levers from the lead's. Returns the
        /// regulator it set, for its fireman.
        /// </summary>
        public float Tick(float now, bool standing, double speed)
        {
            var controls = session.Controls;
            if (workAhead == null)
            {
                controls.Help(0f, null, standing ? 1f : 0.5f, now);
                Status = heardGo ? "Helper: shut off, brake on; two long to go" : "Helper: waiting for two long";
                session.Engineer.SetHelperStatus(Status);
                return 0f;
            }
            // The lead's levers: its regulator, and its reverser's way and cutoff.
            var regulator = Mathf.Clamp01(leadThrottle.Value);
            var reverser = leadReverser.Value;
            var leadAhead = reverser > 0.5f;
            var cutoff = Mathf.Clamp01(Mathf.Abs(reverser - 0.5f) * 2f);
            var wantAhead = workAhead.Value;
            var leadAgrees = cutoff < 0.1f || leadAhead == wantAhead;
            // Its own reverser the same way as the lead's if it faces the same way, else the other.
            var ownForward = wantAhead == sameWay;
            var ownCutoff = Mathf.Max(cutoff, standing ? 1f : 0.25f);
            if (!leadAgrees)
                regulator = 0f;
            controls.Help(regulator, ownForward ? 0.5f + 0.5f * ownCutoff : 0.5f - 0.5f * ownCutoff, 0f, now);
            Status = !leadAgrees ? $"Helper: your reverser is set {Way(leadAhead)}; it works {Way(wantAhead)}: shut off"
                : regulator < 0.02f ? $"Helper: ready {Way(wantAhead)}, shut off with you"
                : $"Helper: working {Way(wantAhead)}, regulator {regulator * 100:F0}%, cutoff {ownCutoff * 100:F0}%";
            session.Engineer.SetHelperStatus(Status);
            return regulator;
        }

        /// <summary>A way relative to the lead: its forward ("ahead") or reverse ("back").</summary>
        private static string Way(bool ahead) => ahead ? "ahead" : "back";

        /// <summary>It stops helping: whistle quiet; the controls' own release puts its brake valve back.</summary>
        public void Release()
        {
            ownWhistle?.Set(0f);
            answerFrom = -1;
        }
    }
}
