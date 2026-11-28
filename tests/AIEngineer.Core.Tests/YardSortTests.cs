using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>
/// Yard classification (flat switching) planner tests: ladder selection, physical lead
/// measurement, optimal track assignment (including corridor clustering when destinations
/// exceed tracks), and end-to-end stateful switching simulations across interchange and stub yards.
/// </summary>
public class YardSortTests
{
    // ---- Synthetic 5-track double-ended interchange yard (modeled on WI / NEI) ----
    // East lead -> 4 east ladder switches -> 5 parallel bowl tracks (T0..T4) -> 4 west ladder switches -> West lead.
    private sealed class InterchangeYard
    {
        public TrackGraph Graph { get; }
        public int EastLead { get; }
        public int WestLead { get; }
        public int[] Bowl { get; } = new int[5];

        public InterchangeYard(double eastLeadLen = 350, double westLeadLen = 350, double[]? bowlLengths = null)
        {
            var lengths = bowlLengths ?? new[] { 620.0, 552.0, 485.0, 417.0, 352.0 };
            var b = new TrackGraphBuilder();
            EastLead = b.AddTrack(eastLeadLen, new MapPoint(-eastLeadLen, 0), new MapPoint(0, 0));
            WestLead = b.AddTrack(westLeadLen, new MapPoint(700, 0), new MapPoint(700 + westLeadLen, 0));

            // 5 bowl tracks running west (+X) from their Start (east end) to End (west end).
            for (var i = 0; i < 5; i++)
            {
                var y = i * 5.0;
                Bowl[i] = b.AddTrack(lengths[i], new MapPoint(40 + i * 15, y), new MapPoint(40 + i * 15 + lengths[i], y));
            }

            // East ladder: EastLead.End -> sw0 -> (Bowl[0].Start, leg01) -> sw1 -> (Bowl[1].Start, leg12) -> ...
            var stem = EastLead;
            var stemEnd = TrackEnd.End;
            for (var i = 0; i < 4; i++)
            {
                if (i < 3)
                {
                    var leg = b.AddTrack(18, new MapPoint(i * 18, i * 5), new MapPoint((i + 1) * 18, (i + 1) * 5));
                    b.AddJunction(stem, stemEnd, new[] { (Bowl[i], TrackEnd.Start), (leg, TrackEnd.Start) }, new MapPoint(i * 18, i * 5));
                    stem = leg;
                    stemEnd = TrackEnd.End;
                }
                else
                {
                    b.AddJunction(stem, stemEnd, new[] { (Bowl[3], TrackEnd.Start), (Bowl[4], TrackEnd.Start) }, new MapPoint(54, 15));
                }
            }

            // West ladder: WestLead.Start -> sw -> (Bowl[0].End, leg) -> ...
            stem = WestLead;
            stemEnd = TrackEnd.Start;
            for (var i = 0; i < 4; i++)
            {
                if (i < 3)
                {
                    var leg = b.AddTrack(18, new MapPoint(700 - i * 18, i * 5), new MapPoint(700 - (i + 1) * 18, (i + 1) * 5));
                    b.AddJunction(stem, stemEnd, new[] { (Bowl[i], TrackEnd.End), (leg, TrackEnd.Start) }, new MapPoint(700 - i * 18, i * 5));
                    stem = leg;
                    stemEnd = TrackEnd.End;
                }
                else
                {
                    b.AddJunction(stem, stemEnd, new[] { (Bowl[3], TrackEnd.End), (Bowl[4], TrackEnd.End) }, new MapPoint(646, 15));
                }
            }

            Graph = b.Build();
        }
    }

    // ---- Synthetic 3-track stub yard (buffer stops on far end) ----
    private sealed class StubYard
    {
        public TrackGraph Graph { get; }
        public int Lead { get; }
        public int[] Bowl { get; } = new int[3];

        public StubYard(double leadLen = 130, double bowlLen = 300)
        {
            var b = new TrackGraphBuilder();
            Lead = b.AddTrack(leadLen, new MapPoint(-leadLen, 0), new MapPoint(0, 0));
            for (var i = 0; i < 3; i++)
                Bowl[i] = b.AddTrack(bowlLen, new MapPoint(30 + i * 18, i * 5), new MapPoint(30 + i * 18 + bowlLen, i * 5));
            var leg = b.AddTrack(18, new MapPoint(0, 0), new MapPoint(18, 5));
            b.AddJunction(Lead, TrackEnd.End, new[] { (Bowl[0], TrackEnd.Start), (leg, TrackEnd.Start) }, new MapPoint(0, 0));
            b.AddJunction(leg, TrackEnd.End, new[] { (Bowl[1], TrackEnd.Start), (Bowl[2], TrackEnd.Start) }, new MapPoint(18, 5));
            Graph = b.Build();
        }
    }

    private static TrainState LightLocoAt(int track, double offset) => new()
    {
        EndA = new TrackPosition(track, offset),
        OutwardA = TrackDirection.Forward,
        EndB = new TrackPosition(track, Math.Max(0, offset - 15)),
        OutwardB = TrackDirection.Backward,
        Length = 15,
        CarCount = 1,
        PowerCars = 1,
        LocoEnd = ConsistEnd.A,
        Owner = 1,
        OnTrack = true,
        CanMove = true,
    };

    private static List<SortTrack> TracksOf(InterchangeYard y) =>
        y.Bowl.Select((t, i) => new SortTrack { Track = t, Name = $"WI-A{i + 1}X", Length = y.Graph.Length(t) }).ToList();

    private static List<SortTrack> TracksOf(StubYard y) =>
        y.Bowl.Select((t, i) => new SortTrack { Track = t, Name = $"Y{i + 1}", Length = y.Graph.Length(t) }).ToList();

    private static SortCar Car(int id, string key, double length = 14) => new() { Id = id, Key = key, Length = length };

    private static YardTrackState MakeTrack(int track, string name, double length, bool deadEnd, params (int Owner, string[] Keys)[] cutsInnerToOuter)
    {
        var ts = new YardTrackState
        {
            Track = new SortTrack { Track = track, Name = name, Length = length, WorkingEnd = TrackEnd.Start, DeadEnd = deadEnd },
        };
        var nextId = track * 1000 + 1;
        var pos = length - (deadEnd ? OrderGoals.BufferMargin : OrderGoals.ClearOfEntry);
        foreach (var (owner, keys) in cutsInnerToOuter)
        {
            var cut = new SortCut { Owner = owner, Track = track };
            foreach (var k in keys)
                cut.CarsInnerToOuter.Add(Car(nextId++, k));
            cut.FarFromWorking = pos;
            cut.NearFromWorking = Math.Max(0, pos - cut.Length);
            cut.OuterEnd = new CutEnd(new TrackPosition(track, cut.NearFromWorking), TrackDirection.Backward);
            cut.InnerEnd = new CutEnd(new TrackPosition(track, cut.FarFromWorking), TrackDirection.Forward);
            pos = cut.NearFromWorking - 10;
            ts.CutsInnerToOuter.Add(cut);
        }
        return ts;
    }

