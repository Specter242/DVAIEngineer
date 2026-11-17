using System;
using System.Collections.Generic;
using System.Diagnostics;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>
    /// Everything one plan of a job run's next moves reads, gathered by the
    /// game side in one go so the plan itself can be worked out off the
    /// game's thread: nothing here is the game's own.
    /// </summary>
    public sealed class JobStepRequest
    {
        /// <summary>The world the routes are looked for in: a <see cref="FrozenWorld"/> when the plan runs on another thread.</summary>
        public IEngineerWorld World;

        /// <summary>The engineer's train as it stands.</summary>
        public TrainState Train;

        /// <summary>The train as the planner sees it: its far end and which way its cars lie, or a light engine.</summary>
        public TrainPose Start;

        /// <summary>
        /// The cuts: the first <see cref="JobCuts"/> hold the jobs' cars and
        /// have their ways out found (<see cref="JobCut.Way"/>, and
        /// <see cref="JobCut.Barred"/> by a buffer or a train with a loco);
        /// the rest only stand in a way. The plan bars more ends, and marks
        /// those only a light loco reaches.
        /// </summary>
        public List<JobCut> Cuts;

        public int JobCuts;

        /// <summary>The codes of the cars the train has, from the far end in (see <see cref="JobPlanner.Plan"/>).</summary>
        public IReadOnlyList<int> InTrain;

        /// <summary>A track's name for the notes; null names it by number.</summary>
        public Func<int, string> TrackName;
    }

    /// <summary>What a plan of a job run's next moves came to.</summary>
    public sealed class JobStepPlan
    {
        public JobPlan Plan;

        /// <summary>What the reach checks found, for the log.</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>How long it took to work out, ms.</summary>
        public double Milliseconds;
    }

    /// <summary>
    /// One plan of a job run's next moves, start to finish: which ends of
    /// the jobs' cuts a route really reaches with the train as it is (and
    /// leaves from, to where the cars go), then the pulls in the best order
    /// (<see cref="JobPlanner"/>). It is the dearest thing the mod does, a
    /// dozen searches of the whole network and more, so it reads only its
    /// request and can run on any thread.
    /// </summary>
    public static class JobStep
    {
        /// <summary>How much longer a train a later pull of a trip is checked with, m: four cars.</summary>
        private const double LoadedExtra = 60;

        public static JobStepPlan Plan(JobStepRequest request)
        {
            var watch = Stopwatch.StartNew();
            var result = new JobStepPlan();
            var world = request.World;
            Func<int, int> lie = world.SelectedBranch;
            for (var i = 0; i < request.JobCuts; i++)
                CheckWays(request, i, lie, result.Notes);
            var own = request.Train.Owner;
            result.Plan = JobPlanner.Plan(world.Graph, request.Start, request.Cuts, request.InTrain,
                (t, a, b) => world.Occupancy.IsStanding(t, a, b, own));
            result.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// For each end of cut <paramref name="i"/> not barred already:
        /// whether a route brings the train to the end the loco meets first,
        /// and takes it on from there to where the cars go; if only with
        /// nothing else on the train, the pull begins a trip.
        /// </summary>
        private static void CheckWays(JobStepRequest request, int i, Func<int, int> lie, List<string> notes)
        {
            var world = request.World;
            var state = request.Train;
            var cuts = request.Cuts;
            var cut = cuts[i];
            for (var e = 0; e < 2; e++)
            {
                if (cut.Barred[e])
                    continue;
                var way = cut.Way[e];
                var which = e == 0 ? "A" : "B";
                var meet = way.Count > 0 ? way[0] : new CutRef(i, e);
                var meetEnd = cuts[meet.Cut].Ends[meet.End];
                if (!CanReach(world, lie, state, meetEnd))
                {
                    cut.Barred[e] = true;
                    notes.Add($"{cut.Label}: no route reaches its {which} end");
                    continue;
                }
                // Pulled out this way, the train must get from there to where its cars go:
                // a ladder that ends in a short stub leaves a train no room to turn back.
                var stuck = -1;
                foreach (var (train, track) in Exits(request, i, e, lie, 0))
                {
                    if (!CanLeaveTo(world, lie, train, track))
                    {
                        stuck = track;
                        break;
                    }
                }
                if (stuck >= 0)
                {
                    cut.Barred[e] = true;
                    notes.Add($"{cut.Label}: pulled out by its {which} end, no route takes it on to {Name(request, stuck)}");
                    continue;
                }
                // Reached, and left, only with nothing else on the train: its pull begins a trip.
                var lightOnly = !CanReach(world, lie, Loaded(world, lie, state), meetEnd);
                if (!lightOnly)
                {
                    foreach (var (train, track) in Exits(request, i, e, lie, LoadedExtra))
                    {
                        if (!CanLeaveTo(world, lie, train, track))
                        {
                            lightOnly = true;
                            break;
                        }
                    }
                }
                if (lightOnly)
                {
                    cut.LightOnly[e] = true;
                    notes.Add($"{cut.Label}: by its {which} end only with nothing else on the train");
                }
            }
        }

        private static string Name(JobStepRequest request, int track) => request.TrackName?.Invoke(track) ?? $"track {track}";

        /// <summary>
        /// The train a pull of cut <paramref name="i"/> by end <paramref name="e"/>
        /// makes, standing where it would once coupled (the loco beyond the
        /// outermost cut, the far end at the cut's other end), and each
        /// track its job cars go to.
        /// </summary>
        private static IEnumerable<(TrainState Train, int Track)> Exits(JobStepRequest request, int i, int e, Func<int, int> lie, double extra)
        {
            var world = request.World;
            var now = request.Train;
            var cuts = request.Cuts;
            var cut = cuts[i];
            var way = cut.Way[e];
            var meet = way.Count > 0 ? way[0] : new CutRef(i, e);
            var outer = cuts[meet.Cut].Ends[meet.End];
            // The loco beyond the outermost cut, with whatever else the train has (extra) between.
            var locoLength = (now.CarCount <= 1 ? now.Length : 15) + extra;
            var locoEnd = TrackWalker.Advance(world.Graph, outer.Position, outer.Outward, locoLength, lie, out var outward);
            var length = locoLength + cut.Length;
            foreach (var w in way)
                length += cuts[w.Cut].Length;
            var far = cut.Ends[1 - e];
            var train = new TrainState
            {
                EndA = locoEnd,
                OutwardA = outward,
                EndB = far.Position,
                OutwardB = far.Outward,
                Length = length,
                CarCount = 2,
                LocoEnd = ConsistEnd.A,
                Owner = now.Owner,
            };
            var tracks = new HashSet<int>();
            for (var k = 0; k <= way.Count; k++)
            {
                var from = k < way.Count ? cuts[way[k].Cut] : cut;
                foreach (var d in from.Destinations)
                {
                    if (d >= 0 && tracks.Add(d))
                        yield return (train, d);
                }
            }
        }

        /// <summary>The train as it is, with four cars more at its far end: as a later pull of a trip finds it.</summary>
        private static TrainState Loaded(IEngineerWorld world, Func<int, int> lie, TrainState now)
        {
            var far = now.CarCount <= 1 ? ConsistEnd.B : now.LocoEnd.Other();
            var at = far == ConsistEnd.A ? now.EndA : now.EndB;
            var outward = far == ConsistEnd.A ? now.OutwardA : now.OutwardB;
            var moved = TrackWalker.Advance(world.Graph, at, outward, LoadedExtra, lie, out var dir);
            var loaded = now;
            if (far == ConsistEnd.A)
            {
                loaded.EndA = moved;
                loaded.OutwardA = dir;
            }
            else
            {
                loaded.EndB = moved;
                loaded.OutwardB = dir;
            }
            loaded.Length += LoadedExtra;
            loaded.CarCount += 4;
            if (now.CarCount <= 1)
                loaded.LocoEnd = ConsistEnd.A;
            return loaded;
        }

        /// <summary>Whether any route brings the train, as it is, to couple to the cut end (either way round).</summary>
        public static bool CanReach(IEngineerWorld world, Func<int, int> lie, TrainState state, CutEnd end)
        {
            foreach (var goal in OrderGoals.CoupleOnward(world.Graph, state, end, lie))
            {
                if (world.Router.Find(Request(world, lie, state, goal), out _) != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether any route brings the train, as it is, to the track: its
        /// leading end just in by an entry (either way round), which a set-out
        /// needs whatever stands further in.
        /// </summary>
        public static bool CanLeaveTo(IEngineerWorld world, Func<int, int> lie, TrainState state, int track)
        {
            var graph = world.Graph;
            var length = graph.Length(track);
            for (var k = 0; k < 2; k++)
            {
                var entry = k == 0 ? TrackEnd.Start : TrackEnd.End;
                if (graph.Links(track, entry).Length == 0)
                    continue;
                var along = Math.Min(5, length / 2);
                var goal = new WaypointGoal(new TrackPosition(track, entry == TrackEnd.Start ? along : length - along), entry.DirectionInto());
                if (world.Router.Find(Request(world, lie, state, goal), out _) != null)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The route a job run asks for: the train as it stands to a goal, by
        /// the switches the engineer may count on (<see cref="SwitchRules"/>)
        /// and past no cars at rest; or, to say why there is none, with the
        /// cars or the switches left out.
        /// </summary>
        public static RouteRequest Request(IEngineerWorld world, Func<int, int> lie, TrainState state, WaypointGoal goal,
            bool blocked = true, bool anySwitch = false) => new RouteRequest
        {
            EndA = state.EndA,
            OutwardA = state.OutwardA,
            EndB = state.EndB,
            OutwardB = state.OutwardB,
            TrainLength = state.Length,
            Goal = goal.Position,
            GoalDirection = goal.Arrive,
            SwitchLie = lie,
            CanSetJunction = anySwitch ? (Func<int, int, bool>)((j, b) => true) : (j, b) => SwitchRules.Usable(world, j, b, state.Owner),
            JunctionPenalty = anySwitch ? (Func<int, int, double>)null : (j, b) => SwitchRules.Penalty(world, j, b, state.Owner),
            Blocked = blocked ? (t, from, to) => world.Occupancy.IsStanding(t, from, to, state.Owner) : (Func<int, double, double, bool>)null,
        };
    }
}
