using System;
using System.Collections.Generic;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>One end of a standing cut: its outer coupler, and the way out of the cut there.</summary>
    public readonly struct CutEnd
    {
        public readonly TrackPosition Position;
        public readonly TrackDirection Outward;

        public CutEnd(TrackPosition position, TrackDirection outward)
        {
            Position = position;
            Outward = outward;
        }
    }

    /// <summary>
    /// A set-out made by coupling to the cars standing on the track and
    /// shoving them further in (<see cref="OrderGoals.ShoveIn"/>).
    /// </summary>
    public readonly struct ShovePlan
    {
        /// <summary>The standing cuts coupled to, in turn from the entry (their occupancy owners).</summary>
        public readonly int[] Owners;

        /// <summary>Each one's end facing the entry: where each coupling is made.</summary>
        public readonly CutEnd[] CoupleTo;

        /// <summary>Where the far end of the last stops once shoved: the leading end's goal.</summary>
        public readonly TrackPosition ShoveTo;

        /// <summary>The way along the track the train moves going in.</summary>
        public readonly TrackDirection Inward;

        /// <summary>How far the standing cuts are shoved, m.</summary>
        public readonly double Shift;

        public ShovePlan(int[] owners, CutEnd[] coupleTo, TrackPosition shoveTo, TrackDirection inward, double shift)
        {
            Owners = owners;
            CoupleTo = coupleTo;
            ShoveTo = shoveTo;
            Inward = inward;
            Shift = shift;
        }

        /// <summary>The cut coupled to first, nearest the entry.</summary>
        public int Owner => Owners[0];
    }

    /// <summary>
    /// Goals for the orders a dispatcher gives by name rather than by a
    /// point on the map: "go to this track" and "pick up this cut".
    /// </summary>
    public static class OrderGoals
    {
        /// <summary>How far past a track's entry switch the whole train pulls, m.</summary>
        public const double ClearOfEntry = 15;

        /// <summary>How far short of a buffer stop the leading end stops, m.</summary>
        public const double BufferMargin = 4;

        /// <summary>Where short of a cut's end a coupling order points, m; the run in looks on from there.</summary>
        public const double ShortOfCut = 15;

        /// <summary>
        /// Goals that bring a train of <paramref name="trainLength"/> wholly
        /// onto a track: entering by either end that has track beyond it,
        /// the leading end stops with the rear 15 m clear of the entry, or as
        /// near the far end as it may when the train is longer than that.
        /// </summary>
        public static List<WaypointGoal> OnTrack(TrackGraph graph, int track, double trainLength)
        {
            var goals = new List<WaypointGoal>();
            var length = graph.Length(track);
            foreach (var entry in new[] { TrackEnd.Start, TrackEnd.End })
            {
                if (graph.Links(track, entry).Length == 0)
                    continue;
                var far = entry.Other();
                var room = length - (graph.Links(track, far).Length == 0 ? BufferMargin : 1);
                var along = Math.Max(0, Math.Min(room, trainLength + ClearOfEntry));
                var offset = entry == TrackEnd.Start ? along : length - along;
                goals.Add(new WaypointGoal(new TrackPosition(track, offset), entry.DirectionInto()));
            }
            // A track with no way in (an isolated stub): its middle, any way.
            if (goals.Count == 0)
                goals.Add(new WaypointGoal(new TrackPosition(track, length / 2)));
            return goals;
        }

        /// <summary>How far short of cars already standing on a track a set-out stops, m.</summary>
        public const double SpotGap = 8;

        /// <summary>
        /// Goals that set <paramref name="cutLength"/> meters of cars, shoved
        /// in ahead of the loco, wholly onto a track around the cars already
        /// standing there (<paramref name="standing"/>, the spans of cars at
        /// rest on it): by each end with track beyond it, as far in as the
        /// track goes (4 m short of a buffer, 15 m short of the switch at a
        /// through track's far end) or 8 m short of the first standing car,
        /// so the next set-out finds its room behind them. An entry whose
        /// clear stretch is too short gives no goal; when neither is long
        /// enough, the empty list says the track has no room. With
        /// <paramref name="entryOnly"/>, by that entry alone.
        /// </summary>
        public static List<WaypointGoal> SpotOn(TrackGraph graph, int track, double cutLength, IReadOnlyList<OccupiedSpan> standing,
            TrackEnd? entryOnly = null, double? maxDepth = null)
        {
            var goals = new List<WaypointGoal>();
            var length = graph.Length(track);
            foreach (var entry in new[] { TrackEnd.Start, TrackEnd.End })
            {
                if (graph.Links(track, entry).Length == 0 || (entryOnly.HasValue && entry != entryOnly.Value))
                    continue;
                var far = entry.Other();
                // The clear stretch in from this entry: to the first standing car, else the far end.
                var room = length - (graph.Links(track, far).Length == 0 ? BufferMargin : ClearOfEntry);
                foreach (var s in standing)
                {
                    var into = entry == TrackEnd.Start ? s.From : length - s.To;
                    room = Math.Min(room, into - SpotGap);
                }
                // On an empty long track, cap depth when requested so a short cut isn't shoved
                // 650+ m to the opposite end switch when far less track is needed.
                if (standing.Count == 0 && maxDepth.HasValue)
                    room = Math.Min(room, Math.Max(cutLength + ClearOfEntry, maxDepth.Value));
                // The cars wholly on the track, their inner end clear of the entry switch.
                if (room < cutLength + ClearOfEntry)
                    continue;
                var offset = entry == TrackEnd.Start ? room : length - room;
                goals.Add(new WaypointGoal(new TrackPosition(track, offset), entry.DirectionInto()));
            }
            return goals;
        }

        public static ShovePlan? ShoveIn(TrackGraph graph, int track, double cutLength, IReadOnlyList<OccupiedSpan> standing, int? join = null,
            Func<int, bool> movable = null, TrackEnd? entryOnly = null)
        {
            ShovePlan? best = null;
            var bestCuts = int.MaxValue;
            var bestSpare = double.NegativeInfinity;
            var length = graph.Length(track);
            foreach (var entry in new[] { TrackEnd.Start, TrackEnd.End })
            {
                if (graph.Links(track, entry).Length == 0 || standing.Count == 0 || (entryOnly.HasValue && entry != entryOnly.Value))
                    continue;
                var limit = length - (graph.Links(track, entry.Other()).Length == 0 ? BufferMargin : ClearOfEntry);
                // In from this entry: each cut's near and far edges, all its spans together, nearest first.
                var lots = new List<(int Owner, double Near, double Far)>();
                foreach (var s in standing)
                {
                    var near = entry == TrackEnd.Start ? s.From : length - s.To;
                    var far = entry == TrackEnd.Start ? s.To : length - s.From;
                    var k = lots.FindIndex(l => l.Owner == s.Owner);
                    if (k < 0)
                        lots.Add((s.Owner, near, far));
                    else
                        lots[k] = (s.Owner, Math.Min(lots[k].Near, near), Math.Max(lots[k].Far, far));
                }
                lots.Sort((a, b) => a.Near.CompareTo(b.Near));
                if (join.HasValue && lots[0].Owner != join.Value)
                    continue;
                double Offset(double along) => entry == TrackEnd.Start ? along : length - along;
                for (var count = 1; count <= lots.Count && count <= bestCuts; count++)
                {
                    // A cut that can't be moved (one with a loco, say) is a wall: shove up to it, never through it.
                    if (movable != null && !movable(lots[count - 1].Owner))
                        break;
                    var lotFar = 0.0;
                    var occupied = 0.0;
                    for (var k = 0; k < count; k++)
                    {
                        lotFar = Math.Max(lotFar, lots[k].Far);
                        occupied += lots[k].Far - lots[k].Near;
                    }
                    // Coupled together the cuts close up against the farthest: the gaps between them are room won.
                    var lotNear = lotFar - occupied;
                    // How far they can go: to 8 m short of the next cars, or the far limit.
                    var stop = count < lots.Count ? lots[count].Near - SpotGap : limit;
                    var most = Math.Max(0, stop - lotFar);
                    var need = Math.Max(0, cutLength + ClearOfEntry - lotNear);
                    if (need > most)
                        continue;
                    var shift = join.HasValue ? need : most;
                    var spare = lotNear + shift - cutLength - ClearOfEntry;
                    if (count < bestCuts || spare > bestSpare)
                    {
                        var owners = new int[count];
                        var ends = new CutEnd[count];
                        for (var k = 0; k < count; k++)
                        {
                            owners[k] = lots[k].Owner;
                            ends[k] = new CutEnd(new TrackPosition(track, Offset(lots[k].Near)), entry.DirectionInto().Reverse());
                        }
                        best = new ShovePlan(owners, ends, new TrackPosition(track, Offset(lotFar + shift)), entry.DirectionInto(), shift);
                        bestCuts = count;
                        bestSpare = spare;
                    }
                    break;
                }
            }
            return best;
        }

        /// <summary>The longest cut <see cref="SpotOn"/> can set out on the track now, m (0 when none).</summary>
        public static double SpotRoom(TrackGraph graph, int track, IReadOnlyList<OccupiedSpan> standing, TrackEnd? entryOnly = null)
        {
            var best = 0.0;
            var length = graph.Length(track);
            foreach (var entry in new[] { TrackEnd.Start, TrackEnd.End })
            {
                if (graph.Links(track, entry).Length == 0 || (entryOnly.HasValue && entry != entryOnly.Value))
                    continue;
                var room = length - (graph.Links(track, entry.Other()).Length == 0 ? BufferMargin : ClearOfEntry);
                foreach (var s in standing)
                    room = Math.Min(room, (entry == TrackEnd.Start ? s.From : length - s.To) - SpotGap);
                best = Math.Max(best, room - ClearOfEntry);
            }
            return best;
        }

        /// <summary>An order onto a track, sized for the train when it starts.</summary>
        public static Waypoint ToTrack(TrackGraph graph, int track, double trainLength, string label = null,
            Arrival arrival = Arrival.CarsFirstIntoDeadEnd) =>
            new Waypoint(OnTrack(graph, track, trainLength))
            {
                Label = label,
                GoalsForLength = length => OnTrack(graph, track, length),
                Arrival = arrival,
                IntoDeadEnd = graph.Links(track, TrackEnd.Start).Length == 0 || graph.Links(track, TrackEnd.End).Length == 0,
            };

        /// <summary>
        /// Goals for coupling to a cut: short of each end, arriving toward
        /// the cut, so a Couple order runs in from whichever end is nearer.
        /// </summary>
        public static List<WaypointGoal> ToCouple(TrackGraph graph, IEnumerable<CutEnd> ends, Func<int, int> selectedBranch)
        {
            var goals = new List<WaypointGoal>();
            foreach (var end in ends)
            {
                var at = TrackWalker.Advance(graph, end.Position, end.Outward, ShortOfCut, selectedBranch, out var outward);
                goals.Add(new WaypointGoal(at, outward.Reverse()));
            }
            return goals;
        }

        /// <summary>
        /// Goals for coupling to a cut end that may stand just beyond one of
        /// the train's ends, as when a pull through a line of cuts meets the
        /// next, or a loco cut off from cars couples back on: the usual goal,
        /// short of the cut, would lie under the train or behind it. When the
        /// cut's end is less than <see cref="ShortOfCut"/> on from the far
        /// end (either end, running light), the goal is halfway across the
        /// gap; otherwise the usual goals.
        /// </summary>
        public static List<WaypointGoal> CoupleOnward(TrackGraph graph, TrainState train, CutEnd target, Func<int, int> selectedBranch)
        {
            var ends = train.LightEngine ? new[] { ConsistEnd.A, ConsistEnd.B } : new[] { train.LocoEnd.Other() };
            foreach (var end in ends)
            {
                var at = end == ConsistEnd.A ? train.EndA : train.EndB;
                var outward = end == ConsistEnd.A ? train.OutwardA : train.OutwardB;
                var walk = TrackWalker.Walk(graph, at, outward, ShortOfCut + 5, selectedBranch, end, out _);
                if (walk.TryLocate(target.Position, 0, 0.5, out var gap, out _) && gap < ShortOfCut)
                {
                    var goal = TrackWalker.Advance(graph, at, outward, Math.Max(0.05, gap / 2), selectedBranch, out var direction);
                    return new List<WaypointGoal> { new WaypointGoal(goal, direction) };
                }
            }
            return ToCouple(graph, new[] { target }, selectedBranch);
        }

        /// <summary>
        /// Goals for coupling on one end of a cut, named by <paramref name="names"/>;
        /// the other end is a second choice, taken (and said) only when the
        /// first can't be reached.
        /// </summary>
        public static List<WaypointGoal> ToCouple(TrackGraph graph, IList<CutEnd> ends, int preferred, IList<string> names, Func<int, int> selectedBranch)
        {
            var goals = ToCouple(graph, ends, selectedBranch);
            var other = 1 - preferred;
            return new List<WaypointGoal>
            {
                goals[preferred],
                goals[other].AsSecondChoice($"coupling on the {names[other]} end: the {names[preferred]} end can't be reached"),
            };
        }

        /// <summary>
        /// Which of a cut's two ends would lead it most cheaply to
        /// <paramref name="destination"/>: the end a loco couples to so it
        /// heads the train there. <paramref name="trainLength"/> is the cut
        /// with the loco. Null when no route reaches it.
        /// </summary>
        public static int? EndToward(Router router, CutEnd a, CutEnd b, double trainLength, TrackPosition destination)
        {
            var route = router.Find(new RouteRequest
            {
                EndA = a.Position,
                OutwardA = a.Outward,
                EndB = b.Position,
                OutwardB = b.Outward,
                TrainLength = trainLength,
                Goal = destination,
            }, out _);
            if (route == null)
                return null;
            // The end that leads the run there: the longest leg's, not that of a short pull
            // clear of a switch, or the first move of a switchback.
            var main = route.Legs[0];
            foreach (var leg in route.Legs)
            {
                if (leg.Length > main.Length)
                    main = leg;
            }
            return main.Leader == ConsistEnd.A ? 0 : 1;
        }
    }
}
