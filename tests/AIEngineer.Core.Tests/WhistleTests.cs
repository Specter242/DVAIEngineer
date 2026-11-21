using System.Globalization;
using AIEngineer.Core.Driving;

namespace AIEngineer.Core.Tests;

/// <summary>The helper's ear: the rulebook's whistle signals from the whistle sounding and falling quiet.</summary>
public class WhistleTests
{
    private const double Frame = 0.02;

    /// <summary>Feeds a whistle level at 50 frames a second for <paramref name="seconds"/>; every signal read, and when.</summary>
    private sealed class Ear
    {
        public readonly WhistleReader Reader = new WhistleReader();
        public readonly List<(WhistleSignal Signal, string Heard, double Time)> Signals = new();
        public double T;
        private double planned;

        // Frames to the planned end of each stretch, so many short stretches keep to time.
        public Ear Run(double seconds, Func<double, double> level)
        {
            var from = planned;
            planned += seconds;
            for (; T < planned - 1e-9; T += Frame)
            {
                var s = Reader.Feed(T, level(T - from));
                if (s.HasValue)
                    Signals.Add((s.Value, Reader.Heard, T));
            }
            return this;
        }

        public Ear Blow(double on, double off = 0.6) => Run(on, _ => 1).Run(off, _ => 0);

        public Ear Quiet(double seconds = 3) => Run(seconds, _ => 0);
    }

    private static List<(WhistleSignal Signal, string Heard, double Time)> Blow(params (double On, double Off)[] blasts)
    {
        var ear = new Ear();
        foreach (var (on, off) in blasts)
            ear.Blow(on, off);
        return ear.Quiet().Signals;
    }

