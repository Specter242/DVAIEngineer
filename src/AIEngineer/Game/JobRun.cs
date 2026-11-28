using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>
    /// A group of jobs handed to one loco's engineer, worked through to the
    /// end, step by step: a job's step is the moves the game asks of it now
    /// (a shunting load's first step gathers its cars from several tracks).
    /// It first drops the cars the loco was coupled to that aren't the jobs'
    /// (where they stand), then pulls each cut a step needs (the planner's
    /// order, coupling on through the cuts in their way), then sets the cars
    /// out one track at a time, each set-out read from the train as it
    /// really is: the run of cars bound for one track at its far end, joined
    /// to the job's cars already there, or spotted in the room left, or
    /// shoved in with the cars by the entry; and the cars that came along
    /// only because they stood in the way go back where they stood. A step
    /// done, it plans the next: after the warehouse machine's (the
    /// player's), once the cars are loaded or unloaded. The orders are the
    /// engineer's ordinary queue: another order put first ends the run.
    /// A plan is a dozen searches of the whole network and more (26 to
    /// 166 ms in the recorded runs), so it is worked out on another thread
    /// from a frozen copy of the world while the train stands, and its
    /// orders queued on a later tick (<see cref="PlanStep"/>).
    /// </summary>
    internal sealed class JobRun
    {
        private enum Phase
        {
            /// <summary>Queued behind other orders: begins when they're done.</summary>
            Starting,

            /// <summary>Dropping the cars the loco had that aren't the jobs'.</summary>
            Clearing,

            /// <summary>Pulling cuts and setting cars out.</summary>
            Working,

            /// <summary>Between steps: for the warehouse machine, or the game to count a step done.</summary>
            Waiting,
        }

        /// <summary>How many moves a run makes at most before it gives up.</summary>
        private const int MoveLimit = 250;

        /// <summary>How many plans a run makes at most: one a trip, each step of a job planned again after each trip.</summary>
        private const int PlanLimit = 40;

        /// <summary>How long it waits for the game to count a finished step done, s.</summary>
        private const float SettleSeconds = 15;

        /// <summary>How long an order of the run may find no route before the run works round it, s.</summary>
        private const float NoRouteSeconds = 45;

        /// <summary>How much farther a set-out may run to join its job's cars rather than stand alone, m (a last move always joins).</summary>
        private const double JoinPremium = 1000;

        /// <summary>
        /// How much farther a set-out may run to a free stretch of its track
        /// rather than couple to the cars standing there and shove them in, m:
        /// a free stretch disturbs no one, but not at any price (a player's
        /// report at City South, 2026-11-18: a train went out onto the main
        /// line to turn, 8.6 to 12.2 km, where shoving in ran 176 to 698 m).
        /// </summary>
        private const double SpotPremium = 1000;

        /// <summary>How far riders may run to go back where they stood before a nearer track is looked for, m.</summary>
        private const double HomeDetour = 2500;

        /// <summary>How much nearer that track must be, m.</summary>
        private const double AsideSaving = 1500;

        private readonly World world;
        private readonly List<Job> handed;
        private readonly Arrival facing;
        private List<JobView> jobs = new List<JobView>();
        private readonly Dictionary<TrainCar, int> destination = new Dictionary<TrainCar, int>();
        private readonly Dictionary<TrainCar, string> jobOf = new Dictionary<TrainCar, string>();
        private readonly Dictionary<int, string> trackNames = new Dictionary<int, string>();
        private readonly HashSet<Waypoint> mine = new HashSet<Waypoint>();
        // Orders already queued when the run was added behind them: it waits its turn.
        private readonly HashSet<Waypoint> before = new HashSet<Waypoint>();
        private readonly List<OccupiedSpan> standing = new List<OccupiedSpan>();
        // Riders: cars that came along only because they stood in the way, by the network
        // track they're to be put back on.
        private readonly Dictionary<TrainCar, int> home = new Dictionary<TrainCar, int>();
        private readonly HashSet<string> told = new HashSet<string>();
        // Cars whose move is their job's last: set out joined to the job's cars already there.
        private readonly HashSet<TrainCar> lastMove = new HashSet<TrainCar>();
        // Riders set aside on another track because going back to theirs was a long way round.
        private readonly HashSet<TrainCar> asideInstead = new HashSet<TrainCar>();
        // The first coupling of each pull, by the cut it goes to (trainset, end): a pull that
        // can't be reached is given up, and that end barred while the train is no shorter.
        private readonly Dictionary<Waypoint, (int Set, int End)> pullStarts = new Dictionary<Waypoint, (int Set, int End)>();
        private readonly HashSet<Waypoint> pullOrders = new HashSet<Waypoint>();
        private readonly Dictionary<(int Set, int End), double> unreachable = new Dictionary<(int Set, int End), double>();
        private Waypoint stuckOn;
        private float stuckSince;
        // Orders a move clear of the switches was tried for, and those moves themselves.
        private readonly HashSet<Waypoint> clearTried = new HashSet<Waypoint>();
        private readonly HashSet<Waypoint> clearMoves = new HashSet<Waypoint>();
        // The run's orders whose ending it hasn't looked at yet, in the order given.
        private readonly List<Waypoint> issued = new List<Waypoint>();
        // Orders the player queued behind the run's own while it worked: they wait for it.
        private readonly HashSet<Waypoint> later = new HashSet<Waypoint>();
        private readonly Dictionary<string, int> couplingsFailed = new Dictionary<string, int>();
        // Jobs added to the queue while this run worked: the next run, begun when this one ends.
        private readonly List<Job> then = new List<Job>();
        // The plan being worked out on another thread, with what it was asked from; null when none is.
        private Asked pending;
        private Phase phase;
        private int setOuts;
        private int steps;
        private int plans;
        private float waitingSince;
        private float nextLook;

        private JobRun(World world, List<Job> handed, Arrival facing)
        {
            this.world = world;
            this.handed = handed;
            this.facing = facing;
        }

        /// <summary>The jobs' IDs, for the window.</summary>
        public string Ids => string.Join(", ", handed.Select(j => j.ID));

        /// <summary>The IDs of the jobs to work after these, or null when there are none.</summary>
        public string ThenIds => then.Count > 0 ? string.Join(", ", then.Select(j => j.ID)) : null;

        /// <summary>
        /// Queues jobs behind this run's: worked as a run of their own once
        /// this one ends (handing them to a new run in its place would give
        /// up this one's). Returns how many were added; jobs already in hand
        /// or queued are left out.
        /// </summary>
        public int Then(List<JobView> views)
        {
            var n = 0;
            foreach (var v in views)
            {
                if (handed.Contains(v.Job) || then.Contains(v.Job))
                    continue;
                then.Add(v.Job);
                n++;
            }
            if (n > 0)
                Main.Log($"jobs: queued {string.Join(", ", views.Select(v => v.Id))} after {Ids}");
            return n;
        }

        /// <summary>Whether the run stands waiting for the player to work a warehouse machine: nothing for it to do meanwhile.</summary>
        public bool WaitingOnPlayer => phase == Phase.Waiting && pending == null;

        /// <summary>Whether its next moves are being planned on another thread: the train stands until they are queued.</summary>
        public bool Planning => pending != null;

        /// <summary>Whether the run put this order in the queue.</summary>
        public bool Owns(Waypoint order) => mine.Contains(order);

        /// <summary>Whether the run is working this job.</summary>
        public bool Has(string jobId) => handed.Exists(j => j.ID == jobId);

        /// <summary>What the run is doing, for the window.</summary>
        public string Status { get; private set; } = "";

        /// <summary>Cars that came along only because they stood in the way, set aside elsewhere because going back was a long way round.</summary>
        public IReadOnlyCollection<TrainCar> SetAsideInstead => asideInstead;

        /// <summary>
        /// Hands the jobs to the session's loco: replacing its queue or,
        /// with <paramref name="queue"/>, after it. <paramref name="facing"/>
        /// is how a light engine meets its first cut. Returns the run, or
        /// null when none of the jobs can be worked; says what was left out
        /// in <paramref name="problems"/>.
        /// </summary>
        public static JobRun Start(Session session, World world, List<JobView> views, bool queue, Arrival facing, out List<string> problems)
        {
            problems = new List<string>();
            world.ScanCars();
            var usable = new List<Job>();
            foreach (var job in views)
            {
                // A job whose cars stand at the warehouse machine is taken on: the run waits
                // there for the player to work it, and carries the cars on after.
                if (job.Blocked != null && job.Warehouse == null)
                {
                    problems.Add($"{job.Id}: {job.Blocked}");
                    continue;
                }
                var why = Unworkable(session, job);
                if (why != null)
                {
                    problems.Add($"{job.Id}: {why}");
                    continue;
                }
                usable.Add(job.Job);
            }
            if (usable.Count == 0)
                return null;
            if (queue && session.Sort != null)
            {
                problems.Add($"can't queue jobs behind sorting {session.Sort.YardName}; use Do it now to stop sorting and work these jobs");
                return null;
            }
            if (session.Sort != null)
            {
                session.Sort.Cancel(session, "it was handed jobs");
                session.Sort = null;
            }
            var run = new JobRun(world, usable, facing);
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
            session.Jobs = run;
            Main.Log($"jobs: {session.Name}: handed {run.Ids}" + (problems.Count > 0 ? "; left out: " + string.Join("; ", problems) : ""));
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

        /// <summary>Why a job's cars can't be worked: in a train with another loco, or derailed; null if they can.</summary>
        private static string Unworkable(Session session, JobView job)
        {
            var own = session.Loco.trainset;
            foreach (var leg in job.Legs)
            {
                foreach (var car in leg.Cars)
                {
                    if (car.trainset != own && car.trainset != null && car.trainset.locoIndices.Count > 0)
                        return $"its cars are in a train with a loco ({car.trainset.cars[car.trainset.locoIndices[0]].ID})";
                    if (car.derailed)
                        return $"{car.ID} is derailed";
                }
            }
            return null;
        }

        /// <summary>
        /// The run's first move: the cars the loco is coupled to that aren't
        /// the jobs' (all of them, or those beyond the jobs' cars) come off
        /// first, where they stand. Then the first step is planned.
        /// </summary>
        private void Begin(Session session)
        {
            ReadTargets(session);
            if (destination.Count == 0)
            {
                // Nothing to fetch yet (the jobs' cars wait at a warehouse machine, maybe coupled
                // to this loco): nothing is uncoupled or set aside for it.
                PlanStep(session);
                return;
            }
            // Cars on the loco's other side too (it never cuts from there): off first, where they
            // stand, once the train stands; on the main line, or with another loco among them, kept.
            var other = session.OtherSide();
            if (other.Count > 0)
            {
                if (session.HasState && Math.Abs(session.State.SpeedTowardA) > 0.1)
                {
                    phase = Phase.Starting;
                    Status = "Stopping to uncouple the cars on the loco's other side";
                    return;
                }
                var onYard = other.TrueForAll(c => TrackOf(c, out _));
                // Another loco, or a caboose, among them: the train's, kept with it.
                var noLoco = other.TrueForAll(c => !CarTypes.IsAnyLocomotiveOrTender(c.carLivery) && !c.IsCaboose);
                if (onYard && noLoco)
                {
                    var n = session.CutOtherSide(setHandbrakes: true);
                    Main.Log($"jobs: {session.Name}: uncoupled the {n} car{(n == 1 ? "" : "s")} on the loco's other side first, where they stand");
                    session.Engineer.Note = $"uncoupled the {n} car{(n == 1 ? "" : "s")} on its other side first";
                    world.ScanCars();
                }
                else
                {
                    Main.Log($"jobs: {session.Name}: kept the {other.Count} car{(other.Count == 1 ? "" : "s")} on the loco's other side ({(onYard ? "another loco or a caboose among them" : "not on a yard track")})");
                }
            }
            var far = FarToNear(session);
            if (far.Count > 0)
            {
                var firstJob = far.FindIndex(c => destination.ContainsKey(c));
                var off = firstJob < 0 ? far : far.Take(firstJob).ToList();
                // Cars between the jobs' and the loco ride along, and go back where they stand now.
                if (firstJob >= 0)
                {
                    foreach (var car in far.Skip(firstJob))
                    {
                        if (!destination.ContainsKey(car) && TrackOf(car, out var t))
                            home[car] = t;
                    }
                }
                if (off.Count > 0 && SetAside(session, off, first: true))
                {
                    phase = Phase.Clearing;
                    return;
                }
            }
            PlanStep(session);
        }

        /// <summary>
        /// Each tick after the engineer's: when its queue runs out, the next
        /// move. Another order at the head of the queue, or the engineer off,
        /// ends it.
        /// </summary>
        public void Tick(Session session)
        {
            var engineer = session.Engineer;
            if (engineer.Mode != EngineerMode.Waypoints)
            {
                Cancel(session, "the engineer was given another mode");
                session.Jobs = null;
                return;
            }
            if (session.Jobs != this)
                return;
            if (!ReadOutcomes(session))
                return;
            if (engineer.Queue.Count > 0)
            {
                var head = engineer.Queue[0];
                if (before.Contains(head))
                    return;
                if (mine.Contains(head))
                {
                    // Orders the player has added behind the run's own wait their turn after it.
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
                    // Put ahead of the run, or in place of its orders: the player wants this first.
                    Cancel(session, "other orders came first");
                    session.Jobs = null;
                    return;
                }
                // An order that was queued behind the run has come to the head only because the
                // run is between moves: its next goes in ahead of it (Queue).
            }
            stuckOn = null;
            if (pending != null)
            {
                // Asked for on an earlier tick: its orders are queued once it is worked out.
                if (pending.Task.IsCompleted)
                    Planned(session);
                return;
            }
            switch (phase)
            {
                case Phase.Starting:
                    Begin(session);
                    return;
                case Phase.Clearing:
                    PlanStep(session);
                    return;
                case Phase.Waiting:
                    Wait(session);
                    return;
            }
            if (++steps > MoveLimit)
            {
                End(session, $"it gave up after {MoveLimit} moves");
                return;
            }
            NextSetOut(session);
        }

        /// <summary>The next set-out from the far end of the train; or, with none left, on to the next step.</summary>
        private void NextSetOut(Session session)
        {
            var far = FarToNear(session);
            world.ScanCars();
            if (far.Count == 0)
            {
                StepDone(session);
                return;
            }
            var first = far[0];
            if (destination.TryGetValue(first, out var track))
            {
                // The run of cars at the far end bound for one track: set out there.
                var group = far.TakeWhile(c => destination.TryGetValue(c, out var d) && d == track).ToList();
                var ids = string.Join(", ", group.Select(c => jobOf[c]).Distinct());
                var name = Name(track);
                var count = group.Count;
                if (!SetOut(session, track, group, name, $"{ids}: {count} car{(count == 1 ? "" : "s")} set out on {name}", join: true))
                {
                    Main.Notify($"AI engineer: {session.Name}: {name} hasn't room for {ids}'s {count} car{(count == 1 ? "" : "s")} ({Length(group):F0} m), even shoving the cars there further in. It keeps them; make room and hand the job over again.");
                    End(session, null);
                    return;
                }
                Status = $"Setting out {count} car{(count == 1 ? "" : "s")} for {ids} on {name}";
                return;
            }
            // Cars at the far end that aren't the jobs': riders go back where they stood; the
            // train's own cars are set aside if job cars, or riders, wait behind them.
            var homeTrack = home.TryGetValue(first, out var h) ? h : -1;
            var strangers = far.TakeWhile(c => !destination.ContainsKey(c) && (home.TryGetValue(c, out var hh) ? hh : -1) == homeTrack).ToList();
            var workBehind = far.Skip(strangers.Count).Any(c => destination.ContainsKey(c) || home.ContainsKey(c));
            var cars = $"{strangers.Count} car{(strangers.Count == 1 ? "" : "s")}";
            if (homeTrack >= 0)
            {
                var name = Name(homeTrack);
                var nearer = NearerThanHome(session, homeTrack, strangers, out var homeCost, out var asideCost);
                if (nearer >= 0)
                {
                    var there = Name(nearer);
                    if (SetOut(session, nearer, strangers, $"{there} (set aside)", $"set {cars} aside on {there}: back on {name} was a long way round", join: false))
                    {
                        Main.Log($"jobs: {session.Name}: {cars} from {name} set aside on {there} instead: {homeCost:F0} m to go back, {asideCost:F0} m there");
                        foreach (var c in strangers)
                        {
                            home.Remove(c);
                            asideInstead.Add(c);
                        }
                        Status = $"Setting {cars} aside on {there}";
                        return;
                    }
                }
                if (SetOut(session, homeTrack, strangers, $"{name} (put back)", $"{cars} put back on {name}", join: false))
                {
                    foreach (var c in strangers)
                        home.Remove(c);
                    Status = $"Putting {cars} back on {name}";
                    return;
                }
            }
            else if (!workBehind)
            {
                StepDone(session);
                return;
            }
            if (SetAside(session, strangers, first: false))
            {
                foreach (var c in strangers)
                    home.Remove(c);
                return;
            }
            End(session, $"no track near has room to set aside {string.Join(", ", strangers.Select(c => c.ID))}, in the way of the rest");
        }

        /// <summary>
        /// An order of the run that has found no route for a while: a pull's
        /// coupling gives up the rest of its trip (the cars it has are set out
        /// and the next trip planned afresh, that end barred while the train is
        /// no shorter); a set-out ends the run, saying so.
        /// </summary>
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
            var why = WhyNoRoute(session, head);
            Main.Log($"jobs: {session.Name}: no route to {head.Label}: {why}");
            // A move clear of the switches that found no way either: drop it, and the order it was for
            // gives up as below next time.
            if (clearMoves.Contains(head))
            {
                engineer.RemoveAt(0);
                return;
            }
            // Standing across a switch (after a set-out, say), the train can't have it thrown under
            // itself: once an order, run straight on clear of the switches first, then plan again.
            if (clearTried.Add(head) && ClearOfSwitches(session))
                return;
            if (pullOrders.Contains(head))
            {
                if (pullStarts.TryGetValue(head, out var target))
                    unreachable[target] = session.HasState ? session.State.Length : 0;
                var dropped = 0;
                for (var i = engineer.Queue.Count - 1; i >= 0; i--)
                {
                    if (pullOrders.Contains(engineer.Queue[i]))
                    {
                        engineer.RemoveAt(i);
                        dropped++;
                    }
                }
                Main.Log($"jobs: {session.Name}: no route to {head.Label} for {NoRouteSeconds:F0} s; the rest of this trip's pulls ({dropped} couplings) dropped, its cars set out first");
                engineer.Note = $"no route to {head.Label}; setting out what it has, then planning again";
                return;
            }
            End(session, status.EndsWith("cars first", StringComparison.Ordinal)
                ? $"no way brings its cars onto {head.Label} cars first, and loco first they would shut it in; it keeps them"
                : status.Contains("foul")
                    ? $"no route to {head.Label}: cars standing too near a switch foul the way (move them clear, about 35 m from the points)"
                    : $"no route to {head.Label}");
            RemoveMine(engineer);
        }

        /// <summary>
        /// Puts a move ahead of the queue that runs the train straight on, by
        /// either end and as the switches lie, its own length and 20 m more:
        /// clear of any switch it stands across. False when there's no room.
        /// </summary>
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
            Main.Log($"jobs: {session.Name}: running clear of the switches first, then planning again");
            return true;
        }

        // ---- Steps ----

        /// <summary>The cars the jobs' steps move now, and where to: from each job's current moves not yet made.</summary>
        private void ReadTargets(Session session)
        {
            jobs = ReadJobs();
            destination.Clear();
            jobOf.Clear();
            lastMove.Clear();
            foreach (var job in jobs)
            {
                if (job.Job.State != DV.ThingTypes.JobState.InProgress || Unworkable(session, job) != null)
                    continue;
                foreach (var leg in job.Legs)
                {
                    // A move made already: its cars are still the job's, for a set-out to join.
                    foreach (var car in leg.Cars)
                    {
                        jobOf[car] = job.Id;
                        if (leg.Last)
                            lastMove.Add(car);
                    }
                    if (leg.There || leg.ToTrack < 0)
                        continue;
                    trackNames[leg.ToTrack] = leg.ToName;
                    foreach (var car in leg.Cars)
                    {
                        // A car already on its track stays: the rest of its move join it there.
                        jobOf[car] = job.Id;
                        if (!TrackOf(car, out var on) || on != leg.ToTrack)
                            destination[car] = leg.ToTrack;
                    }
                }
            }
        }

        private List<JobView> ReadJobs()
        {
            var views = new List<JobView>();
            foreach (var job in handed)
            {
                try
                {
                    views.Add(Jobs.Read(world, job));
                }
                catch (Exception e)
                {
                    Main.Warn($"jobs: couldn't read {job.ID}: {e.GetType().Name}: {e.Message}");
                }
            }
            return views;
        }

        /// <summary>A plan asked for and not yet queued: what it was asked from, to check it still holds when it comes back.</summary>
        private sealed class Asked
        {
            public System.Threading.Tasks.Task<JobStepPlan> Task;
            public CutBook Book;
            public List<string> Problems;
            public int JobCuts;
            public Trainset Train;
            public int TrainCars;
            public System.Diagnostics.Stopwatch Since;
            public double AskedMs;
        }

        /// <summary>
        /// Plans the jobs' current step: the cuts holding their cars, the
        /// cuts standing in their way, and the pulls. The game is read here,
        /// on its own thread (the jobs, the cars, the ways out of each
        /// track); the searches of the network, which cost a frame many
        /// times over, run on another thread from a frozen copy of the world
        /// (<see cref="JobStep"/>). The train stands meanwhile, and
        /// <see cref="Planned"/> queues the orders on the tick that finds the
        /// plan ready. With nothing to fetch, on to the next step at once.
        /// </summary>
        private void PlanStep(Session session)
        {
            if (++plans > PlanLimit)
            {
                End(session, $"it planned {PlanLimit} steps and still had work");
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            world.ScanCars();
            ReadTargets(session);
            var far = FarToNear(session);
            if (destination.Count == 0)
            {
                StepDone(session);
                return;
            }
            var problems = new List<string>();
            var book = new CutBook(this, session);
            foreach (var car in destination.Keys.ToList())
            {
                var set = car.trainset;
                if (set == null || set == session.Loco.trainset)
                    continue;
                if (book.Add(set) < 0)
                    problems.Add($"{JobsIn(set)}: its cars aren't on the network");
            }
            var planCuts = book.Cuts.Count;
            var state = session.Read();
            for (var i = 0; i < planCuts; i++)
                book.FindWays(i, state);

            // The train as the planner sees it: its far end and which way its cars lie, or a light engine.
            var start = far.Count > 0 && world.TryPlace(FarCoupler(session), out var fp, out var fo) ? new TrainPose(fp, fo)
                : world.TryPlace(session.Loco.frontCoupler, out var lp, out _) ? new TrainPose(lp, null) : default;
            var inTrain = far.Select(c => destination.TryGetValue(c, out var d) ? d : home.TryGetValue(c, out var hm) ? JobPlanner.RiderCode(hm) : -1).ToList();
            // Everything the plan reads, copied: the other thread touches nothing of the game's.
            var names = new Dictionary<int, string>(trackNames);
            var request = new JobStepRequest
            {
                World = world.Freeze(),
                Train = state,
                Start = start,
                Cuts = book.Cuts,
                JobCuts = planCuts,
                InTrain = inTrain,
                TrackName = t => names.TryGetValue(t, out var n) ? n : null,
            };
            pending = new Asked
            {
                Book = book,
                Problems = problems,
                JobCuts = planCuts,
                Train = session.Loco.trainset,
                TrainCars = session.Loco.trainset != null ? session.Loco.trainset.cars.Count : 0,
                Since = watch,
                AskedMs = watch.Elapsed.TotalMilliseconds,
            };
            pending.Task = System.Threading.Tasks.Task.Run(() => JobStep.Plan(request));
            Status = $"Planning the next moves for {string.Join(", ", jobOf.Values.Distinct())}";
        }

        /// <summary>
        /// Whether the plan that has come back was asked of the trains as
        /// they still are: the engineer's the same, and every cut it names
        /// still one cut of as many cars (a coupling or a cut meanwhile makes
        /// new trains of them).
        /// </summary>
        private static bool StillHolds(Session session, Asked asked)
        {
            var train = session.Loco.trainset;
            if (train != asked.Train || (train != null ? train.cars.Count : 0) != asked.TrainCars)
                return false;
            for (var k = 0; k < asked.Book.Sets.Count; k++)
            {
                var set = asked.Book.Sets[k];
                if (set == null || set.cars == null || set.cars.Count != asked.Book.Cuts[k].Destinations.Length)
                    return false;
                foreach (var car in set.cars)
                {
                    if (car == null || car.trainset != set)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The plan is worked out: says what it couldn't do, and queues its
        /// first trip's pulls (the next trip is planned afresh once this
        /// one's cars are set out). With nothing it can fetch, ends the run,
        /// saying why.
        /// </summary>
        private void Planned(Session session)
        {
            var asked = pending;
            pending = null;
            if (asked.Task.IsFaulted || asked.Task.IsCanceled)
            {
                var error = asked.Task.Exception?.GetBaseException();
                Main.Warn($"jobs: {session.Name}: planning failed: {error}");
                End(session, $"planning its moves failed ({error?.GetType().Name ?? "canceled"}: {error?.Message}); see the log");
                return;
            }
            if (!StillHolds(session, asked))
            {
                // Rare: cars were coupled or cut in the moment it took. Planned again as they are now.
                Main.Log($"jobs: {session.Name}: the trains changed while plan {plans} was worked out; planning again");
                PlanStep(session);
                return;
            }
            var step = asked.Task.Result;
            var plan = step.Plan;
            var book = asked.Book;
            var planCuts = asked.JobCuts;
            var problems = asked.Problems;
            var far = FarToNear(session);
            problems.AddRange(plan.Problems);
            var stepIds = string.Join(", ", jobOf.Values.Distinct());
            Main.Log($"jobs: {session.Name}: plan {plans} for {stepIds}: {book.Cuts.Count} cuts ({planCuts} of the jobs), " +
                     $"{plan.Pickups.Count} pulls ({string.Join("; ", plan.Pickups.Select(p => $"trip {p.Trip + 1}: " + Describe(book, p)))}), " +
                     $"then set-outs on {string.Join(", ", plan.Drops.Select(d => Name(d)))}; about {plan.Cost:F0} m; " +
                     $"planned in {step.Milliseconds:F0} ms off the game's thread (asked in {asked.AskedMs:F1} ms, back {asked.Since.Elapsed.TotalMilliseconds:F0} ms later)" +
                     (book.Notes.Count + step.Notes.Count > 0 ? "; " + string.Join("; ", book.Notes.Concat(step.Notes)) : "") +
                     (problems.Count > 0 ? "; left out: " + string.Join("; ", problems) : ""));
            if (problems.Count > 0)
                Main.Notify($"AI engineer: {session.Name}: {string.Join("; ", problems)}.");
            if (plan.Pickups.Count == 0 && !far.Any(c => destination.ContainsKey(c)))
            {
                End(session, problems.Count > 0 ? "it can't get at the cars" : null);
                return;
            }
            var light = far.Count == 0;
            var first = true;
            // The first trip only: the next is planned afresh once its cars are set out.
            var trip = plan.Pickups.Where(p => p.Trip == 0).ToList();
            var trips = plan.Pickups.Count > 0 ? plan.Pickups.Max(p => p.Trip) + 1 : 0;
            foreach (var p in trip)
            {
                var chain = new List<CutRef>(p.Through) { new CutRef(p.Cut, p.End) };
                for (var k = 0; k < chain.Count; k++)
                {
                    var r = chain[k];
                    var cut = book.Cuts[r.Cut];
                    var set = book.Sets[r.Cut];
                    var last = k == chain.Count - 1;
                    var kept = !last ? set.cars : r.End == 0 ? set.cars.Take(p.Keep) : set.cars.Skip(set.cars.Count - p.Keep);
                    foreach (var car in kept)
                    {
                        if (!destination.ContainsKey(car))
                            home[car] = cut.Home;
                    }
                    var label = last ? cut.Label + (p.Riders > 0 ? $", {p.Riders} riding along" : "") : $"{cut.Label}, in the way";
                    var waypoint = CoupleTo(cut.Ends[r.End], label, first && light ? facing : Arrival.CarsFirst,
                        last && p.Keep < set.cars.Count ? (Func<int>)(() => StrangersAtFarEnd(session)) : null);
                    Queue(session.Engineer, waypoint);
                    pullOrders.Add(waypoint);
                    if (k == 0)
                        pullStarts[waypoint] = (set.id, r.End);
                    first = false;
                }
            }
            phase = Phase.Working;
            Status = trip.Count > 0 ? $"Fetching {trip.Count} cut{(trip.Count == 1 ? "" : "s")} for {stepIds}" + (trips > 1 ? $" (trip 1 of {trips})" : "") : $"Setting out for {stepIds}";
        }

        private static string Describe(CutBook book, JobPickup p)
        {
            var cut = book.Cuts[p.Cut];
            var set = book.Sets[p.Cut];
            return cut.Label + (p.Through.Count > 0 ? $" through {p.Through.Count} cut{(p.Through.Count == 1 ? "" : "s")} ({string.Join(", ", p.Through.Select(t => book.Cuts[t.Cut].Label))})" : "") +
                   (p.Keep < set.cars.Count ? $", keeping {p.Keep}" : "") + (p.Riders > 0 ? $", {p.Riders} riding" : "");
        }

        /// <summary>The step's set-outs are made: on to the next once the game counts it done.</summary>
        private void StepDone(Session session)
        {
            phase = Phase.Waiting;
            waitingSince = Time.unscaledTime;
            nextLook = 0;
            Wait(session);
        }

        /// <summary>
        /// Between steps: plans the next once a job has moves to make; waits
        /// for the warehouse machine (said once) or for the game to count a
        /// step done; else ends the run.
        /// </summary>
        private void Wait(Session session)
        {
            if (Time.unscaledTime < nextLook)
                return;
            nextLook = Time.unscaledTime + 2;
            jobs = ReadJobs();
            var live = jobs.Where(j => j.Job.State == DV.ThingTypes.JobState.InProgress).ToList();
            if (live.Exists(j => j.Blocked == null && j.Legs.Exists(l => !l.There) && Unworkable(session, j) == null))
            {
                PlanStep(session);
                return;
            }
            var machine = live.Where(j => j.Warehouse != null).ToList();
            if (machine.Count > 0)
            {
                Status = "Waiting for the warehouse machine: " + string.Join("; ", machine.Select(j => $"{j.Id} on {j.Warehouse}"));
                foreach (var j in machine)
                {
                    if (told.Add(j.Id + "@" + j.Blocked))
                        Main.Notify($"AI engineer: {session.Name}: {j.Id}'s cars are on {j.Warehouse}: work the warehouse machine there; the engineer takes them on when it's done.");
                }
                return;
            }
            if (live.Exists(j => j.Settling) && Time.unscaledTime - waitingSince < SettleSeconds)
            {
                Status = "Waiting for the game to count the moves done";
                return;
            }
            End(session, null);
        }

        // ---- Set-outs ----

        /// <summary>
        /// Queues a set-out of the far-end <paramref name="cars"/> on a
        /// track. With <paramref name="join"/>, coupled to the same job's
        /// cars when they stand nearest an entry (the game counts a job's
        /// last move done only with its cars together); else into the room
        /// left there; else coupled to the cars by the entry (on through the
        /// next if need be) and shoved further in with them. False when
        /// nothing has room.
        /// </summary>
        private bool SetOut(Session session, int track, List<TrainCar> cars, string label, string done, bool join)
        {
            var length = Length(cars);
            var own = session.Loco.trainset.id;
            world.Occupancy.Standing(track, own, standing);
            var engineer = session.Engineer;
            var group = new HashSet<TrainCar>(cars);
            var spot = OrderGoals.SpotOn(world.Graph, track, length, standing);
            ShovePlan? joinPlan = null;
            if (join)
            {
                var ids = new HashSet<string>(cars.Select(c => jobOf.TryGetValue(c, out var j) ? j : null).Where(j => j != null));
                foreach (var owner in standing.Select(s => s.Owner).Distinct().ToList())
                {
                    var theirs = FindSet(owner);
                    if (theirs == null || !theirs.cars.Exists(c => jobOf.TryGetValue(c, out var j) && ids.Contains(j)))
                        continue;
                    joinPlan = OrderGoals.ShoveIn(world.Graph, track, length, standing, join: owner, movable: Movable);
                    if (joinPlan != null)
                        break;
                }
            }
            if (joinPlan != null)
            {
                // A last move must end coupled to its job's cars; another joins them unless that
                // means a much longer way round than setting out in the room left.
                var state = session.Read();
                var mustJoin = cars.Exists(c => lastMove.Contains(c));
                var joinCost = mustJoin || spot.Count == 0 ? 0 : RouteCost(state, OrderGoals.CoupleOnward(world.Graph, state, joinPlan.Value.CoupleTo[0], world.SelectedBranchFunc), carsFirst: true);
                var spotCost = mustJoin || spot.Count == 0 ? 0 : RouteCost(state, spot, carsFirst: true);
                if (mustJoin || spot.Count == 0 || joinCost <= spotCost + JoinPremium)
                    return Shove(session, track, cars, label, done, joinPlan.Value, joining: true);
                Main.Log($"jobs: {session.Name}: {label}: joining the job's cars there runs {joinCost:F0} m, the room left {spotCost:F0} m; setting out in the room");
            }
            // A free stretch, or the cars there shoved in from whichever entry: the one it reaches
            // cheapest, cars first, but a free stretch unless that is more than a little farther.
            var shove = CheapestShove(session, track, length, standing, out var shoveCost);
            if (spot.Count > 0 && shove != null)
            {
                var spotCost = RouteCost(session.Read(), spot, carsFirst: true);
                if (spotCost > shoveCost + SpotPremium)
                {
                    Main.Log($"jobs: {session.Name}: {label}: the free stretch runs {spotCost:F0} m, shoving the cars there in {shoveCost:F0} m: shoving them in");
                    return Shove(session, track, cars, label, done, shove.Value, joining: false, nearer: true);
                }
            }
            if (spot.Count > 0)
            {
                var waypoint = new Waypoint(OrderGoals.OnTrack(world.Graph, track, length), WaypointAction.Uncouple)
                {
                    CarCount = cars.Count,
                    CutCount = () => FarRun(session, group),
                    SetHandbrakes = true,
                    Arrival = Arrival.CarsFirst,
                    // Into a stub loco first, the cars cut off would shut the loco in behind them.
                    ArrivalRequired = IsStub(track),
                    Label = label,
                    GoalsFor = train => SpotGoals(session, track, length, train.Owner, label),
                    DoneNote = done,
                };
                Queue(engineer, waypoint);
                setOuts++;
                Main.Log($"jobs: {session.Name}: {done.Replace(" set out on ", " to set out on ").Replace(" put back on ", " to put back on ")} ({length:F0} m of cars)");
                return true;
            }
            return shove != null && Shove(session, track, cars, label, done, shove.Value, joining: false);
        }

        /// <summary>
        /// The shove-in a set-out onto a track with cars on it would make, by
        /// the entry its train reaches cheapest, cars first, to couple to them;
        /// null when neither entry has room. Its cost, m, by the router.
        /// </summary>
        private ShovePlan? CheapestShove(Session session, int track, double length, List<OccupiedSpan> standing, out double cost)
        {
            cost = double.PositiveInfinity;
            ShovePlan? best = null;
            if (standing.Count == 0)
                return null;
            var state = session.Read();
            foreach (var entry in new[] { TrackEnd.Start, TrackEnd.End })
            {
                var plan = OrderGoals.ShoveIn(world.Graph, track, length, standing, movable: Movable, entryOnly: entry);
                if (plan == null)
                    continue;
                var reach = RouteCost(state, OrderGoals.ToCouple(world.Graph, new[] { plan.Value.CoupleTo[0] }, world.SelectedBranchFunc), carsFirst: true);
                // One a route can't reach still stands, as before, when it's the only one.
                if (best == null || reach < cost)
                {
                    best = plan;
                    cost = reach;
                }
            }
            return best;
        }

        /// <summary>
        /// What reaching the goals costs the train as it stands, by the
        /// router (its length and a penalty a reversal), cars first if asked;
        /// infinity when no route does.
        /// </summary>
        private double RouteCost(TrainState state, IEnumerable<WaypointGoal> goals, bool carsFirst)
        {
            var best = double.PositiveInfinity;
            foreach (var goal in goals)
            {
                var request = Request(state, goal);
                if (carsFirst && !state.LightEngine)
                    request.LastLeader = state.LocoEnd.Other();
                var route = world.Router.Find(request, out _);
                if (route != null)
                    best = Math.Min(best, route.Length + request.ReversalPenalty * route.Reversals);
            }
            return best;
        }

        /// <summary>
        /// Where riders go: back on their own track, unless that is a long way
        /// round (a yard full near its switches leaves no room to turn) and a
        /// track nearby has room for them; -1 to go home.
        /// </summary>
        private int NearerThanHome(Session session, int home, List<TrainCar> cars, out double homeCost, out double asideCost)
        {
            homeCost = asideCost = double.PositiveInfinity;
            var length = Length(cars);
            var own = session.Loco.trainset.id;
            var state = session.Read();
            world.Occupancy.Standing(home, own, standing);
            var goals = OrderGoals.SpotOn(world.Graph, home, length, standing);
            if (goals.Count == 0)
                return -1;
            homeCost = RouteCost(state, goals, carsFirst: true);
            if (homeCost <= HomeDetour)
                return -1;
            var best = -1;
            foreach (var yard in Dispatch.Yards(world.Network, Dispatch.MapPosition(session.Loco)).Where(y => !y.Added).Take(3))
            {
                foreach (var t in yard.Tracks)
                {
                    if (t.Track == home || destination.ContainsValue(t.Track) || t.Length < length + 30)
                        continue;
                    world.Occupancy.Standing(t.Track, own, standing);
                    var spot = OrderGoals.SpotOn(world.Graph, t.Track, length, standing);
                    if (spot.Count == 0)
                        continue;
                    var c = RouteCost(state, spot, carsFirst: true);
                    if (c < asideCost)
                    {
                        asideCost = c;
                        best = t.Track;
                        trackNames[t.Track] = t.Id;
                    }
                }
            }
            return best >= 0 && asideCost + AsideSaving < homeCost ? best : -1;
        }

        /// <summary>Queues a set-out coupled to the cuts standing by the entry, in turn, and shoved in with them as far as the plan says.</summary>
        private bool Shove(Session session, int track, List<TrainCar> cars, string label, string done, ShovePlan plan, bool joining, bool nearer = false)
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
                Label = label + (joining ? ", joined to its cars there" : ", shoved in"),
                DoneNote = done + (joining ? $", coupled to the job's {theirs} there" : $", coupled to the {theirs} there") + (still ? "" : $" and shoved {plan.Shift:F0} m further in"),
            };
            Queue(session.Engineer, leave);
            setOuts++;
            Main.Log($"jobs: {session.Name}: {label}: " + (joining ? $"joining the job's {theirs} car{(theirs == 1 ? "" : "s")} there" : (nearer ? "nearer than the clear stretch: " : $"no clear stretch for {Length(cars):F0} m; ") + $"coupling to the {theirs} car{(theirs == 1 ? "" : "s")} by the entry ({plan.Owners.Length} cut{(plan.Owners.Length == 1 ? "" : "s")})") +
                     (still ? "" : $", shoving them {plan.Shift:F0} m further in"));
            return true;
        }

        /// <summary>
        /// Sets cars of the train that aren't the jobs' aside: cut off on the
        /// spot if they stand wholly on a yard track that isn't a job's
        /// destination, else set out on the nearest yard track with room.
        /// <paramref name="first"/>: the cars the loco had when handed the jobs.
        /// </summary>
        private bool SetAside(Session session, List<TrainCar> cars, bool first)
        {
            var group = new HashSet<TrainCar>(cars);
            var ids = cars.Count <= 3 ? string.Join(", ", cars.Select(c => c.ID)) : $"{cars.Count} cars";
            var on = cars[0].logicCar?.CurrentTrack;
            var whole = on != null && !on.ID.IsGeneric() && cars.TrueForAll(c => c.logicCar?.CurrentTrack == on);
            var rail = whole ? on.RailTrack() : null;
            if (rail != null && world.Network.TryGetTrack(rail, out var here) && !destination.ContainsValue(here))
            {
                Queue(session.Engineer, new Waypoint(new TrackPosition(here, 0), WaypointAction.Uncouple)
                {
                    Here = true,
                    CarCount = cars.Count,
                    CutCount = () => FarRun(session, group),
                    SetHandbrakes = true,
                    Label = $"{ids}, {(first ? "uncoupled first" : "set aside")} where they stand",
                    DoneNote = $"{(first ? "uncoupled from" : "set aside")} {ids} on {on.ID.FullDisplayID}, where they stood",
                });
                Status = first ? $"Uncoupling from {ids} first" : $"Setting {ids} aside on {on.ID.FullDisplayID}";
                Main.Log($"jobs: {session.Name}: {Status}, where they stand");
                return true;
            }
            var spare = FindSpare(session, Length(cars));
            if (spare < 0)
                return false;
            var name = Name(spare);
            Status = $"Setting {ids} aside on {name}";
            return SetOut(session, spare, cars, $"{name} (set aside)", $"set {ids} aside on {name}", join: false);
        }

        /// <summary>The nearest yard track (by distance along the track) with room for the cars, not a job's destination; -1 if none.</summary>
        private int FindSpare(Session session, double length)
        {
            if (!world.TryPlace(FarCoupler(session), out var from, out _))
                return -1;
            var distances = GraphDistances.From(world.Graph, from);
            var own = session.Loco.trainset.id;
            var best = -1;
            var bestDistance = double.PositiveInfinity;
            foreach (var yard in Dispatch.Yards(world.Network, Dispatch.MapPosition(session.Loco)).Where(y => !y.Added).Take(3))
            {
                foreach (var t in yard.Tracks)
                {
                    if (destination.ContainsValue(t.Track) || t.Length < length + 30)
                        continue;
                    world.Occupancy.Standing(t.Track, own, standing);
                    if (OrderGoals.SpotOn(world.Graph, t.Track, length, standing).Count == 0)
                        continue;
                    var d = distances.To(new TrackPosition(t.Track, t.Length / 2));
                    if (d < bestDistance)
                    {
                        best = t.Track;
                        bestDistance = d;
                        trackNames[t.Track] = t.Id;
                    }
                }
            }
            return best;
        }

        private RouteRequest Request(TrainState state, WaypointGoal goal, bool blocked = true, bool anySwitch = false) =>
            JobStep.Request(world, world.SelectedBranchFunc, state, goal, blocked, anySwitch);

        /// <summary>
        /// Why no route reaches an order's goals, for the log: whether one
        /// would with nothing standing in the way (and which tracks' cars are),
        /// or with every switch free to be thrown.
        /// </summary>
        private string WhyNoRoute(Session session, Waypoint wp)
        {
            var state = session.Read();
            var parts = new List<string> { $"train {state.Length:F0} m, {state.CarCount} cars, ends {world.Network.Describe(state.EndA.Track)} {state.EndA.Offset:F0} m / {world.Network.Describe(state.EndB.Track)} {state.EndB.Offset:F0} m" };
            foreach (var goal in wp.Goals)
            {
                var at = $"goal {world.Network.Describe(goal.Position.Track)} {goal.Position.Offset:F0} m{(goal.Arrive.HasValue ? " " + goal.Arrive.Value : "")}";
                if (world.Router.Find(Request(state, goal), out var why) != null)
                {
                    parts.Add(at + ": a route now");
                    continue;
                }
                var free = world.Router.Find(Request(state, goal, blocked: false), out _);
                if (free != null)
                {
                    var tracks = new HashSet<string>();
                    foreach (var leg in free.Legs)
                    {
                        foreach (var piece in leg.Pieces)
                        {
                            if (world.Occupancy.IsStanding(piece.Track, Math.Min(piece.From, piece.To), Math.Max(piece.From, piece.To), state.Owner))
                                tracks.Add(world.Network.Describe(piece.Track));
                        }
                    }
                    parts.Add(at + $": cars standing in the way, on {string.Join(", ", tracks)}");
                    continue;
                }
                parts.Add(at + (world.Router.Find(Request(state, goal, anySwitch: true), out _) != null ? ": a switch it can't throw" : $": none even past cars and switches ({why})"));
            }
            return string.Join("; ", parts);
        }

        /// <summary>A coupling to a cut's end, aimed afresh as it starts: across the gap when the cut stands just beyond the train.</summary>
        private Waypoint CoupleTo(CutEnd end, string label, Arrival arrival, Func<int> cutAfter)
        {
            var graph = world.Graph;
            var branch = world.SelectedBranchFunc;
            return new Waypoint(OrderGoals.ToCouple(graph, new[] { end }, branch), WaypointAction.Couple)
            {
                Label = label,
                Arrival = arrival,
                CutAfterCoupling = cutAfter,
                GoalsFor = train => OrderGoals.CoupleOnward(graph, train, end, branch),
            };
        }

        /// <summary>How many cars at the train's far end are of <paramref name="group"/>: the cut, counted from the cars as it is made.</summary>
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

        /// <summary>Queues an order of the run: after its others, ahead of any the player queued behind the run.</summary>
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

        /// <summary>
        /// Looks at how the run's orders that have left the queue ended. A
        /// coupling that found no cars, or wouldn't take, gives up its
        /// trip's other pulls (twice at one cut ends the run); a set-out
        /// that had none of its cars to cut ends it, rather than queue the
        /// same move again and again. False when the run has ended.
        /// </summary>
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
                    for (var i = engineer.Queue.Count - 1; i >= 0; i--)
                    {
                        if (pullOrders.Contains(engineer.Queue[i]))
                        {
                            issued.Remove(engineer.Queue[i]);
                            engineer.RemoveAt(i);
                        }
                    }
                    var what = order.Outcome == OrderOutcome.NoCars ? "found no cars at" : "couldn't couple to";
                    Main.Log($"jobs: {session.Name}: {what} {label} (time {times}); the rest of this trip's pulls dropped");
                    if (times >= 2)
                    {
                        End(session, $"it {what} {label}, twice");
                        RemoveMine(engineer);
                        return false;
                    }
                }
                else if (order.Outcome == OrderOutcome.NothingCut)
                {
                    End(session, $"none of the cars for {order.Label ?? "a set-out"} were at the train's far end to cut off");
                    RemoveMine(engineer);
                    return false;
                }
            }
            return true;
        }

        private void RemoveMine(Engineer engineer)
        {
            for (var i = engineer.Queue.Count - 1; i >= 0; i--)
            {
                if (mine.Contains(engineer.Queue[i]))
                    engineer.RemoveAt(i);
            }
        }

        /// <summary>Whether a track ends in a buffer stop at one end.</summary>
        private bool IsStub(int track) =>
            world.Graph.Links(track, TrackEnd.Start).Length == 0 || world.Graph.Links(track, TrackEnd.End).Length == 0;

        private static double Length(List<TrainCar> cars) => cars.Sum(c => (double)c.InterCouplerDistance);

        public void Cancel(Session session, string why)
        {
            // A plan still being worked out is dropped: its thread finishes and nothing reads it.
            pending = null;
            // Its orders still queued go too: left there, a pickup would go on without the run.
            var queue = session.Engineer.Queue;
            for (var i = queue.Count - 1; i >= 0; i--)
            {
                if (mine.Contains(queue[i]))
                    session.Engineer.RemoveAt(i);
            }
            var dropped = ThenIds;
            then.Clear();
            Main.Notify($"AI engineer: {session.Name} stopped working {Ids}{(dropped != null ? $" (and {dropped} after them)" : "")}: {why}.");
            Main.Log($"jobs: {session.Name}: stopped working {Ids}{(dropped != null ? $", {dropped} queued after" : "")}: {why}");
        }

        private void End(Session session, string why)
        {
            Finish(session, why);
            session.Jobs = null;
            StartNext(session);
        }

        /// <summary>The jobs queued behind this run, handed to a run of their own behind any orders still queued.</summary>
        private void StartNext(Session session)
        {
            if (then.Count == 0 || session.Engineer.Mode != EngineerMode.Waypoints)
                return;
            var views = new List<JobView>();
            foreach (var job in then)
            {
                if (job.State != DV.ThingTypes.JobState.InProgress)
                    continue;
                try
                {
                    views.Add(Jobs.Read(world, job));
                }
                catch (Exception e)
                {
                    Main.Warn($"jobs: couldn't read queued job {job.ID}: {e.GetType().Name}: {e.Message}");
                }
            }
            then.Clear();
            if (views.Count == 0)
                return;
            var ids = string.Join(", ", views.Select(v => v.Id));
            var next = Start(session, world, views, true, facing, out var problems);
            if (problems.Count > 0)
                Main.Notify($"AI engineer: {session.Name}: left out: {string.Join("; ", problems)}.");
            if (next == null)
                Main.Notify($"AI engineer: nothing {session.Name} can do for {ids}.");
        }

        /// <summary>Says how each job stands at the end: ready to turn in, or what is left, and why it stopped if it had to.</summary>
        private void Finish(Session session, string why)
        {
            pending = null;
            var parts = new List<string>();
            foreach (var job in handed)
            {
                JobView now;
                try
                {
                    now = Jobs.Read(world, job);
                }
                catch (Exception)
                {
                    parts.Add($"{job.ID} unreadable");
                    continue;
                }
                if (job.State != DV.ThingTypes.JobState.InProgress)
                    parts.Add($"{job.ID} {job.State.ToString().ToLowerInvariant()}");
                else if (job.tasks.TrueForAll(t => t.state == TaskState.Done))
                    parts.Add($"{job.ID} done: turn in its booklet");
                else if (now.Warehouse != null)
                    parts.Add($"{job.ID} waiting for the warehouse machine on {now.Warehouse}");
                else if (now.Legs.Count == 0 || now.Legs.TrueForAll(l => l.There))
                    parts.Add($"{job.ID} delivered, but not counted done ({now.Blocked})");
                else
                    parts.Add($"{job.ID} not done ({now.Summary})");
            }
            var text = $"{session.Name} has worked {Ids}: {string.Join("; ", parts)}" + (why != null ? $"; it stopped: {why}" : "");
            Main.Notify($"AI engineer: {text}.");
            Main.Log($"jobs: {text} ({setOuts} set-outs, {plans} steps planned)");
            Status = "Done";
        }

        /// <summary>Where a set-out stops: in the room left on its track, or as far in as it gets (said) when there's none.</summary>
        private IEnumerable<WaypointGoal> SpotGoals(Session session, int track, double length, int owner, string name)
        {
            world.Occupancy.Standing(track, owner, standing);
            var goals = OrderGoals.SpotOn(world.Graph, track, length, standing);
            if (goals.Count > 0)
                return goals;
            session.Engineer.Note = $"{name} hasn't {length:F0} m clear for its cars; setting them out as far in as it can";
            return OrderGoals.OnTrack(world.Graph, track, length);
        }

        /// <summary>
        /// The cuts the planner sees: those holding the jobs' cars, and those
        /// standing between them and their tracks' ways out, each once.
        /// </summary>
        private sealed class CutBook
        {
            private readonly JobRun run;
            private readonly int own;
            public readonly List<JobCut> Cuts = new List<JobCut>();
            public readonly List<Trainset> Sets = new List<Trainset>();

            public CutBook(JobRun run, Session session)
            {
                this.run = run;
                own = session.Loco.trainset != null ? session.Loco.trainset.id : int.MinValue;
            }

            /// <summary>The cut's index, added if new; -1 when it isn't on the network.</summary>
            public int Add(Trainset set)
            {
                var k = Sets.IndexOf(set);
                if (k >= 0)
                    return k;
                var world = run.world;
                if (!world.TryPlace(set.GetEndmost(true), out var pa, out var oa) || !world.TryPlace(set.GetEndmost(false), out var pb, out var ob))
                    return -1;
                var jobCars = set.cars.Count(c => run.destination.ContainsKey(c));
                Cuts.Add(new JobCut
                {
                    Label = jobCars > 0 ? $"{run.JobsIn(set)} on {Dispatch.Where(set.firstCar)}"
                        : $"{set.cars.Count} car{(set.cars.Count == 1 ? "" : "s")} on {Dispatch.Where(set.firstCar)}",
                    Ends = new[] { new CutEnd(pa, oa), new CutEnd(pb, ob) },
                    Destinations = set.cars.Select(c => run.destination.TryGetValue(c, out var d) ? d : -1).ToArray(),
                    Home = TrackOf(set.cars[set.cars.Count / 2], out var t) ? t : pa.Track,
                    Length = set.cars.Sum(c => (double)c.InterCouplerDistance),
                });
                Sets.Add(set);
                return Sets.Count - 1;
            }

            /// <summary>
            /// For each end of cut <paramref name="i"/>: the cuts standing
            /// between it and its track's way out, outermost first; or barred,
            /// by a buffer stop or a train with a loco. Reads the game's
            /// trains, so it runs on the game's thread; it searches nothing.
            /// </summary>
            public void FindWays(int i, TrainState state)
            {
                var cut = Cuts[i];
                var world = run.world;
                for (var e = 0; e < 2; e++)
                {
                    var end = cut.Ends[e];
                    var track = end.Position.Track;
                    var forward = end.Outward == TrackDirection.Forward;
                    world.Occupancy.Standing(track, Sets[i].id, run.standing);
                    double Gap(OccupiedSpan span) => forward ? Math.Min(span.From, span.To) - end.Position.Offset : end.Position.Offset - Math.Max(span.From, span.To);
                    // The engineer's own train on this track beyond the end: it comes at the end from
                    // there (even with a buffer that way, shoving the cars out), past nothing beyond it.
                    var ownGap = double.PositiveInfinity;
                    foreach (var span in run.standing)
                    {
                        if (span.Owner == own && Gap(span) >= -0.5)
                            ownGap = Math.Min(ownGap, Gap(span));
                    }
                    if (double.IsPositiveInfinity(ownGap) && world.Graph.Links(track, end.Outward.ExitEnd()).Length == 0)
                    {
                        cut.Barred[e] = true;
                        continue;
                    }
                    var beyond = new Dictionary<int, double>();
                    foreach (var span in run.standing)
                    {
                        if (span.Owner == own)
                            continue;
                        var gap = Gap(span);
                        if (gap < -0.5 || gap >= ownGap)
                            continue;
                        beyond[span.Owner] = beyond.TryGetValue(span.Owner, out var g) ? Math.Min(g, gap) : gap;
                    }
                    var way = new List<CutRef>();
                    foreach (var owner in beyond.OrderBy(b => b.Value).Select(b => b.Key).ToList())
                    {
                        var set = FindSet(owner);
                        var k = set != null && Movable(owner) ? Add(set) : -1;
                        if (k < 0)
                        {
                            cut.Barred[e] = true;
                            break;
                        }
                        way.Add(new CutRef(k, OutwardEnd(k, track, forward)));
                    }
                    if (cut.Barred[e])
                        continue;
                    way.Reverse();
                    cut.Way[e].Clear();
                    cut.Way[e].AddRange(way);
                    // The end the loco meets first: tried before and no route found, with a train no
                    // longer than this, and the end can't be had. Whether a route reaches it now, and
                    // goes on from it to where the cars go, is found with the plan (JobStep), off the
                    // game's thread.
                    var meet = way.Count > 0 ? way[0] : new CutRef(i, e);
                    var key = (Sets[meet.Cut].id, meet.End);
                    if (run.unreachable.TryGetValue(key, out var failedAt) && state.Length >= failedAt - 1)
                    {
                        cut.Barred[e] = true;
                        Notes.Add($"{cut.Label}: its {(e == 0 ? "A" : "B")} end found no route before");
                    }
                }
            }

            /// <summary>What finding the ways turned up, for the log.</summary>
            public readonly List<string> Notes = new List<string>();

            /// <summary>The end of cut <paramref name="k"/> farther out along the track the given way: the one a pull from that side couples to.</summary>
            private int OutwardEnd(int k, int track, bool forward)
            {
                var a = Cuts[k].Ends[0].Position;
                var b = Cuts[k].Ends[1].Position;
                if (a.Track == track && b.Track == track)
                    return (a.Offset > b.Offset) == forward ? 0 : 1;
                // An end off this track lies beyond its way out.
                return a.Track != track ? 0 : 1;
            }
        }

        /// <summary>How many cars at the train's far end aren't on these jobs: what a pickup cuts off again.</summary>
        private int StrangersAtFarEnd(Session session)
        {
            var n = 0;
            foreach (var car in FarToNear(session))
            {
                if (destination.ContainsKey(car))
                    break;
                n++;
            }
            // Never the whole train: with no job car on it at all, the coupling went wrong; keep what it has.
            return n == FarToNear(session).Count ? 0 : n;
        }

        /// <summary>The train's cars from its far end in to the loco (the far end as Session.Cut reckons it).</summary>
        private static List<TrainCar> FarToNear(Session session)
        {
            var set = session.Loco.trainset;
            var list = new List<TrainCar>();
            if (set == null)
                return list;
            var cars = set.cars;
            var locoIndex = cars.IndexOf(session.Loco);
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
            // Locos coupled next to the engineer's own (a second unit, a slug, a tender) are its
            // power, not cars, and a caboose there rides with them: never set aside, put back, or
            // cut off as strangers.
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

        /// <summary>Whether a standing cut may be coupled to and shoved: no loco in it (another's train, maybe worked), no car off the rails.</summary>
        private static bool Movable(int owner)
        {
            var set = FindSet(owner);
            return set != null && set.locoIndices.Count == 0 && !set.cars.Exists(c => c == null || c.derailed);
        }

        /// <summary>The network track a car stands on, if it's a named (yard) track.</summary>
        private static bool TrackOf(TrainCar car, out int track)
        {
            track = -1;
            var on = car.logicCar?.CurrentTrack;
            if (on == null || on.ID == null || on.ID.IsGeneric())
                return false;
            var rail = on.RailTrack();
            return rail != null && Host.World != null && Host.World.Network.TryGetTrack(rail, out track);
        }

        private string JobsIn(Trainset set)
        {
            var ids = set.cars.Where(c => jobOf.ContainsKey(c)).Select(c => jobOf[c]).Distinct().ToList();
            var cars = set.cars.Count(c => jobOf.ContainsKey(c));
            return $"{string.Join(", ", ids)} ({cars} car{(cars == 1 ? "" : "s")})";
        }

        private string Name(int track) => trackNames.TryGetValue(track, out var n) ? n : world.Network.Describe(track);
    }
}
