using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>
/// Jobs handed to an engineer: which end of each cut it takes, the order
/// it fetches them in, the set-outs that order forces, the room it finds on
/// a track, and the orders it carries out on the way.
/// </summary>
public class JobTests
{
    // A yard and a station 3 km apart. The yard: a lead (0) whose far end splits into
    // two stubs, Y1 (1) and Y2 (2), each entered at its Start and ending in a buffer.
    // The main (3) runs west from the lead's Start to the station, where it splits into
    // the stubs D1 (4) and D2 (5).
    private const int Lead = 0, Y1 = 1, Y2 = 2, Main = 3, D1 = 4, D2 = 5;

    private static TrackGraph Network()
    {
        var b = new TrackGraphBuilder();
        var lead = b.AddTrack(400, new MapPoint(0, 0), new MapPoint(400, 0));
        var y1 = b.AddTrack(300, new MapPoint(400, 0), new MapPoint(700, 0));
        var y2 = b.AddTrack(300, new MapPoint(400, 0), new MapPoint(700, 40));
        b.AddJunction(lead, TrackEnd.End, new[] { (y1, TrackEnd.Start), (y2, TrackEnd.Start) }, new MapPoint(400, 0));
        var main = b.AddTrack(3000, new MapPoint(0, 0), new MapPoint(-3000, 0));
        b.Join(lead, TrackEnd.Start, main, TrackEnd.Start);
        var d1 = b.AddTrack(300, new MapPoint(-3000, 0), new MapPoint(-3300, 0));
        var d2 = b.AddTrack(300, new MapPoint(-3000, 0), new MapPoint(-3300, -40));
        b.AddJunction(main, TrackEnd.End, new[] { (d1, TrackEnd.Start), (d2, TrackEnd.Start) }, new MapPoint(-3000, 0));
        return b.Build();
    }

    /// <summary>A cut on a stub from <paramref name="from"/> to <paramref name="to"/> (end A toward the entry), its cars bound as given.</summary>
    private static JobCut Cut(string label, int track, double from, double to, params int[] destinations) => new()
    {
        Label = label,
        // End A toward the entry (lower offsets), facing out that way; end B toward the buffer.
        Ends = new[]
        {
            new CutEnd(new TrackPosition(track, from), TrackDirection.Backward),
            new CutEnd(new TrackPosition(track, to), TrackDirection.Forward),
        },
        Destinations = destinations,
        Home = track,
        // The buffer end of a stub is never reachable.
        Barred = new[] { false, true },
    };

    private static TrackPosition Middle(TrackGraph g, int track) => new(track, g.Length(track) / 2);

    /// <summary>A light engine standing at a point.</summary>
    private static TrainPose Light(int track, double offset) => new(new TrackPosition(track, offset), null);

    [Fact]
    public void DistancesRunAlongTheTrackWhicheverWayTheSwitchesLie()
    {
        var g = Network();
        var d = GraphDistances.From(g, new TrackPosition(Lead, 100));
        Assert.Equal(100 + 3000 + 150, d.To(new TrackPosition(D1, 150)), 6);
        Assert.Equal(300 + 50, d.To(new TrackPosition(Y2, 50)), 6);
        Assert.Equal(40, d.To(new TrackPosition(Lead, 140)), 6);
    }

