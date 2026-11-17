using System;
using System.Collections.Generic;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Routing
{
    /// <summary>Which end of a train leads a move: the loco's, or its cars' (shoved ahead).</summary>
    public enum Lead : byte
    {
        Loco = 0,
        Cars = 1,
    }

    /// <summary>
    /// Where a train stands, for the job planner: a point (its far end, or the
    /// loco for a light engine), and which way along that track its cars lie
    /// from the loco; null for a light engine, which can lead either way.
    /// </summary>
    public readonly struct TrainPose : IEquatable<TrainPose>
    {
        public readonly TrackPosition Position;
        public readonly TrackDirection? CarsToward;

        public TrainPose(TrackPosition position, TrackDirection? carsToward)
        {
            Position = position;
            CarsToward = carsToward;
        }

        public bool Equals(TrainPose other) => Position.Equals(other.Position) && CarsToward == other.CarsToward;

        public override bool Equals(object obj) => obj is TrainPose p && Equals(p);

        public override int GetHashCode() => Position.GetHashCode() * 3 + (CarsToward.HasValue ? (int)CarsToward.Value + 2 : 0);

        public override string ToString() => $"{Position}{(CarsToward.HasValue ? " cars " + CarsToward.Value : " light")}";
    }

    /// <summary>
    /// The running a train of a given length needs from a pose to arrive
    /// somewhere moving a given way with a given end leading, as a train must
    /// run (unlike <see cref="GraphDistances"/>): through a switch only from
    /// its stem to a branch or back, never branch to branch, and turning back
    /// only after running wholly past the switch it came through onto free
    /// track, which costs the run out and back and a penalty. A yard whose
    /// tracks are full near their switches leaves no room to turn, and the
    /// way round can be long; this sees it. Still an estimate for ordering
    /// work: switch settings and other trains are ignored.
    /// </summary>
    public sealed class TrainMoves
    {
        /// <summary>What a reversal costs besides the run out and back, m (the router's own).</summary>
        public const double ReversalPenalty = 400;

        /// <summary>How far past the switch's points a train must run to clear it before turning back, m.</summary>
        public const double Clearance = 15;

        /// <summary>
        /// How many tracks on a reversal may run onto, past the one it
        /// entered: as deep as the router looks. The map's turnouts are
        /// tracks of their own, 8 to 24 m long, so a train's room to turn
        /// lies over many of them; looking only four deep priced a 625 m
        /// move at 8.5 km and others as impossible.
        /// </summary>
        private const int RoomDepth = 64;

        private readonly TrackGraph graph;
        private readonly TrainPose from;
        private readonly Func<int, double, double, bool> standing;
        // The cheapest cost of entering each track by each end with each lead: [(track * 2 + end) * 2 + lead].
        private readonly double[] entry;

        private TrainMoves(TrackGraph graph, TrainPose from, double[] entry, Func<int, double, double, bool> standing)
        {
            this.graph = graph;
            this.from = from;
            this.entry = entry;
            this.standing = standing;
        }

        /// <summary>Whether cars stand on the track between two offsets (either order).</summary>
        private static bool Blocked(Func<int, double, double, bool> standing, int track, double a, double b) =>
            standing != null && Math.Abs(b - a) > 1e-6 && standing(track, Math.Min(a, b), Math.Max(a, b));

        /// <summary>
        /// Searches from <paramref name="pose"/> for a train of
        /// <paramref name="trainLength"/> m. <paramref name="standing"/> says
        /// whether cars stand on a track between two offsets: a train runs
        /// through a track only when nothing stands on it, reaches a point only
        /// with nothing between, and turns back only in the free stretch in
        /// from the switch. Null takes every track as empty.
        /// </summary>
        public static TrainMoves From(TrackGraph graph, TrainPose pose, double trainLength, Func<int, double, double, bool> standing = null)
        {
            // The free stretch in from a track end, up to the first car standing there.
            var known = new Dictionary<int, double>();
            double FreeFrom(int track, TrackEnd end)
            {
                var key = track * 2 + (int)end;
                if (known.TryGetValue(key, out var free))
                    return free;
                return known[key] = Measure(track, end);
            }
            double Measure(int track, TrackEnd end)
            {
                var length = graph.Length(track);
                if (!Blocked(standing, track, 0, length))
                    return length;
                // Halving down to a metre: the nearest car's edge from that end.
                double lo = 0, hi = length;
                while (hi - lo > 1)
                {
                    var mid = (lo + hi) / 2;
                    var blocked = end == TrackEnd.Start ? Blocked(standing, track, 0, mid) : Blocked(standing, track, length - mid, length);
                    if (blocked)
                        hi = mid;
                    else
                        lo = mid;
                }
                return lo;
            }
            return Search(graph, pose, trainLength, standing, FreeFrom);
        }

        private static TrainMoves Search(TrackGraph graph, TrainPose pose, double trainLength, Func<int, double, double, bool> standing,
            Func<int, TrackEnd, double> freeFrom)
        {
            var need = trainLength + Clearance;
            var count = graph.TrackCount * 4;
            var entry = new double[count];
            var exit = new double[count];
            for (var i = 0; i < count; i++)
            {
                entry[i] = double.PositiveInfinity;
                exit[i] = double.PositiveInfinity;
            }
            double Free(int track, TrackEnd end) => Math.Max(0, Math.Min(graph.Length(track), freeFrom(track, end)));
            // Whether a free run of a length lies in from a track end, on through the tracks beyond
            // while they are empty. By track end, the shortest run known not to be there (a longer
            // one isn't either) and the longest known to be.
            var noRoom = new Dictionary<int, double>();
            var hasRoom = new Dictionary<int, double>();
            var cutShort = false;
            bool Room(int track, TrackEnd end, double wanted, int depth)
            {
                var key = track * 2 + (int)end;
                if (noRoom.TryGetValue(key, out var none) && wanted >= none)
                    return false;
                if (hasRoom.TryGetValue(key, out var some) && wanted <= some)
                    return true;
                var free = Free(track, end);
                var length = graph.Length(track);
                var found = free >= wanted;
                if (!found && free >= length - 0.5)
                {
                    if (depth >= RoomDepth)
                    {
                        // Not known either way: nothing is remembered on the way back up.
                        cutShort = true;
                        return false;
                    }
                    foreach (var link in graph.Links(track, end.Other()))
                    {
                        if (Room(link.Track, link.End, wanted - length, depth + 1))
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (found)
                    hasRoom[key] = Math.Max(wanted, hasRoom.TryGetValue(key, out some) ? some : 0);
                else if (!cutShort)
                    noRoom[key] = Math.Min(wanted, noRoom.TryGetValue(key, out none) ? none : double.PositiveInfinity);
                return found;
            }

            var heap = new MinHeap();
            void Exit(int track, TrackEnd end, Lead lead, double cost)
            {
                var i = (track * 2 + (int)end) * 2 + (int)lead;
                if (cost < exit[i])
                {
                    exit[i] = cost;
                    heap.Push(i, cost);
                }
            }
            var start = pose.Position;
            foreach (var dir in new[] { TrackDirection.Forward, TrackDirection.Backward })
            {
                var end = dir.ExitEnd();
                var at = graph.OffsetOf(start.Track, end);
                // Cars standing between the train and that end: no way out there.
                if (Blocked(standing, start.Track, start.Offset + (int)dir * 0.5, at))
                    continue;
                var run = Math.Abs(at - start.Offset);
                foreach (var lead in Leads(pose, dir))
                    Exit(start.Track, end, lead, run);
            }
            while (heap.TryPop(out var node, out var cost))
            {
                if (cost > exit[node])
                    continue;
                var lead = (Lead)(node % 2);
                var track = node / 4;
                var end = (TrackEnd)(node / 2 % 2);
                foreach (var link in graph.Links(track, end))
                {
                    var e = (link.Track * 2 + (int)link.End) * 2 + (int)lead;
                    if (cost >= entry[e])
                        continue;
                    entry[e] = cost;
                    // On along the track entered, to its other end, when nothing stands on it.
                    if (Free(link.Track, link.End) >= graph.Length(link.Track) - 0.5)
                        Exit(link.Track, link.End.Other(), lead, cost + graph.Length(link.Track));
                    // Or wholly past the switch and back out the way it came, the other end leading.
                    cutShort = false;
                    if (Room(link.Track, link.End, need, 0))
                        Exit(link.Track, link.End, lead == Lead.Cars ? Lead.Loco : Lead.Cars, cost + need + Clearance + ReversalPenalty);
                }
            }
            return new TrainMoves(graph, pose, entry, standing);
        }

        private static IEnumerable<Lead> Leads(TrainPose pose, TrackDirection moving)
        {
            if (!pose.CarsToward.HasValue)
            {
                yield return Lead.Loco;
                yield return Lead.Cars;
            }
            else
            {
                yield return pose.CarsToward.Value == moving ? Lead.Cars : Lead.Loco;
            }
        }

        /// <summary>
        /// The running to arrive at <paramref name="to"/> moving
        /// <paramref name="arrive"/> along its track, with <paramref name="lead"/>
        /// leading (null: either); infinity when no way does.
        /// </summary>
        public double To(TrackPosition to, TrackDirection arrive, Lead? lead)
        {
            var best = double.PositiveInfinity;
            // Entered by the end it arrives from.
            var end = arrive == TrackEnd.Start.DirectionInto() ? TrackEnd.Start : TrackEnd.End;
            var from0 = graph.OffsetOf(to.Track, end);
            var along = Math.Abs(to.Offset - from0);
            // Up to the point, short of the cars standing there (the cut it is going to couple to, say).
            var clear = !Blocked(standing, to.Track, from0, to.Offset - (int)arrive * 0.5);
            for (var l = 0; l < 2 && clear; l++)
            {
                if (lead.HasValue && (int)lead.Value != l)
                    continue;
                var c = entry[(to.Track * 2 + (int)end) * 2 + l];
                best = Math.Min(best, c + along);
            }
            // Straight there along the track it stands on.
            var p = from.Position;
            if (to.Track == p.Track && (arrive == TrackDirection.Forward ? to.Offset >= p.Offset - 1e-6 : to.Offset <= p.Offset + 1e-6)
                && !Blocked(standing, p.Track, p.Offset + (int)arrive * 0.5, to.Offset - (int)arrive * 0.5))
            {
                foreach (var l in Leads(from, arrive))
                {
                    if (!lead.HasValue || lead.Value == l)
                        best = Math.Min(best, Math.Abs(to.Offset - p.Offset));
                }
            }
            return best;
        }

        /// <summary>A binary heap of (node, cost), cheapest first.</summary>
        private sealed class MinHeap
        {
            private readonly List<(int Node, double Key)> items = new List<(int, double)>();

            public void Push(int node, double key)
            {
                items.Add((node, key));
                var i = items.Count - 1;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (items[parent].Key <= items[i].Key)
                        break;
                    (items[parent], items[i]) = (items[i], items[parent]);
                    i = parent;
                }
            }

            public bool TryPop(out int node, out double key)
            {
                if (items.Count == 0)
                {
                    node = -1;
                    key = 0;
                    return false;
                }
                (node, key) = items[0];
                var last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (items.Count > 0)
                {
                    items[0] = last;
                    var i = 0;
                    while (true)
                    {
                        var l = i * 2 + 1;
                        var r = l + 1;
                        var smallest = i;
                        if (l < items.Count && items[l].Key < items[smallest].Key)
                            smallest = l;
                        if (r < items.Count && items[r].Key < items[smallest].Key)
                            smallest = r;
                        if (smallest == i)
                            break;
                        (items[smallest], items[i]) = (items[i], items[smallest]);
                        i = smallest;
                    }
                }
                return true;
            }
        }
    }
}
