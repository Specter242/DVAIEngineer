using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Game;
using AIEngineer.UI.Tablet;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    /// <summary>
    /// "boarding": the player steps into the cab of a loco the engineer
    /// drives, moving and standing, on steam (crew and player firing) and a
    /// diesel. With Realistic Brakes (--with RealisticBrakes) the S282's and
    /// S060's independent is its S-6, which springs out of release and quick
    /// application as the cab loads; 0.15.3 took that for the player's hand
    /// and gave the controls back (Aro1225 and Ender, 2026-11-19). The
    /// engineer keeps the train, the loco (a company one) is on the tablet's
    /// list with its button, and a lever the player does move still hands
    /// back, the notice naming it.
    /// "cantmove": a DE2 too heavy for wet rail up a grade gives up after a
    /// fair try, says so, and holds the train; a new order tries again.
    /// </summary>
    internal static partial class SelfTest
    {
        private static IEnumerator BoardingPart()
        {
            var world = Host.World;
            world.ScanCars();
            var track = LevelTracks(world).FirstOrDefault(c => c.Length >= 1800);
            if (track == null)
            {
                Fail("boarding", "no long level track clear");
                yield break;
            }
            var brakes = Type.GetType("RealisticBrakes.Main, RealisticBrakes", false) != null;
            Line($"boarding: {(brakes ? "with Realistic Brakes" : "stock brakes")}");
            var handTried = false;
            foreach (var (power, playerFires) in new[] { (new[] { "LocoS282A", "LocoS282B" }, false), (new[] { "LocoS282A", "LocoS282B" }, true), (new[] { "LocoS060" }, false), (new[] { "LocoDE2" }, false) })
            {
                var name = $"boarding: {power[0].Replace("Loco", "").TrimEnd('A')}{(playerFires ? ", you fire" : "")}";
                PlayerManager.TeleportPlayer(track.Middle + Vector3.right * 15 + Vector3.up * 3, Quaternion.identity, null, useRotation: false);
                yield return new WaitForSeconds(1);
                yield return BuildTrain(world, track, power, 2, false, true);
                var cars = builtCars;
                if (cars == null)
                {
                    Fail(name, "couldn't place the train");
                    continue;
                }
                foreach (var c in cars)
                    c.brakeSystem?.SetHandbrakePosition(0f);
                var loco = cars.First(c => c.IsLoco);
                var s = Host.SessionFor(loco, true);
                if (s.Steam != null)
                    s.Steam.PlayerFires = playerFires;
                var o = loco.SimController.controlsOverrider;
                string Levers() => string.Format(CultureInfo.InvariantCulture, "throttle {0:F2}, reverser {1:F2}, train brake {2:F2}, loco brake {3:F2}",
                    o.Throttle?.Value ?? -1, o.Reverser?.Value ?? -1, o.Brake?.Value ?? -1, o.IndependentBrake?.Value ?? -1);
                Vector3 Away() => loco.transform.position + loco.transform.right * 15 + Vector3.up * 3;
                foreach (var phase in new[] { "moving", "standing" })
                {
                    if (s.Engineer.Mode == EngineerMode.Off)
                        s = Host.SessionFor(loco, true);
                    if (phase == "moving")
                    {
                        s.SetRoad(true, 20 / 3.6);
                        // Under Realistic Brakes the air takes a while to charge first.
                        for (var i = 0; i < 180 && Mathf.Abs(loco.GetForwardSpeed()) < 15 / 3.6f; i++)
                            yield return new WaitForSeconds(1);
                        yield return new WaitForSeconds(3);
                    }
                    else
                    {
                        s.StopHere();
                        for (var i = 0; i < 60 && Mathf.Abs(loco.GetForwardSpeed()) > 0.05f; i++)
                            yield return new WaitForSeconds(1);
                        yield return new WaitForSeconds(10);
                    }
                    var label = $"{name}, {phase}";
                    var before = Levers();
                    var speed = loco.GetForwardSpeed() * 3.6f;
                    PlayerManager.TeleportPlayerToCar(loco);
                    yield return new WaitForSeconds(4);
                    TabletUi.RefreshForTest();
                    var listed = TabletUi.Engineers.Contains(loco);
                    var kept = s.Engineer.Mode != EngineerMode.Off;
                    Check(label, kept && listed && PlayerManager.Car == loco,
                        $"at {speed:F1} km/h, {before}; aboard: {Levers()}; engineer {s.Engineer.Mode} ('{s.Engineer.Status}'); player in {PlayerManager.Car?.ID}; " +
                        $"on the tablet's list {listed} (the player's own {loco.uniqueCar}); said '{(kept ? "" : Main.LastNotified)}'");

                    // The player's own hand on a lever, the cab long settled: it hands back, and says which.
                    if (!handTried && kept && phase == "standing")
                    {
                        handTried = true;
                        Main.LastNotified = null;
                        var regulator = o.Throttle;
                        regulator?.Set(0.5f);
                        yield return new WaitForSeconds(3);
                        var told = Main.LastNotified ?? "";
                        Check("boarding: the player's hand on the throttle hands back, naming it", s.Engineer.Mode == EngineerMode.Off && told.Contains("throttle"),
                            $"engineer {s.Engineer.Mode}; told '{told}'");
                        regulator?.Set(0f);
                    }
                    PlayerManager.TeleportPlayer(Away(), Quaternion.identity, null, useRotation: false);
                    yield return new WaitForSeconds(3);
                }
                s.Stop(secure: true);
                yield return new WaitForSeconds(3);
                yield return ClearSpawned(cars);
            }
        }

        /// <summary>
        /// "trainbrake": a DE6 and 10 loaded hoppers on the level to a mark and
        /// back (Ender Voidwalker, 2026-11-19: the independent at full tilt
        /// with cars behind): how far from each mark it stops, how much loco
        /// brake it used on the move, and how long the start from the first
        /// stop took (the air's release and recharge under Realistic Brakes).
        /// "cabbrake": a DE2, a DH4 and a DE6 with cars, each to a mark with
        /// the player in its cab, its levers loaded, and each train brake
        /// lever logged. Realistic Brakes widens a stepped train brake handle
        /// there, a sprung filling stroke past release, and 0.16.0 read that
        /// spring on the stock scale: it would hold the handle a notch into
        /// service (UserPaddingFTW, Nexus, 2026-11-22: the train waits, then
        /// runs, with its brakes dragging).
        /// </summary>
        private static IEnumerator TrainBrakePart(bool aboard = false)
        {
            if (!aboard)
            {
                yield return TrainBrakeRun("LocoDE6", 10, false, true);
                yield break;
            }
            foreach (var (id, hoppers) in new[] { ("LocoDE2", 5), ("LocoDH4", 8), ("LocoDE6", 10) })
                yield return TrainBrakeRun(id, hoppers, true, false);
        }

        private static IEnumerator TrainBrakeRun(string locoId, int hoppers, bool aboard, bool back)
        {
            var world = Host.World;
            world.ScanCars();
            var track = LevelTracks(world).FirstOrDefault(c => c.Length >= 1800);
            var brakes = Type.GetType("RealisticBrakes.Main, RealisticBrakes", false) != null;
            var name = $"train brake: a {locoId.Replace("Loco", "")} and {hoppers} loaded hoppers, {(brakes ? "Realistic Brakes" : "stock air")}{(aboard ? ", you in the cab" : "")}";
            if (track == null)
            {
                Fail(name, "no long level track clear");
                yield break;
            }
            yield return BuildTrain(world, track, new[] { locoId }, hoppers, false, true);
            var cars = builtCars;
            if (cars == null)
            {
                Fail(name, "couldn't place the train");
                yield break;
            }
            foreach (var c in cars)
                c.brakeSystem?.SetHandbrakePosition(0f);
            var loco = cars.First(c => c.IsLoco);
            var s = Host.SessionFor(loco, true);
            var o = loco.SimController.controlsOverrider;
            if (aboard)
            {
                PlayerManager.TeleportPlayerToCar(loco);
                yield return new WaitForSeconds(6);
                Line(string.Format(CultureInfo.InvariantCulture, "  {0}: aboard {1}, cab loaded {2}; train brake {3:F2}; levers: {4}", name, PlayerManager.Car?.ID, loco.IsInteriorLoaded, o.Brake?.Value ?? -1f, BrakeLevers(loco)));
            }
            var points = new[] { track.Length * 0.12, track.Length * 0.88 };
            var here = s.Read().EndA.Offset;
            var first = Math.Abs(points[0] - here) > Math.Abs(points[1] - here) ? points[0] : points[1];
            var second = first == points[0] ? points[1] : points[0];
            var legs = back ? new[] { ("to the mark", first), ("back to the other", second) } : new[] { ("to the mark", first) };
            foreach (var (leg, at) in legs)
            {
                var goal = new Core.Tracks.TrackPosition(track.Track, at);
                s.Engineer.GoTo(new Waypoint(goal) { Label = "train brake test" });
                float elapsed = 0, startedIn = -1, heavy = 0, mostMoving = 0, trainMost = 0, top = 0, trainWaiting = 0, trainCruising = 0;
                while (elapsed < 600 && s.Engineer.Queue.Count > 0 && s.Engineer.Mode == EngineerMode.Waypoints)
                {
                    yield return new WaitForSeconds(0.5f);
                    elapsed += 0.5f;
                    var v = Mathf.Abs(loco.GetForwardSpeed());
                    top = Mathf.Max(top, v);
                    if (startedIn < 0 && v > 0.5f)
                        startedIn = elapsed;
                    var ind = o.IndependentBrake?.Value ?? 0f;
                    if (startedIn < 0)
                        trainWaiting = Mathf.Max(trainWaiting, o.Brake?.Value ?? 0f);
                    // The first 30 s under way: the brake let off for the run.
                    else if (elapsed - startedIn < 30)
                        trainCruising = Mathf.Max(trainCruising, o.Brake?.Value ?? 0f);
                    if (v > 0.3f)
                    {
                        mostMoving = Mathf.Max(mostMoving, ind);
                        if (ind > 0.25f)
                            heavy += 0.5f;
                        trainMost = Mathf.Max(trainMost, o.Brake?.Value ?? 0f);
                    }
                    if (Mathf.RoundToInt(elapsed * 2) % 40 == 0)
                        Line(string.Format(CultureInfo.InvariantCulture, "  {0}, {1}: t={2:F0} v={3:F1} km/h ind {4:F2} train brake {5:F2} | {6}", name, leg, elapsed, v * 3.6f, ind, o.Brake?.Value ?? 0f, s.Engineer.Status));
                }
                yield return new WaitForSeconds(5);
                var after = s.Read();
                var gap = Math.Min(Gap(after.EndA, goal), Gap(after.EndB, goal));
                Check($"{name}, {leg}",
                    s.Engineer.Queue.Count == 0 && gap < 6 && (!aboard || (trainWaiting < 0.01f && trainCruising < 0.01f)),
                    string.Format(CultureInfo.InvariantCulture, "stopped {0:F1} m from the mark after {1:F0} s (top {2:F0} km/h); started moving after {3:F0} s, the train brake at most {10:F2} until then and {11:F2} for 30 s after; loco brake on the move at most {4:F2}, over 0.25 for {5:F1} s; train brake at most {6:F2}; held with loco brake {7:F2}, train brake {8:F2}; '{9}'",
                        gap, elapsed, top * 3.6f, startedIn, mostMoving, heavy, trainMost, o.IndependentBrake?.Value ?? 0f, o.Brake?.Value ?? 0f, s.Engineer.Status, trainWaiting, trainCruising));
            }
            s.Stop(secure: true);
            if (aboard)
                PlayerManager.TeleportPlayer(loco.transform.position + loco.transform.right * 15 + Vector3.up * 3, Quaternion.identity, null, useRotation: false);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }

        /// <summary>The cab's brake levers: notches, stepped, spring limits, and whether Realistic Brakes widened it.</summary>
        private static string BrakeLevers(TrainCar loco)
        {
            var interior = loco.loadedInterior;
            if (interior == null)
                return "no cab";
            var said = new System.Text.StringBuilder();
            foreach (var lever in interior.GetComponentsInChildren<DV.CabControls.Spec.Lever>(true))
            {
                if (lever.name.IndexOf("rake", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var widened = lever.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "TrainBrakeHandle");
                said.Append(string.Format(CultureInfo.InvariantCulture, "[{0}: notches {1}, stepped {2}, stepped value {3}, spring {4} {5}-{6}, widened {7}, value {8}] ",
                    lever.name, lever.notches, lever.useSteppedJoint, lever.steppedValueUpdate, lever.useInnerLimitSpring, lever.innerLimitMinNotch, lever.innerLimitMaxNotch, widened,
                    lever.GetComponent<DV.CabControls.ControlImplBase>()?.Value ?? -1f));
            }
            return said.Length == 0 ? "none named *rake*" : said.ToString();
        }

        private static IEnumerator CantMovePart()
        {
            var world = Host.World;
            world.ScanCars();
            var climbs = Climbs(world);
            var slope = climbs.Where(c => c.Grade >= 0.017 && c.Grade <= 0.02 && c.Length >= 600).OrderByDescending(c => c.Length).FirstOrDefault();
            const string name = "can't move: a DE2 and 8 loaded hoppers on wet rail up a grade";
            if (slope == null)
            {
                Fail(name, "no such grade clear");
                yield break;
            }
            SetWetness(1f);
            yield return BuildTrain(world, slope, new[] { "LocoDE2" }, 8, true, false);
            var cars = builtCars;
            if (cars == null)
            {
                Fail(name, "couldn't place the train");
                SetWetness(0f);
                yield break;
            }
            foreach (var c in cars)
                c.brakeSystem?.SetHandbrakePosition(0f);
            var loco = cars[0];
            var s = Host.SessionFor(loco, true);
            var start = loco.transform.position;
            var ahead = loco.transform.forward;
            Main.LastNotified = null;
            s.SetRoad(true, 30 / 3.6);
            float waited = 0;
            bool GaveUp() => s.Engineer.Status == "Can't get the train moving here" || s.Engineer.Status == "Can't keep the train moving here";
            var least = 0f;
            while (waited < 420 && !GaveUp())
            {
                yield return new WaitForSeconds(1);
                waited += 1;
                least = Mathf.Min(least, Vector3.Dot(loco.transform.position - start, ahead));
                if (Mathf.RoundToInt(waited) % 30 == 0)
                    Line($"  {name}: t={waited:F0} along {Vector3.Dot(loco.transform.position - start, ahead):F1} m, {loco.GetForwardSpeed() * 3.6f:F1} km/h | {s.Engineer.Status}");
            }
            var told = s.LastNote ?? "";
            var shown = Main.LastNotified ?? "";
            yield return new WaitForSeconds(30);
            var still = Mathf.Abs(loco.GetForwardSpeed()) < 0.05f;
            var along = Vector3.Dot(loco.transform.position - start, ahead);
            var gaveUp = GaveUp();
            // Rolling back, it gives up once 5 m down (and stops in a few more); else after 180 s of trying.
            Check(name + ": gives up after a fair try, says so, and holds it",
                gaveUp && least > -15 && shown.Contains(loco.ID) && told.Contains("can't") && told.Contains("holding the train on its brakes") && still
                && s.Engineer.Mode == EngineerMode.Waypoints && s.Engineer.Queue.Count == 0,
                $"after {waited:F0} s: status '{s.Engineer.Status}'; told '{shown}'; rolled back at most {-least:F1} m; 30 s on standing {still} at {along:F1} m from the start; mode {s.Engineer.Mode}, {s.Engineer.Queue.Count} orders");
            s.SetRoad(true, 30 / 3.6);
            yield return new WaitForSeconds(5);
            Check(name + ": a new order tries again", !GaveUp() && s.Engineer.Mode == EngineerMode.Road,
                $"status '{s.Engineer.Status}', mode {s.Engineer.Mode}");
            s.Stop(secure: true);
            SetWetness(0f);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }
    }
}