    // ---- Stateful Flat-Switching Simulator ----
    // Executes YardSort.NextAction step-by-step on in-memory YardTrackStates + switcher hook,
    // checking capacity invariants, cut integrity, and convergence.
    private static (int Pulls, int SetOuts, int Consolidations, int Sluffs) SimulateSort(
        SortAssignment assignment, List<YardTrackState> tracks, List<SortCar> inTrainFarToNear, int maxSteps = 300)
    {
        var byTrack = tracks.ToDictionary(t => t.Track.Track);
        var nextOwner = 5000;
        var initialIds = new HashSet<int>();
        foreach (var ts in tracks)
            if (!ts.Track.Blocked)
                foreach (var c in ts.AllCarsInnerToOuter())
                    Assert.True(initialIds.Add(c.Id));
        foreach (var c in inTrainFarToNear)
            Assert.True(initialIds.Add(c.Id));

        int pulls = 0, setOuts = 0, consolidations = 0, sluffs = 0;
        int lastPulled = -1, clearingTrack = -1, consecutivePulls = 0;
        var visitedMoves = new HashSet<string>(StringComparer.Ordinal);
        var log = new List<string>();

        for (var step = 0; step < maxSteps; step++)
        {
            if (clearingTrack >= 0 && byTrack.TryGetValue(clearingTrack, out var ct)
                && YardSort.AnchorCount(ct, assignment, tracks, inTrainFarToNear) >= ct.TotalCars)
                clearingTrack = -1;
            var action = YardSort.NextAction(assignment, tracks, inTrainFarToNear, lastPulled, consecutivePulls, clearingTrack: clearingTrack, visitedMoves: visitedMoves);
            if (log.Count < 30 || step >= maxSteps - 15)
                log.Add($"#{step} {action.Kind} T{action.Track}: {action.Reason} (hook={inTrainFarToNear.Count})");
            if (action.Kind == SortActionKind.Done)
            {
                // Verify ALL invariants at Done!
                Assert.Empty(inTrainFarToNear);
                var finalIds = new HashSet<int>();
                foreach (var ts in tracks)
                {
                    if (ts.Track.Blocked)
                        continue;
                    var cars = ts.AllCarsInnerToOuter();
                    foreach (var c in cars)
                        Assert.True(finalIds.Add(c.Id), $"Duplicate car {c.Id}");
                    if (cars.Count == 0)
                        continue;
                    // Every occupied track must be ONE single coupled cut!
                    Assert.Single(ts.CutsInnerToOuter);
                    // Capacity must never be exceeded.
                    Assert.True(ts.OccupiedLength <= ts.Track.UsableLength + 1e-6,
                        $"{ts.Track.Name} overflowed: {ts.OccupiedLength} > {ts.Track.UsableLength}");
                    // Every car must belong to this track AND respect inner-to-outer sub-block order!
                    var allowed = assignment.TrackKeys[ts.Track.Track];
                    Assert.NotEmpty(allowed);
                    var ki = 0;
                    foreach (var c in cars)
                    {
                        var idx = allowed.IndexOf(c.Key ?? "");
                        Assert.True(idx >= ki, $"{ts.Track.Name} out of order: saw '{c.Key}' (idx {idx}) after idx {ki}");
                        ki = idx;
                    }
                }
                Assert.Equal(initialIds.Count, finalIds.Count);
                return (pulls, setOuts, consolidations, sluffs);
            }

            Assert.True(action.Kind != SortActionKind.Blocked, $"Blocked at step {step}: {action.Reason}");

            if (action.Kind == SortActionKind.Pull)
            {
                pulls++;
                lastPulled = action.Track;
                if (!action.ClearsTrack)
                    clearingTrack = action.Track;
                else if (clearingTrack == action.Track)
                    clearingTrack = -1;
                consecutivePulls++;
                var ts = byTrack[action.Track];
                Assert.NotEmpty(action.PullCuts);

                // Coupling from outermost cut inward: each cut's outer end meets the switcher's far end.
                // So cars already on the switcher stay nearer the loco, and deeper pulled cars move to the far end!
                foreach (var cut in action.PullCuts)
                {
                    Assert.Same(ts.CutsInnerToOuter[ts.CutsInnerToOuter.Count - 1], cut);
                    ts.CutsInnerToOuter.RemoveAt(ts.CutsInnerToOuter.Count - 1);
                    // Attach cut's cars outer-to-inner onto the far end (index 0) of inTrainFarToNear.
                    for (var i = cut.CarsInnerToOuter.Count - 1; i >= 0; i--)
                        inTrainFarToNear.Insert(0, cut.CarsInnerToOuter[i]);
                }
                // If leaving inner cars behind, cut them off from the far end and place back on ts!
                if (action.LeaveBehindCarIds.Count > 0)
                {
                    var leftCut = new SortCut { Owner = nextOwner++, Track = action.Track };
                    var leftFarToNear = new List<SortCar>();
                    while (inTrainFarToNear.Count > 0 && action.LeaveBehindCarIds.Contains(inTrainFarToNear[0].Id))
                    {
                        leftFarToNear.Add(inTrainFarToNear[0]);
                        inTrainFarToNear.RemoveAt(0);
                    }
                    Assert.Equal(action.LeaveBehindCarIds.Count, leftFarToNear.Count);
                    // Far-most car of the train was innermost on the track!
                    leftCut.CarsInnerToOuter.AddRange(leftFarToNear);
                    ts.CutsInnerToOuter.Add(leftCut);
                }
                // Verify we respected MaxCutLength on pulls (unless single car > MaxCutLength).
                var hookLen = inTrainFarToNear.Sum(c => c.Length);
                if (inTrainFarToNear.Count > 1)
                    Assert.True(hookLen <= assignment.MaxCutLength + 1e-6,
                        $"Pulled {hookLen} m > MaxCutLength {assignment.MaxCutLength} m");
            }
            else if (action.Kind == SortActionKind.SetOut)
            {
                setOuts++;
                if (action.IsSluff)
                    sluffs++;
                consecutivePulls = 0;
                var ts = byTrack[action.Track];
                Assert.True(action.SetOutCount > 0 && action.SetOutCount <= inTrainFarToNear.Count);
                var dropped = inTrainFarToNear.Take(action.SetOutCount).ToList();
                inTrainFarToNear.RemoveRange(0, action.SetOutCount);
                if (inTrainFarToNear.Count == 0)
                    lastPulled = -1;

                // Shoved cars-first into track: far end (dropped[0]) goes deepest (inner), dropped[last] goes outermost!
                if (action.JoinSorted && ts.CutsInnerToOuter.Count > 0)
                {
                    var outerCut = ts.CutsInnerToOuter[ts.CutsInnerToOuter.Count - 1];
                    outerCut.CarsInnerToOuter.AddRange(dropped);
                }
                else
                {
                    var newCut = new SortCut { Owner = nextOwner++, Track = action.Track };
                    newCut.CarsInnerToOuter.AddRange(dropped);
                    ts.CutsInnerToOuter.Add(newCut);
                }
                Assert.True(ts.OccupiedLength <= ts.Track.UsableLength + 0.5,
                    $"Set-out overflowed {ts.Track.Name}: {ts.OccupiedLength} > {ts.Track.UsableLength}");
            }
            else if (action.Kind == SortActionKind.Consolidate)
            {
                consolidations++;
                lastPulled = -1;
                consecutivePulls = 0;
                var ts = byTrack[action.Track];
                Assert.True(ts.CutsInnerToOuter.Count > 1);
                var combined = new SortCut { Owner = nextOwner++, Track = action.Track };
                foreach (var c in ts.CutsInnerToOuter)
                    combined.CarsInnerToOuter.AddRange(c.CarsInnerToOuter);
                ts.CutsInnerToOuter.Clear();
                ts.CutsInnerToOuter.Add(combined);
            }
        }

        var stateDump = string.Join(" | ", tracks.Select(t => $"{t.Track.Name}[{string.Join(",", assignment.TrackKeys.TryGetValue(t.Track.Track, out var ak) ? ak : Array.Empty<string>())}]=({string.Join(",", t.AllCarsInnerToOuter().Select(c => c.Key))})"));
        throw new InvalidOperationException($"Yard sort did not finish within {maxSteps} steps.\nState: {stateDump}\nLast steps:\n{string.Join("\n", log.Skip(Math.Max(0, log.Count - 15)))}");
    }

    // ---- Unit Tests ----

    [Fact]
    public void SpotOnEntryOnlyRespectsChosenLadderEnd()
    {
        var y = new InterchangeYard();
        var t = y.Bowl[0];
        var both = OrderGoals.SpotOn(y.Graph, t, 50, Array.Empty<OccupiedSpan>());
        Assert.Equal(2, both.Count);

        var startOnly = OrderGoals.SpotOn(y.Graph, t, 50, Array.Empty<OccupiedSpan>(), entryOnly: TrackEnd.Start);
        var gs = Assert.Single(startOnly);
        Assert.Equal(TrackDirection.Forward, gs.Arrive);
        Assert.Equal(y.Graph.Length(t) - OrderGoals.ClearOfEntry, gs.Position.Offset, 6);

        var endOnly = OrderGoals.SpotOn(y.Graph, t, 50, Array.Empty<OccupiedSpan>(), entryOnly: TrackEnd.End);
        var ge = Assert.Single(endOnly);
        Assert.Equal(TrackDirection.Backward, ge.Arrive);
        Assert.Equal(OrderGoals.ClearOfEntry, ge.Position.Offset, 6);

        // On an empty 620 m track, maxDepth caps the spot depth near the working end instead of shoving 605 m.
        var capped = OrderGoals.SpotOn(y.Graph, t, 18, Array.Empty<OccupiedSpan>(), entryOnly: TrackEnd.End, maxDepth: 148);
        var gc = Assert.Single(capped);
        Assert.Equal(TrackDirection.Backward, gc.Arrive);
        Assert.Equal(y.Graph.Length(t) - 148, gc.Position.Offset, 6);
    }

    [Fact]
    public void De2WithOneLoadedCarOnSlowAirStopsCleanlyAndUncouplesOnLongInterchangeTrack()
    {
        // Reproduces the live NEI-A1X (671 m) failure: DE2 + 1 loaded CMS car under Realistic Brakes
        // (SlowAir = true, LocoMassShare = 0.35 -> IndependentOnly = true) with weak DE2 independent brake
        // (LocoBrakeDecel = 0.11 m/s²). Must pull out from Bowl[1], reverse on the lead, shove into empty
        // Bowl[0] (671 m), stop cleanly, and uncouple the 1 car with zero Off-the-route loop!
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350, bowlLengths: new[] { 671.0, 593.0, 520.0, 450.0, 380.0 });
        var world = new FakeWorld(y.Graph);
        var t0 = y.Bowl[0];
        var t1 = y.Bowl[1];

