using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>
/// A player, 17 Nov 2026: trains ran between stations "with the entire train
/// in reverse" though there were easy ways to turn them, and a light loco
/// turned itself to couple with its back to the cars "even when it's faster
/// ... to couple with the front".
/// </summary>
public class ShovingAndFacingTests
{
    /// <summary>
    /// An east-west main with a wye on it, its legs <paramref name="leg"/> long:
    /// switches at x = -100 and 100 send legs north to an apex switch, whose
    /// stem is a 300 m tail. Tracks: 0 west main, 1 centre, 2 east main, 3 and 4
    /// the legs, 5 the tail.
    /// </summary>
    private static TrackGraph Wye(double leg = 180, double main = 900)
    {
        var b = new TrackGraphBuilder();
        var west = b.AddTrack(main, new MapPoint(-100 - main, 0), new MapPoint(-100, 0));
        var centre = b.AddTrack(200, new MapPoint(-100, 0), new MapPoint(100, 0));
        var east = b.AddTrack(main, new MapPoint(100, 0), new MapPoint(100 + main, 0));
        var westLeg = b.AddTrack(leg, new MapPoint(-100, 0), new MapPoint(0, 150));
        var eastLeg = b.AddTrack(leg, new MapPoint(100, 0), new MapPoint(0, 150));
        var tail = b.AddTrack(300, new MapPoint(0, 150), new MapPoint(0, 450));
        b.AddJunction(west, TrackEnd.End, new[] { (centre, TrackEnd.Start), (westLeg, TrackEnd.Start) }, new MapPoint(-100, 0));
        b.AddJunction(east, TrackEnd.Start, new[] { (centre, TrackEnd.End), (eastLeg, TrackEnd.Start) }, new MapPoint(100, 0));
        b.AddJunction(tail, TrackEnd.Start, new[] { (westLeg, TrackEnd.End), (eastLeg, TrackEnd.End) }, new MapPoint(0, 150));
        return b.Build();
    }

    /// <summary>A 100 m train on the east main 100 m from its west end, end A west, bound for the far west of the west main.</summary>
    private static RouteRequest Westbound(ConsistEnd? shoving, double goalFromWest = 50) => new()
    {
        EndA = new TrackPosition(2, 100),
        OutwardA = TrackDirection.Backward,
        EndB = new TrackPosition(2, 200),
        OutwardB = TrackDirection.Forward,
        TrainLength = 100,
        Goal = new TrackPosition(0, goalFromWest),
        ShovingEnd = shoving,
    };

    [Fact]
    public void UnpricedTheCarsAreShovedTheWholeWay()
    {
        var route = new Router(Wye(main: 3000)).Find(Westbound(null));
        Assert.NotNull(route);
        Assert.Equal(0, route!.Reversals);
        Assert.Equal(ConsistEnd.A, route.Legs[0].Leader);
    }

    [Fact]
    public void WithTheCarsAheadTheTrainTurnsOnTheWyeToRunLocoFirst()
    {
        // The loco at B, the cars at A toward the goal: 3 km of shoving, or a turn on the wye.
        var route = new Router(Wye(main: 3000)).Find(Westbound(ConsistEnd.A));
        Assert.NotNull(route);
        Assert.Equal(ConsistEnd.B, route!.Legs[^1].Leader);
        Assert.Contains(route.Legs.SelectMany(l => l.Pieces), p => p.Track == 5);
        Assert.True(route.Legs[^1].Length > 2500, $"the loco leads the last {route.Legs[^1].Length:F0} m");
    }

    [Fact]
    public void WithTheLocoAheadNothingChanges()
    {
        var route = new Router(Wye(main: 3000)).Find(Westbound(ConsistEnd.B));
        Assert.NotNull(route);
        Assert.Equal(0, route!.Reversals);
        Assert.Equal(ConsistEnd.A, route.Legs[0].Leader);
    }

