using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Game;
using DV.ThingTypes;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    internal static partial class SelfTest
    {
        private static IEnumerator FarCarsPart()
        {
            const string name = "far cars";
            if (!PersistentJobs.Present)
            {
                Line($"SKIP {name}: Persistent Jobs isn't loaded (run with --with PersistentJobsMod)");
                yield break;
            }
            var farCarOpt = Type.GetType("PersistentJobsMod.Optimization.FarCarOpt, PersistentJobsMod", false);
            var runSuspend = farCarOpt?.GetMethod("RunSuspendCars");
            var world = Host.World;
            world.ScanCars();
            var player = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
            var yards = Dispatch.Yards(world.Network, new Vector2(player.x, player.z)).Where(y => !y.Added).ToList();
            // A yard with three free through tracks, and its station.
            Yard yard = null;
            StationController station = null;
            List<NamedTrack> free = null;
            foreach (var y in yards)
            {
                var tracks = y.Tracks.Where(t => t.Length > 150 && !t.DeadEnd && !world.Occupancy.IsOccupied(t.Track, int.MinValue)).Take(3).ToList();
                var sc = StationController.allStations.FirstOrDefault(c => c != null && c.stationInfo != null && c.stationInfo.YardID == y.Id);
                if (tracks.Count == 3 && sc != null)
                {
                    yard = y;
                    station = sc;
                    free = tracks;
                    break;
                }
            }
            if (yard == null || runSuspend == null)
            {
                Fail(name, runSuspend == null ? "Persistent Jobs' RunSuspendCars wasn't found" : "no yard with three free through tracks");
                yield break;
            }
            var flat = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "FlatbedEmpty") ?? DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id.StartsWith("Flat", StringComparison.Ordinal));
            var de2 = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "LocoDE2");
            var locoCars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { de2 }, new List<bool> { false }, world.Network.Tracks[free[0].Track], false, false);
            var cutA = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat }, new List<bool> { false, false }, world.Network.Tracks[free[1].Track], false, false);
            if (locoCars == null || locoCars.Count == 0 || cutA == null || cutA.Count < 2)
            {
                Fail(name, "the loco or the cut couldn't be spawned");
                yield break;
            }
            var loco = locoCars[0];
            yield return new WaitForSeconds(3);
            DV.Simulation.Controllers.StartupHelper.Startup(loco);
            // The player well away: Persistent Jobs suspends stations the player is far from.
            var away = yards.Where(y => Vector2.Distance(y.Centre, yard.Centre) > 3000).OrderBy(y => Vector2.Distance(y.Centre, yard.Centre)).FirstOrDefault();
            if (away != null && away.Tracks.Count > 0)
            {
                var spot = Host.Network.MapPointAt(new Core.Tracks.TrackPosition(away.Tracks[0].Track, away.Tracks[0].Length / 2));
                PlayerManager.TeleportPlayer(new Vector3((float)spot.x, 0, (float)spot.y) + WorldMover.currentMove + Vector3.up * 200, Quaternion.identity, null, useRotation: false);
            }
            yield return new WaitForSeconds(5);
            Line($"{name}: {yard.Id}: loco {loco.ID} on {free[0].Id}, cut {cutA[0].ID} on {free[1].Id}; the player at {away?.Id ?? "(not moved)"}");

            // Kept: the pick-up ordered, then Persistent Jobs' suspension run on the station.
            world.ScanCars();
            var s = Host.SessionFor(loco, true);
            var cut = Dispatch.Cuts(world, yard.Centre, 5000, loco.trainset).Find(c => c.Set.cars.Contains(cutA[0]));
            if (cut == null)
            {
                Fail(name, "the spawned cut wasn't found among the cuts");
                yield break;
            }
            Dispatch.PickUp(s, world, cut, queue: false, CutSide.Nearer, Arrival.Any);
            yield return new WaitForSeconds(4);
            var keptBefore = cutA.All(c => c != null && c.preventDelete);
            runSuspend.Invoke(null, new object[] { true, new List<StationController> { station }, null });
            yield return new WaitForSeconds(10);
            var stayed = cutA.All(c => c != null && c.gameObject.activeInHierarchy && c.logicCar != null);
            float waited = 0;
            while (waited < 300 && s.Engineer.Queue.Count > 0)
            {
                yield return new WaitForSeconds(1);
                waited += 1;
            }
            var coupled = stayed && cutA.All(c => c != null && c.trainset == loco.trainset);
            Check(name + ": a cut an engineer is sent to pick up stays, and is coupled", keptBefore && stayed && coupled,
                $"kept from the clean-up before the suspension {keptBefore}; still in the world after it {stayed}; coupled {coupled} after {waited:F0} s; status '{s.Engineer.Status}'");

            // Brought back: a cut suspended first, then an order to its track.
            var cutB = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat }, new List<bool> { false, false }, world.Network.Tracks[free[2].Track], false, false);
            yield return new WaitForSeconds(3);
            // Persistent Jobs queues a pass asked for while one runs: asked again until the cut is gone.
            // Deleted cars go back to the game's pool, deactivated, rather than destroyed.
            bool Gone(TrainCar c) => c == null || !c.gameObject.activeInHierarchy || c.logicCar == null;
            for (var i = 0; i < 120 && !cutB.All(Gone); i++)
            {
                if (i % 10 == 0)
                    runSuspend.Invoke(null, new object[] { true, new List<StationController> { station }, null });
                yield return new WaitForSeconds(1);
            }
            // Suspended: the cut taken out of the world (Persistent Jobs' table is read by the engineer).
            var suspended = cutB.All(Gone);
            var broughtBefore = s.BroughtBack;
            Dispatch.GoToTrack(s, world, free[2], queue: false);
            var sawWait = false;
            waited = 0;
            while (waited < 360 && (s.Engineer.Queue.Count > 0 || waited < 5))
            {
                yield return new WaitForSeconds(1);
                waited += 1;
                sawWait |= (s.Engineer.Status ?? "").StartsWith("Waiting for Persistent Jobs", StringComparison.Ordinal);
            }
            // The hold can be shorter than a second's look: the engineer's own count says it waited.
            sawWait |= s.BroughtBack > broughtBefore;
            var logic = world.Network.Tracks[free[2].Track].LogicTrack();
            var back = logic != null ? logic.GetCarsFullyOnTrack().Count + logic.GetCarsPartiallyOnTrack().Count : 0;
            var derailed = loco.trainset != null && loco.trainset.cars.Any(c => c == null || c.derailed);
            Check(name + ": an order to a suspended cut's track waits for it to come back, and goes in short of it",
                suspended && sawWait && back >= 2 && !derailed && s.Engineer.Queue.Count == 0,
                $"suspended first {suspended}; waited for Persistent Jobs {sawWait}; cars on {free[2].Id} after {back}; derailed {derailed}; {waited:F0} s; status '{s.Engineer.Status}'");
            s.Stop(secure: true);
            yield return new WaitForSeconds(3);
        }
    }
}
