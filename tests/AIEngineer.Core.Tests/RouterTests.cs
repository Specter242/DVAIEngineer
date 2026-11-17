using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

public class RouterTests
{
    /// <summary>
    /// A switch at the origin: its stem (track 0) runs east, branch 0
    /// (track 1) west, branch 1 (track 2) west and a little south. Every
    /// track starts at the switch.
    /// </summary>
    private static (TrackGraph Graph, int Junction) Wye(double stemLength = 300, double branchLength = 300)
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(stemLength, new MapPoint(0, 0), new MapPoint(stemLength, 0));
        var straight = b.AddTrack(branchLength, new MapPoint(0, 0), new MapPoint(-branchLength, 0));
        var diverging = b.AddTrack(branchLength, new MapPoint(0, 0), new MapPoint(-branchLength, -30));
        var j = b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start), (diverging, TrackEnd.Start) }, new MapPoint(0, 0));
        return (b.Build(), j);
    }

    /// <summary>A consist on one track between two offsets, end A at the lower offset.</summary>
    private static RouteRequest On(int track, double a, double bOffset, TrackPosition goal) => new RouteRequest
    {
        EndA = new TrackPosition(track, a),
        OutwardA = TrackDirection.Backward,
        EndB = new TrackPosition(track, bOffset),
        OutwardB = TrackDirection.Forward,
        TrainLength = bOffset - a,
        Goal = goal,
    };

    [Fact]
    public void GoalAheadOnTheSameTrackIsOnePiece()
    {
        var (g, _) = Wye();
        var route = new Router(g).Find(On(0, 100, 150, new TrackPosition(0, 250)));
        Assert.NotNull(route);
        var leg = Assert.Single(route.Legs);
        Assert.Equal(ConsistEnd.B, leg.Leader);
        Assert.Equal(100, leg.Length, 6);
        Assert.Empty(leg.Switches);
    }

    [Fact]
    public void FacingMoveSetsTheSwitchForTheBranch()
    {
        var (g, j) = Wye();
        var route = new Router(g).Find(On(0, 100, 150, new TrackPosition(2, 50)));
        var leg = Assert.Single(route!.Legs);
        Assert.Equal(ConsistEnd.A, leg.Leader);
        Assert.Equal(150, leg.Length, 6);
        var sw = Assert.Single(leg.Switches);
        Assert.Equal(j, sw.Junction);
        Assert.Equal(1, sw.Branch);
        Assert.Equal(100, sw.AtDistance, 6);
    }

    [Fact]
    public void BranchToBranchIsASwitchbackOnTheStem()
    {
        var (g, j) = Wye();
        var router = new Router(g);
        var route = router.Find(On(1, 100, 150, new TrackPosition(2, 200)), out var failure);
        Assert.Equal(RouteFailure.None, failure);
        Assert.Equal(2, route!.Legs.Count);

        var pull = route.Legs[0];
        Assert.Equal(ConsistEnd.A, pull.Leader);
        Assert.True(pull.EndsInReversal);
        // 100 m to the switch, then the train's 50 m and 15 m clear of the points.
        Assert.Equal(165, pull.Length, 6);
        Assert.Equal(new SwitchSetting(j, 0, 100).ToString(), Assert.Single(pull.Switches).ToString());

        var shove = route.Legs[1];
        Assert.Equal(ConsistEnd.B, shove.Leader);
        Assert.False(shove.EndsInReversal);
        Assert.Equal(65 + 200, shove.Length, 6);
        var sw = Assert.Single(shove.Switches);
        Assert.Equal(1, sw.Branch);
        Assert.Equal(65, sw.AtDistance, 6);
    }

    [Fact]
    public void ASwitchbackNeedsRoomForTheWholeTrain()
    {
        var (g, _) = Wye(stemLength: 40);
        var route = new Router(g).Find(On(1, 100, 150, new TrackPosition(2, 200)), out var failure);
        Assert.Null(route);
        Assert.Equal(RouteFailure.NoPath, failure);
    }

    [Fact]
    public void APullForwardRunsOnPastAShortTrack()
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(30, new MapPoint(0, 0), new MapPoint(30, 0));
        var beyond = b.AddTrack(100, new MapPoint(30, 0), new MapPoint(130, 0));
        var straight = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(-300, 0));
        var diverging = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(-300, -30));
        b.Join(stem, TrackEnd.End, beyond, TrackEnd.Start);
        b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start), (diverging, TrackEnd.Start) }, new MapPoint(0, 0));
        var route = new Router(b.Build()).Find(On(straight, 100, 150, new TrackPosition(diverging, 200)));
        Assert.NotNull(route);
        var pull = route!.Legs[0];
        Assert.Equal(3, pull.Pieces.Count);
        Assert.Equal(new RoutePiece(beyond, 0, 35).ToString(), pull.Pieces[2].ToString());
        Assert.Equal(new TrackPosition(stem, 1), route.Legs[1].PositionAt(64));
    }

    [Fact]
    public void ALockedSwitchIsNotUsed()
    {
        var (g, j) = Wye();
        var request = On(0, 100, 150, new TrackPosition(2, 50));
        request.CanSetJunction = (junction, branch) => !(junction == j && branch == 1);
        Assert.Null(new Router(g).Find(request, out var failure));
        Assert.Equal(RouteFailure.NoPath, failure);
    }

    [Fact]
    public void AGoalUnderTheTrainIsReported()
    {
        var (g, _) = Wye(stemLength: 200);
        var request = On(0, 100, 150, new TrackPosition(0, 120));
        request.AllowReversals = false;
        request.AllowBackOnto = false;
        Assert.Null(new Router(g).Find(request, out var failure));
        Assert.Equal(RouteFailure.GoalUnderTrain, failure);
    }

    [Fact]
    public void AGoalUnderTheTrainIsBackedOnto()
    {
        // The train stands from 100 to 150 on the stem; the goal is at 120, under it. Going out
        // through the switch and back to reach it would be 350 m; end A is 20 m beyond it, so
        // end B leads 20 m and A stands on it.
        var (g, _) = Wye(stemLength: 400);
        var route = new Router(g).Find(On(0, 100, 150, new TrackPosition(0, 120)), out var failure);
        Assert.Equal(RouteFailure.None, failure);
        var leg = Assert.Single(route.Legs);
        Assert.Equal(ConsistEnd.B, leg.Leader);
        Assert.Equal(20, leg.Length, 3);
        Assert.Equal(150, leg.Pieces[0].From, 3);
        Assert.Equal(170, leg.Pieces[0].To, 3);
    }

    [Fact]
    public void TheShorterOfTwoWaysRoundALoopWins()
    {
        // Two parallel tracks between a switch at each end: one 400 m, one 250 m.
        var b = new TrackGraphBuilder();
        var west = b.AddTrack(100, new MapPoint(-100, 0), new MapPoint(0, 0));
        var longWay = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(200, 0));
        var shortWay = b.AddTrack(250, new MapPoint(0, 0), new MapPoint(200, 0));
        var east = b.AddTrack(100, new MapPoint(200, 0), new MapPoint(300, 0));
        b.AddJunction(west, TrackEnd.End, new[] { (longWay, TrackEnd.Start), (shortWay, TrackEnd.Start) }, new MapPoint(0, 0));
        b.AddJunction(east, TrackEnd.Start, new[] { (longWay, TrackEnd.End), (shortWay, TrackEnd.End) }, new MapPoint(200, 0));
        var request = new RouteRequest
        {
            EndA = new TrackPosition(west, 0),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(west, 50),
            OutwardB = TrackDirection.Forward,
            TrainLength = 50,
            Goal = new TrackPosition(east, 50),
        };
        var leg = Assert.Single(new Router(b.Build()).Find(request)!.Legs);
        Assert.Equal(50 + 250 + 50, leg.Length, 6);
        Assert.All(leg.Switches, s => Assert.Equal(1, s.Branch));
        Assert.Equal(2, leg.Switches.Count);
    }

    [Fact]
    public void StandingCarsSendItTheLongWayRound()
    {
        // The loop above, with cars standing on the short way: the reported
        // run of 2026-11-04 planned straight through a cut to reach its far end.
        var b = new TrackGraphBuilder();
        var west = b.AddTrack(100, new MapPoint(-100, 0), new MapPoint(0, 0));
        var longWay = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(200, 0));
        var shortWay = b.AddTrack(250, new MapPoint(0, 0), new MapPoint(200, 0));
        var east = b.AddTrack(100, new MapPoint(200, 0), new MapPoint(300, 0));
        b.AddJunction(west, TrackEnd.End, new[] { (longWay, TrackEnd.Start), (shortWay, TrackEnd.Start) }, new MapPoint(0, 0));
        b.AddJunction(east, TrackEnd.Start, new[] { (longWay, TrackEnd.End), (shortWay, TrackEnd.End) }, new MapPoint(200, 0));
        var occupancy = new OccupancyIndex();
        occupancy.Add(shortWay, new OccupiedSpan(100, 140, owner: 9));
        var request = new RouteRequest
        {
            EndA = new TrackPosition(west, 0),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(west, 50),
            OutwardB = TrackDirection.Forward,
            TrainLength = 50,
            Goal = new TrackPosition(east, 50),
            Blocked = (t, from, to) => occupancy.IsStanding(t, from, to, ignoreOwner: 1),
        };
        var router = new Router(b.Build());
        var leg = Assert.Single(router.Find(request)!.Legs);
        Assert.Equal(50 + 400 + 50, leg.Length, 6);
        Assert.All(leg.Switches, s => Assert.Equal(0, s.Branch));
        // A goal on the short way beyond the cars: from the far end, not through them.
        request.Goal = new TrackPosition(shortWay, 200);
        var route = router.Find(request)!;
        Assert.DoesNotContain(route.Legs[0].Pieces, p => p.Track == shortWay && Math.Min(p.From, p.To) < 140);
        // A goal among the cars (a coupling clicked on them) is approached as before.
        request.Goal = new TrackPosition(shortWay, 120);
        Assert.Equal(50 + 120, Assert.Single(router.Find(request)!.Legs).Length, 6);
    }

    [Fact]
    public void LocatingFindsTheLeadingEndAlongALeg()
    {
        var (g, _) = Wye();
        var leg = new Router(g).Find(On(0, 100, 150, new TrackPosition(2, 50)))!.Legs[0];
        Assert.True(leg.TryLocate(new TrackPosition(0, 40), 0, 0.5, out var d, out var piece));
        Assert.Equal(60, d, 6);
        Assert.Equal(0, piece);
        Assert.True(leg.TryLocate(new TrackPosition(2, 20), 0, 0.5, out d, out piece));
        Assert.Equal(120, d, 6);
        Assert.Equal(1, piece);
        Assert.False(leg.TryLocate(new TrackPosition(1, 20), 0, 0.5, out _, out _));
    }

    [Fact]
    public void ALeadingEndRightAtATrackEndIsStillOnTheRoute()
    {
        var (g, _) = Wye();
        // End A exactly at the switch end of the stem.
        var leg = new Router(g).Find(On(0, 0, 50, new TrackPosition(2, 100)))!.Legs[0];
        Assert.True(leg.TryLocate(new TrackPosition(0, 0), 0, 0.5, out var d, out _));
        Assert.Equal(0, d, 6);
        Assert.Equal(100, leg.Length, 6);
    }
}
