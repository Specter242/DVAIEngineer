using System;
using System.Collections.Generic;
using System.Text;

namespace AIEngineer.Core.Driving
{
 public enum WhistleSignal
    {
        /// <summary>Two long: release brakes and proceed.</summary>
        Go,

        /// <summary>One short: apply brakes, stop.</summary>
        Stop,

        /// <summary>Three short, standing: back up.</summary>
        Back,

        /// <summary>Two short: an answer, acknowledging a signal.</summary>
        Acknowledge,

        /// <summary>Blasts that mean nothing to a helper.</summary>
        Unknown,
    }

 public struct WhistleTiming
    {
        /// <summary>A dip shorter than this inside a blast is the cord bouncing or a quill: the blast goes on.</summary>
        public double Bounce;

        /// <summary>A blast shorter than this is a blip (a bounce, a brush of the cord): it doesn't count.</summary>
        public double Shortest;

        /// <summary>The shortest a long blast is: anything shorter (and no blip) is short.</summary>
        public double ShortestLong;

        /// <summary>This long after a blast without another ends the signal, whether the whistle is quiet or held low.</summary>
        public double QuietEnds;

        /// <summary>A fall below this share of the blast's loudness is a gap, quiet or not: the cord eased, not let go.</summary>
        public double Dip;

        public static WhistleTiming Default => new WhistleTiming
        {
            Bounce = 0.25,
            Shortest = 0.4,
            ShortestLong = 1.5,
            QuietEnds = 2.0,
            Dip = 0.5,
        };

        /// <summary>Put in order: each limit at least the one before it, a bounce shorter than a blip and than the quiet that ends a signal.</summary>
        public WhistleTiming Sane()
        {
            var t = this;
            t.Shortest = Math.Max(0.05, t.Shortest);
            t.Bounce = Math.Max(0, Math.Min(t.Bounce, t.Shortest * 0.9));
            t.ShortestLong = Math.Max(t.ShortestLong, t.Shortest);
            t.QuietEnds = Math.Max(t.QuietEnds, t.Bounce + 0.1);
            t.Dip = Math.Max(0.1, Math.Min(0.9, t.Dip));
            return t;
        }
    }

 public sealed class WhistleReader
    {
        /// <summary>From quiet, a blast starts above this loudness.</summary>
        public const double Sounds = 0.15;

        /// <summary>A gap never ends below this: under it the whistle is quiet.</summary>
        public const double Quiet = 0.06;

        /// <summary>After a gap, a new blast once the loudness climbs this share of the way back to the last blast's loudest.</summary>
        public const double Rise = 0.4;

        /// <summary>And at least this far above the gap's quietest.</summary>
        public const double LeastRise = 0.12;

        /// <summary>A burst after a blast within this long of its end, and shorter than <see cref="ReboundLongest"/>, is the cord springing back.</summary>
        public const double ReboundWithin = 0.7;

        /// <summary>The longest a cord's spring back sounds.</summary>
        public const double ReboundLongest = 0.45;

        /// <summary>A spring back inside the debounce comes after the whistle has been quiet this long; a shorter silence is a held cord's bounce.</summary>
        public const double ReboundQuiet = 0.15;

        private const double SampleEvery = 0.1;
        private const int SampleCount = 600;

        private enum State { Between, Blast, Dipping }

        private readonly List<double> blasts = new List<double>();
        private State state;
        private double blastFrom, dipFrom;
        private double peak;
        private double lastPeak;
        private double floor;
        private double lastEnd;
        // The last dip inside this blast that went quiet and came back within the bounce, and when it came back.
        private double quietDipFrom = -1, backAt;

        // The loudness every tenth of a second, in percent, the last minute: the log's trace of each signal.
        private readonly byte[] samples = new byte[SampleCount];
        private int sampled;
        private double nextSample = double.NegativeInfinity;
        private int signalFrom = -1;

        public WhistleTiming Timing = WhistleTiming.Default;

        /// <summary>The last signal read, as heard: "long 2.40 s, long 2.10 s".</summary>
        public string Heard { get; private set; } = "";

        /// <summary>The last signal's loudness every tenth of a second in percent, from just before its first blast to its end.</summary>
        public string Trace { get; private set; } = "";

        /// <summary>Whether a signal is being blown: a blast sounding, or blasts counted and the signal not yet ended.</summary>
        public bool Hearing => state != State.Between || blasts.Count > 0;

