using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>Snapshot of the tablet's asynchronous yard sort preview.</summary>
    internal sealed class SortPreview
    {
        public SortAssignment Assignment;
        public string SuspendedYard;
        public bool Computing;
    }

    /// <summary>
    /// Classifies (flat-switches) a yard bowl — an interchange yard (WI, NEI)
    /// or a station's freight sub-yard (HB-C, MF-B, ...) — with one AI
    /// engineer: chooses the best switching ladder, measures physical lead
    /// capacity on a frozen copy of the world off the game's thread, assigns
    /// each destination (or job) to a track, and drives pull / set-out /
    /// consolidation cycles until every track holds one clean coupled cut.
    /// </summary>
    internal sealed class SortRun
    {
        private enum Phase
        {
            /// <summary>Queued behind other orders, or stopping to uncouple the loco's other side.</summary>
            Starting,

            /// <summary>Waiting for Persistent Jobs to restore suspended cars in this yard.</summary>
            Resuming,

            /// <summary>Working through pull, set-out, and consolidation cycles.</summary>
            Working,
        }

        private const int MoveLimit = 400;
        private const int MaxReplans = 3;
        private const float NoRouteSeconds = 45;

        private readonly World world;
        private readonly SortableBowl bowl;
        private readonly SortMode mode;
        private readonly Dictionary<int, string> trackNames = new Dictionary<int, string>();
        private readonly HashSet<Waypoint> mine = new HashSet<Waypoint>();
        private readonly HashSet<Waypoint> before = new HashSet<Waypoint>();
        private readonly HashSet<Waypoint> later = new HashSet<Waypoint>();
        private readonly List<Waypoint> issued = new List<Waypoint>();
        private readonly List<OccupiedSpan> standing = new List<OccupiedSpan>();
        private readonly HashSet<Waypoint> clearTried = new HashSet<Waypoint>();
        private readonly HashSet<Waypoint> clearMoves = new HashSet<Waypoint>();
        private readonly Dictionary<string, int> couplingsFailed = new Dictionary<string, int>();
        private readonly HashSet<string> visitedMoves = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<TrainCar> keptCarSet = new HashSet<TrainCar>();

        private SetupPending pending;
        private SortAssignment assignment;
        private Phase phase;
        private Waypoint stuckOn;
        private float stuckSince;
        private string resumingYard;
        private float nextResume;
        private float nextKeepBowl;
        private bool spotInvalidated;
        private int replans;
        private int lastPulledTrack = -1;
        private int clearingTrack = -1;
        private int consecutivePulls;
        private int moves;
        private int setOuts;
        private int inPlaceCars;
        private int totalCars;

        // ---- Asynchronous tablet preview state (off the main thread) ----
        private static System.Threading.Tasks.Task<SortAssignment> previewTask;
        private static SortAssignment previewResult;
        private static SortableBowl previewBowl;
        private static SortMode previewMode;
        private static TrainCar previewLoco;
        private static float previewNextScan;
        private static int previewFingerprint = int.MinValue;
        private static string previewSuspended;

        private SortRun(World world, SortableBowl bowl, SortMode mode)
        {
            this.world = world;
            this.bowl = bowl;
            this.mode = mode;
            foreach (var t in bowl.Tracks)
                trackNames[t.Track] = t.Id;
        }

        public string YardName => bowl.Name;

        public SortMode Mode => mode;

        public bool Planning => pending != null || phase == Phase.Resuming;

        public string HoldingReason =>
            phase == Phase.Resuming ? $"Waiting for Persistent Jobs to bring back the cars at {resumingYard}"
            : pending != null ? "Planning the yard sort"
            : null;

        public bool Owns(Waypoint order) => mine.Contains(order);

        public string Status { get; private set; } = "";

        public string Summary => assignment == null
            ? $"Sorting {bowl.Name} ({ModeLabel(mode)})"
            : $"Sorting {bowl.Name} {ModeLabel(mode)} · {inPlaceCars}/{totalCars} cars in place";

        public static string ModeLabel(SortMode m) => m == SortMode.ByJob ? "by job" : "by destination";

        /// <summary>Clears cached tablet preview state when the world unloads or the user switches mode.</summary>
        public static void ResetForUnload()
        {
            previewTask = null;
            previewResult = null;
            previewBowl = null;
            previewLoco = null;
            previewNextScan = 0f;
            previewFingerprint = int.MinValue;
            previewSuspended = null;
        }

        /// <summary>
        /// Returns the game station yard ID if Persistent Jobs currently holds cars suspended
        /// on any track of <paramref name="bowl"/>; null otherwise.
        /// </summary>
        public static string SuspendedYard(World world, SortableBowl bowl)
        {
            if (world?.Network == null || bowl == null || !PersistentJobs.Present)
                return null;
            var checkedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nt in bowl.Tracks)
            {
                if (nt.Track < 0 || nt.Track >= world.Network.Tracks.Length || world.Network.Tracks[nt.Track] == null)
                    continue;
                string yid = null;
                try
                {
                    var id = world.Network.Tracks[nt.Track].LogicTrack()?.ID;
                    if (id != null && !id.IsGeneric())
                        yid = id.yardId;
                }
                catch (Exception)
                {
                }
                if (!string.IsNullOrEmpty(yid) && checkedIds.Add(yid) && PersistentJobs.Suspended(yid))
                    return yid;
            }
            if (!string.IsNullOrEmpty(bowl.YardId) && checkedIds.Add(bowl.YardId) && PersistentJobs.Suspended(bowl.YardId))
                return bowl.YardId;
            return null;
        }

        /// <summary>
        /// Cheap fingerprint of standing positions plus every bowl and loco car's live classification key,
        /// so stationary cars acquiring or losing a job invalidate cached previews and in-flight setups.
        /// </summary>
        private static int YardFingerprint(World world, SortableBowl bowl, SortMode mode, TrainCar loco)
        {
            unchecked
            {
                var h = 17;
                h = h * 31 + world.Occupancy.StandingStamp;
                var jobs = SingletonBehaviour<JobsManager>.Instance;
                var spans = new List<OccupiedSpan>();
                var ownId = loco?.trainset != null ? loco.trainset.id : int.MinValue;
                foreach (var nt in bowl.Tracks)
                {
                    h = h * 31 + nt.Track;
                    world.Occupancy.Standing(nt.Track, ownId, spans);
                    var seenOwners = new HashSet<int>();
                    foreach (var s in spans)
                    {
                        if (!seenOwners.Add(s.Owner))
                            continue;
                        h = h * 31 + s.Owner;
                        var set = FindSet(s.Owner);
                        if (set == null)
                            continue;
                        h = h * 31 + set.cars.Count;
                        h = h * 31 + set.locoIndices.Count;
                        for (var i = 0; i < set.cars.Count; i++)
                        {
                            var c = set.cars[i];
                            if (c == null)
                                continue;
                            h = h * 31 + c.GetInstanceID();
                            h = h * 31 + (c.derailed ? 1 : 0);
                            var k = KeyOf(c, mode, jobs);
                            h = h * 31 + (k != null ? StringComparer.Ordinal.GetHashCode(k) : 0);
                        }
                    }
                }
                if (loco?.trainset != null)
                {
                    var cars = loco.trainset.cars;
                    h = h * 31 + loco.trainset.id;
                    h = h * 31 + cars.Count;
                    for (var i = 0; i < cars.Count; i++)
                    {
                        var c = cars[i];
                        if (c == null)
                            continue;
                        h = h * 31 + c.GetInstanceID();
                        var k = KeyOf(c, mode, jobs);
                        h = h * 31 + (k != null ? StringComparer.Ordinal.GetHashCode(k) : 0);
                    }
                }
                return h;
            }
        }

        /// <summary>
        /// Returns the latest yard sort preview for the tablet UI without running graph searches
        /// or assignment on Unity's main thread. Snapshots occupancy at most every 1.5 s when the
        /// yard or any car's job changes, and computes ladder orientation, corridor distances, and assignment on a worker thread.
        /// </summary>
        public static SortPreview GetPreview(World world, SortableBowl bowl, SortMode mode, TrainCar loco)
        {
            if (previewBowl != bowl || previewMode != mode || previewLoco != loco)
            {
                previewTask = null;
                previewResult = null;
                previewBowl = bowl;
                previewMode = mode;
                previewLoco = loco;
                previewNextScan = 0f;
                previewFingerprint = int.MinValue;
                previewSuspended = null;
            }

            if (previewTask != null && previewTask.IsCompleted)
            {
                if (!previewTask.IsFaulted && !previewTask.IsCanceled)
                {
                    previewResult = previewTask.Result;
                }
                else
                {
                    var err = previewTask.Exception?.GetBaseException();
                    Main.Warn($"sort: preview failed for {bowl.Name}: {err?.Message}");
                    if (previewResult == null)
                    {
                        var failed = new SortAssignment();
                        failed.Problems.Add($"preview failed ({err?.Message ?? "canceled"})");
                        previewResult = failed;
                    }
                }
                previewTask = null;
            }

            var suspended = SuspendedYard(world, bowl);
            if (previewTask == null && (previewResult == null || Time.unscaledTime >= previewNextScan))
            {
                previewNextScan = Time.unscaledTime + 1.5f;
                world.ScanCars();
                var fp = YardFingerprint(world, bowl, mode, loco);
                if (previewResult == null || fp != previewFingerprint
                    || !string.Equals(suspended, previewSuspended, StringComparison.Ordinal))
                {
                    previewFingerprint = fp;
                    previewSuspended = suspended;
                    previewTask = LaunchPreviewTask(world, bowl, mode, loco);
                }
            }

            return new SortPreview
            {
                Assignment = previewResult,
                SuspendedYard = suspended,
                Computing = previewTask != null,
            };
        }

        private static System.Threading.Tasks.Task<SortAssignment> LaunchPreviewTask(World world, SortableBowl bowl, SortMode mode, TrainCar loco)
        {
            var initialEnds = InitialEnds(world.Graph, bowl);
            var ownId = loco?.trainset != null ? loco.trainset.id : int.MinValue;
            var tracks = ReadYardTracks(world, bowl, mode, initialEnds, ownId, null, out _);
            var inTrain = loco != null ? ReadLocoCars(loco, mode, out _) : new List<SortCar>();
            TrackPosition? locoPos = null;
            if (loco != null && world.TryPlace(loco.frontCoupler, out var lp, out _))
                locoPos = lp;

            Dictionary<string, int> repTracks = null;
            if (mode == SortMode.ByDestination)
            {
                var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ts in tracks)
                    if (!ts.Track.Blocked)
                        foreach (var c in ts.AllCarsInnerToOuter())
                            uniqueKeys.Add(c.Key ?? "");
                foreach (var c in inTrain)
                    uniqueKeys.Add(c.Key ?? "");
                repTracks = FilterRepTracks(Dispatch.YardRepTracks(world.Network), uniqueKeys);
            }
            var yardRep = bowl.Tracks.Count > 0 ? bowl.Tracks[0].Track : -1;
            var trackIds = bowl.Tracks.Select(t => t.Track).ToArray();
            var frozen = world.Freeze();

            return System.Threading.Tasks.Task.Run(() => ComputePreview(frozen, trackIds, locoPos, tracks, inTrain, mode, repTracks, yardRep));
        }

        private static SortAssignment ComputePreview(FrozenWorld frozen, int[] trackIds, TrackPosition? locoPos,
            List<YardTrackState> tracks, List<SortCar> inTrain, SortMode mode,
            Dictionary<string, int> repTracks, int yardRep)
        {
            var graph = frozen.Graph;
            var ends = QuickWorkingEnds(graph, frozen.SelectedBranch, trackIds, locoPos);
            foreach (var ts in tracks)
            {
                if (!ends.TryGetValue(ts.Track.Track, out var we))
                    continue;
                if (ts.Track.WorkingEnd != we)
                {
                    ts.Track.WorkingEnd = we;
                    ts.Track.DeadEnd = graph.Links(ts.Track.Track, we.Other()).Length == 0;
                    YardSort.ReorientTrack(ts);
                }
                if (!ts.Track.Blocked && graph.Links(ts.Track.Track, we).Length == 0)
                {
                    ts.Track.Blocked = true;
                    ts.Track.BlockedReason = "no switch into the working ladder";
                    ts.CutsInnerToOuter.Clear();
                }
            }

            var usableCount = tracks.Count(t => !t.Track.Blocked && t.Track.UsableLength >= YardSort.MinUsableTrack);
            var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ts in tracks)
                if (!ts.Track.Blocked)
                    foreach (var c in ts.AllCarsInnerToOuter())
                        uniqueKeys.Add(c.Key ?? "");
            foreach (var c in inTrain)
                uniqueKeys.Add(c.Key ?? "");

            Func<string, string, double> keyDist = null;
            Func<string, double> originDist = null;
            if (mode == SortMode.ByDestination && uniqueKeys.Count > usableCount && usableCount >= 2
                && repTracks != null && repTracks.Count > 1)
            {
                YardSort.BuildDistances(graph, repTracks, yardRep, out keyDist, out originDist);
            }
            return YardSort.Assign(tracks, inTrain, keyDist, originDist);
        }

        /// <summary>Starts (or queues) a yard sort run on <paramref name="session"/>.</summary>
        public static SortRun Start(Session session, World world, SortableBowl bowl, SortMode mode, bool queue, out List<string> problems)
        {
            problems = new List<string>();
            if (bowl == null || bowl.Tracks.Count < 2)
            {
                problems.Add("a yard needs at least two tracks to sort");
                return null;
            }
            if (queue && (session.Jobs != null || session.Sort != null))
            {
                problems.Add(session.Sort != null
                    ? $"already sorting {session.Sort.YardName}; use Sort now to switch yards"
                    : "can't queue a yard sort behind an active job run; use Sort now to switch to sorting");
                return null;
            }
            if (session.Jobs != null)
            {
                session.Jobs.Cancel(session, "it was ordered to sort a yard");
                session.Jobs = null;
            }
            if (session.Sort != null)
            {
                session.Sort.Cancel(session, "it was given another yard to sort");
                session.Sort = null;
            }
            var run = new SortRun(world, bowl, mode);
            if (queue)
            {
                foreach (var w in session.Engineer.Queue)
                    run.before.Add(w);
            }
            else
            {
                session.Engineer.ClearQueue();
            }
            session.Engineer.SetWaypoints();
            session.Sort = run;
            Main.Log($"sort: {session.Name}: ordered to sort {bowl.Name} ({ModeLabel(mode)})");
            if (run.before.Count > 0)
            {
                run.phase = Phase.Starting;
                run.Status = "Waiting its turn behind the orders queued";
            }
            else
            {
                run.Begin(session);
            }
            return run;
        }

        private void Begin(Session session)
        {
            // If Persistent Jobs holds this remote yard's cars suspended, bring them back before scanning.
            var suspended = SuspendedYard(world, bowl);
            if (suspended != null)
            {
                resumingYard = suspended;
                phase = Phase.Resuming;
                nextResume = 0f;
                TickResume(session);
                return;
            }

            // Uncouple cars on the loco's other side first, where they stand (same rule as JobRun).
            var other = session.OtherSide();
            if (other.Count > 0)
            {
                if (session.HasState && Math.Abs(session.State.SpeedTowardA) > 0.1)
                {
                    phase = Phase.Starting;
                    Status = "Stopping to uncouple cars on the loco's other side";
                    return;
                }
                var onYard = other.TrueForAll(c => TrackOf(c, out _));
                var noLoco = other.TrueForAll(c => !CarTypes.IsAnyLocomotiveOrTender(c.carLivery) && !c.IsCaboose);
                if (onYard && noLoco)
                {
                    var n = session.CutOtherSide(setHandbrakes: true);
                    Main.Log($"sort: {session.Name}: uncoupled {n} car{(n == 1 ? "" : "s")} on its other side first");
                    session.Engineer.Note = $"uncoupled the {n} car{(n == 1 ? "" : "s")} on its other side first";
                    world.ScanCars();
                }
            }
            KeepBowlCars(session);
            LaunchSetup(session);
        }

        private void TickResume(Session session)
        {
            var suspended = SuspendedYard(world, bowl);
            if (suspended != null)
            {
                resumingYard = suspended;
                if (Time.unscaledTime >= nextResume)
                {
                    nextResume = Time.unscaledTime + 5f;
                    if (PersistentJobs.Resume(suspended))
                        Main.Log($"sort: {session.Name}: asked Persistent Jobs to bring back the cars at {suspended} before sorting {bowl.Name}");
                }
                Status = $"Waiting for Persistent Jobs to bring back the cars at {suspended}";
                return;
            }
            if (PersistentJobs.Busy)
            {
                Status = $"Waiting for Persistent Jobs to bring back the cars at {resumingYard}";
                return;
            }
            Main.Log($"sort: {session.Name}: cars at {resumingYard} are back; scanning {bowl.Name}");
            resumingYard = null;
            phase = Phase.Starting;
            world.ScanCars();
            KeepBowlCars(session);
            Begin(session);
        }

        private void KeepBowlCars(Session session)
        {
            nextKeepBowl = Time.unscaledTime + 2f;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            if (registry == null || world?.Network == null)
                return;
            foreach (var nt in bowl.Tracks)
            {
                if (nt.Track < 0 || nt.Track >= world.Network.Tracks.Length || world.Network.Tracks[nt.Track] == null)
                    continue;
                DV.Logic.Job.Track logic;
                try
                {
                    logic = world.Network.Tracks[nt.Track].LogicTrack();
                }
                catch (Exception)
                {
                    continue;
                }
                if (logic == null)
                    continue;
                try
                {
                    foreach (var list in new[] { logic.GetCarsFullyOnTrack(), logic.GetCarsPartiallyOnTrack() })
                    {
                        if (list == null)
                            continue;
                        foreach (var car in list)
                        {
                            if (car == null || !registry.logicCarToTrainCar.TryGetValue(car, out var tc) || tc == null)
                                continue;
                            var set = tc.trainset;
                            // Skip own consist and any trainset containing a locomotive (those tracks are blocked and
                            // other engineers own their own locos' preventDelete protection).
                            if (set == null || set.cars == null || set.locoIndices.Count > 0
                                || (session.Loco != null && set == session.Loco.trainset))
                                continue;
                            foreach (var c in set.cars)
                            {
                                if (c == null || !keptCarSet.Add(c))
                                    continue;
                                Session.CarProtection.Acquire(c);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private void ReleaseKept(Session session)
        {
            foreach (var car in keptCarSet)
                Session.CarProtection.Release(car);
            keptCarSet.Clear();
        }

        private sealed class SetupPending
        {
            public System.Threading.Tasks.Task<YardSortSetupResult> Task;
            public Trainset Train;
            public int TrainCars;
            public int Fingerprint;
            public System.Diagnostics.Stopwatch Since;
        }

        private void LaunchSetup(Session session)
        {
            visitedMoves.Clear();
            spotInvalidated = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            world.ScanCars();
            var fp = YardFingerprint(world, bowl, mode, session.Loco);
            var state = session.Read();
            var initialEnds = InitialEnds(world.Graph, bowl);
            var ownId = session.Loco.trainset != null ? session.Loco.trainset.id : int.MinValue;
            var tracks = ReadYardTracks(world, bowl, mode, initialEnds, ownId, null, out _);
            var inTrain = ReadLocoCars(session.Loco, mode, out _);
            var uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ts in tracks)
                if (!ts.Track.Blocked)
                    foreach (var c in ts.AllCarsInnerToOuter())
                        uniqueKeys.Add(c.Key ?? "");
            foreach (var c in inTrain)
                uniqueKeys.Add(c.Key ?? "");

            var repTracks = mode == SortMode.ByDestination
                ? FilterRepTracks(Dispatch.YardRepTracks(world.Network), uniqueKeys)
                : null;
            var req = new YardSortSetupRequest
            {
                World = world.Freeze(),
                Train = state,
                PowerLength = PowerLength(session),
                Tracks = tracks,
                InTrain = inTrain,
                KeyRepTrack = repTracks,
                YardRepTrack = bowl.Tracks.Count > 0 ? bowl.Tracks[0].Track : -1,
            };
            pending = new SetupPending
            {
                Train = session.Loco.trainset,
                TrainCars = session.Loco.trainset != null ? session.Loco.trainset.cars.Count : 0,
                Fingerprint = fp,
                Since = watch,
                Task = System.Threading.Tasks.Task.Run(() => YardSort.PlanSetup(req)),
            };
            Status = $"Planning how to sort {bowl.Name}";
        }

        private void SetupReady(Session session)
        {
            var asked = pending;
            pending = null;
            if (asked.Task.IsFaulted || asked.Task.IsCanceled)
            {
                var error = asked.Task.Exception?.GetBaseException();
                Main.Warn($"sort: {session.Name}: planning failed: {error}");
                End(session, $"planning the yard sort failed ({error?.GetType().Name ?? "canceled"}: {error?.Message})");
                return;
            }
            world.ScanCars();
            var train = session.Loco.trainset;
            var liveFp = YardFingerprint(world, bowl, mode, session.Loco);
            if (train != asked.Train || (train != null ? train.cars.Count : 0) != asked.TrainCars || liveFp != asked.Fingerprint)
            {
                Main.Log($"sort: {session.Name}: yard or consist changed while planning {bowl.Name}; planning again");
                LaunchSetup(session);
                return;
            }
            var result = asked.Task.Result;
            assignment = result.Assignment;
            var planDesc = string.Join("; ", assignment.Lines.Select(l =>
                l.Blocked ? $"{l.TrackName}: skipped ({l.BlockedReason})"
                : l.Keys.Count == 0 ? $"{l.TrackName}: spare"
                : $"{l.TrackName}: {string.Join("+", l.Keys.Select(SortAssignment.FormatKey))} ({l.PlannedCars})"));
            Main.Log($"sort: {session.Name}: plan for {bowl.Name} ({ModeLabel(mode)}) in {result.Milliseconds:F0} ms (lead fits {assignment.MaxCutLength:F0} m of cars): {planDesc}" +
                (result.Notes.Count > 0 ? " | " + string.Join("; ", result.Notes) : ""));

            if (!assignment.Valid)
            {
                End(session, string.Join("; ", assignment.Problems));
                return;
            }
            if (assignment.AlreadySorted)
            {
                ReleaseKept(session);
                Main.Notify($"AI engineer: {session.Name}: {bowl.Name} is already sorted.");
                Main.Log($"sort: {session.Name}: {bowl.Name} is already sorted");
                Status = "Done";
                session.Sort = null;
                return;
            }
            phase = Phase.Working;
            PlanNextMove(session);
        }

        public void Tick(Session session)
        {
            var engineer = session.Engineer;
            if (engineer.Mode != EngineerMode.Waypoints)
            {
                Cancel(session, "the engineer was given another mode");
                session.Sort = null;
                return;
            }
            if (session.Sort != this)
                return;
            if (!ReadOutcomes(session))
                return;
            if (phase == Phase.Working && Time.unscaledTime >= nextKeepBowl)
                KeepBowlCars(session);
            if (engineer.Queue.Count > 0)
            {
                var head = engineer.Queue[0];
                if (before.Contains(head))
                    return;
                if (mine.Contains(head))
                {
                    if (spotInvalidated)
                    {
                        spotInvalidated = false;
                        RemoveMine(engineer);
                        Main.Log($"sort: {session.Name}: working-end stretch on {head.Label} became occupied; replanning move");
                        PlanNextMove(session);
                        return;
                    }
                    for (var i = 1; i < engineer.Queue.Count; i++)
                    {
                        var order = engineer.Queue[i];
                        if (!mine.Contains(order) && !before.Contains(order))
                            later.Add(order);
                    }
                    WatchForNoRoute(session, head);
                    return;
                }
                if (!later.Contains(head))
                {
                    Cancel(session, "other orders came first");
                    session.Sort = null;
                    return;
                }
            }
            stuckOn = null;
            spotInvalidated = false;
            if (pending != null)
            {
                if (pending.Task.IsCompleted)
                    SetupReady(session);
                return;
            }
            if (phase == Phase.Resuming)
            {
                TickResume(session);
                return;
            }
            if (phase == Phase.Starting)
            {
                Begin(session);
                return;
            }
            if (++moves > MoveLimit)
            {
                End(session, $"it gave up after {MoveLimit} moves");
                return;
            }
            PlanNextMove(session);
        }

        private void PlanNextMove(Session session)
        {
            world.ScanCars();
            var ownId = session.Loco.trainset != null ? session.Loco.trainset.id : int.MinValue;
            var tracks = ReadYardTracks(world, bowl, mode, assignment.WorkingEnds, ownId, assignment, out var yardCarsById);
            var inTrain = ReadLocoCars(session.Loco, mode, out var locoCarsById);

            // Track progress for UI.
            totalCars = inTrain.Count;
            inPlaceCars = 0;
            foreach (var ts in tracks)
            {
                if (ts.Track.Blocked)
                    continue;
                totalCars += ts.TotalCars;
                inPlaceCars += YardSort.AnchorCount(ts, assignment, tracks, inTrain);
            }

            // Distance from loco to each track's working end for tie-breaking dirty track selection.
            Func<int, double> distToTrack = null;
            if (world.TryPlace(FarCoupler(session), out var pos, out _))
            {
                var gd = GraphDistances.From(world.Graph, pos);
                distToTrack = t =>
                {
                    var end = assignment.WorkingEnds.TryGetValue(t, out var e) ? e : TrackEnd.Start;
                    return gd.To(new TrackPosition(t, world.Graph.OffsetOf(t, end)));
                };
            }

            // Clear active multi-batch clearingTrack once it has no unsorted cars left.
            if (clearingTrack >= 0)
            {
                var ct = tracks.Find(t => t.Track.Track == clearingTrack);
                if (ct == null || ct.Track.Blocked || YardSort.AnchorCount(ct, assignment, tracks, inTrain) >= ct.TotalCars)
                    clearingTrack = -1;
            }

            var action = YardSort.NextAction(assignment, tracks, inTrain, lastPulledTrack, consecutivePulls, distToTrack, clearingTrack, visitedMoves);
            switch (action.Kind)
            {
                case SortActionKind.Done:
                    End(session, null);
                    return;

                case SortActionKind.Blocked:
                    if (action.NeedsReplan && replans < MaxReplans)
                    {
                        replans++;
                        Main.Log($"sort: {session.Name}: {action.Reason}; replanning ({replans}/{MaxReplans})");
                        lastPulledTrack = -1;
                        clearingTrack = -1;
                        consecutivePulls = 0;
                        visitedMoves.Clear();
                        LaunchSetup(session);
                        return;
                    }
                    End(session, action.Reason);
                    return;

                case SortActionKind.Pull:
                    ExecutePull(session, action, yardCarsById);
                    return;

                case SortActionKind.SetOut:
                    ExecuteSetOut(session, action, locoCarsById);
                    return;

                case SortActionKind.Consolidate:
                    ExecuteConsolidate(session, tracks.Find(t => t.Track.Track == action.Track));
                    return;
            }
        }

        private void ExecutePull(Session session, SortAction action, Dictionary<int, TrainCar> yardCarsById)
        {
            lastPulledTrack = action.Track;
            if (!action.ClearsTrack)
                clearingTrack = action.Track;
            else if (clearingTrack == action.Track)
                clearingTrack = -1;
            consecutivePulls++;
            var light = FarToNear(session).Count == 0;
            var leaveBehind = new HashSet<TrainCar>();
            foreach (var id in action.LeaveBehindCarIds)
                if (yardCarsById.TryGetValue(id, out var c))
                    leaveBehind.Add(c);

            for (var k = 0; k < action.PullCuts.Count; k++)
            {
                var cut = action.PullCuts[k];
                var last = k == action.PullCuts.Count - 1;
                var label = $"{Name(action.Track)} ({cut.CarsInnerToOuter.Count} car{(cut.CarsInnerToOuter.Count == 1 ? "" : "s")})";
                Func<int> cutAfter = last && leaveBehind.Count > 0 ? () => LeaveAtFarEnd(session, leaveBehind) : (Func<int>)null;
                var wp = CoupleTo(cut.OuterEnd, label, k == 0 && light ? Arrival.BackFirst : Arrival.CarsFirst, cutAfter);
                Queue(session.Engineer, wp);
            }
            Status = Capitalize(action.Reason);
            Main.Log($"sort: {session.Name}: {action.Reason} ({action.PullCuts.Count} cut{(action.PullCuts.Count == 1 ? "" : "s")}, leaving {leaveBehind.Count})");
        }

        private void ExecuteSetOut(Session session, SortAction action, Dictionary<int, TrainCar> locoCarsById)
        {
            consecutivePulls = 0;
            var far = FarToNear(session);
            // Keep lastPulledTrack until the whole hook is cleared, so sluffed cars are never dumped back onto the track we just pulled.
            if (far.Count <= action.SetOutCount)
                lastPulledTrack = -1;
            var cars = new List<TrainCar>();
            var group = new HashSet<TrainCar>();
            for (var i = 0; i < far.Count && cars.Count < action.SetOutCount; i++)
            {
                cars.Add(far[i]);
                group.Add(far[i]);
            }
            if (cars.Count == 0)
            {
                End(session, "no cars on the train to set out");
                return;
            }
            var track = action.Track;
            var workingEnd = assignment.WorkingEnds.TryGetValue(track, out var we) ? we : TrackEnd.Start;
            var name = Name(track);
            var keyText = SortAssignment.FormatKey(action.Key);
            var label = action.IsSluff ? $"{name} (stage {keyText})" : $"{name} ({keyText})";
            var done = action.IsSluff
                ? $"staged {cars.Count} car{(cars.Count == 1 ? "" : "s")} ({keyText}) on {name}"
                : $"{cars.Count} car{(cars.Count == 1 ? "" : "s")} ({keyText}) set out on {name}";

            if (!QueueSetOut(session, track, workingEnd, cars, group, action.JoinSorted, action.IsSluff, label, done))
            {
                End(session, $"{name} hasn't room for {cars.Count} car{(cars.Count == 1 ? "" : "s")} ({Length(cars):F0} m)");
                return;
            }
            Status = Capitalize(action.Reason);
            Main.Log($"sort: {session.Name}: {action.Reason} ({Length(cars):F0} m, join={action.JoinSorted}, sluff={action.IsSluff})");
        }

        private double PlannedSpotDepth(int track, double cutLength, bool isSluff)
        {
            if (isSluff)
                return cutLength + OrderGoals.ClearOfEntry + 12;
            var planned = cutLength;
            if (assignment != null)
            {
                foreach (var line in assignment.Lines)
                {
                    if (line.Track == track)
                    {
                        planned = Math.Max(planned, line.PlannedLength);
                        break;
                    }
                }
            }
            return Math.Max(cutLength + OrderGoals.ClearOfEntry + 15, planned + OrderGoals.ClearOfEntry + 25);
        }

        private bool QueueSetOut(Session session, int track, TrackEnd workingEnd, List<TrainCar> cars, HashSet<TrainCar> group,
            bool joinSorted, bool isSluff, string label, string done)
        {
            var length = Length(cars);
            var own = session.Loco.trainset.id;
            world.Occupancy.Standing(track, own, standing);

            if (joinSorted && standing.Count > 0)
            {
                var outerOwner = OuterMostOwner(track, workingEnd, standing);
                if (outerOwner.HasValue)
                {
                    var joinPlan = OrderGoals.ShoveIn(world.Graph, track, length, standing, join: outerOwner.Value, movable: Movable, entryOnly: workingEnd);
                    if (joinPlan != null)
                        return QueueShove(session, track, cars, label, done, joinPlan.Value, joining: true);
                }
            }

            // Empty track, sluff track, or fallback when join couldn't find the outer cut: spot strictly from workingEnd,
            // capping empty-track depth to what this track's planned block needs so 650+ m interchange tracks aren't
            // shoved end-to-end (leaving ample clearance before the far switch).
            var maxDepth = PlannedSpotDepth(track, length, isSluff);
            var spot = OrderGoals.SpotOn(world.Graph, track, length, standing, entryOnly: workingEnd, maxDepth: maxDepth);
            if (spot.Count > 0)
            {
                var wp = new Waypoint(spot, WaypointAction.Uncouple)
                {
                    CarCount = cars.Count,
                    CutCount = () => FarRun(session, group),
                    SetHandbrakes = true,
                    Arrival = Arrival.CarsFirst,
                    ArrivalRequired = true,
                    RejectEmptyGoals = true,
                    Label = label,
                    GoalsFor = train => SpotGoals(track, workingEnd, length, maxDepth, train.Owner),
                    DoneNote = done,
                };
                Queue(session.Engineer, wp);
                setOuts++;
                return true;
            }

            // No clear stretch by the working end -> shove standing cuts deeper to make room.
            var shove = OrderGoals.ShoveIn(world.Graph, track, length, standing, movable: Movable, entryOnly: workingEnd);
            if (shove != null)
                return QueueShove(session, track, cars, label, done, shove.Value, joining: false);
            return false;
        }

        private IEnumerable<WaypointGoal> SpotGoals(int track, TrackEnd workingEnd, double length, double maxDepth, int owner)
        {
            world.Occupancy.Standing(track, owner, standing);
            var goals = OrderGoals.SpotOn(world.Graph, track, length, standing, entryOnly: workingEnd, maxDepth: maxDepth);
            if (goals.Count > 0)
                return goals;
            // Never fall back to unrestricted OrderGoals.OnTrack: entering from the opposite ladder
            // would invert the track's inner-to-outer car order. Flag Tick to replan from live occupancy.
            spotInvalidated = true;
            return Array.Empty<WaypointGoal>();
        }

        private bool QueueShove(Session session, int track, List<TrainCar> cars, string label, string done, ShovePlan plan, bool joining)
        {
            var group = new HashSet<TrainCar>(cars);
            var theirs = 0;
            var couples = new List<Waypoint>();
            for (var k = 0; k < plan.Owners.Length; k++)
            {
                var set = FindSet(plan.Owners[k]);
                if (set == null || !Movable(plan.Owners[k]))
                    return false;
                foreach (var c in set.cars)
                    group.Add(c);
                theirs += set.cars.Count;
                couples.Add(CoupleTo(plan.CoupleTo[k], $"the {set.cars.Count} car{(set.cars.Count == 1 ? "" : "s")} on {Name(track)}", Arrival.CarsFirst, null));
            }
            foreach (var w in couples)
                Queue(session.Engineer, w);
            var still = plan.Shift < 1;
            var leave = new Waypoint(new[] { still ? new WaypointGoal(plan.ShoveTo) : new WaypointGoal(plan.ShoveTo, plan.Inward) }, WaypointAction.Uncouple)
            {
                Here = still,
                CarCount = cars.Count + theirs,
                CutCount = () => FarRun(session, group),
                SetHandbrakes = true,
                Arrival = Arrival.CarsFirst,
                ArrivalRequired = true,
                Label = label + (joining ? ", joined" : ", shoved in"),
                DoneNote = done,
            };
            Queue(session.Engineer, leave);
            setOuts++;
            return true;
        }

        /// <summary>Couples all separated sorted cuts on <paramref name="ts"/> into one solid cut.</summary>
        private void ExecuteConsolidate(Session session, YardTrackState ts)
        {
            lastPulledTrack = -1;
            consecutivePulls = 0;
            if (ts == null || ts.CutsInnerToOuter.Count < 2)
            {
                End(session, null);
                return;
            }
            var group = new HashSet<TrainCar>();
            var couples = new List<Waypoint>();
            // Couple from outermost cut inward through each cut until all are joined.
            for (var i = ts.CutsInnerToOuter.Count - 1; i >= 0; i--)
            {
                var cut = ts.CutsInnerToOuter[i];
                var set = FindSet(cut.Owner);
                if (set == null || !Movable(cut.Owner))
                {
                    End(session, $"couldn't consolidate cuts on {ts.Track.Name}");
                    return;
                }
                foreach (var c in set.cars)
                    group.Add(c);
                var first = i == ts.CutsInnerToOuter.Count - 1;
                couples.Add(CoupleTo(cut.OuterEnd, $"cut on {ts.Track.Name}", first ? Arrival.BackFirst : Arrival.CarsFirst, null));
            }
            foreach (var w in couples)
                Queue(session.Engineer, w);
            Queue(session.Engineer, new Waypoint(ts.CutsInnerToOuter[0].InnerEnd.Position, WaypointAction.Uncouple)
            {
                Here = true,
                CarCount = group.Count,
                CutCount = () => FarRun(session, group),
                SetHandbrakes = true,
                Label = $"{ts.Track.Name} (coupled together)",
                DoneNote = $"coupled the {group.Count} cars on {ts.Track.Name} together",
            });
            Status = $"Coupling cuts on {ts.Track.Name} together";
            Main.Log($"sort: {session.Name}: consolidating {ts.CutsInnerToOuter.Count} cuts ({group.Count} cars) on {ts.Track.Name}");
        }

        // ---- Reading live yard state ----

        /// <summary>Cheap initial per-track working ends (no GraphDistances): open end for stubs, Start for double-ended tracks.</summary>
        private static Dictionary<int, TrackEnd> InitialEnds(TrackGraph graph, SortableBowl bowl)
        {
            var ends = new Dictionary<int, TrackEnd>(bowl.Tracks.Count);
            foreach (var t in bowl.Tracks)
            {
                var hs = graph.Links(t.Track, TrackEnd.Start).Length > 0;
                var he = graph.Links(t.Track, TrackEnd.End).Length > 0;
                ends[t.Track] = !hs && he ? TrackEnd.End : TrackEnd.Start;
            }
            return ends;
        }

        private static Dictionary<int, TrackEnd> QuickWorkingEnds(TrackGraph graph, Func<int, int> lie, int[] trackIds, TrackPosition? locoPos)
        {
            var ends = new Dictionary<int, TrackEnd>();
            var refTrack = -1;
            for (var i = 0; i < trackIds.Length; i++)
            {
                var t = trackIds[i];
                if (graph.Links(t, TrackEnd.Start).Length > 0 || graph.Links(t, TrackEnd.End).Length > 0)
                {
                    refTrack = t;
                    break;
                }
            }
            if (refTrack < 0)
            {
                for (var i = 0; i < trackIds.Length; i++)
                    ends[trackIds[i]] = TrackEnd.Start;
                return ends;
            }
            TrackEnd refEnd;
            var hasS = graph.Links(refTrack, TrackEnd.Start).Length > 0;
            var hasE = graph.Links(refTrack, TrackEnd.End).Length > 0;
            if (hasS && !hasE)
                refEnd = TrackEnd.Start;
            else if (hasE && !hasS)
                refEnd = TrackEnd.End;
            else if (locoPos.HasValue)
            {
                var d = GraphDistances.From(graph, locoPos.Value);
                refEnd = d.To(new TrackPosition(refTrack, 0)) <= d.To(new TrackPosition(refTrack, graph.Length(refTrack)))
                    ? TrackEnd.Start : TrackEnd.End;
            }
            else
            {
                refEnd = TrackEnd.Start;
            }
            var exitDir = refEnd == TrackEnd.Start ? TrackDirection.Backward : TrackDirection.Forward;
            var probe = TrackWalker.Advance(graph, new TrackPosition(refTrack, graph.OffsetOf(refTrack, refEnd)),
                exitDir, 5, lie, out _);
            var fromProbe = GraphDistances.From(graph, probe);
            for (var i = 0; i < trackIds.Length; i++)
            {
                var t = trackIds[i];
                var hs = graph.Links(t, TrackEnd.Start).Length > 0;
                var he = graph.Links(t, TrackEnd.End).Length > 0;
                if (t == refTrack)
                    ends[t] = refEnd;
                else if (hs && !he)
                    ends[t] = TrackEnd.Start;
                else if (he && !hs)
                    ends[t] = TrackEnd.End;
                else if (hs && he)
                    ends[t] = fromProbe.To(new TrackPosition(t, 0)) <= fromProbe.To(new TrackPosition(t, graph.Length(t)))
                        ? TrackEnd.Start : TrackEnd.End;
                else
                    ends[t] = TrackEnd.Start;
            }
            return ends;
        }

        private static List<YardTrackState> ReadYardTracks(World world, SortableBowl bowl, SortMode mode,
            Dictionary<int, TrackEnd> workingEnds, int ownId, SortAssignment activePlan, out Dictionary<int, TrainCar> carsById)
        {
            carsById = new Dictionary<int, TrainCar>();
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var result = new List<YardTrackState>(bowl.Tracks.Count);
            var spans = new List<OccupiedSpan>();
            var graph = world.Graph;

            // Count which bowl tracks each trainset touches, to detect any cut straddling two bowl tracks.
            var ownerBowlTracks = new Dictionary<int, HashSet<int>>();
            foreach (var nt in bowl.Tracks)
            {
                world.Occupancy.Standing(nt.Track, ownId, spans);
                foreach (var s in spans)
                {
                    if (!ownerBowlTracks.TryGetValue(s.Owner, out var set))
                        ownerBowlTracks[s.Owner] = set = new HashSet<int>();
                    set.Add(nt.Track);
                }
            }

            foreach (var nt in bowl.Tracks)
            {
                var hasWorkingEnd = workingEnds.TryGetValue(nt.Track, out var we);
                if (!hasWorkingEnd)
                    we = TrackEnd.Start;
                var st = new SortTrack
                {
                    Track = nt.Track,
                    Name = nt.Id,
                    Length = nt.Length,
                    WorkingEnd = we,
                    DeadEnd = graph.Links(nt.Track, we.Other()).Length == 0,
                };
                var yts = new YardTrackState { Track = st };
                if (activePlan != null && (!hasWorkingEnd || !activePlan.TrackKeys.ContainsKey(nt.Track)))
                {
                    st.Blocked = true;
                    st.BlockedReason = "excluded from the current sort plan";
                    result.Add(yts);
                    continue;
                }
                if (graph.Links(nt.Track, we).Length == 0)
                {
                    st.Blocked = true;
                    st.BlockedReason = "no switch into the working ladder";
                    result.Add(yts);
                    continue;
                }
                world.Occupancy.Standing(nt.Track, ownId, spans);
                var owners = new HashSet<int>();
                foreach (var s in spans)
                    owners.Add(s.Owner);

                foreach (var owner in owners)
                {
                    var set = FindSet(owner);
                    if (set == null || set.cars.Count == 0)
                        continue;
                    if (set.locoIndices.Count > 0)
                    {
                        st.Blocked = true;
                        st.BlockedReason = $"loco {set.cars[set.locoIndices[0]].ID} standing on it";
                        break;
                    }
                    if (set.cars.Exists(c => c == null || c.derailed))
                    {
                        st.Blocked = true;
                        st.BlockedReason = "derailed car on it";
                        break;
                    }
                    if (ownerBowlTracks.TryGetValue(owner, out var touched) && touched.Count > 1)
                    {
                        st.Blocked = true;
                        st.BlockedReason = "cars straddle a switch";
                        break;
                    }
                    if (!world.TryPlace(set.GetEndmost(true), out var pa, out var oa)
                        || !world.TryPlace(set.GetEndmost(false), out var pb, out var ob))
                    {
                        st.Blocked = true;
                        st.BlockedReason = "cut couldn't be placed on the network";
                        break;
                    }
                    var cut = BuildSortCut(world, nt, we, owner, set, pa, oa, pb, ob, mode, jobs, carsById, out var cutError);
                    if (cut == null)
                    {
                        st.Blocked = true;
                        st.BlockedReason = cutError;
                        break;
                    }
                    yts.CutsInnerToOuter.Add(cut);
                }
                if (st.Blocked)
                    yts.CutsInnerToOuter.Clear();
                else
                    // Deepest (largest distance from working end) first, outermost (nearest ladder) last.
                    yts.CutsInnerToOuter.Sort((a, b) => b.FarFromWorking.CompareTo(a.FarFromWorking));
                result.Add(yts);
            }
            return result;
        }

        private static SortCut BuildSortCut(World world, NamedTrack nt, TrackEnd workingEnd, int owner, Trainset set,
            TrackPosition pa, TrackDirection oa, TrackPosition pb, TrackDirection ob,
            SortMode mode, JobsManager jobs, Dictionary<int, TrainCar> carsById, out string error)
        {
            error = null;
            if (!YardSort.LocateCutOnTrack(world.Graph, nt.Track, workingEnd, pa, pb, world.SelectedBranchFunc,
                out var aIsOuter, out var near, out var far))
            {
                error = "cars extend past the switch";
                return null;
            }
            if (near < -2.0 || far > nt.Length + 2.0)
            {
                error = "cars extend past the switch";
                return null;
            }
            var cut = new SortCut
            {
                Owner = owner,
                Track = nt.Track,
                OuterEnd = aIsOuter ? new CutEnd(pa, oa) : new CutEnd(pb, ob),
                InnerEnd = aIsOuter ? new CutEnd(pb, ob) : new CutEnd(pa, oa),
                NearFromWorking = near,
                FarFromWorking = far,
            };
            // cars[0] is end A; cars[n-1] is end B. Order inner-to-outer:
            if (aIsOuter)
            {
                for (var i = set.cars.Count - 1; i >= 0; i--)
                    AddCar(set.cars[i], cut.CarsInnerToOuter, mode, jobs, carsById);
            }
            else
            {
                for (var i = 0; i < set.cars.Count; i++)
                    AddCar(set.cars[i], cut.CarsInnerToOuter, mode, jobs, carsById);
            }
            return cut;
        }

        private static List<SortCar> ReadLocoCars(TrainCar loco, SortMode mode, out Dictionary<int, TrainCar> carsById)
        {
            carsById = new Dictionary<int, TrainCar>();
            var list = new List<SortCar>();
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            foreach (var car in FarToNearCars(loco))
                AddCar(car, list, mode, jobs, carsById);
            return list;
        }

        private static void AddCar(TrainCar car, List<SortCar> into, SortMode mode, JobsManager jobs, Dictionary<int, TrainCar> carsById)
        {
            if (car == null)
                return;
            var id = car.GetInstanceID();
            carsById[id] = car;
            var key = KeyOf(car, mode, jobs);
            into.Add(new SortCar
            {
                Id = id,
                Key = key,
                Length = Math.Max(4, car.InterCouplerDistance),
            });
        }

        /// <summary>
        /// Classification key for a car: destination yard code (or job ID in <see cref="SortMode.ByJob"/>),
        /// or "" when the car has no active or available job (e.g. outbound/empty at an interchange).
        /// </summary>
        public static string KeyOf(TrainCar car, SortMode mode, JobsManager jobs)
        {
            if (car?.logicCar == null || jobs == null)
                return "";
            Job job = null;
            try
            {
                job = jobs.GetJobOfCar(car.logicCar);
            }
            catch (Exception)
            {
                return "";
            }
            if (job == null || (job.State != JobState.Available && job.State != JobState.InProgress))
                return "";
            if (mode == SortMode.ByJob)
                return job.ID ?? "";
            var dest = job.chainData?.chainDestinationYardId;
            if (!string.IsNullOrEmpty(dest))
                return dest;
            // Fallback: inspect task destination track if chainDestinationYardId is absent.
            var trackYard = FirstTaskDestinationYard(job);
            return !string.IsNullOrEmpty(trackYard) ? trackYard : job.ID ?? "";
        }

        private static string FirstTaskDestinationYard(Job job)
        {
            if (job?.tasks == null)
                return null;
            var stack = new Stack<Task>(job.tasks);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t == null)
                    continue;
                var data = t.GetTaskData();
                if (data.destinationTrack?.ID != null && !string.IsNullOrEmpty(data.destinationTrack.ID.yardId))
                    return data.destinationTrack.ID.yardId;
                if (data.nestedTasks != null)
                    foreach (var n in data.nestedTasks)
                        stack.Push(n);
            }
            return null;
        }

        private static Dictionary<string, int> FilterRepTracks(Dictionary<string, int> all, HashSet<string> keys)
        {
            var filtered = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in keys)
                if (!string.IsNullOrEmpty(k) && all.TryGetValue(k, out var t))
                    filtered[k] = t;
            return filtered;
        }

        private int? OuterMostOwner(int track, TrackEnd workingEnd, List<OccupiedSpan> spans)
        {
            var length = world.Graph.Length(track);
            int? best = null;
            var bestNear = double.PositiveInfinity;
            foreach (var s in spans)
            {
                var near = workingEnd == TrackEnd.Start ? s.From : length - s.To;
                if (near < bestNear)
                {
                    bestNear = near;
                    best = s.Owner;
                }
            }
            return best;
        }

        // ---- Order plumbing & failure handling ----

        private Waypoint CoupleTo(CutEnd end, string label, Arrival arrival, Func<int> cutAfter)
        {
            var graph = world.Graph;
            var branch = world.SelectedBranchFunc;
            return new Waypoint(OrderGoals.ToCouple(graph, new[] { end }, branch), WaypointAction.Couple)
            {
                Label = label,
                Arrival = arrival,
                // Extending an already-loaded hook, shoving in, or consolidating MUST arrive cars-first;
                // loco-first would couple to the wrong side of the switcher and invert the consist.
                ArrivalRequired = arrival == Arrival.CarsFirst,
                CutAfterCoupling = cutAfter,
                GoalsFor = train => OrderGoals.CoupleOnward(graph, train, end, branch),
            };
        }

        private void Queue(Engineer engineer, Waypoint waypoint)
        {
            mine.Add(waypoint);
            issued.Add(waypoint);
            var queue = engineer.Queue;
            for (var i = 0; i < queue.Count; i++)
            {
                if (later.Contains(queue[i]))
                {
                    engineer.InsertAt(i, waypoint);
                    return;
                }
            }
            engineer.Enqueue(waypoint);
        }

        private bool ReadOutcomes(Session session)
        {
            var engineer = session.Engineer;
            while (issued.Count > 0)
            {
                var order = issued[0];
                var queued = false;
                for (var i = 0; i < engineer.Queue.Count && !queued; i++)
                    queued = ReferenceEquals(engineer.Queue[i], order);
                if (queued)
                    return true;
                issued.RemoveAt(0);
                if (order.Outcome == OrderOutcome.NoCars || order.Outcome == OrderOutcome.NotCoupled)
                {
                    var label = order.Label ?? "a cut";
                    couplingsFailed.TryGetValue(label, out var times);
                    couplingsFailed[label] = ++times;
                    RemoveMine(engineer);
                    var what = order.Outcome == OrderOutcome.NoCars ? "found no cars at" : "couldn't couple to";
                    Main.Log($"sort: {session.Name}: {what} {label} (time {times})");
                    if (times >= 2)
                    {
                        End(session, $"it {what} {label}, twice");
                        return false;
                    }
                }
                else if (order.Outcome == OrderOutcome.NothingCut)
                {
                    RemoveMine(engineer);
                    End(session, $"none of the cars for {order.Label ?? "a set-out"} were at the far end to cut off");
                    return false;
                }
            }
            return true;
        }

        private void WatchForNoRoute(Session session, Waypoint head)
        {
            var status = session.Engineer.Status ?? "";
            if (!status.StartsWith("No route", StringComparison.Ordinal) && !status.StartsWith("The waypoint is under", StringComparison.Ordinal))
            {
                stuckOn = null;
                return;
            }
            if (!ReferenceEquals(stuckOn, head))
            {
                stuckOn = head;
                stuckSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - stuckSince < NoRouteSeconds)
                return;
            stuckOn = null;
            var engineer = session.Engineer;
            if (clearMoves.Contains(head))
            {
                engineer.RemoveAt(0);
                return;
            }
            if (clearTried.Add(head) && ClearOfSwitches(session))
                return;
            RemoveMine(engineer);
            End(session, status.Contains("foul")
                ? $"no route to {head.Label}: cars foul a switch on the way"
                : $"no route to {head.Label}");
        }

        private bool ClearOfSwitches(Session session)
        {
            if (!session.HasState)
                return false;
            var state = session.Read();
            var goals = new List<WaypointGoal>();
            foreach (var (at, outward) in new[] { (state.EndA, state.OutwardA), (state.EndB, state.OutwardB) })
            {
                var goal = TrackWalker.Advance(world.Graph, at, outward, state.Length + 20, world.SelectedBranchFunc, out var dir);
                goals.Add(new WaypointGoal(goal, dir));
            }
            var step = new Waypoint(goals) { Label = "clear of the switches", Arrival = Arrival.Any };
            mine.Add(step);
            clearMoves.Add(step);
            session.Engineer.PutFirst(step);
            Main.Log($"sort: {session.Name}: running clear of the switches first");
            return true;
        }

        private void RemoveMine(Engineer engineer)
        {
            for (var i = engineer.Queue.Count - 1; i >= 0; i--)
                if (mine.Contains(engineer.Queue[i]))
                    engineer.RemoveAt(i);
            issued.Clear();
        }

        public void Cancel(Session session, string why)
        {
            pending = null;
            ReleaseKept(session);
            RemoveMine(session.Engineer);
            Main.Notify($"AI engineer: {session.Name} stopped sorting {bowl.Name}: {why}.");
            Main.Log($"sort: {session.Name}: stopped sorting {bowl.Name}: {why}");
        }

        private void End(Session session, string why)
        {
            pending = null;
            ReleaseKept(session);
            if (why != null)
            {
                Main.Notify($"AI engineer: {session.Name} stopped sorting {bowl.Name}: {why}.");
                Main.Log($"sort: {session.Name}: stopped sorting {bowl.Name}: {why} ({setOuts} set-outs, {moves} cycles)");
                Status = "Stopped";
            }
            else
            {
                var summary = FinalTrackSummary(session);
                Main.Notify($"AI engineer: {session.Name} finished sorting {bowl.Name}: {summary}.");
                Main.Log($"sort: {session.Name}: finished sorting {bowl.Name} ({setOuts} set-outs, {moves} cycles): {summary}");
                Status = "Done";
            }
            session.Sort = null;
        }

        private string FinalTrackSummary(Session session)
        {
            if (assignment == null)
                return "done";
            world.ScanCars();
            var ownId = session.Loco.trainset != null ? session.Loco.trainset.id : int.MinValue;
            var tracks = ReadYardTracks(world, bowl, mode, assignment.WorkingEnds, ownId, assignment, out _);
            var parts = new List<string>();
            foreach (var ts in tracks)
            {
                if (ts.Track.Blocked || ts.TotalCars == 0)
                    continue;
                var keys = ts.AllCarsInnerToOuter().Select(c => SortAssignment.FormatKey(c.Key)).Distinct();
                parts.Add($"{ts.Track.Name} {string.Join("+", keys)} ({ts.TotalCars})");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "yard empty";
        }

        // ---- Consist helpers ----

        /// <summary>Total physical length (m) of the loco plus any coupled locos/tenders/cabooses riding with it.</summary>
        private static double PowerLength(Session session)
        {
            var set = session.Loco?.trainset;
            if (set == null || set.cars.Count == 0)
                return session.HasState ? session.State.Length : (session.Loco != null ? session.Loco.InterCouplerDistance : 16);
            var total = 0.0;
            foreach (var car in set.cars)
                if (car != null)
                    total += car.InterCouplerDistance;
            var cargo = Length(FarToNear(session));
            return Math.Max(session.Loco.InterCouplerDistance, total - cargo);
        }

        private static int LeaveAtFarEnd(Session session, HashSet<TrainCar> leaveBehind)
        {
            var far = FarToNear(session);
            var n = 0;
            foreach (var car in far)
            {
                if (!leaveBehind.Contains(car))
                    break;
                n++;
            }
            // Never cut off the entire train if something went unexpectedly.
            return n >= far.Count ? 0 : n;
        }

        private static int FarRun(Session session, HashSet<TrainCar> group)
        {
            var n = 0;
            foreach (var car in FarToNear(session))
            {
                if (!group.Contains(car))
                    break;
                n++;
            }
            return n;
        }

        private static List<TrainCar> FarToNear(Session session) => FarToNearCars(session.Loco);

        private static List<TrainCar> FarToNearCars(TrainCar loco)
        {
            var set = loco?.trainset;
            var list = new List<TrainCar>();
            if (set == null)
                return list;
            var cars = set.cars;
            var locoIndex = cars.IndexOf(loco);
            if (locoIndex < 0)
                return list;
            var fromA = locoIndex >= cars.Count - 1 - locoIndex;
            if (fromA)
            {
                for (var k = 0; k < locoIndex; k++)
                    list.Add(cars[k]);
            }
            else
            {
                for (var k = cars.Count - 1; k > locoIndex; k--)
                    list.Add(cars[k]);
            }
            var riding = TrainMakeup.RidingWithLoco(list, Session.RidesWithLoco);
            if (riding > 0)
                list.RemoveRange(list.Count - riding, riding);
            return list;
        }

        private static Coupler FarCoupler(Session session)
        {
            var set = session.Loco.trainset;
            var locoIndex = set.cars.IndexOf(session.Loco);
            return set.GetEndmost(locoIndex >= set.cars.Count - 1 - locoIndex);
        }

        private static Trainset FindSet(int owner) => Trainset.allSets.Find(t => t != null && t.id == owner);

        private static bool Movable(int owner)
        {
            var set = FindSet(owner);
            return set != null && set.locoIndices.Count == 0 && !set.cars.Exists(c => c == null || c.derailed);
        }

        private static bool TrackOf(TrainCar car, out int track)
        {
            track = -1;
            var on = car.logicCar?.CurrentTrack;
            if (on == null || on.ID == null || on.ID.IsGeneric())
                return false;
            var rail = on.RailTrack();
            return rail != null && Host.World != null && Host.World.Network.TryGetTrack(rail, out track);
        }

        private static double Length(List<TrainCar> cars) => cars.Sum(c => (double)c.InterCouplerDistance);

        private string Name(int track) => trackNames.TryGetValue(track, out var n) ? n : world.Network.Describe(track);

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