    [Fact]
    public void AJobsCarsSplitUpInTheirCutAreTakenWithTheCarsBetweenRiding()
    {
        Assert.Equal((0, 2), JobPlanner.JobSpan(new[] { D1, -1, D1, -1 }));
        Assert.Null(JobPlanner.JobSpan(new[] { -1, -1 }));

        var g = Network();
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[]
        {
            Cut("split", Y2, 100, 160, D1, -1, D1, -1),
        }, Array.Empty<int>());
        Assert.Empty(plan.Problems);
        var pickup = Assert.Single(plan.Pickups);
        // Coupled at the entry end: up to the second job car, the one between riding; the last stays.
        Assert.Equal(0, pickup.End);
        Assert.Equal(3, pickup.Keep);
        Assert.Equal(1, pickup.Riders);
        // The far one comes off first; the rider goes home between the two set-outs on D1.
        Assert.Equal(new[] { D1, D1 }, plan.Drops);
    }

    [Fact]
    public void ItTakesTheJobsCarsAndLeavesTheRest()
    {
        var g = Network();
        // Two cars for D1 at the entry end of four.
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { Cut("cut", Y1, 100, 160, D1, D1, -1, -1) },
            Array.Empty<int>());
        var pickup = Assert.Single(plan.Pickups);
        Assert.Equal(0, pickup.End);
        Assert.Equal(2, pickup.Keep);
        Assert.Equal(new[] { D1 }, plan.Drops);
    }

    [Fact]
    public void ACutBehindAnotherOfTheJobsComesInTheSamePull()
    {
        var g = Network();
        // On Y1, "near" stands between "deep" and the way out (as the game side reports it).
        var near = Cut("near", Y1, 50, 100, D1, D1);
        var deep = Cut("deep", Y1, 150, 200, D2, D2);
        deep.Way[0].Add(new CutRef(0, 0));
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { near, deep }, Array.Empty<int>());
        // One pull takes both: coupled to "near", then on through to "deep".
        var pull = Assert.Single(plan.Pickups);
        Assert.Equal(1, pull.Cut);
        Assert.Equal(new[] { new CutRef(0, 0) }, pull.Through);
        Assert.Equal(0, pull.Riders);
        // The deeper is the far end of the train: set out first.
        Assert.Equal(new[] { D2, D1 }, plan.Drops);
    }

    [Fact]
    public void ABuriedCutIsPulledOutWithTheCutsInFrontOfItRiding()
    {
        var g = Network();
        // On the stub Y1, three cars nobody wants stand between the job's cut and the way out.
        var strangers = Cut("strangers", Y1, 50, 90, -1, -1, -1);
        var buried = Cut("buried", Y1, 120, 140, D1);
        buried.Way[0].Add(new CutRef(0, 0));
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { strangers, buried }, Array.Empty<int>());
        Assert.Empty(plan.Problems);
        var pull = Assert.Single(plan.Pickups);
        Assert.Equal(1, pull.Cut);
        Assert.Equal(new[] { new CutRef(0, 0) }, pull.Through);
        Assert.Equal(3, pull.Riders);
        Assert.Equal(new[] { D1 }, plan.Drops);
        // With a buffer at its only end and nothing open, it can't be had at all.
        var walled = Cut("walled", Y2, 100, 120, D1);
        walled.Barred[0] = true;
        var none = JobPlanner.Plan(g, Light(Lead, 100), new[] { walled }, Array.Empty<int>());
        Assert.Empty(none.Pickups);
        Assert.Contains(none.Problems, p => p.StartsWith("walled"));
    }

    [Fact]
    public void OnAThroughTrackItPullsFromTheSideThatLeavesItsCarsLeading()
    {
        var g = Network();
        // The lead (400 m, open at both ends), the job's cut in the middle, a car between it and
        // the main end, four between it and the yard end. With no wye or loop, a train can't be
        // turned: the side it's pulled from fixes which way its cars can be shoved.
        JobPickup Pull(int destination, TrackPosition start)
        {
            var west = Cut("west", Lead, 10, 25, -1);
            var job = Cut("job", Lead, 190, 210, destination);
            var east = Cut("east", Lead, 300, 360, -1, -1, -1, -1);
            foreach (var c in new[] { west, job, east })
                c.Barred[1] = false;
            job.Way[0].Add(new CutRef(0, 0));
            job.Way[1].Add(new CutRef(2, 1));
            return Assert.Single(JobPlanner.Plan(g, new TrainPose(start, null), new[] { west, job, east }, Array.Empty<int>()).Pickups);
        }
        // For Y2, beyond the yard end: pulled from the main end, the loco west of its cars, so it
        // shoves them east into the stub; even starting from the yard, and through the one car.
        Assert.Equal(0, Pull(Y2, new TrackPosition(Main, 100)).End);
        var fromYard = Pull(Y2, new TrackPosition(Y1, 150));
        Assert.Equal(0, fromYard.End);
        Assert.Equal(1, fromYard.Riders);
        // For D1, at the station down the main: pulled from the yard end, through the four,
        // even starting from the main.
        var forStation = Pull(D1, new TrackPosition(Main, 100));
        Assert.Equal(1, forStation.End);
        Assert.Equal(4, forStation.Riders);
    }

    [Fact]
    public void InAnIndustryItPullsOneLineAtATime()
    {
        var g = Network();
        // Two stubs, each with a job's car buried behind three others, both for the lead:
        // pull one line, set its car out and put the three back, then the other.
        var s1 = Cut("s1", Y1, 50, 95, -1, -1, -1);
        var j1 = Cut("j1", Y1, 120, 135, Lead);
        var s2 = Cut("s2", Y2, 50, 95, -1, -1, -1);
        var j2 = Cut("j2", Y2, 120, 135, Lead);
        j1.Way[0].Add(new CutRef(0, 0));
        j2.Way[0].Add(new CutRef(2, 0));
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { s1, j1, s2, j2 }, Array.Empty<int>());
        Assert.Equal(2, plan.Pickups.Count);
        Assert.Equal(new[] { 0, 1 }, plan.Pickups.Select(p => p.Trip));
        // For the station 3 km off, with nothing in the way: both cuts on one trip.
        var far = JobPlanner.Plan(g, Light(Lead, 100), new[] { Cut("a", Y1, 100, 150, D1), Cut("b", Y2, 100, 150, D1) },
            Array.Empty<int>());
        Assert.Equal(new[] { 0, 0 }, far.Pickups.Select(p => p.Trip));
        Assert.Equal(new[] { D1 }, far.Drops);
    }

    [Fact]
    public void APullOnlyALightLocoCanMakeBeginsATrip()
    {
        var g = Network();
        // Both for the station: gathered on one trip, but the second only a light loco can reach.
        var a = Cut("a", Y1, 100, 150, D1);
        var b = Cut("b", Y2, 100, 150, D1);
        b.LightOnly[0] = true;
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { a, b }, Array.Empty<int>());
        Assert.Equal(2, plan.Pickups.Count);
        var bp = plan.Pickups.Single(p => p.Cut == 1);
        Assert.True(plan.Pickups.IndexOf(bp) == 0 || plan.Pickups[plan.Pickups.IndexOf(bp) - 1].Trip != bp.Trip,
            $"b's pull came after another in its trip: {string.Join("; ", plan.Pickups)}");
        // With cars on the train already, it waits for a trip of its own.
        var loaded = JobPlanner.Plan(g, Light(Lead, 100), new[] { b }, new[] { D2 });
        Assert.Equal(1, Assert.Single(loaded.Pickups).Trip);
    }

    [Fact]
    public void RidersAlreadyOnTheTrainGoHomeAfterTheSetOuts()
    {
        var g = Network();
        // A car between the loco and the job's car, from Y2: the job's set out, then it goes back.
        var plan = JobPlanner.Plan(g, Light(Lead, 100), Array.Empty<JobCut>(), new[] { D1, JobPlanner.RiderCode(Y2) });
        Assert.Empty(plan.Problems);
        Assert.Equal(new[] { D1 }, plan.Drops);
        var stays = JobPlanner.Plan(g, Light(Lead, 100), Array.Empty<JobCut>(), new[] { D1, -1 });
        Assert.True(plan.Cost > stays.Cost, "putting the rider back costs the trip there");
    }

    [Fact]
    public void ItOrdersThePickupsSoTheSetOutsRunInOneSweep()
    {
        var g = Network();
        // One cut for the station 3 km off, one for a track in the yard. The cheapest plan
        // sets the yard's cars out before leaving: fetched last, they're at the far end of
        // the train and come off first.
        var forStation = Cut("station", Y1, 100, 150, D1, D1);
        var forYard = Cut("yard", Y2, 100, 150, Y1, Y1);
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { forStation, forYard }, Array.Empty<int>());
        Assert.Equal(new[] { 0, 1 }, plan.Pickups.Select(p => p.Cut));
        Assert.Equal(new[] { Y1, D1 }, plan.Drops);
    }

    [Fact]
    public void TheSameTracksCarsAreKeptTogether()
    {
        var g = Network();
        // Two cuts for D1 and one for D2: fetching D2's between them would split D1's set-out.
        var a = Cut("a", Y1, 100, 150, D1, D1);
        var b = Cut("b", Y2, 100, 150, D2);
        var c = Cut("c", Y2, 200, 250, D1);
        c.Barred[1] = false; // c's far end is open (as though Y2 ran on); from the entry, b stands in its way
        c.Way[0].Add(new CutRef(1, 0));
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { a, b, c }, Array.Empty<int>());
        Assert.Equal(2, plan.Drops.Count);
        Assert.Equal(plan.Drops.Distinct().Count(), plan.Drops.Count);
    }

    [Fact]
    public void CarsAlreadyOnTheTrainAreSetOutAfterThePickups()
    {
        var g = Network();
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { Cut("cut", Y1, 100, 150, D2) },
            new[] { D1, D1 });
        Assert.Equal(new[] { D2, D1 }, plan.Drops);
        // A car of the train's own at the far end, in the way of the job's: set aside, then the job's set out.
        var inTheWay = JobPlanner.Plan(g, Light(Lead, 100), Array.Empty<JobCut>(), new[] { -1, D1 });
        Assert.Empty(inTheWay.Problems);
        Assert.Equal(new[] { D1 }, inTheWay.Drops);
    }

    [Fact]
    public void AJobsCarsInTheMiddleOfACutAreHadWithTheCarsBeforeThemRiding()
    {
        var g = Network();
        // On Y1, entered at its Start (end A): one car, the job's two, then two more toward the buffer.
        var plan = JobPlanner.Plan(g, Light(Lead, 100), new[] { Cut("middle", Y1, 100, 175, -1, D1, D1, -1, -1) },
            Array.Empty<int>());
        var pickup = Assert.Single(plan.Pickups);
        Assert.Equal(0, pickup.End);
        // The first three come along (one riding, put back after); the two beyond stay where they are.
        Assert.Equal(3, pickup.Keep);
        Assert.Equal(1, pickup.Riders);
        Assert.Empty(plan.Problems);
        Assert.Equal(new[] { D1 }, plan.Drops);
        // The rider's code, as the prediction carries it: back to Y1.
        Assert.Equal(new[] { D1, D1, JobPlanner.RiderCode(Y1) }, JobPlanner.FarToNear(Cut("x", Y1, 0, 1, -1, D1, D1, -1, -1), 0, 3, riders: true));
    }

    [Fact]
    public void ASetOutCouplesToCarsByTheEntryAndShovesThemIn()
    {
        var g = Network();
        // D1, a 300 m stub entered at its Start: a cut stands 20-60 m in, too near the entry for 60 m of cars.
        var standing = new[] { new OccupiedSpan(20, 60, owner: 7) };
        Assert.Empty(OrderGoals.SpotOn(g, D1, 60, standing));
        var shove = OrderGoals.ShoveIn(g, D1, 60, standing);
        Assert.NotNull(shove);
        Assert.Equal(7, shove!.Value.Owner);
        // Couple at its near end, then shove it to 4 m short of the buffer.
        Assert.Equal(20, Assert.Single(shove.Value.CoupleTo).Position.Offset, 6);
        Assert.Equal(TrackDirection.Backward, shove.Value.CoupleTo[0].Outward);
        Assert.Equal(300 - OrderGoals.BufferMargin, shove.Value.ShoveTo.Offset, 6);
        Assert.Equal(TrackDirection.Forward, shove.Value.Inward);
        // A second cut deeper in stops the shove 8 m short of it; too little room then, none.
        var two = new[] { new OccupiedSpan(20, 60, owner: 7), new OccupiedSpan(100, 296, owner: 8) };
        Assert.Null(OrderGoals.ShoveIn(g, D1, 60, two));
        Assert.NotNull(OrderGoals.ShoveIn(g, D1, 30, two));
    }

    [Fact]
    public void WithNoRoomBehindTheFirstCutTheSetOutCouplesOnThroughTheNext()
    {
        var g = Network();
        // Three cuts close together by D1's entry: only shoving all three in makes room for 60 m.
        var three = new[] { new OccupiedSpan(20, 60, owner: 7), new OccupiedSpan(70, 120, owner: 8), new OccupiedSpan(130, 180, owner: 9) };
        var shove = OrderGoals.ShoveIn(g, D1, 60, three);
        Assert.NotNull(shove);
        Assert.Equal(new[] { 7, 8, 9 }, shove!.Value.Owners);
        Assert.Equal(new[] { 20.0, 70, 130 }, shove.Value.CoupleTo.Select(c => c.Position.Offset));
        Assert.Equal(300 - OrderGoals.BufferMargin, shove.Value.ShoveTo.Offset, 6);
        // A short set-out needs the first only.
        Assert.Equal(new[] { 7 }, OrderGoals.ShoveIn(g, D1, 5, three)!.Value.Owners);
    }

    [Fact]
    public void ShovingThroughCutsCountsTheGapsTheyCloseAndNeverMovesALoco()
    {
        var g = Network();
        // D1 (300 m stub): 40 m of cars by the entry, 160 m deeper with a 40 m gap between.
        // Coupled together they close up: 200 m of cars, 80 m to come and 15 m to clear the entry fit in 296.
        var two = new[] { new OccupiedSpan(20, 60, owner: 7), new OccupiedSpan(100, 260, owner: 8) };
        var shove = OrderGoals.ShoveIn(g, D1, 80, two);
        Assert.NotNull(shove);
        Assert.Equal(new[] { 7, 8 }, shove!.Value.Owners);
        Assert.Equal(300 - OrderGoals.BufferMargin, shove.Value.ShoveTo.Offset, 6);
        // The deeper one a train with a loco: no shoving through it, and not room enough short of it.
        Assert.Null(OrderGoals.ShoveIn(g, D1, 80, two, movable: owner => owner != 8));
        // The cars by the entry a loco's: never coupled to at all.
        Assert.Null(OrderGoals.ShoveIn(g, D1, 5, two, movable: owner => owner != 7));
        // With room enough short of the loco's train, the cut by the entry is shoved up to it.
        var roomy = new[] { new OccupiedSpan(20, 40, owner: 7), new OccupiedSpan(200, 260, owner: 8) };
        var upTo = OrderGoals.ShoveIn(g, D1, 40, roomy, movable: owner => owner != 8);
        Assert.Equal(new[] { 7 }, upTo!.Value.Owners);
        Assert.Equal(200 - OrderGoals.SpotGap, upTo.Value.ShoveTo.Offset, 6);
    }

    [Fact]
    public void ASetOutJoinsItsJobsCarsStandingByTheEntry()
    {
        var g = Network();
        var theirs = new[] { new OccupiedSpan(40, 80, owner: 7) };
        // Room for 20 m: coupled on where they stand, not shoved.
        var join = OrderGoals.ShoveIn(g, D1, 20, theirs, join: 7);
        Assert.Equal(0, join!.Value.Shift, 6);
        Assert.Equal(80, join.Value.ShoveTo.Offset, 6);
        // 40 m: shoved just far enough for the set-out to clear the entry.
        Assert.Equal(40 + OrderGoals.ClearOfEntry - 40, OrderGoals.ShoveIn(g, D1, 40, theirs, join: 7)!.Value.Shift, 6);
        // Someone else's cars nearest the entry: no join.
        Assert.Null(OrderGoals.ShoveIn(g, D1, 20, theirs, join: 8));
    }

    [Fact]
    public void ACouplingToCarsJustBeyondTheTrainAimsAcrossTheGap()
    {
        var g = Network();
        // A train on Y1 from 60 to 100 m, the loco at 60: its far end at 100, facing the buffer.
        var train = new TrainState
        {
            EndA = new TrackPosition(Y1, 60),
            OutwardA = TrackDirection.Backward,
            EndB = new TrackPosition(Y1, 100),
            OutwardB = TrackDirection.Forward,
            Length = 40,
            CarCount = 3,
            LocoEnd = ConsistEnd.A,
        };
        var near = Assert.Single(OrderGoals.CoupleOnward(g, train, new CutEnd(new TrackPosition(Y1, 106), TrackDirection.Backward), _ => 0));
        Assert.Equal(103, near.Position.Offset, 6);
        Assert.Equal(TrackDirection.Forward, near.Arrive);
        // Farther off: the usual goal, short of the cut.
        var far = Assert.Single(OrderGoals.CoupleOnward(g, train, new CutEnd(new TrackPosition(Y1, 200), TrackDirection.Backward), _ => 0));
        Assert.Equal(200 - OrderGoals.ShortOfCut, far.Position.Offset, 6);
        // Touching: just ahead of the train's end, never under it.
        var touching = Assert.Single(OrderGoals.CoupleOnward(g, train, new CutEnd(new TrackPosition(Y1, 100), TrackDirection.Backward), _ => 0));
        Assert.True(touching.Position.Track == Y1 && touching.Position.Offset > 100 && touching.Position.Offset < 100.5, touching.ToString());
        // Past the switch at the lead's end: the goal follows the switch as set, onto Y1.
        var onLead = train;
        onLead.EndA = new TrackPosition(Lead, 350);
        onLead.EndB = new TrackPosition(Lead, 394);
        var beyond = Assert.Single(OrderGoals.CoupleOnward(g, onLead, new CutEnd(new TrackPosition(Y1, 8), TrackDirection.Backward), _ => 0));
        Assert.Equal(Y1, beyond.Position.Track);
        Assert.Equal(TrackDirection.Forward, beyond.Arrive);
    }

    [Fact]
    public void TheNextSetOutIsTheFarEndsRunOfCarsForOneTrack()
    {
        Assert.Equal((D1, 2), JobPlanner.NextDrop(new[] { D1, D1, D2, -1 }));
        Assert.Equal((D2, 1), JobPlanner.NextDrop(new[] { D2, D1 }));
        Assert.Null(JobPlanner.NextDrop(new[] { -1, D1 }));
        Assert.Null(JobPlanner.NextDrop(Array.Empty<int>()));
    }

    [Fact]
    public void ASetOutGoesAsFarInAsTheTrackAllows()
    {
        var g = Network();
        // D1 is a 300 m stub entered at its Start: empty, the cars go to 4 m short of the buffer,
        // leaving the room by the entry for the next set-out.
        var empty = OrderGoals.SpotOn(g, D1, 60, Array.Empty<OccupiedSpan>());
        var goal = Assert.Single(empty);
        Assert.Equal(300 - OrderGoals.BufferMargin, goal.Position.Offset, 6);
        Assert.Equal(TrackDirection.Forward, goal.Arrive);
        // Cars standing from 150 m: the next cut goes in to 8 m short of them.
        var some = OrderGoals.SpotOn(g, D1, 100, new[] { new OccupiedSpan(150, 296, owner: 5) });
        Assert.Equal(150 - OrderGoals.SpotGap, Assert.Single(some).Position.Offset, 6);
        Assert.Equal(150 - OrderGoals.SpotGap - OrderGoals.ClearOfEntry, OrderGoals.SpotRoom(g, D1, new[] { new OccupiedSpan(150, 296, owner: 5) }), 6);
        // Standing from 60 m, there's no room for 100 m of cars.
        Assert.Empty(OrderGoals.SpotOn(g, D1, 100, new[] { new OccupiedSpan(60, 200, owner: 5) }));
        // A through track keeps clear of the switch at its far end too.
        var through = OrderGoals.SpotOn(g, Lead, 60, Array.Empty<OccupiedSpan>());
        Assert.Contains(through, x => Math.Abs(x.Position.Offset - (400 - OrderGoals.ClearOfEntry)) < 1e-6);
    }

    // ---- The engineer carrying the orders out ----

    private static (FakeWorld World, int Junction) Wye()
    {
        var b = new TrackGraphBuilder();
        var stem = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(600, 0));
        var straight = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, 0));
        var diverging = b.AddTrack(600, new MapPoint(0, 0), new MapPoint(-600, -60));
        var j = b.AddJunction(stem, TrackEnd.Start, new[] { (straight, TrackEnd.Start), (diverging, TrackEnd.Start) }, new MapPoint(0, 0));
        return (new FakeWorld(b.Build()), j);
    }

    [Fact]
    public void APickupKeepsOnlyTheJobsCars()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 20) { CarCount = 1 };
        world.Occupancy.Add(2, new OccupiedSpan(300, 360, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 280), WaypointAction.Couple) { KeepCars = 2, DoneNote = "picked up" });
        CutRequest? cut = null;
        string? note = null;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0, onOutput: output =>
        {
            cut ??= output.Cut;
            note ??= engineer.Note;
            if (output.CoupleAt == ConsistEnd.A && train.CarCount == 1 && train.EndA.Track == 2 && 300 - train.EndA.Offset <= 1.5)
            {
                world.Occupancy.Clear();
                train.CoupleOn(ConsistEnd.A, 60, 4);
            }
        });
        Assert.NotNull(cut);
        // Four coupled, two kept: the two beyond them cut off again, hand brakes on.
        Assert.Equal(2, cut!.Value.Cars);
        Assert.True(cut.Value.SetHandbrakes);
        Assert.Equal("picked up", note);
    }

    [Fact]
    public void CoupledToALongCutTheOrderEndsWhereItStands()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 20) { CarCount = 1 };
        world.Occupancy.Add(2, new OccupiedSpan(300, 500, owner: 9));
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 285), WaypointAction.Couple));
        TrackPosition? coupledAt = null;
        var statuses = new HashSet<string>();
        Drive.Run(engineer, train, world, maxSeconds: 400, until: () => engineer.Queue.Count == 0, onOutput: output =>
        {
            if (coupledAt != null)
                statuses.Add(engineer.Status);
            if (output.CoupleAt == ConsistEnd.A && train.CarCount == 1 && train.EndA.Track == 2 && 300 - train.EndA.Offset <= 1.5)
            {
                world.Occupancy.Clear();
                train.CoupleOn(ConsistEnd.A, 200, 14);
                coupledAt = train.EndB;
            }
        });
        Assert.NotNull(coupledAt);
        Assert.Empty(engineer.Queue);
        Assert.DoesNotContain(statuses, x => x.StartsWith("Off the route"));
        Assert.True(Math.Abs(train.EndB.Offset - coupledAt!.Value.Offset) < 3 && train.EndB.Track == coupledAt.Value.Track,
            $"moved on from {coupledAt} to {train.EndB}");
    }

    [Fact]
    public void APickupCutsOffWhatTheGameSideCounts()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 20) { CarCount = 1 };
        world.Occupancy.Add(2, new OccupiedSpan(300, 360, owner: 9));
        var engineer = new Engineer();
        // Asked once coupled: the count comes from the cars themselves, not from the train's size.
        engineer.GoTo(new Waypoint(new TrackPosition(2, 280), WaypointAction.Couple) { CutAfterCoupling = () => 1 });
        CutRequest? cut = null;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0, onOutput: output =>
        {
            cut ??= output.Cut;
            if (output.CoupleAt == ConsistEnd.A && train.CarCount == 1 && train.EndA.Track == 2 && 300 - train.EndA.Offset <= 1.5)
            {
                world.Occupancy.Clear();
                train.CoupleOn(ConsistEnd.A, 60, 4);
            }
        });
        Assert.Equal(1, cut!.Value.Cars);
    }

    [Fact]
    public void ACutOnTheSpotIsMadeWithoutAMoveAndCountedAsItIsMade()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        var counted = 0;
        engineer.GoTo(new Waypoint(new TrackPosition(1, 0), WaypointAction.Uncouple) { Here = true, CarCount = 5, CutCount = () => ++counted == 1 ? 2 : 99 });
        CutRequest? cut = null;
        var start = train.EndA;
        Drive.Run(engineer, train, world, maxSeconds: 30, until: () => engineer.Queue.Count == 0, onOutput: o => cut ??= o.Cut);
        Assert.Empty(engineer.Queue);
        Assert.Equal(2, cut!.Value.Cars);
        Assert.Equal(1, counted);
        Assert.Equal(start, train.EndA);
    }

    [Fact]
    public void AShortCutOnSlowAirIsWorkedOnTheIndependent()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 60);
        // A loco whose own brake stops the train: half its mass.
        train.Physics.LocoBrakeDecel = 0.4;
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)));
        var trainBrake = 0.0;
        Drive.Run(engineer, train, world, until: () => engineer.Queue.Count == 0,
            state: s =>
            {
                s.Controls.SlowAir = true;
                s.LocoMassShare = 0.5; // a DE2 and a few empties: its own brake stops them
                return s;
            },
            onOutput: o => trainBrake = Math.Max(trainBrake, o.Controls.TrainBrake));
        Assert.Empty(engineer.Queue);
        Assert.Equal(0, trainBrake);
        // A heavy train is braked on the air.
        Assert.True(engineer.IndependentOnly(new TrainState { Controls = new ControlCapabilities { HasTrainBrake = true, SlowAir = true }, LocoMassShare = 0.5 }));
        Assert.False(engineer.IndependentOnly(new TrainState { Controls = new ControlCapabilities { HasTrainBrake = true, SlowAir = true }, LocoMassShare = 0.1 }));
    }

    [Fact]
    public void ABrakeThatWontComeOffAllTheWayDoesntHoldTheTrainForever()
    {
        // In game (2026-11-06): after a coupling one car's cylinder stayed at 0.6 bar with the
        // line charged, and the engineer waited 25 minutes for it.
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)));
        double? movedAt = null;
        string? note = null;
        Drive.Run(engineer, train, world, maxSeconds: 600, until: () => engineer.Queue.Count == 0,
            state: s =>
            {
                s.AirKnown = true;
                s.CarPipeMin = 5;
                s.CarCylinderMax = 0.6;
                s.HandbrakesOn = 0;
                return s;
            },
            onOutput: o =>
            {
                note ??= engineer.Note;
                if (movedAt == null && Math.Abs(train.SpeedTowardA) > 0.05)
                    movedAt = 0;
            });
        Assert.NotNull(movedAt);
        Assert.Empty(engineer.Queue);
        Assert.Contains("stays on a little", note);
        // A cylinder still well applied is waited for.
        Assert.False(engineer.AirReady(new TrainState { AirKnown = true, CarPipeMin = 5, CarCylinderMax = 1.5, CarCount = 6 }));
    }

    [Fact]
    public void ItWaitsForTheBrakesBeforeStartingAndAsksForHandBrakesOff()
    {
        var (world, _) = Wye();
        var train = new NetworkTrain(world, new TrackPosition(0, 200), TrackDirection.Backward, 80);
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 300)));
        var released = false;
        var ready = false;
        var movedWhileHeld = false;
        Drive.Run(engineer, train, world, maxSeconds: 400, until: () => engineer.Queue.Count == 0,
            state: s =>
            {
                s.AirKnown = true;
                s.CarPipeMin = ready ? 5 : 3;
                s.CarCylinderMax = ready ? 0 : 1.2;
                s.HandbrakesOn = released ? 0 : 3;
                return s;
            },
            onOutput: o =>
            {
                if (o.ReleaseHandbrakes)
                    released = true;
                if (!ready && Math.Abs(train.SpeedTowardA) > 0.05)
                    movedWhileHeld = true;
                if (!ready && engineer.Status.StartsWith("Charging"))
                    ready = true;
            });
        Assert.True(released, "never asked for the hand brakes off");
        Assert.False(movedWhileHeld, "moved before the brakes were off");
        Assert.Empty(engineer.Queue);
    }
}