        // Consist standing on Bowl[1] near West end (TrackEnd.End = 593 m), EndA = loco toward End, EndB = CMS car toward Start.
        var train = new NetworkTrain(world, new TrackPosition(t1, 579), TrackDirection.Forward, length: 28)
        {
            CarCount = 2,
        };
        train.Physics.LocoBrakeDecel = 0.11;
        train.Physics.TrainBrakeDecel = 0.25;
        train.Physics.ApplyLag = 5;

        var eng = new Engineer();
        // Spot 1 car (18 m) into empty 671 m Bowl[0] from TrackEnd.End with planned depth 148 m (as SortRun does).
        var spot = OrderGoals.SpotOn(y.Graph, t0, cutLength: 18, Array.Empty<OccupiedSpan>(), entryOnly: TrackEnd.End, maxDepth: 148);
        Assert.Single(spot);
        eng.Enqueue(new Waypoint(spot, WaypointAction.Uncouple)
        {
            Label = "NEI-A1X (CMS)",
            CarCount = 1,
            SetHandbrakes = true,
            Arrival = Arrival.CarsFirst,
            ArrivalRequired = true,
            RejectEmptyGoals = true,
        });

        var cutDone = false;
        var offRouteTicks = 0;
        Drive.Run(eng, train, world, maxSeconds: 900,
            state: s =>
            {
                s.PowerCars = 1;
                s.LocoEnd = ConsistEnd.A;
                s.LocoFront = ConsistEnd.A;
                // Realistic DE2 (~38 t) + 1 loaded car (~70 t): LocoMassShare ~0.35 -> uses SlowAir.
                s.LocoMassShare = 0.35;
                s.Controls.HasTrainBrake = true;
                s.Controls.SlowAir = true;
                return s;
            },
            onOutput: o =>
            {
                if (eng.Status != null && eng.Status.StartsWith("Off the route"))
                    offRouteTicks++;
                if (o.Cut.HasValue && !cutDone)
                {
                    cutDone = true;
                    Assert.Equal(1, o.Cut.Value.Cars);
                    Assert.True(o.Cut.Value.SetHandbrakes);
                }
            },
            until: () => eng.Queue.Count == 0);

