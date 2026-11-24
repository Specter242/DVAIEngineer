using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Game;
using DV.ThingTypes;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    /// <summary>
    /// "crossmap" and "crossride": a light DE2 from MF-A1P at the Machine
    /// Factory (a stall of its shed, a turntable's, has no route: turntables
    /// are left out of the network) to HB-D7L at the Harbor, across the map,
    /// with the player left at the start or riding in the cab. Ender
    /// Voidwalker (Discord, 2026-11-21) had an engineer "sort of die" on that
    /// run, southbound from "the little train house thing" at MF to HB-DL7
    /// (the booklet's HB-D7L); picking the destination again set it going. Every standstill of more than a minute on the way is logged
    /// with what the engineer said.
    /// </summary>
    internal static partial class SelfTest
    {
        private static IEnumerator CrossMapPart(bool ride)
        {
            var name = $"across the map, MF-A1P to HB-D7L, {(ride ? "you riding" : "you left at MF")}";
            var world = Host.World;
            world.ScanCars();
            var player = PlayerManager.PlayerTransform.position - WorldMover.currentMove;
            var yards = Dispatch.Yards(world.Network, new Vector2(player.x, player.z));
            var mf = yards.FirstOrDefault(y => y.Id == "MF");
            var hb = yards.FirstOrDefault(y => y.Id == "HB");
            if (mf == null || hb == null)
            {
                Fail(name, $"MF or HB not found among {string.Join(", ", yards.Select(y => y.Id))}");
                yield break;
            }
            string Norm(string id) => new string(id.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            var dest = hb.Tracks.FirstOrDefault(t => Norm(t.Id) == "HBD7L" || Norm(t.Id) == "HBDL7");
            Line($"  {name}: MF tracks {string.Join(", ", mf.Tracks.Select(t => $"{t.Id} {t.Length:F0} m{(t.DeadEnd ? " stub" : "")}{(world.Occupancy.IsOccupied(t.Track, int.MinValue) ? " taken" : "")}"))}");
            if (dest == null)
            {
                Fail(name, $"HB-D7L not found among {string.Join(", ", hb.Tracks.Select(t => t.Id))}");
                yield break;
            }
            var start = mf.Tracks.FirstOrDefault(t => Norm(t.Id) == "MFA1P" && !world.Occupancy.IsOccupied(t.Track, int.MinValue));
            if (start == null)
            {
                Fail(name, "MF-A1P not found, or not free");
                yield break;
            }
            var de2 = DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "LocoDE2");
            var spot = Host.Network.MapPointAt(new Core.Tracks.TrackPosition(start.Track, start.Length / 2));
            PlayerManager.TeleportPlayer(new Vector3((float)spot.x, 0, (float)spot.y) + WorldMover.currentMove + Vector3.up * 20, Quaternion.identity, null, useRotation: false);
            yield return new WaitForSeconds(8);
            var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { de2 }, new List<bool> { false }, world.Network.Tracks[start.Track], false, false);
            if (cars == null || cars.Count < 1)
            {
                Fail(name, $"the train couldn't be spawned on {start.Id}");
                yield break;
            }
            yield return new WaitForSeconds(3);
            foreach (var c in cars)
                c.brakeSystem?.SetHandbrakePosition(0f);
            var loco = cars.First(c => c.IsLoco);
            DV.Simulation.Controllers.StartupHelper.Startup(loco);
            yield return new WaitForSeconds(3);
            if (ride)
            {
                PlayerManager.TeleportPlayerToCar(loco);
                yield return new WaitForSeconds(6);
            }
            else
            {
                PlayerManager.TeleportPlayer(loco.transform.position + loco.transform.right * 20 + Vector3.up * 3, Quaternion.identity, null, useRotation: false);
                yield return new WaitForSeconds(3);
            }
            world.ScanCars();
            var s = Host.SessionFor(loco, true);
            Dispatch.GoToTrack(s, world, dest, queue: false);
            Line($"  {name}: loco {loco.ID} on {start.Id}, ordered to {dest.Id}; status '{s.Engineer.Status}'");
            float t = 0, still = 0, longest = 0, top = 0, startedAt = -1;
            string longestAt = "";
            var stalls = new List<string>();
            var lastStatus = "";
            while (t < 3000 && s.Engineer.Queue.Count > 0 && s.Engineer.Mode == EngineerMode.Waypoints
                && !(startedAt < 0 && t > 120 && (s.Engineer.Status ?? "").StartsWith("No route", StringComparison.Ordinal)))
            {
                yield return new WaitForSeconds(1);
                t += 1;
                var v = Mathf.Abs(loco.GetForwardSpeed());
                top = Mathf.Max(top, v);
                if (startedAt < 0 && v > 0.5f)
                    startedAt = t;
                var status = s.Engineer.Status ?? "";
                if (v < 0.05f && startedAt >= 0)
                {
                    still += 1;
                    if (still == 60)
                        stalls.Add(string.Format(CultureInfo.InvariantCulture, "t={0:F0} '{1}'", t, status));
                    if (still > longest)
                    {
                        longest = still;
                        longestAt = string.Format(CultureInfo.InvariantCulture, "t={0:F0} '{1}'", t, status);
                    }
                }
                else
                {
                    still = 0;
                }
                if (status != lastStatus || Mathf.RoundToInt(t) % 60 == 0)
                {
                    var p = loco.transform.position - WorldMover.currentMove;
                    Line(string.Format(CultureInfo.InvariantCulture, "  {0}: t={1:F0} {2:F0} km/h at ({3:F0}, {4:F0}) | {5}", name, t, v * 3.6f, p.x, p.z, status));
                    lastStatus = status;
                }
            }
            yield return new WaitForSeconds(5);
            var after = s.Read();
            var onDest = after.EndA.Track == dest.Track || after.EndB.Track == dest.Track;
            Check(name + ": gets there without standing a minute on the way",
                s.Engineer.Queue.Count == 0 && onDest && stalls.Count == 0,
                string.Format(CultureInfo.InvariantCulture, "after {0:F0} s: {1} orders left, on {2} {3}; started after {4:F0} s, top {5:F0} km/h; longest standstill {6:F0} s ({7}); standing over a minute: {8}; mode {9}, '{10}'",
                    t, s.Engineer.Queue.Count, dest.Id, onDest, startedAt, top * 3.6f, longest, longestAt, stalls.Count == 0 ? "none" : string.Join("; ", stalls), s.Engineer.Mode, s.Engineer.Status));
            s.Stop(secure: true);
            if (ride)
                PlayerManager.TeleportPlayer(loco.transform.position + loco.transform.right * 15 + Vector3.up * 3, Quaternion.identity, null, useRotation: false);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }
    }
}
