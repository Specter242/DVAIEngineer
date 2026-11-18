using System;
using System.Collections.Generic;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using DV.MultipleUnit;
using DV.Simulation.Controllers;
using DV.ThingTypes;
using LocoSim.Implementations;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>
    /// One loco under an AI engineer: reads its consist each tick into the
    /// engineer's terms (the two outer couplers on the network, the way out
    /// of the train at each, speed toward end A), applies what the engineer
    /// decides, and carries out couplings and cuts. End A is the trainset's
    /// first car's outer coupler, end B its last car's.
    /// </summary>
    internal sealed class Session
    {
        private const float StartRetrySeconds = 20f;

        /// <summary>How many times a shut-down loco is started before the engineer gives it up.</summary>
        private const int StartTries = 3;

        /// <summary>A tick slower than this is logged: a route search that should move off the main thread.</summary>
        private const double SlowTickMs = 4;

        private readonly World world;
        private float lastTick;
        private float lastStartAttempt = float.NegativeInfinity;
        private int startAttempts;
        private bool standing;
        private bool easedNoted;
        private bool prepared;
        private bool keeping;
        private bool previousPreventDelete;

        public Session(TrainCar loco, World world, EngineerSettings settings)
        {
            Loco = loco;
            this.world = world;
            Engineer = new Engineer(settings);
            Controls = new LocoControls(loco);
            lastTick = Time.time;
            Unsupported = WhyUnsupported(loco);
            Steam = SteamCrew.For(loco);
            Controls.Steam = Steam != null;
        }

        /// <summary>A steam loco's fire, water, and cylinder cocks; null for any other loco.</summary>
        public SteamCrew Steam { get; }

        /// <summary>Working as a helper on a doubleheaded train, from the lead's whistle and levers; null otherwise.</summary>
        public Helper Helper { get; private set; }

        /// <summary>
        /// Makes this steam loco the helper on the train the player drives
        /// from <paramref name="lead"/>; returns why it can't, or null.
        /// </summary>
        public string SetHelper(TrainCar lead)
        {
            if (Steam == null)
                return "only a steam loco helps by the whistle (diesels and electrics work in multiple)";
            if (lead == null || lead == Loco || lead.trainset == null || lead.trainset != Loco.trainset)
                return "it must be coupled in the train you drive";
            var helper = new Helper(this, lead);
            if (!helper.Usable)
                return $"{lead.ID} has no whistle or levers it can read";
            Stop(secure: false);
            Helper = helper;
            Engineer.SetHelper(helper.Status);
            Main.Notify($"AI engineer: {Name} is your helper on {lead.ID}'s train: two long to go, one short to stop, three short (standing) to back up; it answers two short.");
            Main.Log($"{Name}: helper to {lead.ID}, {(Vector3.Dot(Loco.transform.forward, lead.transform.forward) > 0 ? "facing the same way" : "facing the other way")}; its whistle heard from {helper.HeardFrom}");
            return null;
        }

        private bool steamNoted;

        /// <summary>The slowest engineer tick since the game started, ms.</summary>
        public static double SlowestTickMs { get; private set; }

        public TrainCar Loco { get; }

        public Engineer Engineer { get; }

        public LocoControls Controls { get; }

        /// <summary>Why this loco can't be driven yet (steam, a manual gearbox); null if it can.</summary>
        public string Unsupported { get; }

        /// <summary>When this session next ticks, in Time.time.</summary>
        public float NextTick { get; set; }

        /// <summary>The last state read, for the windows.</summary>
        public TrainState State { get; private set; }

        public bool HasState { get; private set; }

        public string Name => Loco != null ? Loco.ID : "(gone)";

        /// <summary>The engineer's last note, as told to the player (the note itself is cleared once told).</summary>
        public string LastNote { get; private set; }

        /// <summary>The engineer's last logged note, not told to the player.</summary>
        public string LastLogged { get; private set; }

        /// <summary>A loco, tender, or slug: what moves a train, not what it carries.</summary>
        public static bool IsPower(TrainCar car) =>
            car != null && (car.IsLoco || CarTypes.IsAnyLocomotiveOrTender(car.carLivery) || CarTypes.IsSlug(car.carLivery));

        /// <summary>
        /// Whether the engineer has nothing to do but hold the train:
        /// standing with no orders, or waiting on the player at a warehouse
        /// machine. It is then looked in on once a second, and the cars
        /// aren't scanned on its account.
        /// </summary>
        public bool Idle => standing && Engineer.Mode == EngineerMode.Waypoints && Engineer.Queue.Count == 0 && (Jobs == null || Jobs.WaitingOnPlayer);

        /// <summary>The player at this loco's controls: aboard its train, or holding a remote paired to it.</summary>
        private bool PlayerAtControls()
        {
            var car = PlayerManager.Car;
            if (car != null && car.trainset != null && car.trainset == Loco.trainset)
                return true;
            var remote = Loco.SimController != null ? Loco.SimController.remoteController : null;
            return remote != null && remote.IsPaired;
        }

        /// <summary>
        /// Ends whatever the engineer is doing and holds the train where it
        /// is, the engineer still on it: its orders gone, and any jobs it
        /// was working handed back. (Emptying the queue alone reads to a job
        /// run as its last order done, and it sets off on the next.)
        /// </summary>
        public void StopHere()
        {
            if (Jobs != null)
            {
                Jobs.Cancel(this, "you stopped it");
                Jobs = null;
            }
            Engineer.ClearQueue();
            if (Engineer.Mode != EngineerMode.Off)
                Engineer.SetWaypoints();
        }

        /// <summary>Removes one order; if a job run put it there, the run ends with it (it can't carry on round a hole in its plan).</summary>
        public void RemoveOrder(int index)
        {
            if (index < 0 || index >= Engineer.Queue.Count)
                return;
            if (Jobs != null && Jobs.Owns(Engineer.Queue[index]))
            {
                StopHere();
                return;
            }
            Engineer.RemoveAt(index);
        }

        /// <summary>Whether Road and Yard moves head toward the loco's own front.</summary>
        public bool MoveForward { get; private set; } = true;

        /// <summary>
        /// Which consist end lies ahead of the loco's front, read live: a
        /// coupling can make either end A.
        /// </summary>
        public ConsistEnd FrontEnd
        {
            get
            {
                var set = Loco != null ? Loco.trainset : null;
                if (set == null)
                    return ConsistEnd.A;
                return FrontLeadsTo(Loco) == set.GetEndmost(true) ? ConsistEnd.A : ConsistEnd.B;
            }
        }

        public void Dispose()
        {
            Stop(secure: false);
        }

        /// <summary>Runs at a set speed toward the loco's front, or its back.</summary>
        public void SetRoad(bool forward, double speed)
        {
            MoveForward = forward;
            Engineer.SetRoad(forward ? FrontEnd : FrontEnd.Other(), speed);
        }

        /// <summary>One car length for Yard moves, as Railroader counts it (40 ft), m.</summary>
        public const double CarLength = 12.2;

        /// <summary>Moves a distance toward the loco's front, or its back.</summary>
        public void SetYard(bool forward, double distance)
        {
            MoveForward = forward;
            Engineer.SetYard(forward ? FrontEnd : FrontEnd.Other(), distance);
        }

        /// <summary>
        /// The engineer lets go. Unless the player took the controls, the
        /// loco is left safe first (power off, brakes on), so a train it was
        /// driving doesn't run on with no one watching.
        /// </summary>
        public void Stop(bool secure)
        {
            var wasOn = Engineer.Mode != EngineerMode.Off;
            var helping = Helper != null;
            Engineer.SetOff();
            Helper?.Release();
            Helper = null;
            try
            {
                Steam?.Release();
            }
            catch (Exception e)
            {
                Main.Warn($"{Name}: its fireman couldn't be stood down: {e.GetBaseException().Message}");
            }
            try
            {
                // The train first: nothing after this may leave it under power. A helper shuts off and
                // brakes itself only: the train's brake is the lead's.
                if (Loco != null && secure && wasOn && Controls.Usable)
                {
                    if (helping)
                        Controls.Help(0f, null, 1f, Time.time);
                    else
                        Controls.Secure(Time.time);
                }
            }
            finally
            {
                Controls.Release();
                Keep(false);
                prepared = false;
                standing = false;
                startAttempts = 0;
                var jobs = Jobs;
                Jobs = null;
                jobs?.Cancel(this, "the engineer was stopped");
            }
        }

        /// <summary>
        /// While driving, the loco is kept from the game's clean-up, which
        /// deletes an unattended loco more than 4 km from the player.
        /// </summary>
        private void Keep(bool keep)
        {
            if (keep == keeping || Loco == null)
                return;
            keeping = keep;
            if (keep)
            {
                previousPreventDelete = Loco.preventDelete;
                Loco.preventDelete = true;
            }
            else
            {
                Loco.preventDelete = previousPreventDelete;
            }
        }

        public void Tick(float now)
        {
            var dt = Mathf.Clamp(now - lastTick, 0f, 1f);
            lastTick = now;
            if (Loco == null)
                return;
            if (Engineer.Mode == EngineerMode.Off)
            {
                Keep(false);
                prepared = false;
                return;
            }
            if (Unsupported != null)
            {
                Stop(secure: false);
                Main.Notify($"AI engineer: {Name} {Unsupported}.");
                return;
            }
            if (!MultiplayerGate.MayDrive)
            {
                Stop(secure: false);
                Main.Notify("AI engineer: only the host runs engineers in multiplayer.");
                return;
            }
            if (!Controls.Usable)
            {
                Stop(secure: false);
                Main.Notify($"AI engineer: {Name}'s controls can't be worked from here.");
                return;
            }
            var moved = Controls.MovedByAnother(now);
            if (moved != null)
            {
                if (PlayerAtControls())
                {
                    Stop(secure: false);
                    Main.Notify($"AI engineer: you have {Name}'s controls.");
                    return;
                }
                if (Controls.OnlyThrottleEased(now))
                {
                    // Wheel-slip or overheat protection easing the throttle: the engineer follows it.
                    Controls.FollowEasedThrottle(now);
                    if (!easedNoted)
                        Main.Log($"{Name}: something eased its throttle (wheel-slip or overheat protection?); the engineer follows it");
                    easedNoted = true;
                }
                else
                {
                    // No one aboard and a lever moved: another mod, another player, a linked loco.
                    // Not a train to leave running with nobody driving.
                    Stop(secure: true);
                    Main.Notify($"AI engineer: something else moved {Name}'s {moved}; the engineer has stopped the train and let go.");
                    return;
                }
            }
            Keep(true);
            // Road and Yard moves keep their way relative to the loco, whichever end is A.
            if (Engineer.Mode == EngineerMode.Road || Engineer.Mode == EngineerMode.Yard)
                Engineer.SetToward(MoveForward ? FrontEnd : FrontEnd.Other());

            var state = Read();
            State = state;
            HasState = true;
            standing = Math.Abs(state.SpeedTowardA) < Engineer.StandingSpeed;
            if (state.CanMove)
            {
                startAttempts = 0;
            }
            else if (Main.Settings.AutoStart && now - lastStartAttempt > StartRetrySeconds)
            {
                if (startAttempts >= StartTries)
                {
                    Stop(secure: true);
                    Main.Notify($"AI engineer: {Name} won't start (out of fuel, or a fuse?); the engineer has let go.");
                    return;
                }
                lastStartAttempt = now;
                startAttempts++;
                StartupHelper.Startup(Loco);
            }

            if (Engineer.Mode == EngineerMode.Helper)
            {
                TickHelper(now, state);
                return;
            }

            // While a job run's next moves are planned on another thread the train stands: an
            // order waiting behind the run mustn't set off in between. Read afresh each tick, so
            // a run that ends or is canceled can't leave the engineer held.
            Engineer.Holding = Jobs != null && Jobs.Planning ? "Planning the jobs' next moves" : Steam?.Waiting(standing);
            if (Steam != null && !steamNoted)
            {
                steamNoted = true;
                var note = Steam.Note;
                if (note != null)
                    Main.Notify($"AI engineer: {Name}: {note}.");
                Main.Log($"{Name}: steam, {(Steam.Realistic ? "fired by Realistic Firing's fireman" : "fired by the engineer")}, {Steam.Pressure:F1} bar");
            }
            var before = Engineer.Route;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var output = Engineer.Tick(dt, state, world);
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            SlowestTickMs = Math.Max(SlowestTickMs, ms);
            if (ms > SlowTickMs)
                Main.Warn($"{Name}: engineer tick took {ms:F1} ms ({(Engineer.Route != before ? "planned a route" : Engineer.Status)})");
#if !PUBLIC_RELEASE
            var logRoute = Main.Settings.LogRoutes || Diagnostics.SelfTest.Requested;
#else
            var logRoute = Main.Settings.LogRoutes;
#endif
            if (logRoute && Engineer.Route != null && !ReferenceEquals(Engineer.Route, before))
                Main.Log($"{Name}: route {Engineer.Route.Length:F0} m, {Engineer.Route.Reversals} reversals, planned in {ms:F1} ms\n{Describe(Engineer.Route)}");
            if (Engineer.Route != null && !ReferenceEquals(Engineer.Route, before))
            {
                try
                {
                    LogFirstRoute(state);
                }
                catch (Exception e)
                {
                    // A log line must never stop a train.
                    Main.Warn($"{Name}: couldn't log its route: {e.GetType().Name}: {e.Message}");
                }
            }
            LogWaits(state);
            if (Engineer.Note != null)
            {
                LastNote = Engineer.Note;
                Main.Notify($"AI engineer: {Name}: {Engineer.Note}.");
                Main.Log($"{Name}: {Engineer.Note}");
                Engineer.Note = null;
            }
            if (Engineer.LogNote != null)
            {
                LastLogged = Engineer.LogNote;
                Main.Log($"{Name}: {Engineer.LogNote}");
                Engineer.LogNote = null;
            }
            WatchForTrouble(state, now);
            WatchForStanding(now);
            if (!output.Active)
                return;
            if (!prepared)
            {
                // Once as it takes over: cut in the brake valve, take off the loco's hand brake.
                Controls.Prepare();
                prepared = true;
            }
            bool? forward = null;
            if (output.Toward.HasValue)
                forward = output.Toward.Value == FrontEnd;
            Controls.Apply(output.Controls, forward, Math.Abs(state.SpeedTowardA) < Engineer.StandingSpeed, now, state.SpeedTowardA);
            if (Steam != null)
            {
                var stopAhead = Engineer.Route != null ? Math.Max(0, Engineer.StopAt - Engineer.Progress) : double.PositiveInfinity;
                var road = Engineer.Mode == EngineerMode.Road || (Engineer.Route != null && Engineer.Route.Length > 2000);
                try
                {
                    Steam.Tick(now, (float)output.Controls.Throttle, standing, state.SpeedTowardA, stopAhead, road);
                }
                catch (Exception e)
                {
                    Main.Warn($"{Name}: its fire couldn't be seen to: {e.GetBaseException().Message}");
                }
            }

            if (output.ReleaseHandbrakes)
                ReleaseHandbrakes();
            if (output.CoupleAt.HasValue)
                Couple(output.CoupleAt.Value);
            if (output.Cut.HasValue)
                Cut(output.Cut.Value);
            if (Jobs != null)
            {
                // Timed and logged as the engineer's own is. The plan of a step, the dearest thing the
                // mod does, is worked out on another thread (JobRun.PlanStep); what is left here is
                // reading the jobs and cars, and queuing the orders.
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                Jobs.Tick(this);
                ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                SlowestJobsTickMs = Math.Max(SlowestJobsTickMs, ms);
                if (ms > SlowTickMs)
                    Main.Warn($"{Name}: working jobs took {ms:F1} ms this tick ({Jobs?.Status})");
            }
        }

        /// <summary>The slowest tick of a job run since the game started, ms.</summary>
        public static double SlowestJobsTickMs { get; private set; }

        /// <summary>The jobs this loco was handed, while it works them; null otherwise.</summary>
        public JobRun Jobs { get; set; }

        private string lastStatus;
        private float waitingSince;

        /// <summary>
        /// Logs each wait as it begins and how long the last one took: a
        /// stop for a switch, for cars, for the brakes, or for want of a
        /// route, the things a player sees as the train standing.
        /// </summary>
        private void LogWaits(TrainState state)
        {
            var status = Engineer.Status;
            if (status == lastStatus)
                return;
            var wasWaiting = IsWait(lastStatus);
            var waiting = IsWait(status);
            // A wait's figures change as it goes ("2.1 of 4.2 bar"): the same wait.
            if (wasWaiting && waiting && Stem(status) == Stem(lastStatus))
            {
                lastStatus = status;
                return;
            }
            if (wasWaiting)
                Main.Log($"{Name}: waited {Time.time - waitingSince:F0} s: {lastStatus}");
            if (waiting)
            {
                waitingSince = Time.time;
                Main.Log($"{Name}: {status} ({Math.Abs(state.SpeedTowardA) * 3.6:F1} km/h, {state.CarCount} cars, air {(state.Controls.SlowAir ? Engineer.IndependentOnly(state) ? "slow, on the independent" : "slow" : "stock")}; leg {Engineer.LegIndex + 1}, {Engineer.StopAt - Engineer.Progress:F1} m to its stop)");
            }
            lastStatus = status;
        }

        private static bool IsWait(string status) =>
            status != null && (status.StartsWith("Waiting:") || status.StartsWith("Stopping:") || status.StartsWith("Releasing")
                               || status.StartsWith("Charging") || status.StartsWith("Taking off") || status.StartsWith("No route")
                               || status.StartsWith("Off the route") || status.StartsWith("Couldn't") || status.StartsWith("Can't") || status.StartsWith("The waypoint"));

        private static string Stem(string status)
        {
            var colon = status.IndexOf(':');
            return colon > 0 ? status.Substring(0, colon) : status;
        }

        /// <summary>A helper's tick: its levers from the lead's, its fire seen to; let go if the lead is no longer in its train.</summary>
        private void TickHelper(float now, TrainState state)
        {
            if (Helper == null || Helper.Lead == null || Helper.Lead.trainset != Loco.trainset)
            {
                var lead = Helper?.Lead != null ? Helper.Lead.ID : "the lead";
                Stop(secure: true);
                Main.Notify($"AI engineer: {Name} is no longer coupled to {lead}: it has stopped helping, brakes on.");
                return;
            }
            var regulator = Helper.Tick(now, standing, state.SpeedTowardA);
            try
            {
                Steam?.Tick(now, regulator, standing, state.SpeedTowardA, double.PositiveInfinity, road: true);
            }
            catch (Exception e)
            {
                Main.Warn($"{Name}: its fire couldn't be seen to: {e.GetBaseException().Message}");
            }
        }

        private Waypoint routeLoggedFor;

 private void LogFirstRoute(TrainState state)
        {
            var head = Engineer.Queue.Count > 0 ? Engineer.Queue[0] : null;
            var route = Engineer.Route;
            if (head == null || ReferenceEquals(head, routeLoggedFor) || route.Legs.Count == 0)
                return;
            routeLoggedFor = head;
            var leg = route.Legs[0];
            var way = leg.Leader == state.LocoFront ? "loco's front first" : "loco's rear first";
            var net = world.Network;
            Main.Log($"{Name}: to {head.Label}: standing on {net.Describe(state.EndA.Track)} / {net.Describe(state.EndB.Track)}, {state.CarCount} cars, " +
                $"reverser {(state.Reverser.HasValue ? (state.Reverser == state.LocoFront ? "forward" : "reverse") : "neutral")}; " +
                $"route {route.Length:F0} m, {route.Reversals} reversals, sets off {way}, {leg.Length:F0} m\n{Describe(route)}");
        }

        /// <summary>A route, leg by leg, for the log: its pieces (track IDs and offsets) and switches.</summary>
        private string Describe(Route route)
        {
            var b = new System.Text.StringBuilder();
            for (var l = 0; l < route.Legs.Count; l++)
            {
                var leg = route.Legs[l];
                b.Append($"  leg {l + 1}, {leg.Leader} leading, {leg.Length:F0} m:");
                foreach (var p in leg.Pieces)
                    b.Append($" [{p.Track} {world.Network.Describe(p.Track)} {p.From:F0}->{p.To:F0}]");
                foreach (var sw in leg.Switches)
                    b.Append($" sw{sw.Junction}={sw.Branch}@{sw.AtDistance:F0}");
                b.Append('\n');
            }
            return b.ToString().TrimEnd();
        }

        /// <summary>Seconds a train may stand on one order before the player is told what it waits on.</summary>
        private const float StandingNotice = 300f;

        private Waypoint stoodOn;
        private float stoodSince;
        private bool stoodTold;

        /// <summary>
        /// Tells the player, once an order, when the train has stood five
        /// minutes on it and what the engineer says it is waiting on: cars
        /// ahead, a switch, a route, the brakes. A train that stands and
        /// says nothing looks broken.
        /// </summary>
        private void WatchForStanding(float now)
        {
            var head = Engineer.Mode == EngineerMode.Waypoints && Engineer.Queue.Count > 0 ? Engineer.Queue[0] : null;
            var status = Engineer.Status ?? "";
            // A wait the order itself asks for ("At X; waiting 240 s") isn't trouble.
            if (head == null || !standing || status.StartsWith("At ", StringComparison.Ordinal))
            {
                stoodOn = null;
                return;
            }
            if (!ReferenceEquals(head, stoodOn))
            {
                stoodOn = head;
                stoodSince = now;
                stoodTold = false;
                return;
            }
            if (stoodTold || now - stoodSince < StandingNotice)
                return;
            stoodTold = true;
            Main.Notify($"AI engineer: {Name} has stood {StandingNotice / 60:F0} minutes on its order for {head.Label ?? "a waypoint"}: {status}.");
        }

        private float lastSpeed = float.NaN;
        private bool wasOnTrack = true;

        /// <summary>
        /// Logs what the engineer saw when the train derails, or stops much
        /// faster than any brake would (a collision): its leg and place on
        /// it, the switches ahead and how they lie, and the cars near.
        /// </summary>
        private void WatchForTrouble(TrainState state, float now)
        {
            var speed = (float)Math.Abs(state.SpeedTowardA);
            var before = lastSpeed;
            var slammed = !float.IsNaN(before) && before - speed > 3f;
            var derailed = wasOnTrack && !state.OnTrack;
            lastSpeed = speed;
            wasOnTrack = state.OnTrack;
            if (!slammed && !derailed)
                return;
            var b = new System.Text.StringBuilder();
            b.Append($"{Name}: {(derailed ? "DERAILED" : "SUDDEN STOP")} from {before * 3.6:F0} km/h; A {state.EndA}, B {state.EndB}; " +
                     $"leg {Engineer.LegIndex + 1}, progress {Engineer.Progress:F1}, stop at {Engineer.StopAt:F1}, status '{Engineer.Status}'");
            var route = Engineer.Route;
            if (route != null && Engineer.LegIndex < route.Legs.Count)
            {
                foreach (var sw in route.Legs[Engineer.LegIndex].Switches)
                    b.Append($"\n  switch {sw.Junction} wanted {sw.Branch} at {sw.AtDistance:F0} m, lies {world.SelectedBranch(sw.Junction)}");
            }
            foreach (var car in CarSpawner.Instance.AllCars)
            {
                if (car == null || car.trainset == Loco.trainset)
                    continue;
                var d = Vector3.Distance(car.transform.position, Loco.transform.position);
                if (d < 60)
                    b.Append($"\n  {car.ID} {d:F0} m away, set {car.trainset?.id}, derailed {car.derailed}, front on {car.FrontBogie?.track?.name}, rear on {car.RearBogie?.track?.name}");
            }
            Main.Warn(b.ToString());
            if (!derailed)
                return;
            Main.Notify($"AI engineer: {Name}'s train has derailed; the engineer holds it where it is.");
            var run = Jobs;
            if (run != null)
            {
                Jobs = null;
                run.Cancel(this, "the train derailed");
            }
        }

        /// <summary>The consist as the engineer sees it.</summary>
        public TrainState Read()
        {
            var state = new TrainState();
            var set = Loco.trainset;
            if (set == null || set.cars.Count == 0)
                return state;
            var front = FrontEnd;
            var reverser = Controls.ReverserForward;
            if (reverser.HasValue)
                state.Reverser = reverser.Value ? front : front.Other();
            var endA = set.GetEndmost(true);
            var endB = set.GetEndmost(false);
            var onTrack = world.TryPlace(endA, out state.EndA, out state.OutwardA) & world.TryPlace(endB, out state.EndB, out state.OutwardB);
            var length = 0.0;
            var mass = 0.0;
            var locoMass = 0.0;
            var power = 0;
            foreach (var car in set.cars)
            {
                length += car.InterCouplerDistance;
                if (IsPower(car))
                    power++;
                if (car.derailed)
                    onTrack = false;
                var m = car.massController != null ? car.massController.TotalMass : 0f;
                mass += m;
                if (car.IsLoco)
                {
                    locoMass += m;
                    var slip = car.SimController != null ? car.SimController.wheelslipController : null;
                    if (slip != null && slip.IsWheelslipping)
                        state.Slipping = true;
                }
            }
            state.LocoMassShare = mass > 0 ? locoMass / mass : 0;
            var v = endA.train.GetForwardSpeed();
            state.SpeedTowardA = endA.isFrontCoupler ? v : -v;
            state.Length = length;
            state.CarCount = set.cars.Count;
            state.PowerCars = power;
            state.LocoFront = front;
            // cars[0] is end A's car. As Cut reckons it, the far end is the one with more cars beyond the loco (A on a tie).
            var locoIndex = set.cars.IndexOf(Loco);
            state.LocoEnd = locoIndex < set.cars.Count - 1 - locoIndex ? ConsistEnd.A : ConsistEnd.B;
            state.PowerAtFarEnd = IsPower(set.cars[state.LocoEnd == ConsistEnd.A ? set.cars.Count - 1 : 0]);
            state.Owner = set.id;
            state.OnTrack = onTrack;
            state.CanMove = Controls.EngineRunning;
            var brakeset = Loco.brakeSystem?.brakeset;
            // A light engine brakes on its independent; one with no independent has only the train brake.
            var hasTrainBrake = Controls.HasTrainBrakeControl && brakeset != null && (brakeset.cars.Count > 1 || !Controls.HasLocoBrake);
            state.Controls = new ControlCapabilities
            {
                HasTrainBrake = hasTrainBrake,
                HasDynamicBrake = Controls.HasDynamicBrake,
                SlowAir = hasTrainBrake && BrakesMod.Active,
            };
            ReadAir(set, ref state);
            return state;
        }

        /// <summary>
        /// The cars' air, for the engineer's check before it starts: the
        /// highest cylinder pressure and lowest pipe among the cars (gauge;
        /// the game keeps them absolute), and their hand brakes. Power is
        /// left out of the pressures (a loco's own brake holds the train while
        /// it waits, and a tender's cylinder copies its loco's, so it would
        /// read as a brake that never comes off) but not of the hand brakes:
        /// this loco's too, which the player may set after the engineer took over.
        /// </summary>
        private void ReadAir(Trainset set, ref TrainState state)
        {
            var any = false;
            var cylinder = 0.0;
            var pipe = double.PositiveInfinity;
            var handbrakes = 0;
            foreach (var car in set.cars)
            {
                var brakes = car != null ? car.brakeSystem : null;
                if (brakes == null)
                    continue;
                if (brakes.hasHandbrake && brakes.handbrakePosition > 0.01f)
                    handbrakes++;
                if (IsPower(car))
                    continue;
                any = true;
                cylinder = Math.Max(cylinder, brakes.brakeCylinderPressure - 1.0);
                pipe = Math.Min(pipe, brakes.brakePipePressure - 1.0);
            }
            // Cars not on the loco's air (no hoses to it) can't be released from the cab: only their hand brakes count.
            var onAir = any && state.Controls.HasTrainBrake;
            state.AirKnown = onAir || handbrakes > 0;
            state.CarCylinderMax = onAir ? cylinder : 0;
            state.CarPipeMin = onAir ? pipe : 5;
            state.HandbrakesOn = handbrakes;
        }

        /// <summary>Takes the hand brakes off the train's own cars, the engineer's to drive now.</summary>
        private void ReleaseHandbrakes()
        {
            var released = 0;
            foreach (var car in Loco.trainset.cars)
            {
                var brakes = car != null ? car.brakeSystem : null;
                if (brakes == null || !brakes.hasHandbrake || brakes.handbrakePosition <= 0.01f)
                    continue;
                // A loco's by its cab control, so the wheel in the cab follows.
                var wheel = car.SimController != null && car.SimController.controlsOverrider != null ? car.SimController.controlsOverrider.Handbrake : null;
                if (wheel != null)
                    wheel.Set(0f);
                else
                    brakes.SetHandbrakePosition(0f);
                released++;
            }
            if (released > 0)
                Main.Log($"{Name}: took off {released} hand brake{(released == 1 ? "" : "s")} on its train");
        }

        /// <summary>The outer coupler reached going out of a car's front, along its train.</summary>
        private static Coupler FrontLeadsTo(TrainCar car)
        {
            var c = car.frontCoupler;
            for (var guard = 0; guard < 1000 && c.coupledTo != null; guard++)
                c = c.coupledTo.GetOppositeCoupler();
            return c;
        }

        private Coupler EndCoupler(ConsistEnd end) => Loco.trainset.GetEndmost(end == ConsistEnd.A);

        private void Couple(ConsistEnd end)
        {
            var coupler = EndCoupler(end);
            if (coupler.IsCoupled())
                return;
            var was = Loco.trainset.id;
            CouplerLogic.CoupleFirstInRange(coupler, 1.5f, releaseHandbrakesOnCoupledCars: true);
            if (!coupler.IsCoupled())
                return;
            // As the game's own couplings do: a loco coupled on is linked for multiple-unit
            // working (the levers then follow this loco's; linking moves them, so what the
            // engineer last set is forgotten).
            MultipleUnitModule.ConnectCablesOfConnectedCouplersIfMultipleUnitSupported(coupler, coupler.coupledTo);
            Controls.Release();
            TrainChanged(was);
        }

        /// <summary>
        /// A coupling or a cut makes the game a new trainset with a new id,
        /// and the switches the engineer holds and the cars it owns in the
        /// index are under the old one: they are handed on at once, or it
        /// would be barred from its own switches for a few seconds and see
        /// its own cars as another train's.
        /// </summary>
        private void TrainChanged(int was)
        {
            var now = Loco.trainset != null ? Loco.trainset.id : was;
            if (now != was)
                world.HandOn(was, now);
            world.ScanCars();
        }

        /// <summary>
        /// Cuts cars off the end of the consist away from the loco (the end
        /// with more cars beyond it), never the loco itself, and sets their
        /// hand brakes if asked.
        /// </summary>
        private void Cut(CutRequest cut)
        {
            var set = Loco.trainset;
            var was = set.id;
            var locoIndex = set.cars.IndexOf(Loco);
            // cars[0] is end A's car: the far end is whichever has more cars between it and the loco.
            var fromA = locoIndex >= set.cars.Count - 1 - locoIndex;
            var c = set.GetEndmost(fromA);
            var cars = new List<TrainCar>();
            for (var i = 0; i < cut.Cars; i++)
            {
                var car = c.train;
                if (car == Loco)
                    break;
                cars.Add(car);
                var inner = c.GetOppositeCoupler();
                if (inner.coupledTo == null)
                    break;
                if (i == cut.Cars - 1 || inner.coupledTo.train == Loco)
                {
                    Uncouple(inner);
                    break;
                }
                c = inner.coupledTo;
            }
            if (cut.SetHandbrakes)
            {
                foreach (var car in cars)
                    car.brakeSystem?.SetHandbrakePosition(1f);
            }
            Main.Notify($"AI engineer: {Name} left {cars.Count} car{(cars.Count == 1 ? "" : "s")}.");
            TrainChanged(was);
        }

        /// <summary>Uncouples as the game's own controls do: the multiple-unit cable across the cut comes apart too, or a loco left behind would go on answering this one's throttle.</summary>
        private static void Uncouple(Coupler coupler)
        {
            var other = coupler.coupledTo;
            CouplerLogic.Uncouple(coupler, applyHandbrakeToUncoupledCar: false);
            MultipleUnitModule.DisconnectCablesIfMultipleUnitSupported(coupler.train, coupler.isFrontCoupler, !coupler.isFrontCoupler);
            if (other != null)
                MultipleUnitModule.DisconnectCablesIfMultipleUnitSupported(other.train, other.isFrontCoupler, !other.isFrontCoupler);
        }

        /// <summary>
        /// The cars on the loco's other side: the side with fewer cars, which
        /// the engineer never cuts from (its cuts come off the far end).
        /// </summary>
        public List<TrainCar> OtherSide()
        {
            var list = new List<TrainCar>();
            var set = Loco.trainset;
            if (set == null)
                return list;
            var cars = set.cars;
            var locoIndex = cars.IndexOf(Loco);
            var fromA = locoIndex >= cars.Count - 1 - locoIndex;
            if (fromA)
            {
                for (var k = locoIndex + 1; k < cars.Count; k++)
                    list.Add(cars[k]);
            }
            else
            {
                for (var k = locoIndex - 1; k >= 0; k--)
                    list.Add(cars[k]);
            }
            return list;
        }

        /// <summary>Uncouples every car on the loco's other side (<see cref="OtherSide"/>), hand brakes on if asked; how many came off.</summary>
        public int CutOtherSide(bool setHandbrakes)
        {
            var cars = OtherSide();
            if (cars.Count == 0)
                return 0;
            var next = cars[0];
            var coupler = Loco.frontCoupler != null && Loco.frontCoupler.coupledTo?.train == next ? Loco.frontCoupler
                : Loco.rearCoupler != null && Loco.rearCoupler.coupledTo?.train == next ? Loco.rearCoupler : null;
            if (coupler == null)
                return 0;
            var was = Loco.trainset.id;
            Uncouple(coupler);
            if (setHandbrakes)
            {
                foreach (var car in cars)
                    car.brakeSystem?.SetHandbrakePosition(1f);
            }
            TrainChanged(was);
            return cars.Count;
        }

        internal static string WhyUnsupported(TrainCar loco)
        {
            var flow = loco.SimController?.simFlow;
            if (flow == null)
                return "has no controls the engineer can work";
            foreach (var comp in flow.OrderedSimComps)
            {
                if (comp is ManualTransmissionInput)
                    return "has a manual gearbox, not supported yet";
            }
            return null;
        }
    }
}
