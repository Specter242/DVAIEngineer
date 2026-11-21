using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Tracks;
using DV.ThingTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AIEngineer.Game
{
    /// <summary>
    /// The game's service stations (<c>PitStopStation</c>): where each is,
    /// what it sells (its resource modules: diesel, coal, water, charge, oil,
    /// sand, repairs), and the spot on the track its pit area covers. A
    /// tablet's Fuel and Service send a loco to the nearest that has what it
    /// needs, loco end first, the loco stopped across that spot. Stations are
    /// remembered as they are seen: the world loads its scenery near the
    /// player, so one far away may not be loaded now.
    /// </summary>
    internal static class Service
    {
        internal sealed class Station
        {
            public Vector3 Center;
            public HashSet<ResourceType> Offers;
            public TrackPosition Spot;
            public string Where;
        }

        private static readonly ResourceType[] FuelTypes = { ResourceType.Fuel, ResourceType.Coal, ResourceType.Water, ResourceType.ElectricCharge };
        private static readonly ResourceType[] RepairTypes = { ResourceType.Car_DMG, ResourceType.Wheels_DMG, ResourceType.MechanicalPowertrain_DMG, ResourceType.ElectricalPowertrain_DMG };

        private static Network stationsFor;
        private static readonly List<Station> stations = new List<Station>();

        /// <summary>Clears cached service station data when the world unloads.</summary>
        public static void ResetForUnload()
        {
            stationsFor = null;
            stations.Clear();
        }

        /// <summary>Every station seen so far on this network, read again as more of the world loads.</summary>
        public static IReadOnlyList<Station> Stations(Network network)
        {
            if (!ReferenceEquals(stationsFor, network))
            {
                stationsFor = network;
                stations.Clear();
            }
            foreach (var pit in Resources.FindObjectsOfTypeAll<PitStopStation>())
            {
                if (pit == null || !pit.gameObject.scene.IsValid() || pit.pitstop == null || pit.locoResourceModules == null)
                    continue;
                var area = Area(pit.pitstop);
                var center = area.center - WorldMover.currentMove;
                if (stations.Any(s => (s.Center - center).sqrMagnitude < 25))
                    continue;
                if (!network.TryFindNearest(new Vector2(center.x, center.z), 25f, out var spot, out _))
                    continue;
                var offers = new HashSet<ResourceType>();
                foreach (var m in pit.locoResourceModules.resourceModules ?? new LocoResourceModule[0])
                    if (m != null)
                        offers.Add(m.resourceType);
                stations.Add(new Station { Center = center, Offers = offers, Spot = spot, Where = NearestStation(area.center) });
            }
            return stations;
        }

        /// <summary>The pit area: its trigger colliders' bounds, or where it stands.</summary>
        private static Bounds Area(PitStop pit)
        {
            Bounds? b = null;
            foreach (var c in pit.GetComponents<Collider>())
            {
                if (!c.isTrigger)
                    continue;
                // A disabled collider has no bounds; the box's own size and place stand for it.
                var cb = c.enabled && c.gameObject.activeInHierarchy ? c.bounds
                    : c is BoxCollider box ? new Bounds(c.transform.TransformPoint(box.center), Vector3.Scale(box.size, c.transform.lossyScale)) : new Bounds(c.transform.position, Vector3.one);
                if (b.HasValue)
                {
                    var u = b.Value;
                    u.Encapsulate(cb);
                    b = u;
                }
                else
                    b = cb;
            }
            return b ?? new Bounds(pit.transform.position, Vector3.one);
        }

        private static string NearestStation(Vector3 at)
        {
            var best = StationController.allStations?.Where(s => s != null && s.stationInfo != null)
                .OrderBy(s => (s.transform.position - at).sqrMagnitude).FirstOrDefault();
            return best != null ? best.stationInfo.YardID : "the service station";
        }

        /// <summary>The supplies a loco runs on, its tender's with it: the fuel types it holds.</summary>
        public static HashSet<ResourceType> FuelsOf(TrainCar loco)
        {
            var found = new HashSet<ResourceType>();
            foreach (var car in PowerOf(loco))
            {
                var p = car.GetComponent<CarPitStopParametersBase>();
                var parameters = p != null ? p.GetCarPitStopParameters() : null;
                if (parameters == null)
                    continue;
                foreach (var t in FuelTypes)
                    if (parameters.ContainsKey(t))
                        found.Add(t);
            }
            return found;
        }

        private static IEnumerable<TrainCar> PowerOf(TrainCar loco)
        {
            yield return loco;
            var set = loco.trainset;
            if (set == null)
                yield break;
            foreach (var car in set.cars)
                if (car != null && car != loco && CarTypes.IsTender(car.carLivery))
                    yield return car;
        }

        /// <summary>
        /// The nearest station selling a loco's fuel (any of them: a steam
        /// loco's coal or water), or, for service, the nearest that repairs;
        /// null if none is known.
        /// </summary>
        public static Station Nearest(World world, TrainCar loco, bool fuel)
        {
            var wants = fuel ? FuelsOf(loco) : new HashSet<ResourceType>(RepairTypes);
            if (wants.Count == 0)
                wants = new HashSet<ResourceType>(FuelTypes);
            var here = loco.transform.position - WorldMover.currentMove;
            return Stations(world.Network).Where(s => s.Offers.Overlaps(wants)).OrderBy(s => (s.Center - here).sqrMagnitude).FirstOrDefault();
        }

        /// <summary>Sends (or queues) a loco to a station, loco end first, the loco's middle over its spot. Returns what it was sent to, for the strip.</summary>
        public static string Send(Session session, World world, Station station, bool fuel, bool queue)
        {
            var loco = session.Loco;
            var length = Math.Max(10, PowerOf(loco).Sum(c => c.InterCouplerDistance));
            var graph = world.Graph;
            var t = station.Spot.Track;
            var len = graph.Length(t);
            // Its leading end half its length past the spot, whichever way it comes.
            var goals = new List<WaypointGoal>
            {
                new WaypointGoal(new TrackPosition(t, Math.Min(len, station.Spot.Offset + length / 2)), TrackDirection.Forward),
                new WaypointGoal(new TrackPosition(t, Math.Max(0, station.Spot.Offset - length / 2)), TrackDirection.Backward),
            };
            var label = $"{(fuel ? "fuel" : "service")} at {station.Where}";
            var waypoint = new Waypoint(goals) { Label = label, Arrival = Arrival.LocoFirst };
            if (queue)
                session.Engineer.Enqueue(waypoint);
            else
                session.Engineer.GoTo(waypoint);
            var km = (station.Center - (loco.transform.position - WorldMover.currentMove)).magnitude / 1000;
            Main.Log($"service: {session.Name}: {(queue ? "queued" : "sent")} to {label} ({km:F1} km), track {world.Network.Describe(t)}; it sells {string.Join(", ", station.Offers)}");
            return $"{label}, {km:F1} km away";
        }
    }
}
