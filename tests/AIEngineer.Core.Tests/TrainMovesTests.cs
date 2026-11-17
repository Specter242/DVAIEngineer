using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>
/// The planner's estimate of running that knows how a train must move: from
/// one branch of a switch to the other only by running past it onto free track
/// and turning back, which a long train can't do in a short headshunt, and
/// with a lead that only turning changes.
/// </summary>
public class TrainMovesTests
{
    // A 30 m headshunt (0), ending in a buffer at its Start; at its End a switch to two
    // 300 m stubs, B1 (1) and B2 (2), each entered at its Start.
    private const int Head = 0, B1 = 1, B2 = 2;

    private static TrackGraph Network(double head = 30)
    {
        var b = new TrackGraphBuilder();
        var h = b.AddTrack(head, new MapPoint(-head, 0), new MapPoint(0, 0));
        var b1 = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(300, 0));
        var b2 = b.AddTrack(300, new MapPoint(0, 0), new MapPoint(300, 40));
        b.AddJunction(h, TrackEnd.End, new[] { (b1, TrackEnd.Start), (b2, TrackEnd.Start) }, new MapPoint(0, 0));
        return b.Build();
    }

    [Fact]
    public void FromBranchToBranchItTurnsInTheHeadshuntIfItFits()
    {
        var g = Network();
        // A light engine on B1: out past the switch into the headshunt, back, into B2.
        var light = TrainMoves.From(g, new TrainPose(new TrackPosition(B1, 150), null), 15);
        // The pull past the switch (its length and the clearance) and the clearance back to the points.
        Assert.Equal(150 + 15 + 2 * TrainMoves.Clearance + TrainMoves.ReversalPenalty + 150,
            light.To(new TrackPosition(B2, 150), TrackDirection.Forward, null), 6);
        // The undirected estimate takes the V for nothing.
        Assert.Equal(300, GraphDistances.From(g, new TrackPosition(B1, 150)).To(new TrackPosition(B2, 150)), 6);
        // A train of 100 m can't clear the switch in 30 m: no way there.
        var train = TrainMoves.From(g, new TrainPose(new TrackPosition(B1, 150), TrackDirection.Backward), 100);
        Assert.True(double.IsPositiveInfinity(train.To(new TrackPosition(B2, 150), TrackDirection.Forward, null)));
        // With a long headshunt it can.
        var roomy = TrainMoves.From(Network(head: 200), new TrainPose(new TrackPosition(B1, 150), TrackDirection.Backward), 100);
        Assert.False(double.IsPositiveInfinity(roomy.To(new TrackPosition(B2, 150), TrackDirection.Forward, null)));
    }

    [Fact]
    public void TheLeadChangesOnlyByTurning()
    {
        var g = Network(head: 200);
        // Cars toward the switch: it shoves them out, turns in the headshunt, and so the loco leads into B2.
        var moves = TrainMoves.From(g, new TrainPose(new TrackPosition(B1, 150), TrackDirection.Backward), 60);
        Assert.False(double.IsPositiveInfinity(moves.To(new TrackPosition(B2, 150), TrackDirection.Forward, Lead.Loco)));
        // Cars first into B2 needs the train turned round: there's no wye.
        Assert.True(double.IsPositiveInfinity(moves.To(new TrackPosition(B2, 150), TrackDirection.Forward, Lead.Cars)));
        // Back along B1 toward the switch, cars first, is straight ahead.
        Assert.Equal(100, moves.To(new TrackPosition(B1, 50), TrackDirection.Backward, Lead.Cars), 6);
    }

    [Fact]
    public void CarsStandingInTheHeadshuntLeaveNoRoomToTurn()
    {
        var g = Network(head: 200);
        // A cut 40 m in from the switch end of the headshunt (200 m, its switch end at 200).
        bool Standing(int track, double from, double to) => track == Head && to > 140 && from < 160;
        var light = TrainMoves.From(g, new TrainPose(new TrackPosition(B1, 150), null), 15, Standing);
        Assert.False(double.IsPositiveInfinity(light.To(new TrackPosition(B2, 150), TrackDirection.Forward, null)));
        var train = TrainMoves.From(g, new TrainPose(new TrackPosition(B1, 150), TrackDirection.Backward), 60, Standing);
        Assert.True(double.IsPositiveInfinity(train.To(new TrackPosition(B2, 150), TrackDirection.Forward, null)));
    }

    [Fact]
    public void ItDoesntRunThroughCarsStandingOnATrack()
    {
        var g = Network(head: 200);
        // Cars at 100-120 m on B2: B2 is reached up to them, not beyond.
        bool Standing(int track, double from, double to) => track == B2 && to > 100 && from < 120;
        var light = TrainMoves.From(g, new TrainPose(new TrackPosition(B1, 150), null), 15, Standing);
        Assert.False(double.IsPositiveInfinity(light.To(new TrackPosition(B2, 100), TrackDirection.Forward, null)));
        Assert.True(double.IsPositiveInfinity(light.To(new TrackPosition(B2, 200), TrackDirection.Forward, null)));
        // Standing on B2 beyond them, it can't get out past them.
        var beyond = TrainMoves.From(g, new TrainPose(new TrackPosition(B2, 200), null), 15, Standing);
        Assert.True(double.IsPositiveInfinity(beyond.To(new TrackPosition(B1, 150), TrackDirection.Forward, null)));
    }

    [Fact]
    public void RoomToTurnIsFoundOverManyShortTracks()
    {
        // As in game: the lead beyond the switch is a string of 24 m pieces. A 200 m train
        // needs nine of them to turn; the room was looked for only four tracks deep.
        var b = new TrackGraphBuilder();
        var first = b.AddTrack(24, new MapPoint(-24, 0), new MapPoint(0, 0));
        var b1 = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 0));
        var b2 = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 40));
        b.AddJunction(first, TrackEnd.End, new[] { (b1, TrackEnd.Start), (b2, TrackEnd.Start) }, new MapPoint(0, 0));
        var last = first;
        for (var i = 1; i < 14; i++)
        {
            var next = b.AddTrack(24, new MapPoint(-24 * (i + 1), 0), new MapPoint(-24 * i, 0));
            b.Join(next, TrackEnd.End, last, TrackEnd.Start);
            last = next;
        }
        var g = b.Build();
        var train = TrainMoves.From(g, new TrainPose(new TrackPosition(b1, 300), TrackDirection.Forward), 200);
        var cost = train.To(new TrackPosition(b2, 300), TrackDirection.Forward, Lead.Cars);
        Assert.Equal(300 + 200 + 2 * TrainMoves.Clearance + TrainMoves.ReversalPenalty + 300, cost, 6);
        // And the router agrees it can be done.
        var route = new Router(g).Find(new RouteRequest
        {
            EndA = new TrackPosition(b1, 300),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(b1, 500),
            OutwardB = TrackDirection.Forward,
            TrainLength = 200,
            Goal = new TrackPosition(b2, 300),
        });
        Assert.NotNull(route);
        Assert.Equal(1, route.Reversals);
    }
}
