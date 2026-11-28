using System;
using AIEngineer.Core.Routing;

namespace AIEngineer.Core.Driving
{
    /// <summary>What <see cref="RoutePlanner.Plan"/> found for an order.</summary>
    public sealed class RoutePlan
    {
        /// <summary>The cheapest route; null when none was found.</summary>
        public Route Route;

        /// <summary>The waypoint's goal the route leads to.</summary>
        public int GoalIndex = -1;

        /// <summary>Why no route was found.</summary>
        public RouteFailure Failure = RouteFailure.NoPath;

        /// <summary>The route doesn't arrive the way the order asked: no route does.</summary>
        public bool ArrivalMissed;

        /// <summary>What turning a light loco to couple as asked would have added; null when it wasn't given up.</summary>
        public (double Meters, int Reversals)? FacingGivenUp;

        /// <summary>A search was refused a switch for the cars fouling it: the likeliest reason for no route.</summary>
        public bool FouledOnTheWay;

        /// <summary>An order that must arrive one way round found no route that does.</summary>
        public bool WrongWayOnly;
    }

    /// <summary>
    /// Chooses an order's route, for whoever follows it: over every goal the
    /// order offers, each arriving as asked or, failing that, any way (ranked
    /// after), a second-choice goal ranked after the rest. Changes nothing
    /// but the searches' own work, so planning throws no switch.
    /// </summary>
    public static class RoutePlanner
    {
        /// <summary>What a route that arrives the wrong way round costs, ranking it after every one that doesn't, m.</summary>
        private const double WrongWayCost = 1e5;

        /// <summary>What a second-choice goal costs, ranking it after every first choice (the right end before the right way round), m.</summary>
        private const double SecondChoiceCost = 1e6;

        /// <summary>
        /// How much more a coupling's route may cost, by the router's
        /// reckoning (its running and 400 m a reversal), to couple as asked
        /// (a light loco facing away from the cars, by default; with cars on,
        /// cars first) before it couples the other way round, m. Turning a loco means a wye or a loop, often kilometers
        /// off, and every loco drives either way (a player, 17 Nov 2026:
        /// "trying to couple with the back of the train only even when it's
        /// faster ... to couple with the front").
        /// </summary>
        private const double FacingAllowance = 1000;

        /// <param name="lastLeader">The end that must lead the last leg; null for any.</param>
        /// <param name="avoidOccupied">Whether to cost occupied tracks, to look for a way round them.</param>
        /// <param name="firstLeader">The end that must lead the first leg; null for either.</param>
        /// <param name="allowSwitch">A further say on which branch of a switch a route may use; null for none.</param>
        public static RoutePlan Plan(Waypoint wp, TrainState train, IEngineerWorld world, ConsistEnd? lastLeader,
            bool avoidOccupied = false, ConsistEnd? firstLeader = null, Func<int, int, bool> allowSwitch = null)
        {
            var plan = new RoutePlan();
            var owner = train.Owner;
            // A coupling's facing (or, with cars on, cars first) is a preference, not worth a long way round to turn the train.
            var softFacing = lastLeader.HasValue && !wp.ArrivalRequired && Engineer.IsFacing(wp);
            var bestCost = double.PositiveInfinity;
            for (var g = 0; g < wp.Goals.Count; g++)
            {
                var goal = wp.Goals[g];
                var request = new RouteRequest
                {
                    EndA = train.EndA,
                    OutwardA = train.OutwardA,
                    EndB = train.EndB,
                    OutwardB = train.OutwardB,
                    TrainLength = train.Length,
                    Goal = goal.Position,
                    GoalDirection = goal.Arrive,
                    FirstLeader = firstLeader,
                    LastLeader = lastLeader,
                    // Its cars ahead of the loco: priced, so the loco leads on the road.
                    ShovingEnd = train.ShovingEnd,
                    SwitchLie = world.SelectedBranch,
                    // A coupling runs on from where it stands; it isn't backed away from its cars.
                    AllowBackOnto = wp.Action != WaypointAction.Couple,
                    CanSetJunction = (j, b) =>
                    {
                        if (allowSwitch != null && !allowSwitch(j, b))
                            return false;
                        if ((world.SelectedBranch(j) != b && world.Jammed(j, owner)))
                            return false;
                        if (!SwitchRules.Fouled(world, j, b, owner))
                            return true;
                        plan.FouledOnTheWay = true;
                        return false;
                    },
                    JunctionPenalty = (j, b) => SwitchRules.Penalty(world, j, b, owner),
                    TrackPenalty = avoidOccupied
                        ? (Func<int, double>)(t => t != goal.Position.Track && world.Occupancy.IsOccupied(t, owner) ? 5000 : 0)
                        : null,
                    Blocked = (t, from, to) => world.Occupancy.IsStanding(t, from, to, owner),
                };
                var extra = goal.Instead != null ? SecondChoiceCost : 0;
                var wrongWay = false;
                (double Meters, int Reversals)? givenUp = null;
                var candidate = world.Router.Find(request, out var why);
                if (candidate != null && softFacing)
                {
                    request.LastLeader = null;
                    var either = world.Router.Find(request, out _);
                    request.LastLeader = lastLeader;
                    if (either != null && either.Legs[either.Legs.Count - 1].Leader != lastLeader.Value
                        && either.SearchCost + FacingAllowance < candidate.SearchCost)
                    {
                        givenUp = (candidate.Running - either.Running, candidate.Reversals - either.Reversals);
                        candidate = either;
                        wrongWay = true;
                        extra += FacingAllowance;
                    }
                }
                if (candidate == null && lastLeader.HasValue && wp.ArrivalRequired)
                    plan.WrongWayOnly = true;
                if (candidate == null && lastLeader.HasValue && !wp.ArrivalRequired)
                {
                    request.LastLeader = null;
                    candidate = world.Router.Find(request, out why);
                    wrongWay = true;
                    extra += WrongWayCost;
                }
                if (candidate == null)
                {
                    if (plan.Failure == RouteFailure.NoPath)
                        plan.Failure = why;
                    continue;
                }
                var cost = candidate.SearchCost + extra;
                if (cost < bestCost)
                {
                    plan.Route = candidate;
                    plan.GoalIndex = g;
                    bestCost = cost;
                    plan.ArrivalMissed = wrongWay;
                    plan.FacingGivenUp = givenUp;
                }
            }
            return plan;
        }
    }
}
