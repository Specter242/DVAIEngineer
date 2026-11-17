using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class WalkerAndOccupancyTests
{
    private static (TrackGraph Graph, int Junction) Wye()
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(300, 0));
        var straight = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(-300, 0));
        var diverging = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(-300, -30));
        var j = b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start), (diverging, TrackEnd.Start) }, new MapPoint(0, 0));
        return (b.Build(), j);
    }

    [Fact]
    public void WalkFollowsTheSwitchAsSet()
    {
        var (g, j) = Wye();
        var leg = TrackWalker.Walk(g, new TrackPosition(0, 100), TrackDirection.Backward, 1000, _ => 1, ConsistEnd.A, out var end);
        Assert.Equal(WalkEnd.DeadEnd, end);
        Assert.Equal(400, leg.Length, 6);
        Assert.Equal(2, leg.Pieces[1].Track);
        Assert.Equal(new SwitchSetting(j, 1, 100).ToString(), Assert.Single(leg.Switches).ToString());
    }

    [Fact]
    public void WalkStopsAtATrailingSwitchSetAgainstIt()
    {
        var (g, _) = Wye();
        var leg = TrackWalker.Walk(g, new TrackPosition(1, 100), TrackDirection.Backward, 1000, _ => 1, ConsistEnd.A, out var end);
        Assert.Equal(WalkEnd.SwitchAgainst, end);
        Assert.Equal(100, leg.Length, 6);
        leg = TrackWalker.Walk(g, new TrackPosition(1, 100), TrackDirection.Backward, 50, _ => 1, ConsistEnd.A, out end);
        Assert.Equal(WalkEnd.Distance, end);
        Assert.Equal(50, leg.Length, 6);
    }

    [Fact]
    public void FirstCarAheadIsFoundAcrossTracksAndOwnCarsIgnored()
    {
        var (g, _) = Wye();
        var leg = TrackWalker.Walk(g, new TrackPosition(0, 100), TrackDirection.Backward, 1000, _ => 0, ConsistEnd.A, out _);
        var index = new OccupancyIndex();
        index.Add(0, new OccupiedSpan(100, 160, owner: 7));
        index.Add(1, new OccupiedSpan(50, 64, owner: 3));
        Assert.Equal(150, index.FirstAhead(leg, 0, 0, 1000, ignoreOwner: 7, out var owner), 6);
        Assert.Equal(3, owner);
        Assert.Equal(double.PositiveInfinity, index.FirstAhead(leg, 0, 0, 100, ignoreOwner: 7, out _));
        // A car the leading end touches is in the way at once.
        Assert.Equal(155, index.FirstAhead(leg, 155, 1, 1000, ignoreOwner: 7, out _), 6);
        index.Clear();
        Assert.Equal(double.PositiveInfinity, index.FirstAhead(leg, 0, 0, 1000, ignoreOwner: 7, out _));
    }

    [Fact]
    public void AdvanceCrossesOntoTheNextTrackAndStopsAtADeadEnd()
    {
        var (g, _) = Wye();
        // From the stem toward the switch, 30 m past it onto the set branch, entered at its start.
        var p = TrackWalker.Advance(g, new TrackPosition(0, 10), TrackDirection.Backward, 40, _ => 1, out var dir);
        Assert.Equal(new TrackPosition(2, 30), p);
        Assert.Equal(TrackDirection.Forward, dir);
        // Off the far end of a branch: held at the buffer stop.
        p = TrackWalker.Advance(g, new TrackPosition(2, 290), TrackDirection.Forward, 40, _ => 1, out dir);
        Assert.Equal(new TrackPosition(2, 300), p);
    }

    [Fact]
    public void AWalkFromATrackEndStillHoldsItsStart()
    {
        var (g, _) = Wye();
        // Right at the stem's switch end, walking onto the branch.
        var leg = TrackWalker.Walk(g, new TrackPosition(0, 0), TrackDirection.Backward, 50, _ => 0, ConsistEnd.A, out _);
        Assert.True(leg.TryLocate(new TrackPosition(0, 0), 0, 0.5, out var d, out _));
        Assert.Equal(0, d, 6);
        Assert.Equal(50, leg.Length, 6);
    }
}
