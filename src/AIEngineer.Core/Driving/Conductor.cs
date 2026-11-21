using System;
using System.Collections.Generic;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    public enum ConductorState
    {
        /// <summary>No conductor on this train.</summary>
        None,

        /// <summary>A route is shown, waiting for the word. Nothing is touched.</summary>
        Planned,

        /// <summary>Following the train and lining the switches ahead of it.</summary>
        Executing,

        /// <summary>The train is there; the result stays until dismissed.</summary>
        Arrived,
    }

    /// <summary>What pressing Execute came to.</summary>
    public enum ConductorExecution
    {
        /// <summary>The conductor is at work.</summary>
        Started,

        /// <summary>The route has changed since it was shown: the new one is shown, and waits for the word.</summary>
        RouteChanged,

        /// <summary>No route from where the train is now.</summary>
        NoRoute,

        /// <summary>There was no plan to execute.</summary>
        NotPlanned,
    }

    public enum ConductorCallLevel
    {
        /// <summary>Worth knowing.</summary>
        Know,

        /// <summary>Something the driver has to do.</summary>
        Do,

        /// <summary>The train must stop short of something.</summary>
        Stop,
    }

    public enum ConductorCallKind
    {
        None,
        Clear,
        NewRoute,
        SwitchLeft,
        Arrived,
        OffRoute,
        NoRoute,
        Derailed,
        Reverse,
        PullAhead,
        PullClear,
        StopAtDestination,
        SwitchBlocked,
        SwitchFouled,
        CarsAhead,
    }

    /// <summary>One thing the conductor tells the driver.</summary>
    public readonly struct ConductorCall
    {
        public readonly ConductorCallKind Kind;

        /// <summary>How far off what it is about lies, m; for a new route, the route's length.</summary>
        public readonly double Distance;

        /// <summary>The switch it is about; -1 for none. For a new route, its reversals.</summary>
        public readonly int Junction;

        public ConductorCall(ConductorCallKind kind, double distance = 0, int junction = -1)
        {
            Kind = kind;
            Distance = distance;
            Junction = junction;
        }

        public ConductorCallLevel Level
        {
            get
            {
                switch (Kind)
                {
                    case ConductorCallKind.SwitchBlocked:
                    case ConductorCallKind.SwitchFouled:
                    case ConductorCallKind.CarsAhead:
                    case ConductorCallKind.Derailed:
                        return ConductorCallLevel.Stop;
                    case ConductorCallKind.Reverse:
                    case ConductorCallKind.PullAhead:
                    case ConductorCallKind.PullClear:
                    case ConductorCallKind.StopAtDestination:
                    case ConductorCallKind.NoRoute:
                        return ConductorCallLevel.Do;
                    default:
                        return ConductorCallLevel.Know;
                }
            }
        }

        /// <summary>The call in words.</summary>
        /// <param name="switchName">A switch's name for the player.</param>
        /// <param name="destination">Where the train is bound, for the player.</param>
        public string Say(Func<int, string> switchName, string destination)
        {
            var place = destination ?? "the destination";
            var sw = Junction >= 0 && switchName != null ? switchName(Junction) : "a switch";
            switch (Kind)
            {
                case ConductorCallKind.Clear:
                    return "The line is clear";
                case ConductorCallKind.NewRoute:
                    return $"New route: {Far(Distance)}, {Junction} reversal{(Junction == 1 ? "" : "s")}";
                case ConductorCallKind.SwitchLeft:
                    return $"{sw} left as you set it";
                case ConductorCallKind.Arrived:
                    return $"Arrived at {place}";
                case ConductorCallKind.OffRoute:
                    return "Off the route; finding a new one";
                case ConductorCallKind.NoRoute:
                    return $"No route to {place} from here";
                case ConductorCallKind.Derailed:
                    return "Stop: the train is off the rails";
                case ConductorCallKind.Reverse:
                    return Distance > 2 ? $"Stop in {Far(Distance)}, then reverse" : "Stop here and reverse";
                case ConductorCallKind.PullAhead:
                    return $"Pull ahead {Far(Distance)} before reversing";
                case ConductorCallKind.PullClear:
                    return $"Pull ahead: the train is on {sw}";
                case ConductorCallKind.StopAtDestination:
                    return $"Stop at {place}, {Far(Distance)}";
                case ConductorCallKind.SwitchBlocked:
                    return $"Stop: {sw} can't be set, {Far(Distance)}";
                case ConductorCallKind.SwitchFouled:
                    return $"Stop: cars foul {sw}, {Far(Distance)}";
                case ConductorCallKind.CarsAhead:
                    return $"Stop: cars ahead, {Far(Distance)}";
                default:
                    return "";
            }
        }

        /// <summary>A distance as the player reads it.</summary>
        public static string Far(double meters) =>
            meters >= 1000 ? $"{meters / 1000:F1} km" : $"{Math.Max(0, meters):F0} m";
    }

    public sealed class Conductor
    {
        private const double LocateTolerance = 3;

        /// <summary>A new plan as good as the one shown: no more than this much longer or shorter, m, with the same reversals.</summary>
        public const double SameRouteLength = 50;

        /// <summary>Standing this close to the end of a leg that reverses counts as at the reversal, m.</summary>
        public const double ReversalWindow = 15;

        /// <summary>Standing this close to a stopping place counts as there, m.</summary>
        public const double ArrivalWindow = 30;

        /// <summary>The train's length changing by more than this means cars came or went, m.</summary>
        private const double LengthChange = 1;

        /// <summary>How long before the same call goes to the notification line again, s.</summary>
        private const double NoticeRepeat = 30;

        /// <summary>Coming down a branch inside the fouling reach, cars on another branch this much farther from the points than the leading end still count as ahead of it, m.</summary>
        private const double TrailingMargin = 4;

        private readonly EngineerSettings settings;
        private readonly Func<int, int, bool> allowSwitch;

        // Switches on the route the conductor has seen lie right while it held them: one that
        // changes after that was thrown by someone outside the holds.
        private readonly Dictionary<int, int> relied = new Dictionary<int, int>();

        // How the player has set switches against the route, for this execution.
        private readonly Dictionary<int, int> playerSwitches = new Dictionary<int, int>();
        private readonly List<int> changed = new List<int>();
        private readonly Queue<ConductorCall> notices = new Queue<ConductorCall>();
        private readonly Dictionary<long, double> noticedAt = new Dictionary<long, double>();

        private Route route;
        private bool arriveAnywhereOnTrack;
        private int legIndex;
        private int pieceHint;
        private double clock;
        private double lastPlanAttempt = double.NegativeInfinity;
        private int planFailures;
        private int failedStamp;
        private int plannedCars;
        private double plannedLength;

        public Conductor(EngineerSettings settings = null)
        {
            this.settings = settings ?? new EngineerSettings();
            allowSwitch = (junction, branch) => !playerSwitches.TryGetValue(junction, out var lie) || lie == branch;
        }

        public ConductorState State { get; private set; }

        /// <summary>Where the train is bound; null with no plan.</summary>
        public Waypoint Destination { get; private set; }

        /// <summary>The route; null with none, and while a new one is being found.</summary>
        public Route Route => route;

        public int LegIndex => legIndex;

        /// <summary>The leading end's distance along the current leg, m.</summary>
        public double Progress { get; private set; }

        /// <summary>The running left to the destination, m.</summary>
        public double Remaining { get; private set; }

        /// <summary>What matters most now.</summary>
        public ConductorCall Call { get; private set; }

        /// <summary>What comes after it; <see cref="ConductorCallKind.None"/> for nothing.</summary>
        public ConductorCall NextCall { get; private set; }

        /// <summary>How many switches the route shown needs thrown, as they lie now.</summary>
        public int SwitchesToThrow { get; private set; }

        /// <summary>How many it has thrown in this execution.</summary>
        public int SwitchesThrown { get; private set; }

        /// <summary>Why the route shown isn't quite what was asked; null when it is.</summary>
        public string PlanNote { get; private set; }

        /// <summary>Takes the next call meant for the notification line.</summary>
        public bool TryTakeNotice(out ConductorCall call)
        {
            if (notices.Count > 0)
            {
                call = notices.Dequeue();
                return true;
            }
            call = default;
            return false;
        }

        // ---- The player's word ----

        /// <summary>
        /// Plans a route to show. Nothing on the railroad changes. False
        /// with no route: the conductor has no plan, and says so.
        /// </summary>
        /// <param name="anywhereOnTrack">The whole train standing on the destination's track is arrival (a named track, not a point on one).</param>
        public bool Plan(Waypoint destination, bool anywhereOnTrack, TrainState train, IEngineerWorld world)
        {
            StandDown();
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
            arriveAnywhereOnTrack = anywhereOnTrack;
            if (!FindRoute(train, world))
            {
                Destination = null;
                Call = new ConductorCall(ConductorCallKind.NoRoute);
                return false;
            }
            State = ConductorState.Planned;
            Call = new ConductorCall(ConductorCallKind.Clear);
            CountSwitches(world);
            return true;
        }

        /// <summary>
        /// The word to go. The route is worked out again from the switches
        /// and cars as they are now, and a route that has changed is shown
        /// first: Execute never starts one the player hasn't seen.
        /// </summary>
        public ConductorExecution Execute(TrainState train, IEngineerWorld world)
        {
            if (State != ConductorState.Planned)
                return ConductorExecution.NotPlanned;
            var shown = route;
            if (!FindRoute(train, world))
            {
                route = shown;
                Call = new ConductorCall(ConductorCallKind.NoRoute);
                return ConductorExecution.NoRoute;
            }
            CountSwitches(world);
            if (route.Reversals != shown.Reversals || Math.Abs(route.Length - shown.Length) > SameRouteLength)
            {
                Call = new ConductorCall(ConductorCallKind.NewRoute, route.Length, route.Reversals);
                return ConductorExecution.RouteChanged;
            }
            State = ConductorState.Executing;
            SwitchesThrown = 0;
            playerSwitches.Clear();
            noticedAt.Clear();
            Call = new ConductorCall(ConductorCallKind.Clear);
            return ConductorExecution.Started;
        }

        /// <summary>Ends it. Every switch stays as it lies.</summary>
        public void StandDown()
        {
            State = ConductorState.None;
            Destination = null;
            route = null;
            legIndex = 0;
            pieceHint = 0;
            planFailures = 0;
            lastPlanAttempt = double.NegativeInfinity;
            relied.Clear();
            playerSwitches.Clear();
            noticedAt.Clear();
            notices.Clear();
            Call = default;
            NextCall = default;
            PlanNote = null;
            SwitchesToThrow = 0;
            SwitchesThrown = 0;
            Progress = 0;
            Remaining = 0;
        }

        // ---- At work ----

        public void Tick(double dt, TrainState train, IEngineerWorld world)
        {
            if (State != ConductorState.Executing)
                return;
            clock += dt;
            if (!train.OnTrack)
            {
                // Nothing is thrown for a train that isn't on the rails; it goes on if rerailed.
                Say(new ConductorCall(ConductorCallKind.Derailed), default);
                return;
            }
            WatchSwitches(world);
            if (route != null && (train.CarCount != plannedCars || Math.Abs(train.Length - plannedLength) > LengthChange))
                route = null;
            if (route == null && !Replan(train, world))
                return;
            if (!Follow(train, world) && Replan(train, world))
                Follow(train, world);
        }

        /// <summary>
        /// Whether a switch the conductor was relying on has been thrown, or
        /// one the player set has been set again: the player's say goes, for
        /// the rest of this execution, and the route is found afresh around it.
        /// </summary>
        private void WatchSwitches(IEngineerWorld world)
        {
            changed.Clear();
            foreach (var kv in relied)
            {
                if (world.SelectedBranch(kv.Key) != kv.Value)
                    changed.Add(kv.Key);
            }
            foreach (var kv in playerSwitches)
            {
                if (world.SelectedBranch(kv.Key) != kv.Value && !relied.ContainsKey(kv.Key))
                    changed.Add(kv.Key);
            }
            if (changed.Count == 0)
                return;
            foreach (var junction in changed)
            {
                relied.Remove(junction);
                playerSwitches[junction] = world.SelectedBranch(junction);
                Notice(new ConductorCall(ConductorCallKind.SwitchLeft, 0, junction));
            }
            route = null;
        }

        private bool Replan(TrainState train, IEngineerWorld world)
        {
            var since = clock - lastPlanAttempt;
            var wait = planFailures > 0
                ? Math.Min(settings.ReplanIntervalMax, settings.ReplanInterval * Math.Pow(2, Math.Min(planFailures - 1, 16)))
                : settings.ReplanInterval;
            var soonest = since >= wait
                || (planFailures > 0 && since >= settings.ReplanInterval && world.Occupancy.StandingStamp != failedStamp);
            if (!soonest)
            {
                Say(new ConductorCall(planFailures > 0 ? ConductorCallKind.NoRoute : ConductorCallKind.OffRoute), default);
                return false;
            }
            if (!FindRoute(train, world))
            {
                Say(new ConductorCall(ConductorCallKind.NoRoute), default);
                return false;
            }
            Notice(new ConductorCall(ConductorCallKind.NewRoute, route.Length, route.Reversals));
            return true;
        }

        private bool Follow(TrainState train, IEngineerWorld world)
        {
            var leg = route.Legs[legIndex];
            var towardA = train.SpeedTowardA;
            var speed = leg.Leader == ConsistEnd.A ? towardA : -towardA;
            var standing = Math.Abs(towardA) < Engineer.StandingSpeed;
            var located = Locate(leg, End(train, leg.Leader), ref pieceHint, out var progress);

            if (leg.EndsInReversal && legIndex + 1 < route.Legs.Count)
            {
                // Over to the next leg when the driver reverses, or stands at the reversal, with
                // the rear end on it: the rear has passed the switch the train comes back through.
                var next = route.Legs[legIndex + 1];
                var reversing = speed < -Engineer.WrongWaySpeed;
                var atEnd = located && progress >= leg.Length - ReversalWindow;
                if ((reversing || (standing && atEnd))
                    && next.TryLocate(End(train, next.Leader), 0, LocateTolerance, out var along, out var hint))
                {
                    legIndex++;
                    pieceHint = hint;
                    relied.Clear();
                    leg = next;
                    progress = along;
                    speed = -speed;
                    located = true;
                }
            }

            if (!located)
            {
                route = null;
                Say(new ConductorCall(ConductorCallKind.OffRoute), default);
                return false;
            }

            Progress = progress;
            Remaining = Math.Max(0, leg.Length - progress);
            for (var i = legIndex + 1; i < route.Legs.Count; i++)
                Remaining += route.Legs[i].Length;

            var lastLeg = legIndex == route.Legs.Count - 1;
            if (lastLeg && standing && HasArrived(leg, progress, train, world))
            {
                Arrive();
                return true;
            }

            var braking = BrakingOf(train);
            var stopping = braking.DistanceToSlow(Math.Max(speed, 0), 0);
            var reach = Math.Max(Engineer.SwitchReachMin, stopping + Engineer.SwitchReachMargin);
            var lookahead = Math.Max(300, stopping + 150);
            var owner = train.Owner;

            var toEnd = leg.Length - progress;
            var endCall = EndCall(leg, lastLeg, toEnd, standing);
            ConductorCall call = default;
            var bestStop = double.PositiveInfinity;
            void Consider(ConductorCall candidate, double stopDistance)
            {
                if (candidate.Kind == ConductorCallKind.None)
                    return;
                var d = Math.Max(0, stopDistance);
                if (call.Kind == ConductorCallKind.None || d < bestStop || (d == bestStop && candidate.Level > call.Level))
                {
                    call = candidate;
                    bestStop = d;
                }
            }

            // Not while the train runs away from the leg: the switches ahead aren't what it needs now.
            if (speed >= -Engineer.WrongWaySpeed)
            {
                var switchCall = LineSwitches(leg, progress, reach, owner, world, out var switchStop);
                Consider(switchCall, switchStop);
            }
            var fouledCall = FouledAhead(leg, progress, reach, owner, world, out var fouledStop);
            Consider(fouledCall, fouledStop);
            var cars = world.Occupancy.FirstAhead(leg, progress, pieceHint, lookahead, owner, out _);
            var gap = cars - progress - settings.CarGap;
            if (gap <= stopping + Engineer.SwitchReachMargin)
                Consider(new ConductorCall(ConductorCallKind.CarsAhead, Math.Max(0, gap)), gap);

            if (call.Kind == ConductorCallKind.None && toEnd <= lookahead)
                call = endCall;
            if (call.Kind == ConductorCallKind.None)
                call = new ConductorCall(ConductorCallKind.Clear);
            Say(call, call.Kind == endCall.Kind ? default : endCall);
            return true;
        }

        /// <summary>
        /// Lines the switches in reach ahead of the leading end, under the
        /// engineers' rules. The call is for the first one it can't.
        /// </summary>
        private ConductorCall LineSwitches(RouteLeg leg, double progress, double reach, int owner, IEngineerWorld world, out double stopDistance)
        {
            var switches = leg.Switches;
            for (var i = 0; i < switches.Count; i++)
            {
                var sw = switches[i];
                if (sw.AtDistance <= progress)
                {
                    relied.Remove(sw.Junction);
                    continue;
                }
                if (sw.AtDistance > progress + reach)
                    break;
                if (Engineer.WantedSooner(switches, i, progress))
                    continue;
                var canThrow = world.CanThrow(sw.Junction, owner);
                if (world.SelectedBranch(sw.Junction) == sw.Branch)
                {
                    world.Reserve(sw.Junction, owner);
                    // Seen lying right under its own hold: from here a change is someone's choice.
                    if (canThrow)
                        relied[sw.Junction] = sw.Branch;
                    continue;
                }
                // Not with the leading end all but on it: the cars' places are half a second old.
                if (canThrow && sw.AtDistance > progress + 1 && !world.PointsOccupied(sw.Junction))
                {
                    world.Throw(sw.Junction, sw.Branch, owner);
                    relied[sw.Junction] = sw.Branch;
                    SwitchesThrown++;
                    continue;
                }
                // Only its own cars stand in the way of a switch it may otherwise throw.
                if (canThrow && world.PointsOccupied(sw.Junction))
                {
                    stopDistance = 0;
                    return new ConductorCall(ConductorCallKind.PullClear, 0, sw.Junction);
                }
                stopDistance = Math.Max(0, sw.AtDistance - settings.SwitchStandoff - progress);
                return new ConductorCall(ConductorCallKind.SwitchBlocked, Math.Max(0, sw.AtDistance - progress), sw.Junction);
            }
            stopDistance = double.PositiveInfinity;
            return default;
        }

        private ConductorCall FouledAhead(RouteLeg leg, double progress, double reach, int owner, IEngineerWorld world, out double stopDistance)
        {
            var switches = leg.Switches;
            ConductorCall best = default;
            stopDistance = double.PositiveInfinity;
            for (var i = 0; i < switches.Count; i++)
            {
                var sw = switches[i];
                if (sw.AtDistance <= progress)
                    continue;
                if (sw.AtDistance - Fouling.Reach > progress + reach)
                    break;
                var trailing = leg.Pieces[leg.PieceIndexAt(sw.AtDistance - 0.01)].Track != world.Graph.Junction(sw.Junction).StemTrack;
                if (!trailing)
                {
                    if (sw.AtDistance > progress + reach)
                        continue;
                    if (Fouling.OtherBranchesFouled(world.Graph, world.Occupancy, sw.Junction, sw.Branch, owner, Fouling.Reach, standingOnly: false))
                    {
                        var stop = Math.Max(0, sw.AtDistance - settings.CarGap - progress);
                        if (stop < stopDistance)
                        {
                            stopDistance = stop;
                            best = new ConductorCall(ConductorCallKind.SwitchFouled, stop, sw.Junction);
                        }
                    }
                    continue;
                }
                var checkReach = Math.Min(Fouling.Reach, sw.AtDistance - progress + TrailingMargin);
                if (Fouling.OtherBranchesFouled(world.Graph, world.Occupancy, sw.Junction, sw.Branch, owner, checkReach, standingOnly: false))
                {
                    var stop = Math.Max(0, sw.AtDistance - Fouling.Reach - settings.CarGap - progress);
                    if (stop < stopDistance)
                    {
                        stopDistance = stop;
                        best = new ConductorCall(ConductorCallKind.SwitchFouled, stop, sw.Junction);
                    }
                }
            }
            return best;
        }

        private static ConductorCall EndCall(RouteLeg leg, bool lastLeg, double toEnd, bool standing)
        {
            if (lastLeg)
                return new ConductorCall(ConductorCallKind.StopAtDestination, Math.Max(0, toEnd));
            // Stood at the reversal and still here: the rear isn't past the switch yet.
            if (standing && toEnd <= ReversalWindow)
                return new ConductorCall(ConductorCallKind.PullAhead, Math.Max(5, toEnd));
            return new ConductorCall(ConductorCallKind.Reverse, Math.Max(0, toEnd));
        }

        private bool HasArrived(RouteLeg leg, double progress, TrainState train, IEngineerWorld world)
        {
            var switches = leg.Switches;
            for (var i = switches.Count - 1; i >= 0; i--)
            {
                if (switches[i].AtDistance > progress)
                    return false;
            }
            if (switches.Count > 0 && world.PointsOccupied(switches[switches.Count - 1].Junction))
                return false;
            if (arriveAnywhereOnTrack)
            {
                var track = Destination.Position.Track;
                if (train.EndA.Track == track && train.EndB.Track == track)
                    return true;
                if (train.Length <= world.Graph.Length(track))
                    return false;
                return (train.EndA.Track == track || train.EndB.Track == track) && progress >= leg.Length - ArrivalWindow;
            }
            return progress >= leg.Length - ArrivalWindow;
        }

        private void Arrive()
        {
            State = ConductorState.Arrived;
            route = null;
            relied.Clear();
            Remaining = 0;
            Say(new ConductorCall(ConductorCallKind.Arrived), default);
            Notice(Call);
        }

        // ---- Routes ----

        private bool FindRoute(TrainState train, IEngineerWorld world)
        {
            lastPlanAttempt = clock;
            Destination.Resize(train);
            var wanted = Engineer.Leader(Destination.Arrival, train, Destination.IntoDeadEnd);
            // A moving train's route must begin in its direction of travel.
            ConsistEnd? first = null;
            if (Math.Abs(train.SpeedTowardA) >= Engineer.StandingSpeed)
                first = train.SpeedTowardA > 0 ? ConsistEnd.A : ConsistEnd.B;
            var plan = RoutePlanner.Plan(Destination, train, world, wanted, firstLeader: first, allowSwitch: allowSwitch);
            if (plan.Route == null)
            {
                planFailures++;
                failedStamp = world.Occupancy.StandingStamp;
                return false;
            }
            planFailures = 0;
            Destination.Choose(plan.GoalIndex);
            Destination.ArrivalMet = !plan.ArrivalMissed;
            PlanNote = plan.ArrivalMissed ? "It can't arrive the way round asked" : Destination.Goal.Instead;
            route = plan.Route;
            legIndex = 0;
            pieceHint = 0;
            plannedCars = train.CarCount;
            plannedLength = train.Length;
            relied.Clear();
            return true;
        }

        private void CountSwitches(IEngineerWorld world)
        {
            var count = 0;
            var length = 0.0;
            foreach (var leg in route.Legs)
            {
                length += leg.Length;
                foreach (var sw in leg.Switches)
                {
                    if (world.SelectedBranch(sw.Junction) != sw.Branch)
                        count++;
                }
            }
            SwitchesToThrow = count;
            Remaining = length;
        }

        /// <summary>
        /// Where the leading end is on the leg. Past the end still counts,
        /// on the leg's last track: a driver runs on before reversing, and a
        /// stop may run a little long.
        /// </summary>
        private static bool Locate(RouteLeg leg, TrackPosition front, ref int hint, out double progress)
        {
            if (leg.TryLocate(front, hint, LocateTolerance, out progress, out var piece))
            {
                hint = piece;
                return true;
            }
            var past = leg.PastEnd(front);
            if (!double.IsNaN(past) && (leg.EndsInReversal || past <= Engineer.OvershootLimit))
            {
                progress = leg.Length + past;
                return true;
            }
            return false;
        }

        private static TrackPosition End(TrainState train, ConsistEnd end) => end == ConsistEnd.A ? train.EndA : train.EndB;

        private BrakingModel BrakingOf(TrainState train)
        {
            if (!train.Controls.HasTrainBrake)
                return settings.LightBraking;
            return train.Controls.SlowAir ? settings.SlowAirBraking : settings.TrainBraking;
        }

        // ---- Saying it ----

        private void Say(ConductorCall call, ConductorCall next)
        {
            Call = call;
            NextCall = next;
            if (call.Level != ConductorCallLevel.Know || call.Kind == ConductorCallKind.OffRoute)
                Notice(call);
        }

        /// <summary>Queues a call for the notification line, once, and again only after a while.</summary>
        private void Notice(ConductorCall call)
        {
            var key = ((long)call.Kind << 32) | (uint)call.Junction;
            if (noticedAt.TryGetValue(key, out var at) && clock - at < NoticeRepeat)
                return;
            noticedAt[key] = clock;
            notices.Enqueue(call);
        }
    }
}
