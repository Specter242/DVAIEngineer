using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>A cut named by its index in the planner's list and one of its ends.</summary>
    public readonly struct CutRef
    {
        public readonly int Cut;
        public readonly int End;

        public CutRef(int cut, int end)
        {
            Cut = cut;
            End = end;
        }

        public override string ToString() => $"cut {Cut} by end {End}";
    }

    /// <summary>
    /// A standing cut as the planner sees it: one holding cars of the jobs
    /// handed to an engineer, or one standing in the way of such a cut.
    /// </summary>
    public sealed class JobCut
    {
        /// <summary>For the player: "4 cars on MF-B3S".</summary>
        public string Label;

        /// <summary>Its two ends: [0] end A (its first car's outer coupler), [1] end B.</summary>
        public CutEnd[] Ends;

        /// <summary>Each car's destination track, from end A; -1 for a car that isn't on a handed job and stays.</summary>
        public int[] Destinations;

        /// <summary>The track its cars go back to when they come along only because they stood in the way: the one it stands on.</summary>
        public int Home = -1;

        /// <summary>Its length, coupler to coupler, m: what a pull adds to the train (<see cref="JobStep"/>'s reach checks).</summary>
        public double Length;

        /// <summary>Per end: whether no loco can come at it from its track's way out there (a buffer stop, or a train with a loco in the way).</summary>
        public bool[] Barred = new bool[2];

        /// <summary>
        /// Per end: the cuts standing between it and its track's way out,
        /// the outermost first, each by its end facing the way out. A pull
        /// from that end couples to each in turn, so they all come along.
        /// </summary>
        public List<CutRef>[] Way = { new List<CutRef>(), new List<CutRef>() };

        /// <summary>
        /// Per end: reached (or left with its cars) only by a loco with no other
        /// cars, as at a ladder that ends in a short stub: a pull from it begins
        /// a trip.
        /// </summary>
        public bool[] LightOnly = new bool[2];

        public bool HasJobCars => Destinations != null && Array.Exists(Destinations, d => d >= 0);
    }

    /// <summary>
    /// One pull of a plan: the cuts coupled to in turn from a track's way
    /// out (<see cref="Through"/>, outermost first, then the cut itself at
    /// <see cref="End"/>), and how many of the cut's cars stay with the
    /// train, counted from that end: up to its farthest job car. The cars
    /// coupled through that aren't the jobs' are riders, put back where
    /// they stood once they reach the far end of the train.
    /// </summary>
    public sealed class JobPickup
    {
        public int Cut;
        public int End;
        public int Keep;
        public int Riders;
        public readonly List<CutRef> Through = new List<CutRef>();

        /// <summary>Which trip it is made on, from 0: a trip's pulls are made, then all its cars set out, before the next trip's.</summary>
        public int Trip;

        public override string ToString() =>
            $"trip {Trip}: cut {Cut} by end {End}, keeping {Keep}" + (Through.Count > 0 ? $", through {Through.Count} cut{(Through.Count == 1 ? "" : "s")}" : "") +
            (Riders > 0 ? $" ({Riders} riding)" : "");
    }

    /// <summary>What the planner made of the jobs: the pulls in order, the set-outs expected after, and what it couldn't do.</summary>
    public sealed class JobPlan
    {
        public readonly List<JobPickup> Pickups = new List<JobPickup>();

        /// <summary>The destination tracks in the order the cars are expected to be set out.</summary>
        public readonly List<int> Drops = new List<int>();

        /// <summary>Cuts or cars left out, each said for the player.</summary>
        public readonly List<string> Problems = new List<string>();

        /// <summary>The estimated distance, m (infinity when some leg has no track joining it).</summary>
        public double Cost;
    }

    /// <summary>
    /// Plans the work of a group of jobs (waybills) for one loco: which
    /// cuts to pull from which way out of their track, and in what order.
    /// A pull couples to every cut standing between the way out and the
    /// one wanted, in turn, and takes them all along; once coupled to the
    /// last, the cars beyond its farthest job car are cut off where they
    /// stand. The cars that came along only because they stood in the way
    /// (riders) are put back on their track once they reach the far end of
    /// the train. Each later pull is coupled on the far end of what the
    /// train already has (cars first), so the last pulled is the first set
    /// out: the order of the pulls fixes the order of the set-outs, which
    /// are always made from the far end. A buried cut, or a job's cars
    /// split up among others, is had the same way. Every cut of the jobs
    /// is taken by exactly one pull (its own, or a deeper one's from the
    /// same side). Pulls are grouped in trips: a trip's pulls are made, then
    /// all of its cars set out and its riders put back, before the next
    /// trip's (in an industry, one line at a time; for a far destination,
    /// several cuts gathered first). Of the orders and groupings that do so,
    /// the planner takes the one with the least running, as estimated by
    /// running a train must do (<see cref="TrainMoves"/>: which end leads,
    /// room to turn back past a switch), with costs
    /// on riders and on the cars a pull adds to, in a depth-first search
    /// nearest first, bounded by <see cref="SearchBudget"/>. Engine-free:
    /// the game side reads the jobs and the cuts.
    /// </summary>
    public static class JobPlanner
    {
        /// <summary>How many steps the search takes before it settles for the best order found.</summary>
        public const int SearchBudget = 20000;

        /// <summary>The train lengths the running is reckoned for, m: a light engine, and longer trains in steps.</summary>
        private static readonly double[] LengthSteps = { 20, 60, 120, 200, 320, 500 };

        /// <summary>The step of <see cref="LengthSteps"/> for a loco with <paramref name="cars"/> cars (15 m a loco, 14 m a car).</summary>
        private static int LengthStep(int cars)
        {
            var length = 15 + 14.0 * cars;
            for (var i = 0; i < LengthSteps.Length; i++)
            {
                if (length <= LengthSteps[i])
                    return i;
            }
            return LengthSteps.Length - 1;
        }

        /// <summary>What setting out one track's cars in two lots on one trip costs, m: a set-out more (the second joins the first).</summary>
        private const double SplitCost = 500;

        /// <summary>
        /// What each car already on the train costs a pull, m: a long train is
        /// slow to work, so a crew in an industry pulls one line, sets it out
        /// and puts its riders back before the next.
        /// </summary>
        private const double CarryCost = 30;

        /// <summary>What closing a trip with a set-out on a track where a cut still to be pulled stands costs, m: the set-out may bury it.</summary>
        private const double BuryCost = 1000;

        /// <summary>What each car carried along costs, m, besides the trip to put it back: fewer riders first.</summary>
        private const double RiderCost = 100;

        /// <summary>What setting aside the train's own cars that are in the way costs, m: a short move to a track nearby.</summary>
        private const double SetAsideCost = 400;

        /// <summary>What a move the wrong way round costs, m, when no way turns the train for it (the engineer goes anyway, and says so).</summary>
        private const double WrongWayCost = 1e4;

        /// <summary>What leaving a cut of the jobs out costs, m: so every order that takes it ranks first.</summary>
        private const double LeftOutCost = 1e7;

        /// <summary>The code a rider carries in a prediction: below -1 (a car that isn't a job's), naming the track it goes back to.</summary>
        public static int RiderCode(int home) => -2 - home;

        /// <summary>The track a rider's code sends it back to.</summary>
        public static int HomeOf(int code) => -2 - code;

        /// <summary>The first and last of a cut's handed cars, from end A; null when it has none.</summary>
        public static (int First, int Last)? JobSpan(IReadOnlyList<int> destinations)
        {
            var first = -1;
            var last = -1;
            for (var i = 0; i < destinations.Count; i++)
            {
                if (destinations[i] < 0)
                    continue;
                if (first < 0)
                    first = i;
                last = i;
            }
            return first < 0 ? ((int, int)?)null : (first, last);
        }

        /// <summary>How many of a cut's cars a pull coupled at <paramref name="end"/> keeps: up to its farthest job car (all, for a cut with none).</summary>
        public static int KeepFrom(JobCut cut, int end)
        {
            var n = cut.Destinations.Length;
            var span = JobSpan(cut.Destinations);
            if (span == null)
                return n;
            return end == 0 ? span.Value.Last + 1 : n - span.Value.First;
        }

        /// <summary>
        /// The codes of a pull's cars once on the train, from the far end in:
        /// coupled at end A, the cut's first car is next to the train and its
        /// <paramref name="keep"/>-th the farthest. A car that isn't a job's
        /// is a rider (<see cref="RiderCode"/>) with <paramref name="riders"/>
        /// and the cut's home known, else -1.
        /// </summary>
        public static IEnumerable<int> FarToNear(JobCut cut, int end, int keep, bool riders = false)
        {
            var n = cut.Destinations.Length;
            int Code(int d) => d >= 0 || !riders || cut.Home < 0 ? d : RiderCode(cut.Home);
            if (end == 0)
            {
                for (var i = keep - 1; i >= 0; i--)
                    yield return Code(cut.Destinations[i]);
            }
            else
            {
                for (var i = n - keep; i < n; i++)
                    yield return Code(cut.Destinations[i]);
            }
        }

        /// <summary>
        /// The next set-out from a train, by its cars' destinations from the
        /// far end in: the run of cars bound for the same track at the far
        /// end. Null when the train holds no handed cars, or a car that
        /// stays is at the far end in their way.
        /// </summary>
        public static (int Destination, int Cars)? NextDrop(IReadOnlyList<int> farToNear)
        {
            if (farToNear.Count == 0 || farToNear[0] < 0)
                return null;
            var dest = farToNear[0];
            var cars = 1;
            while (cars < farToNear.Count && farToNear[cars] == dest)
                cars++;
            return (dest, cars);
        }

        /// <summary>
        /// Plans the pulls. <paramref name="start"/> is the loco's train's
        /// far end; <paramref name="inTrainFarToNear"/> the codes of the cars
        /// it already has, from the far end in (a destination, -1 for a car
        /// that stays, a <see cref="RiderCode"/> for one to put back);
        /// <paramref name="standing"/> whether cars stand on a track between
        /// two offsets (null: every track empty), for the ways through and
        /// room to turn.
        /// middle).
        /// </summary>
        public static JobPlan Plan(TrackGraph graph, TrainPose start, IReadOnlyList<JobCut> cuts,
            IReadOnlyList<int> inTrainFarToNear, Func<int, double, double, bool> standing = null)
        {
            var plan = new JobPlan();
            var pulls = new List<Pull>();
            var coverable = new HashSet<int>();
            for (var i = 0; i < cuts.Count; i++)
            {
                if (!cuts[i].HasJobCars)
                    continue;
                for (var e = 0; e < 2; e++)
                {
                    if (cuts[i].Barred[e])
                        continue;
                    var pull = Pull.Of(cuts, i, e);
                    pulls.Add(pull);
                    foreach (var c in pull.Cover)
                        coverable.Add(c);
                }
            }
            var wanted = new List<int>();
            for (var i = 0; i < cuts.Count; i++)
            {
                if (!cuts[i].HasJobCars)
                    continue;
                if (coverable.Contains(i))
                    wanted.Add(i);
                else
                    plan.Problems.Add($"{cuts[i].Label}: no loco can get at it: a buffer stop or a train with a loco stands in the way at each end");
            }

            // One search per pose and train length (in steps), shared by every order tried.
            var searches = new Dictionary<(TrainPose, int), TrainMoves>();
            TrainMoves From(TrainPose pose, int cars)
            {
                var step = LengthStep(cars);
                if (!searches.TryGetValue((pose, step), out var moves))
                    searches[(pose, step)] = moves = TrainMoves.From(graph, pose, LengthSteps[step], standing);
                return moves;
            }

            var search = new Search(graph, pulls, cuts, wanted, inTrainFarToNear, From);
            var best = search.Run(start);
            for (var k = 0; k < best.Pulls.Count; k++)
            {
                var pull = best.Pulls[k];
                var pickup = new JobPickup { Cut = pull.Cut, End = pull.End, Keep = pull.Keep, Riders = pull.Riders, Trip = best.Trips[k] };
                pickup.Through.AddRange(pull.Through);
                plan.Pickups.Add(pickup);
            }
            foreach (var i in best.LeftOut)
                plan.Problems.Add($"{cuts[i].Label}: the other cuts stand in its way whichever way it's taken");
            plan.Drops.AddRange(best.Drops);
            plan.Cost = best.Cost;
            return plan;
        }

        /// <summary>A pull the planner may make: a cut from one end, through what stands on that side.</summary>
        private sealed class Pull
        {
            public int Cut;
            public int End;
            public int Keep;
            public int Riders;
            public List<CutRef> Through;
            public bool LightOnly;

            /// <summary>The cuts of the jobs it takes: its own and those it couples through.</summary>
            public readonly List<int> Cover = new List<int>();

            /// <summary>Where the loco meets the first cut: the outermost one's end, arriving toward it.</summary>
            public TrackPosition Approach;
            public TrackDirection ApproachArrive;

            /// <summary>Where the train stands once it has pulled: at the outermost cut's end, its cars inward.</summary>
            public TrainPose After => new TrainPose(Approach, ApproachArrive);

            /// <summary>The codes of the cars it adds to the train, from the far end in.</summary>
            public readonly List<int> FarToNear = new List<int>();

            public static Pull Of(IReadOnlyList<JobCut> cuts, int i, int end)
            {
                var cut = cuts[i];
                var pull = new Pull
                {
                    Cut = i,
                    End = end,
                    Keep = KeepFrom(cut, end),
                    Through = cut.Way[end],
                    LightOnly = cut.LightOnly[end],
                };
                pull.Cover.Add(i);
                pull.FarToNear.AddRange(FarToNear(cut, end, pull.Keep, riders: true));
                for (var k = pull.Through.Count - 1; k >= 0; k--)
                {
                    var w = pull.Through[k];
                    var other = cuts[w.Cut];
                    if (other.HasJobCars)
                        pull.Cover.Add(w.Cut);
                    pull.FarToNear.AddRange(FarToNear(other, w.End, other.Destinations.Length, riders: true));
                }
                foreach (var code in pull.FarToNear)
                {
                    if (code < 0)
                        pull.Riders++;
                }
                var first = pull.Through.Count > 0 ? pull.Through[0] : new CutRef(i, end);
                pull.Approach = cuts[first.Cut].Ends[first.End].Position;
                pull.ApproachArrive = cuts[first.Cut].Ends[first.End].Outward.Reverse();
                return pull;
            }
        }

        private sealed class Outcome
        {
            public readonly List<Pull> Pulls = new List<Pull>();
            public readonly List<int> Trips = new List<int>();
            public readonly List<int> Drops = new List<int>();
            public readonly List<int> LeftOut = new List<int>();
            public double Cost = double.PositiveInfinity;
        }

        private sealed class Search
        {
            private readonly List<Pull> pulls;
            private readonly IReadOnlyList<JobCut> cuts;
            private readonly List<int> wanted;
            private readonly IReadOnlyList<int> inTrain;
            private readonly TrackGraph graph;
            private readonly Func<TrainPose, int, TrainMoves> from;
            private readonly bool[] taken;
            private readonly List<Pull> sequence = new List<Pull>();
            private Outcome best;
            private int steps;

            public Search(TrackGraph graph, List<Pull> pulls, IReadOnlyList<JobCut> cuts, List<int> wanted, IReadOnlyList<int> inTrain,
                Func<TrainPose, int, TrainMoves> from)
            {
                this.pulls = pulls;
                this.cuts = cuts;
                this.wanted = wanted;
                this.inTrain = inTrain;
                this.graph = graph;
                this.from = from;
                taken = new bool[cuts.Count];
            }

            public Outcome Run(TrainPose start)
            {
                Descend(start, 0, 0, new List<int>(inTrain), new List<int>());
                return best;
            }

            /// <summary>
            /// One step of the search: from <paramref name="at"/>, having spent
            /// <paramref name="cost"/>, with the current trip's pulls starting at
            /// <paramref name="tripStart"/> in the sequence, a train of
            /// <paramref name="base"/> (far to near) under them, and the set-outs
            /// of the trips closed so far: add a pull to this trip, or close it.
            /// </summary>
            private void Descend(TrainPose at, double cost, int tripStart, List<int> @base, List<int> drops)
            {
                steps++;
                var carried = @base.Count;
                for (var k = tripStart; k < sequence.Count; k++)
                    carried += sequence[k].FarToNear.Count;
                var children = new List<(Pull Pull, double Step)>();
                var waiting = false;
                foreach (var pull in pulls)
                {
                    var free = true;
                    foreach (var c in pull.Cover)
                    {
                        if (taken[c])
                        {
                            free = false;
                            break;
                        }
                    }
                    if (!free)
                        continue;
                    // A pull only a light loco can make begins its trip.
                    if (pull.LightOnly && carried > 0)
                        waiting = true;
                    else
                        children.Add((pull, Run(from(at, carried), pull.Approach, pull.ApproachArrive, carried > 0 ? Lead.Cars : (Lead?)null)
                                            + RiderCost * pull.Riders + CarryCost * carried));
                }
                // Closing the trip here: its cars set out, the next pull begins a trip of its own
                // (the cars the train started with too, when a pull waits for a light loco).
                List<int> closedDrops = null, rest = null;
                var closeCost = 0.0;
                var closeAt = at;
                var open = sequence.Count > tripStart || (waiting && @base.Count > 0);
                if (open)
                {
                    closedDrops = new List<int>(drops);
                    closeCost = SetOuts(TripTrain(tripStart, @base), at, closedDrops, out closeAt, out rest);
                    for (var k = drops.Count; k < closedDrops.Count; k++)
                    {
                        foreach (var i in wanted)
                        {
                            if (!taken[i] && cuts[i].Home == closedDrops[k])
                            {
                                closeCost += BuryCost;
                                break;
                            }
                        }
                    }
                    // A close that sets nothing out (only cars that stay) gets nowhere.
                    if (sequence.Count == tripStart && rest.Count >= @base.Count)
                        open = false;
                }
                if (children.Count == 0 && !open)
                {
                    Leaf(at, cost, tripStart, @base, drops);
                    return;
                }
                children.Sort((a, b) => a.Step.CompareTo(b.Step));
                // A trip with riders closes first: in an industry, a line at a time.
                var closeFirst = open && sequence.Skip(tripStart).Any(p => p.Riders > 0);
                if (closeFirst)
                    Close(closeAt, cost + closeCost, rest, closedDrops);
                foreach (var (pull, step) in children)
                {
                    // Out of budget: keep the best found (the first descent is nearest-first, so there is one).
                    if (best != null && (steps > SearchBudget || cost + step >= best.Cost))
                        continue;
                    foreach (var c in pull.Cover)
                        taken[c] = true;
                    sequence.Add(pull);
                    trips.Add(trip);
                    Descend(pull.After, cost + step, tripStart, @base, drops);
                    trips.RemoveAt(trips.Count - 1);
                    sequence.RemoveAt(sequence.Count - 1);
                    foreach (var c in pull.Cover)
                        taken[c] = false;
                }
                if (open && !closeFirst)
                    Close(closeAt, cost + closeCost, rest, closedDrops);
            }

            private int trip;
            private readonly List<int> trips = new List<int>();

            private void Close(TrainPose at, double cost, List<int> rest, List<int> drops)
            {
                if (best != null && (steps > SearchBudget || cost >= best.Cost))
                    return;
                trip++;
                Descend(at, cost, sequence.Count, rest, drops);
                trip--;
            }

            /// <summary>The train a trip makes, from the far end in: its last pull first, what it started with last.</summary>
            private List<int> TripTrain(int tripStart, List<int> @base)
            {
                var farToNear = new List<int>();
                for (var k = sequence.Count - 1; k >= tripStart; k--)
                    farToNear.AddRange(sequence[k].FarToNear);
                farToNear.AddRange(@base);
                return farToNear;
            }

            /// <summary>No pull left to make: the last trip's set-outs, and whether that's the best so far.</summary>
            private void Leaf(TrainPose at, double cost, int tripStart, List<int> @base, List<int> drops)
            {
                var outcome = new Outcome();
                foreach (var i in wanted)
                {
                    if (!taken[i])
                        outcome.LeftOut.Add(i);
                }
                cost += LeftOutCost * outcome.LeftOut.Count;
                if (best != null && cost >= best.Cost)
                    return;
                outcome.Drops.AddRange(drops);
                cost += SetOuts(TripTrain(tripStart, @base), at, outcome.Drops, out _, out _);
                if (best != null && cost >= best.Cost)
                    return;
                outcome.Pulls.AddRange(sequence);
                outcome.Trips.AddRange(trips);
                outcome.Cost = cost;
                best = outcome;
            }

            /// <summary>
            /// The running to a point with an end leading; with no way to
            /// turn the train for it, the running either way round and
            /// <see cref="WrongWayCost"/>, as the engineer goes anyway then.
            /// </summary>
            private static double Run(TrainMoves moves, TrackPosition to, TrackDirection arrive, Lead? lead)
            {
                var c = moves.To(to, arrive, lead);
                return lead.HasValue && double.IsPositiveInfinity(c) ? moves.To(to, arrive, null) + WrongWayCost : c;
            }

            /// <summary>
            /// A set-out of <paramref name="run"/> of the train's
            /// <paramref name="cars"/> cars on a track: in by whichever entry
            /// costs least, cars first; the train then stands there, its other
            /// cars (if any) inward.
            /// </summary>
            private double SetOutAt(int track, int cars, int run, ref TrainPose at)
            {
                var moves = from(at, cars);
                var best = double.PositiveInfinity;
                var bestPose = at;
                var length = graph.Length(track);
                foreach (var entry in new[] { TrackEnd.Start, TrackEnd.End })
                {
                    if (graph.Links(track, entry).Length == 0)
                        continue;
                    var into = entry.DirectionInto();
                    var along = Math.Min(5, length / 2);
                    var point = new TrackPosition(track, entry == TrackEnd.Start ? along : length - along);
                    var c = Run(moves, point, into, Lead.Cars);
                    if (c < best)
                    {
                        best = c;
                        bestPose = new TrainPose(point, cars > run ? into : (TrackDirection?)null);
                    }
                }
                at = bestPose;
                return best;
            }

            /// <summary>The set-outs from the far end, as the run makes them: each to its track, riders back home, the train's own cars aside.</summary>
            private double SetOuts(List<int> farToNear, TrainPose at, List<int> drops, out TrainPose end, out List<int> left)
            {
                var cost = 0.0;
                var seen = new HashSet<int>();
                var rest = 0;
                while (rest < farToNear.Count)
                {
                    var code = farToNear[rest];
                    var run = 1;
                    while (rest + run < farToNear.Count && farToNear[rest + run] == code)
                        run++;
                    if (code >= 0)
                    {
                        if (!seen.Add(code))
                            cost += SplitCost;
                        cost += SetOutAt(code, farToNear.Count - rest, run, ref at);
                        drops.Add(code);
                    }
                    else
                    {
                        var jobsBehind = false;
                        for (var k = rest + run; k < farToNear.Count && !jobsBehind; k++)
                            jobsBehind = farToNear[k] >= 0 || farToNear[k] <= -2;
                        if (code <= -2)
                        {
                            // Riders go back to the track they came from.
                            cost += SetOutAt(HomeOf(code), farToNear.Count - rest, run, ref at);
                        }
                        else if (jobsBehind)
                        {
                            // The train's own cars, in the way of the rest: set aside nearby.
                            cost += SetAsideCost;
                        }
                        else
                        {
                            break;
                        }
                    }
                    rest += run;
                }
                end = at;
                left = farToNear.GetRange(rest, farToNear.Count - rest);
                return cost;
            }
        }
    }
}
