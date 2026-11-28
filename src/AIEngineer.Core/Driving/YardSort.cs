using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>How cars in a yard are grouped onto its classification tracks.</summary>
    public enum SortMode
    {
        /// <summary>By destination yard (e.g. HB, SM, GF); cars without a job form their own outbound block.</summary>
        ByDestination,

        /// <summary>By job ID; cars without a job form their own outbound block.</summary>
        ByJob,
    }

    /// <summary>One classification track in a yard being sorted.</summary>
    public sealed class SortTrack
    {
        public int Track;
        public string Name;
        public double Length;

        /// <summary>Which end faces the active switching ladder (where pulls come out and set-outs shove in).</summary>
        public TrackEnd WorkingEnd;

        /// <summary>Whether the inner (non-working) end terminates in a buffer stop.</summary>
        public bool DeadEnd;

        /// <summary>True when an immovable train (another loco, derailed car, or fouling cut) prevents using this track.</summary>
        public bool Blocked;

        /// <summary>Why the track is blocked, for the preview and log.</summary>
        public string BlockedReason;

        /// <summary>Clear usable length between entry clearance and inner stop limit, m.</summary>
        public double UsableLength => Math.Max(0, Length - OrderGoals.ClearOfEntry - (DeadEnd ? OrderGoals.BufferMargin : OrderGoals.ClearOfEntry));
    }

    /// <summary>One freight car known to the yard sorter.</summary>
    public sealed class SortCar
    {
        public int Id;

        /// <summary>Primary classification key (destination yard code, job ID, or "" for cars without a job).</summary>
        public string Key = "";

        /// <summary>Optional display label for the key (e.g. "No job" for "").</summary>
        public string KeyLabel;

        /// <summary>Car length, coupler to coupler, m.</summary>
        public double Length = 14;

        /// <summary>Index of the standing cut this car belongs to, or -1 if coupled to the switcher.</summary>
        public int CutIndex = -1;

        public string DisplayKey => !string.IsNullOrEmpty(KeyLabel) ? KeyLabel : string.IsNullOrEmpty(Key) ? "No job" : Key;
    }

    /// <summary>One coupled cut standing on a yard track.</summary>
    public sealed class SortCut
    {
        public int Owner;
        public int Track;

        /// <summary>Coupler facing the working ladder.</summary>
        public CutEnd OuterEnd;

        /// <summary>Coupler facing the inner end.</summary>
        public CutEnd InnerEnd;

        /// <summary>Distance from the track's working end to the outer coupler, m (may be negative if overhanging).</summary>
        public double NearFromWorking;

        /// <summary>Distance from the track's working end to the inner coupler, m.</summary>
        public double FarFromWorking;

        /// <summary>Cars in this cut ordered from inner end toward the working ladder.</summary>
        public readonly List<SortCar> CarsInnerToOuter = new List<SortCar>();

        public double Length
        {
            get
            {
                var sum = 0.0;
                for (var i = 0; i < CarsInnerToOuter.Count; i++)
                    sum += CarsInnerToOuter[i].Length;
                return sum;
            }
        }
    }

    /// <summary>Current occupancy of one yard track, ordered from its inner end toward its working ladder.</summary>
    public sealed class YardTrackState
    {
        public SortTrack Track;

        /// <summary>Standing cuts on this track, innermost first and outermost (nearest the ladder) last.</summary>
        public readonly List<SortCut> CutsInnerToOuter = new List<SortCut>();

        public int TotalCars
        {
            get
            {
                var n = 0;
                for (var i = 0; i < CutsInnerToOuter.Count; i++)
                    n += CutsInnerToOuter[i].CarsInnerToOuter.Count;
                return n;
            }
        }

        public double OccupiedLength
        {
            get
            {
                var sum = 0.0;
                for (var i = 0; i < CutsInnerToOuter.Count; i++)
                    sum += CutsInnerToOuter[i].Length;
                return sum;
            }
        }

        /// <summary>All cars on this track from inner end toward the working ladder.</summary>
        public List<SortCar> AllCarsInnerToOuter()
        {
            var list = new List<SortCar>(TotalCars);
            for (var i = 0; i < CutsInnerToOuter.Count; i++)
                list.AddRange(CutsInnerToOuter[i].CarsInnerToOuter);
            return list;
        }
    }

    /// <summary>Per-track target in a classification plan, for UI preview and logging.</summary>
    public sealed class TrackPlanLine
    {
        public int Track;
        public string TrackName;
        public double TrackLength;
        public bool Blocked;
        public string BlockedReason;

        /// <summary>Ordered classification keys assigned to this track (inner to outer); empty for a spare/staging track.</summary>
        public readonly List<string> Keys = new List<string>();

        public int PlannedCars;
        public double PlannedLength;
        public int AlreadyInPlace;
    }

    /// <summary>
    /// Fixed classification plan for a yard sort run: which keys go to which tracks
    /// (and in what inner-to-outer sub-block order), which ladder end each track is
    /// worked from, and how long a cut can be pulled onto the switching lead.
    /// </summary>
    public sealed class SortAssignment
    {
        /// <summary>Key -> primary target track index. For overflowed keys split across tracks, see <see cref="KeyTracks"/>.</summary>
        public readonly Dictionary<string, int> KeyToTrack = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Key -> all target tracks in fill order (usually 1; >1 only when a single key exceeds one track's length).</summary>
        public readonly Dictionary<string, List<int>> KeyTracks = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        /// <summary>Track index -> ordered list of keys assigned to that track (inner end to working end); empty list for usable spare tracks.</summary>
        public readonly Dictionary<int, List<string>> TrackKeys = new Dictionary<int, List<string>>();

        /// <summary>Track index -> working end facing the active switching ladder.</summary>
        public readonly Dictionary<int, TrackEnd> WorkingEnds = new Dictionary<int, TrackEnd>();

        /// <summary>Usable tracks with no keys assigned (free for sluffing/staging).</summary>
        public readonly List<int> SpareTracks = new List<int>();

        /// <summary>Display summary per track in the yard's order.</summary>
        public readonly List<TrackPlanLine> Lines = new List<TrackPlanLine>();

        /// <summary>Problems preventing a sort (or warnings), phrased for the player.</summary>
        public readonly List<string> Problems = new List<string>();

        /// <summary>Maximum car cut length (excluding locomotive) that fits on the switching lead, m.</summary>
        public double MaxCutLength = 180;

        /// <summary>True when every car is already on its assigned track in valid order and the loco holds no sortable cars.</summary>
        public bool AlreadySorted;

        /// <summary>Total number of cars covered by this sort.</summary>
        public int TotalCars;

        /// <summary>True when the plan can be executed (no fatal problems).</summary>
        public bool Valid => Problems.Count == 0;

        public bool TryTrackFor(string key, out int track) => KeyToTrack.TryGetValue(key ?? "", out track);

        public static string FormatKey(string key) => string.IsNullOrEmpty(key) ? "No job" : key;
    }

    public enum SortActionKind
    {
        /// <summary>All cars are sorted onto their assigned tracks and consolidated.</summary>
        Done,

        /// <summary>Cannot continue safely (e.g. yard gridlocked, assigned track became blocked, or unknown key appeared).</summary>
        Blocked,

        /// <summary>Pull one or more cuts from a track's working end, optionally cutting off anchored/excess cars behind.</summary>
        Pull,

        /// <summary>Set out a contiguous run of cars from the far end of the switcher onto a target or sluff track.</summary>
        SetOut,

        /// <summary>Shove separated cuts of already-sorted cars on a track together into one coupled cut.</summary>
        Consolidate,
    }

    /// <summary>One concrete switching move returned by <see cref="YardSort.NextAction"/>.</summary>
    public sealed class SortAction
    {
        public SortActionKind Kind;

        /// <summary>For Blocked: true when live yard changes (blocked track, new key) warrant replanning the assignment.</summary>
        public bool NeedsReplan;

        /// <summary>Track being pulled from, set out onto, or consolidated.</summary>
        public int Track = -1;

        /// <summary>For Pull: the cuts to couple through, outermost first (from working end inward).</summary>
        public readonly List<SortCut> PullCuts = new List<SortCut>();

        /// <summary>For Pull: exact car IDs in the deepest pulled cut that must stay behind on the track.</summary>
        public readonly HashSet<int> LeaveBehindCarIds = new HashSet<int>();

        /// <summary>For Pull: total number of cars pulled onto the switcher.</summary>
        public int PullCount;

        /// <summary>For Pull: true if this pull removes all remaining unsorted cars from the track.</summary>
        public bool ClearsTrack;

        /// <summary>For SetOut: number of far-end cars to cut off.</summary>
        public int SetOutCount;

        /// <summary>For SetOut: exact car IDs in the far-end run being set out.</summary>
        public readonly HashSet<int> SetOutCarIds = new HashSet<int>();

        /// <summary>For SetOut: classification key of the cars being set out.</summary>
        public string Key;

        /// <summary>For SetOut: true when coupling onto already-sorted cars on the target track.</summary>
        public bool JoinSorted;

        /// <summary>For SetOut: true when staging temporarily on a sluff/spare track.</summary>
        public bool IsSluff;

        /// <summary>Human-readable description of the move or block reason.</summary>
        public string Reason;
    }

    /// <summary>Everything needed to plan a yard sort's working ladder, lead capacity, and track assignment off the game's thread.</summary>
    public sealed class YardSortSetupRequest
    {
        public IEngineerWorld World;
        public TrainState Train;

        /// <summary>Actual length (m) of power/tenders/cabooses that stay with the switcher; 0 falls back to Train.</summary>
        public double PowerLength;

        public List<YardTrackState> Tracks;
        public List<SortCar> InTrain;

        /// <summary>Optional representative track index for each destination key, for corridor proximity clustering.</summary>
        public Dictionary<string, int> KeyRepTrack;

        /// <summary>Optional track index representing this yard, for ordering multi-key tracks farthest-first.</summary>
        public int YardRepTrack = -1;
    }

    public sealed class YardSortSetupResult
    {
        public SortAssignment Assignment;
        public readonly List<string> Notes = new List<string>();
        public double Milliseconds;
    }

    /// <summary>
    /// Engine-free yard classification (flat switching) planner.
    /// <para>
    /// Given a yard's bowl tracks and the cars standing on them (plus any cars on the switcher),
    /// it chooses the best switching ladder, measures the physical lead length with the router,
    /// assigns classification keys to tracks (with backtracking corridor clustering when destinations
    /// exceed tracks and exact whole-car overflow packing), and then plans each pull / set-out /
    /// consolidation step monotonically with cycle detection.
    /// </para>
    /// </summary>
    public static class YardSort
    {
        /// <summary>Minimum usable track length to participate in sorting, m.</summary>
        public const double MinUsableTrack = 25;

        /// <summary>Safety margin kept between cut length and track usable capacity, m.</summary>
        public const double TrackSlack = 6;

        /// <summary>Safety margin subtracted from measured physical lead capacity, m.</summary>
        public const double LeadSafetyMargin = 15;

        /// <summary>Upper bound for lead capacity binary search (loco + cut), m.</summary>
        public const double MaxLeadSearchLength = 340;

        /// <summary>Minimum cut length we consider viable for switching (roughly two short cars), m.</summary>
        public const double MinCutLength = 24;

        /// <summary>Maximum distance past either end of a bowl track to locate an overhanging coupler, m.</summary>
        public const double OverhangSearchLimit = 60;

        /// <summary>Bonus score for assigning a group to an already-clean track (empty or pure), in equivalent anchored cars.</summary>
        private const double CleanTrackBonus = 2.0;

        /// <summary>Maximum consecutive hook-swap pulls without setting out cars.</summary>
        public const int MaxConsecutiveHookSwaps = 2;

        // ---- Coordinate-independent cut orientation helper ----

        /// <summary>
        /// Locates a cut's two outer couplers (<paramref name="endA"/>, <paramref name="endB"/>) along
        /// <paramref name="track"/> measured from <paramref name="workingEnd"/>, handling overhanging
        /// couplers across reversed (Start-to-Start / End-to-End) track joints via <see cref="TrackWalker"/>.
        /// Returns false if either end cannot be located on the track or within <see cref="OverhangSearchLimit"/> m of its ends.
        /// </summary>
        public static bool LocateCutOnTrack(TrackGraph graph, int track, TrackEnd workingEnd,
            TrackPosition endA, TrackPosition endB, Func<int, int> lie,
            out bool aIsOuter, out double nearFromWorking, out double farFromWorking)
        {
            aIsOuter = true;
            nearFromWorking = 0;
            farFromWorking = 0;
            if (graph == null || track < 0 || track >= graph.TrackCount)
                return false;
            var len = graph.Length(track);
            var workingOffset = graph.OffsetOf(track, workingEnd);
            var innerEnd = workingEnd.Other();
            var innerOffset = graph.OffsetOf(track, innerEnd);
            var outWorking = workingEnd == TrackEnd.Start ? TrackDirection.Backward : TrackDirection.Forward;
            var outInner = innerEnd == TrackEnd.Start ? TrackDirection.Backward : TrackDirection.Forward;

            // Lazily walk outward past workingEnd and innerEnd only when an overhang occurs.
            RouteLeg walkWorking = null;
            RouteLeg walkInner = null;

            bool TryAlong(TrackPosition p, out double along)
            {
                if (p.Track == track)
                {
                    along = workingEnd == TrackEnd.Start ? p.Offset : len - p.Offset;
                    return true;
                }
                if (walkWorking == null)
                    walkWorking = TrackWalker.Walk(graph, new TrackPosition(track, workingOffset), outWorking, OverhangSearchLimit, lie, ConsistEnd.A, out _);
                if (walkWorking.TryLocate(p, 0, 0.5, out var beyondWorking, out _))
                {
                    along = -beyondWorking;
                    return true;
                }
                if (walkInner == null)
                    walkInner = TrackWalker.Walk(graph, new TrackPosition(track, innerOffset), outInner, OverhangSearchLimit, lie, ConsistEnd.A, out _);
                if (walkInner.TryLocate(p, 0, 0.5, out var beyondInner, out _))
                {
                    along = len + beyondInner;
                    return true;
                }
                along = 0;
                return false;
            }

            if (!TryAlong(endA, out var da) || !TryAlong(endB, out var db))
                return false;
            aIsOuter = da <= db;
            nearFromWorking = Math.Min(da, db);
            farFromWorking = Math.Max(da, db);
            return true;
        }

        // ---- Setup off the game's thread ----

        /// <summary>
        /// Runs ladder selection, physical lead measurement, corridor distance calculation, and
        /// optimal track assignment against a frozen world (safe on a background thread).
        /// </summary>
        public static YardSortSetupResult PlanSetup(YardSortSetupRequest request)
        {
            var watch = Stopwatch.StartNew();
            var result = new YardSortSetupResult();
            var world = request.World;
            Func<int, int> lie = world.SelectedBranch;
            var tracks = request.Tracks.Select(t => t.Track).ToList();
            var initialEnds = new Dictionary<int, TrackEnd>(tracks.Count);
            foreach (var t in tracks)
                initialEnds[t.Track] = t.WorkingEnd;
            var powerLen = request.PowerLength > 1 ? request.PowerLength
                : request.Train.LightEngine ? Math.Max(12, request.Train.Length)
                : Math.Max(16, Math.Max(1, request.Train.PowerCars) * 16);
            ChooseWorkingEnds(world, lie, request.Train, tracks, out var maxCut, result.Notes, powerLen);

            // Re-orient cuts/cars on any track whose WorkingEnd flipped from the initial reading,
            // and clear cuts on tracks marked blocked by reachability checks.
            foreach (var ts in request.Tracks)
            {
                if (ts.Track.Blocked)
                {
                    ts.CutsInnerToOuter.Clear();
                    continue;
                }
                if (initialEnds.TryGetValue(ts.Track.Track, out var was) && was != ts.Track.WorkingEnd)
                    ReorientTrack(ts);
            }

            Func<string, string, double> keyDist = null;
            Func<string, double> originDist = null;
            if (request.KeyRepTrack != null && request.KeyRepTrack.Count > 1)
                BuildDistances(world.Graph, request.KeyRepTrack, request.YardRepTrack, out keyDist, out originDist);

            var assignment = Assign(request.Tracks, request.InTrain, keyDist, originDist);
            assignment.MaxCutLength = maxCut;
            if (assignment.Valid && !assignment.AlreadySorted)
            {
                if (maxCut < MinCutLength)
                {
                    assignment.Problems.Add("the switching lead has too little clear room to pull and reverse cars between tracks");
                }
                else
                {
                    var longestCar = 0.0;
                    foreach (var ts in request.Tracks)
                        if (assignment.TrackKeys.ContainsKey(ts.Track.Track))
                            foreach (var c in ts.AllCarsInnerToOuter())
                                if (c.Length > longestCar)
                                    longestCar = c.Length;
                    if (request.InTrain != null)
                        foreach (var c in request.InTrain)
                            if (c.Length > longestCar)
                                longestCar = c.Length;
                    if (longestCar > maxCut + 1e-6)
                        assignment.Problems.Add($"the switching lead only fits {maxCut:F0} m of cars, too short for a {longestCar:F0} m car");
                }
            }
            result.Assignment = assignment;
            result.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Flips a track's cuts and cars when its <see cref="SortTrack.WorkingEnd"/> is reversed
        /// (inner becomes outer and vice versa).
        /// </summary>
        public static void ReorientTrack(YardTrackState ts)
        {
            if (ts == null || ts.CutsInnerToOuter.Count == 0)
                return;
            var len = ts.Track.Length;
            ts.CutsInnerToOuter.Reverse();
            foreach (var cut in ts.CutsInnerToOuter)
            {
                cut.CarsInnerToOuter.Reverse();
                var oldOuter = cut.OuterEnd;
                cut.OuterEnd = cut.InnerEnd;
                cut.InnerEnd = oldOuter;
                var oldNear = cut.NearFromWorking;
                var oldFar = cut.FarFromWorking;
                cut.NearFromWorking = len - oldFar;
                cut.FarFromWorking = len - oldNear;
            }
        }

        /// <summary>
        /// Computes rail distances between destination keys and from the yard, using
        /// <see cref="GraphDistances"/> once per unique destination key.
        /// </summary>
        public static void BuildDistances(TrackGraph graph, Dictionary<string, int> keyRepTrack, int yardRepTrack,
            out Func<string, string, double> keyDistance, out Func<string, double> originDistance)
        {
            var maps = new Dictionary<string, GraphDistances>(StringComparer.Ordinal);
            foreach (var kv in keyRepTrack)
            {
                if (kv.Value >= 0 && kv.Value < graph.TrackCount)
                    maps[kv.Key] = GraphDistances.From(graph, new TrackPosition(kv.Value, graph.Length(kv.Value) / 2));
            }
            var fromYard = yardRepTrack >= 0 && yardRepTrack < graph.TrackCount
                ? GraphDistances.From(graph, new TrackPosition(yardRepTrack, graph.Length(yardRepTrack) / 2))
                : null;

            keyDistance = (a, b) =>
            {
                if (string.Equals(a, b, StringComparison.Ordinal))
                    return 0;
                if (maps.TryGetValue(a, out var da) && keyRepTrack.TryGetValue(b, out var tb) && tb >= 0 && tb < graph.TrackCount)
                    return da.To(new TrackPosition(tb, graph.Length(tb) / 2));
                return double.PositiveInfinity;
            };
            originDistance = k =>
            {
                if (fromYard != null && keyRepTrack.TryGetValue(k, out var tk) && tk >= 0 && tk < graph.TrackCount)
                    return fromYard.To(new TrackPosition(tk, graph.Length(tk) / 2));
                return 0;
            };
        }

        /// <summary>
        /// Picks which end of each yard track faces the active switching ladder, marks any track
        /// unreachable from that ladder as <see cref="SortTrack.Blocked"/>, and measures how long
        /// a cut can be pulled past the ladder switches and reversed into every reachable track.
        /// </summary>
        public static void ChooseWorkingEnds(IEngineerWorld world, Func<int, int> lie, TrainState loco,
            IReadOnlyList<SortTrack> tracks, out double maxCutLength, List<string> notes = null, double powerLength = 0)
        {
            maxCutLength = 150;
            var graph = world.Graph;
            var usable = tracks.Where(t => !t.Blocked).ToList();
            if (usable.Count < 2)
            {
                maxCutLength = 0;
                return;
            }

            // Pick a reference track with at least one open end (prefer double-ended if any).
            var refTrack = usable.FirstOrDefault(t => graph.Links(t.Track, TrackEnd.Start).Length > 0 && graph.Links(t.Track, TrackEnd.End).Length > 0)
                ?? usable.FirstOrDefault(t => graph.Links(t.Track, TrackEnd.Start).Length > 0 || graph.Links(t.Track, TrackEnd.End).Length > 0);
            if (refTrack == null)
            {
                maxCutLength = 0;
                return;
            }

            var locoLength = powerLength > 1 ? powerLength
                : loco.LightEngine ? Math.Max(12, loco.Length)
                : Math.Max(16, Math.Max(1, loco.PowerCars) * 16);
            var locoPos = loco.EndA;
            var fromLoco = locoPos.Track >= 0 && locoPos.Track < graph.TrackCount
                ? GraphDistances.From(graph, locoPos)
                : null;

            // Evaluate each open end of the reference track as a candidate switching ladder.
            var candidates = new List<(Dictionary<int, TrackEnd> Ends, HashSet<int> Switched, double LeadCut, double LocoDist)>();
            foreach (var refEnd in new[] { TrackEnd.Start, TrackEnd.End })
            {
                if (graph.Links(refTrack.Track, refEnd).Length == 0)
                    continue;
                var exitDir = refEnd == TrackEnd.Start ? TrackDirection.Backward : TrackDirection.Forward;
                var probe = TrackWalker.Advance(graph, new TrackPosition(refTrack.Track, graph.OffsetOf(refTrack.Track, refEnd)),
                    exitDir, 5, lie, out _);
                var fromProbe = GraphDistances.From(graph, probe);
                var ends = new Dictionary<int, TrackEnd>();
                var openTracks = new List<SortTrack>();
                foreach (var t in usable)
                {
                    var hasStart = graph.Links(t.Track, TrackEnd.Start).Length > 0;
                    var hasEnd = graph.Links(t.Track, TrackEnd.End).Length > 0;
                    if (!hasStart && !hasEnd)
                        continue;
                    TrackEnd chosen;
                    if (t.Track == refTrack.Track)
                        chosen = refEnd;
                    else if (hasStart && !hasEnd)
                        chosen = TrackEnd.Start;
                    else if (hasEnd && !hasStart)
                        chosen = TrackEnd.End;
                    else
                    {
                        var ds = fromProbe.To(new TrackPosition(t.Track, 0));
                        var de = fromProbe.To(new TrackPosition(t.Track, t.Length));
                        chosen = ds <= de ? TrackEnd.Start : TrackEnd.End;
                    }
                    ends[t.Track] = chosen;
                    openTracks.Add(t);
                }
                if (openTracks.Count < 2)
                    continue;

                // Keep only tracks proven reachable both ways from this ladder at minimum switching length.
                var testTrainLen = locoLength + MinCutLength;
                var switchedList = new List<SortTrack> { refTrack };
                var switchedSet = new HashSet<int> { refTrack.Track };
                foreach (var other in openTracks)
                {
                    if (other.Track == refTrack.Track)
                        continue;
                    if (CanSwitchBetween(world, lie, loco.Owner, refTrack.Track, ends[refTrack.Track], other.Track, ends[other.Track], testTrainLen)
                        && CanSwitchBetween(world, lie, loco.Owner, other.Track, ends[other.Track], refTrack.Track, ends[refTrack.Track], testTrainLen))
                    {
                        switchedList.Add(other);
                        switchedSet.Add(other.Track);
                    }
                }
                if (switchedList.Count < 2)
                    continue;

                var leadCut = MeasureLeadCutLength(world, lie, loco.Owner, refTrack, switchedList, ends, locoLength);
                var locoDist = fromLoco != null ? fromLoco.To(probe) : 0;
                candidates.Add((ends, switchedSet, leadCut, locoDist));
                notes?.Add($"ladder at {refTrack.Name} {refEnd}: {switchedList.Count}/{usable.Count} tracks, lead fits {leadCut:F0} m of cars, {locoDist:F0} m from loco");
            }

            if (candidates.Count == 0)
            {
                foreach (var t in tracks)
                {
                    var hasStart = graph.Links(t.Track, TrackEnd.Start).Length > 0;
                    var hasEnd = graph.Links(t.Track, TrackEnd.End).Length > 0;
                    t.WorkingEnd = hasStart || !hasEnd ? TrackEnd.Start : TrackEnd.End;
                    t.DeadEnd = graph.Links(t.Track, t.WorkingEnd.Other()).Length == 0;
                }
                maxCutLength = 0;
                return;
            }

            var best = candidates[0];
            for (var i = 1; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Switched.Count != best.Switched.Count)
                {
                    if (c.Switched.Count > best.Switched.Count)
                        best = c;
                    continue;
                }
                var bestAmple = best.LeadCut >= 120;
                var cAmple = c.LeadCut >= 120;
                if (bestAmple != cAmple)
                {
                    if (cAmple)
                        best = c;
                    continue;
                }
                if (!bestAmple && !cAmple && Math.Abs(c.LeadCut - best.LeadCut) > 15)
                {
                    if (c.LeadCut > best.LeadCut)
                        best = c;
                    continue;
                }
                if (c.LocoDist < best.LocoDist)
                    best = c;
            }

            foreach (var t in tracks)
            {
                if (!t.Blocked && !best.Switched.Contains(t.Track))
                {
                    t.Blocked = true;
                    t.BlockedReason = "not reachable from the working ladder";
                }
                if (best.Ends.TryGetValue(t.Track, out var end))
                    t.WorkingEnd = end;
                else
                {
                    var hasStart = graph.Links(t.Track, TrackEnd.Start).Length > 0;
                    var hasEnd = graph.Links(t.Track, TrackEnd.End).Length > 0;
                    t.WorkingEnd = hasStart || !hasEnd ? TrackEnd.Start : TrackEnd.End;
                }
                t.DeadEnd = graph.Links(t.Track, t.WorkingEnd.Other()).Length == 0;
            }
            maxCutLength = best.LeadCut;
        }

        /// <summary>
        /// Binary-searches the longest cut length (m, excluding power) that can switch between
        /// ALL reachable tracks of the ladder without exceeding lead clearance or hitting standing cars.
        /// </summary>
        private static double MeasureLeadCutLength(IEngineerWorld world, Func<int, int> lie, int owner,
            SortTrack refTrack, List<SortTrack> tracks, Dictionary<int, TrackEnd> ends, double locoLength)
        {
            if (tracks.Count < 2)
                return 0;
            double lo = 0;
            double hi = Math.Max(MinCutLength, MaxLeadSearchLength - locoLength);
            bool AllFit(double cutLen)
            {
                var trainLen = locoLength + cutLen;
                // Check every unordered pair of reachable tracks so no direct move can exceed lead room.
                for (var i = 0; i < tracks.Count; i++)
                {
                    for (var j = i + 1; j < tracks.Count; j++)
                    {
                        if (!FitsLead(world, lie, owner, tracks[i].Track, ends[tracks[i].Track], tracks[j].Track, ends[tracks[j].Track], trainLen))
                            return false;
                    }
                }
                return true;
            }
            if (AllFit(hi))
                return Math.Max(0, hi - LeadSafetyMargin);
            for (var iter = 0; iter < 6; iter++)
            {
                var mid = (lo + hi) / 2;
                if (AllFit(mid))
                    lo = mid;
                else
                    hi = mid;
            }
            return Math.Max(0, lo - LeadSafetyMargin);
        }

        private static bool FitsLead(IEngineerWorld world, Func<int, int> lie, int owner,
            int trackA, TrackEnd endA, int trackB, TrackEnd endB, double trainLength) =>
            CanSwitchBetween(world, lie, owner, trackA, endA, trackB, endB, trainLength)
            && CanSwitchBetween(world, lie, owner, trackB, endB, trackA, endA, trainLength);

        private static bool CanSwitchBetween(IEngineerWorld world, Func<int, int> lie, int owner,
            int fromTrack, TrackEnd fromEnd, int toTrack, TrackEnd toEnd, double trainLength)
        {
            var graph = world.Graph;
            var fromLen = graph.Length(fromTrack);
            var toLen = graph.Length(toTrack);
            var intoFrom = fromEnd.DirectionInto();
            var outFrom = intoFrom.Reverse();
            var locoPos = new TrackPosition(fromTrack, graph.OffsetOf(fromTrack, fromEnd));
            var farPos = TrackWalker.Advance(graph, locoPos, intoFrom, Math.Min(trainLength, Math.Max(1, fromLen - 1)), lie, out var farOut);
            var goalOffset = toEnd == TrackEnd.Start ? Math.Min(5, toLen / 2) : Math.Max(toLen - 5, toLen / 2);
            var goal = new WaypointGoal(new TrackPosition(toTrack, goalOffset), toEnd.DirectionInto());
            var state = new TrainState
            {
                EndA = locoPos,
                OutwardA = outFrom,
                EndB = farPos,
                OutwardB = farOut,
                Length = trainLength,
                CarCount = 2,
                LocoEnd = ConsistEnd.A,
                Owner = owner,
            };
            var req = JobStep.Request(world, lie, state, goal);
            req.LastLeader = ConsistEnd.B;
            req.Blocked = (t, a, b) => t != fromTrack && t != toTrack && world.Occupancy.IsStanding(t, a, b, owner);
            var route = world.Router.Find(req, out _);
            return route != null && route.Length <= Math.Max(1200, trainLength * 4 + 400);
        }

        // ---- Track Assignment ----

        private sealed class KeyGroup
        {
            public readonly List<string> Keys = new List<string>();
            public int Cars;
            public double Length;
            public bool IsOverflowPart;
            public bool IsNoJob => Keys.Count == 1 && string.IsNullOrEmpty(Keys[0]);

            public KeyGroup Clone()
            {
                var c = new KeyGroup { Cars = Cars, Length = Length, IsOverflowPart = IsOverflowPart };
                c.Keys.AddRange(Keys);
                return c;
            }
        }

        /// <summary>
        /// Assigns classification keys to the yard's usable tracks:
        /// packs oversized single keys by exact whole-car lengths, clusters excess keys via
        /// backtracking guided by rail proximity, and solves exact maximum-weight matching
        /// maximizing clean tracks and anchored prefixes.
        /// </summary>
        public static SortAssignment Assign(IReadOnlyList<YardTrackState> tracks, IReadOnlyList<SortCar> inTrain,
            Func<string, string, double> keyDistance = null, Func<string, double> originDistance = null)
        {
            var result = new SortAssignment();
            var usable = tracks.Where(t => !t.Track.Blocked && t.Track.UsableLength >= MinUsableTrack).ToList();
            // Only participating usable tracks belong in WorkingEnds, so blocked/unreachable/short tracks
            // stay excluded on subsequent live scans.
            foreach (var ts in usable)
                result.WorkingEnds[ts.Track.Track] = ts.Track.WorkingEnd;

            if (usable.Count < 2)
            {
                result.Problems.Add("a yard needs at least two clear tracks to sort");
                BuildLines(result, tracks, inTrain);
                return result;
            }

            // Gather exact per-car lengths per key (on usable tracks + switcher hook).
            var carLengthsByKey = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            void CountCar(SortCar car)
            {
                var k = car.Key ?? "";
                if (!carLengthsByKey.TryGetValue(k, out var list))
                    carLengthsByKey[k] = list = new List<double>();
                list.Add(car.Length);
                result.TotalCars++;
            }
            foreach (var ts in usable)
                foreach (var cut in ts.CutsInnerToOuter)
                    foreach (var car in cut.CarsInnerToOuter)
                        CountCar(car);
            if (inTrain != null)
                foreach (var car in inTrain)
                    CountCar(car);

            if (result.TotalCars == 0)
            {
                result.AlreadySorted = true;
                foreach (var ts in usable)
                {
                    result.TrackKeys[ts.Track.Track] = new List<string>();
                    result.SpareTracks.Add(ts.Track.Track);
                }
                BuildLines(result, tracks, inTrain);
                return result;
            }

            var capacities = usable.Select(t => Math.Max(0, t.Track.UsableLength - TrackSlack)).OrderByDescending(x => x).ToList();
            var totalCarLength = carLengthsByKey.Values.Sum(v => v.Sum());
            var totalCapacity = capacities.Sum();
            if (totalCarLength > totalCapacity + 1e-6)
            {
                result.Problems.Add($"the yard's {result.TotalCars} cars ({totalCarLength:F0} m) exceed its usable track length ({totalCapacity:F0} m)");
                BuildLines(result, tracks, inTrain);
                return result;
            }

            // Build initial groups; pack oversized single keys using exact whole-car lengths against real track capacities!
            var maxCap = capacities[0];
            var groups = new List<KeyGroup>();
            foreach (var kv in carLengthsByKey.OrderBy(k => string.IsNullOrEmpty(k.Key) ? 1 : 0).ThenByDescending(k => k.Value.Sum()).ThenBy(k => k.Key, StringComparer.Ordinal))
            {
                var lengths = kv.Value;
                var totalLen = lengths.Sum();
                if (totalLen <= maxCap + 1e-6)
                {
                    var g = new KeyGroup { Cars = lengths.Count, Length = totalLen };
                    g.Keys.Add(kv.Key);
                    groups.Add(g);
                    continue;
                }
                // Whole-car bin packing into actual track capacities (FFD first, then exact bounded backtracking
                // if FFD used more bins than the capacity lower bound, e.g. [24,20,12,8,8,8] into [40,40]).
                var sortedCars = lengths.OrderByDescending(x => x).ToList();
                if (sortedCars[0] > maxCap + 1e-6)
                {
                    result.Problems.Add($"a car for {SortAssignment.FormatKey(kv.Key)} ({sortedCars[0]:F0} m) is longer than any track");
                    BuildLines(result, tracks, inTrain);
                    return result;
                }
                var bins = PackCarsIntoTrackBins(sortedCars, capacities);
                if (bins == null)
                {
                    result.Problems.Add($"the cars for {SortAssignment.FormatKey(kv.Key)} ({totalLen:F0} m) can't be packed into the yard's tracks");
                    BuildLines(result, tracks, inTrain);
                    return result;
                }
                foreach (var b in bins)
                {
                    var part = new KeyGroup { Cars = b.Cars, Length = b.Len, IsOverflowPart = true };
                    part.Keys.Add(kv.Key);
                    groups.Add(part);
                }
            }

            // Cluster groups until groups.Count <= usable.Count AND FitsCapacities(groups, capacities).
            // Uses backtracking guided by MergeScore so a greedy dead-end (e.g. [60,60,40,40] into [100,100])
            // backtracks to find the valid (60+40, 60+40) packing!
            if (groups.Count > usable.Count || !FitsCapacities(groups, capacities))
            {
                var clustered = ClusterGroupsToFit(groups, capacities, keyDistance, originDistance);
                if (clustered == null)
                {
                    result.Problems.Add("the cars can't be grouped to fit the yard's tracks");
                    BuildLines(result, tracks, inTrain);
                    return result;
                }
                groups = clustered;
            }

            // Compute score matrix [group, track] for exact maximum-weight matching.
            var gCount = groups.Count;
            var tCount = usable.Count;
            var scores = new double[gCount, tCount];
            var feasible = new bool[gCount, tCount];
            var totalByKey = carLengthsByKey.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);

            for (var gi = 0; gi < gCount; gi++)
            {
                var g = groups[gi];
                for (var ti = 0; ti < tCount; ti++)
                {
                    var ts = usable[ti];
                    var cap = Math.Max(0, ts.Track.UsableLength - TrackSlack);
                    if (g.Length > cap + 1e-6)
                        continue;
                    feasible[gi, ti] = true;
                    scores[gi, ti] = TrackMatchScore(g, ts, totalByKey, cap);
                }
            }

            var match = SolveAssignment(gCount, tCount, feasible, scores);
            if (match == null)
            {
                result.Problems.Add("no valid assignment of destinations to the yard's tracks fits all cars");
                BuildLines(result, tracks, inTrain);
                return result;
            }

            var assignedTracks = new HashSet<int>();
            foreach (var ts in usable)
                result.TrackKeys[ts.Track.Track] = new List<string>();

            for (var gi = 0; gi < gCount; gi++)
            {
                var trackId = usable[match[gi]].Track.Track;
                assignedTracks.Add(trackId);
                var list = result.TrackKeys[trackId];
                foreach (var k in groups[gi].Keys)
                {
                    if (!list.Contains(k))
                        list.Add(k);
                    if (!result.KeyToTrack.ContainsKey(k))
                        result.KeyToTrack[k] = trackId;
                    if (!result.KeyTracks.TryGetValue(k, out var kt))
                        result.KeyTracks[k] = kt = new List<int>();
                    if (!kt.Contains(trackId))
                        kt.Add(trackId);
                }
            }
            foreach (var kv in result.KeyTracks)
            {
                if (kv.Value.Count > 1)
                {
                    // Prefer dedicated single-key tracks before shared multi-key tracks so overflowed keys
                    // fill their dedicated track(s) first and preserve room for partner keys on shared tracks.
                    kv.Value.Sort((a, b) =>
                    {
                        var ca = result.TrackKeys[a].Count;
                        var cb = result.TrackKeys[b].Count;
                        if (ca != cb)
                            return ca.CompareTo(cb);
                        return a.CompareTo(b);
                    });
                    result.KeyToTrack[kv.Key] = kv.Value[0];
                }
            }
            foreach (var ts in usable)
                if (!assignedTracks.Contains(ts.Track.Track))
                    result.SpareTracks.Add(ts.Track.Track);

            // Check if the yard is already completely sorted (and consolidated, and loco empty).
            var allClean = (inTrain == null || inTrain.Count == 0);
            if (allClean)
            {
                var anchors = ComputeAllAnchors(result, tracks, inTrain, totalByKey);
                foreach (var ts in usable)
                {
                    if (anchors[ts.Track.Track] < ts.TotalCars || ts.CutsInnerToOuter.Count > 1)
                    {
                        allClean = false;
                        break;
                    }
                }
            }
            result.AlreadySorted = allClean;
            BuildLines(result, tracks, inTrain);
            return result;
        }

        /// <summary>
        /// Backtracking cluster search guided by corridor <see cref="MergeScore"/>: always tries
        /// preferred corridor merges first, and backtracks cleanly if a greedy merge prevents fitting
        /// all remaining groups into <paramref name="capacities"/>.
        /// </summary>
        private static List<KeyGroup> ClusterGroupsToFit(List<KeyGroup> initial, List<double> capacities,
            Func<string, string, double> keyDistance, Func<string, double> originDistance)
        {
            var maxCap = capacities[0];
            var nodes = 0;
            const int nodeBudget = 4000;
            var failedMemo = new HashSet<string>(StringComparer.Ordinal);

            List<KeyGroup> Search(List<KeyGroup> current)
            {
                if (current.Count <= capacities.Count && FitsCapacities(current, capacities))
                    return current;
                if (current.Count <= 1 || ++nodes > nodeBudget)
                    return null;
                // Quick lower bound: sum of lengths must fit in total capacity.
                var sig = string.Join("|", current.Select(g => string.Join("+", g.Keys) + ":" + Math.Round(g.Length, 1)).OrderBy(s => s, StringComparer.Ordinal));
                if (!failedMemo.Add(sig))
                    return null;

                var pairs = new List<(int I, int J, double Score)>();
                for (var i = 0; i < current.Count; i++)
                {
                    for (var j = i + 1; j < current.Count; j++)
                    {
                        var combined = current[i].Length + current[j].Length;
                        if (combined > maxCap + 1e-6)
                            continue;
                        // Never merge two overflow halves of the exact same key back together.
                        if (current[i].Keys.Count == 1 && current[j].Keys.Count == 1 && current[i].Keys[0] == current[j].Keys[0])
                            continue;
                        pairs.Add((i, j, MergeScore(current[i], current[j], keyDistance)));
                    }
                }
                pairs.Sort((a, b) => a.Score.CompareTo(b.Score));
                foreach (var (i, j, _) in pairs)
                {
                    var next = new List<KeyGroup>(current.Count - 1);
                    for (var k = 0; k < current.Count; k++)
                    {
                        if (k == i)
                            next.Add(MergeTwo(current[i], current[j], originDistance));
                        else if (k != j)
                            next.Add(current[k]);
                    }
                    var found = Search(next);
                    if (found != null)
                        return found;
                }
                return null;
            }

            return Search(initial);
        }

        private static KeyGroup MergeTwo(KeyGroup a, KeyGroup b, Func<string, double> originDistance)
        {
            var merged = new KeyGroup
            {
                Cars = a.Cars + b.Cars,
                Length = a.Length + b.Length,
                IsOverflowPart = a.IsOverflowPart || b.IsOverflowPart,
            };
            merged.Keys.AddRange(a.Keys);
            foreach (var k in b.Keys)
                if (!merged.Keys.Contains(k))
                    merged.Keys.Add(k);
            if (originDistance != null)
            {
                merged.Keys.Sort((x, y) =>
                {
                    if (string.IsNullOrEmpty(x) != string.IsNullOrEmpty(y))
                        return string.IsNullOrEmpty(x) ? 1 : -1;
                    var dx = originDistance(x);
                    var dy = originDistance(y);
                    var cmp = dy.CompareTo(dx);
                    return cmp != 0 ? cmp : string.CompareOrdinal(x, y);
                });
            }
            else
            {
                merged.Keys.Sort(StringComparer.Ordinal);
            }
            return merged;
        }

        private static double MergeScore(KeyGroup a, KeyGroup b, Func<string, string, double> keyDistance)
        {
            var penalty = (a.IsNoJob != b.IsNoJob) ? 1e7 : 0;
            // Avoid merging an overflowed key with another key unless required to fit.
            if (a.IsOverflowPart || b.IsOverflowPart)
                penalty += 1e6;
            var dist = 0.0;
            if (keyDistance != null)
            {
                var minD = double.PositiveInfinity;
                foreach (var ka in a.Keys)
                    foreach (var kb in b.Keys)
                        if (!string.IsNullOrEmpty(ka) && !string.IsNullOrEmpty(kb))
                            minD = Math.Min(minD, keyDistance(ka, kb));
                dist = double.IsPositiveInfinity(minD) ? 1e5 : minD;
            }
            return penalty + dist + (a.Length + b.Length) * 2.0 + (a.Keys.Count + b.Keys.Count) * 500.0;
        }

        /// <summary>
        /// Packs whole cars of a single oversized key into actual track capacities (descending).
        /// Uses fast first-fit-decreasing first, then bounded exact backtracking if FFD used more bins
        /// than the capacity lower bound (e.g. [24,20,12,8,8,8] into [40,40]).
        /// </summary>
        private static List<(int Cars, double Len)> PackCarsIntoTrackBins(List<double> carsDescending, List<double> capacitiesDescending)
        {
            if (carsDescending.Count == 0 || capacitiesDescending.Count == 0)
                return null;
            var maxCap = capacitiesDescending[0];

            // 1. Fast first-fit-decreasing baseline.
            var ffd = new List<(int Cars, double Len)>();
            var ffdOk = true;
            foreach (var cl in carsDescending)
            {
                var placed = false;
                for (var bi = 0; bi < ffd.Count; bi++)
                {
                    var cap = bi < capacitiesDescending.Count ? capacitiesDescending[bi] : maxCap;
                    if (ffd[bi].Len + cl <= cap + 1e-6)
                    {
                        ffd[bi] = (ffd[bi].Cars + 1, ffd[bi].Len + cl);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    var cap = ffd.Count < capacitiesDescending.Count ? capacitiesDescending[ffd.Count] : maxCap;
                    if (cl > cap + 1e-6)
                    {
                        ffdOk = false;
                        break;
                    }
                    ffd.Add((1, cl));
                }
            }

            // Compute minimum possible number of bins from total car length vs largest capacities.
            var totalLen = carsDescending.Sum();
            var minBins = 0;
            var capSum = 0.0;
            while (minBins < capacitiesDescending.Count && capSum < totalLen - 1e-6)
                capSum += capacitiesDescending[minBins++];
            if (capSum < totalLen - 1e-6)
                return null;
            minBins = Math.Max(2, minBins);

            if (ffdOk && ffd.Count <= minBins)
                return ffd;

            // 2. Bounded exact backtracking for fewer bins than FFD (from minBins up to min(capacities.Count, ffd.Count - 1)).
            var upperBins = ffdOk ? Math.Min(capacitiesDescending.Count, ffd.Count - 1) : capacitiesDescending.Count;
            for (var targetBins = minBins; targetBins <= upperBins; targetBins++)
            {
                var used = new double[targetBins];
                var counts = new int[targetBins];
                var caps = new double[targetBins];
                for (var i = 0; i < targetBins; i++)
                    caps[i] = capacitiesDescending[i];
                var budget = 8000;
                if (TryExactBinPack(carsDescending, 0, caps, used, counts, ref budget))
                {
                    var exact = new List<(int Cars, double Len)>(targetBins);
                    for (var i = 0; i < targetBins; i++)
                        if (counts[i] > 0)
                            exact.Add((counts[i], used[i]));
                    return exact;
                }
            }
            return ffdOk && ffd.Count <= capacitiesDescending.Count ? ffd : null;
        }

        private static bool TryExactBinPack(List<double> cars, int index, double[] caps, double[] used, int[] counts, ref int budget)
        {
            if (index >= cars.Count)
                return true;
            if (--budget <= 0)
                return false;
            var cl = cars[index];
            for (var b = 0; b < caps.Length; b++)
            {
                if (used[b] + cl > caps[b] + 1e-6)
                    continue;
                // Symmetry break: skip bin b if an earlier bin has identical remaining capacity and cap.
                var dup = false;
                for (var prev = 0; prev < b; prev++)
                {
                    if (Math.Abs((caps[prev] - used[prev]) - (caps[b] - used[b])) < 1e-6
                        && Math.Abs(caps[prev] - caps[b]) < 1e-6)
                    {
                        dup = true;
                        break;
                    }
                }
                if (dup)
                    continue;
                used[b] += cl;
                counts[b]++;
                if (TryExactBinPack(cars, index + 1, caps, used, counts, ref budget))
                    return true;
                used[b] -= cl;
                counts[b]--;
                // Note: do NOT unconditionally break when counts[b] == 0, because subsequent empty bins
                // may have different capacities (equal-capacity empty bins are already skipped by the
                // symmetry check above).
            }
            return false;
        }

        private static bool FitsCapacities(List<KeyGroup> groups, List<double> capacitiesDescending)
        {
            if (groups.Count > capacitiesDescending.Count)
                return false;
            var needed = groups.Select(g => g.Length).OrderByDescending(x => x).ToList();
            for (var i = 0; i < needed.Count; i++)
                if (needed[i] > capacitiesDescending[i] + 1e-6)
                    return false;
            return true;
        }

        private static double TrackMatchScore(KeyGroup group, YardTrackState ts, Dictionary<string, int> totalByKey, double cap)
        {
            var cars = ts.AllCarsInnerToOuter();
            var anchor = CountPrefixAnchor(cars, group.Keys, totalByKey, extraAnchoredByKey: null);
            var totalInGroupOnTrack = 0;
            var keySet = new HashSet<string>(group.Keys, StringComparer.Ordinal);
            foreach (var c in cars)
                if (keySet.Contains(c.Key ?? ""))
                    totalInGroupOnTrack++;

            var isPure = cars.Count == 0 || anchor == cars.Count;
            var score = anchor * 1.0 + (totalInGroupOnTrack - anchor) * 0.2;
            if (isPure)
                score += CleanTrackBonus;
            var waste = Math.Max(0, cap - group.Length);
            score -= waste * 1e-4;
            return score;
        }

        private static int CountPrefixAnchor(List<SortCar> carsInnerToOuter, List<string> orderedKeys,
            Dictionary<string, int> totalByKey, Dictionary<string, int> extraAnchoredByKey)
        {
            if (carsInnerToOuter.Count == 0 || orderedKeys.Count == 0)
                return 0;
            var seenPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var ki = 0;
            var anchor = 0;
            foreach (var car in carsInnerToOuter)
            {
                var k = car.Key ?? "";
                var idx = orderedKeys.IndexOf(k);
                if (idx < ki)
                    break;
                var earlierComplete = true;
                for (var prev = ki; prev < idx; prev++)
                {
                    var pk = orderedKeys[prev];
                    var have = (seenPerKey.TryGetValue(pk, out var s) ? s : 0)
                        + (extraAnchoredByKey != null && extraAnchoredByKey.TryGetValue(pk, out var ex) ? ex : 0);
                    var need = totalByKey.TryGetValue(pk, out var tot) ? tot : 0;
                    if (have < need)
                    {
                        earlierComplete = false;
                        break;
                    }
                }
                if (!earlierComplete)
                    break;
                ki = idx;
                seenPerKey[k] = (seenPerKey.TryGetValue(k, out var cur) ? cur : 0) + 1;
                anchor++;
            }
            return anchor;
        }

        /// <summary>Exact branch-and-bound maximum-weight bipartite matching.</summary>
        private static int[] SolveAssignment(int gCount, int tCount, bool[,] feasible, double[,] scores)
        {
            if (gCount == 0)
                return Array.Empty<int>();
            var order = Enumerable.Range(0, gCount)
                .OrderBy(g => Enumerable.Range(0, tCount).Count(t => feasible[g, t]))
                .ToArray();

            var maxPerGroup = new double[gCount];
            for (var i = 0; i < gCount; i++)
            {
                var best = double.NegativeInfinity;
                for (var t = 0; t < tCount; t++)
                    if (feasible[order[i], t] && scores[order[i], t] > best)
                        best = scores[order[i], t];
                if (double.IsNegativeInfinity(best))
                    return null;
                maxPerGroup[i] = best;
            }
            var suffixBound = new double[gCount + 1];
            for (var i = gCount - 1; i >= 0; i--)
                suffixBound[i] = suffixBound[i + 1] + maxPerGroup[i];

            var used = new bool[tCount];
            var current = new int[gCount];
            int[] bestMatch = null;
            var bestScore = double.NegativeInfinity;
            var nodes = 0;
            const int nodeLimit = 50000;

            void Search(int depth, double score)
            {
                if (++nodes > nodeLimit)
                    return;
                if (depth == gCount)
                {
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = (int[])current.Clone();
                    }
                    return;
                }
                if (bestMatch != null && score + suffixBound[depth] <= bestScore + 1e-9)
                    return;
                var g = order[depth];
                var choices = new List<(int Track, double Score)>();
                for (var t = 0; t < tCount; t++)
                    if (!used[t] && feasible[g, t])
                        choices.Add((t, scores[g, t]));
                choices.Sort((a, b) => b.Score.CompareTo(a.Score));
                foreach (var (t, s) in choices)
                {
                    used[t] = true;
                    current[g] = t;
                    Search(depth + 1, score + s);
                    used[t] = false;
                }
            }

            Search(0, 0);
            return bestMatch;
        }

        private static void BuildLines(SortAssignment assignment, IReadOnlyList<YardTrackState> tracks, IReadOnlyList<SortCar> inTrain)
        {
            assignment.Lines.Clear();
            var carCounts = new Dictionary<string, (int Cars, double Len)>(StringComparer.Ordinal);
            foreach (var ts in tracks)
                if (!ts.Track.Blocked)
                    foreach (var c in ts.AllCarsInnerToOuter())
                    {
                        var k = c.Key ?? "";
                        carCounts[k] = carCounts.TryGetValue(k, out var cur) ? (cur.Cars + 1, cur.Len + c.Length) : (1, c.Length);
                    }
            if (inTrain != null)
                foreach (var c in inTrain)
                {
                    var k = c.Key ?? "";
                    carCounts[k] = carCounts.TryGetValue(k, out var cur) ? (cur.Cars + 1, cur.Len + c.Length) : (1, c.Length);
                }

            var totalByKey = carCounts.ToDictionary(kv => kv.Key, kv => kv.Value.Cars, StringComparer.Ordinal);
            var anchors = ComputeAllAnchors(assignment, tracks, inTrain, totalByKey);

            foreach (var ts in tracks)
            {
                var line = new TrackPlanLine
                {
                    Track = ts.Track.Track,
                    TrackName = ts.Track.Name,
                    TrackLength = ts.Track.Length,
                    Blocked = ts.Track.Blocked,
                    BlockedReason = ts.Track.BlockedReason,
                };
                if (!ts.Track.Blocked && assignment.TrackKeys.TryGetValue(ts.Track.Track, out var keys))
                {
                    line.Keys.AddRange(keys);
                    foreach (var k in keys)
                    {
                        if (carCounts.TryGetValue(k, out var cnt))
                        {
                            var split = assignment.KeyTracks.TryGetValue(k, out var kt) ? Math.Max(1, kt.Count) : 1;
                            line.PlannedCars += (cnt.Cars + split - 1) / split;
                            line.PlannedLength += cnt.Len / split;
                        }
                    }
                    line.AlreadyInPlace = anchors.TryGetValue(ts.Track.Track, out var a) ? a : 0;
                }
                assignment.Lines.Add(line);
            }
        }

        // ---- Live Switching State & Next Move Planner ----

        /// <summary>
        /// Computes anchor counts for all tracks jointly so an overflowed earlier sub-block
        /// anchored on another track properly unlocks a shared track's later sub-block.
        /// </summary>
        private static Dictionary<int, int> ComputeAllAnchors(SortAssignment assignment,
            IReadOnlyList<YardTrackState> allTracks, IReadOnlyList<SortCar> inTrain,
            Dictionary<string, int> totalByKey)
        {
            var anchors = new Dictionary<int, int>();
            var carsByTrack = new Dictionary<int, List<SortCar>>();
            foreach (var ts in allTracks)
            {
                anchors[ts.Track.Track] = 0;
                if (!ts.Track.Blocked)
                    carsByTrack[ts.Track.Track] = ts.AllCarsInnerToOuter();
            }

            // Fixed-point iteration (at most 3 passes): first pass anchors primary/single-key tracks,
            // subsequent passes let overflowed earlier keys anchored elsewhere unlock shared tracks' later sub-blocks.
            for (var pass = 0; pass < 3; pass++)
            {
                var anchoredByKeyOnTrack = new Dictionary<(int Track, string Key), int>();
                var totalAnchoredByKey = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var ts in allTracks)
                {
                    var tid = ts.Track.Track;
                    if (ts.Track.Blocked || !carsByTrack.TryGetValue(tid, out var cars))
                        continue;
                    var a = anchors[tid];
                    for (var i = 0; i < a; i++)
                    {
                        var k = cars[i].Key ?? "";
                        anchoredByKeyOnTrack[(tid, k)] = (anchoredByKeyOnTrack.TryGetValue((tid, k), out var n) ? n : 0) + 1;
                        totalAnchoredByKey[k] = (totalAnchoredByKey.TryGetValue(k, out var tn) ? tn : 0) + 1;
                    }
                }
                var changed = false;
                foreach (var ts in allTracks)
                {
                    var tid = ts.Track.Track;
                    if (ts.Track.Blocked || !assignment.TrackKeys.TryGetValue(tid, out var allowed) || allowed.Count == 0)
                        continue;
                    var cars = carsByTrack[tid];
                    // Extra anchored cars of each key on OTHER tracks (for overflowed keys).
                    Dictionary<string, int> extra = null;
                    if (allowed.Count > 1)
                    {
                        extra = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var k in allowed)
                        {
                            var tot = totalAnchoredByKey.TryGetValue(k, out var tn) ? tn : 0;
                            var here = anchoredByKeyOnTrack.TryGetValue((tid, k), out var hn) ? hn : 0;
                            if (tot > here)
                                extra[k] = tot - here;
                        }
                    }
                    var nextA = CountPrefixAnchor(cars, allowed, totalByKey, extra);
                    if (nextA != anchors[tid])
                    {
                        anchors[tid] = nextA;
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }
            return anchors;
        }

        /// <summary>
        /// How many cars starting from the inner end of <paramref name="track"/> are already
        /// validly sorted in place according to <paramref name="assignment"/>.
        /// </summary>
        public static int AnchorCount(YardTrackState track, SortAssignment assignment,
            IReadOnlyList<YardTrackState> allTracks, IReadOnlyList<SortCar> inTrain)
        {
            if (track.Track.Blocked || !assignment.TrackKeys.TryGetValue(track.Track.Track, out var allowed) || allowed.Count == 0)
                return 0;
            var totalByKey = CountAllCarsByKey(allTracks, inTrain, assignment);
            var anchors = ComputeAllAnchors(assignment, allTracks, inTrain, totalByKey);
            return anchors.TryGetValue(track.Track.Track, out var a) ? a : 0;
        }

        private static Dictionary<string, int> CountAllCarsByKey(IReadOnlyList<YardTrackState> allTracks, IReadOnlyList<SortCar> inTrain,
            SortAssignment assignment = null)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var ts in allTracks)
            {
                if (ts.Track.Blocked || ts.Track.UsableLength < MinUsableTrack)
                    continue;
                if (assignment != null && !assignment.TrackKeys.ContainsKey(ts.Track.Track))
                    continue;
                foreach (var cut in ts.CutsInnerToOuter)
                    foreach (var c in cut.CarsInnerToOuter)
                    {
                        var k = c.Key ?? "";
                        counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
                    }
            }
            if (inTrain != null)
            {
                foreach (var c in inTrain)
                {
                    var k = c.Key ?? "";
                    counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
                }
            }
            return counts;
        }

        /// <summary>Canonical yard + hook state signature (for cycle prevention across pulls and set-outs).</summary>
        public static string StateSignature(IReadOnlyList<YardTrackState> tracks, IReadOnlyList<SortCar> inTrainFarToNear)
        {
            var sb = new StringBuilder(128);
            if (inTrainFarToNear != null)
            {
                for (var i = 0; i < inTrainFarToNear.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(inTrainFarToNear[i].Key ?? "");
                }
            }
            foreach (var ts in tracks)
            {
                sb.Append('|').Append(ts.Track.Track).Append(':');
                var first = true;
                for (var ci = 0; ci < ts.CutsInnerToOuter.Count; ci++)
                {
                    var cut = ts.CutsInnerToOuter[ci];
                    for (var i = 0; i < cut.CarsInnerToOuter.Count; i++)
                    {
                        if (!first) sb.Append(',');
                        sb.Append(cut.CarsInnerToOuter[i].Key ?? "");
                        first = false;
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns the target track for <paramref name="key"/> (for overflowed keys split across
        /// tracks, prefers a ready clean track with room, else the first assigned track with room).
        /// </summary>
        public static int TargetTrackFor(SortAssignment assignment, string key, double needLength,
            Dictionary<int, YardTrackState> byTrack, Func<int, string, bool> isReady = null)
        {
            var k = key ?? "";
            if (!assignment.KeyTracks.TryGetValue(k, out var list) || list.Count == 0)
                return assignment.TryTrackFor(k, out var single) ? single : -1;
            if (list.Count == 1)
                return list[0];
            if (isReady != null)
            {
                foreach (var t in list)
                {
                    if (byTrack.TryGetValue(t, out var ts) && !ts.Track.Blocked && isReady(t, k)
                        && ts.Track.UsableLength - ts.OccupiedLength >= needLength - 0.5)
                        return t;
                }
            }
            foreach (var t in list)
            {
                if (byTrack.TryGetValue(t, out var ts) && !ts.Track.Blocked
                    && ts.Track.UsableLength - ts.OccupiedLength >= needLength - 0.5)
                    return t;
            }
            // No single track holds the full run: prefer the ready track with the most free room (for partial overflow set-out).
            var bestTrack = -1;
            var bestFree = -1.0;
            if (isReady != null)
            {
                foreach (var t in list)
                {
                    if (byTrack.TryGetValue(t, out var ts) && !ts.Track.Blocked && isReady(t, k))
                    {
                        var free = ts.Track.UsableLength - ts.OccupiedLength;
                        if (free > bestFree)
                        {
                            bestFree = free;
                            bestTrack = t;
                        }
                    }
                }
                if (bestTrack >= 0)
                    return bestTrack;
            }
            foreach (var t in list)
            {
                if (byTrack.TryGetValue(t, out var ts) && !ts.Track.Blocked)
                {
                    var free = ts.Track.UsableLength - ts.OccupiedLength;
                    if (free > bestFree)
                    {
                        bestFree = free;
                        bestTrack = t;
                    }
                }
            }
            return bestTrack >= 0 ? bestTrack : list[list.Count - 1];
        }

        /// <summary>
        /// True when <paramref name="trackId"/> is clean (0 unsorted cars) AND every earlier
        /// sub-block assigned to it has already been completely placed in its assigned track(s)' anchors.
        /// </summary>
        public static bool IsReadyForKey(int trackId, string key, SortAssignment assignment,
            IReadOnlyList<YardTrackState> allTracks, IReadOnlyList<SortCar> inTrain,
            Dictionary<int, YardTrackState> byTrack, Dictionary<int, int> anchors,
            Dictionary<string, int> totalByKey, Dictionary<string, int> totalAnchoredByKey = null)
        {
            if (!byTrack.TryGetValue(trackId, out var ts) || ts.Track.Blocked)
                return false;
            if (!anchors.TryGetValue(trackId, out var anc) || anc < ts.TotalCars)
                return false;
            if (!assignment.TrackKeys.TryGetValue(trackId, out var allowed))
                return false;
            var k = key ?? "";
            var idx = allowed.IndexOf(k);
            if (idx < 0)
                return false;
            if (idx == 0)
                return true;
            if (totalAnchoredByKey == null)
                totalAnchoredByKey = TotalAnchoredByKey(allTracks, anchors);
            for (var prev = 0; prev < idx; prev++)
            {
                var pk = allowed[prev];
                var have = totalAnchoredByKey.TryGetValue(pk, out var n) ? n : 0;
                var need = totalByKey.TryGetValue(pk, out var tot) ? tot : 0;
                if (have < need)
                    return false;
            }
            return true;
        }

        private static Dictionary<string, int> TotalAnchoredByKey(IReadOnlyList<YardTrackState> allTracks, Dictionary<int, int> anchors)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var ts in allTracks)
            {
                if (ts.Track.Blocked || !anchors.TryGetValue(ts.Track.Track, out var a) || a <= 0)
                    continue;
                var cars = ts.AllCarsInnerToOuter();
                for (var i = 0; i < a && i < cars.Count; i++)
                {
                    var k = cars[i].Key ?? "";
                    map[k] = map.TryGetValue(k, out var n) ? n + 1 : 1;
                }
            }
            return map;
        }

        /// <summary>
        /// Plans the next concrete switching action given the fixed <paramref name="assignment"/>
        /// and the live state of every yard track and the switcher's hook.
        /// Pass <paramref name="visitedMoves"/> (persisted across steps) to prevent multi-step cycles.
        /// </summary>
        public static SortAction NextAction(SortAssignment assignment, IReadOnlyList<YardTrackState> tracks,
            IReadOnlyList<SortCar> inTrainFarToNear, int lastPulledTrack = -1, int consecutivePulls = 0,
            Func<int, double> distanceToTrack = null, int clearingTrack = -1, HashSet<string> visitedMoves = null)
        {
            var byTrack = new Dictionary<int, YardTrackState>();
            var usable = new List<YardTrackState>();
            foreach (var ts in tracks)
            {
                byTrack[ts.Track.Track] = ts;
                var participating = assignment.TrackKeys.ContainsKey(ts.Track.Track);
                // Revalidate tracks participating in the assignment: if any track the plan relies on
                // became blocked mid-run (e.g. another loco parked on it), stop/replan cleanly instead
                // of throwing KeyNotFoundException or falsely reporting Done.
                if (ts.Track.Blocked)
                {
                    if (participating)
                    {
                        return new SortAction
                        {
                            Kind = SortActionKind.Blocked,
                            NeedsReplan = true,
                            Track = ts.Track.Track,
                            Reason = $"{ts.Track.Name} is now blocked ({ts.Track.BlockedReason ?? "obstructed"})",
                        };
                    }
                    continue;
                }
                if (participating && ts.Track.UsableLength >= MinUsableTrack)
                    usable.Add(ts);
            }

            var totalByKey = CountAllCarsByKey(usable, inTrainFarToNear, assignment);
            // Verify every live key on participating tracks/hook is known to the assignment (new job mid-sort triggers replan).
            foreach (var k in totalByKey.Keys)
            {
                if (!assignment.KeyTracks.ContainsKey(k) && !assignment.KeyToTrack.ContainsKey(k))
                {
                    return new SortAction
                    {
                        Kind = SortActionKind.Blocked,
                        NeedsReplan = true,
                        Reason = $"new classification key '{SortAssignment.FormatKey(k)}' appeared during sorting",
                    };
                }
            }

            var anchors = ComputeAllAnchors(assignment, usable, inTrainFarToNear, totalByKey);
            var totalAnchored = TotalAnchoredByKey(usable, anchors);
            var unsorted = new Dictionary<int, int>();
            foreach (var ts in tracks)
            {
                var anc = anchors.TryGetValue(ts.Track.Track, out var a) ? a : 0;
                unsorted[ts.Track.Track] = ts.Track.Blocked || !assignment.TrackKeys.ContainsKey(ts.Track.Track) ? 0 : Math.Max(0, ts.TotalCars - anc);
            }

            bool Ready(int trackId, string key) =>
                IsReadyForKey(trackId, key, assignment, usable, inTrainFarToNear, byTrack, anchors, totalByKey, totalAnchored);

            // Count how many unsorted cars anywhere in the yard (plus on the hook) are still waiting for each track.
            var waitingForTrack = new Dictionary<int, int>();
            foreach (var ts in usable)
                waitingForTrack[ts.Track.Track] = 0;
            foreach (var ts in usable)
            {
                var cars = ts.AllCarsInnerToOuter();
                var anc = anchors[ts.Track.Track];
                for (var i = anc; i < cars.Count; i++)
                {
                    var dest = TargetTrackFor(assignment, cars[i].Key ?? "", cars[i].Length, byTrack, Ready);
                    if (dest >= 0 && waitingForTrack.ContainsKey(dest))
                        waitingForTrack[dest]++;
                }
            }
            if (inTrainFarToNear != null)
            {
                foreach (var c in inTrainFarToNear)
                {
                    var dest = TargetTrackFor(assignment, c.Key ?? "", c.Length, byTrack, Ready);
                    if (dest >= 0 && waitingForTrack.ContainsKey(dest))
                        waitingForTrack[dest]++;
                }
            }

            var stateSig = visitedMoves != null ? StateSignature(usable, inTrainFarToNear) : null;

            // ---- Case 1: The switcher has cars on its hook -> classify the far-end run ----
            if (inTrainFarToNear != null && inTrainFarToNear.Count > 0)
            {
                var firstKey = inTrainFarToNear[0].Key ?? "";
                var run = new List<SortCar>();
                var runLen = 0.0;
                for (var i = 0; i < inTrainFarToNear.Count; i++)
                {
                    if (!string.Equals(inTrainFarToNear[i].Key ?? "", firstKey, StringComparison.Ordinal))
                        break;
                    run.Add(inTrainFarToNear[i]);
                    runLen += inTrainFarToNear[i].Length;
                }
                var target = TargetTrackFor(assignment, firstKey, runLen, byTrack, Ready);
                if (target < 0 || !byTrack.TryGetValue(target, out var targetState) || targetState.Track.Blocked)
                {
                    return new SortAction
                    {
                        Kind = SortActionKind.Blocked,
                        NeedsReplan = true,
                        Reason = $"no usable track is assigned for {SortAssignment.FormatKey(firstKey)}",
                    };
                }

                // 1A: Target track is ready (clean & sub-block order satisfied) -> direct permanent set-out.
                // When firstKey overflows across multiple tracks, try every ready assigned track to find a
                // prefix drop that keeps all remaining unplaced cars of `firstKey` packable across its tracks
                // (prevents stranding e.g. [12,24,24,24] on unequal 84 m + 72 m tracks or [20,20,20,20,30,30] on two 106 m tracks).
                var isOverflowKey = assignment.KeyTracks.TryGetValue(firstKey, out var keyTrackList) && keyTrackList.Count > 1;
                if (!isOverflowKey)
                {
                    if (Ready(target, firstKey) && FreeRoom(targetState) >= runLen - 0.5)
                    {
                        return MakeSetOut(targetState, run, firstKey, joinSorted: targetState.CutsInnerToOuter.Count > 0, isSluff: false);
                    }
                }
                else
                {
                    // Pass 1: try target first, then every other ready assigned track for a packing-feasible drop.
                    var orderedTargets = new List<YardTrackState>(keyTrackList.Count);
                    if (Ready(target, firstKey) && FreeRoom(targetState) >= run[0].Length - 0.5)
                        orderedTargets.Add(targetState);
                    foreach (var tid in keyTrackList)
                    {
                        if (tid == target)
                            continue;
                        if (byTrack.TryGetValue(tid, out var ts) && !ts.Track.Blocked
                            && Ready(tid, firstKey) && FreeRoom(ts) >= run[0].Length - 0.5)
                            orderedTargets.Add(ts);
                    }
                    foreach (var candidateTs in orderedTargets)
                    {
                        var dropCount = ChooseFeasibleOverflowDrop(firstKey, run, candidateTs, assignment, usable, byTrack, anchors, inTrainFarToNear, out var feasible);
                        if (feasible && dropCount > 0)
                        {
                            var drop = dropCount == run.Count ? run : run.GetRange(0, dropCount);
                            return MakeSetOut(candidateTs, drop, firstKey, joinSorted: candidateTs.CutsInnerToOuter.Count > 0, isSluff: false);
                        }
                    }
                    // Pass 2: if all assigned tracks for firstKey are already ready yet none passed the exact check
                    // (e.g. a shared partner key temporarily shifted slack), fall back to maxFit on the first ready track.
                    // If some assigned track is still dirty, do NOT commit an infeasible drop onto a ready track; let
                    // 1B/1C clear or stage until the needed track is ready.
                    var allAssignedReady = true;
                    foreach (var tid in keyTrackList)
                    {
                        if (byTrack.TryGetValue(tid, out var ts) && !ts.Track.Blocked && !Ready(tid, firstKey))
                        {
                            allAssignedReady = false;
                            break;
                        }
                    }
                    if (allAssignedReady && orderedTargets.Count > 0)
                    {
                        var fallbackTs = orderedTargets[0];
                        var dropCount = ChooseFeasibleOverflowDrop(firstKey, run, fallbackTs, assignment, usable, byTrack, anchors, inTrainFarToNear, out _);
                        if (dropCount > 0)
                        {
                            var drop = dropCount == run.Count ? run : run.GetRange(0, dropCount);
                            return MakeSetOut(fallbackTs, drop, firstKey, joinSorted: fallbackTs.CutsInnerToOuter.Count > 0, isSluff: false);
                        }
                    }
                }

                // 1B: An assigned track for firstKey is dirty (and clearing it would make it ready for firstKey):
                // hook-swap by pulling that track's entire unsorted tail right now if it fits on the lead.
                // Considers every dirty assigned track (including on an initially loaded hook with consecutivePulls == 0).
                if (clearingTrack < 0 && consecutivePulls < MaxConsecutiveHookSwaps)
                {
                    var swapCandidates = new List<YardTrackState>();
                    if (isOverflowKey)
                    {
                        foreach (var tid in keyTrackList)
                            if (byTrack.TryGetValue(tid, out var ts) && !ts.Track.Blocked && unsorted[tid] > 0)
                                swapCandidates.Add(ts);
                    }
                    else if (unsorted[target] > 0)
                    {
                        swapCandidates.Add(targetState);
                    }

                    if (swapCandidates.Count > 0)
                    {
                        var inTrainLen = inTrainFarToNear.Sum(c => c.Length);
                        foreach (var swapTs in swapCandidates)
                        {
                            var swapTid = swapTs.Track.Track;
                            if (swapTid == lastPulledTrack)
                                continue;
                            if (!WouldBeReadyAfterClear(swapTs, anchors[swapTid], firstKey, assignment, totalByKey, totalAnchored))
                                continue;
                            var swapUnsorted = unsorted[swapTid];
                            var pullTailLen = UnsortedTailLength(swapTs, swapUnsorted);
                            if (inTrainLen + pullTailLen <= assignment.MaxCutLength + 1e-6)
                            {
                                var moveKey = stateSig != null ? stateSig + "|HP" + swapTid : null;
                                if (moveKey == null || visitedMoves.Add(moveKey))
                                {
                                    var swapPull = BuildPullAction(swapTs, swapUnsorted, clearsTrack: true,
                                        reason: $"clearing {swapTs.Track.Name} to place {SortAssignment.FormatKey(firstKey)}");
                                    if (swapPull != null)
                                        return swapPull;
                                }
                            }
                        }
                    }
                }

                // 1C: Target track isn't ready yet -> sluff this run onto the best temporary staging track!
                var avoidSluff = lastPulledTrack >= 0 ? lastPulledTrack : clearingTrack;
                var hookKeysAfter = new HashSet<string>(StringComparer.Ordinal);
                for (var i = run.Count; i < inTrainFarToNear.Count; i++)
                    hookKeysAfter.Add(inTrainFarToNear[i].Key ?? "");

                var sluff = PickSluffTrack(assignment, usable, byTrack, unsorted, waitingForTrack, target, avoidSluff, firstKey, run.Count, runLen, hookKeysAfter, stateSig, visitedMoves);
                if (sluff != null)
                {
                    if (stateSig != null)
                        visitedMoves.Add(stateSig + "|S" + sluff.Track.Track + ":" + run.Count);
                    return MakeSetOut(sluff, run, firstKey, joinSorted: false, isSluff: true);
                }

                // Split run if partial staging fits (e.g. completed track has room for 1-2 cars when 3 don't fit).
                if (run.Count > 1)
                {
                    var sub = new List<SortCar>();
                    var subLen = 0.0;
                    for (var i = 0; i < run.Count; i++)
                    {
                        sub.Add(run[i]);
                        subLen += run[i].Length;
                        var partial = PickSluffTrack(assignment, usable, byTrack, unsorted, waitingForTrack, target, avoidSluff, firstKey, sub.Count, subLen, hookKeysAfter, stateSig, visitedMoves);
                        if (partial != null)
                        {
                            while (i + 1 < run.Count && FreeRoom(partial) >= subLen + run[i + 1].Length - 0.5
                                && (stateSig == null || !visitedMoves.Contains(stateSig + "|S" + partial.Track.Track + ":" + (sub.Count + 1))))
                            {
                                i++;
                                sub.Add(run[i]);
                                subLen += run[i].Length;
                            }
                            if (stateSig != null)
                                visitedMoves.Add(stateSig + "|S" + partial.Track.Track + ":" + sub.Count);
                            return MakeSetOut(partial, sub, firstKey, joinSorted: false, isSluff: true);
                        }
                    }
                }

                return new SortAction
                {
                    Kind = SortActionKind.Blocked,
                    Reason = $"no track in the yard has {runLen:F0} m free to stage {run.Count} car{(run.Count == 1 ? "" : "s")} ({SortAssignment.FormatKey(firstKey)}) while {targetState.Track.Name} is cleared",
                };
            }

            // ---- Case 2: Switcher is light -> pick the best dirty track to pull from ----
            var dirty = usable.Where(ts => unsorted[ts.Track.Track] > 0).ToList();
            if (dirty.Count == 0)
            {
                foreach (var ts in usable)
                {
                    if (ts.CutsInnerToOuter.Count > 1)
                    {
                        return new SortAction
                        {
                            Kind = SortActionKind.Consolidate,
                            Track = ts.Track.Track,
                            Reason = $"coupling the {ts.CutsInnerToOuter.Count} cuts on {ts.Track.Name} together",
                        };
                    }
                }
                return new SortAction { Kind = SortActionKind.Done, Reason = "all cars sorted" };
            }

            var spareSet = new HashSet<int>(assignment.SpareTracks);
            var nonSpareDirty = dirty.Where(ts => !spareSet.Contains(ts.Track.Track) && waitingForTrack[ts.Track.Track] > 0).ToList();
            var keepForSluff = nonSpareDirty.Count > 1 && assignment.SpareTracks.Count == 0
                ? nonSpareDirty.OrderByDescending(FreeRoom).First().Track.Track
                : -1;

            var candidates = new List<(YardTrackState Track, int PullCount, bool Clears, (int Tier, int Sluffs, int NotClear, int IsSluffHolder, int UnsortedCount, double Dist) Key)>();
            var oversizedCarLength = 0.0;

            foreach (var ts in dirty)
            {
                var tid = ts.Track.Track;
                var uCount = unsorted[tid];
                bool IsDirectKey(string key)
                {
                    var d = TargetTrackFor(assignment, key, 14, byTrack, Ready);
                    return d >= 0 && Ready(d, key);
                }
                var pullCount = ChooseBatchCount(ts, uCount, assignment.MaxCutLength, IsDirectKey, out var clears);
                if (pullCount <= 0)
                {
                    var outerCars = ts.AllCarsInnerToOuter();
                    if (outerCars.Count > 0)
                        oversizedCarLength = Math.Max(oversizedCarLength, outerCars[outerCars.Count - 1].Length);
                    continue;
                }

                var cars = ts.AllCarsInnerToOuter();
                var anc = anchors[tid];
                var startIdx = cars.Count - pullCount;
                var sluffs = 0;
                var buriedReady = 0;
                var maxSluffCarLen = 0.0;
                for (var i = anc; i < cars.Count; i++)
                {
                    var k = cars[i].Key ?? "";
                    var dest = TargetTrackFor(assignment, k, cars[i].Length, byTrack, Ready);
                    var destReady = dest >= 0 && (Ready(dest, k) || (dest == tid && clears && WouldBeReadyAfterClear(ts, anc, k, assignment, totalByKey, totalAnchored)));
                    if (destReady)
                        buriedReady++;
                    if (i >= startIdx && !destReady)
                    {
                        sluffs++;
                        if (cars[i].Length > maxSluffCarLen)
                            maxSluffCarLen = cars[i].Length;
                    }
                }

                var directCars = pullCount - sluffs;
                var isHolding = spareSet.Contains(tid) || waitingForTrack[tid] == 0;
                var needRoom = Math.Max(14.0, maxSluffCarLen);
                var hasSafeStaging = sluffs == 0 || usable.Any(other =>
                    other.Track.Track != tid
                    && (spareSet.Contains(other.Track.Track) || unsorted[other.Track.Track] > 0 || waitingForTrack[other.Track.Track] == 0)
                    && FreeRoom(other) >= needRoom + 0.5);
                var hasAnyStaging = sluffs == 0 || hasSafeStaging || usable.Any(other =>
                    other.Track.Track != tid && FreeRoom(other) >= needRoom + 0.5);

                int tier;
                if (sluffs == 0 && clears && !isHolding)
                    tier = 0;
                else if (sluffs == 0 && clears && isHolding)
                    tier = 1;
                else if (tid == clearingTrack && hasAnyStaging)
                    tier = 2;
                else if (directCars > 0 && hasSafeStaging && !isHolding)
                    tier = 3;
                else if (directCars > 0 && hasSafeStaging)
                    tier = 4;
                else if (buriedReady > 0 && hasSafeStaging)
                    tier = 5;
                else if (!isHolding && hasSafeStaging)
                    tier = 6;
                else if (hasSafeStaging)
                    tier = 7;
                else if (hasAnyStaging)
                    tier = 8;
                else
                    tier = 9;

                var dist = distanceToTrack != null ? distanceToTrack(tid) : 0;
                var rankSluffs = tier == 5 ? -buriedReady : tier >= 6 ? 0 : sluffs;
                var key = (tier, rankSluffs, clears ? 0 : 1, tid == keepForSluff ? 1 : 0, uCount, dist);
                candidates.Add((ts, pullCount, clears, key));
            }

            if (candidates.Count == 0 && oversizedCarLength > 0)
            {
                return new SortAction
                {
                    Kind = SortActionKind.Blocked,
                    Reason = $"a {oversizedCarLength:F0} m car exceeds the switching lead limit ({assignment.MaxCutLength:F0} m)",
                };
            }

            candidates.Sort((a, b) => CompareTuple(a.Key, b.Key));

            // Pick the highest-ranked candidate that has not already been attempted from this exact yard state.
            foreach (var c in candidates)
            {
                var moveKey = stateSig != null ? stateSig + "|P" + c.Track.Track.Track + ":" + c.PullCount : null;
                if (moveKey != null && !visitedMoves.Add(moveKey))
                    continue;
                return BuildPullAction(c.Track, c.PullCount, c.Clears,
                    $"pulling {c.PullCount} car{(c.PullCount == 1 ? "" : "s")} from {c.Track.Track.Name}");
            }

            // Fallback 1: try pulling a full max-lead batch from any dirty track.
            foreach (var c in candidates)
            {
                var fullCount = ChooseMaxBatchCount(c.Track, unsorted[c.Track.Track.Track], assignment.MaxCutLength, out var clears);
                if (fullCount <= 0 || fullCount == c.PullCount)
                    continue;
                var moveKey = stateSig != null ? stateSig + "|P" + c.Track.Track.Track + ":" + fullCount : null;
                if (moveKey != null && !visitedMoves.Add(moveKey))
                    continue;
                return BuildPullAction(c.Track, fullCount, clears,
                    $"pulling {fullCount} car{(fullCount == 1 ? "" : "s")} from {c.Track.Track.Name}");
            }

            // Fallback 2: try shorter legal batch sizes (preferring clean key boundaries, then any 1..fullCount-1)
            // so batch-boundary cycles (e.g. seed 3569) always explore every remaining legal pull before giving up.
            foreach (var c in candidates)
            {
                var tid = c.Track.Track.Track;
                var uCount = unsorted[tid];
                var fullCount = ChooseMaxBatchCount(c.Track, uCount, assignment.MaxCutLength, out _);
                if (fullCount <= 1)
                    continue;
                var cars = c.Track.AllCarsInnerToOuter();
                // Pass A: key boundaries first; Pass B: any remaining legal count from fullCount-1 down to 1.
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var k = fullCount - 1; k >= 1; k--)
                    {
                        var boundaryIdx = cars.Count - k;
                        var isBoundary = boundaryIdx == 0
                            || !string.Equals(cars[boundaryIdx - 1].Key ?? "", cars[boundaryIdx].Key ?? "", StringComparison.Ordinal);
                        if ((pass == 0) != isBoundary)
                            continue;
                        var moveKey = stateSig != null ? stateSig + "|P" + tid + ":" + k : null;
                        if (moveKey != null && !visitedMoves.Add(moveKey))
                            continue;
                        return BuildPullAction(c.Track, k, k >= uCount,
                            $"pulling {k} car{(k == 1 ? "" : "s")} from {c.Track.Track.Name}");
                    }
                }
            }

            return new SortAction
            {
                Kind = SortActionKind.Blocked,
                Reason = "couldn't find further progress without repeating previous switching moves",
            };
        }

        /// <summary>
        /// For a ready set-out of an overflow key spanning multiple tracks, picks the largest prefix of
        /// <paramref name="run"/> that fits on <paramref name="targetState"/> without stranding the
        /// remaining unplaced cars of <paramref name="key"/> across the key's assigned tracks.
        /// Sets <paramref name="feasible"/> to true when a packable prefix exists on <paramref name="targetState"/>.
        /// </summary>
        private static int ChooseFeasibleOverflowDrop(string key, List<SortCar> run, YardTrackState targetState,
            SortAssignment assignment, List<YardTrackState> usable, Dictionary<int, YardTrackState> byTrack,
            Dictionary<int, int> anchors, IReadOnlyList<SortCar> inTrainFarToNear, out bool feasible)
        {
            feasible = false;
            var targetFree = FreeRoom(targetState);
            var maxFit = 0;
            var acc = 0.0;
            while (maxFit < run.Count && acc + run[maxFit].Length <= targetFree + 0.5)
            {
                acc += run[maxFit].Length;
                maxFit++;
            }
            if (maxFit <= 0)
                return 0;
            if (!assignment.KeyTracks.TryGetValue(key, out var keyTracks) || keyTracks.Count <= 1)
            {
                feasible = true;
                return maxFit;
            }

            // Collect all unplaced cars of `key` outside the prefix we're testing:
            // remaining hook cars of `key` (after index k) + unsorted yard cars of `key`.
            var unsortedYardCars = new List<double>();
            foreach (var ts in usable)
            {
                var cars = ts.AllCarsInnerToOuter();
                var anc = anchors.TryGetValue(ts.Track.Track, out var a) ? a : 0;
                for (var i = anc; i < cars.Count; i++)
                    if (string.Equals(cars[i].Key ?? "", key, StringComparison.Ordinal))
                        unsortedYardCars.Add(cars[i].Length);
            }
            var hookTailAfterRun = new List<double>();
            for (var i = run.Count; i < inTrainFarToNear.Count; i++)
                if (string.Equals(inTrainFarToNear[i].Key ?? "", key, StringComparison.Ordinal))
                    hookTailAfterRun.Add(inTrainFarToNear[i].Length);

            // Available eventual capacity for `key` on each of its assigned tracks.
            var trackRooms = new List<(int Track, double Room)>(keyTracks.Count);
            foreach (var tid in keyTracks)
            {
                if (!byTrack.TryGetValue(tid, out var ts) || ts.Track.Blocked)
                    continue;
                double room;
                if (tid == targetState.Track.Track)
                {
                    room = targetFree;
                }
                else
                {
                    var cars = ts.AllCarsInnerToOuter();
                    var anc = anchors.TryGetValue(tid, out var a) ? a : 0;
                    var anchoredLen = 0.0;
                    for (var i = 0; i < anc && i < cars.Count; i++)
                        anchoredLen += cars[i].Length;
                    room = Math.Max(FreeRoom(ts), Math.Max(0, ts.Track.UsableLength - anchoredLen));
                }
                trackRooms.Add((tid, room));
            }

            // Try k from maxFit down to 1; pick the largest k leaving all remaining `key` cars packable.
            for (var k = maxFit; k >= 1; k--)
            {
                var droppedLen = 0.0;
                for (var i = 0; i < k; i++)
                    droppedLen += run[i].Length;

                var remaining = new List<double>(unsortedYardCars.Count + hookTailAfterRun.Count + (run.Count - k));
                for (var i = k; i < run.Count; i++)
                    remaining.Add(run[i].Length);
                remaining.AddRange(hookTailAfterRun);
                remaining.AddRange(unsortedYardCars);
                if (remaining.Count == 0)
                {
                    feasible = true;
                    return k;
                }
                remaining.Sort((a, b) => b.CompareTo(a));

                var caps = new double[trackRooms.Count];
                for (var i = 0; i < trackRooms.Count; i++)
                    caps[i] = trackRooms[i].Track == targetState.Track.Track
                        ? Math.Max(0, trackRooms[i].Room - droppedLen)
                        : trackRooms[i].Room;
                Array.Sort(caps);
                Array.Reverse(caps);

                var used = new double[caps.Length];
                var counts = new int[caps.Length];
                var budget = 4000;
                if (TryExactBinPack(remaining, 0, caps, used, counts, ref budget))
                {
                    feasible = true;
                    return k;
                }
            }
            return maxFit;
        }

        private static bool WouldBeReadyAfterClear(YardTrackState ts, int anchorCount, string key, SortAssignment assignment,
            Dictionary<string, int> totalByKey, Dictionary<string, int> totalAnchoredByKey)
        {
            if (!assignment.TrackKeys.TryGetValue(ts.Track.Track, out var allowed))
                return false;
            var idx = allowed.IndexOf(key ?? "");
            if (idx < 0)
                return false;
            if (idx == 0)
                return true;
            for (var prev = 0; prev < idx; prev++)
            {
                var pk = allowed[prev];
                var have = totalAnchoredByKey.TryGetValue(pk, out var n) ? n : 0;
                var need = totalByKey.TryGetValue(pk, out var tot) ? tot : 0;
                if (have < need)
                    return false;
            }
            return true;
        }

        private static int CompareTuple((int a, int b, int c, int d, int e, double f) x, (int a, int b, int c, int d, int e, double f) y)
        {
            if (x.a != y.a) return x.a.CompareTo(y.a);
            if (x.b != y.b) return x.b.CompareTo(y.b);
            if (x.c != y.c) return x.c.CompareTo(y.c);
            if (x.d != y.d) return x.d.CompareTo(y.d);
            if (x.e != y.e) return x.e.CompareTo(y.e);
            return x.f.CompareTo(y.f);
        }

        private static int ChooseMaxBatchCount(YardTrackState ts, int unsortedCount, double maxCutLength, out bool clearsTrack)
        {
            var cars = ts.AllCarsInnerToOuter();
            var len = 0.0;
            var count = 0;
            for (var i = cars.Count - 1; i >= cars.Count - unsortedCount; i--)
            {
                if (len + cars[i].Length > maxCutLength + 1e-6)
                    break;
                len += cars[i].Length;
                count++;
            }
            clearsTrack = count >= unsortedCount && count > 0;
            return Math.Min(count, unsortedCount);
        }

        /// <summary>
        /// Chooses how many unsorted cars from the outer end of <paramref name="ts"/> to pull in one
        /// batch: all <paramref name="unsortedCount"/> if they fit on the switching lead, otherwise as
        /// many as fit. Avoids splitting a key only when doing so keeps at least half the batch AND
        /// does not drop directly-ready cars from the pull.
        /// </summary>
        private static int ChooseBatchCount(YardTrackState ts, int unsortedCount, double maxCutLength,
            Func<string, bool> isDirectKey, out bool clearsTrack)
        {
            var count = ChooseMaxBatchCount(ts, unsortedCount, maxCutLength, out clearsTrack);
            if (count <= 0 || clearsTrack)
                return count;
            var cars = ts.AllCarsInnerToOuter();
            var minShrunk = Math.Max(1, (count + 1) / 2);
            var boundaryIdx = cars.Count - count;
            var splitKey = cars[boundaryIdx].Key ?? "";
            // Never shrink past cars whose key is already ready for a clean track!
            if (isDirectKey != null && isDirectKey(splitKey))
                return count;
            if (boundaryIdx > 0 && string.Equals(cars[boundaryIdx - 1].Key ?? "", splitKey, StringComparison.Ordinal))
            {
                var shrunk = count;
                while (shrunk > minShrunk && string.Equals(cars[cars.Count - shrunk].Key ?? "", splitKey, StringComparison.Ordinal))
                    shrunk--;
                if (shrunk >= minShrunk && !string.Equals(cars[cars.Count - shrunk].Key ?? "", splitKey, StringComparison.Ordinal))
                    return shrunk;
            }
            return count;
        }

        private static double UnsortedTailLength(YardTrackState ts, int unsortedCount)
        {
            var cars = ts.AllCarsInnerToOuter();
            var len = 0.0;
            for (var i = Math.Max(0, cars.Count - unsortedCount); i < cars.Count; i++)
                len += cars[i].Length;
            return len;
        }

        private static double FreeRoom(YardTrackState ts) =>
            Math.Max(0, ts.Track.UsableLength - ts.OccupiedLength);

        private static YardTrackState PickSluffTrack(SortAssignment assignment, List<YardTrackState> usable,
            Dictionary<int, YardTrackState> byTrack, Dictionary<int, int> unsorted, Dictionary<int, int> waitingForTrack,
            int target, int lastPulled, string key, int runCount, double runLen, HashSet<string> hookKeysAfter,
            string stateSig, HashSet<string> visitedMoves)
        {
            bool Allowed(int tid) =>
                stateSig == null || visitedMoves == null || !visitedMoves.Contains(stateSig + "|S" + tid + ":" + runCount);

            // 1. Unassigned spare track with room (prefer one already holding the same key on the outside, else emptiest).
            YardTrackState bestSpare = null;
            var bestSpareSameKey = false;
            var bestSpareFree = -1.0;
            foreach (var sp in assignment.SpareTracks)
            {
                if (sp == lastPulled || !byTrack.TryGetValue(sp, out var ts) || ts.Track.Blocked || !Allowed(sp))
                    continue;
                var free = FreeRoom(ts);
                if (free < runLen - 0.5)
                    continue;
                var outerSame = ts.TotalCars > 0 && string.Equals(OuterKey(ts), key, StringComparison.Ordinal);
                if (bestSpare == null || (outerSame && !bestSpareSameKey) || (outerSame == bestSpareSameKey && free > bestSpareFree))
                {
                    bestSpare = ts;
                    bestSpareSameKey = outerSame;
                    bestSpareFree = free;
                }
            }
            if (bestSpare != null)
                return bestSpare;

            // 2. Another DIRTY track != target and != lastPulled (never contaminates a clean track and never dumps back onto target).
            YardTrackState bestDirty = null;
            var bestDirtyScore = double.NegativeInfinity;
            foreach (var ts in usable)
            {
                var tid = ts.Track.Track;
                if (tid == lastPulled || tid == target || unsorted[tid] == 0 || !Allowed(tid))
                    continue;
                var free = FreeRoom(ts);
                if (free < runLen - 0.5)
                    continue;
                var outerSame = ts.TotalCars > 0 && string.Equals(OuterKey(ts), key, StringComparison.Ordinal);
                var score = free + (outerSame ? 40.0 : 0.0);
                if (bestDirty == null || score > bestDirtyScore)
                {
                    bestDirty = ts;
                    bestDirtyScore = score;
                }
            }
            if (bestDirty != null)
                return bestDirty;

            // 3. Another DIRTY track that happens to be target (still better than contaminating a CLEAN track!).
            if (target >= 0 && target != lastPulled && byTrack.TryGetValue(target, out var dirtyTarget)
                && !dirtyTarget.Track.Blocked && unsorted[target] > 0 && FreeRoom(dirtyTarget) >= runLen - 0.5
                && Allowed(target))
                return dirtyTarget;

            // 4. Clean track != target and != lastPulled: strongly prefer a COMPLETED clean track (0 cars waiting for it),
            // then one not needed by remaining cars on the hook, then most free room.
            YardTrackState bestClean = null;
            var bestCleanComplete = false;
            var bestCleanNotHook = false;
            var bestCleanFree = -1.0;
            foreach (var ts in usable)
            {
                var tid = ts.Track.Track;
                if (tid == target || tid == lastPulled || !Allowed(tid))
                    continue;
                var free = FreeRoom(ts);
                if (free < runLen - 0.5)
                    continue;
                var complete = waitingForTrack.TryGetValue(tid, out var w) && w == 0;
                var neededByHook = assignment.TrackKeys.TryGetValue(tid, out var keys) && keys.Any(k => hookKeysAfter.Contains(k));
                var notHook = !neededByHook;
                var better = bestClean == null
                    || (complete != bestCleanComplete ? complete
                        : notHook != bestCleanNotHook ? notHook
                        : free > bestCleanFree);
                if (better)
                {
                    bestClean = ts;
                    bestCleanComplete = complete;
                    bestCleanNotHook = notHook;
                    bestCleanFree = free;
                }
            }
            if (bestClean != null)
                return bestClean;

            // 5. Absolute last resort (clean target waiting on an earlier sub-block when all other tracks are full):
            if (target >= 0 && target != lastPulled && byTrack.TryGetValue(target, out var targetTs)
                && !targetTs.Track.Blocked && FreeRoom(targetTs) >= runLen - 0.5 && Allowed(target))
                return targetTs;

            // 6. Final fallback: if every other track is physically full, return the leftover car(s) to lastPulled.
            if (lastPulled >= 0 && byTrack.TryGetValue(lastPulled, out var backTs)
                && !backTs.Track.Blocked && FreeRoom(backTs) >= runLen - 0.5 && Allowed(lastPulled))
                return backTs;

            // 7. Never strand a loaded hook solely due to visitedMoves if a track has physical room;
            // the Pull cycle breaker will choose a fresh pull size on the next empty-hook step.
            if (stateSig != null && visitedMoves != null)
                return PickSluffTrack(assignment, usable, byTrack, unsorted, waitingForTrack, target, lastPulled, key, runCount, runLen, hookKeysAfter, null, null);

            return null;
        }

        private static string OuterKey(YardTrackState ts)
        {
            if (ts.CutsInnerToOuter.Count == 0)
                return null;
            var lastCut = ts.CutsInnerToOuter[ts.CutsInnerToOuter.Count - 1];
            return lastCut.CarsInnerToOuter.Count > 0 ? lastCut.CarsInnerToOuter[lastCut.CarsInnerToOuter.Count - 1].Key ?? "" : null;
        }

        private static SortAction BuildPullAction(YardTrackState ts, int pullCount, bool clearsTrack, string reason)
        {
            if (ts == null || pullCount <= 0 || ts.CutsInnerToOuter.Count == 0)
                return null;
            var action = new SortAction
            {
                Kind = SortActionKind.Pull,
                Track = ts.Track.Track,
                PullCount = pullCount,
                ClearsTrack = clearsTrack,
                Reason = reason,
            };
            var remaining = pullCount;
            for (var ci = ts.CutsInnerToOuter.Count - 1; ci >= 0 && remaining > 0; ci--)
            {
                var cut = ts.CutsInnerToOuter[ci];
                action.PullCuts.Add(cut);
                var n = cut.CarsInnerToOuter.Count;
                if (remaining >= n)
                {
                    remaining -= n;
                }
                else
                {
                    var leave = n - remaining;
                    for (var k = 0; k < leave; k++)
                        action.LeaveBehindCarIds.Add(cut.CarsInnerToOuter[k].Id);
                    remaining = 0;
                }
            }
            return action;
        }

        private static SortAction MakeSetOut(YardTrackState dest, List<SortCar> run, string key, bool joinSorted, bool isSluff)
        {
            var action = new SortAction
            {
                Kind = SortActionKind.SetOut,
                Track = dest.Track.Track,
                SetOutCount = run.Count,
                Key = key,
                JoinSorted = joinSorted,
                IsSluff = isSluff,
                Reason = isSluff
                    ? $"staging {run.Count} car{(run.Count == 1 ? "" : "s")} ({SortAssignment.FormatKey(key)}) on {dest.Track.Name}"
                    : $"setting out {run.Count} car{(run.Count == 1 ? "" : "s")} ({SortAssignment.FormatKey(key)}) on {dest.Track.Name}",
            };
            foreach (var c in run)
                action.SetOutCarIds.Add(c.Id);
            return action;
        }
    }
}