        /// <summary>Takes how loud the whistle sounds at <paramref name="time"/> (s); returns a signal when one has ended, else null.</summary>
        public WhistleSignal? Feed(double time, double level)
        {
            Sample(time, level);
            switch (state)
            {
                case State.Blast:
                    peak = Math.Max(peak, level);
                    if (level < DipBelow)
                    {
                        state = State.Dipping;
                        dipFrom = time;
                        floor = level;
                    }
                    return null;

                case State.Dipping:
                    floor = Math.Min(floor, level);
                    if (level > RiseTo(peak))
                    {
                        // Back up before a gap's worth: a quill or a bounce, the same blast.
                        // Quiet a while (a let-go, not a bounce's flicker) and back: perhaps the cord springing back.
                        if (floor < Quiet && time - dipFrom >= ReboundQuiet)
                        {
                            quietDipFrom = dipFrom;
                            backAt = time;
                        }
                        state = State.Blast;
                        peak = Math.Max(peak, level);
                        return null;
                    }
                    if (time - dipFrom < Timing.Bounce)
                        return null;
                    // Down long enough: the blast ended where the loudness fell, or, if all after a
                    // quiet dip was a moment's sound, where it fell before that: the cord sprang back.
                    state = State.Between;
                    var end = quietDipFrom >= 0 && dipFrom - backAt < ReboundLongest ? quietDipFrom : dipFrom;
                    quietDipFrom = -1;
                    var length = end - blastFrom;
                    var rebound = blasts.Count > 0 && blastFrom - lastEnd < ReboundWithin && length < ReboundLongest;
                    if (length >= Timing.Shortest && !rebound)
                    {
                        blasts.Add(length);
                        lastEnd = end;
                        lastPeak = peak;
                    }
                    return blasts.Count == 0 ? SignalUnstarted() : null;

                default:
                    floor = Math.Min(floor, level);
                    if (level > RiseTo(blasts.Count > 0 ? lastPeak : 0))
                    {
                        // A blast after the quiet that ends a signal: that signal is over, if it wasn't fed the quiet.
                        var ended = blasts.Count > 0 && time - lastEnd > Timing.QuietEnds ? End() : (WhistleSignal?)null;
                        state = State.Blast;
                        blastFrom = time;
                        peak = level;
                        quietDipFrom = -1;
                        if (blasts.Count == 0 && signalFrom < 0)
                            signalFrom = Math.Max(0, sampled - 6);
                        return ended;
                    }
                    return blasts.Count > 0 && time - lastEnd > Timing.QuietEnds ? End() : (WhistleSignal?)null;
            }
        }

        private double DipBelow => Math.Max(Quiet, Timing.Dip * peak);

        /// <summary>The loudness a new blast must climb above: well above the quietest since the last, and above <see cref="Sounds"/>.</summary>
        private double RiseTo(double before) => Math.Max(Sounds, floor + Math.Max(LeastRise, Rise * (before - floor)));

        /// <summary>A blip with no blast counted yet: no signal started, so its trace is dropped.</summary>
        private WhistleSignal? SignalUnstarted()
        {
            signalFrom = -1;
            floor = 1;
            return null;
        }

        private void Sample(double time, double level)
        {
            if (time < nextSample)
                return;
            // A steady tenth of a second, whatever the frame rate; after a long frame, from now.
            nextSample = nextSample + SampleEvery > time ? nextSample + SampleEvery : time + SampleEvery;
            samples[sampled % SampleCount] = (byte)Math.Round(Math.Max(0, Math.Min(1, level)) * 100);
            sampled++;
        }

        private enum Blast { Short, Long }

        private Blast Kind(double length) => length < Timing.ShortestLong ? Blast.Short : Blast.Long;

        private WhistleSignal End()
        {
            var kinds = blasts.ConvertAll(Kind);
            var words = new List<string>(blasts.Count);
            for (var i = 0; i < blasts.Count; i++)
                words.Add($"{(kinds[i] == Blast.Short ? "short" : "long")} {blasts[i]:F2} s");
            Heard = string.Join(", ", words);
            var from = Math.Max(signalFrom < 0 ? 0 : signalFrom, sampled - SampleCount);
            var trace = new StringBuilder((sampled - from) * 4);
            for (var i = from; i < sampled; i++)
                trace.Append(i > from ? " " : "").Append(samples[i % SampleCount]);
            Trace = trace.ToString();
            blasts.Clear();
            signalFrom = -1;
            lastPeak = 0;
            floor = 1;
            return Read(kinds);
        }

        private static WhistleSignal Read(List<Blast> b)
        {
            var shorts = b.TrueForAll(k => k == Blast.Short);
            var longs = b.TrueForAll(k => k == Blast.Long);
            if (b.Count == 1 && shorts)
                return WhistleSignal.Stop;
            if (b.Count == 2 && longs)
                return WhistleSignal.Go;
            if (b.Count == 2 && shorts)
                return WhistleSignal.Acknowledge;
            if (b.Count == 3 && shorts)
                return WhistleSignal.Back;
            return WhistleSignal.Unknown;
        }
    }
}
