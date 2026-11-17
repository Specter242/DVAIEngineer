using System;
using System.Collections.Generic;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    public enum EngineerMode
    {
        /// <summary>Hands off: the controls are the player's.</summary>
        Off,

        /// <summary>Run at a set speed one way, with the switches as they lie, stopping for cars and dead ends.</summary>
        Road,

        /// <summary>Move a set distance one way, with the switches as they lie.</summary>
        Yard,

        /// <summary>Work through the waypoint queue, routing and setting switches.</summary>
        Waypoints,

        /// <summary>
        /// A helper on a doubleheaded train: no route of its own; the game
        /// side works it from the lead engine's whistle and levers.
        /// </summary>
        Helper,
    }

    /// <summary>What to do on reaching a waypoint.</summary>
    public enum WaypointAction
    {
        /// <summary>Stop with the leading end at the waypoint.</summary>
        Stop,

        /// <summary>Run on past the waypoint to the cars standing there and couple to them.</summary>
        Couple,

        /// <summary>Stop at the waypoint, then cut off cars.</summary>
        Uncouple,
    }

    /// <summary>
    /// Which end of the train an order wants arriving first, in the loco's
    /// terms: the consist's own ends change names as cars couple and part.
    /// </summary>
    public enum Arrival
    {
        /// <summary>Whichever is cheaper.</summary>
        Any,

        /// <summary>The cars ahead of the loco: shoving them in, so the loco isn't trapped behind them.</summary>
        CarsFirst,

        /// <summary>The loco's end first.</summary>
        LocoFirst,

        /// <summary>The loco's front leading (the way its reverser calls forward; the cab end on most locos).</summary>
        FrontFirst,

        /// <summary>The loco's back leading: coupling to a cut so, after, the loco faces away from the cars and pulls them going forward.</summary>
        BackFirst,

        /// <summary>Cars first into a dead-end track (the loco would be trapped behind them otherwise); either way elsewhere.</summary>
        CarsFirstIntoDeadEnd,
    }

    /// <summary>One place an order may end: a track position, and the way the train must be moving as it arrives, if it matters.</summary>
    public readonly struct WaypointGoal
    {
        public readonly TrackPosition Position;
        public readonly TrackDirection? Arrive;

        /// <summary>
        /// Set on a second choice, taken only when no other goal can be
        /// reached: what the player is told then ("coupling on the south
        /// end: the north end can't be reached").
        /// </summary>
        public readonly string Instead;

        public WaypointGoal(TrackPosition position, TrackDirection? arrive = null, string instead = null)
        {
            Position = position;
            Arrive = arrive;
            Instead = instead;
        }

        public WaypointGoal AsSecondChoice(string instead) => new WaypointGoal(Position, Arrive, instead);

        public override string ToString() => Arrive.HasValue ? $"{Position} arriving {Arrive}" : Position.ToString();
    }

    /// <summary>
    /// One order in the queue: where to go and what to do there. It may
    /// offer several goals (either end of a cut to couple to, either way
    /// onto a track): the engineer takes whichever it reaches most cheaply
    /// when the order starts.
    /// </summary>
    /// <summary>How an order ended, for whoever gave it (a job run reads it to see whether its plan still holds).</summary>
    public enum OrderOutcome
    {
        /// <summary>Not ended yet, or taken out of the queue by someone else.</summary>
        None,

        /// <summary>Carried out.</summary>
        Done,

        /// <summary>A coupling that found no cars where it looked.</summary>
        NoCars,

        /// <summary>A coupling whose couplers met and wouldn't take.</summary>
        NotCoupled,

        /// <summary>A cut with no cars to cut: none of those meant were at the train's far end.</summary>
        NothingCut,
    }

    public sealed class Waypoint
    {
        private readonly List<WaypointGoal> goals;
        private int chosen;

        public Waypoint(TrackPosition position, WaypointAction action = WaypointAction.Stop)
            : this(new[] { new WaypointGoal(position) }, action)
        {
        }

        public Waypoint(IEnumerable<WaypointGoal> goals, WaypointAction action = WaypointAction.Stop)
        {
            this.goals = new List<WaypointGoal>(goals ?? throw new ArgumentNullException(nameof(goals)));
            if (this.goals.Count == 0)
                throw new ArgumentException("A waypoint needs a goal.", nameof(goals));
            Action = action;
        }

        public IReadOnlyList<WaypointGoal> Goals => goals;

        /// <summary>The goal taken (the first until the engineer plans).</summary>
        public WaypointGoal Goal => goals[chosen];

        public TrackPosition Position => goals[chosen].Position;

        /// <summary>Records the goal the engineer's route leads to.</summary>
        public void Choose(int index) => chosen = Math.Max(0, Math.Min(goals.Count - 1, index));

        /// <summary>
        /// If set, the goals for a train of a given length, worked out as the
        /// order starts: a track order queued behind a pickup stops for the
        /// train as it will be then.
        /// </summary>
        public Func<double, IEnumerable<WaypointGoal>> GoalsForLength { get; set; }

        /// <summary>
        /// If set, the goals for the train as it is when the order starts
        /// (taking precedence over <see cref="GoalsForLength"/>): a set-out
        /// finds the room left on its track then, around whatever stands
        /// there, not the train's own cars.
        /// </summary>
        public Func<TrainState, IEnumerable<WaypointGoal>> GoalsFor { get; set; }

        /// <summary>Recomputes the goals for the train now, if the order sizes them.</summary>
        public void Resize(TrainState train)
        {
            var source = GoalsFor != null ? GoalsFor(train)
                : GoalsForLength != null ? GoalsForLength(train.Length)
                : null;
            if (source == null)
                return;
            var fresh = new List<WaypointGoal>(source);
            if (fresh.Count == 0)
                return;
            goals.Clear();
            goals.AddRange(fresh);
            chosen = 0;
        }

        public WaypointAction Action { get; set; }

        /// <summary>How the order ended, set as the engineer takes it off its queue.</summary>
        public OrderOutcome Outcome { get; set; }

        /// <summary>
        /// Whether the order is only worth carrying out arriving as
        /// <see cref="Arrival"/> asks: with no route that does, it has no
        /// route. (Cars set out loco first are cut off behind the loco, which
        /// is then shut in by them; an order to stop somewhere is still worth
        /// making the wrong way round.)
        /// </summary>
        public bool ArrivalRequired { get; set; }

        /// <summary>
        /// For <see cref="WaypointAction.Uncouple"/>: how many cars to cut
        /// off, counted from the end of the train away from the loco, so
        /// "leave three" leaves the three cars farthest from it.
        /// </summary>
        public int CarCount { get; set; } = 1;

        /// <summary>Whether the cars cut off get their hand brakes set.</summary>
        public bool SetHandbrakes { get; set; } = true;

        /// <summary>
        /// For <see cref="WaypointAction.Couple"/>: how many of the cars
        /// coupled to stay with the train, counted from the coupling; the
        /// rest are cut off again where they stand, hand brakes set. Null
        /// keeps them all.
        /// </summary>
        public int? KeepCars { get; set; }

        /// <summary>
        /// For <see cref="WaypointAction.Couple"/>, asked once coupled: how
        /// many cars to cut off the far end again, where they stand, hand
        /// brakes set. The game side counts them from the cars themselves
        /// (those not on the jobs at the far end), so a count taken at the
        /// wrong moment can't take the wrong cars. Takes the place of
        /// <see cref="KeepCars"/> when set.
        /// </summary>
        public Func<int> CutAfterCoupling { get; set; }

        /// <summary>
        /// For <see cref="WaypointAction.Uncouple"/>, asked as the cut is
        /// made: how many cars to cut off, counted by the game side from the
        /// cars themselves (a set-out shoved together with cars it coupled
        /// to on the track). Takes the place of <see cref="CarCount"/> when set.
        /// </summary>
        public Func<int> CutCount { get; set; }

        /// <summary>The order is carried out where the train stands, with no move (a cut made on the spot).</summary>
        public bool Here { get; set; }

        /// <summary>Said to the player once the order is done ("MF-FH-07 is on HB-F2L").</summary>
        public string DoneNote { get; set; }

        /// <summary>Seconds to wait after the action before the next order.</summary>
        public double WaitSeconds { get; set; }

        /// <summary>Which end of the train must arrive first, if it matters.</summary>
        public Arrival Arrival { get; set; }

        /// <summary>Whether the order's track ends in a buffer stop (for <see cref="Arrival.CarsFirstIntoDeadEnd"/>).</summary>
        public bool IntoDeadEnd { get; set; }

        /// <summary>After planning: false when no route could arrive as <see cref="Arrival"/> asks, and the engineer went anyway.</summary>
        public bool ArrivalMet { get; set; } = true;

        /// <summary>A name for the player: the track's, say.</summary>
        public string Label { get; set; }

        /// <summary>If set, the way the train must be moving as it arrives (at the taken goal).</summary>
        public TrackDirection? ArriveDirection
        {
            get => goals[chosen].Arrive;
            set => goals[chosen] = new WaypointGoal(goals[chosen].Position, value);
        }

        public override string ToString() => $"{Action} at {Label ?? Position.ToString()}";
    }

    /// <summary>A cut the game side is asked to make: cars counted from the end of the consist away from the loco.</summary>
    public readonly struct CutRequest
    {
        public readonly int Cars;
        public readonly bool SetHandbrakes;

        public CutRequest(int cars, bool setHandbrakes)
        {
            Cars = Math.Max(1, cars);
            SetHandbrakes = setHandbrakes;
        }
    }
}
