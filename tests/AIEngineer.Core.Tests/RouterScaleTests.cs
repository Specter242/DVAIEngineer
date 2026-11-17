using System.Diagnostics;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using Xunit.Abstractions;

namespace AIEngineer.Core.Tests;

public class RouterScaleTests
{
    private readonly ITestOutputHelper output;

    public RouterScaleTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// A 20 km main line in 300 pieces, each joint a switch to a siding,
    /// every third siding with switches of its own to a short stub: about
    /// the size of the game's map. Routing end to end must stay cheap.
    /// </summary>
    [Fact]
    public void AMapSizedSearchIsCheap()
    {
        var b = new TrackGraphBuilder();
        const int pieces = 300;
        const double len = 66;
        var main = new int[pieces];
        for (var i = 0; i < pieces; i++)
            main[i] = b.AddTrack(len, new MapPoint(i * len, 0), new MapPoint((i + 1) * len, 0));
        for (var i = 0; i + 1 < pieces; i++)
        {
            var siding = b.AddTrack(250, new MapPoint((i + 1) * len, 0), new MapPoint((i + 1) * len + 240, 40));
            b.AddJunction(main[i], TrackEnd.End, new[] { (main[i + 1], TrackEnd.Start), (siding, TrackEnd.Start) }, new MapPoint((i + 1) * len, 0));
            if (i % 3 == 0)
            {
                var a = b.AddTrack(40, new MapPoint(0, 0), new MapPoint(40, 0));
                var c = b.AddTrack(40, new MapPoint(0, 0), new MapPoint(40, 10));
                b.AddJunction(siding, TrackEnd.End, new[] { (a, TrackEnd.Start), (c, TrackEnd.Start) }, new MapPoint(0, 0));
            }
        }
        var router = new Router(b.Build());
        var request = new RouteRequest
        {
            EndA = new TrackPosition(main[2], 10),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(main[2], 60),
            OutwardB = TrackDirection.Forward,
            TrainLength = 200,
            Goal = new TrackPosition(main[pieces - 3], 30),
        };
        router.Find(request);
        var watch = Stopwatch.StartNew();
        const int runs = 20;
        Route? route = null;
        for (var r = 0; r < runs; r++)
            route = router.Find(request);
        var ms = watch.Elapsed.TotalMilliseconds / runs;
        output.WriteLine($"end to end: {ms:F2} ms per search, {route!.Length:F0} m");
        Assert.Equal(0, route.Reversals);
        Assert.True(ms < 50, $"{ms:F1} ms");

        // Tracks 305-307: the siding off the fourth switch and its two stubs.
        request.Goal = new TrackPosition(307, 20);
        route = router.Find(request);
        Assert.NotNull(route);
        Assert.Equal(0, route!.Reversals);

        // From one stub to the other: out onto the siding, clear its switch, and back in.
        var shunt = new RouteRequest
        {
            EndA = new TrackPosition(306, 5),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(306, 35),
            OutwardB = TrackDirection.Forward,
            TrainLength = 30,
            Goal = new TrackPosition(307, 25),
        };
        watch.Restart();
        route = router.Find(shunt);
        output.WriteLine($"switchback: {watch.Elapsed.TotalMilliseconds:F2} ms, {route?.Reversals} reversals");
        Assert.NotNull(route);
        Assert.Equal(1, route!.Reversals);
    }
}
