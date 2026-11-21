using System;
using System.Collections.Generic;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>The consist as the game side sees it each tick.</summary>
    public struct TrainState
    {
        /// <summary>End A's outer coupler and the direction out of the consist there.</summary>
        public TrackPosition EndA;
        public TrackDirection OutwardA;

        /// <summary>End B's outer coupler and the direction out of the consist there.</summary>
        public TrackPosition EndB;
        public TrackDirection OutwardB;

        /// <summary>Coupler to coupler, m.</summary>
        public double Length;

        /// <summary>Speed toward end A, m/s; negative toward B.</summary>
        public double SpeedTowardA;

        public int CarCount;

        /// <summary>How many of those cars are power (locos, tenders, slugs); the engineer's own loco is always one, so 0 reads as 1.</summary>
        public int PowerCars;

        /// <summary>Whether the car at the end away from the loco is power too: then neither way is shoving.</summary>
        public bool PowerAtFarEnd;

        /// <summary>The cars the power is moving: what a train carries, and all that can be cut off.</summary>
        public int WagonCount => Math.Max(0, CarCount - Math.Max(1, PowerCars));

        /// <summary>Power alone: a loco, with any tender, second unit, or slug.</summary>
        public bool LightEngine => WagonCount == 0;

        /// <summary>The end whose leading is shoving cars; null running light or with power at both ends.</summary>
        public ConsistEnd? ShovingEnd => LightEngine || PowerAtFarEnd ? (ConsistEnd?)null : LocoEnd.Other();

        /// <summary>The end the reverser drives toward as it stands now; null in neutral or unknown.</summary>
        public ConsistEnd? Reverser;

        /// <summary>The consist end lying ahead of the loco's front (the way its reverser calls forward).</summary>
        public ConsistEnd LocoFront;

        /// <summary>The consist end nearer the loco; with no cars, either.</summary>
        public ConsistEnd LocoEnd;

        /// <summary>Whose the consist's cars are in the occupancy index (it changes as cars couple and part).</summary>
        public int Owner;

        /// <summary>Whether every car is on the rails and found on the network.</summary>
        public bool OnTrack;

        /// <summary>Whether the loco can move under power (engine running, or able to be started).</summary>
        public bool CanMove;

        /// <summary>Whether a loco's driven wheels are slipping.</summary>
        public bool Slipping;

        /// <summary>The loco's traction motor temperature, °C; 0 if it has none.</summary>
        public double MotorHeat;

        /// <summary>The lead loco's driving force as a share of the rail's grip; 0 when unknown.</summary>
        public double GripShare;

        /// <summary>The loco's traction generator current as a share of its limit; 0 if it has none.</summary>
        public double MotorAmpsShare;

        /// <summary>How much higher end A stands than end B, over the train's length: the grade it stands on toward A.</summary>
        public double RiseTowardA;

        /// <summary>Whether the loco runs on steam, so there are no motors to rest.</summary>
        public bool Steam;

        public ControlCapabilities Controls;

        /// <summary>Whether the air figures below were read: the train has cars with brakes.</summary>
        public bool AirKnown;

        /// <summary>The highest brake cylinder pressure among the cars (not the locos, whose own brake holds them), bar gauge.</summary>
        public double CarCylinderMax;

        /// <summary>The lowest brake pipe pressure among the cars, bar gauge.</summary>
        public double CarPipeMin;

        /// <summary>How many of the cars have a hand brake on.</summary>
        public int HandbrakesOn;

        /// <summary>The locos' share of the train's mass, 0 to 1 (1 running light; 0 when unknown).</summary>
        public double LocoMassShare;
    }

    /// <summary>What the engineer needs of the world: the network, the switches, and who stands where.</summary>
    public interface IEngineerWorld
    {
        TrackGraph Graph { get; }

        Router Router { get; }

        OccupancyIndex Occupancy { get; }

        int SelectedBranch(int junction);

        bool CanThrow(int junction, int owner);

        /// <summary>
        /// Whether another train's cars stand at rest across the switch's
        /// points: it can't be thrown until someone moves them, where a
        /// hold or a passing train only wants waiting for.
        /// </summary>
        bool Jammed(int junction, int owner);

        /// <summary>
        /// Whether anything at all, the engineer's own train included, is
        /// across the switch's points: it must not be thrown this moment
        /// (the game has no interlocking; a switch thrown under a car
        /// splits the train).
        /// </summary>
        bool PointsOccupied(int junction);

        void Throw(int junction, int branch, int owner);

        void Reserve(int junction, int owner);
    }

    /// <summary>Which switches a route may count on, as every route request asks them.</summary>
    public static class SwitchRules
    {
        /// <summary>What a switch that can't be thrown just now costs a route, m: less than any real way round, so the train waits at it.</summary>
        public const double HeldCost = 300;

        /// <summary>
        /// Whether a route may pass the switch by this branch at all: as it
        /// lies, or thrown if no cars at rest jam its points; and never with
        /// cars at rest fouling it from another branch, which the engineer
        /// would stop short of for good (<see cref="Fouling"/>).
        /// </summary>
        public static bool Usable(IEngineerWorld world, int junction, int branch, int owner) =>
            (world.SelectedBranch(junction) == branch || !world.Jammed(junction, owner)) && !Fouled(world, junction, branch, owner);

        public static bool Fouled(IEngineerWorld world, int junction, int branch, int owner) =>
            Fouling.OtherBranchesFouled(world.Graph, world.Occupancy, junction, branch, owner, standingOnly: true);

        /// <summary>The cost of a switch that would have to be waited for: held by another engineer, or with a train moving near it.</summary>
        public static double Penalty(IEngineerWorld world, int junction, int branch, int owner) =>
            world.SelectedBranch(junction) == branch || world.CanThrow(junction, owner) ? 0 : HeldCost;
    }

    /// <summary>What the engineer does this tick.</summary>
    public struct EngineerOutput
    {
        /// <summary>The end to drive toward; null leaves the reverser where it is.</summary>
        public ConsistEnd? Toward;

        public ControlOutput Controls;

        /// <summary>Set when the engineer wants cars cut off this tick.</summary>
        public CutRequest? Cut;

        /// <summary>Set when the engineer wants to couple to what its leading end touches.</summary>
        public ConsistEnd? CoupleAt;

        /// <summary>Whether the engineer has hold of the controls at all.</summary>
        public bool Active;

        /// <summary>Set when the train's own cars have hand brakes on that must come off before it can start.</summary>
        public bool ReleaseHandbrakes;
    }

    public sealed class EngineerSettings
    {
        /// <summary>The top speed in Waypoints mode, m/s.</summary>
        public double MaxSpeed = 50 / 3.6;

        /// <summary>The top speed for Yard moves and the last stretch to a coupling, m/s.</summary>
        public double YardSpeed = 12 / 3.6;

        /// <summary>The speed to touch cars at when coupling, m/s.</summary>
        public double CouplingSpeed = 0.8;

        /// <summary>
        /// The most a coupling's last 50 m are run at, m/s (a fast walk):
        /// slow enough that a brake that is late, or a grade, still leaves
        /// the train at coupling speed when it touches.
        /// </summary>
        public double CouplingRunInSpeed = 1.6;

        /// <summary>How far short of standing cars the engineer stops, m.</summary>
        public double CarGap = 8;

        /// <summary>How far short of a switch it can't set it stops, m.</summary>
        public double SwitchStandoff = 15;

        /// <summary>How far short of a buffer stop it stops when running on the switches as set, m.</summary>
        public double BufferStandoff = 3;

        /// <summary>The limit through any switch, m/s; zero for none beyond the track's own.</summary>
        public double JunctionSpeed = 0;

        /// <summary>The planned deceleration and brake delay with cars, and running light.</summary>
        public BrakingModel TrainBraking = new BrakingModel(0.25, 4);
        public BrakingModel LightBraking = new BrakingModel(0.45, 1.5);

        /// <summary>
        /// The same with cars on Realistic Brakes: an application takes
        /// seconds to reach the cars, and a loaded car on American
        /// equipment brakes with its empty force (about 0.18 m/s² at full
        /// service on Period loading).
        /// </summary>
        public BrakingModel SlowAirBraking = new BrakingModel(0.12, 10);

        /// <summary>The cars' brake pipe must be up to this before the train starts, bar gauge (charged is 5).</summary>
        public double ChargedPipe = 4.2;

        /// <summary>A car's cylinders under this count as released, bar gauge (Realistic Brakes brakes nothing below 0.5).</summary>
        public double ReleasedCylinder = 0.35;

        /// <summary>Waiting longer than this for the brakes, s, the engineer says so: a hose apart or an angle cock shut.</summary>
        public double AirWaitWarning = 600;

        /// <summary>
        /// A brake cylinder left this full (bar) with the line charged and the
        /// hand brakes off, and no lower for <see cref="LingerSeconds"/>, is a
        /// brake that won't come off all the way (Realistic Brakes, a car that
        /// wants bleeding): the train goes anyway, and says so.
        /// </summary>
        public double LingeringCylinder = 1.0;

        public double LingerSeconds = 90;

        /// <summary>
        /// Seconds before a second attempt to plan when no route was found;
        /// each failure after doubles the wait, up to
        /// <see cref="ReplanIntervalMax"/> (a search that fails is the
        /// dearest kind: it looks everywhere).
        /// </summary>
        public double ReplanInterval = 3;
        public double ReplanIntervalMax = 60;

        /// <summary>Seconds stopped short of something before trying another route.</summary>
        public double BlockedReplanAfter = 20;
    }

    /// <summary>
    /// One AI engineer, for one consist. Engine-free: the game side feeds
    /// it the train's state a few times a second and applies what it
    /// returns. Owns its orders, its route, and the leg it is on.
    /// </summary>
    public sealed class Engineer
    {
        private const double LocateTolerance = 3;

        private readonly List<Waypoint> queue = new List<Waypoint>();
        private readonly SpeedController controller = new SpeedController();

        /// <summary>The speed controller's tuning (the self-test compares settings by it).</summary>
        public ControlTuning Tuning => controller.Tuning;
        private int owner;

        private Route route;
        private int legIndex;
        private SpeedPlanner planner;
        private int pieceHint;
        private double clock;
        private double lastPlanAttempt = double.NegativeInfinity;
        private int planFailures;
        private int failedStamp;
        private double blockedSince = double.NaN;
        private bool avoidOccupied;
        private double waitUntil = double.NaN;
        private int carsAtOrderStart = -1;

        /// <summary>The Couple order at the head of the queue has coupled: it finishes where the train stands.</summary>
        private bool coupledHere;
        private bool actionIssued;
        private double waypointAt;
        private double couplingSince = double.NaN;
        private double airWaitSince = double.NaN;
        private bool airWarned;

        // The end the reverser was last set toward; null when unknown (after Off).
        private ConsistEnd? lastToward;

        /// <summary>How far past a Couple waypoint the engineer looks for cars, m: ten car lengths.</summary>
        public const double CoupleSearch = 122;

        /// <summary>How far short of a Couple waypoint cars still count as the ones to couple to, m.</summary>
        public const double CoupleReachBack = 40;

        /// <summary>Within this of the cars, the couplers are in reach, m.</summary>
        public const double CoupleReach = 1.2;

        /// <summary>Seconds in reach without coupling before giving up.</summary>
        public const double CouplingTimeout = 10;

        /// <summary>Within this of the cars a coupling's run in is at <see cref="EngineerSettings.CouplingRunInSpeed"/> or less, and held closely to its target, m.</summary>
        public const double CoupleRunIn = 50;

        /// <summary>The run in is down to coupling speed this far short of the cars, m.</summary>
        public const double CoupleSettle = 5;

        /// <summary>A stop that ran past its waypoint by no more than this still counts as there, m: further, and it plans the way back.</summary>
        public const double OvershootLimit = 30;

        /// <summary>Switches are set within the braking distance plus this, m, and never further out than needed.</summary>
        public const double SwitchReachMargin = 100;
        public const double SwitchReachMin = 150;

        /// <summary>Moving faster than this against the way it should go, m/s, the engineer stops the train first.</summary>
        public const double WrongWaySpeed = 0.3;

        /// <summary>Slower than this the train stands, and the reverser may be moved, m/s.</summary>
        public const double StandingSpeed = 0.05;

        // Road and Yard moves: a leg walked along the switches as set.
        private RouteLeg walk;
        private WalkEnd walkEnd;
        private SpeedPlanner walkPlanner;
        private int walkHint;
        private double walkedAt = double.NegativeInfinity;

        public Engineer(EngineerSettings settings = null)
        {
            Settings = settings ?? new EngineerSettings();
        }

        public EngineerSettings Settings { get; }

        public EngineerMode Mode { get; private set; }

        /// <summary>The end Road and Yard moves head for.</summary>
        public ConsistEnd Toward { get; private set; } = ConsistEnd.A;

        /// <summary>Road mode's speed, m/s.</summary>
        public double RoadSpeed { get; private set; }

        /// <summary>Yard mode's distance still to go when ordered, m.</summary>
        public double YardDistance { get; private set; }

        public IReadOnlyList<Waypoint> Queue => queue;

        public Route Route => route;

        public int LegIndex => legIndex;

        /// <summary>The leading end's distance along the current leg (or walk).</summary>
        public double Progress { get; private set; }

        /// <summary>Where it means to stop on the current leg (or walk), m along it.</summary>
        public double StopAt { get; private set; }

        public double TargetSpeed { get; private set; }

        public string Status { get; private set; } = "Off";

        // ---- Orders ----

        public void SetOff()
        {
            Mode = EngineerMode.Off;
            lastToward = null;
            ClearRoute();
            Status = "Off";
        }

        /// <summary>A helper on a doubleheaded train: the queue let go; the game side works the controls and says what it is doing.</summary>
        public void SetHelper(string status)
        {
            Mode = EngineerMode.Helper;
            queue.Clear();
            ClearRoute();
            Status = status;
        }

        /// <summary>What a helper is doing, as its driver says.</summary>
        public void SetHelperStatus(string status)
        {
            if (Mode == EngineerMode.Helper)
                Status = status;
        }

        public void SetRoad(ConsistEnd toward, double speed)
        {
            RoadSpeed = Math.Max(0, speed);
            if (Mode == EngineerMode.Road && Toward == toward)
                return;
            Mode = EngineerMode.Road;
            Toward = toward;
            ClearRoute();
        }

        /// <summary>
        /// Re-aims a Road or Yard move at the same way under another name:
        /// a coupling can swap which end of the consist is A. A yard move
        /// keeps only the distance it has still to go.
        /// </summary>
        public void SetToward(ConsistEnd toward)
        {
            if (toward == Toward || (Mode != EngineerMode.Road && Mode != EngineerMode.Yard))
                return;
            if (Mode == EngineerMode.Yard && walk != null)
                YardDistance = Math.Max(0, YardDistance - Progress);
            Toward = toward;
            walk = null;
            walkPlanner = null;
            walkedAt = double.NegativeInfinity;
        }

        public void SetYard(ConsistEnd toward, double distance)
        {
            Mode = EngineerMode.Yard;
            Toward = toward;
            YardDistance = Math.Max(0, distance);
            ClearRoute();
        }

        /// <summary>Waypoints mode: works the queue, holding the train while it's empty.</summary>
        public void SetWaypoints()
        {
            if (Mode == EngineerMode.Waypoints)
                return;
            Mode = EngineerMode.Waypoints;
            ClearRoute();
        }

        /// <summary>Replaces the queue with one waypoint.</summary>
        public void GoTo(Waypoint waypoint)
        {
            queue.Clear();
            Enqueue(waypoint);
        }

        public void Enqueue(Waypoint waypoint)
        {
            if (waypoint == null)
                throw new ArgumentNullException(nameof(waypoint));
            var wasEmpty = queue.Count == 0;
            queue.Add(waypoint);
            if (Mode != EngineerMode.Waypoints)
            {
                Mode = EngineerMode.Waypoints;
                ClearRoute();
            }
            else if (wasEmpty)
            {
                ClearRoute();
            }
        }

        /// <summary>Puts an order ahead of the queue: the one at its head starts again after it.</summary>
        public void PutFirst(Waypoint waypoint)
        {
            if (waypoint == null)
                throw new ArgumentNullException(nameof(waypoint));
            queue.Insert(0, waypoint);
            if (Mode != EngineerMode.Waypoints)
                Mode = EngineerMode.Waypoints;
            ClearRoute();
        }

        /// <summary>Puts an order at a place in the queue; at its head, the order there starts again after it.</summary>
        public void InsertAt(int index, Waypoint waypoint)
        {
            if (waypoint == null)
                throw new ArgumentNullException(nameof(waypoint));
            index = Math.Max(0, Math.Min(queue.Count, index));
            if (index == 0)
            {
                PutFirst(waypoint);
                return;
            }
            queue.Insert(index, waypoint);
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= queue.Count)
                return;
            queue.RemoveAt(index);
            if (index == 0)
                ClearRoute();
        }

        public void ClearQueue()
        {
            queue.Clear();
            ClearRoute();
        }

        /// <summary>Forget the route so the next tick plans afresh (the network or the train changed).</summary>
        public void Replan()
        {
            route = null;
            planner = null;
            legIndex = 0;
            pieceHint = 0;
            lastPlanAttempt = double.NegativeInfinity;
            walkedAt = double.NegativeInfinity;
        }

        private void ClearRoute()
        {
            Replan();
            walk = null;
            walkPlanner = null;
            blockedSince = double.NaN;
            avoidOccupied = false;
            waitUntil = double.NaN;
            carsAtOrderStart = -1;
            coupledHere = false;
            actionIssued = false;
            couplingSince = double.NaN;
            planFailures = 0;
            airWarned = false;
            cantMoveStatus = null;
            controller.Reset();
            controller.ForgetTries();
        }

        /// <summary>Whether every car's brakes are off and the train line charged: the train may take power.</summary>
        public bool AirReady(TrainState train) =>
            !train.AirKnown
            || (train.HandbrakesOn == 0 && train.CarPipeMin >= Settings.ChargedPipe &&
                (train.CarCylinderMax < Settings.ReleasedCylinder || (lingerAccepted == train.CarCount && train.CarCylinderMax < Settings.LingeringCylinder)));

        // A brake that won't come off all the way, accepted for the train of this many cars (-1: none).
        private int lingerAccepted = -1;
        private double lingerSince = double.NaN;
        private double lingerLowest;

        /// <summary>What the engineer is waiting on while the brakes come off, for its status.</summary>
        private string AirStatus(TrainState train)
        {
            if (train.HandbrakesOn > 0)
                return $"Taking off {train.HandbrakesOn} hand brake{(train.HandbrakesOn == 1 ? "" : "s")}";
            if (train.CarPipeMin < Settings.ChargedPipe)
                return $"Charging the train line: {Math.Max(0, train.CarPipeMin):F1} of {Settings.ChargedPipe:F1} bar";
            return $"Releasing the brakes: {train.CarCylinderMax:F1} bar in the cylinders";
        }

        // ---- Ticking ----

        public EngineerOutput Tick(double dt, TrainState train, IEngineerWorld world)
        {
            clock += dt;
            owner = train.Owner;
            var output = new EngineerOutput { Active = Mode != EngineerMode.Off };
            // A helper's controls are worked on the game side, from the lead engine.
            if (Mode == EngineerMode.Off || Mode == EngineerMode.Helper)
                return output;
            if (!train.OnTrack)
            {
                Status = "Derailed or off the network";
                output.Controls = Hold(train);
                return output;
            }
            if (!train.CanMove)
            {
                Status = "Loco can't move (engine off?)";
                output.Controls = Hold(train);
                return output;
            }
            switch (Mode)
            {
                case EngineerMode.Road:
                case EngineerMode.Yard:
                    TickWalk(dt, train, world, ref output);
                    break;
                case EngineerMode.Waypoints:
                    TickWaypoints(dt, train, world, ref output);
                    break;
            }
            if (output.Toward.HasValue)
                lastToward = output.Toward;
            if (controller.CantMove != CantMove.No && (Mode == EngineerMode.Road || Mode == EngineerMode.Yard || Mode == EngineerMode.Waypoints))
                GiveUp(train, ref output);
            return output;
        }

        // ---- A train it can't move ----

        private CantMove gaveUp;
        private double gaveUpGrade;
        private bool gaveUpUnderWay;

        // What the engineer says it does, holding a train it gave up on, until its next order.
        private string cantMoveStatus;

        /// <summary>
        /// A fair try made (the sand, the easing, the take-up, rests for the
        /// motors) and the train not moving, or not keeping moving: the
        /// orders end, and the train is held on its brakes until the next.
        /// </summary>
        private void GiveUp(TrainState train, ref EngineerOutput output)
        {
            gaveUp = controller.CantMove;
            gaveUpUnderWay = controller.Progress > 50;
            var toward = output.Toward ?? lastToward ?? ConsistEnd.A;
            gaveUpGrade = toward == ConsistEnd.B ? -train.RiseTowardA : train.RiseTowardA;
            queue.Clear();
            Mode = EngineerMode.Waypoints;
            ClearRoute();
            cantMoveStatus = gaveUpUnderWay ? "Can't keep the train moving here" : "Can't get the train moving here";
            Status = cantMoveStatus;
            output.Controls = Hold(train);
        }

        /// <summary>
        /// Once, the tick after the engineer gave up on moving its train:
        /// what held it back, the grade uphill the way it was going, and
        /// whether it had been under way.
        /// </summary>
        public bool TakeGiveUp(out CantMove cause, out double grade, out bool underWay)
        {
            cause = gaveUp;
            grade = gaveUpGrade;
            underWay = gaveUpUnderWay;
            if (gaveUp == CantMove.No)
                return false;
            gaveUp = CantMove.No;
            return true;
        }

        private void TickWaypoints(double dt, TrainState train, IEngineerWorld world, ref EngineerOutput output)
        {
            if (Holding != null)
            {
                Status = Holding;
                output.Controls = Hold(train);
                return;
            }
            if (queue.Count == 0)
            {
                Status = cantMoveStatus ?? "Waiting for orders";
                output.Controls = Hold(train);
                return;
            }
            var wp = queue[0];
            if (carsAtOrderStart < 0)
                carsAtOrderStart = train.CarCount;

            if (wp.Action == WaypointAction.Couple && !wp.Here && (coupledHere || train.CarCount > carsAtOrderStart))
            {
                coupledHere = true;
                output.Controls = Hold(train);
                couplingSince = double.NaN;
                if (Math.Abs(train.SpeedTowardA) < StandingSpeed)
                    ArriveAt(wp, train, ref output);
                else
                    Status = "Coupled";
                return;
            }

            // An order carried out where the train stands: no route, once it stands.
            if (wp.Here)
            {
                output.Controls = Hold(train);
                if (Math.Abs(train.SpeedTowardA) < StandingSpeed)
                    ArriveAt(wp, train, ref output);
                else
                    Status = "Stopping to cut off cars";
                return;
            }

            // At the waypoint: carry out its action, wait, and move on.
            if (route != null && legIndex >= route.Legs.Count)
            {
                output.Controls = Hold(train);
                ArriveAt(wp, train, ref output);
                return;
            }

            if (route == null && !Plan(wp, train, world))
            {
                output.Controls = Hold(train);
                return;
            }

            var leg = route.Legs[legIndex];
            var leader = leg.Leader;
            var front = leader == ConsistEnd.A ? train.EndA : train.EndB;
            if (!leg.TryLocate(front, pieceHint, LocateTolerance, out var progress, out pieceHint))
            {
                // A stop that ran a little long is still the stop: the order is carried out
                // where the train stands, not chased back for (there is no route to a waypoint
                // under the train).
                var past = legIndex == route.Legs.Count - 1 ? leg.PastEnd(front) : double.NaN;
                if (past <= OvershootLimit)
                {
                    output.Controls = Hold(train);
                    if (Math.Abs(train.SpeedTowardA) < StandingSpeed)
                    {
                        legIndex = route.Legs.Count;
                        Note = $"stopped {past:F0} m past {wp.Label ?? "the waypoint"}";
                    }
                    Status = "Past the waypoint; stopping";
                    return;
                }
                // Off the planned path: a switch went the wrong way, or the train was moved.
                Status = "Off the route; replanning";
                Replan();
                output.Controls = Hold(train);
                return;
            }
            var speed = leader == ConsistEnd.A ? train.SpeedTowardA : -train.SpeedTowardA;
            if (MovingTheWrongWay(leader, speed, train, ref output))
                return;
            var braking = Braking(train);
            var lastLeg = legIndex == route.Legs.Count - 1;
            var coupling = lastLeg && wp.Action == WaypointAction.Couple;
            var legEnd = lastLeg ? waypointAt : leg.Length;
            var cap = Settings.MaxSpeed;
            var stopAt = legEnd;
            var reason = lastLeg ? "waypoint" : "reversal";
            var lookahead = Math.Max(300, braking.DistanceToSlow(Math.Max(speed, cap), 0) + 150);
            // Switches are set only as the train needs them: within its braking distance and a margin.
            var switchReach = Math.Max(SwitchReachMin, braking.DistanceToSlow(Math.Max(speed, 0), 0) + SwitchReachMargin);

            // Switches ahead: set them, or stop short of one that can't be.
            var waitingForSwitch = false;
            var legSwitches = leg.Switches;
            for (var i = 0; i < legSwitches.Count; i++)
            {
                var sw = legSwitches[i];
                if (sw.AtDistance <= progress || sw.AtDistance > progress + switchReach)
                    continue;
                // A leg that passes a switch twice (round a loop) wants it one way and then the
                // other: only the nearer pass is set, until the leading end has made it.
                if (WantedSooner(legSwitches, i, progress))
                    continue;
                if (world.SelectedBranch(sw.Junction) == sw.Branch)
                {
                    world.Reserve(sw.Junction, owner);
                    continue;
                }
                // Never under the train itself or with the leading end all but on the points:
                // the cars' places are half a second old, and round a loop the tail may still be on it.
                if (sw.AtDistance > progress + 1 && world.CanThrow(sw.Junction, owner) && !world.PointsOccupied(sw.Junction))
                {
                    world.Throw(sw.Junction, sw.Branch, owner);
                    continue;
                }
                var before = sw.AtDistance - Settings.SwitchStandoff;
                if (before < stopAt)
                {
                    stopAt = before;
                    reason = "a switch that can't be set";
                    waitingForSwitch = true;
                }
                break;
            }

            // Cars ahead: stop short of them, unless they're the cut to couple to.
            var range = coupling ? Math.Max(lookahead, leg.Length - progress + 1) : lookahead;
            var obstacle = world.Occupancy.FirstAhead(leg, progress, pieceHint, range, owner, out _);
            var target = coupling && !double.IsPositiveInfinity(obstacle) && obstacle >= waypointAt - CoupleReachBack;
            if (!target && obstacle - Settings.CarGap < stopAt)
            {
                stopAt = obstacle - Settings.CarGap;
                reason = "cars ahead";
            }
            var fouledStop = FouledSwitchAhead(leg, progress, lookahead, world, out var fouled);
            if (fouledStop < stopAt)
            {
                stopAt = fouledStop;
                reason = "cars fouling a switch";
                if (target && fouled < obstacle)
                    target = false;
            }

            Progress = progress;
            output.Toward = leader;
            if (target && waitingForSwitch && stopAt < obstacle)
            {
                // A switch it can't set comes first: wait there before running in to couple.
                target = false;
            }
            if (target)
            {
                TickCoupling(dt, train, wp, speed, progress, obstacle, cap, braking, ref output);
                return;
            }

            StopAt = stopAt;
            TargetSpeed = planner.TargetSpeed(progress, cap, stopAt, braking);
            var uphill = output.Toward == ConsistEnd.B ? -train.RiseTowardA : train.RiseTowardA;
            output.Controls = controller.Update(new ControlInput
            {
                Speed = speed,
                Target = TargetSpeed,
                TargetAhead = planner.TargetSpeed(progress + Math.Max(0, speed) * braking.Delay, cap, stopAt, braking),
                HasTargetAhead = true,
                ToStop = stopAt - progress,
                Dt = dt,
                BrakesDragging = !AirReady(train),
                Slipping = train.Slipping,
                MotorHeat = train.MotorHeat,
                Uphill = uphill,
                AmpsShare = train.MotorAmpsShare,
                GripShare = train.GripShare,
            }, Controls(train));
            if (WaitForAir(train, ref output))
                return;

            var stopped = Math.Abs(speed) < 0.05;
            var atStop = stopAt - progress <= Math.Max(2, controller.Tuning.FinalStopDistance + 0.5);
            // Cars standing on the waypoint, or just past it: alongside them is as near as it gets.
            var carsAtWaypoint = lastLeg && reason == "cars ahead"
                && (obstacle >= legEnd - 1 || world.Occupancy.IsOccupied(wp.Position.Track, wp.Position.Offset - 1, wp.Position.Offset + 1, owner));
            if (stopped && atStop && (stopAt >= legEnd - 0.01 || carsAtWaypoint))
            {
                blockedSince = double.NaN;
                legIndex++;
                pieceHint = 0;
                controller.Reset();
                if (legIndex < route.Legs.Count)
                {
                    var next = route.Legs[legIndex];
                    planner = BuildPlanner(world.Graph, next, train.Length);
                    AddLimitsUnderTrain(planner, world, next.Leader == ConsistEnd.A ? train.EndA : train.EndB,
                        next.Leader == ConsistEnd.A ? train.OutwardA : train.OutwardB, train.Length);
                    Status = "Reversing";
                }
                else
                {
                    Status = coupling ? "No cars to couple to at " + (wp.Label ?? "the waypoint") : "At " + (wp.Label ?? "waypoint");
                    if (coupling)
                    {
                        wp.Outcome = OrderOutcome.NoCars;
                        Note = "found no cars to couple to at " + (wp.Label ?? "the waypoint");
                    }
                }
                return;
            }

            if (stopped && atStop)
            {
                if (double.IsNaN(blockedSince))
                    blockedSince = clock;
                Status = "Waiting: " + reason;
                if (clock - blockedSince > Settings.BlockedReplanAfter)
                {
                    // Look for a way round whatever stands in the way.
                    blockedSince = double.NaN;
                    avoidOccupied = true;
                    Replan();
                }
                return;
            }
            blockedSince = double.NaN;
            if (waitingForSwitch)
                Status = "Stopping: " + reason;
            else
                SayRunning(wp, legIndex + 1, route.Legs.Count);
        }

        /// <summary>
        /// The run in to a coupling: the braking curve down to coupling
        /// speed at the cars, then on at that speed until the couplers meet,
        /// asking the game side to couple once they're in reach.
        /// </summary>
        private void TickCoupling(double dt, TrainState train, Waypoint wp, double speed, double progress, double obstacle,
            double cap, BrakingModel braking, ref EngineerOutput output)
        {
            var gap = obstacle - progress;
            StopAt = obstacle;
            if (train.CarCount > carsAtOrderStart)
            {
                legIndex = route.Legs.Count;
                couplingSince = double.NaN;
                Status = "Coupled";
                output.Controls = Hold(train);
                return;
            }
            if (gap <= CoupleReach)
                output.CoupleAt = output.Toward;
            // Down the braking curve to a fast walk for the run in, and to coupling speed a few
            // meters short of the cars; then on at that speed into them, held closely to it.
            var slowest = Math.Min(Settings.CouplingSpeed, cap);
            var runIn = Math.Max(slowest, Math.Min(Settings.CouplingRunInSpeed, Math.Min(Settings.YardSpeed, cap)));
            double Approach(double at, double gapThen)
            {
                var v = planner.TargetSpeed(at, cap, double.PositiveInfinity, braking);
                v = Math.Min(v, braking.SpeedToMeet(runIn, gapThen - CoupleRunIn));
                v = Math.Min(v, braking.SpeedToMeet(slowest, gapThen - CoupleSettle));
                return Math.Max(v, slowest);
            }
            TargetSpeed = Approach(progress, gap);
            var reach = Math.Max(0, speed) * braking.Delay;
            var uphill = output.Toward == ConsistEnd.B ? -train.RiseTowardA : train.RiseTowardA;
            output.Controls = gap <= 0.15
                ? Hold(train)
                : controller.Update(new ControlInput
                {
                    Speed = speed,
                    Target = TargetSpeed,
                    TargetAhead = Approach(progress + reach, gap - reach),
                    HasTargetAhead = true,
                    ToStop = double.PositiveInfinity,
                    Tight = gap <= CoupleRunIn + reach,
                    Dt = dt,
                    BrakesDragging = !AirReady(train),
                    Slipping = train.Slipping,
                    MotorHeat = train.MotorHeat,
                    Uphill = uphill,
                    AmpsShare = train.MotorAmpsShare,
                    GripShare = train.GripShare,
                }, Controls(train));
            if (WaitForAir(train, ref output))
            {
                // Held for its brakes within reach of the cars (it has just coupled a cold cut,
                // and the next stands a metre on): it isn't trying yet, so the time doesn't count.
                // In game, 2026-11-10, a coupling was given up while the train line charged.
                couplingSince = double.NaN;
                return;
            }
            if (gap > CoupleReach)
            {
                couplingSince = double.NaN;
            }
            else
            {
                if (double.IsNaN(couplingSince))
                    couplingSince = clock;
                if (clock - couplingSince > CouplingTimeout)
                {
                    // It won't take: say so and go on with the queue.
                    legIndex = route.Legs.Count;
                    couplingSince = double.NaN;
                    Status = "Couldn't couple at " + (wp.Label ?? "the waypoint");
                    wp.Outcome = OrderOutcome.NotCoupled;
                    Note = "couldn't couple at " + (wp.Label ?? "the waypoint") + ": the couplers met and wouldn't take";
                    output.Controls = Hold(train);
                    return;
                }
            }
            if (gap <= CoupleReach)
                Status = "Coupling";
            else
                Say(StatusKind.RunningIn, (int)Math.Round(gap));
        }

        private void ArriveAt(Waypoint wp, TrainState train, ref EngineerOutput output)
        {
            if (wp.Action == WaypointAction.Couple && (wp.CutAfterCoupling != null || wp.KeepCars.HasValue) && !actionIssued && carsAtOrderStart >= 0)
            {
                // Coupled to more than the order wants: cut the rest off again where they stand.
                var coupled = train.CarCount - carsAtOrderStart;
                var extra = wp.CutAfterCoupling != null ? wp.CutAfterCoupling() : coupled - wp.KeepCars.Value;
                extra = Math.Min(extra, train.WagonCount);
                actionIssued = true;
                if (coupled > 0 && extra > 0)
                {
                    output.Cut = new CutRequest(extra, setHandbrakes: true);
                    waitUntil = clock + Math.Max(2, wp.WaitSeconds);
                    Status = $"Leaving the {extra} car{(extra == 1 ? "" : "s")} it doesn't need";
                    return;
                }
            }
            if (wp.Action == WaypointAction.Uncouple && !actionIssued)
            {
                var cars = Math.Min(wp.CutCount != null ? wp.CutCount() : wp.CarCount, train.WagonCount);
                actionIssued = true;
                if (cars <= 0)
                {
                    // Nothing to cut: not done, and not to be reported as done.
                    wp.Outcome = OrderOutcome.NothingCut;
                    Note = "had no cars to leave at " + (wp.Label ?? "the waypoint");
                    queue.RemoveAt(0);
                    ClearRoute();
                    Status = queue.Count == 0 ? "Waiting for orders" : "Next order";
                    return;
                }
                output.Cut = new CutRequest(cars, wp.SetHandbrakes);
                waitUntil = clock + Math.Max(2, wp.WaitSeconds);
                Status = $"Cutting off {cars} car{(cars == 1 ? "" : "s")}";
                return;
            }
            if (double.IsNaN(waitUntil))
                waitUntil = clock + wp.WaitSeconds;
            if (clock < waitUntil)
            {
                if (wp.WaitSeconds > 0)
                    Status = $"At {wp.Label ?? "waypoint"}; waiting {waitUntil - clock:F0} s";
                return;
            }
            // Done with this order (a coupling that found nothing has said so already).
            if (wp.Outcome == OrderOutcome.None)
            {
                wp.Outcome = OrderOutcome.Done;
                if (wp.DoneNote != null)
                    Note = wp.DoneNote;
            }
            queue.RemoveAt(0);
            ClearRoute();
            Status = queue.Count == 0 ? "Waiting for orders" : "Next order";
        }

        private bool Plan(Waypoint wp, TrainState train, IEngineerWorld world)
        {
            // After a failure, again when the standing cars have changed (and no sooner than
            // the interval), or after a wait that doubles each time: a switch held by
            // another engineer, or a train that has moved off, shows only by looking.
            if (planFailures > 0)
            {
                var since = clock - lastPlanAttempt;
                var wait = Math.Min(Settings.ReplanIntervalMax, Settings.ReplanInterval * Math.Pow(2, Math.Min(planFailures - 1, 16)));
                if (since < Settings.ReplanInterval || (since < wait && world.Occupancy.StandingStamp == failedStamp))
                    return false;
            }
            lastPlanAttempt = clock;
            var avoid = avoidOccupied;
            wp.Resize(train);
            var wanted = Leader(wp.Arrival, train, wp.IntoDeadEnd);
            if (!TryPlan(wp, train, world, avoid, wanted, out var failure, out var missed))
            {
                Status = failure == RouteFailure.GoalUnderTrain ? "The waypoint is under the train"
                    : failure == RouteFailure.BadTrack ? "Train or waypoint not on the network"
                    : fouledOnTheWay ? "No route to the waypoint: cars foul a switch on the way"
                    : wrongWayOnly ? $"No route to the waypoint {Describe(wp.Arrival)}"
                    : "No route to the waypoint";
                planFailures++;
                failedStamp = world.Occupancy.StandingStamp;
                return false;
            }
            planFailures = 0;
            // Asked to arrive a certain way, or at a certain end, and no route does: go anyway, and say so.
            wp.ArrivalMet = !missed;
            var label = wp.Label ?? "the waypoint";
            string note = null;
            if (missed && IsFacing(wp))
            {
                // A coupling's facing is the default, no choice of the player's (no control sets it): logged, once an order.
                if (!ReferenceEquals(facingLoggedFor, wp))
                {
                    facingLoggedFor = wp;
                    LogNote = facingGivenUp.HasValue
                        ? $"coupling to {label} {Describe(wp.Arrival == Arrival.BackFirst ? Arrival.FrontFirst : Arrival.BackFirst, coupling: true)}: "
                          + $"turning it first would add about {Math.Max(0, Math.Round(facingGivenUp.Value.Meters / 100) * 100):F0} m and {Math.Max(0, facingGivenUp.Value.Reversals)} reversals"
                        : $"no way to turn it to couple to {label} {Describe(wp.Arrival, coupling: true)}; coupling anyway";
                }
            }
            else if (missed)
            {
                note = wp.Action == WaypointAction.Couple
                    ? $"no way to turn it to couple to {label} {Describe(wp.Arrival, coupling: true)}; coupling anyway"
                    : $"no route brings it onto {label} {Describe(wp.Arrival)}; going anyway";
            }
            var instead = wp.Goal.Instead;
            if (instead != null)
                note = note == null ? $"{label}: {instead}" : $"{label}: {instead}; and {note}";
            // Said once an order, not at every replan.
            if (note != null && !(ReferenceEquals(notedFor, wp) && note == lastNote))
                Note = note;
            notedFor = wp;
            lastNote = note;
            return true;
        }

        private Waypoint notedFor;
        private string lastNote;

        // Set by a search that was refused a switch for the cars fouling it: the likeliest reason it found no route.
        private bool fouledOnTheWay;

        // Set when an order that must arrive one way round found no route that does.
        private bool wrongWayOnly;

        // Set when a light loco couples facing the other way, as turning it would have cost more than the allowance: what turning adds.
        private (double Meters, int Reversals)? facingGivenUp;

        // The order whose facing has been logged: its numbers change as it replans, the line is written once.
        private Waypoint facingLoggedFor;

        internal static bool IsFacing(Waypoint wp) =>
            wp.Action == WaypointAction.Couple && (wp.Arrival == Arrival.BackFirst || wp.Arrival == Arrival.FrontFirst);

        /// <summary>The consist end an arrival asks to lead the last leg; null for any.</summary>
        public static ConsistEnd? Leader(Arrival arrival, TrainState train, bool intoDeadEnd = false)
        {
            var light = train.LightEngine;
            switch (arrival)
            {
                case Arrival.CarsFirstIntoDeadEnd:
                    return light || !intoDeadEnd ? (ConsistEnd?)null : train.LocoEnd.Other();
                case Arrival.CarsFirst:
                    return light ? (ConsistEnd?)null : train.LocoEnd.Other();
                case Arrival.LocoFirst:
                    return light ? (ConsistEnd?)null : train.LocoEnd;
                case Arrival.FrontFirst:
                    return train.LocoFront;
                case Arrival.BackFirst:
                    return train.LocoFront.Other();
                default:
                    return null;
            }
        }

        /// <summary>An arrival as the player reads it: "cars first"; for a coupling, which way the loco faces the cars.</summary>
        public static string Describe(Arrival arrival, bool coupling = false)
        {
            if (coupling && arrival == Arrival.BackFirst)
                return "facing away from them";
            if (coupling && arrival == Arrival.FrontFirst)
                return "facing them";
            switch (arrival)
            {
                case Arrival.CarsFirst:
                case Arrival.CarsFirstIntoDeadEnd:
                    return "cars first";
                case Arrival.LocoFirst:
                    return "loco first";
                case Arrival.FrontFirst:
                    return "front first";
                case Arrival.BackFirst:
                    return "back first";
                default:
                    return "either way";
            }
        }

        /// <summary>The last thing worth telling the player about an order (cleared when read).</summary>
        public string Note { get; set; }

        /// <summary>The last thing worth logging about an order but not telling the player (cleared when read).</summary>
        public string LogNote { get; set; }

        /// <summary>
        /// Why the engineer stands and starts no order for the moment, said
        /// as its status; null lets it work its queue. A job run sets it
        /// while its next moves are planned off the game's thread, so an
        /// order waiting behind the run doesn't set off in between.
        /// </summary>
        public string Holding { get; set; }

        /// <summary>Plans the order's cheapest route (<see cref="RoutePlanner"/>) and readies the engineer to run it.</summary>
        private bool TryPlan(Waypoint wp, TrainState train, IEngineerWorld world, bool avoid, ConsistEnd? lastLeader,
            out RouteFailure failure, out bool missed)
        {
            var plan = RoutePlanner.Plan(wp, train, world, lastLeader, avoid);
            fouledOnTheWay = plan.FouledOnTheWay;
            wrongWayOnly = plan.WrongWayOnly;
            facingGivenUp = plan.FacingGivenUp;
            failure = plan.Failure;
            missed = plan.ArrivalMissed;
            route = plan.Route;
            if (route == null)
                return false;
            wp.Choose(plan.GoalIndex);
            waypointAt = route.Legs[route.Legs.Count - 1].Length;
            if (wp.Action == WaypointAction.Couple)
                route = ExtendLastLeg(route, world, CoupleSearch);
            legIndex = 0;
            pieceHint = 0;
            planner = BuildPlanner(world.Graph, route.Legs[0], train.Length);
            var first = route.Legs[0].Leader;
            AddLimitsUnderTrain(planner, world, first == ConsistEnd.A ? train.EndA : train.EndB,
                first == ConsistEnd.A ? train.OutwardA : train.OutwardB, train.Length);
            controller.Reset();
            return true;
        }

        /// <summary>The route with its last leg run on past the waypoint, along the switches as set, to find cars to couple to.</summary>
        private static Route ExtendLastLeg(Route route, IEngineerWorld world, double distance)
        {
            var legs = new List<RouteLeg>(route.Legs);
            var last = legs[legs.Count - 1];
            var lastPiece = last.Pieces[last.Pieces.Count - 1];
            var end = last.PositionAt(last.Length);
            var more = TrackWalker.Walk(world.Graph, end, lastPiece.Direction, distance, world.SelectedBranch, last.Leader, out _);
            var pieces = new List<RoutePiece>(last.Pieces);
            foreach (var p in more.Pieces)
            {
                if (p.Length > 1e-6)
                    pieces.Add(p);
            }
            var switches = new List<SwitchSetting>(last.Switches);
            foreach (var sw in more.Switches)
                switches.Add(new SwitchSetting(sw.Junction, sw.Branch, last.Length + sw.AtDistance));
            legs[legs.Count - 1] = new RouteLeg(pieces, switches, last.Leader, false, last.StartOverlap);
            return new Route(legs);
        }

        private SpeedPlanner BuildPlanner(TrackGraph graph, RouteLeg leg, double trainLength) =>
            SpeedPlanner.ForLeg(graph, leg, trainLength, Settings.JunctionSpeed);

        /// <summary>
        /// A leg that starts at the leading end knows nothing of the track
        /// under the train behind it: the limits there hold until the tail
        /// is clear of them, as any limit does. Found by walking back the
        /// train's length along the switches as they lie (they lie under
        /// the train).
        /// </summary>
        private static void AddLimitsUnderTrain(SpeedPlanner planner, IEngineerWorld world, TrackPosition front, TrackDirection outward, double trainLength)
        {
            if (trainLength <= 1)
                return;
            var back = TrackWalker.Walk(world.Graph, front, outward.Reverse(), trainLength, world.SelectedBranch, ConsistEnd.A, out _);
            for (var i = 0; i < back.Pieces.Count; i++)
            {
                var piece = back.Pieces[i];
                var limit = world.Graph.Limits(piece.Track).MinLimit(piece.From, piece.To);
                if (!double.IsPositiveInfinity(limit))
                    planner.Add(new SpeedZone(-LocateTolerance - 1, trainLength - back.PieceStart(i), limit));
            }
        }

        private void TickWalk(double dt, TrainState train, IEngineerWorld world, ref EngineerOutput output)
        {
            if (Holding != null)
            {
                Status = Holding;
                output.Controls = Hold(train);
                return;
            }
            var leader = Toward;
            var front = leader == ConsistEnd.A ? train.EndA : train.EndB;
            var outward = leader == ConsistEnd.A ? train.OutwardA : train.OutwardB;
            var speed = leader == ConsistEnd.A ? train.SpeedTowardA : -train.SpeedTowardA;
            if (MovingTheWrongWay(leader, speed, train, ref output))
                return;
            var braking = Braking(train);
            var cap = Mode == EngineerMode.Road ? RoadSpeed : Math.Min(Settings.YardSpeed, Settings.MaxSpeed);
            var lookahead = Math.Max(250, braking.DistanceToSlow(Math.Max(speed, cap), 0) + 150);

            double progress = 0;
            var located = walk != null && walk.TryLocate(front, walkHint, LocateTolerance, out progress, out walkHint);
            // Road: re-walk as the train moves on or every couple of seconds (switches change).
            // Yard: walk once, for the distance ordered.
            var stale = Mode == EngineerMode.Road && (clock - walkedAt > 2 || progress > 100);
            if (!located || stale)
            {
                if (Mode == EngineerMode.Yard && walk != null && located == false && walkedAt > double.NegativeInfinity)
                {
                    // A yard move that lost its path stops where it is.
                    YardDistance = 0;
                }
                var distance = Mode == EngineerMode.Road ? lookahead + 200 : YardDistance;
                walk = TrackWalker.Walk(world.Graph, front, outward, Math.Max(0.01, distance), world.SelectedBranch, leader, out walkEnd);
                walkPlanner = SpeedPlanner.ForLeg(world.Graph, walk, train.Length, Settings.JunctionSpeed);
                AddLimitsUnderTrain(walkPlanner, world, front, outward, train.Length);
                walkHint = 0;
                walkedAt = clock;
                progress = 0;
            }

            var stopAt = double.PositiveInfinity;
            var reason = "";
            if (Mode == EngineerMode.Yard && walkEnd == WalkEnd.Distance)
            {
                stopAt = walk.Length;
                reason = "end of move";
            }
            else if (walkEnd != WalkEnd.Distance)
            {
                stopAt = walk.Length - (walkEnd == WalkEnd.DeadEnd ? Settings.BufferStandoff : Settings.SwitchStandoff);
                reason = walkEnd == WalkEnd.DeadEnd ? "end of track" : "a switch set against it";
            }
            var obstacle = world.Occupancy.FirstAhead(walk, progress, walkHint, lookahead, owner, out _);
            if (obstacle - Settings.CarGap < stopAt)
            {
                stopAt = obstacle - Settings.CarGap;
                reason = "cars ahead";
            }
            var fouledStop = FouledSwitchAhead(walk, progress, lookahead, world, out _);
            if (fouledStop < stopAt)
            {
                stopAt = fouledStop;
                reason = "cars fouling a switch";
            }

            Progress = progress;
            StopAt = stopAt;
            TargetSpeed = walkPlanner.TargetSpeed(progress, cap, stopAt, braking);
            output.Toward = leader;
            var uphill = output.Toward == ConsistEnd.B ? -train.RiseTowardA : train.RiseTowardA;
            output.Controls = controller.Update(new ControlInput
            {
                Speed = speed,
                Target = TargetSpeed,
                TargetAhead = walkPlanner.TargetSpeed(progress + Math.Max(0, speed) * braking.Delay, cap, stopAt, braking),
                HasTargetAhead = true,
                ToStop = stopAt - progress,
                Halt = cap <= 0.01,
                Dt = dt,
                BrakesDragging = !AirReady(train),
                Slipping = train.Slipping,
                MotorHeat = train.MotorHeat,
                Uphill = uphill,
                AmpsShare = train.MotorAmpsShare,
                GripShare = train.GripShare,
            }, Controls(train));
            if (WaitForAir(train, ref output))
                return;

            var stopped = Math.Abs(speed) < 0.05;
            if (stopped && stopAt - progress < 3)
            {
                if (Mode == EngineerMode.Yard && reason == "end of move")
                {
                    YardDistance = 0;
                    Status = "Yard move done";
                }
                else
                {
                    Status = "Stopped: " + reason;
                }
            }
            else
            {
                if (Mode == EngineerMode.Road)
                    Say(StatusKind.Road, (int)Math.Round(cap * 3.6));
                else
                    Say(StatusKind.YardMove, (int)Math.Round(Math.Max(0, stopAt - progress)));
            }
        }

        // The statuses said every tick while a train runs are built again only when their figure
        // changes: a string a tick, five ticks a second, an engineer, is garbage for nothing.
        private enum StatusKind
        {
            None,
            Running,
            RunningIn,
            Road,
            YardMove,
        }

        private StatusKind saidKind;
        private int saidFigure;
        private int saidOf;
        private Waypoint saidFor;
        private string said;

        private void Say(StatusKind kind, int figure)
        {
            if (kind != saidKind || figure != saidFigure || !ReferenceEquals(Status, said))
            {
                saidKind = kind;
                saidFigure = figure;
                said = kind == StatusKind.RunningIn ? $"Running in to couple, {figure} m"
                    : kind == StatusKind.Road ? $"Road, {figure} km/h"
                    : $"Yard move, {figure} m to go";
            }
            Status = said;
        }

        private void SayRunning(Waypoint wp, int leg, int legs)
        {
            if (saidKind != StatusKind.Running || saidFigure != leg || saidOf != legs || !ReferenceEquals(saidFor, wp) || !ReferenceEquals(Status, said))
            {
                saidKind = StatusKind.Running;
                saidFigure = leg;
                saidOf = legs;
                saidFor = wp;
                said = $"Running to {wp.Label ?? "waypoint"} ({leg}/{legs})";
            }
            Status = said;
        }

        /// <summary>Whether an earlier pass of the same switch on the leg is still ahead of the leading end.</summary>
        internal static bool WantedSooner(IReadOnlyList<SwitchSetting> switches, int index, double progress)
        {
            var junction = switches[index].Junction;
            for (var k = 0; k < index; k++)
            {
                if (switches[k].Junction == junction && switches[k].AtDistance > progress)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Where on the leg the train must stop for the first switch ahead
        /// with cars fouling it from another branch, and the switch's own
        /// distance (<paramref name="at"/>); infinity if none. Facing the
        /// points (from the stem), that is short of the points: the cars
        /// stand alongside the track beyond them. Trailing (down a branch),
        /// it is short of where the branches close on each other, the reach
        /// back from the points; and a train already within that reach is
        /// held only by cars nearer the points than it is.
        /// </summary>
        private double FouledSwitchAhead(RouteLeg leg, double progress, double lookahead, IEngineerWorld world, out double at)
        {
            var switches = leg.Switches;
            for (var i = 0; i < switches.Count; i++)
            {
                var sw = switches[i];
                // Once the leading end is past the points, the train is committed.
                if (sw.AtDistance <= progress)
                    continue;
                if (sw.AtDistance - Fouling.Reach > progress + lookahead)
                    break;
                var trailing = leg.Pieces[leg.PieceIndexAt(sw.AtDistance - 0.01)].Track != world.Graph.Junction(sw.Junction).StemTrack;
                if (!trailing)
                {
                    if (sw.AtDistance > progress + lookahead)
                        break;
                    if (Fouling.OtherBranchesFouled(world.Graph, world.Occupancy, sw.Junction, sw.Branch, owner))
                    {
                        at = sw.AtDistance;
                        return sw.AtDistance - Settings.CarGap;
                    }
                    continue;
                }
                var reach = Math.Min(Fouling.Reach, sw.AtDistance - progress + TrailingMargin);
                if (Fouling.OtherBranchesFouled(world.Graph, world.Occupancy, sw.Junction, sw.Branch, owner, reach))
                {
                    at = sw.AtDistance;
                    return sw.AtDistance - Fouling.Reach - Settings.CarGap;
                }
            }
            at = double.PositiveInfinity;
            return double.PositiveInfinity;
        }

        /// <summary>Coming down a branch inside the fouling reach, cars on another branch this much farther from the points than the leading end still count as ahead of it, m.</summary>
        private const double TrailingMargin = 4;

        /// <summary>
        /// Moving against the way it should go (a new order given at speed,
        /// a replan that leads with the other end, rolling back): brake to a
        /// stand first, the reverser left alone, before anything else.
        /// </summary>
        private bool MovingTheWrongWay(ConsistEnd leader, double speed, TrainState train, ref EngineerOutput output)
        {
            // Changing ends means moving the reverser: only at a stand. With the
            // reverser already set this way, only a real roll back counts; from
            // neutral, any motion against the way it should go.
            var reverser = train.Reverser ?? lastToward;
            bool ok;
            if (!reverser.HasValue)
                ok = speed >= -StandingSpeed;
            else if (reverser.Value != leader)
                ok = Math.Abs(speed) <= StandingSpeed;
            else
                ok = speed >= -WrongWaySpeed;
            if (ok)
                return false;
            controller.Reset();
            TargetSpeed = 0;
            output.Toward = null;
            output.Controls = new ControlOutput
            {
                TrainBrake = train.Controls.HasTrainBrake ? 0.8 : 0,
                LocoBrake = 1,
                DynamicBrake = 0,
            };
            Status = "Stopping before it can go the other way";
            return true;
        }

        private BrakingModel Braking(TrainState train)
        {
            if (!train.Controls.HasTrainBrake)
                return Settings.LightBraking;
            if (IndependentOnly(train))
                return new BrakingModel(Math.Max(0.05, Math.Min(Settings.TrainBraking.Deceleration, Settings.LightBraking.Deceleration * train.LocoMassShare)),
                    Math.Max(Settings.LightBraking.Delay, 2));
            return train.Controls.SlowAir ? SlowAirModel(Settings.SlowAirBraking, controller.AirHolding) : Settings.TrainBraking;
        }

        /// <summary>
        /// Under slow air, whether the locos' own brake can stop the train
        /// as hard as the train brake is planned to: a short cut in a yard,
        /// worked on the independent as a crew would, so no application has
        /// to come off (most of a minute on European brakes in G) before
        /// the next move.
        /// </summary>
        public bool IndependentOnly(TrainState train) =>
            train.Controls.SlowAir && train.LocoMassShare > 0
            && Settings.LightBraking.Deceleration * train.LocoMassShare >= Settings.SlowAirBraking.Deceleration;

        /// <summary>The controls as the controller is to work them: a train on the independent alone is driven as a light engine is.</summary>
        private ControlCapabilities Controls(TrainState train)
        {
            var caps = train.Controls;
            if (IndependentOnly(train))
            {
                caps.HasTrainBrake = false;
                caps.SlowAir = false;
            }
            return caps;
        }

        /// <summary>
        /// The slow-air braking planned for: the setting, less the share of
        /// the brake a grade is taking just to hold the speed (a train that
        /// needs half its brake to hold a downgrade has half left to stop).
        /// </summary>
        public static BrakingModel SlowAirModel(BrakingModel planned, double holding) =>
            new BrakingModel(planned.Deceleration * Math.Max(0.25, 1 - Math.Max(0, holding)), planned.Delay);

        /// <summary>
        /// The train held where it stands. Under slow air the train brake
        /// stays as set between orders (setting it would only mean waiting
        /// for it to come off again), and goes on for a long stand with no
        /// orders, or to stop a train still on the move.
        /// </summary>
        private ControlOutput Hold(TrainState train)
        {
            controller.Reset();
            return controller.Held(Controls(train), firm: queue.Count == 0, speed: train.SpeedTowardA, grade: train.RiseTowardA);
        }

        /// <summary>
        /// After the controller's update: if it is holding the train for its
        /// brakes to come off, says what it waits on, asks for the train's
        /// hand brakes to come off, and warns once if it takes too long.
        /// </summary>
        private bool WaitForAir(TrainState train, ref EngineerOutput output)
        {
            if (controller.Resting)
            {
                Status = train.Steam
                    ? $"Can't get the train moving; shut off for a minute (try {controller.Stalls})"
                    : $"Can't get the train moving; resting the motors (try {controller.Stalls})";
                return true;
            }
            if (!controller.WaitingForAir)
            {
                airWaitSince = double.NaN;
                return false;
            }
            if (double.IsNaN(airWaitSince))
                airWaitSince = clock;
            if (train.HandbrakesOn > 0)
                output.ReleaseHandbrakes = true;
            // The line charged, the hand brakes off, a cylinder stuck a little full: once it has
            // stopped falling for a while, go anyway (a shunt drags one car's brake no harm).
            if (train.AirKnown && train.HandbrakesOn == 0 && train.CarPipeMin >= Settings.ChargedPipe &&
                train.CarCylinderMax >= Settings.ReleasedCylinder && train.CarCylinderMax < Settings.LingeringCylinder)
            {
                if (double.IsNaN(lingerSince) || train.CarCylinderMax < lingerLowest - 0.05)
                {
                    lingerSince = clock;
                    lingerLowest = train.CarCylinderMax;
                }
                else if (clock - lingerSince > Settings.LingerSeconds)
                {
                    lingerAccepted = train.CarCount;
                    lingerSince = double.NaN;
                    Note = $"a car's brake stays on a little ({train.CarCylinderMax:F1} bar) with the line charged; going anyway (it may want bleeding)";
                }
            }
            else
            {
                lingerSince = double.NaN;
            }
            if (!airWarned && clock - airWaitSince > Settings.AirWaitWarning)
            {
                airWarned = true;
                Note = $"its brakes haven't come off in {Settings.AirWaitWarning / 60:F0} minutes ({AirStatus(train).ToLowerInvariant()}): is a hose apart or an angle cock shut?";
            }
            Status = controller.TakingUpPower ? "Taking up power against the brakes" : AirStatus(train);
            return true;
        }
    }
}
