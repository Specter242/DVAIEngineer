using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    internal static partial class SelfTest
    {
        // "seed=1234" in the trigger file: the industry's random layout again.
        private static int? industrySeed;

        // "station=GF" in the trigger file: that industry, wherever the loco is.
        private static string industryStation;

        /// <summary>The car types the industry is filled with, besides the jobs' own.</summary>
        private static readonly TrainCarType[] FillerTypes =
        {
            TrainCarType.FlatbedEmpty, TrainCarType.BoxcarBrown, TrainCarType.BoxcarGreen, TrainCarType.HopperBrown,
            TrainCarType.TankOrange, TrainCarType.TankBlack, TrainCarType.GondolaRed, TrainCarType.GondolaGreen,
            TrainCarType.StockBrown, TrainCarType.RefrigeratorWhite,
        };

        /// <summary>One track of the industry as the scenario fills it.</summary>
        private sealed class Siding
        {
            public NamedTrack Track;
            public double Used;
            public double Reserved;
            public double Room => Track.Length - 40 - Used - Reserved;
        }

        private static IEnumerator Industry(Session s)
        {
            const string worked = "industry: every job done, by the game's own check";
            const string firstOff = "industry: the cars the loco had uncoupled first, where they stood";
            const string putBack = "industry: every other car back on its own track (or set aside nearer, said why), off the train";
            const string derails = "industry: nothing derailed";
            var world = Host.World;
            world.ScanCars();
            var seed = industrySeed ?? Environment.TickCount & 0xFFFF;
            var rng = new System.Random(seed);
            var state = s.Read();
            var at = Dispatch.MapPosition(s.Loco);

            // The industry: the nearest station with seven free tracks in reach, one with a warehouse machine first.
            StationController station = null;
            List<NamedTrack> free = null;
            WarehouseMachineController machine = null;
            var candidates = Dispatch.Yards(world.Network, at).Where(y => !y.Added).ToList();
            candidates = industryStation != null ? candidates.Where(y => y.Id == industryStation).ToList() : candidates.Take(6).ToList();
            foreach (var yard in candidates)
            {
                var st = StationController.allStations?.Find(c => c != null && c.stationInfo != null && c.stationInfo.YardID == yard.Id);
                if (st == null)
                    continue;
                // Shunting, not hauling: the tracks a 120 m train standing on the warehouse machine's
                // track (or the yard's middle track) reaches within 2 km by the router, reversals
                // counted, with the yard as it is before the fill: a station's far sub-yard is left out.
                var hubMachine = WarehouseMachineController.allControllers.Find(c => c != null && c.warehouseTrack != null &&
                    world.Network.TryGetTrack(c.warehouseTrack, out var ht) && yard.Tracks.Exists(t => t.Track == ht));
                var hub = -1;
                if (hubMachine == null || !world.Network.TryGetTrack(hubMachine.warehouseTrack, out hub))
                    hub = yard.Tracks.OrderBy(t => (t.Middle - yard.Centre).sqrMagnitude).First().Track;
                var tracks = new List<NamedTrack>();
                foreach (var t in yard.Tracks)
                {
                    // Not the loco's own track either: cars spawned there couple to it (after the jobs
                    // part it stood on OWN-A2D, and part of a job's cut came away with it).
                    if (t.Length < 70 || world.Occupancy.IsOccupied(t.Track, int.MinValue))
                        continue;
                    if (t.Track != hub && ShuntingCost(world, hub, t.Track) > 2000)
                        continue;
                    var route = RouteTo(state, OrderGoals.OnTrack(world.Graph, t.Track, state.Length + 40));
                    if (route == null || route.Length > (industryStation != null ? 40000 : 6000))
                        continue;
                    tracks.Add(t);
                }
                if (tracks.Count < 7)
                    continue;
                var wm = WarehouseMachineController.allControllers.Find(c => c != null && c.warehouseMachine != null && c.warehouseTrack != null &&
                    world.Network.TryGetTrack(c.warehouseTrack, out var wt) && tracks.Exists(t => t.Track == wt && t.Length >= 100));
                if (station == null || (machine == null && wm != null))
                {
                    station = st;
                    free = tracks;
                    machine = wm;
                }
                if (machine != null)
                    break;
            }
            if (station == null)
            {
                Line("SKIP industry: no station within six yards with seven free tracks of 70 m in reach");
                yield break;
            }

            // The jobs' cars: for the shunting load, of a type its cargo loads into.
            CargoType cargo = CargoType.None;
            TrainCarLivery loadable = null;
            if (machine != null)
            {
                foreach (var c in machine.warehouseMachine.SupportedCargoTypes)
                {
                    var v2 = c.ToV2();
                    var info = v2?.loadableCarTypes?.FirstOrDefault(l => l.carType?.liveries != null && l.carType.liveries.Count > 0);
                    if (info == null)
                        continue;
                    cargo = c;
                    loadable = info.carType.liveries[0];
                    break;
                }
                if (loadable == null)
                {
                    Line($"industry: the warehouse machine on {machine.warehouseTrack.LogicTrack()?.ID?.FullDisplayID} loads nothing a car type here carries; no shunting load");
                    machine = null;
                }
            }
            var warehouseTrack = -1;
            if (machine != null)
                world.Network.TryGetTrack(machine.warehouseTrack, out warehouseTrack);
            var total = rng.Next(10, 31);
            Line($"industry: seed {seed}; at {station.stationInfo.YardID}, {total} cuts on {free.Count} free tracks ({string.Join(", ", free.Select(t => $"{t.Part} {t.Length:F0} m{(t.DeadEnd ? " stub" : "")}"))})" +
                 (machine != null ? $"; warehouse machine on {Name(world, warehouseTrack)} loads {cargo} into {loadable.id}" : "; no warehouse machine: no shunting load"));

            // Roles: the loading track, the destinations, the sources, the start cut's track; the rest fill.
            var pool = free.Where(t => t.Track != warehouseTrack).OrderBy(_ => rng.Next()).Select(t => new Siding { Track = t }).ToList();
            Siding Take(double atLeast)
            {
                var pick = pool.Where(p => p.Track.Length >= atLeast).OrderBy(p => p.Track.Length).FirstOrDefault() ?? pool.OrderByDescending(p => p.Track.Length).First();
                pool.Remove(pick);
                return pick;
            }
            var sidings = new List<Siding>();
            var loading = machine != null ? new Siding { Track = free.Find(t => t.Track == warehouseTrack) } : null;
            var outbound = machine != null ? Take(140) : null;
            var dest = Take(140);
            var src1 = Take(120);
            var src2 = Take(100);
            var start = Take(80);
            Siding srcL1 = null, srcL2 = null;
            if (machine != null)
            {
                srcL1 = pool.Count > 0 ? Take(120) : src1;
                srcL2 = pool.Count > 0 ? Take(100) : src2;
            }
            foreach (var x in new[] { loading, outbound, dest, src1, src2, start, srcL1, srcL2 })
            {
                if (x != null && !sidings.Contains(x))
                    sidings.Add(x);
            }
            sidings.AddRange(pool);

            var spawned = new List<TrainCar>();
            var home = new Dictionary<TrainCar, Track>();
            var cutsMade = 0;
            List<TrainCar> Spawn(Siding on, IList<TrainCarLivery> liveries, string what)
            {
                var length = liveries.Count * CarLength + 2;
                if (on.Room < length && what == null)
                    return null;
                var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries.ToList(), liveries.Select(_ => rng.Next(2) == 0).ToList(),
                    world.Network.Tracks[on.Track.Track], true, true);
                if (cars == null || cars.Count < liveries.Count)
                {
                    if (what != null)
                        Line($"industry: couldn't spawn {what} on {on.Track.Id}");
                    return null;
                }
                on.Used += length;
                spawned.AddRange(cars);
                cutsMade++;
                return cars;
            }
            List<TrainCarLivery> Fillers(int n) => Enumerable.Range(0, n).Select(_ => FillerTypes[rng.Next(FillerTypes.Length)].ToV2()).ToList();
            var flat = TrainCarType.FlatbedEmpty.ToV2();

            // Room kept on the destinations for the jobs' cars.
            var mCount = 2 + rng.Next(3);
            var mA = 1 + rng.Next(mCount - 1);
            dest.Reserved = mCount * CarLength + 30;
            var lCount = machine != null ? 2 + rng.Next(3) : 0;
            var lA = lCount > 0 ? 1 + rng.Next(lCount - 1) : 0;
            if (loading != null)
            {
                loading.Reserved = lCount * CarLength + 30;
                outbound.Reserved = lCount * CarLength + 30;
            }

            // One cut at a time, a moment apart: the spawner sees only the cars already on the
            // track, and spawned in one frame they land on top of each other (the first run).
            const float settle = 0.6f;
            // M's first lot buried: alone in the middle of its track, cuts spawned either side after.
            var mFirst = Spawn(src1, Enumerable.Repeat(flat, mA).ToList(), "M's first lot");
            yield return new WaitForSeconds(settle);
            // M's second lot in the middle of a cut: a car either side of it.
            var mSecondCut = Spawn(src2, new[] { FillerTypes[rng.Next(FillerTypes.Length)].ToV2() }.Concat(Enumerable.Repeat(flat, mCount - mA)).Concat(new[] { FillerTypes[rng.Next(FillerTypes.Length)].ToV2() }).ToList(), "M's second lot");
            yield return new WaitForSeconds(settle);
            List<TrainCar> lFirst = null, lSecondCut = null;
            if (machine != null)
            {
                lFirst = Spawn(srcL1, Enumerable.Repeat(loadable, lA).ToList(), "L's first lot");
                yield return new WaitForSeconds(settle);
                lSecondCut = Spawn(srcL2, Enumerable.Repeat(loadable, lCount - lA).Concat(Fillers(1)).ToList(), "L's second lot");
                yield return new WaitForSeconds(settle);
            }
            var startCut = Spawn(start, Fillers(2), "the loco's start cut");
            yield return new WaitForSeconds(settle);
            if (mFirst == null || mSecondCut == null || startCut == null || (machine != null && (lFirst == null || lSecondCut == null)))
            {
                Fail(worked, "couldn't spawn the jobs' cars");
                CarSpawner.Instance.DeleteTrainCars(spawned, true);
                yield break;
            }
            // Bury the first lots: a cut each side.
            foreach (var on in new[] { src1, srcL1 }.Where(x => x != null).Distinct().ToList())
            {
                for (var k = 0; k < 2; k++)
                {
                    Spawn(on, Fillers(1 + rng.Next(3)), null);
                    yield return new WaitForSeconds(settle);
                }
            }
            // And the rest at random, up to the number drawn.
            for (var tries = 0; cutsMade < total && tries < 200; tries++)
            {
                var on = sidings[rng.Next(sidings.Count)];
                if (on == start)
                    continue;
                if (Spawn(on, Fillers(1 + rng.Next(4)), null) != null)
                    yield return new WaitForSeconds(settle);
            }
            yield return new WaitForSeconds(5);
            world.ScanCars();
            foreach (var car in spawned)
                home[car] = car.logicCar?.FrontBogieTrack;

            var mCars = mFirst.Concat(mSecondCut.Skip(1).Take(mCount - mA)).ToList();
            var lCars = machine != null ? lFirst.Concat(lSecondCut.Take(lCount - lA)).ToList() : new List<TrainCar>();
            var jobCars = new HashSet<TrainCar>(mCars.Concat(lCars));
            var strangers = spawned.Where(c => !jobCars.Contains(c)).ToList();
            Line($"industry: {cutsMade} cuts, {spawned.Count} cars spawned");
            var standing = new List<OccupiedSpan>();
            foreach (var sd in sidings)
            {
                world.Occupancy.Standing(sd.Track.Track, int.MinValue, standing);
                var spans = standing.GroupBy(x => x.Owner).Select(g => (From: g.Min(x => x.From), To: g.Max(x => x.To), Owner: g.Key)).OrderBy(x => x.From).ToList();
                var parts = spans.Select(x =>
                {
                    var set = Trainset.allSets.Find(t => t != null && t.id == x.Owner);
                    var tag = set == null ? "?" : string.Concat(set.cars.Select(c => mCars.Contains(c) ? "M" : lCars.Contains(c) ? "L" : startCut.Contains(c) ? "Z" : "."));
                    return $"{x.From:F0}-{x.To:F0} [{tag}]";
                });
                Line($"  {sd.Track.Id} ({sd.Track.Length:F0} m{(sd.Track.DeadEnd ? ", stub" : "")}): {string.Join("  ", parts)}");
            }

            // The jobs.
            Job jobM, jobL = null;
            try
            {
                jobM = MakeHaul(world, station, mCars, src1.Track, dest.Track);
                if (machine != null)
                    jobL = MakeShuntingLoad(world, station, machine, new[] { (srcL1.Track, lFirst), (srcL2.Track, lSecondCut.Take(lCount - lA).ToList()) }, cargo, outbound.Track);
            }
            catch (Exception e)
            {
                Fail(worked, $"couldn't make the jobs: {e.GetType().Name}: {e.Message}");
                CarSpawner.Instance.DeleteTrainCars(spawned, true);
                yield break;
            }
            Line($"industry: M {jobM.ID}: {mA} car(s) buried on {src1.Track.Id}, {mCount - mA} in the middle of a cut on {src2.Track.Id}, all to {dest.Track.Id}");
            if (jobL != null)
                Line($"industry: L {jobL.ID}: {lA} car(s) buried on {srcL1.Track.Id}, {lCount - lA} on {srcL2.Track.Id}, to the warehouse on {loading.Track.Id}, then {outbound.Track.Id}");

            // The loco starts coupled to two cars of no job.
            var zCut = Dispatch.Cuts(world, Dispatch.MapPosition(s.Loco), 8000, s.Loco.trainset).Find(c => c.Set.cars.Contains(startCut[0]));
            if (zCut == null)
            {
                var z = startCut[0];
                Fail(firstOff, $"the start cut on {start.Track.Id} isn't listed: {z.ID} {(z.derailed ? "derailed" : "on the rails")}, in a set of {z.trainset?.cars.Count} " +
                               $"({(z.trainset != null && world.TryPlace(z.trainset.GetEndmost(true), out _, out _) ? "placed" : "not placed")}), {(Dispatch.MapPosition(z) - Dispatch.MapPosition(s.Loco)).magnitude:F0} m from the loco");
                CarSpawner.Instance.DeleteTrainCars(spawned, true);
                yield break;
            }
            Dispatch.PickUp(s, world, zCut, queue: false, CutSide.Nearer, Arrival.Any);
            yield return UntilQueueEmpty(s, "industry start", 1200);
            if (!startCut.TrueForAll(c => c.trainset == s.Loco.trainset))
            {
                Fail(firstOff, $"the loco didn't couple to the start cut: status '{s.Engineer.Status}'");
                CarSpawner.Instance.DeleteTrainCars(spawned, true);
                yield break;
            }
            var views = new List<JobView> { Jobs.Read(world, jobM) };
            if (jobL != null)
                views.Add(Jobs.Read(world, jobL));
            foreach (var v in views)
                Line($"industry: {v.Id} reads as {v.Kind}: {v.Blocked ?? v.Summary}");
            // Either way round: an industry's shunting has no use for turning the loco on a wye first.
            var run = JobRun.Start(s, world, views, queue: false, Arrival.Any, out var problems);
            Line($"industry: handed {string.Join(", ", views.Select(v => v.Id))}{(problems.Count > 0 ? "; left out: " + string.Join("; ", problems) : "")}");
            if (run == null)
            {
                Fail(worked, "the run didn't start");
                CarSpawner.Instance.DeleteTrainCars(spawned, true);
                yield break;
            }

            // Worked through: the test is the player at the warehouse machine.
            var elapsed = 0f;
            var mixed = false;
            var loaded = 0;
            var startSet = new HashSet<TrainCar>(startCut);
            while (elapsed < 5400 && s.Jobs != null)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                var train = s.Loco.trainset?.cars;
                if (train != null && train.Exists(c => startSet.Contains(c)) && train.Exists(c => c != s.Loco && !startSet.Contains(c)))
                    mixed = true;
                if (jobL != null)
                    loaded += LoadAtMachine(jobL, machine);
                if ((int)elapsed % 10 == 0)
                    Trace(s, "industry", elapsed);
                if (elapsed == 60)
                {
                    var frame = 0f;
                    yield return MeasureFrames(5, v => frame = v);
                    elapsed += 5;
                    Line($"frame time while shunting {frame * 1000:F2} ms (idle was {idleFrameTime * 1000:F2} ms)");
                }
            }
            Line($"industry: the run {(s.Jobs == null ? "finished" : "still going: " + s.Jobs.Status)} after {elapsed:F0} s; the test loaded {loaded} car(s) at the machine");

            var jobsDone = new[] { jobM, jobL }.Where(j => j != null).ToList();
            var detail = string.Join("; ", jobsDone.Select(j => $"{j.ID} {(j.tasks.TrueForAll(t => t.state == TaskState.Done) ? "done" : "NOT done: " + Jobs.Read(world, j).Blocked + " " + Jobs.Read(world, j).Summary)}")) + $"; status '{s.Engineer.Status}'";
            if (jobsDone.TrueForAll(j => j.tasks.TrueForAll(t => t.state == TaskState.Done)))
                Pass(worked, detail);
            else
                Fail(worked, detail);

            var startTrack = home[startCut[0]];
            detail = $"{(mixed ? "the start cars were still on the train when it coupled to others; " : "")}" +
                     string.Join(", ", startCut.Select(c => $"{c.ID} {(c.trainset == s.Loco.trainset ? "on the train" : "off")} on {Dispatch.Where(c)}"));
            if (!mixed && startCut.TrueForAll(c => c.trainset != s.Loco.trainset && c.logicCar?.FrontBogieTrack == startTrack))
                Pass(firstOff, detail);
            else
                Fail(firstOff, detail);

            // Riders the run set aside elsewhere, saying why (going back was a long way round), count
            // as handled when they stand off the train on a yard track.
            var aside = strangers.Where(c => c != null && run.SetAsideInstead.Contains(c) && c.trainset != s.Loco.trainset &&
                                             c.logicCar?.FrontBogieTrack?.ID != null && !c.logicCar.FrontBogieTrack.ID.IsGeneric()).ToList();
            var moved = strangers.Where(c => c != null && !aside.Contains(c) && (c.trainset == s.Loco.trainset || c.logicCar?.FrontBogieTrack != home[c])).ToList();
            detail = $"{strangers.Count - moved.Count - aside.Count} of {strangers.Count} where they started" +
                     (aside.Count > 0 ? $"; {aside.Count} set aside nearer, going back a long way round: " + string.Join(", ", aside.Take(8).Select(c => $"{c.ID} on {Dispatch.Where(c)} (was {home[c]?.ID?.FullDisplayID})")) : "") +
                     (moved.Count > 0 ? "; elsewhere: " + string.Join(", ", moved.Take(12).Select(c => $"{c.ID} {(c.trainset == s.Loco.trainset ? "on the train" : "on " + Dispatch.Where(c))} (was {home[c]?.ID?.FullDisplayID})")) : "");
            if (moved.Count == 0)
                Pass(putBack, detail);
            else
                Fail(putBack, detail);

            var off = spawned.Where(c => c != null && c.derailed).ToList();
            detail = off.Count == 0 ? $"{spawned.Count} cars on the rails; slowest tick {Session.SlowestTickMs:F2} ms" : string.Join(", ", off.Select(c => c.ID));
            if (off.Count == 0 && !s.Loco.derailed)
                Pass(derails, detail);
            else
                Fail(derails, detail);

            // Clear the industry for the parts after.
            if (s.Jobs != null)
            {
                s.Jobs.Cancel(s, "the self-test ended it");
                s.Jobs = null;
            }
            s.Engineer.ClearQueue();
            CarSpawner.Instance.DeleteTrainCars(spawned.Where(c => c != null).ToList(), true);
            yield return new WaitForSeconds(3);
        }

        /// <summary>
        /// The player at the warehouse machine: once the job's step is its
        /// loading and every car stands on the loading track, stopped, loads
        /// them one a second, as the machine does. Returns the cars loaded.
        /// </summary>
        private static int LoadAtMachine(Job job, WarehouseMachineController machine)
        {
            var leaves = new List<Task>();
            foreach (var task in job.tasks)
                CurrentTasks(task, leaves);
            var n = 0;
            foreach (var task in leaves.OfType<WarehouseTask>())
            {
                if (task.state == TaskState.Done)
                    continue;
                var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
                var ready = task.cars.TrueForAll(c => c.CurrentTrack == machine.warehouseMachine.WarehouseTrack &&
                    registry.logicCarToTrainCar.TryGetValue(c, out var tc) && tc != null && tc.GetAbsSpeed() < 0.1f);
                if (!ready)
                    continue;
                var car = task.warehouseTaskType == WarehouseTaskType.Loading ? machine.warehouseMachine.LoadOneCarOfTask(task) : machine.warehouseMachine.UnloadOneCarOfTask(task);
                if (car != null)
                {
                    n++;
                    Line($"industry: the test, as the player, {(task.warehouseTaskType == WarehouseTaskType.Loading ? "loads" : "unloads")} {car.ID} at the machine");
                }
            }
            return n;
        }

        private static void CurrentTasks(Task task, List<Task> into)
        {
            if (task == null || task.state == TaskState.Done)
                return;
            switch (task.InstanceTaskType)
            {
                case TaskType.Sequential:
                    var next = task.GetTaskData().nestedTasks.FirstOrDefault(t => t != null && t.state != TaskState.Done);
                    CurrentTasks(next, into);
                    return;
                case TaskType.Parallel:
                    foreach (var t in task.GetTaskData().nestedTasks)
                        CurrentTasks(t, into);
                    return;
                default:
                    into.Add(task);
                    return;
            }
        }

        /// <summary>A shunting load at the station, taken: cars from each storage track to the machine's track, loaded, then to <paramref name="to"/>.</summary>
        private static Job MakeShuntingLoad(World world, StationController station, WarehouseMachineController machine,
            IList<(NamedTrack Track, List<TrainCar> Cars)> lots, CargoType cargo, NamedTrack to)
        {
            var allCars = lots.SelectMany(l => TrainCar.ExtractLogicCars(l.Cars)).ToList();
            var go = new GameObject($"ChainJob[ShuntingLoad]: AI Engineer self-test to {to.Id}");
            go.transform.SetParent(station.transform);
            var chain = new JobChainController(go) { carsForJobChain = allCars.ToList() };
            var chainData = new StationsChainData(station.stationInfo.YardID, station.stationInfo.YardID);
            var load = go.AddComponent<StaticShuntingLoadJobDefinition>();
            load.PopulateBaseJobDefinition(station.logicStation, 7200f, 100f, chainData, JobLicenses.Basic);
            load.carsPerStartingTrack = lots.Select(l => new CarsPerTrack(world.Network.Tracks[l.Track.Track].LogicTrack(), TrainCar.ExtractLogicCars(l.Cars).ToList())).ToList();
            load.loadMachine = machine.warehouseMachine;
            load.loadData = new List<CarsPerCargoType> { new CarsPerCargoType(cargo, allCars, allCars.Sum(c => c.capacity)) };
            load.destinationTrack = world.Network.Tracks[to.Track].LogicTrack();
            load.forceCorrectCargoStateOnCars = true;
            chain.AddJobDefinitionToChain(load);
            chain.FinalizeSetupAndGenerateFirstJob();
            var job = load.job ?? throw new InvalidOperationException("the chain made no job");
            SingletonBehaviour<JobsManager>.Instance.TakeJob(job);
            return job;
        }

        private static string Name(World world, int track) => track >= 0 ? world.Network.Describe(track) : "?";

        /// <summary>
        /// What moving a 120 m train from the middle of <paramref name="from"/>
        /// wholly onto <paramref name="to"/> costs by the router, m (a reversal
        /// 400); infinity when none does.
        /// </summary>
        private static double ShuntingCost(World world, int from, int to)
        {
            var length = world.Graph.Length(from);
            var half = Math.Min(60, length / 2 - 1);
            var mid = length / 2;
            var best = double.PositiveInfinity;
            foreach (var goal in OrderGoals.OnTrack(world.Graph, to, 120))
            {
                var request = new RouteRequest
                {
                    EndA = new TrackPosition(from, mid - half),
                    OutwardA = TrackDirection.Backward,
                    EndB = new TrackPosition(from, mid + half),
                    OutwardB = TrackDirection.Forward,
                    TrainLength = 2 * half,
                    Goal = goal.Position,
                    GoalDirection = goal.Arrive,
                    CanSetJunction = (j, b) => true,
                    Blocked = (t, a, b) => world.Occupancy.IsStanding(t, a, b, int.MinValue),
                };
                var route = world.Router.Find(request, out _);
                if (route != null)
                    best = Math.Min(best, route.Length + request.ReversalPenalty * route.Reversals);
            }
            return best;
        }
    }
}
