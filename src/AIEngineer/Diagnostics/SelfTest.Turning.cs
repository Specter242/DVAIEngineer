using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    internal static partial class SelfTest
    {
        /// <summary>
        /// A player's report (17 Nov 2026), at Steel Mill: a train with its
        /// loco at the back turns on a wye and runs loco first to the Goods
        /// Factory, not shoving its cars the 6.7 km; a light loco on the
        /// turntable lead, sent to couple to a cut where facing away from it
        /// means a 4 km turn, couples facing it and logs why; and an S282 with
        /// its tender counts as light for both.
        /// </summary>
        private static IEnumerator TurningPart()
        {
            var world = Host.World;
            var yards = Dispatch.Yards(world.Network, Vector2.zero);
            NamedTrack Named(string id) => yards.SelectMany(y => y.Tracks).FirstOrDefault(t => t.Id == id);
            TrainCarLivery de2 = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "LocoDE2");
            TrainCarLivery s282 = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "LocoS282A");
            TrainCarLivery tender = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "LocoS282B");
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var from = Named("SM-B7S");
            var to = Named("GF-B3S");
            var cutTrack = Named("SM-A4S");
            if (de2 == null || s282 == null || tender == null || from == null || to == null || cutTrack == null)
            {
                Line($"SKIP turning: not the stock map (SM-B7S {from != null}, GF-B3S {to != null}, SM-A4S {cutTrack != null}), or a livery missing");
                yield break;
            }
            yield return Caught("turning: the train", TurnsToRunLocoFirst(world, de2, flat, from, to));
            yield return Caught("turning: the S282 running light", TenderLocoRunsLight(world, s282, tender, from, to));
            // The A yard's tracks, A4S first: the station's own job cars may stand on any of them.
            var cutTracks = yards.SelectMany(y => y.Tracks).Where(t => t.Id.StartsWith("SM-A", StringComparison.Ordinal) && t.Length >= 60)
                .OrderBy(t => t.Id == cutTrack.Id ? 0 : 1).ToList();
            yield return Caught("turning: the light DE2", CouplesFacingTheCars(world, new List<TrainCarLivery> { de2 }, "a light DE2", flat, cutTrack, cutTracks));
            yield return Caught("turning: the light S282", CouplesFacingTheCars(world, new List<TrainCarLivery> { s282, tender }, "a light S282 and tender", flat, cutTrack, cutTracks));
        }

        /// <summary>A spawned loco and tender coupled: the spawner leaves that to chance, as the two may settle out of the auto-coupling's reach.</summary>
        private static IEnumerator CoupleTogether(List<TrainCar> cars)
        {
            var engine = cars.First(c => c.IsLoco);
            var tender = cars.FirstOrDefault(c => c != engine);
            if (tender == null || engine.trainset == tender.trainset)
                yield break;
            engine.rearCoupler.TryCouple(false, false, 5f);
            yield return new WaitForSeconds(2);
        }

        /// <summary>A spawned steam loco fired up, as the game's own start does; others start themselves when given orders.</summary>
        private static IEnumerator FireUp(TrainCar loco)
        {
            if (!loco.carLivery.id.StartsWith("LocoS", StringComparison.Ordinal))
                yield break;
            DV.Simulation.Controllers.StartupHelper.Startup(loco);
            yield return new WaitForSeconds(8);
        }

        /// <summary>An S282 and its tender are power alone: nothing to shove, and the route a light loco takes.</summary>
        private static IEnumerator TenderLocoRunsLight(World world, TrainCarLivery engine, TrainCarLivery tender, NamedTrack from, NamedTrack to)
        {
            const string name = "turning: an S282 and tender run as a light engine";
            var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { engine, tender }, new List<bool> { false, false }, world.Network.Tracks[from.Track], false, false);
            if (cars == null || cars.Count < 2)
            {
                Fail(name, $"couldn't spawn them on {from.Id}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            yield return CoupleTogether(cars);
            StandBeside(cars[0]);
            yield return new WaitForSecondsRealtime(15);
            world.ScanCars();
            var loco = cars.First(c => c.IsLoco);
            yield return FireUp(loco);
            var s = Host.SessionFor(loco, true);
            var state = s.Read();
            var plain = Cheapest(world, state, OrderGoals.ToTrack(world.Graph, to.Track, state.Length, to.Id, Arrival.CarsFirstIntoDeadEnd).Goals.ToList(), null, false);
            Dispatch.GoToTrack(s, world, to, queue: false);
            var elapsed = 0f;
            while (elapsed < 60 && s.Engineer.Route == null)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
            }
            var route = s.Engineer.Route;
            var start = loco.transform.position;
            var derailed = false;
            elapsed = 0;
            while (elapsed < 420 && s.Engineer.Queue.Count > 0 && !derailed && Vector3.Distance(loco.transform.position, start) < 300)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                derailed = cars.Any(c => c != null && c.derailed);
                if ((int)elapsed % 10 == 0)
                    Trace(s, "turning S282", elapsed);
            }
            var run = Vector3.Distance(loco.transform.position, start);
            var straight = route != null && plain != null && route.Reversals == plain.Reversals && Math.Abs(route.Length - plain.Length) < plain.Length * 0.05;
            var detail = $"{state.CarCount} cars, {state.WagonCount} wagons, shoving end {state.ShovingEnd?.ToString() ?? "none"}; route {Legs(route, state.LocoEnd)}, the plain one {Legs(plain, state.LocoEnd)}; ran {run:F0} m in {elapsed:F0} s; derailed {derailed}";
            (state.CarCount == 2 && state.LightEngine && state.ShovingEnd == null && straight && run >= 300 && !derailed ? (Action<string, string>)Pass : Fail)(name, detail);
            s.Stop(secure: true);
            yield return new WaitForSeconds(10);
            yield return ClearSpawned(cars);
        }

        /// <summary>A step run so that what it throws fails it: a nested coroutine that throws would otherwise leave the test waiting forever.</summary>
        private static IEnumerator Caught(string name, IEnumerator inner)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext())
                        yield break;
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    Fail(name, "threw " + e);
                    yield break;
                }
                yield return current is IEnumerator nested ? Caught(name, nested) : current;
            }
        }

        /// <summary>The player stood beside a car (not in it: a car just spawned has no interior ready), so its surroundings stay loaded.</summary>
        private static void StandBeside(TrainCar car) =>
            PlayerManager.TeleportPlayer(car.transform.position + car.transform.right * 8 + Vector3.up * 2, car.transform.rotation, null, useRotation: false);

        private static double CostOf(Route route) => route.Length + 400 * route.Reversals;

        /// <summary>The cheapest route from a state to any of the goals, the last leg led by <paramref name="lastLeader"/> if given, shoving priced or not.</summary>
        private static Route Cheapest(World world, TrainState state, List<WaypointGoal> goals, ConsistEnd? lastLeader, bool priceShoving)
        {
            Route best = null;
            foreach (var goal in goals)
            {
                var route = world.Router.Find(new RouteRequest
                {
                    EndA = state.EndA,
                    OutwardA = state.OutwardA,
                    EndB = state.EndB,
                    OutwardB = state.OutwardB,
                    TrainLength = state.Length,
                    Goal = goal.Position,
                    GoalDirection = goal.Arrive,
                    LastLeader = lastLeader,
                    ShovingEnd = priceShoving ? state.ShovingEnd : null,
                    AllowBackOnto = false,
                    CanSetJunction = (j, b) => SwitchRules.Usable(world, j, b, state.Owner),
                    Blocked = (t, a, b) => world.Occupancy.IsStanding(t, a, b, state.Owner),
                });
                if (route != null && (best == null || CostOf(route) < CostOf(best)))
                    best = route;
            }
            return best;
        }

        /// <summary>The meters a route runs with the end away from the loco leading.</summary>
        private static double Shoved(Route route, ConsistEnd locoEnd) => route.Legs.Where(l => l.Leader != locoEnd).Sum(l => l.Running);

        private static string Legs(Route route, ConsistEnd locoEnd) => route == null ? "no route"
            : $"{route.Length:F0} m, {route.Reversals} reversals: " + string.Join(", ", route.Legs.Select(l => $"{(l.Leader == locoEnd ? "loco" : "cars")} first {l.Length:F0} m"));

        private static IEnumerator TurnsToRunLocoFirst(World world, TrainCarLivery de2, TrainCarLivery flat, NamedTrack from, NamedTrack to)
        {
            const string planned = "turning: a train with its cars ahead plans to turn and run loco first";
            const string ran = "turning: it turned and ran loco first";
            List<TrainCar> spawned = null;
            Session s = null;
            TrainState state = default;
            Route plain = null;
            List<WaypointGoal> goals = null;
            // The loco at the end away from the Goods Factory: spawned so, or again the other way round.
            foreach (var locoLast in new[] { false, true })
            {
                var liveries = new List<TrainCarLivery> { de2, flat, flat, flat, flat };
                if (locoLast)
                    liveries.Reverse();
                spawned = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, liveries.Select(_ => false).ToList(), world.Network.Tracks[from.Track], true, false);
                if (spawned == null || spawned.Count < 5)
                {
                    Fail(planned, $"couldn't spawn a train on {from.Id}");
                    yield break;
                }
                yield return new WaitForSeconds(5);
                StandBeside(spawned[0]);
                yield return new WaitForSecondsRealtime(15);
                world.ScanCars();
                var loco = spawned.First(c => c.carLivery == de2);
                s = Host.SessionFor(loco, true);
                state = s.Read();
                goals = OrderGoals.ToTrack(world.Graph, to.Track, state.Length, to.Id, Arrival.CarsFirstIntoDeadEnd).Goals.ToList();
                var unpriced = Cheapest(world, state, goals, null, false);
                Line($"turning: {state.CarCount} cars, {state.Length:F0} m, on {from.Id} to {to.Id}, unpriced: {Legs(unpriced, state.LocoEnd)}");
                // Standing cars can send the way out either way: what counts is that, unpriced, the cars would lead most of it.
                if (unpriced != null && Shoved(unpriced, state.LocoEnd) > unpriced.Running / 2)
                {
                    plain = unpriced;
                    break;
                }
                CarSpawner.Instance.DeleteTrainCars(spawned, true);
                spawned = null;
                yield return new WaitForSeconds(3);
            }
            if (spawned == null)
            {
                Fail(planned, $"no way round was the loco at the back on {from.Id}");
                yield break;
            }
            Dispatch.GoToTrack(s, world, to, queue: false);
            var elapsed = 0f;
            while (elapsed < 60 && s.Engineer.Route == null)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
            }
            var route = s.Engineer.Route;
            var locoEnd = state.LocoEnd;
            var locoFirst = route?.Legs.Where(l => l.Leader == locoEnd).Sum(l => l.Length) ?? 0;
            var longest = route == null ? -1 : Enumerable.Range(0, route.Legs.Count).OrderByDescending(i => route.Legs[i].Length).First();
            var detail = $"{Legs(route, locoEnd)}; shoved {(route != null ? Shoved(route, locoEnd) : 0):F0} m of {route?.Running ?? 0:F0}, where the unpriced route shoves {Shoved(plain, locoEnd):F0} of {plain.Running:F0}";
            var turns = route != null && longest > 0 && route.Legs[longest].Leader == locoEnd && Shoved(route, locoEnd) < route.Running / 2;
            (turns ? (Action<string, string>)Pass : Fail)(planned, detail);
            if (!turns)
            {
                s.Stop(secure: true);
                yield return ClearSpawned(spawned);
                yield break;
            }
            // Driven until it has turned and run 300 m on the long leg, the loco leading.
            Vector3? startOfLeg = null;
            var derailed = false;
            var wrongWay = 0;
            elapsed = 0;
            while (elapsed < 1500 && s.Engineer.Queue.Count > 0)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                if ((int)elapsed % 10 == 0)
                    Trace(s, "turning", elapsed);
                derailed |= spawned.Any(c => c != null && c.derailed);
                if (derailed)
                    break;
                if (Vector3.Distance(PlayerManager.PlayerTransform.position, s.Loco.transform.position) > 500)
                    StandBeside(s.Loco);
                if (s.Engineer.LegIndex < longest)
                    continue;
                var now = s.Read();
                startOfLeg = startOfLeg ?? s.Loco.transform.position;
                var towardLoco = locoEnd == ConsistEnd.A ? now.SpeedTowardA : -now.SpeedTowardA;
                if (towardLoco < -0.5)
                    wrongWay++;
                if (Vector3.Distance(s.Loco.transform.position, startOfLeg.Value) > 300)
                    break;
            }
            var run = startOfLeg.HasValue ? Vector3.Distance(s.Loco.transform.position, startOfLeg.Value) : 0;
            detail = $"leg {s.Engineer.LegIndex + 1} of {route.Legs.Count} after {elapsed:F0} s, {run:F0} m on it, {wrongWay} s cars first on it, derailed {derailed}; status '{s.Engineer.Status}'";
            (!derailed && run > 300 && wrongWay == 0 ? (Action<string, string>)Pass : Fail)(ran, detail);
            s.Stop(secure: true);
            yield return new WaitForSeconds(20);
            yield return ClearSpawned(spawned);
        }

        private static IEnumerator CouplesFacingTheCars(World world, List<TrainCarLivery> power, string label, TrainCarLivery flat, NamedTrack anchor, List<NamedTrack> cutTracks)
        {
            var name = $"turning: {label} couples facing the cars rather than turn far";
            var lead = Enumerable.Range(0, world.Graph.TrackCount).FirstOrDefault(t =>
                world.Network.Tracks[t].name == "[#] Road 66"
                && (world.Graph.Links(t, TrackEnd.Start).Length == 0 || world.Graph.Links(t, TrackEnd.End).Length == 0)
                && Vector2.Distance(world.Network.MapPointAt(new TrackPosition(t, 0)), anchor.Middle) < 600);
            if (world.Network.Tracks[lead].name != "[#] Road 66")
            {
                Line($"SKIP {name}: no turntable lead at Steel Mill");
                yield break;
            }
            world.ScanCars();
            NamedTrack cutTrack = null;
            List<TrainCar> cutCars = null;
            foreach (var track in cutTracks.Where(t => !world.Occupancy.IsOccupied(t.Track, int.MinValue)))
            {
                cutCars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat, flat }, new List<bool> { false, false, false },
                    world.Network.Tracks[track.Track], true, true);
                if (cutCars != null && cutCars.Count >= 3)
                {
                    cutTrack = track;
                    break;
                }
            }
            if (cutTrack == null)
            {
                Fail(name, $"no free track for the cut among {string.Join(", ", cutTracks.Select(t => t.Id))}");
                yield break;
            }
            Line($"turning: {label}: the cut on {cutTrack.Id}");
            Session s = null;
            Cut cut = null;
            var side = -1;
            List<TrainCar> loco = null;
            foreach (var flipped in new[] { false, true })
            {
                // Turned round: the order swapped and each car reversed, so the loco's rear still meets its tender.
                var order = flipped ? Enumerable.Reverse(power).ToList() : power;
                loco = CarSpawner.Instance.SpawnCarTypesOnTrack(order, order.Select(_ => flipped).ToList(), world.Network.Tracks[lead], false, false);
                if (loco == null || loco.Count == 0)
                {
                    Fail(name, "couldn't spawn the loco on the turntable lead");
                    yield return ClearSpawned(cutCars);
                    yield break;
                }
                yield return new WaitForSeconds(5);
                yield return CoupleTogether(loco);
                var engine = loco.First(c => c.IsLoco);
                StandBeside(engine);
                yield return new WaitForSecondsRealtime(15);
                world.ScanCars();
                yield return FireUp(engine);
                s = Host.SessionFor(engine, true);
                var state = s.Read();
                if (!state.LightEngine || state.CarCount != power.Count)
                {
                    Fail(name, $"{state.CarCount} cars of {power.Count} coupled, {state.WagonCount} of them wagons: not read as this light engine");
                    yield return ClearSpawned(cutCars.Concat(loco));
                    yield break;
                }
                cut = Dispatch.Cuts(world, Dispatch.MapPosition(engine), 3000, engine.trainset).Find(c => c.Set.cars.Contains(cutCars[0]));
                if (cut == null)
                {
                    Fail(name, $"the cut on {cutTrack.Id} isn't listed");
                    yield return ClearSpawned(cutCars.Concat(loco));
                    yield break;
                }
                var backFirst = Engineer.Leader(Arrival.BackFirst, state);
                for (var e = 0; e < 2 && side < 0; e++)
                {
                    var goals = OrderGoals.ToCouple(world.Graph, cut.Ends, e, cut.EndNames, world.SelectedBranchFunc);
                    var facing = Cheapest(world, state, goals, backFirst, false);
                    var either = Cheapest(world, state, goals, null, false);
                    Line($"turning: loco {(flipped ? "flipped" : "as spawned")}, the {cut.EndNames[e]} end: facing away {(facing != null ? CostOf(facing).ToString("F0") : "-")}, either way {(either != null ? CostOf(either).ToString("F0") : "-")}");
                    if (facing != null && either != null && either.Legs[either.Legs.Count - 1].Leader != backFirst && CostOf(facing) > CostOf(either) + 1500)
                        side = e;
                }
                if (side >= 0)
                    break;
                CarSpawner.Instance.DeleteTrainCars(loco, true);
                yield return new WaitForSeconds(3);
            }
            if (side < 0)
            {
                Line($"SKIP {name}: turning to face away was never more than 1.5 km longer");
                yield return ClearSpawned(cutCars);
                yield break;
            }
            var pickup = Dispatch.PickUp(s, world, cut, queue: false, side == 0 ? CutSide.End0 : CutSide.End1, Arrival.BackFirst);
            var start = s.Loco.transform.position;
            var travelled = 0f;
            var last = start;
            var elapsed = 0f;
            while (elapsed < 600 && s.Engineer.Queue.Count > 0)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                travelled += Vector3.Distance(s.Loco.transform.position, last);
                last = s.Loco.transform.position;
                if ((int)elapsed % 10 == 0)
                    Trace(s, "turning pickup", elapsed);
            }
            bool OnCut(Coupler c) => c.coupledTo != null && cutCars.Contains(c.coupledTo.train);
            var front = OnCut(s.Loco.frontCoupler);
            var coupler = front ? "the loco's front" : OnCut(s.Loco.rearCoupler) ? "the loco's back" : "nothing of the loco";
            var detail = $"the cut coupled to {coupler} ({s.Read().CarCount} cars now) after {travelled:F0} m, {elapsed:F0} s; arrival {(pickup.ArrivalMet ? "met" : "not met")}; logged '{s.LastLogged}'; told '{s.LastNote}'";
            var ok = front && !pickup.ArrivalMet && travelled < 1500
                && (s.LastLogged ?? "").Contains("turning it first would add about") && !(s.LastNote ?? "").Contains("turning it");
            (ok ? (Action<string, string>)Pass : Fail)(name, detail);
            s.Stop(secure: true);
            yield return new WaitForSeconds(5);
            yield return ClearSpawned(cutCars.Concat(loco));
        }

        private static IEnumerator ClearSpawned(IEnumerable<TrainCar> cars)
        {
            var gone = cars.Where(c => c != null).ToList();
            if (gone.Count > 0)
                CarSpawner.Instance.DeleteTrainCars(gone, true);
            yield return new WaitForSeconds(3);
            Host.World?.ScanCars();
        }
    }
}
