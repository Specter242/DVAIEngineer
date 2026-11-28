using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Game;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    internal static partial class SelfTest
    {
        private static IEnumerator CoupleCarsPart(Session s)
        {
            const string name = "couplecars: with a car on, the next cut couples to the car, nearby";
            var world = Host.World;
            world.ScanCars();
            var state = s.Read();
            var at = Dispatch.MapPosition(s.Loco);
            var tracks = new List<NamedTrack>();
            foreach (var yard in Dispatch.Yards(world.Network, at).Take(4))
            {
                tracks.Clear();
                foreach (var track in yard.Tracks.OrderBy(t => t.Length))
                {
                    if (track.Length < 100 || world.Occupancy.IsOccupied(track.Track, state.Owner))
                        continue;
                    var route = RouteTo(state, OrderGoals.OnTrack(world.Graph, track.Track, state.Length + 60));
                    if (route == null || route.Length > 3000)
                        continue;
                    tracks.Add(track);
                    if (tracks.Count == 2)
                        break;
                }
                if (tracks.Count == 2)
                    break;
            }
            if (tracks.Count < 2)
            {
                Line($"SKIP {name}: no yard within four with two free tracks of 100 m in reach");
                yield break;
            }
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var one = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat }, new List<bool> { false }, world.Network.Tracks[tracks[0].Track], true, true);
            var two = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat }, new List<bool> { false, false }, world.Network.Tracks[tracks[1].Track], true, true);
            if (one == null || one.Count < 1 || two == null || two.Count < 2)
            {
                Fail(name, $"couldn't spawn the cars on {tracks[0].Id} and {tracks[1].Id}");
                yield break;
            }
            var spawned = one.Concat(two).ToList();
            yield return new WaitForSeconds(5);
            world.ScanCars();
            var cutOne = Dispatch.Cuts(world, at, 5000, s.Loco.trainset).Find(c => c.Set.cars.Contains(one[0]));
            if (cutOne == null)
            {
                Fail(name, $"the car on {tracks[0].Id} isn't listed");
                yield return ClearSpawned(spawned);
                yield break;
            }
            Line($"couplecars: a car on {tracks[0].Id}, then 2 on {tracks[1].Id}");
            Dispatch.PickUp(s, world, cutOne, queue: false);
            yield return UntilQueueEmpty(s, "couplecars first", 900);
            if (one[0].trainset != s.Loco.trainset)
            {
                Fail(name, $"the loco didn't couple to the car on {tracks[0].Id}: status '{s.Engineer.Status}'");
                yield return ClearSpawned(spawned);
                yield break;
            }
            world.ScanCars();
            var cutTwo = Dispatch.Cuts(world, Dispatch.MapPosition(s.Loco), 5000, s.Loco.trainset).Find(c => c.Set.cars.Contains(two[0]));
            if (cutTwo == null)
            {
                Fail(name, $"the cars on {tracks[1].Id} aren't listed");
                yield return ClearSpawned(spawned);
                yield break;
            }
            // As the tablet gives it: the end by the default, back first.
            var pickup = Dispatch.PickUp(s, world, cutTwo, queue: false);
            var last = s.Loco.transform.position;
            float travelled = 0, elapsed = 0;
            var reversals = -1;
            while (elapsed < 900 && s.Engineer.Queue.Count > 0 && s.Engineer.Mode == EngineerMode.Waypoints)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                travelled += Vector3.Distance(s.Loco.transform.position, last);
                last = s.Loco.transform.position;
                if (reversals < 0 && s.Engineer.Route != null)
                    reversals = s.Engineer.Route.Reversals;
                if ((int)elapsed % 15 == 0)
                    Trace(s, "couplecars", elapsed);
            }
            bool Theirs(TrainCar c) => c != null && two.Contains(c);
            var loco = s.Loco;
            var toLoco = Theirs(loco.frontCoupler.coupledTo?.train) || Theirs(loco.rearCoupler.coupledTo?.train);
            var toCar = Theirs(one[0].frontCoupler.coupledTo?.train) || Theirs(one[0].rearCoupler.coupledTo?.train);
            Check(name, toCar && !toLoco && travelled < 1500,
                $"the cut coupled to {(toCar ? "the car" : toLoco ? "the LOCO" : "nothing")} after {travelled:F0} m, {elapsed:F0} s, " +
                $"{reversals} reversal{(reversals == 1 ? "" : "s")} planned; arrival {(pickup.ArrivalMet ? "met" : "not met")}; logged '{s.LastLogged}'; status '{s.Engineer.Status}'");
            yield return ClearSpawned(spawned);
        }
    }
}