    [Fact]
    public void AShortShoveIsStillTaken()
    {
        // The goal on the east main itself, 60 m beyond end A: shoving there beats any turn.
        var request = Westbound(ConsistEnd.A);
        request.Goal = new TrackPosition(2, 40);
        var route = new Router(Wye(main: 3000)).Find(request);
        Assert.NotNull(route);
        Assert.Equal(0, route!.Reversals);
        Assert.Equal(ConsistEnd.A, route.Legs[0].Leader);
    }

    /// <summary>A 40 m train on the east main, end A west, told to go to the far west of the west main.</summary>
    private static Route? PlanGoTo(int cars, int power, ConsistEnd locoEnd, bool powerAtFarEnd = false)
    {
        var world = new FakeWorld(Wye(main: 3000));
        var train = new NetworkTrain(world, new TrackPosition(2, 100), TrackDirection.Backward, 40) { CarCount = cars };
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 50)));
        Drive.Run(engineer, train, world, maxSeconds: 5, until: () => engineer.Route != null,
            state: s =>
            {
                s.PowerCars = power;
                s.LocoEnd = locoEnd;
                s.LocoFront = locoEnd;
                s.PowerAtFarEnd = powerAtFarEnd;
                return s;
            });
        return engineer.Route;
    }

    [Theory]
    [InlineData(2, 2, false)]  // a loco and its tender, or two units
    [InlineData(3, 3, true)]   // three units
    [InlineData(6, 2, true)]   // cars between a loco at each end
    public void PowerAloneOrAtBothEndsRunsStraight(int cars, int power, bool powerAtFarEnd)
    {
        var route = PlanGoTo(cars, power, ConsistEnd.B, powerAtFarEnd);
        Assert.NotNull(route);
        Assert.Equal(0, route!.Reversals);
    }

    [Fact]
    public void ACarAheadOfALocoAndTenderIsStillShoving()
    {
        var route = PlanGoTo(3, 2, ConsistEnd.B);
        Assert.NotNull(route);
        Assert.Equal(ConsistEnd.B, route!.Legs[^1].Leader);
        Assert.Contains(route.Legs.SelectMany(l => l.Pieces), p => p.Track == 5);
    }

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(2, 2, 0, true)]   // a loco and its tender
    [InlineData(2, 1, 1, false)]
    [InlineData(6, 0, 5, false)]  // no count given: the one loco
    [InlineData(7, 2, 5, false)]
    public void WagonsAreTheCarsThatAreNotPower(int cars, int power, int wagons, bool light)
    {
        var train = new TrainState { CarCount = cars, PowerCars = power, LocoEnd = ConsistEnd.A, LocoFront = ConsistEnd.A };
        Assert.Equal(wagons, train.WagonCount);
        Assert.Equal(light, train.LightEngine);
        Assert.Equal(light ? null : ConsistEnd.B, train.ShovingEnd);
        Assert.Equal(light ? null : ConsistEnd.B, Engineer.Leader(Arrival.CarsFirst, train));
        Assert.Equal(light ? null : ConsistEnd.A, Engineer.Leader(Arrival.LocoFirst, train));
    }

    [Fact]
    public void ALocosTenderIsNeverLeftAsACar()
    {
        var world = new FakeWorld(Wye());
        var train = new NetworkTrain(world, new TrackPosition(2, 300), TrackDirection.Backward, 30) { CarCount = 2 };
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(2, 200), WaypointAction.Uncouple) { CarCount = 1 });
        var cut = false;
        Drive.Run(engineer, train, world, maxSeconds: 200, until: () => engineer.Queue.Count == 0, onOutput: o => cut |= o.Cut != null,
            state: s =>
            {
                s.PowerCars = 2;
                return s;
            });
        Assert.Empty(engineer.Queue);
        Assert.False(cut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ConsistEnd.A)]
    [InlineData(ConsistEnd.B)]
    public void TheSearchPricesShovingAsTheTrainRunsIt(ConsistEnd? shoving)
    {
        // West, end B last: a turn on the wye, with its reversal; every meter run is priced once, by the end leading it.
        var request = Westbound(shoving);
        request.LastLeader = ConsistEnd.B;
        request.DivergingPenalty = 0;
        var route = new Router(Wye(main: 3000)).Find(request);
        Assert.NotNull(route);
        Assert.True(route!.Reversals > 0);
        var shoved = route.Legs.Where(l => l.Leader == shoving).Sum(l => l.Running);
        Assert.Equal(route.Running + 400 * route.Reversals + shoved, route.SearchCost, 6);
        Assert.Equal(route.Length + 400 * route.Reversals + shoved, route.Cost(request), 6);
        // The leg after the reversal starts a train's length beyond the end now leading it.
        Assert.Equal(request.TrainLength, route.Legs[1].StartOverlap, 6);
    }

    /// <summary>A light loco on the east main, front (A) west, told to couple on the west main facing away from the cars; a tender or second unit counted.</summary>
    private static (Engineer Engineer, Route? Route, NetworkTrain Train, FakeWorld World) PlanCouple(double leg, int cars = 1, int power = 0)
    {
        var world = new FakeWorld(Wye(leg: leg, main: 900));
        var train = new NetworkTrain(world, new TrackPosition(2, 100), TrackDirection.Backward, 20) { CarCount = cars };
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 300), WaypointAction.Couple) { Arrival = Arrival.BackFirst, Label = "the cut" });
        RunUntilPlanned(engineer, train, world, power);
        return (engineer, engineer.Route, train, world);
    }

    private static void RunUntilPlanned(Engineer engineer, NetworkTrain train, FakeWorld world, int power) =>
        Drive.Run(engineer, train, world, maxSeconds: 5, until: () => engineer.Route != null,
            state: s =>
            {
                s.PowerCars = power;
                s.LocoEnd = ConsistEnd.A;
                s.LocoFront = ConsistEnd.A;
                return s;
            });

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    public void ALightLocoTurnsToCoupleBackFirstWhenTheWyeIsNear(int cars, int power)
    {
        var (engineer, route, _, _) = PlanCouple(180, cars, power);
        Assert.NotNull(route);
        Assert.Equal(ConsistEnd.B, route!.Legs[^1].Leader);
        Assert.Null(engineer.LogNote);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]   // a loco and its tender
    public void ALightLocoCouplesFacingTheCarsRatherThanGoFarToTurn(int cars, int power)
    {
        var (engineer, route, _, _) = PlanCouple(1500, cars, power);
        Assert.NotNull(route);
        Assert.Equal(ConsistEnd.A, route!.Legs[^1].Leader);
        Assert.Equal(0, route.Reversals);
        Assert.Null(engineer.Note);   // the facing is the default, not the player's: logged, not told
        Assert.Contains("turning it first would add about", engineer.LogNote);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(1500)]
    public void WithCarsOnTheCarsMeetTheCut(double leg)
    {
        var world = new FakeWorld(Wye(leg: leg, main: 900));
        var train = new NetworkTrain(world, new TrackPosition(2, 100), TrackDirection.Backward, 30) { CarCount = 2 };
        var engineer = new Engineer();
        engineer.GoTo(new Waypoint(new TrackPosition(0, 300), WaypointAction.Couple) { Arrival = Arrival.BackFirst, Label = "the cut" });
        Drive.Run(engineer, train, world, maxSeconds: 5, until: () => engineer.Route != null,
            state: s =>
            {
                s.PowerCars = 1;
                s.LocoEnd = ConsistEnd.B;
                s.LocoFront = ConsistEnd.A;
                return s;
            });
        Assert.NotNull(engineer.Route);
        Assert.Equal(ConsistEnd.A, engineer.Route!.Legs[^1].Leader);
        Assert.Equal(0, engineer.Route.Reversals);
        Assert.Null(engineer.LogNote);
    }

    [Fact]
    public void TheFacingIsLoggedOnceAnOrderHoweverItsNumbersChange()
    {
        var (engineer, _, train, world) = PlanCouple(1500);
        Assert.NotNull(engineer.LogNote);
        engineer.LogNote = null;
        Drive.Run(engineer, train, world, maxSeconds: 10);   // moved on, so a new plan has other numbers
        engineer.Replan();
        RunUntilPlanned(engineer, train, world, 0);
        Assert.NotNull(engineer.Route);
        Assert.Null(engineer.LogNote);
    }
}