    [Fact]
    public void TwoLongIsGoOneShortIsStopThreeShortIsBack()
    {
        Assert.Equal(WhistleSignal.Go, Blow((2.4, 0.8), (2.4, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Stop, Blow((0.7, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Back, Blow((0.6, 0.5), (0.6, 0.5), (0.6, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Acknowledge, Blow((0.6, 0.5), (0.6, 0.5)).Single().Signal);
        var odd = Blow((2.4, 0.6), (0.7, 0.5)).Single();
        Assert.Equal(WhistleSignal.Unknown, odd.Signal);
        Assert.Equal("long 2.40 s, short 0.70 s", odd.Heard);
    }

    [Fact]
    public void TheOwnersTimingsHoldAtTheirEdges()
    {
        // A long is a second and a half or more; anything shorter (and no blip) is short.
        Assert.Equal(WhistleSignal.Stop, Blow((0.5, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Stop, Blow((1.0, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Stop, Blow((1.4, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Go, Blow((1.6, 0.7), (3.0, 0.5)).Single().Signal);
        var one = Blow((1.6, 0.5)).Single();
        Assert.Equal((WhistleSignal.Unknown, "long 1.60 s"), (one.Signal, one.Heard));
        Assert.Equal(WhistleSignal.Unknown, Blow((2.4, 0.6), (1.4, 0.5)).Single().Signal);
    }

    [Fact]
    public void ABlipIsNotRegistered()
    {
        Assert.Empty(Blow((0.2, 0.5)));
        Assert.Empty(Blow((0.3, 0.5), (0.1, 0.4)));
        // A blip before a short, or between two longs, changes nothing.
        Assert.Equal(WhistleSignal.Stop, Blow((0.2, 0.4), (0.7, 0.5)).Single().Signal);
        Assert.Equal(WhistleSignal.Go, Blow((2.4, 0.4), (0.15, 0.4), (2.4, 0.5)).Single().Signal);
    }

    [Fact]
    public void ACordSpringingBackIsNoBlast()
    {
        // Let go, the cord springs back: the whistle sounds again a third of a second after, nearly full, for a fifth.
        var ear = new Ear().Run(1.0, _ => 1).Run(0.3, _ => 0).Run(0.2, _ => 0.9).Quiet();
        var stop = ear.Signals.Single();
        Assert.Equal((WhistleSignal.Stop, "short 1.00 s"), (stop.Signal, stop.Heard));
        // Sooner, inside the debounce: cut off the blast, not added to it (a short is not made long).
        ear = new Ear().Run(1.2, _ => 1).Run(0.2, _ => 0).Run(0.3, _ => 0.9).Quiet();
        stop = ear.Signals.Single();
        Assert.Equal((WhistleSignal.Stop, "short 1.20 s"), (stop.Signal, stop.Heard));
        // Between two long, and after the second: still two long.
        ear = new Ear().Run(1.8, _ => 1).Run(0.3, _ => 0).Run(0.25, _ => 0.9).Run(0.5, _ => 0).Run(1.8, _ => 1).Run(0.3, _ => 0).Run(0.2, _ => 0.85).Quiet();
        Assert.Equal(WhistleSignal.Go, ear.Signals.Single().Signal);
    }

    [Theory]
    [InlineData("0 0 0 0 0 0 60 95 99 100 100 100 100 100 100 100 100 100 100 100 100 86 0 0 0 69 96 61 0 0 76 98 100 100 100 100 100 100 100 100 100 100 100 100 100 19 0 0 0 80 87 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0", "Go", 2)]
    [InlineData("0 0 0 0 0 0 62 96 99 100 100 100 100 100 100 100 91 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0", "Stop", 1)]
    [InlineData("0 0 0 0 0 0 77 97 100 100 100 100 100 100 100 100 100 100 100 100 2 0 0 4 93 71 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0", "Unknown", 1)]
    [InlineData("0 0 0 0 0 30 93 99 100 100 100 100 100 100 100 100 100 95 22 0 0 0 0 0 0 24 94 99 100 100 100 100 100 100 100 100 100 100 100 100 100 1 35 0 68 85 0 0 0 0 29 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0", "Unknown", 2)]
    public void TheOwnersPullsAreReadWithTheirReboundsLeftOut(string trace, string signal, int blasts)
    {
        var ear = new Ear();
        foreach (var sample in trace.Split(' ').Select(int.Parse))
            ear.Run(0.113, _ => sample / 100.0);
        ear.Quiet();
        var heard = ear.Signals.Single();
        Assert.Equal(Enum.Parse<WhistleSignal>(signal), heard.Signal);
        Assert.Equal(blasts, heard.Heard.Split(", ").Length);
    }

    [Fact]
    public void ABouncingCordIsOneBlast()
    {
        // A long pull whose pole bounces: the whistle drops out for a tenth of a second every half second.
        static double Bouncing(double t) => t % 0.5 < 0.1 && t > 0.3 ? 0 : 1;
        var ear = new Ear().Run(2.4, Bouncing).Quiet(0.6).Run(2.4, Bouncing).Quiet();
        var go = ear.Signals.Single();
        Assert.Equal(WhistleSignal.Go, go.Signal);
        // Held to the end: its length whole, not cut at its last flicker.
        Assert.Equal("long 2.40 s, long 2.40 s", go.Heard);
        ear = new Ear().Run(0.7, Bouncing).Quiet();
        Assert.Equal((WhistleSignal.Stop, "short 0.70 s"), (ear.Signals.Single().Signal, ear.Signals.Single().Heard));
    }

    [Fact]
    public void ACordEasedButNotLetGoEndsABlast()
    {
        // Two long, the cord eased to a sigh between them (30% and 40% of the blast), not let go: two long, not one.
        foreach (var between in new[] { 0.3, 0.4 })
        {
            var ear = new Ear().Run(2.3, _ => 1).Run(0.6, _ => between).Run(2.3, _ => 1).Quiet();
            var go = ear.Signals.Single();
            Assert.Equal(WhistleSignal.Go, go.Signal);
            Assert.StartsWith("long 2.30 s, long 2.30 s", go.Heard);
        }
        // Three short, eased to a third between: back.
        var back = new Ear().Run(0.7, _ => 1).Run(0.5, _ => 0.35).Run(0.7, _ => 1).Run(0.5, _ => 0.35).Run(0.7, _ => 1).Quiet();
        Assert.Equal(WhistleSignal.Back, back.Signals.Single().Signal);
        // A quiet whistle (little steam, half as loud): the same, by its own loudness.
        var quiet = new Ear().Run(2.3, _ => 0.5).Run(0.6, _ => 0.15).Run(2.3, _ => 0.5).Quiet();
        Assert.Equal(WhistleSignal.Go, quiet.Signals.Single().Signal);
    }

    [Fact]
    public void AQuilledBlastIsOneBlast()
    {
        // A long quilled: its loudness swelling between 65% and full, a second and a half a swell.
        static double Quill(double t) => 0.825 + 0.175 * Math.Cos(t * 2 * Math.PI / 0.65);
        var ear = new Ear().Run(2.5, Quill).Run(0.7, _ => 0.25).Run(2.5, Quill).Quiet();
        Assert.Equal(WhistleSignal.Go, ear.Signals.Single().Signal);
        // A quill dipping deep (to 30%) for a moment, shorter than a gap: still one long.
        ear = new Ear().Run(1.2, _ => 1).Run(0.2, _ => 0.3).Run(1.2, _ => 1).Run(0.7, _ => 0).Run(2.4, _ => 1).Quiet();
        Assert.Equal(WhistleSignal.Go, ear.Signals.Single().Signal);
    }

    [Fact]
    public void AWhistleHeldLowAfterASignalEndsIt()
    {
        // One short, then the cord held at a sigh: the stop comes a second and a half after the short, and the sigh starts nothing.
        var ear = new Ear().Run(0.7, _ => 1).Run(4, _ => 0.3);
        var stop = ear.Signals.Single();
        Assert.Equal(WhistleSignal.Stop, stop.Signal);
        Assert.InRange(stop.Time, 0.7 + WhistleTiming.Default.QuietEnds, 0.7 + WhistleTiming.Default.QuietEnds + 0.1);
        // From the sigh, a full pull is a new blast.
        ear.Run(0.7, _ => 1).Run(0.5, _ => 0.3).Quiet();
        Assert.Equal(new[] { WhistleSignal.Stop, WhistleSignal.Stop }, ear.Signals.Select(s => s.Signal));
    }

    [Fact]
    public void EachSignalLeavesATraceOfItsLoudness()
    {
        var ear = new Ear().Quiet(1).Run(0.7, _ => 1).Quiet();
        Assert.Equal(WhistleSignal.Stop, ear.Signals.Single().Signal);
        var trace = ear.Reader.Trace.Split(' ').Select(int.Parse).ToList();
        Assert.Equal(0, trace[0]);
        Assert.Contains(100, trace);
        Assert.InRange(trace.Count, 25, 45);
    }

    [Fact]
    public void APoleRingingAfterItsLetGoAddsNoBlast()
    {
        // One short, then the pole rings down: peaks a third of a second apart, fading from 0.6.
        static double Ringing(double t) => 0.6 * Math.Exp(-t / 0.4) * Math.Abs(Math.Cos(Math.PI * 3 * t));
        var ear = new Ear().Blow(0.7, 0).Run(1.5, Ringing).Quiet();
        var heard = ear.Signals.Single();
        Assert.Equal(WhistleSignal.Stop, heard.Signal);
        // A bump at rest well after it (the pole knocked) is a blip.
        ear = new Ear().Blow(0.7, 1.0).Run(0.25, _ => 0.5).Quiet();
        Assert.Equal(WhistleSignal.Stop, ear.Signals.Single().Signal);
    }

    [Fact]
    public void QuietEndsASignalAndABlipDoesNotHoldItOpen()
    {
        // One short, a pause, then one short again: two stops, not two short.
        var heard = Blow((0.7, 2.6), (0.7, 0.5));
        Assert.Equal(new[] { WhistleSignal.Stop, WhistleSignal.Stop }, heard.Select(h => h.Signal));
        // A short ending at 0.7 s, a blip at 1.7 s: the stop still comes a second and a half after the short.
        var ear = new Ear().Blow(0.7, 1.0).Run(0.2, _ => 1).Quiet();
        var stop = ear.Signals.Single();
        Assert.Equal(WhistleSignal.Stop, stop.Signal);
        Assert.InRange(stop.Time, 0.7 + WhistleTiming.Default.QuietEnds, 0.7 + WhistleTiming.Default.QuietEnds + 0.1);
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

    [Fact]
    public void TimingsAreKeptInOrder()
    {
        var t = new WhistleTiming { Bounce = 2, Shortest = 0.5, ShortestLong = 0.2, QuietEnds = 0, Dip = 3 }.Sane();
        Assert.True(t.Bounce < t.Shortest);
        Assert.True(t.Shortest <= t.ShortestLong);
        Assert.True(t.QuietEnds > t.Bounce);
        Assert.InRange(t.Dip, 0.1, 0.9);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void HeardAndTraceStayInvariantUnderNonEnglishCultures(string cultureName)
    {
        WithCulture(CultureInfo.GetCultureInfo(cultureName), () =>
        {
            var ear = new Ear().Quiet(0.5).Blow(2.4, 0.6).Blow(0.7, 0.5).Quiet();
            var odd = ear.Signals.Single();
            Assert.Equal(WhistleSignal.Unknown, odd.Signal);
            Assert.Equal("long 2.40 s, short 0.70 s", odd.Heard);
            var trace = ear.Reader.Trace.Split(' ').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(0, trace[0]);
            Assert.Contains(100, trace);
        });
    }
}
