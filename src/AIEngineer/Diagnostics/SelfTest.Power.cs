using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using AIEngineer.UI.Tablet;
using DV.Logic.Job;
using DV.Simulation.Controllers;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using HarmonyLib;
using LocoSim.Implementations;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    /// <summary>
    /// Run only when named, each in a fresh game (they move the player about
    /// the map). "motors": heavy trains climbing with the throttle eased as the
    /// traction motors heat, and a fuse lost on the run. "grip": a DE2 and 300 t
    /// on dry and wet rail, the published throttle against the present one. "firing": a steam loco
    /// the player fires, on whatever firing the clone has (stock, or Realistic
    /// Firing with --with RealisticFiring): the engineer leaves the fire alone,
    /// warns, and waits for steam and water in every kind of order.
    /// "gradestart": heavy trains started uphill. "cssetout": a set-out from
    /// City South's A3L onto C1O with cars on it, as a job.
    /// "firebutton": the Engineers card's Fire button, through the Comms
    /// Tablet's app entries (merged, with --with CommsTablet).
    /// </summary>
    internal static partial class SelfTest
    {
        private sealed class Slope
        {
            public int Track;
            public double Length, Grade;
            public bool Rising;
            public string Name;
            public Vector3 Middle;
        }

        private static List<Slope> Slopes(World world, Func<float[], double, bool> wanted)
        {
            var found = new List<Slope>();
            for (var t = 0; t < world.Network.Tracks.Length; t++)
            {
                var track = world.Network.Tracks[t];
                var set = track.GetKinkedPointSet();
                if (set == null || set.points == null || set.points.Length < 8 || world.Occupancy.IsOccupied(t, int.MinValue))
                    continue;
                var heights = set.points.Select(p => (float)p.position.y).ToArray();
                if (!wanted(heights, set.span))
                    continue;
                var middle = set.points[set.points.Length / 2].position;
                found.Add(new Slope
                {
                    Track = t,
                    Length = set.span,
                    Grade = Math.Abs(heights[heights.Length - 1] - heights[0]) / set.span,
                    Rising = heights[heights.Length - 1] > heights[0],
                    Name = track.name,
                    Middle = new Vector3((float)middle.x, (float)middle.y, (float)middle.z),
                });
            }
            return found;
        }

        private static List<Slope> Climbs(World world) => Slopes(world, (h, span) =>
        {
            var n = h.Length - 1;
            int a = 0, b = n / 4, c = n / 2, d = 3 * n / 4;
            return span >= 340 && ((h[a] < h[b] && h[b] < h[c] && h[c] < h[d] && h[d] < h[n]) || (h[a] > h[b] && h[b] > h[c] && h[c] > h[d] && h[d] > h[n]));
        }).OrderByDescending(s => s.Grade).ToList();

        private static List<Slope> LevelTracks(World world) => Slopes(world, (h, span) => span >= 600 && h.Max() - h.Min() <= 0.002 * span)
            .OrderByDescending(s => s.Length).ToList();

        private static void CoupleEveryCar(List<TrainCar> cars)
        {
            foreach (var c in cars)
            {
                if (c.frontCoupler != null && c.frontCoupler.coupledTo == null)
                    c.frontCoupler.TryCouple(false, false, 5f);
                if (c.rearCoupler != null && c.rearCoupler.coupledTo == null)
                    c.rearCoupler.TryCouple(false, false, 5f);
            }
        }

        private static TrainCarLivery Livery(string id) => DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == id);

        // What BuildTrain made, for the step after it.
        private static List<TrainCar> builtCars;

        /// <summary>
        /// A train with its loco leading, front first, uphill on a slope (from
        /// its low end if <paramref name="fromLowEnd"/>), loaded, coupled,
        /// started, and held by the engineer on its brakes.
        /// </summary>
        private static IEnumerator BuildTrain(World world, Slope slope, string[] power, int hoppers, bool fromLowEnd, bool level, int emptyHoppers = 0)
        {
            builtCars = null;
            PlayerManager.TeleportPlayer(slope.Middle + Vector3.right * 12 + Vector3.up * 3, Quaternion.identity, null, useRotation: false);
            yield return new WaitForSecondsRealtime(10);
            var hopper = TrainCarType.HopperBrown.ToV2();
            var length = power.Length * 20 + (hoppers + emptyHoppers) * 14.0 + 20;
            var start = !fromLowEnd ? slope.Length / 2 - length / 2 : slope.Rising ? 30 : slope.Length - length - 30;
            var track = world.Network.Tracks[slope.Track];
            foreach (var reversed in new[] { false, true })
            {
                foreach (var flip in new[] { false, true })
                {
                    var list = power.Select(Livery).Concat(Enumerable.Repeat(hopper, hoppers + emptyHoppers)).ToList();
                    if (reversed)
                        list.Reverse();
                    var cars = CarSpawner.Instance.SpawnCarTypesOnTrackStrict(list, track, false, true, start, flip);
                    if (cars == null || cars.Count < list.Count)
                    {
                        if (cars != null)
                            yield return ClearSpawned(cars);
                        continue;
                    }
                    yield return new WaitForSeconds(1);
                    foreach (var c in cars)
                        c.brakeSystem?.SetHandbrakePosition(1f);
                    var loco = cars.First(c => c.IsLoco);
                    var wagons = cars.Where(c => !c.carLivery.id.StartsWith("Loco", StringComparison.Ordinal)).ToList();
                    var leads = wagons.Count == 0 || Vector3.Dot(loco.transform.forward, loco.transform.position - wagons.Aggregate(Vector3.zero, (v, c) => v + c.transform.position) / wagons.Count) > 0;
                    if (leads && (level || loco.transform.forward.y > 0))
                    {
                        yield return new WaitForSeconds(2);
                        foreach (var c in wagons.Take(hoppers))
                            c.logicCar.LoadCargo(c.logicCar.capacity, CargoType.Coal);
                        CoupleEveryCar(cars);
                        yield return new WaitForSeconds(4);
                        CoupleEveryCar(cars);
                        StartupHelper.Startup(loco);
                        yield return new WaitForSeconds(10);
                        world.ScanCars();
                        builtCars = cars;
                        yield break;
                    }
                    yield return ClearSpawned(cars);
                }
            }
        }

        /// <summary>The engineer holds the train on its brakes, then the hand brakes come off: a train left as a crew leaves one.</summary>
        private static IEnumerator HeldOnItsBrakes(Session s, List<TrainCar> cars)
        {
            s.SetRoad(true, 0);
            var wagons = cars.Where(c => !c.carLivery.id.StartsWith("Loco", StringComparison.Ordinal) && c.brakeSystem != null).ToList();
            for (var wait = 0; wait < 60; wait++)
            {
                yield return new WaitForSeconds(1);
                if (wait > 20 && (wagons.Count == 0 || wagons.Min(c => c.brakeSystem.brakeCylinderPressure) > 1.8f))
                    break;
            }
            foreach (var c in cars)
                c.brakeSystem?.SetHandbrakePosition(0f);
            yield return new WaitForSeconds(4);
        }

        private static IEnumerator MotorsPart()
        {
            var world = Host.World;
            world.ScanCars();
            var climbs = Climbs(world);
            var level = LevelTracks(world);
            Line("motors: steepest clear tracks: " + string.Join("; ", climbs.Take(8).Select(c => string.Format(CultureInfo.InvariantCulture, "{0} {1:F0} m {2:P2}", c.Name, c.Length, c.Grade))));
            Slope Long(double least, double most, double length) => climbs.Where(c => c.Grade >= least && c.Grade <= most && c.Length >= length).OrderByDescending(c => c.Length).FirstOrDefault();
            var de2 = new[] { "LocoDE2" };
            var de6 = new[] { "LocoDE6" };
            var be2 = new[] { "LocoMicroshunter" };
            yield return Caught("motors: DE2 from a stand", Climb(world, "a DE2 and 3 loaded hoppers start on a grade", climbs.FirstOrDefault(c => c.Grade >= 0.009 && c.Grade <= 0.016 && c.Length >= 342), de2, 3, 100, 180, false));
            yield return Caught("motors: DE2 long climb", Climb(world, "a DE2 and 3 loaded hoppers on a long climb", Long(0.014, 0.022, 1500), de2, 3, 1200, 600, true));
            yield return Caught("motors: DE2 steeper climb", Climb(world, "a DE2 and 3 loaded hoppers on a steeper climb", Long(0.018, 0.022, 1500), de2, 3, 1200, 600, true));
            yield return Caught("motors: DE6 long climb", Climb(world, "a DE6 and 13 loaded hoppers on a long climb", Long(0.014, 0.022, 1500), de6, 13, 1000, 600, true));
            yield return Caught("motors: BE2", Climb(world, "a BE2 and a loaded hopper on a grade", Long(0.009, 0.022, 500), be2, 1, 300, 300, true));
            yield return Caught("motors: level", Climb(world, "a DE2 and 6 loaded hoppers on the level", level.FirstOrDefault(c => c.Length >= 1500), de2, 6, 1000, 420, true, true));
            yield return Caught("motors: the fuse", FuseLost(world, level.FirstOrDefault(c => c.Length >= 1000)));
        }

        private static IEnumerator Climb(World world, string name, Slope slope, string[] power, int hoppers, double meters, float seconds, bool fromLowEnd, bool isLevel = false)
        {
            name = "motors: " + name;
            if (slope == null || power.Any(p => Livery(p) == null))
            {
                Fail(name, slope == null ? "no such track clear" : "no such loco");
                yield break;
            }
            yield return BuildTrain(world, slope, power, hoppers, fromLowEnd, isLevel);
            var cars = builtCars;
            if (cars == null)
            {
                Fail(name, $"couldn't place the train on {slope.Name}");
                yield break;
            }
            var loco = cars.First(c => c.IsLoco);
            var s = Host.SessionFor(loco, true);
            yield return HeldOnItsBrakes(s, cars);
            s.SetRoad(true, 40 / 3.6);
            double run = 0;
            float elapsed = 0, hottest = 0, eased = 0, slowest = float.MaxValue;
            var fuse = false;
            while (elapsed < seconds && run < meters)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                var v = loco.GetForwardSpeed();
                run += Math.Max(0, v);
                var heat = s.Motors != null ? s.Motors.Temperature : 0f;
                hottest = Mathf.Max(hottest, heat);
                if (heat > 100)
                    eased += 1;
                if (run > 30)
                    slowest = Mathf.Min(slowest, Mathf.Abs(v));
                fuse |= s.Motors != null && s.Motors.FuseOff;
                if (cars.Any(c => c == null || c.derailed))
                    break;
                if ((int)elapsed % 15 == 0)
                    Line(string.Format(CultureInfo.InvariantCulture, "  {0}: t={1:F0} run={2:F0} m v={3:F1} km/h throttle={4:F2} motors={5:F1} C | {6}", name, elapsed, run, v * 3.6f, s.Controls.Throttle, heat, s.Engineer.Status));
            }
            var derailed = cars.Any(c => c == null || c.derailed);
            var detail = string.Format(CultureInfo.InvariantCulture, "{0} {1:P2}: {2:F0} m in {3:F0} s; hottest {4:F1} C, {5:F0} s over 100 C; slowest once moving {6:F1} km/h; fuse off {7}; derailed {8}; status '{9}'",
                slope.Name, slope.Grade, run, elapsed, hottest, eased, slowest * 3.6f, fuse, derailed, s.Engineer.Status);
            (run >= meters && !fuse && !derailed && hottest < 120 ? (Action<string, string>)Pass : Fail)(name, detail);
            s.Stop(secure: true);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }

        private static IEnumerator FuseLost(World world, Slope track)
        {
            const string name = "motors: the fuse lost on the run";
            yield return BuildTrain(world, track, new[] { "LocoDE2" }, 0, false, true);
            var cars = builtCars;
            if (cars == null)
            {
                Fail(name, "couldn't place the loco");
                yield break;
            }
            var loco = cars[0];
            var s = Host.SessionFor(loco, true);
            foreach (var c in cars)
                c.brakeSystem?.SetHandbrakePosition(0f);
            s.SetRoad(true, 30 / 3.6);
            for (var i = 0; i < 40 && Mathf.Abs(loco.GetForwardSpeed()) < 5; i++)
                yield return new WaitForSeconds(1);
            var set = loco.SimController.simFlow.OrderedSimComps.OfType<TractionMotorSet>().First();
            var reference = (FuseReference)TractionMotors.FuseField.GetValue(set);
            loco.SimController.simFlow.TryGetFuse(reference.fuseId, out var fuse);
            var speedBefore = Mathf.Abs(loco.GetForwardSpeed());
            fuse.ChangeState(false);
            float waited = 0;
            while (waited < 60 && Mathf.Abs(loco.GetForwardSpeed()) > 0.05f)
            {
                yield return new WaitForSeconds(1);
                waited += 1;
            }
            yield return new WaitForSeconds(5);
            var status = s.Engineer.Status;
            var throttle = s.Controls.Throttle;
            var note = s.LastNote ?? "";
            var held = status == TractionMotors.FuseStatus && throttle < 0.01f && Mathf.Abs(loco.GetForwardSpeed()) < 0.05f && note.Contains("fuse");
            (held ? (Action<string, string>)Pass : Fail)(name, $"from {speedBefore * 3.6f:F0} km/h, stopped in {waited:F0} s; throttle {throttle:F2}; status '{status}'; told '{note}'");
            fuse.ChangeState(true);
            var start = loco.transform.position;
            waited = 0;
            while (waited < 40 && Vector3.Distance(loco.transform.position, start) < 5)
            {
                yield return new WaitForSeconds(1);
                waited += 1;
            }
            (Vector3.Distance(loco.transform.position, start) >= 5 ? (Action<string, string>)Pass : Fail)("motors: it goes on once the fuse is back", $"{Vector3.Distance(loco.transform.position, start):F1} m in {waited:F0} s; status '{s.Engineer.Status}'");
            s.Stop(secure: true);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }

        // ---- Grip ----

        private static void SetWetness(float wetness)
        {
            var driverType = Type.GetType("DV.WeatherSystem.WeatherDriver, DV.WeatherSystem", true);
            var driver = driverType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
            var value = driverType.GetProperty("WetnessValue").GetValue(driver);
            value.GetType().GetMethod("EngageOverride").Invoke(value, new object[] { wetness });
        }

        /// <summary>
        /// A DE2 and about 300 t started on dry and wet rail, on the grade
        /// nearest north of Steel Mill and on a steeper one: the throttle as
        /// 0.15.3 published it, and as it is now. Slip, sand, and wheel wear
        /// are logged for each.
        /// </summary>
        private static IEnumerator GripPart()
        {
            var world = Host.World;
            world.ScanCars();
            var yard = Dispatch.Yards(world.Network, Vector2.zero).FirstOrDefault(y => y.Id == "SM");
            var sm = yard.Tracks.Select(t => world.Network.MapPointAt(new TrackPosition(t.Track, t.Length / 2))).ToList();
            var at = new Vector2(sm.Average(p => p.x), sm.Average(p => p.y));
            Vector2 Mid(Slope c) => world.Network.MapPointAt(new TrackPosition(c.Track, c.Length / 2));
            double Off(Slope c) => Vector2.Distance(Mid(c), at);
            var climbs = Climbs(world);
            var near = climbs.Where(c => c.Grade >= 0.003 && c.Grade <= 0.012 && c.Length >= 420 && Mid(c).y > at.y).OrderBy(Off).ToList();
            Line("grip: Steel Mill at " + at + "; grades north of it: " + string.Join("; ", near.Take(6).Select(c => string.Format(CultureInfo.InvariantCulture, "{0} {1:F0} m {2:P2}, {3:F0} m off", c.Name, c.Length, c.Grade, Off(c)))));
            var steep = climbs.FirstOrDefault(c => c.Grade >= 0.012 && c.Grade <= 0.016 && c.Length >= 420);
            foreach (var slope in new[] { near.FirstOrDefault(), steep })
            {
                if (slope == null)
                {
                    Fail("grip", "a grade wasn't found");
                    continue;
                }
                foreach (var wet in new[] { 0f, 1f })
                {
                    foreach (var published in new[] { true, false })
                        yield return Caught("grip", GripTrial(world, slope, wet, published));
                }
            }
            SetWetness(0f);
        }

        private static IEnumerator GripTrial(World world, Slope slope, float wet, bool published)
        {
            SetWetness(wet);
            yield return BuildTrain(world, slope, new[] { "LocoDE2" }, 3, true, false, 1);
            var cars = builtCars;
            var label = string.Format(CultureInfo.InvariantCulture, "{0}, {1} rail, {2} {3:P2}", published ? "as published" : "now", wet > 0 ? "wet" : "dry", slope.Name, slope.Grade);
            if (cars == null)
            {
                Fail("grip", $"{label}: couldn't place the train");
                yield break;
            }
            var loco = cars.First(c => c.IsLoco);
            var s = Host.SessionFor(loco, true);
            var t = s.Engineer.Tuning;
            if (published)
            {
                t.ThrottleRate = 0.12;
                t.MotorEaseFrom = double.PositiveInfinity;
                t.SlipRecovery = double.PositiveInfinity;
                t.SandFor = t.SandMost = 10;
            }
            yield return HeldOnItsBrakes(s, cars);
            var flow = loco.SimController.simFlow;
            var sander = flow.OrderedSimComps.OfType<Sander>().FirstOrDefault();
            var wheels = loco.GetComponent<DV.Damage.DamageController>()?.wheels;
            var slipper = loco.SimController.wheelslipController;
            var sandStart = sander?.sand.Value ?? 0;
            var wheelsStart = wheels?.CurrentHitPoints ?? 0;
            var limit = slipper.TotalForceLimit / 1000f;
            s.SetRoad(true, 40 / 3.6);
            double run = 0;
            float elapsed = 0, slipping = 0, sanding = 0, hottest = 0, topSlip = 0;
            var bouts = 0;
            var was = false;
            var fuse = false;
            while (elapsed < 200 && run < 300)
            {
                yield return new WaitForSeconds(0.2f);
                elapsed += 0.2f;
                run += Math.Max(0, loco.GetForwardSpeed()) * 0.2;
                var slip = slipper.wheelslip;
                if (slip > 0)
                    slipping += 0.2f;
                if (slip > 0 && !was)
                    bouts++;
                was = slip > 0;
                topSlip = Mathf.Max(topSlip, slip);
                if (sander != null && sander.sandFlowReadOut.Value > 0.05f)
                    sanding += 0.2f;
                hottest = Mathf.Max(hottest, s.Motors.Temperature);
                fuse |= s.Motors.FuseOff;
                if (cars.Any(c => c == null || c.derailed))
                    break;
                if (Mathf.RoundToInt(elapsed * 5) % 75 == 0)
                    Line(string.Format(CultureInfo.InvariantCulture, "  grip {0}: t={1:F0} run={2:F0} v={3:F1} km/h throttle={4:F2} slip={5:F2} sand={6:F2} motors={7:F0} C limit={8:F0} kN | {9}", label, elapsed, run, loco.GetForwardSpeed() * 3.6f, s.Controls.Throttle, slip, sander?.sandFlowReadOut.Value ?? 0, s.Motors.Temperature, slipper.TotalForceLimit / 1000f, s.Engineer.Status));
            }
            var mass = cars.Sum(c => c.massController.TotalMass) / 1000f;
            Line(string.Format(CultureInfo.InvariantCulture, "grip: RESULT {0}: {1:F0} t; {2:F0} m in {3:F0} s{4}; slipping {5:F1} s in {6} bouts (worst {7:F2}); sand ran {8:F1} s, used {9:F2} of {16:F0}; wheels lost {10:F3} of {11:F0} hit points; hottest {12:F0} C; fuse off {13}; force limit at the start {14:F0} kN; status '{15}'",
                label, mass, run, elapsed, run >= 300 ? "" : " (NOT THERE)", slipping, bouts, topSlip, sanding, sandStart - (sander?.sand.Value ?? 0), wheelsStart - (wheels?.CurrentHitPoints ?? 0), wheels?.fullHitPoints ?? 0, hottest, fuse, limit, s.Engineer.Status, sandStart));
            s.Stop(secure: true);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }

        // ---- Grade starts ----

        /// <summary>
        /// Heavy trains started uphill from a stand, held on their brakes: a
        /// DE2 and 302 t on wet rail on the grade nearest north of Steel Mill
        /// (it rolled back before power, 2026-11-18) and on 1.56 %; a DE6 and
        /// 9 loaded hoppers and an S282 and 5 on the steepest grade, dry.
        /// </summary>
        private static IEnumerator GradeStartPart()
        {
            var world = Host.World;
            world.ScanCars();
            var climbs = Climbs(world);
            var yard = Dispatch.Yards(world.Network, Vector2.zero).FirstOrDefault(y => y.Id == "SM");
            var sm = yard.Tracks.Select(t => world.Network.MapPointAt(new TrackPosition(t.Track, t.Length / 2))).ToList();
            var at = new Vector2(sm.Average(p => p.x), sm.Average(p => p.y));
            Vector2 Mid(Slope c) => world.Network.MapPointAt(new TrackPosition(c.Track, c.Length / 2));
            var north = climbs.Where(c => c.Grade >= 0.003 && c.Grade <= 0.012 && c.Length >= 420 && Mid(c).y > at.y).OrderBy(c => Vector2.Distance(Mid(c), at)).FirstOrDefault();
            // About 1 % under the train, as the player described: the tracks near it, each until one stands so.
            var aboutOne = climbs.Where(c => c.Grade >= 0.008 && c.Grade <= 0.012 && c.Length >= 420).OrderBy(c => Vector2.Distance(Mid(c), at)).Take(5).ToList();
            foreach (var c in aboutOne)
            {
                gradeOk = false;
                yield return Caught("grade start", GradeStart(world, c, 1f, new[] { "LocoDE2" }, 3, 1, 300, middleFirst: true, within: (0.007, 0.013)));
                if (gradeOk)
                    break;
            }
            var moderate = climbs.FirstOrDefault(c => c.Grade >= 0.012 && c.Grade <= 0.016 && c.Length >= 420);
            var long185 = climbs.Where(c => c.Grade >= 0.017 && c.Grade <= 0.02 && c.Length >= 600).OrderByDescending(c => c.Length).FirstOrDefault();
            // Road 35, north of Steel Mill: "0.98 %" on average, about 1.6 % where a train stands on it.
            yield return Caught("grade start", GradeStart(world, north, 1f, new[] { "LocoDE2" }, 3, 1, 300, middleFirst: true));
            yield return Caught("grade start", GradeStart(world, moderate, 1f, new[] { "LocoDE2" }, 3, 1, 300));
            yield return Caught("grade start", GradeStart(world, long185, 0f, new[] { "LocoDE6" }, 9, 0, 300));
            yield return Caught("grade start", GradeStart(world, long185, 0f, new[] { "LocoS282A", "LocoS282B" }, 5, 0, 300));
            SetWetness(0f);
        }

        // Whether the last GradeStart found the train standing within the grade asked for.
        private static bool gradeOk;

        private static IEnumerator GradeStart(World world, Slope slope, float wet, string[] power, int hoppers, int empty, double meters, bool middleFirst = false, (double Least, double Most)? within = null)
        {
            var name = string.Format(CultureInfo.InvariantCulture, "grade start: {0} and {1} hoppers, {2} rail, {3} {4:P2}", power[0].Replace("Loco", "").Replace("A", ""), hoppers + empty, wet > 0 ? "wet" : "dry", slope?.Name, slope?.Grade ?? 0);
            if (slope == null)
            {
                Fail(name, "no such grade clear");
                yield break;
            }
            SetWetness(wet);
            List<TrainCar> cars = null;
            TrainCar loco = null;
            Session s = null;
            double uphill = 0;
            // From the low end, else from the middle: wherever it stands facing uphill.
            foreach (var fromLowEnd in middleFirst ? new[] { false, true } : new[] { true, false })
            {
                yield return BuildTrain(world, slope, power, hoppers, fromLowEnd, false, empty);
                cars = builtCars;
                if (cars == null)
                    continue;
                loco = cars.First(c => c.IsLoco);
                s = Host.SessionFor(loco, true);
                yield return HeldOnItsBrakes(s, cars);
                var held = s.Read();
                uphill = s.FrontEnd == ConsistEnd.A ? held.RiseTowardA : -held.RiseTowardA;
                if (uphill >= 0.004 && (within == null || (uphill >= within.Value.Least && uphill <= within.Value.Most)))
                {
                    gradeOk = true;
                    break;
                }
                Line(string.Format(CultureInfo.InvariantCulture, "{0}: placed {1}, it stands on {2:P2} the way it goes: tried again", name, fromLowEnd ? "at the low end" : "in the middle", uphill));
                s.Stop(secure: true);
                yield return ClearSpawned(cars);
                cars = null;
            }
            if (cars == null)
            {
                if (within != null)
                    Line($"{name}: no spot stood within the grade asked for");
                else
                    Fail(name, "couldn't place the train facing uphill");
                yield break;
            }
            name += string.Format(CultureInfo.InvariantCulture, " (under the train {0:P2})", uphill);
            var start = loco.transform.position;
            var ahead = loco.transform.forward;
            s.SetRoad(true, 40 / 3.6);
            double run = 0, least = 0;
            float elapsed = 0;
            var tookUp = false;
            double topAmps = 0;
            var fuseOff = false;
            var slipper = loco.SimController.wheelslipController;
            float slipping = 0;
            while (elapsed < 300 && run < meters)
            {
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
                run += Math.Max(0, loco.GetForwardSpeed()) * 0.5;
                least = Math.Min(least, Vector3.Dot(loco.transform.position - start, ahead));
                tookUp |= (s.Engineer.Status ?? "").StartsWith("Taking up power", StringComparison.Ordinal);
                if (slipper != null && slipper.IsWheelslipping)
                    slipping += 0.5f;
                topAmps = Math.Max(topAmps, s.Motors?.AmpsShare ?? 0);
                fuseOff |= s.Motors != null && s.Motors.FuseOff;
                if (cars.Any(c => c == null || c.derailed))
                    break;
                if (Mathf.RoundToInt(elapsed * 2) % 20 == 0)
                    Line(string.Format(CultureInfo.InvariantCulture, "  {0}: t={1:F0} run={2:F0} m along={3:F1} v={4:F1} km/h throttle={5:F2} current={6:P0} grip={8:P0} sand={9} | {7}", name, elapsed, run, Vector3.Dot(loco.transform.position - start, ahead), loco.GetForwardSpeed() * 3.6f, s.Controls.Throttle, s.Motors?.AmpsShare ?? 0, s.Engineer.Status,
                        slipper != null && slipper.TotalForceLimit > 1f && slipper.DrivingForce != null ? Math.Abs(slipper.DrivingForce.generatedForce) / slipper.TotalForceLimit : 0f, s.Controls.Sanding));
            }
            var detail = string.Format(CultureInfo.InvariantCulture, "{0:F0} m in {1:F0} s; rolled back at most {2:F1} m; took up power against the brakes {3}; slipping {4:F1} s; hottest {5:F0} C; most current {6:P0} of the generator's limit; fuse off {7}; status '{8}'",
                run, elapsed, -least, tookUp, slipping, s.Motors?.Temperature ?? 0, topAmps, fuseOff, s.Engineer.Status);
            // A Road move stops where the track ends or a switch is set against it: far enough, once well under way.
            var ranOut = run >= 100 && (s.Engineer.Status ?? "").StartsWith("Stopped", StringComparison.Ordinal);
            var gaveUp = wet > 0 && (s.Engineer.Status ?? "").StartsWith("Can't", StringComparison.Ordinal) && (s.LastNote ?? "").StartsWith("can't", StringComparison.Ordinal);
            if (gaveUp)
                detail += $"; gave up, saying '{s.LastNote}'";
            Check(name, (run >= meters || ranOut || gaveUp) && least > -1.5 && tookUp && !fuseOff && !cars.Any(c => c == null || c.derailed), detail);
            s.Stop(secure: true);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(cars);
        }

        // ---- A set-out at City South ----

        /// <summary>
        /// A player's report (2026-11-18): from CS A3L, a set-out onto C1O with
        /// cars at the far end of it went out onto the main line to turn
        /// (8.6 to 12.2 km offline) where coupling to those cars and shoving
        /// them in runs a few hundred meters. Both ways round, as a job.
        /// </summary>
        private static IEnumerator CsSetOutPart()
        {
            var world = Host.World;
            var yards = Dispatch.Yards(world.Network, Vector2.zero);
            NamedTrack Named(string id) => yards.SelectMany(y => y.Tracks).FirstOrDefault(t => t.Id == id);
            var lead = Named("CS-A3L");
            var into = Named("CS-C1O");
            var station = StationController.allStations?.Find(c => c != null && c.stationInfo != null && c.stationInfo.YardID == "CS");
            if (lead == null || into == null || station == null)
            {
                Line($"SKIP cs set-out: not the stock map (A3L {lead != null}, C1O {into != null}, station {station != null})");
                yield break;
            }
            // The two that turned on the main line offline: the free stretch at the entry the train
            // faces the wrong way for. Loco toward the dead end, cars at C1O's west (near) end;
            // loco toward C1O, cars at its east (far) end.
            yield return Caught("cs set-out", CsSetOut(world, station, lead, into, locoTowardC1O: false, carsAtFarEnd: false));
            yield return Caught("cs set-out", CsSetOut(world, station, lead, into, locoTowardC1O: true, carsAtFarEnd: true));
        }

        private static IEnumerator CsSetOut(World world, StationController station, NamedTrack lead, NamedTrack into, bool locoTowardC1O, bool carsAtFarEnd)
        {
            var name = $"cs set-out: the loco {(locoTowardC1O ? "toward C1O" : "toward the dead end")}, cars at C1O's {(carsAtFarEnd ? "far" : "near")} end";
            var c1 = world.Network.Tracks[into.Track];
            var a3 = world.Network.Tracks[lead.Track];
            var points = c1.GetKinkedPointSet().points;
            var c1West = (Vector3)points[0].position;
            PlayerManager.TeleportPlayer(c1West + Vector3.right * 10 + Vector3.up * 3, Quaternion.identity, null, useRotation: false);
            yield return new WaitForSecondsRealtime(10);
            // A clean start: whatever stands on the two tracks goes.
            world.ScanCars();
            var lt = new[] { c1.LogicTrack(), a3.LogicTrack() };
            var old = CarSpawner.Instance.AllCars.Where(c => c != null && !c.IsLoco && lt.Contains(c.logicCar?.FrontBogieTrack)).ToList();
            if (old.Count > 0)
                CarSpawner.Instance.DeleteTrainCars(old, true);
            yield return new WaitForSeconds(2);
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var span = c1.GetKinkedPointSet().span;
            var standingCars = CarSpawner.Instance.SpawnCarTypesOnTrackStrict(new List<TrainCarLivery> { flat, flat, flat }, c1, true, true, carsAtFarEnd ? span - 3 * 14 - 30 : 25);
            List<TrainCar> train = null;
            TrainCar loco = null;
            foreach (var reversed in new[] { false, true })
            {
                var list = new List<TrainCarLivery> { Livery("LocoDE2"), flat, flat };
                if (reversed)
                    list.Reverse();
                train = CarSpawner.Instance.SpawnCarTypesOnTrackStrict(list, a3, true, false, 60);
                if (train == null)
                    continue;
                yield return new WaitForSeconds(2);
                loco = train.First(c => c.IsLoco);
                var wagons = train.Where(c => !c.IsLoco).ToList();
                var locoNearer = Vector3.Distance(loco.transform.position, c1West) < wagons.Min(c => Vector3.Distance(c.transform.position, c1West));
                if (locoNearer == locoTowardC1O)
                    break;
                yield return ClearSpawned(train);
                train = null;
            }
            if (standingCars == null || train == null)
            {
                Fail(name, $"couldn't place the cars (standing {standingCars != null}, train {train != null})");
                if (standingCars != null)
                    yield return ClearSpawned(standingCars);
                yield break;
            }
            CoupleEveryCar(train);
            StartupHelper.Startup(loco);
            yield return new WaitForSeconds(10);
            world.ScanCars();
            var s = Host.SessionFor(loco, true);
            Job job = null;
            try
            {
                job = MakeHaul(world, station, train.Where(c => !c.IsLoco).ToList(), lead, into);
            }
            catch (Exception e)
            {
                Fail(name, $"couldn't make the job: {e.GetType().Name}: {e.Message}");
            }
            if (job == null)
            {
                yield return ClearSpawned(train.Concat(standingCars));
                yield break;
            }
            var run = JobRun.Start(s, world, new List<JobView> { Jobs.Read(world, job) }, queue: false, Arrival.BackFirst, out var problems);
            if (run == null)
            {
                Fail(name, "the run didn't start: " + string.Join("; ", problems));
                yield return ClearSpawned(train.Concat(standingCars));
                yield break;
            }
            double odometer = 0, longest = 0;
            float elapsed = 0;
            var reversals = 0;
            while (elapsed < 900 && s.Jobs != null)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                odometer += Math.Abs(loco.GetForwardSpeed());
                var route = s.Engineer.Route;
                if (route != null && route.Length > longest)
                {
                    longest = route.Length;
                    reversals = route.Reversals;
                }
                if ((int)elapsed % 20 == 0)
                    Line($"  {name}: t={elapsed:F0} run {odometer:F0} m | {s.Engineer.Status}");
            }
            var done = job.tasks.TrueForAll(t => t.state == TaskState.Done);
            var setOutOnC1O = train.Where(c => !c.IsLoco).All(c => c.logicCar?.FrontBogieTrack == c1.LogicTrack());
            Check(name, done && odometer < 2000, $"job {(done ? "done" : "NOT done")}, the cars on C1O {setOutOnC1O}; ran {odometer:F0} m in {elapsed:F0} s; longest route {longest:F0} m, {reversals} reversals; status '{s.Engineer.Status}'");
            s.Stop(secure: true);
            yield return new WaitForSeconds(3);
            yield return ClearSpawned(train.Concat(standingCars));
        }

        // ---- Firing ----

        /// <summary>A steam loco's works, as the test reads and forces them.</summary>
        private sealed class SteamWorks
        {
            private static readonly FieldInfo VesselField = AccessTools.Field(typeof(Boiler), "vessel");
            private static readonly FieldInfo VolumeField = AccessTools.Field(typeof(Boiler), "totalVolume");
            public readonly Boiler Boiler;
            public readonly Firebox Firebox;
            public readonly Port Injector, Blower, Damper;

            public SteamWorks(TrainCar loco)
            {
                var flow = loco.SimController.simFlow;
                Boiler = flow.OrderedSimComps.OfType<Boiler>().First();
                Firebox = flow.OrderedSimComps.OfType<Firebox>().FirstOrDefault();
                foreach (var c in flow.OrderedSimComps.OfType<ExternalControl>())
                {
                    var id = c.id.ToLowerInvariant();
                    if (id.Contains("injector") && Injector == null)
                        Injector = c.controlExtIn;
                    else if (id.Contains("blower") && Blower == null)
                        Blower = c.controlExtIn;
                    else if (id.Contains("damper") && Damper == null)
                        Damper = c.controlExtIn;
                }
            }

            /// <summary>The boiler made to read so; the level is always given, as a pressure forced alone would move it.</summary>
            public void Force(float pressure, float level)
            {
                var volume = (float)VolumeField.GetValue(Boiler);
                VesselField.SetValue(Boiler, WaterPressureVessel.FromSaveData(volume, pressure, level));
            }

            public void SetHands(float injector, float blower, float damper)
            {
                Injector?.ExternalValueUpdate(injector);
                Blower?.ExternalValueUpdate(blower);
                Damper?.ExternalValueUpdate(damper);
            }

            public string Hands => string.Format(CultureInfo.InvariantCulture, "injector {0:F2}, blower {1:F2}, damper {2:F2}", Injector?.Value ?? -1, Blower?.Value ?? -1, Damper?.Value ?? -1);
        }

        private static IEnumerator FiringPart()
        {
            var world = Host.World;
            world.ScanCars();
            var api = Type.GetType("RealisticFiring.FiremanApi, RealisticFiring", false);
            var modeOf = api?.GetMethod("ModeOf", new[] { typeof(TrainCar) });
            var setMode = api?.GetMethod("SetMode", new[] { typeof(TrainCar), typeof(string) });
            Line($"firing: {(api != null ? "with Realistic Firing" : "stock steam")}");
            var track = LevelTracks(world).FirstOrDefault(c => c.Length >= 1800);
            if (track == null)
            {
                Fail("firing", "no long level track clear");
                yield break;
            }
            yield return BuildTrain(world, track, new[] { "LocoS282A", "LocoS282B" }, 0, false, true);
            var cars = builtCars;
            if (cars == null)
            {
                Fail("firing", "couldn't place the S282");
                yield break;
            }
            foreach (var c in cars)
                c.brakeSystem?.SetHandbrakePosition(0f);
            var loco = cars.First(c => c.IsLoco);
            var works = new SteamWorks(loco);
            string Mode() => modeOf?.Invoke(null, new object[] { loco }) as string ?? "-";
            setMode?.Invoke(null, new object[] { loco, "Off" });
            var s = Host.SessionFor(loco, true);
            var crew = s.Steam;
            var said = new List<string>();
            void Listen()
            {
                if (s.LastNote != null && (said.Count == 0 || said[said.Count - 1] != s.LastNote))
                    said.Add(s.LastNote);
            }
            Check("firing: a new engineer leaves the firing to the crew by default", !crew.PlayerFires, "");
            crew.PlayerFires = true;
            var goodLevel = works.Boiler.waterLevelReadOut.Value;
            works.Force(13f, goodLevel);
            works.SetHands(0f, 0.3f, 0.4f);
            var hands = works.Hands;
            var coal = works.Firebox != null ? works.Firebox.coalLevelNormalizedReadOut.Value : 0f;
            var lit = works.Firebox != null && works.Firebox.fireOnReadOut.Value > 0.5f;
            s.SetRoad(true, 15 / 3.6);
            for (var i = 0; i < 25; i++)
            {
                yield return new WaitForSeconds(1);
                Listen();
            }
            var coalNow = works.Firebox != null ? works.Firebox.coalLevelNormalizedReadOut.Value : 0f;
            Check("firing: the engineer leaves the fire and the water alone", works.Hands == hands && Mode() is var mode && (api == null || mode == "Off") && coalNow <= coal + 0.005f,
                $"{works.Hands} (was {hands}); fireman {Mode()}; coal {coal:F3} then {coalNow:F3}; ran at {loco.GetForwardSpeed() * 3.6f:F0} km/h; {crew.Describe()}");
            Check("firing: the player is told at takeover", said.Any(n => n.Contains("you're firing")), string.Join(" | ", said));

            // No steam, in each kind of order: it stops and waits, and goes on by itself.
            var points = new[] { track.Length * 0.1, track.Length * 0.9 };
            foreach (var kind in new[] { "road", "yard", "waypoint" })
            {
                works.Force(13f, goodLevel);
                if (kind == "road")
                    s.SetRoad(true, 15 / 3.6);
                else if (kind == "yard")
                    s.SetYard(true, 400);
                else
                {
                    var here = s.Read().EndA.Offset;
                    s.Engineer.GoTo(new Waypoint(new TrackPosition(track.Track, Math.Abs(points[0] - here) > Math.Abs(points[1] - here) ? points[0] : points[1])) { Label = "firing test" });
                }
                for (var i = 0; i < 40 && Mathf.Abs(loco.GetForwardSpeed()) < 1.5f; i++)
                {
                    works.Force(13f, goodLevel);
                    yield return new WaitForSeconds(1);
                    Listen();
                }
                var moving = Mathf.Abs(loco.GetForwardSpeed());
                float stoppedIn = 0;
                while (stoppedIn < 70 && Mathf.Abs(loco.GetForwardSpeed()) > 0.05f)
                {
                    works.Force(0.5f, goodLevel);
                    yield return new WaitForSeconds(1);
                    stoppedIn += 1;
                    Listen();
                }
                for (var i = 0; i < 5; i++)
                {
                    works.Force(0.5f, goodLevel);
                    yield return new WaitForSeconds(1);
                    Listen();
                }
                var status = s.Engineer.Status ?? "";
                var regulator = s.Controls.Throttle;
                Check($"firing: no steam on a {kind} move: it stops and waits", status.StartsWith("Waiting for steam: you're firing", StringComparison.Ordinal) && regulator < 0.01f && Mathf.Abs(loco.GetForwardSpeed()) < 0.05f,
                    $"from {moving * 3.6f:F0} km/h in {stoppedIn:F0} s; regulator {regulator:F2}; status '{status}'");
                works.Force(4f, goodLevel);
                for (var i = 0; i < 4; i++)
                {
                    works.Force(4f, goodLevel);
                    yield return new WaitForSeconds(1);
                }
                var stillWaiting = (s.Engineer.Status ?? "").StartsWith("Waiting for steam", StringComparison.Ordinal);
                var start = loco.transform.position;
                float went = 0;
                while (went < 45 && Vector3.Distance(loco.transform.position, start) < 4)
                {
                    works.Force(13f, goodLevel);
                    yield return new WaitForSeconds(1);
                    went += 1;
                }
                Check($"firing: on a {kind} move it goes on by itself with steam back", stillWaiting && Vector3.Distance(loco.transform.position, start) >= 4,
                    $"still waiting at 4 bar: {stillWaiting}; {Vector3.Distance(loco.transform.position, start):F1} m in {went:F0} s at 13 bar; status '{s.Engineer.Status}'");
                s.SetRoad(true, 0);
                for (var i = 0; i < 25 && Mathf.Abs(loco.GetForwardSpeed()) > 0.05f; i++)
                {
                    works.Force(13f, goodLevel);
                    yield return new WaitForSeconds(1);
                }
            }
            Check("firing: low steam is warned of", said.Any(n => n.StartsWith("steam is down", StringComparison.Ordinal)), string.Join(" | ", said));

            // Water out of sight, the train running.
            // Just in the bottom of the glass, over the crown sheet: under it the boiler would blow up.
            var lowLevel = Enumerable.Range(0, 200).Select(i => i / 200f).FirstOrDefault(l => crew.GlassAt(l) >= 0.05f);
            works.Force(13f, goodLevel);
            s.SetRoad(true, 15 / 3.6);
            for (var i = 0; i < 40 && Mathf.Abs(loco.GetForwardSpeed()) < 1.5f; i++)
                yield return new WaitForSeconds(1);
            float dry = 0;
            while (dry < 45 && !((s.Engineer.Status ?? "").StartsWith("Water out of sight", StringComparison.Ordinal) && Mathf.Abs(loco.GetForwardSpeed()) < 0.05f))
            {
                works.Force(13f, lowLevel);
                yield return new WaitForSeconds(1);
                dry += 1;
                Listen();
            }
            var dryStatus = s.Engineer.Status;
            Check("firing: water out of sight stops the train", (dryStatus ?? "").StartsWith("Water out of sight", StringComparison.Ordinal) && said.Any(n => n.StartsWith("the water is out of sight", StringComparison.Ordinal)),
                $"after {dry:F0} s at level {lowLevel:F2} (reads {crew.GlassReadForTest:F2} of the glass); status '{dryStatus}'; {loco.GetForwardSpeed() * 3.6f:F1} km/h");
            var dryAt = loco.transform.position;
            float wet = 0;
            while (wet < 45 && Vector3.Distance(loco.transform.position, dryAt) < 4)
            {
                works.Force(13f, goodLevel);
                yield return new WaitForSeconds(1);
                wet += 1;
            }
            Check("firing: with water back it goes on by itself", Vector3.Distance(loco.transform.position, dryAt) >= 4, $"{Vector3.Distance(loco.transform.position, dryAt):F1} m in {wet:F0} s; status '{s.Engineer.Status}'");

            // The fire out.
            if (works.Firebox != null && lit)
            {
                works.Firebox.extinguishExtIn.ExternalValueUpdate(1f);
                for (var i = 0; i < 8; i++)
                {
                    works.Force(13f, goodLevel);
                    yield return new WaitForSeconds(1);
                    Listen();
                }
                works.Firebox.extinguishExtIn.ExternalValueUpdate(0f);
                for (var i = 0; i < 6; i++)
                {
                    works.Force(13f, goodLevel);
                    yield return new WaitForSeconds(1);
                    Listen();
                }
                Check("firing: a fire gone out is warned of and left out", works.Firebox.fireOnReadOut.Value < 0.5f && said.Any(n => n.StartsWith("the fire is out", StringComparison.Ordinal)), $"fire on {works.Firebox.fireOnReadOut.Value:F0}; {string.Join(" | ", said)}");
                works.Firebox.ignitionExtIn.ExternalValueUpdate(1f);
                yield return new WaitForSeconds(2);
                works.Firebox.ignitionExtIn.ExternalValueUpdate(0f);
            }
            else
            {
                Line($"firing: the fire as Realistic Firing reads it: lit {crew.FireLitForTest?.ToString() ?? "unknown"}, glass {crew.GlassReadForTest:F2}");
                Check("firing: Realistic Firing's readings reach the engineer", api == null || crew.FireLitForTest == true, $"lit {crew.FireLitForTest?.ToString() ?? "unknown"}");
            }

            // Handing back leaves everything as the player has it.
            works.Force(13f, goodLevel);
            works.SetHands(0.5f, 0.2f, 0.6f);
            hands = works.Hands;
            s.Stop(secure: true);
            yield return new WaitForSeconds(4);
            Check("firing: hand-back leaves the fire and the water alone", works.Hands == hands && (api == null || Mode() == "Off"), $"{works.Hands} (was {hands}); fireman {Mode()}");

            // Crew, then back to the player, with the engineer driving.
            crew.PlayerFires = false;
            s.SetRoad(true, 10 / 3.6);
            for (var i = 0; i < 8; i++)
                yield return new WaitForSeconds(1);
            var crewMode = Mode();
            var damperByCrew = works.Damper?.Value ?? -1;
            crew.PlayerFires = true;
            yield return new WaitForSeconds(3);
            Check("firing: the button works with the engineer driving", api != null ? crewMode != "Off" && Mode() == "Off" : damperByCrew > 0.99f, $"crew: fireman {crewMode}, damper {damperByCrew:F2}; you: fireman {Mode()}");
            s.Stop(secure: true);
            yield return new WaitForSeconds(4);
            yield return ClearSpawned(cars);

            // The setting.
            var before = Main.Settings.PlayerFiresNewEngineers;
            Main.Settings.PlayerFiresNewEngineers = true;
            var other = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { Livery("LocoS060") }, new List<bool> { false }, world.Network.Tracks[track.Track], true, true)?.FirstOrDefault();
            yield return new WaitForSeconds(3);
            var fresh = other != null ? Host.SessionFor(other, true) : null;
            Check("firing: the setting starts new engineers on You", fresh?.Steam != null && fresh.Steam.PlayerFires, fresh == null ? "no session" : "");
            Main.Settings.PlayerFiresNewEngineers = before;
            if (other != null)
                yield return ClearSpawned(new[] { other });
        }

        private static void Check(string name, bool ok, string detail) => (ok ? (Action<string, string>)Pass : Fail)(name, detail);

        /// <summary>The Fire button pressed through the app entries the Comms Tablet calls.</summary>
        private static IEnumerator FireButtonPart()
        {
            var loco = SpawnLoco("LocoS060");
            var diesel = SpawnLoco("LocoDE2");
            yield return new WaitForSeconds(3);
            if (loco == null || diesel == null)
            {
                Fail("fire button", "no locos");
                yield break;
            }
            yield return OffTheLocos(diesel);
            var s = Host.SessionFor(loco, true);
            var d = Host.SessionFor(diesel, true);
            StartupHelper.Startup(loco);
            yield return new WaitForSeconds(5);
            s.StopHere();
            d.StopHere();
            TabletUi.Chosen = loco;
            TabletUi.Page = TabletTab.Engineers;
            var holder = new GameObject("AIEngineer self-test view", typeof(RectTransform), typeof(Canvas));
            var region = (RectTransform)holder.transform;
            region.sizeDelta = new Vector2(TabletSkin.W, TabletSkin.H);
            var placed = new Rect[64];
            var redrawn = 0;
            var view = CommsTabletApp.Open(region, holder, () => redrawn++, (i, r, active) => placed[i] = active ? r : default, _ => { }, placed.Length, false);
            CommsTabletApp.Shown(view, true);
            yield return new WaitForSeconds(1.5f);
            CommsTabletApp.Draw(view);
            yield return null;
            CommsTabletApp.Draw(view);
            var drawn = TabletPages.FireButtons.TryGetValue(loco, out var button);
            var dieselHasNone = !TabletPages.FireButtons.ContainsKey(diesel);
            var slot = drawn ? Array.FindIndex(placed, r => r.Contains(button.center)) : -1;
            Check("fire button: on a steam loco's card, none on a diesel's", drawn && slot >= 0 && dieselHasNone, $"merged {CommsTabletLink.Merged}; rect {button}; slot {slot}; diesel listed {TabletUi.Engineers.Contains(diesel)}");
            var pressed = false;
            var back = true;
            if (slot >= 0)
            {
                CommsTabletApp.Pressed(view, slot);
                CommsTabletApp.Draw(view);
                pressed = s.Steam.PlayerFires;
                CommsTabletApp.Draw(view);
                CommsTabletApp.Pressed(view, Array.FindIndex(placed, r => r.Contains(button.center)));
                CommsTabletApp.Draw(view);
                back = s.Steam.PlayerFires;
            }
            Check("fire button: a press gives the fire to the player, another to the crew", pressed && !back, $"after one press {pressed}, after two {back}; faults {CommsTabletApp.Faults}");
            var installed = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "CommsTablet");
            Check("fire button: merged when the Comms Tablet is here", !installed || CommsTabletLink.Merged, $"installed {installed}, merged {CommsTabletLink.Merged}");
            CommsTabletApp.Close(view);
            UnityEngine.Object.Destroy(holder);
            s.Stop(secure: true);
            d.Stop(secure: true);
            yield return new WaitForSeconds(2);
            yield return ClearSpawned(new[] { loco, diesel });
        }
    }
}
