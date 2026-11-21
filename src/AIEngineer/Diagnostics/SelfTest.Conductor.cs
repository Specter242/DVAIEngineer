#if !PUBLIC_RELEASE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using DV;
using DV.Simulation.Controllers;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    internal static partial class SelfTest
    {
        private const float DriverStep = 0.25f;

        private static IEnumerator ConductorPart()
        {
            var world = Host.World;
            var yards = Dispatch.Yards(world.Network, Vector2.zero);
            NamedTrack Named(string id) => yards.SelectMany(y => y.Tracks).FirstOrDefault(t => t.Id == id);
            var de2 = Globals.G.Types.Liveries.FirstOrDefault(l => l.id == "LocoDE2");
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            if (de2 == null || Named("SM-B7S") == null)
            {
                Line("SKIP conductor: not the stock map");
                yield break;
            }
            // A yard can be full of the save's cars: a few starting tracks at different stations.
            var starts = new List<NamedTrack> { Named("SM-B7S") };
            starts.AddRange(yards.Where(y => y.Tracks.Count > 0)
                .Select(y => y.Tracks.Where(t => t.Id.EndsWith("S") && t.Length >= 120).OrderByDescending(t => t.Length).FirstOrDefault())
                .Where(t => t != null && t.Id != "SM-B7S").Take(5));
            Vector2 Middle(NamedTrack t) => world.Network.MapPointAt(new TrackPosition(t.Track, t.Length / 2));
            conductorRouteFound = false;
            foreach (var from in starts)
            {
                var start = Middle(from);
                var near = yards.SelectMany(y => y.Tracks).Where(t => t.Id != from.Id)
                    .OrderBy(t => (Middle(t) - start).sqrMagnitude).Take(60).ToList();
                yield return Caught("conductor", ConductorRun(world, de2, flat, from, near));
                if (conductorRouteFound)
                    yield break;
            }
            Fail("conductor: nothing is thrown before Execute", $"no route from any of {starts.Count} starting tracks");
        }

        private static bool conductorRouteFound;

        private static IEnumerator ConductorRun(World world, TrainCarLivery de2, TrainCarLivery flat, NamedTrack from, List<NamedTrack> near)
        {
            const string planned = "conductor: nothing is thrown before Execute";
            const string started = "conductor: Execute starts the route shown";
            const string lined = "conductor: each switch lay right as the train reached it";
            const string arrived = "conductor: it saw the train in";
            var liveries = new List<TrainCarLivery> { de2, flat, flat };
            var spawned = CarSpawner.Instance.SpawnCarTypesOnTrack(liveries, liveries.Select(_ => false).ToList(), world.Network.Tracks[from.Track], true, true);
            if (spawned == null || spawned.Count < liveries.Count)
            {
                Line($"conductor: couldn't spawn a train on {from.Id}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            StandBeside(spawned[0]);
            yield return new WaitForSecondsRealtime(15);
            var loco = spawned.First(c => c.carLivery == de2);
            var s = Host.SessionFor(loco, true);
            StartupHelper.Startup(loco);
            s.Controls.Prepare();
            s.ReleaseHandbrakes();
            var waited = 0f;
            while (waited < 60 && !(s.Read().CanMove && (!s.Read().AirKnown || s.Read().CarPipeMin >= 4.2)))
            {
                yield return new WaitForSeconds(1);
                waited += 1;
            }
            world.ScanCars();
            var state = s.Read();
            var conductor = s.Conductor;
            var to = ChooseConductorRoute(world, conductor, state, from.Id, near);
            if (to == null)
            {
                Line($"conductor: no route from {from.Id} to any of the {near.Count} nearest tracks");
                conductor.StandDown();
                s.Controls.Release();
                yield return ClearSpawned(spawned);
                yield break;
            }
            conductorRouteFound = true;
            var lie = world.Network.Junctions.Select(j => j != null ? j.selectedBranch : (byte)0).ToArray();
            yield return new WaitForSeconds(3);
            var moved = world.Network.Junctions.Where((j, i) => j != null && j.selectedBranch != lie[i]).Count();
            Line($"conductor: {from.Id} to {to.Id}, {conductor.Route.Length:F0} m, {conductor.Route.Reversals} reversals, {conductor.SwitchesToThrow} switches to throw");
            (moved == 0 ? (Action<string, string>)Pass : Fail)(planned, $"{moved} switches moved");

            var result = conductor.Execute(s.Read(), world);
            if (result == ConductorExecution.RouteChanged)
                result = conductor.Execute(s.Read(), world);
            (result == ConductorExecution.Started ? (Action<string, string>)Pass : Fail)(started, result.ToString());
            if (result != ConductorExecution.Started)
            {
                conductor.StandDown();
                yield return ClearSpawned(spawned);
                yield break;
            }

            var wrong = 0;
            var derailed = false;
            var elapsed = 0f;
            while (elapsed < 900 && conductor.State == ConductorState.Executing)
            {
                yield return new WaitForSeconds(DriverStep);
                elapsed += DriverStep;
                derailed |= spawned.Any(c => c != null && c.derailed);
                if (derailed)
                    break;
                if (Vector3.Distance(PlayerManager.PlayerTransform.position, s.Loco.transform.position) > 500)
                    StandBeside(s.Loco);
                wrong += DriveForConductor(s, conductor, world);
                if ((int)(elapsed / DriverStep) % 40 == 0)
                    Line($"conductor: {elapsed:F0} s, leg {conductor.LegIndex + 1}, {conductor.Remaining:F0} m to go, {conductor.SwitchesThrown} lined: {ConductorDesk.Say(conductor, conductor.Call)}");
            }
            s.Controls.Secure(Time.time);
            s.Controls.Release();
            (wrong == 0 && !derailed ? (Action<string, string>)Pass : Fail)(lined,
                $"{conductor.SwitchesThrown} thrown, {wrong} met wrong{(derailed ? ", derailed" : "")}, slowest tick {Session.SlowestTickMs:F1} ms");
            (conductor.State == ConductorState.Arrived ? (Action<string, string>)Pass : Fail)(arrived,
                $"{conductor.State} after {elapsed:F0} s: {ConductorDesk.Say(conductor, conductor.Call)}");
            conductor.StandDown();
            yield return new WaitForSeconds(10);
            yield return ClearSpawned(spawned);
        }

        /// <summary>
        /// Plans to the nearest track the save leaves reachable: a reversal
        /// and at least one switch away if one is, within 3 km. Says what
        /// stood in the way of the others.
        /// </summary>
        private static NamedTrack ChooseConductorRoute(World world, Conductor conductor, TrainState state, string from, List<NamedTrack> near)
        {
            Waypoint Goal(NamedTrack t) => OrderGoals.ToTrack(world.Graph, t.Track, state.Length, t.Id, Arrival.CarsFirstIntoDeadEnd);
            NamedTrack best = null;
            var bestRank = int.MaxValue;
            var bestLength = double.PositiveInfinity;
            int refused = 0, open = 0, openPastCars = 0, openPastSwitches = 0;
            var owner = state.Owner;
            foreach (var track in near.Where(t => t.Length >= state.Length + 40))
            {
                if (!conductor.Plan(Goal(track), true, state, world))
                {
                    refused++;
                    // The search again without each thing a route must respect: which is stopping it?
                    var goal = Goal(track).Goal;
                    RouteRequest Bare() => new RouteRequest
                    {
                        EndA = state.EndA, OutwardA = state.OutwardA, EndB = state.EndB, OutwardB = state.OutwardB,
                        TrainLength = state.Length, Goal = goal.Position, GoalDirection = goal.Arrive, SwitchLie = world.SelectedBranch,
                    };
                    Func<int, int, bool> switches = (j, b) => SwitchRules.Usable(world, j, b, owner);
                    Func<int, double, double, bool> cars = (t, a, b) => world.Occupancy.IsStanding(t, a, b, owner);
                    var bare = Bare();
                    if (world.Router.Find(bare) != null)
                        open++;
                    var noCars = Bare();
                    noCars.CanSetJunction = switches;
                    if (world.Router.Find(noCars) != null)
                        openPastCars++;
                    var noSwitches = Bare();
                    noSwitches.Blocked = cars;
                    if (world.Router.Find(noSwitches) != null)
                        openPastSwitches++;
                    continue;
                }
                if (conductor.Route.Length > 3000)
                    continue;
                var rank = (conductor.Route.Reversals > 0 ? 0 : 2) + (conductor.SwitchesToThrow > 0 ? 0 : 1);
                if (rank < bestRank || (rank == bestRank && conductor.Route.Length < bestLength))
                {
                    bestRank = rank;
                    bestLength = conductor.Route.Length;
                    best = track;
                }
            }
            Line($"conductor: {state.CarCount} cars, {state.Length:F0} m on {from}; {refused} tracks refused: {open} open with nothing counted, "
                + $"{openPastCars} ignoring standing cars, {openPastSwitches} ignoring switch rules; {Surroundings(world, state)}");
            conductor.StandDown();
            return best != null && conductor.Plan(Goal(best), true, state, world) ? best : null;
        }

        /// <summary>
        /// The conductor through the tablet, as the player works it: a track
        /// picked on the map and the Conductor choice, then the tab's Execute
        /// and Stand down, each pressed through the screen's own buttons.
        /// </summary>
        private static IEnumerator TabletConductor(Held.EngineerTabletScreen screen, TrainCar loco)
        {
            const string planned = "tablet conductor: the map's choice plans a route and opens the tab";
            const string executed = "tablet conductor: Execute starts it";
            const string stood = "tablet conductor: Stand down ends it";
            var world = Host.World;
            if (loco == null)
            {
                Line("SKIP tablet conductor: no loco of its own to work with");
                yield break;
            }
            Host.SessionFor(loco, true).Stop(secure: false);
            ConductorDesk.LocoForTest = loco;
            try
            {
                world.ScanCars();
                var session = Host.SessionFor(loco, true);
                var state = session.Read();
                var at = Dispatch.MapPosition(loco);
                Vector2 Middle(NamedTrack t) => world.Network.MapPointAt(new TrackPosition(t.Track, t.Length / 2));
                var near = Dispatch.Yards(world.Network, at).SelectMany(y => y.Tracks).OrderBy(t => (Middle(t) - at).sqrMagnitude).Take(60).ToList();
                var to = ChooseConductorRoute(world, session.Conductor, state, loco.ID, near);
                session.Conductor.StandDown();
                if (to == null)
                {
                    Line("SKIP tablet conductor: no route from this loco");
                    yield break;
                }
                var choice = UI.Tablet.TabletMap.PickTrackForTest(world, new TrackPosition(to.Track, to.Length / 2), "Conductor");
                var pressed = choice >= 0 ? UI.Tablet.TabletMap.DoForTest(choice) : null;
                yield return new WaitForSeconds(1);
                screen.DrawNow();
                var c = session.Conductor;
                (pressed != null && c.State == ConductorState.Planned && UI.Tablet.TabletUi.Page == UI.Tablet.TabletTab.Conductor ? (Action<string, string>)Pass : Fail)(planned,
                    $"choice {choice}, {c.State} to {to.Id}, the page is {UI.Tablet.TabletUi.Page}");
                SaveScreen(screen, "tablet-conductor-plan");
                if (c.State != ConductorState.Planned)
                    yield break;
                // The page's bottom row: Execute at its left; then See the map, Stand down.
                screen.PressSlotForTest(screen.SlotAt(new Vector2(36 + 160, 740 + 40)));
                yield return new WaitForSeconds(1);
                screen.DrawNow();
                (c.State == ConductorState.Executing ? (Action<string, string>)Pass : Fail)(executed, $"{c.State}: {ConductorDesk.Say(c, c.Call)}");
                SaveScreen(screen, "tablet-conductor-working");
                screen.PressSlotForTest(screen.SlotAt(new Vector2(36 + 252 + 130, 740 + 40)));
                yield return new WaitForSeconds(1);
                screen.DrawNow();
                (c.State == ConductorState.None ? (Action<string, string>)Pass : Fail)(stood, c.State.ToString());
                SaveScreen(screen, "tablet-conductor-stood-down");
            }
            finally
            {
                ConductorDesk.LocoForTest = null;
                ConductorDesk.StandDown();
            }
        }

        /// <summary>What lies beyond each end of the train: standing cars on its track, and the switches it leaves by.</summary>
        private static string Surroundings(World world, TrainState state)
        {
            string End(string name, TrackPosition at, TrackDirection outward)
            {
                var graph = world.Graph;
                var exit = graph.OffsetOf(at.Track, outward.ExitEnd());
                var spans = new List<OccupiedSpan>();
                world.Occupancy.Standing(at.Track, state.Owner, spans);
                var ahead = spans.Where(c => (Math.Min(c.From, c.To) - at.Offset) * (int)outward > -1 || (Math.Max(c.From, c.To) - at.Offset) * (int)outward > 0)
                    .Select(c => Math.Min(Math.Abs(c.From - at.Offset), Math.Abs(c.To - at.Offset))).DefaultIfEmpty(double.PositiveInfinity).Min();
                var links = graph.Links(at.Track, outward.ExitEnd()).Select(l => l.NeedsJunction
                    ? $"switch {l.Junction} branch {l.Branch}{(SwitchRules.Fouled(world, l.Junction, l.Branch, state.Owner) ? " fouled" : "")}{(world.Jammed(l.Junction, state.Owner) ? " jammed" : "")}"
                    : $"track {l.Track}");
                return $"end {name} {Math.Abs(exit - at.Offset):F0} m from its track's end, cars {ahead:F0} m ahead, then {string.Join(", ", links)}";
            }
            return End("A", state.EndA, state.OutwardA) + "; " + End("B", state.EndB, state.OutwardB);
        }

        /// <summary>One look by the test's driver: down the leg at a walking pace, stopping at its end. Returns the switches met lying wrong.</summary>
        private static int DriveForConductor(Session s, Conductor c, World world)
        {
            var state = s.Read();
            var route = c.Route;
            if (route == null || c.LegIndex >= route.Legs.Count)
            {
                s.Controls.Apply(new ControlOutput { TrainBrake = 0.6, LocoBrake = 0.6 }, null, Math.Abs(state.SpeedTowardA) < 0.05, Time.time);
                return 0;
            }
            var leg = route.Legs[c.LegIndex];
            var speed = leg.Leader == ConsistEnd.A ? state.SpeedTowardA : -state.SpeedTowardA;
            var toEnd = leg.Length - c.Progress;
            var target = c.Call.Level == ConductorCallLevel.Stop ? 0 : Math.Min(5, Math.Sqrt(2 * 0.25 * Math.Max(toEnd - 2, 0)));
            var standing = Math.Abs(speed) < Engineer.StandingSpeed;
            var output = speed < target - 0.3 ? new ControlOutput { Throttle = 0.35 }
                : speed > target + 0.3 || target <= 0 ? new ControlOutput { TrainBrake = 0.5, LocoBrake = 0.5 }
                : new ControlOutput();
            s.Controls.Apply(output, leg.Leader == s.FrontEnd, standing, Time.time, speed);
            var wrong = 0;
            foreach (var sw in leg.Switches)
            {
                var ahead = sw.AtDistance - c.Progress;
                if (ahead > 0 && ahead < 8 && world.SelectedBranch(sw.Junction) != sw.Branch)
                    wrong++;
            }
            return wrong;
        }
    }
}
#endif