        Assert.True(cutDone, $"Failed to uncouple; final status: {eng.Status}, EndB: {train.EndB}");
        Assert.Equal(0, offRouteTicks);
        Assert.False(train.Derailed);
        Assert.Equal(t0, train.EndB.Track);
    }

    [Fact]
    public void De2WithVeryWeakIndependentAndFull656mShoveStillStopsInsideTrackAndUncouples()
    {
        // Even with NO maxDepth cap (shoving 656 m to offset 15 m, 15 m before the far switch) and an
        // unusually weak DE2 independent brake (0.07 m/s²) on Realistic Brakes, emergency braking on
        // overshoot must stop the train cleanly inside Bowl[0] and uncouple instead of rolling off the
        // far switch into a 700 m loop!
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350, bowlLengths: new[] { 671.0, 593.0, 520.0, 450.0, 380.0 });
        var world = new FakeWorld(y.Graph);
        var t0 = y.Bowl[0];
        var t1 = y.Bowl[1];

        var train = new NetworkTrain(world, new TrackPosition(t1, 579), TrackDirection.Forward, length: 28)
        {
            CarCount = 2,
        };
        train.Physics.LocoBrakeDecel = 0.07;
        train.Physics.TrainBrakeDecel = 0.25;
        train.Physics.ApplyLag = 5;

        var eng = new Engineer();
        var spot = OrderGoals.SpotOn(y.Graph, t0, cutLength: 18, Array.Empty<OccupiedSpan>(), entryOnly: TrackEnd.End);
        Assert.Single(spot);
        eng.Enqueue(new Waypoint(spot, WaypointAction.Uncouple)
        {
            Label = "NEI-A1X (CMS)",
            CarCount = 1,
            SetHandbrakes = true,
            Arrival = Arrival.CarsFirst,
            ArrivalRequired = true,
            RejectEmptyGoals = true,
        });

        var cutDone = false;
        var offRouteTicks = 0;
        Drive.Run(eng, train, world, maxSeconds: 900,
            state: s =>
            {
                s.PowerCars = 1;
                s.LocoEnd = ConsistEnd.A;
                s.LocoFront = ConsistEnd.A;
                // Even when IndependentOnly is true (LocoMassShare = 0.5) AND the real independent brake
                // is far weaker than expected (0.07 m/s²), PreferIndependent must fall back to air in time!
                s.LocoMassShare = 0.5;
                s.Controls.HasTrainBrake = true;
                s.Controls.SlowAir = true;
                return s;
            },
            onOutput: o =>
            {
                if (eng.Status != null && eng.Status.StartsWith("Off the route"))
                    offRouteTicks++;
                if (o.Cut.HasValue && !cutDone)
                {
                    cutDone = true;
                    Assert.Equal(1, o.Cut.Value.Cars);
                }
            },
            until: () => eng.Queue.Count == 0);

        Assert.True(cutDone, $"Failed to uncouple; final status: {eng.Status}, EndB: {train.EndB}");
        Assert.Equal(0, offRouteTicks);
        Assert.False(train.Derailed);
        Assert.Equal(t0, train.EndB.Track);
    }

    [Fact]
    public void ChooseWorkingEndsPicksNearestAmpleLadderOnInterchangeYard()
    {
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350);
        var world = new FakeWorld(y.Graph);

        // Loco on East lead -> all 5 tracks choose TrackEnd.Start (East ladder).
        var tracksEast = TracksOf(y);
        YardSort.ChooseWorkingEnds(world, world.SelectedBranch, LightLocoAt(y.EastLead, 200), tracksEast, out var eastCut);
        Assert.All(tracksEast, t => Assert.Equal(TrackEnd.Start, t.WorkingEnd));
        Assert.True(eastCut >= 200, $"Expected ample lead capacity on 350 m lead, got {eastCut}");

        // Loco on West lead -> all 5 tracks choose TrackEnd.End (West ladder).
        var tracksWest = TracksOf(y);
        YardSort.ChooseWorkingEnds(world, world.SelectedBranch, LightLocoAt(y.WestLead, 150), tracksWest, out var westCut);
        Assert.All(tracksWest, t => Assert.Equal(TrackEnd.End, t.WorkingEnd));
        Assert.True(westCut >= 200, $"Expected ample lead capacity on 350 m lead, got {westCut}");
    }

    [Fact]
    public void ChooseWorkingEndsSwitchesToUnblockedLadderWhenNearLeadIsBlocked()
    {
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350);
        var world = new FakeWorld(y.Graph);
        // Block the East lead right outside the east ladder!
        world.Occupancy.Add(y.EastLead, new OccupiedSpan(320, 345, owner: 99));

        var tracks = TracksOf(y);
        YardSort.ChooseWorkingEnds(world, world.SelectedBranch, LightLocoAt(y.EastLead, 100), tracks, out var cutLen);
        // Must pick West ladder (TrackEnd.End) because East lead is blocked!
        Assert.All(tracks, t => Assert.Equal(TrackEnd.End, t.WorkingEnd));
        Assert.True(cutLen >= 200);
    }

    [Fact]
    public void ChooseWorkingEndsMeasuresShortStubLeadAccurately()
    {
        var y = new StubYard(leadLen: 120, bowlLen: 300);
        var world = new FakeWorld(y.Graph);
        var tracks = TracksOf(y);
        YardSort.ChooseWorkingEnds(world, world.SelectedBranch, LightLocoAt(y.Lead, 50), tracks, out var cutLen);
        Assert.All(tracks, t => Assert.Equal(TrackEnd.Start, t.WorkingEnd));
        Assert.All(tracks, t => Assert.True(t.DeadEnd));
        // 120 m lead minus 15 m clearance minus 15 m loco minus 15 m safety margin => roughly 60-85 m of cars.
        Assert.InRange(cutLen, 40, 95);
    }

    [Fact]
    public void AssignPrefersEmptyCleanTracksOverSingleAnchorAndPreservesLargeAnchors()
    {
        // T0 has 1 anchored HB + 3 other cars (dirty); T1 has 5 anchored SM + 1 other car (dirty); T2, T3 empty.
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 400, false, (10, new[] { "HB", "GF", "MF", "GF" })),
            MakeTrack(1, "WI-A2X", 400, false, (11, new[] { "SM", "SM", "SM", "SM", "SM", "HB" })),
            MakeTrack(2, "WI-A3X", 400, false),
            MakeTrack(3, "WI-A4X", 400, false),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        // SM has 5 anchored cars on T1 (score 5.0 > empty 2.0), so SM MUST stay on T1!
        Assert.Equal(1, plan.KeyToTrack["SM"]);
        // HB only had 1 anchored car on dirty T0 (score 1.0 < empty 2.0), so empty T2/T3 are preferred for immediately-clean sorting!
        Assert.True(plan.KeyToTrack["HB"] == 2 || plan.KeyToTrack["HB"] == 3 || plan.KeyToTrack["GF"] == 2 || plan.KeyToTrack["GF"] == 3);
    }

    [Fact]
    public void AssignClustersNearestCorridorsWhenDestinationsExceedTracks()
    {
        // 7 destinations on 5 tracks: GF & FF are 2 km apart, IME & NEI are 0.5 km apart; others > 8 km apart.
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 600, false, (10, new[] { "HB", "SM", "GF", "FF", "MF", "IME", "NEI", "HB", "GF", "IME" })),
            MakeTrack(1, "WI-A2X", 550, false, (11, new[] { "SM", "MF", "FF", "NEI", "", "" })),
            MakeTrack(2, "WI-A3X", 480, false),
            MakeTrack(3, "WI-A4X", 420, false),
            MakeTrack(4, "WI-A5X", 350, false),
        };
        var coords = new Dictionary<string, (double X, double Y)>
        {
            ["HB"] = (0, -10000),
            ["SM"] = (2000, -7000),
            ["MF"] = (0, 500),
            ["GF"] = (-8000, -2000),
            ["FF"] = (-9500, -1500),
            ["IME"] = (12000, 6000),
            ["NEI"] = (12400, 6200),
        };
        double KeyDist(string a, string b)
        {
            var ca = coords[a];
            var cb = coords[b];
            return Math.Sqrt((ca.X - cb.X) * (ca.X - cb.X) + (ca.Y - cb.Y) * (ca.Y - cb.Y));
        }
        double OriginDist(string a)
        {
            var c = coords[a];
            return Math.Sqrt(c.X * c.X + c.Y * c.Y);
        }

        // 8 keys (7 destinations + NoJob "") -> must cluster down to 5 tracks without merging NoJob with a destination!
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>(), KeyDist, OriginDist);
        Assert.True(plan.Valid);
        // IME and NEI must share a track (NEI farther from origin -> inner, IME outer!).
        Assert.Equal(plan.KeyToTrack["IME"], plan.KeyToTrack["NEI"]);
        var neiTrack = plan.TrackKeys[plan.KeyToTrack["NEI"]];
        Assert.True(neiTrack.IndexOf("NEI") < neiTrack.IndexOf("IME"));
        // GF and FF must share a track (FF farther -> inner, GF outer!).
        Assert.Equal(plan.KeyToTrack["GF"], plan.KeyToTrack["FF"]);
        // NoJob ("") stays on its own track (not merged with a destination!).
        var noJobTrack = plan.TrackKeys[plan.KeyToTrack[""]];
        Assert.Single(noJobTrack);

        // Simulate sorting all 16 cars across the 5 tracks and verify exact sub-block ordering!
        plan.MaxCutLength = 180;
        SimulateSort(plan, tracks, new List<SortCar>());
    }

    [Fact]
    public void ClassicInterchangeYardSortsWithZeroWastedSluffs()
    {
        // 5-track interchange yard: 18 mixed cars on T0 & T1 for 4 destinations, T2-T4 empty.
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 620, false, (10, new[] { "HB", "SM", "GF", "HB", "SM", "MF", "GF", "HB", "SM", "MF" })),
            MakeTrack(1, "WI-A2X", 552, false, (11, new[] { "GF", "HB", "MF", "SM", "HB", "GF", "SM", "MF" })),
            MakeTrack(2, "WI-A3X", 485, false),
            MakeTrack(3, "WI-A4X", 417, false),
            MakeTrack(4, "WI-A5X", 352, false),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        Assert.False(plan.AlreadySorted);
        plan.MaxCutLength = 220;

        var (pulls, setOuts, _, sluffs) = SimulateSort(plan, tracks, new List<SortCar>());
        // With 3 empty tracks assigned to 3 destinations and 1 track cleared first, ZERO sluffs!
        Assert.Equal(0, sluffs);
        Assert.True(pulls <= 2, $"Expected at most 2 pulls, took {pulls}");
        Assert.True(setOuts <= 18);
    }

    [Fact]
    public void TwoTrackFullCycleWithZeroSpareTracksSortsViaHookSwap()
    {
        // T0 (assigned SM) has [SM, HB]; T1 (assigned HB) has [HB, SM]; NO spare track!
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "T1", 300, true, (10, new[] { "SM", "HB" })),
            MakeTrack(1, "T2", 300, true, (11, new[] { "HB", "SM" })),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        plan.MaxCutLength = 120;

        var (pulls, setOuts, _, sluffs) = SimulateSort(plan, tracks, new List<SortCar>());
        Assert.Equal(0, sluffs);
        Assert.Equal(2, pulls);
        Assert.Equal(2, setOuts);
    }

    [Fact]
    public void AllFiveTracksDirtyWithShortLeadConvergesWithoutDeadlock()
    {
        // Hardest case: ALL 5 tracks dirty, 5 destinations (0 spare tracks), 30 scrambled cars,
        // AND short switching lead (MaxCutLength = 75 m => at most 5 cars per pull!).
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 350, false, (10, new[] { "SM", "GF", "MF", "HB", "NEI", "SM" })),
            MakeTrack(1, "WI-A2X", 350, false, (11, new[] { "GF", "HB", "NEI", "SM", "MF", "GF" })),
            MakeTrack(2, "WI-A3X", 350, false, (12, new[] { "MF", "NEI", "HB", "GF", "SM", "MF" })),
            MakeTrack(3, "WI-A4X", 350, false, (13, new[] { "NEI", "SM", "MF", "GF", "HB", "NEI" })),
            MakeTrack(4, "WI-A5X", 350, false, (14, new[] { "HB", "MF", "SM", "NEI", "GF", "HB" })),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        plan.MaxCutLength = 75; // ~5 cars max per pull!

        var (pulls, setOuts, _, _) = SimulateSort(plan, tracks, new List<SortCar>(), maxSteps: 200);
        Assert.True(pulls > 0 && setOuts > 0);
    }

    [Fact]
    public void SwitcherStartingWithMixedCarsOnHookClassifiesThemDirectly()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 400, false, (10, new[] { "HB", "SM" })),
            MakeTrack(1, "WI-A2X", 400, false),
            MakeTrack(2, "WI-A3X", 400, false),
        };
        var onHook = new List<SortCar>
        {
            Car(9001, "GF"), Car(9002, "HB"), Car(9003, "SM"), Car(9004, "GF"),
        };
        var plan = YardSort.Assign(tracks, onHook);
        Assert.True(plan.Valid);
        Assert.Equal(6, plan.TotalCars);
        plan.MaxCutLength = 180;

        SimulateSort(plan, tracks, onHook);
    }

    [Fact]
    public void SeparatedCutsOfSameDestinationAreConsolidatedIntoOneCut()
    {
        // Already on the right tracks, but T0 has 2 separated HB cuts!
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 400, false, (10, new[] { "HB", "HB" }), (11, new[] { "HB", "HB" })),
            MakeTrack(1, "WI-A2X", 400, false, (12, new[] { "SM", "SM" })),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        Assert.False(plan.AlreadySorted); // Not yet consolidated!

        var (pulls, setOuts, consolidations, _) = SimulateSort(plan, tracks, new List<SortCar>());
        Assert.Equal(0, pulls);
        Assert.Equal(0, setOuts);
        Assert.Equal(1, consolidations);
    }

    [Fact]
    public void AlreadySortedYardIsDetectedImmediately()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 400, false, (10, new[] { "HB", "HB", "HB" })),
            MakeTrack(1, "WI-A2X", 400, false, (11, new[] { "SM", "SM" })),
            MakeTrack(2, "WI-A3X", 400, false),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        Assert.True(plan.AlreadySorted);
    }

    [Fact]
    public void BlockedTrackWithAnotherLocoIsSkippedCleanly()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "WI-A1X", 400, false, (10, new[] { "HB", "SM", "HB" })),
            MakeTrack(1, "WI-A2X", 400, false),
            MakeTrack(2, "WI-A3X", 400, false),
        };
        tracks[2].Track.Blocked = true;
        tracks[2].Track.BlockedReason = "loco L-012 standing on it";

        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        Assert.False(plan.TrackKeys.ContainsKey(2));
        Assert.True(plan.Lines[2].Blocked);
        plan.MaxCutLength = 150;

        SimulateSort(plan, tracks, new List<SortCar>());
    }

    [Fact]
    public void OversizedDestinationSplitsCleanlyAcrossTwoTracks()
    {
        // 16 HB cars (224 m) + 4 SM cars (56 m) on three 170 m tracks (usable 140 m each).
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "Y1", 170, false, (10, new[] { "HB", "SM", "HB", "HB", "SM", "HB", "HB", "HB" })),
            MakeTrack(1, "Y2", 170, false, (11, new[] { "HB", "HB", "SM", "HB", "HB", "HB", "SM", "HB" })),
            MakeTrack(2, "Y3", 170, false, (12, new[] { "HB", "HB", "HB", "HB" })),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
        Assert.Single(plan.KeyTracks["SM"]);
        plan.MaxCutLength = 120;

        SimulateSort(plan, tracks, new List<SortCar>());
    }

    [Fact]
    public void ReorientTrackFlipsCutsAndCarsWhenWorkingEndChanges()
    {
        var ts = MakeTrack(0, "WI-A1X", 400, false,
            (10, new[] { "HB", "SM" }),   // inner cut
            (11, new[] { "GF", "MF" }));  // outer cut
        ts.Track.WorkingEnd = TrackEnd.End;
        YardSort.ReorientTrack(ts);

        Assert.Equal(11, ts.CutsInnerToOuter[0].Owner);
        Assert.Equal(new[] { "MF", "GF" }, ts.CutsInnerToOuter[0].CarsInnerToOuter.Select(c => c.Key));
        Assert.Equal(10, ts.CutsInnerToOuter[1].Owner);
        Assert.Equal(new[] { "SM", "HB" }, ts.CutsInnerToOuter[1].CarsInnerToOuter.Select(c => c.Key));
    }

    [Fact]
    public void EngineerDrivesPullAndCarsFirstSetOutOnInterchangeYardGraph()
    {
        // Full physics + Engineer + NetworkTrain test on our 5-track interchange yard:
        // Light loco on EastLead couples to a 2-car cut on Bowl[0], pulls out onto EastLead,
        // and shoves cars-first into Bowl[2] via WorkingEnd (TrackEnd.Start)!
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350);
        var world = new FakeWorld(y.Graph);
        var t0 = y.Bowl[0];
        var t2 = y.Bowl[2];

        // Standing 2-car cut on Bowl[0] at 80..110 m.
        const int cutOwner = 20;
        world.Occupancy.Add(t0, new OccupiedSpan(80, 110, cutOwner));
        var cutEnd = new CutEnd(new TrackPosition(t0, 80), TrackDirection.Backward);

        // Light loco on EastLead at 220..235 m, EndA toward the yard (Forward).
        var train = new NetworkTrain(world, new TrackPosition(y.EastLead, 235), TrackDirection.Forward, length: 15)
        {
            CarCount = 1,
        };
        var eng = new Engineer();

        // 1. Couple to the cut on Bowl[0].
        eng.Enqueue(new Waypoint(OrderGoals.ToCouple(y.Graph, new[] { cutEnd }, world.SelectedBranch), WaypointAction.Couple)
        {
            Label = "2 cars on WI-A1X",
            Arrival = Arrival.Any,
        });

        var coupled = false;
        Drive.Run(eng, train, world, maxSeconds: 600,
            state: s => { s.PowerCars = 1; s.LocoEnd = ConsistEnd.B; s.LocoFront = ConsistEnd.A; return s; },
            onOutput: o =>
            {
                if (o.CoupleAt.HasValue && !coupled)
                {
                    coupled = true;
                    world.Occupancy.Clear();
                    train.CoupleOn(o.CoupleAt.Value, length: 30, cars: 2);
                }
            },
            until: () => eng.Queue.Count == 0);

        Assert.True(coupled);
        Assert.Equal(3, train.CarCount);
        Assert.False(train.Derailed);

        // 2. Shove cars-first onto Bowl[2] from WorkingEnd (TrackEnd.Start) and uncouple 2 cars!
        var spotGoals = OrderGoals.SpotOn(y.Graph, t2, cutLength: 30, Array.Empty<OccupiedSpan>(), entryOnly: TrackEnd.Start);
        Assert.Single(spotGoals);
        eng.Enqueue(new Waypoint(spotGoals, WaypointAction.Uncouple)
        {
            Label = "WI-A3X (HB)",
            CarCount = 2,
            SetHandbrakes = true,
            Arrival = Arrival.CarsFirst,
            ArrivalRequired = true,
        });

        var cutDone = false;
        Drive.Run(eng, train, world, maxSeconds: 900,
            state: s => { s.PowerCars = 1; s.LocoEnd = ConsistEnd.B; s.LocoFront = ConsistEnd.A; return s; },
            onOutput: o =>
            {
                if (o.Cut.HasValue && !cutDone)
                {
                    cutDone = true;
                    Assert.Equal(2, o.Cut.Value.Cars);
                    Assert.True(o.Cut.Value.SetHandbrakes);
                }
            },
            until: () => eng.Queue.Count == 0);

        Assert.True(cutDone);
        Assert.False(train.Derailed);
        Assert.Equal(0, world.ThrownUnderTheTrain);
        // Leading end (cars) must be deep inside Bowl[2], clear of both ends!
        Assert.Equal(t2, train.EndA.Track);
        Assert.InRange(train.EndA.Offset, y.Graph.Length(t2) - 40, y.Graph.Length(t2) - 5);
    }

    [Fact]
    public void RandomizedScrambledYardsAlwaysConvergeCleanly()
    {
        var keys = new[] { "HB", "SM", "GF", "FF", "MF", "IME", "NEI", "CW", "" };
        for (var seed = 1; seed <= 250; seed++)
        {
            RunRandomizedSeed(seed, keys);
        }
    }

    [Fact]
    public void Seed140ConvergesWithoutCycling()
    {
        var keys = new[] { "HB", "SM", "GF", "FF", "MF", "IME", "NEI", "CW", "" };
        RunRandomizedSeed(140, keys);
    }

    [Fact]
    public void Seed3569ConvergesWithoutStoppingEarly()
    {
        var keys = new[] { "HB", "SM", "GF", "FF", "MF", "IME", "NEI", "CW", "" };
        RunRandomizedSeed(3569, keys);
    }

    [Fact]
    public void ExcludedShortTrackWithForeignKeyDoesNotBlockSortingRestOfYard()
    {
        // Two 160 m tracks holding mixed A/B cuts + one 50 m double-ended track (UsableLength = 20 < 25) holding C.
        // Assign excludes the short track; NextAction/SimulateSort must ignore C and finish A/B cleanly!
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "T1", 160, false, (10, new[] { "A", "B", "A" })),
            MakeTrack(1, "T2", 160, false, (11, new[] { "B", "A", "B" })),
            MakeTrack(2, "SHORT", 50, false, (12, new[] { "C" })),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.False(plan.TrackKeys.ContainsKey(2));
        Assert.False(plan.WorkingEnds.ContainsKey(2));
        plan.MaxCutLength = 120;

        // SimulateSort checks participating tracks (exclude the short track from the final car-id/sort assertion by filtering).
        var participating = tracks.Where(t => plan.TrackKeys.ContainsKey(t.Track.Track)).ToList();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int lastPulled = -1, clearingTrack = -1, consecutivePulls = 0;
        var inTrain = new List<SortCar>();
        var byTrack = tracks.ToDictionary(t => t.Track.Track);
        var done = false;
        for (var step = 0; step < 100; step++)
        {
            // Pass ALL tracks (including the short excluded track with C!) to NextAction every step!
            var action = YardSort.NextAction(plan, tracks, inTrain, lastPulled, consecutivePulls, clearingTrack: clearingTrack, visitedMoves: visited);
            Assert.NotEqual(SortActionKind.Blocked, action.Kind);
            if (action.Kind == SortActionKind.Done)
            {
                done = true;
                break;
            }
            if (action.Kind == SortActionKind.Pull)
            {
                lastPulled = action.Track;
                clearingTrack = action.ClearsTrack ? -1 : action.Track;
                consecutivePulls++;
                var ts = byTrack[action.Track];
                foreach (var cut in action.PullCuts)
                {
                    ts.CutsInnerToOuter.RemoveAt(ts.CutsInnerToOuter.Count - 1);
                    for (var i = cut.CarsInnerToOuter.Count - 1; i >= 0; i--)
                        inTrain.Insert(0, cut.CarsInnerToOuter[i]);
                }
                if (action.LeaveBehindCarIds.Count > 0)
                {
                    var left = new SortCut { Owner = 7000 + step, Track = action.Track };
                    while (inTrain.Count > 0 && action.LeaveBehindCarIds.Contains(inTrain[0].Id))
                    {
                        left.CarsInnerToOuter.Add(inTrain[0]);
                        inTrain.RemoveAt(0);
                    }
                    ts.CutsInnerToOuter.Add(left);
                }
            }
            else if (action.Kind == SortActionKind.SetOut)
            {
                consecutivePulls = 0;
                var dropped = inTrain.Take(action.SetOutCount).ToList();
                inTrain.RemoveRange(0, action.SetOutCount);
                if (inTrain.Count == 0) lastPulled = -1;
                var ts = byTrack[action.Track];
                if (action.JoinSorted && ts.CutsInnerToOuter.Count > 0)
                    ts.CutsInnerToOuter[ts.CutsInnerToOuter.Count - 1].CarsInnerToOuter.AddRange(dropped);
                else
                {
                    var nc = new SortCut { Owner = 8000 + step, Track = action.Track };
                    nc.CarsInnerToOuter.AddRange(dropped);
                    ts.CutsInnerToOuter.Add(nc);
                }
            }
            else if (action.Kind == SortActionKind.Consolidate)
            {
                lastPulled = -1;
                consecutivePulls = 0;
                var ts = byTrack[action.Track];
                var comb = new SortCut { Owner = 9000 + step, Track = action.Track };
                foreach (var c in ts.CutsInnerToOuter)
                    comb.CarsInnerToOuter.AddRange(c.CarsInnerToOuter);
                ts.CutsInnerToOuter.Clear();
                ts.CutsInnerToOuter.Add(comb);
            }
        }
        Assert.True(done);
        Assert.Single(tracks[2].AllCarsInnerToOuter()); // Short track's C car untouched!
    }

    [Fact]
    public void AssignExactWholeCarBacktrackingPacksIrregularLengthsIntoTwoTracks()
    {
        // Two double-ended tracks with 40 m effective planning capacity (UsableLength = 46 m => Length = 76 m).
        // One destination with car lengths 24, 20, 12, 8, 8, 8 m ONLY fits as (24+8+8=40) and (20+12+8=40).
        // First-fit decreasing creates 3 bins (24+12, 20+8+8, 8); exact backtracking must pack into 2!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 76, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 76, WorkingEnd = TrackEnd.Start } };
        var cut0 = new SortCut { Owner = 10, Track = 0 };
        cut0.CarsInnerToOuter.AddRange(new[] { Car(1, "HB", 24), Car(2, "HB", 12), Car(3, "HB", 8) });
        var cut1 = new SortCut { Owner = 11, Track = 1 };
        cut1.CarsInnerToOuter.AddRange(new[] { Car(4, "HB", 20), Car(5, "HB", 8), Car(6, "HB", 8) });
        t0.CutsInnerToOuter.Add(cut0);
        t1.CutsInnerToOuter.Add(cut1);

        var plan = YardSort.Assign(new List<YardTrackState> { t0, t1 }, Array.Empty<SortCar>());
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
    }

    [Fact]
    public void AssignExactWholeCarBacktrackingPacksUnequalCapacityTracks()
    {
        // Two double-ended tracks with unequal planning capacities: 24 m (Length = 60) and 20 m (Length = 56).
        // Cars 18, 8, 8, 8 m ONLY fit as (8+8+8=24) in the 24 m track and (18) in the 20 m track!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 60, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 56, WorkingEnd = TrackEnd.Start } };
        var cut0 = new SortCut { Owner = 10, Track = 0 };
        cut0.CarsInnerToOuter.AddRange(new[] { Car(1, "HB", 18), Car(2, "HB", 8), Car(3, "HB", 8), Car(4, "HB", 8) });
        t0.CutsInnerToOuter.Add(cut0);

        var plan = YardSort.Assign(new List<YardTrackState> { t0, t1 }, Array.Empty<SortCar>());
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
    }

    [Fact]
    public void OverflowPartialSetOutPreservesFeasiblePackingForRemainingHookCars()
    {
        // Two empty double-ended 106 m tracks (UsableLength = 76 m, planning cap = 70 m each).
        // Hook ordered [20, 20, 20, 20, 30, 30] (140 m total): greedy 60+50 strands the final 30 m car;
        // feasibility-preserving drop must place (20+20+30=70) and (20+20+30=70) without stranding!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 106, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 106, WorkingEnd = TrackEnd.Start } };
        var onHook = new List<SortCar>
        {
            Car(1, "HB", 20), Car(2, "HB", 20), Car(3, "HB", 20),
            Car(4, "HB", 20), Car(5, "HB", 30), Car(6, "HB", 30),
        };
        var tracks = new List<YardTrackState> { t0, t1 };
        var plan = YardSort.Assign(tracks, onHook);
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
        plan.MaxCutLength = 160;

        SimulateSort(plan, tracks, onHook, maxSteps: 50);
    }

    [Fact]
    public void OverflowUnequalTracksTriesOtherReadyTrackBeforeCommitting()
    {
        // Two empty double-ended tracks: 84 m (Usable 54 m, cap 48 m) and 72 m (Usable 42 m, cap 36 m).
        // Hook ordered [12, 24, 24, 24] m (same key): dropping 12+24 on the 84 m track strands the last 24 m car;
        // NextAction must try the other ready assigned track (72 m) to drop 12+24 there, then 24+24 on the 84 m track!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 84, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 72, WorkingEnd = TrackEnd.Start } };
        var onHook = new List<SortCar>
        {
            Car(1, "HB", 12), Car(2, "HB", 24), Car(3, "HB", 24), Car(4, "HB", 24),
        };
        var tracks = new List<YardTrackState> { t0, t1 };
        var plan = YardSort.Assign(tracks, onHook);
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
        plan.MaxCutLength = 120;

        SimulateSort(plan, tracks, onHook, maxSteps: 50);
    }

    [Fact]
    public void OverflowWithPartiallyDirtyAssignedTrackHookSwapsCleanlyFromLoadedHook()
    {
        // Three double-ended tracks: T0 (84 m, empty), T1 (72 m, holds 12 m SM), T2 (56 m, holds 8 m SM).
        // Hook starts loaded with HB cars [12, 24, 24, 24] m (84 m). Assign gives HB -> T0+T1, SM -> T2.
        // Case 1B must clear dirty assigned track T1 (12 m SM -> 96 m <= 160 m lead) even on the first move
        // (consecutivePulls == 0), set out SM on T2, then drop 12+24 on T1 and 24+24 on T0 to reach Done!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 84, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 72, WorkingEnd = TrackEnd.Start } };
        var t2 = new YardTrackState { Track = new SortTrack { Track = 2, Name = "T3", Length = 56, WorkingEnd = TrackEnd.Start } };
        var c1 = new SortCut { Owner = 10, Track = 1 };
        c1.CarsInnerToOuter.Add(Car(101, "SM", 12));
        t1.CutsInnerToOuter.Add(c1);
        var c2 = new SortCut { Owner = 11, Track = 2 };
        c2.CarsInnerToOuter.Add(Car(102, "SM", 8));
        t2.CutsInnerToOuter.Add(c2);

        var onHook = new List<SortCar>
        {
            Car(1, "HB", 12), Car(2, "HB", 24), Car(3, "HB", 24), Car(4, "HB", 24),
        };
        var tracks = new List<YardTrackState> { t0, t1, t2 };
        var plan = YardSort.Assign(tracks, onHook);
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
        plan.MaxCutLength = 160;

        SimulateSort(plan, tracks, onHook, maxSteps: 50);
    }

    [Fact]
    public void AssignExcludesBlockedTrackFromWorkingEnds()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "T1", 300, false, (10, new[] { "HB", "SM" })),
            MakeTrack(1, "T2", 300, false),
            MakeTrack(2, "ISO", 300, false, (11, new[] { "GF" })),
        };
        tracks[2].Track.Blocked = true;
        tracks[2].Track.BlockedReason = "unreachable from the working ladder";

        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        Assert.False(plan.WorkingEnds.ContainsKey(2));
        Assert.False(plan.TrackKeys.ContainsKey(2));
    }

    [Fact]
    public void StagingCycleDetectionUsesExactCarCountForNonStandardLengths()
    {
        // Two 20 m cars of HB on the hook (40 m total): staging records count 2 ("|S2:2"), NOT Round(40/14)=3.
        // Calling NextAction again with the exact same state & visitedMoves must NOT pick the same track+count again!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 300, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 300, WorkingEnd = TrackEnd.Start } };
        var t2 = new YardTrackState { Track = new SortTrack { Track = 2, Name = "T3", Length = 300, WorkingEnd = TrackEnd.Start } };
        var c0 = new SortCut { Owner = 10, Track = 0 };
        c0.CarsInnerToOuter.AddRange(new[] { Car(1, "SM", 20), Car(2, "HB", 20) });
        t0.CutsInnerToOuter.Add(c0);
        var onHook = new List<SortCar> { Car(3, "HB", 20), Car(4, "HB", 20) };

        var tracks = new List<YardTrackState> { t0, t1, t2 };
        var plan = YardSort.Assign(tracks, onHook);
        Assert.True(plan.Valid);

        // Make HB's target track dirty with SM so HB must stage on spare track.
        var hbTrack = plan.KeyToTrack["HB"];
        var dirtyTrack = tracks.First(t => t.Track.Track == hbTrack);
        if (dirtyTrack.TotalCars == 0)
        {
            var d = new SortCut { Owner = 11, Track = hbTrack };
            d.CarsInnerToOuter.Add(Car(99, "SM", 20));
            dirtyTrack.CutsInnerToOuter.Add(d);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var first = YardSort.NextAction(plan, tracks, onHook, lastPulledTrack: hbTrack, visitedMoves: visited);
        Assert.Equal(SortActionKind.SetOut, first.Kind);
        Assert.True(first.IsSluff);
        Assert.Equal(2, first.SetOutCount);

        // Repeating from the exact same state must NOT repeat the same (track, count=2) staging move!
        var second = YardSort.NextAction(plan, tracks, onHook, lastPulledTrack: hbTrack, visitedMoves: visited);
        Assert.False(second.Kind == SortActionKind.SetOut && second.Track == first.Track && second.SetOutCount == first.SetOutCount,
            "Exact count=2 staging move was repeated because lookup used a length-rounded key");
    }

    [Fact]
    public void SingleCarExceedingMaxCutLengthBlocksInsteadOfViolatingLead()
    {
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 300, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 300, WorkingEnd = TrackEnd.Start } };
        var c0 = new SortCut { Owner = 10, Track = 0 };
        c0.CarsInnerToOuter.AddRange(new[] { Car(1, "HB", 35), Car(2, "SM", 35) });
        t0.CutsInnerToOuter.Add(c0);

        var tracks = new List<YardTrackState> { t0, t1 };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);
        plan.MaxCutLength = 25; // Shorter than a single 35 m car!

        var action = YardSort.NextAction(plan, tracks, Array.Empty<SortCar>());
        Assert.Equal(SortActionKind.Blocked, action.Kind);
        Assert.Contains("35 m", action.Reason);
    }

    [Fact]
    public void WaypointRejectEmptyGoalsPreventsPlanningTowardStaleGoal()
    {
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350);
        var world = new FakeWorld(y.Graph);
        var train = new NetworkTrain(world, new TrackPosition(y.EastLead, 235), TrackDirection.Forward, length: 30)
        {
            CarCount = 2,
        };
        var eng = new Engineer();
        var staleGoal = new WaypointGoal(new TrackPosition(y.Bowl[0], 300), TrackDirection.Forward);
        var wp = new Waypoint(new[] { staleGoal }, WaypointAction.Uncouple)
        {
            Label = "WI-A1X",
            CarCount = 1,
            Arrival = Arrival.CarsFirst,
            ArrivalRequired = true,
            RejectEmptyGoals = true,
            GoalsFor = _ => Array.Empty<WaypointGoal>(),
        };
        eng.Enqueue(wp);

        var s = train.State();
        s.PowerCars = 1;
        s.LocoEnd = ConsistEnd.B;
        var output = eng.Tick(0.2, s, world);
        Assert.Null(eng.Route);
        Assert.Equal(0, output.Controls.Throttle);
    }

    private static void RunRandomizedSeed(int seed, string[] keys)
    {
        var rng = new Random(seed * 7919);
        var trackCount = rng.Next(3, 6); // 3 to 5 tracks (interchange & classification yards)
        var destCount = rng.Next(2, Math.Min(keys.Length, trackCount + 3)); // often > trackCount!
        var activeKeys = keys.Take(destCount).ToArray();
        var trackLen = rng.Next(240, 480);

        var tracks = new List<YardTrackState>();
        var nextId = 1;
        var nextOwner = 100;
        var totalBudget = (int)((trackLen - 40) * trackCount * 0.55 / 14.0);
        var carCounts = new int[trackCount];
        for (var c = 0; c < totalBudget; c++)
            carCounts[rng.Next(trackCount)]++;

        for (var t = 0; t < trackCount; t++)
        {
            var ts = new YardTrackState
            {
                Track = new SortTrack { Track = t, Name = $"T{t + 1}", Length = trackLen, WorkingEnd = TrackEnd.Start, DeadEnd = t % 2 == 0 },
            };
            if (carCounts[t] > 0)
            {
                var cut = new SortCut { Owner = nextOwner++, Track = t };
                for (var c = 0; c < carCounts[t]; c++)
                    cut.CarsInnerToOuter.Add(Car(nextId++, activeKeys[rng.Next(activeKeys.Length)]));
                ts.CutsInnerToOuter.Add(cut);
            }
            tracks.Add(ts);
        }

        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        if (!plan.Valid || plan.AlreadySorted)
            return;
        plan.MaxCutLength = rng.Next(65, 220);
        SimulateSort(plan, tracks, new List<SortCar>(), maxSteps: 400);
    }

    [Fact]
    public void NewlyBlockedDestinationReturnsBlockedNeedsReplanInsteadOfThrowing()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "T1", 300, false),
            MakeTrack(1, "T2", 300, false, (10, new[] { "SM" })),
        };
        var onHook = new List<SortCar> { Car(9001, "HB") };
        var plan = YardSort.Assign(tracks, onHook);
        Assert.True(plan.Valid);

        // Destination track for HB becomes blocked mid-run (e.g. another loco parks on it).
        var hbTrack = plan.KeyToTrack["HB"];
        tracks.First(t => t.Track.Track == hbTrack).Track.Blocked = true;
        tracks.First(t => t.Track.Track == hbTrack).Track.BlockedReason = "loco L-099 standing on it";

        var action = YardSort.NextAction(plan, tracks, onHook);
        Assert.Equal(SortActionKind.Blocked, action.Kind);
        Assert.True(action.NeedsReplan);
        Assert.Contains("T", action.Reason);
    }

    [Fact]
    public void NewlyBlockedDirtyTrackDoesNotFalselyReportDone()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "T1", 300, false, (10, new[] { "HB", "SM" })),
            MakeTrack(1, "T2", 300, false),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);

        // Mark dirty T0 blocked while hook is empty -> must NOT return Done!
        tracks[0].Track.Blocked = true;
        tracks[0].Track.BlockedReason = "derailed car on it";

        var action = YardSort.NextAction(plan, tracks, Array.Empty<SortCar>());
        Assert.Equal(SortActionKind.Blocked, action.Kind);
        Assert.True(action.NeedsReplan);
    }

    [Fact]
    public void AssignBacktracksToFitFourKeysIntoTwoEqualTracks()
    {
        // Two tracks with 100 m effective capacity (UsableLength = 106 m => EffectiveCap = 100 m; track length = 136 m double-ended).
        // Four distinct keys totaling 60, 60, 40, and 40 m ONLY fit as (60+40) and (60+40).
        // A greedy merge that pairs 40+40 fails; backtracking must find the feasible 60+40 + 60+40 packing.
        var t0 = new YardTrackState
        {
            Track = new SortTrack { Track = 0, Name = "T1", Length = 136, WorkingEnd = TrackEnd.Start, DeadEnd = false },
        };
        var t1 = new YardTrackState
        {
            Track = new SortTrack { Track = 1, Name = "T2", Length = 136, WorkingEnd = TrackEnd.Start, DeadEnd = false },
        };
        var cut0 = new SortCut { Owner = 10, Track = 0 };
        cut0.CarsInnerToOuter.AddRange(new[]
        {
            Car(1, "A", 20), Car(2, "A", 20), Car(3, "A", 20), // 60 m
            Car(4, "C", 20), Car(5, "C", 20),                   // 40 m
        });
        t0.CutsInnerToOuter.Add(cut0);

        var cut1 = new SortCut { Owner = 11, Track = 1 };
        cut1.CarsInnerToOuter.AddRange(new[]
        {
            Car(6, "B", 20), Car(7, "B", 20), Car(8, "B", 20), // 60 m
            Car(9, "D", 20), Car(10, "D", 20),                  // 40 m
        });
        t1.CutsInnerToOuter.Add(cut1);

        // Give C and D zero distance so greedy clustering strongly wants to merge C+D (40+40) first!
        double KeyDist(string a, string b) => (a == "C" && b == "D") || (a == "D" && b == "C") ? 10 : 5000;
        var tracks = new List<YardTrackState> { t0, t1 };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>(), KeyDist);
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.NotEqual(plan.KeyToTrack["A"], plan.KeyToTrack["B"]);
        Assert.NotEqual(plan.KeyToTrack["C"], plan.KeyToTrack["D"]);
    }

    [Fact]
    public void AssignPacksTenNineteenMeterCarsAcrossTwoHundredMeterTracks()
    {
        // Two tracks with 100 m effective capacity (Length = 136 m double-ended => UsableLength = 106, Effective = 100).
        // Ten 19 m cars of one key (190 m total) fit 5+5 (95 m each) on two tracks!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 136, WorkingEnd = TrackEnd.Start } };
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 136, WorkingEnd = TrackEnd.Start } };
        var cut0 = new SortCut { Owner = 10, Track = 0 };
        var cut1 = new SortCut { Owner = 11, Track = 1 };
        for (var i = 1; i <= 5; i++)
            cut0.CarsInnerToOuter.Add(Car(i, "HB", 19));
        for (var i = 6; i <= 10; i++)
            cut1.CarsInnerToOuter.Add(Car(i, "HB", 19));
        t0.CutsInnerToOuter.Add(cut0);
        t1.CutsInnerToOuter.Add(cut1);

        var tracks = new List<YardTrackState> { t0, t1 };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["HB"].Count);
    }

    [Fact]
    public void OverflowedEarlierKeyUnlocksSharedTrackReadiness()
    {
        // Key A overflows onto T0 (pure A) and T1 (shared [A, B]).
        // Once all A cars are anchored across T0 + T1 combined, T1 MUST become ready for B!
        var t0 = new YardTrackState { Track = new SortTrack { Track = 0, Name = "T1", Length = 116, WorkingEnd = TrackEnd.Start } }; // Effective 80 m
        var t1 = new YardTrackState { Track = new SortTrack { Track = 1, Name = "T2", Length = 116, WorkingEnd = TrackEnd.Start } }; // Effective 80 m
        var t2 = new YardTrackState { Track = new SortTrack { Track = 2, Name = "T3", Length = 116, WorkingEnd = TrackEnd.Start } }; // Effective 80 m (spare)

        // 7 cars of A (15 m each = 105 m -> splits 5 on one track + 2 on another) + 2 cars of B (15 m each = 30 m)
        var cut0 = new SortCut { Owner = 10, Track = 0 };
        cut0.CarsInnerToOuter.AddRange(new[] { Car(1, "A", 15), Car(2, "B", 15), Car(3, "A", 15), Car(4, "A", 15) });
        var cut1 = new SortCut { Owner = 11, Track = 1 };
        cut1.CarsInnerToOuter.AddRange(new[] { Car(5, "A", 15), Car(6, "B", 15), Car(7, "A", 15), Car(8, "A", 15), Car(9, "A", 15) });
        t0.CutsInnerToOuter.Add(cut0);
        t1.CutsInnerToOuter.Add(cut1);

        // Two tracks only so A overflows AND shares the second track with B!
        var twoTracks = new List<YardTrackState> { t0, t1 };
        var plan = YardSort.Assign(twoTracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid, string.Join("; ", plan.Problems));
        Assert.Equal(2, plan.KeyTracks["A"].Count);
        var sharedTrack = plan.KeyToTrack["B"];
        Assert.Contains("A", plan.TrackKeys[sharedTrack]);

        // Give it a spare track in the simulation? No, verify sorting works cleanly right on the 2 tracks!
        plan.MaxCutLength = 120;
        SimulateSort(plan, twoTracks, new List<SortCar>(), maxSteps: 200);
    }

    [Fact]
    public void UnknownKeyMidSortTriggersReplanInsteadOfStagingForever()
    {
        var tracks = new List<YardTrackState>
        {
            MakeTrack(0, "T1", 300, false, (10, new[] { "HB", "SM" })),
            MakeTrack(1, "T2", 300, false),
            MakeTrack(2, "T3", 300, false),
        };
        var plan = YardSort.Assign(tracks, Array.Empty<SortCar>());
        Assert.True(plan.Valid);

        // A car with a brand-new key ("NEW") appears on the hook mid-sort.
        var onHook = new List<SortCar> { Car(9999, "NEW") };
        var action = YardSort.NextAction(plan, tracks, onHook);
        Assert.Equal(SortActionKind.Blocked, action.Kind);
        Assert.True(action.NeedsReplan);
        Assert.Contains("NEW", action.Reason);
    }

    [Fact]
    public void LocateCutOnTrackHandlesReversedJointOverhang()
    {
        // Build two tracks joined Start-to-Start (reversed orientation at the joint!).
        // Bowl track T0 runs 0..200 m (Start is WorkingEnd). Adjacent lead T1 connects T1.Start -> T0.Start!
        var b = new TrackGraphBuilder();
        var t0 = b.AddTrack(200, new MapPoint(0, 0), new MapPoint(200, 0));
        var t1 = b.AddTrack(100, new MapPoint(0, 0), new MapPoint(-100, 0));
        b.Join(t0, TrackEnd.Start, t1, TrackEnd.Start);
        var graph = b.Build();

        // Cut sits on T0 near Start (WorkingEnd): end A overhangs 1.2 m onto T1 (offset 1.2 on T1, facing Forward on T1!),
        // while end B sits on T0 at offset 28.8 m. Because of the Start-to-Start joint, end A's outward direction on T1
        // is Forward (same enum value as T0's WorkingEnd.DirectionInto!), which broke the old heuristic.
        var endA = new TrackPosition(t1, 1.2);
        var endB = new TrackPosition(t0, 28.8);

        var ok = YardSort.LocateCutOnTrack(graph, t0, TrackEnd.Start, endA, endB, _ => 0,
            out var aIsOuter, out var near, out var far);

        Assert.True(ok);
        Assert.True(aIsOuter, "Overhanging coupler past WorkingEnd must be identified as outer even across a Start-to-Start joint!");
        Assert.Equal(-1.2, near, 4);
        Assert.Equal(28.8, far, 4);
    }

    [Fact]
    public void ChooseWorkingEndsMarksUnreachableTrackBlockedAndUsesActualPowerLength()
    {
        // 3-track yard where Track 2 is physically disconnected from both ladders.
        var b = new TrackGraphBuilder();
        var lead = b.AddTrack(140, new MapPoint(-140, 0), new MapPoint(0, 0));
        var t0 = b.AddTrack(250, new MapPoint(20, 0), new MapPoint(270, 0));
        var t1 = b.AddTrack(250, new MapPoint(20, 5), new MapPoint(270, 5));
        var isolated = b.AddTrack(250, new MapPoint(20, 10), new MapPoint(270, 10));
        b.AddJunction(lead, TrackEnd.End, new[] { (t0, TrackEnd.Start), (t1, TrackEnd.Start) }, new MapPoint(0, 0));
        var graph = b.Build();
        var world = new FakeWorld(graph);

        var tracksShortPower = new List<SortTrack>
        {
            new() { Track = t0, Name = "T1", Length = 250 },
            new() { Track = t1, Name = "T2", Length = 250 },
            new() { Track = isolated, Name = "T3", Length = 250 },
        };
        YardSort.ChooseWorkingEnds(world, world.SelectedBranch, LightLocoAt(lead, 60), tracksShortPower, out var cutShort, powerLength: 15);
        Assert.True(tracksShortPower[2].Blocked);
        Assert.False(tracksShortPower[0].Blocked);
        Assert.False(tracksShortPower[1].Blocked);

        var tracksLongPower = new List<SortTrack>
        {
            new() { Track = t0, Name = "T1", Length = 250 },
            new() { Track = t1, Name = "T2", Length = 250 },
        };
        YardSort.ChooseWorkingEnds(world, world.SelectedBranch, LightLocoAt(lead, 60), tracksLongPower, out var cutLong, powerLength: 55);
        Assert.True(cutShort - cutLong >= 30, $"Expected 40 m longer power consist to reduce max cut length (short={cutShort}, long={cutLong})");
    }

    [Fact]
    public void EngineerSecondPickupOntoLoadedHookRequiresCarsFirstArrival()
    {
        // Full physics test: switcher already has 1 car coupled to its far end (EndA = cars, EndB = loco)
        // on EastLead, and must couple to a SECOND standing cut on Bowl[1] with Arrival.CarsFirst + ArrivalRequired = true.
        var y = new InterchangeYard(eastLeadLen: 350, westLeadLen: 350);
        var world = new FakeWorld(y.Graph);
        var t1 = y.Bowl[1];

        const int cutOwner = 25;
        world.Occupancy.Add(t1, new OccupiedSpan(90, 120, cutOwner));
        var cutEnd = new CutEnd(new TrackPosition(t1, 90), TrackDirection.Backward);

        // Consist on EastLead: total length 30 m (15 m car at EndA facing yard + 15 m loco at EndB).
        var train = new NetworkTrain(world, new TrackPosition(y.EastLead, 235), TrackDirection.Forward, length: 30)
        {
            CarCount = 2,
        };
        var eng = new Engineer();

        eng.Enqueue(new Waypoint(OrderGoals.ToCouple(y.Graph, new[] { cutEnd }, world.SelectedBranch), WaypointAction.Couple)
        {
            Label = "2 cars on WI-A2X",
            Arrival = Arrival.CarsFirst,
            ArrivalRequired = true,
        });

        ConsistEnd? coupledEnd = null;
        Drive.Run(eng, train, world, maxSeconds: 600,
            state: s => { s.PowerCars = 1; s.LocoEnd = ConsistEnd.B; s.LocoFront = ConsistEnd.B; return s; },
            onOutput: o =>
            {
                if (o.CoupleAt.HasValue && !coupledEnd.HasValue)
                {
                    coupledEnd = o.CoupleAt.Value;
                    world.Occupancy.Clear();
                    train.CoupleOn(o.CoupleAt.Value, length: 30, cars: 2);
                }
            },
            until: () => eng.Queue.Count == 0);

        Assert.True(coupledEnd.HasValue);
        // EndA is the cars end (far from LocoEnd.B): coupling MUST happen on EndA (cars-first), NOT EndB (loco-first)!
        Assert.Equal(ConsistEnd.A, coupledEnd.Value);
        Assert.Equal(4, train.CarCount);
        Assert.False(train.Derailed);
    }
}
