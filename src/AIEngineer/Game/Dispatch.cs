using System;
using System.Collections.Generic;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using DV.Logic.Job;
using DV.Utils;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>A named track a dispatcher can send a loco to.</summary>
    internal sealed class NamedTrack
    {
        public int Track;
        public string Id;
        public string Part;
        public double Length;
        public Vector2 Middle;

        /// <summary>Whether one end is a buffer stop: a stub, which cars should go into first.</summary>
        public bool DeadEnd;
    }

    /// <summary>A yard's named tracks, by the game's track IDs ("HB-C3L").</summary>
    internal sealed class Yard
    {
        public string Id;
        public readonly List<NamedTrack> Tracks = new List<NamedTrack>();
        public Vector2 Centre;

        /// <summary>
        /// A sub-yard of tracks of a type the stock map doesn't use, listed
        /// as a yard of its own under its sub-yard's name: Interchange Yards'
        /// West Interchange ("WI", its tracks MF-WI1X and on) and Northeast
        /// Interchange ("NEI"). Not a station's: no cars are set aside there.
        /// </summary>
        public bool Added;
    }

    /// <summary>A standing cut: cars with no loco, which a loco can be sent to couple to.</summary>
    internal sealed class Cut
    {
        public Trainset Set;
        public CutEnd[] Ends;

        /// <summary>Each end by compass point, from the other: "west", "east".</summary>
        public string[] EndNames;

        public int Cars;
        public double Length;
        public string Where;
        public string YardId;
        public string Jobs;

        /// <summary>The yard the first job's cars are bound for, if any.</summary>
        public string Destination;

        public float Distance;

        public string Label => $"{Cars} car{(Cars == 1 ? "" : "s")} on {Where}{(Jobs != null ? ", " + Jobs : "")}";

        public string Short => $"{Cars} car{(Cars == 1 ? "" : "s")} on {Where}";
    }

    /// <summary>Which end of a cut a pickup couples to.</summary>
    internal enum CutSide
    {
        /// <summary>The end that heads the cars toward their job's destination, or the nearer end if they have none.</summary>
        Auto,
        Nearer,
        End0,
        End1,
    }

    /// <summary>
    /// What the tablet's orders work from (the old dispatcher window's,
    /// removed in 0.15.0): the player's own locos (owned vehicles, as bought
    /// locos and garage locos are), every yard's named tracks, and the
    /// standing cuts, nearest first. Yards are read once per network; cuts
    /// when the tablet asks, a few times a second at most.
    /// </summary>
    internal static class Dispatch
    {
        private static Network yardsFor;
        private static List<Yard> yards = new List<Yard>();

        /// <summary>Clears cached yard data when the world unloads.</summary>
        public static void ResetForUnload()
        {
            yardsFor = null;
            yards.Clear();
        }

        /// <summary>The player's own locos.</summary>
        public static List<TrainCar> Locos()
        {
            var list = new List<TrainCar>();
            var spawner = CarSpawner.Instance;
            if (spawner == null)
                return list;
            foreach (var loco in spawner.AllLocos)
            {
                if (loco != null && loco.uniqueCar)
                    list.Add(loco);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.ID, b.ID));
            return list;
        }

        /// <summary>A loco's short type name: its livery's ID without the "Loco" prefix ("DE2").</summary>
        public static string TypeName(TrainCar car)
        {
            var id = car.carLivery != null ? car.carLivery.id : "?";
            return id.StartsWith("Loco", StringComparison.Ordinal) && id.Length > 4 ? id.Substring(4) : id;
        }

        /// <summary>Where a car stands, by its track's ID, or "main line".</summary>
        public static string Where(TrainCar car)
        {
            var track = car?.logicCar?.FrontBogieTrack;
            if (track?.ID != null && !track.ID.IsGeneric())
                return track.ID.FullDisplayID;
            return car != null && car.derailed ? "derailed" : "main line";
        }

        public static Vector2 MapPosition(TrainCar car)
        {
            var abs = car.transform.position - WorldMover.currentMove;
            return new Vector2(abs.x, abs.z);
        }

        /// <summary>Every yard with named tracks, nearest to <paramref name="from"/> first.</summary>
        public static List<Yard> Yards(Network network, Vector2 from)
        {
            var sorted = new List<Yard>(AllYards(network));
            sorted.Sort((a, b) => (a.Centre - from).sqrMagnitude.CompareTo((b.Centre - from).sqrMagnitude));
            return sorted;
        }

        private static List<Yard> AllYards(Network network)
        {
            if (!ReferenceEquals(yardsFor, network))
            {
                yardsFor = network;
                yards = BuildYards(network);
            }
            return yards;
        }

        /// <summary>The track types of the stock map's yards (storage, loading, in, out, parking, passengers' and the rest).</summary>
        private static readonly HashSet<string> StockTypes = new HashSet<string> { "S", "L", "I", "O", "P", "M", "SP", "LP", "D" };

        /// <summary>
        /// The yard a track is listed under: its station's, or for a type the
        /// stock map doesn't use (an added interchange's), its sub-yard's.
        /// </summary>
        private static string YardKey(TrackID id, out bool added)
        {
            added = false;
            // FullID is yard-subyard-number-type.
            var parts = id.FullID.Split('-');
            if (parts.Length == 4 && !StockTypes.Contains(parts[3]) && !string.IsNullOrEmpty(parts[1]))
            {
                added = true;
                return parts[1];
            }
            return id.yardId;
        }

        private static List<Yard> BuildYards(Network network)
        {
            var byId = new Dictionary<string, Yard>();
            for (var t = 0; t < network.Tracks.Length; t++)
            {
                TrackID id;
                try
                {
                    id = network.Tracks[t].LogicTrack()?.ID;
                }
                catch (Exception)
                {
                    continue;
                }
                if (id == null || id.IsGeneric() || string.IsNullOrEmpty(id.yardId))
                    continue;
                var key = YardKey(id, out var added);
                // An added sub-yard is keyed apart, so a station of the same name can't take its tracks.
                var slot = added ? "+" + key : key;
                if (!byId.TryGetValue(slot, out var yard))
                    byId[slot] = yard = new Yard { Id = key, Added = added };
                var length = network.Graph.Length(t);
                yard.Tracks.Add(new NamedTrack
                {
                    Track = t,
                    Id = id.FullDisplayID,
                    Part = id.TrackPartOnly,
                    Length = length,
                    Middle = network.MapPointAt(new TrackPosition(t, length / 2)),
                    DeadEnd = network.Graph.Links(t, TrackEnd.Start).Length == 0 || network.Graph.Links(t, TrackEnd.End).Length == 0,
                });
            }
            foreach (var yard in byId.Values)
            {
                if (yard.Added && byId.ContainsKey(yard.Id))
                    yard.Id = yard.Tracks[0].Id.Split('-')[0] + "-" + yard.Id;
                var sum = Vector2.zero;
                foreach (var track in yard.Tracks)
                    sum += track.Middle;
                yard.Centre = sum / Math.Max(1, yard.Tracks.Count);
                yard.Tracks.Sort((a, b) => string.CompareOrdinal(a.Part, b.Part));
            }
            var list = new List<Yard>(byId.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        /// <summary>The standing cuts on the network, nearest to <paramref name="from"/> first, within a radius.</summary>
        public static List<Cut> Cuts(World world, Vector2 from, float radius, Trainset own)
        {
            var cuts = new List<Cut>();
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            foreach (var set in Trainset.allSets)
            {
                if (set == null || ReferenceEquals(set, own) || set.cars.Count == 0 || set.locoIndices.Count > 0)
                    continue;
                var first = set.firstCar;
                if (first == null || first.derailed)
                    continue;
                var distance = (MapPosition(first) - from).magnitude;
                if (distance > radius)
                    continue;
                var a = set.GetEndmost(true);
                var b = set.GetEndmost(false);
                if (!world.TryPlace(a, out var pa, out var oa) || !world.TryPlace(b, out var pb, out var ob))
                    continue;
                var length = 0.0;
                foreach (var car in set.cars)
                    length += car.InterCouplerDistance;
                var names = Compass(world.Network.MapPointAt(pa), world.Network.MapPointAt(pb));
                var track = first.logicCar?.FrontBogieTrack?.ID;
                cuts.Add(new Cut
                {
                    Set = set,
                    Ends = new[] { new CutEnd(pa, oa), new CutEnd(pb, ob) },
                    EndNames = names,
                    Cars = set.cars.Count,
                    Length = length,
                    Where = Where(first),
                    YardId = track != null && !track.IsGeneric() ? track.yardId : null,
                    Jobs = JobsOf(set, jobs, out var destination),
                    Destination = destination,
                    Distance = distance,
                });
            }
            cuts.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            return cuts;
        }

        /// <summary>Two ends' names by compass point, each seen from the other (the map's +z is north).</summary>
        private static string[] Compass(Vector2 a, Vector2 b)
        {
            var d = a - b;
            if (Math.Abs(d.x) >= Math.Abs(d.y))
                return d.x >= 0 ? new[] { "east", "west" } : new[] { "west", "east" };
            return d.y >= 0 ? new[] { "north", "south" } : new[] { "south", "north" };
        }

        /// <summary>The cut's jobs, for its label: the first job's ID and where it goes, and how many more.</summary>
        private static string JobsOf(Trainset set, JobsManager jobs, out string destination)
        {
            destination = null;
            if (jobs == null)
                return null;
            string first = null;
            var ids = new HashSet<string>();
            foreach (var car in set.cars)
            {
                Job job = null;
                try
                {
                    job = car?.logicCar != null ? jobs.GetJobOfCar(car.logicCar) : null;
                }
                catch (Exception)
                {
                    // A car between jobs.
                }
                if (job == null || !ids.Add(job.ID))
                    continue;
                if (first == null)
                {
                    var to = job.chainData?.chainDestinationYardId;
                    first = string.IsNullOrEmpty(to) ? job.ID : $"{job.ID} to {to}";
                    destination = string.IsNullOrEmpty(to) ? null : to;
                }
            }
            if (first == null)
                return null;
            return ids.Count > 1 ? $"{first} +{ids.Count - 1}" : first;
        }

        /// <summary>
        /// Sends (or queues) a loco onto a named track, the whole train on
        /// it, the end <paramref name="arrival"/> asks for first (by default
        /// the cars, into a stub).
        /// </summary>
        public static Waypoint GoToTrack(Session session, World world, NamedTrack track, bool queue,
            Arrival arrival = Arrival.CarsFirstIntoDeadEnd)
        {
            var waypoint = OrderGoals.ToTrack(world.Graph, track.Track, TrainLength(session), track.Id, arrival);
            Give(session, waypoint, queue);
            return waypoint;
        }

        /// <summary>
        /// Sends (or queues) a loco to couple to a cut on the end
        /// <paramref name="side"/> picks, arriving as <paramref name="facing"/>
        /// asks (by default back first: the loco then faces away from the cars, to pull them forward).
        /// </summary>
        public static Waypoint PickUp(Session session, World world, Cut cut, bool queue,
            CutSide side = CutSide.Auto, Arrival facing = Arrival.BackFirst)
        {
            var end = EndOf(world, cut, side, TrainLength(session), out var why);
            // The end asked for, or the other when it can't be reached (said so); else whichever is reached first.
            var goals = end.HasValue
                ? OrderGoals.ToCouple(world.Graph, cut.Ends, end.Value, cut.EndNames, world.SelectedBranchFunc)
                : OrderGoals.ToCouple(world.Graph, cut.Ends, world.SelectedBranchFunc);
            var waypoint = new Waypoint(goals, WaypointAction.Couple)
            {
                Label = end.HasValue ? $"{cut.Short}, {cut.EndNames[end.Value]} end" : cut.Short,
                Arrival = facing,
            };
            if (why != null)
                Main.Log($"dispatch: {session.Name}: {why}");
            Give(session, waypoint, queue);
            return waypoint;
        }

        /// <summary>The cut end a pickup couples to; null for whichever is reached first.</summary>
        public static int? EndOf(World world, Cut cut, CutSide side, double locoLength, out string why)
        {
            why = null;
            switch (side)
            {
                case CutSide.End0:
                    return 0;
                case CutSide.End1:
                    return 1;
                case CutSide.Nearer:
                    return null;
            }
            if (cut.Destination == null || cut.Destination == cut.YardId)
                return null;
            var yard = AllYards(world.Network).Find(y => y.Id == cut.Destination);
            if (yard == null || yard.Tracks.Count == 0)
            {
                why = $"{cut.Destination} has no named tracks; coupling on the nearer end";
                return null;
            }
            // Its track nearest the yard's middle stands for the yard.
            var into = yard.Tracks[0];
            foreach (var t in yard.Tracks)
            {
                if ((t.Middle - yard.Centre).sqrMagnitude < (into.Middle - yard.Centre).sqrMagnitude)
                    into = t;
            }
            var end = OrderGoals.EndToward(world.Router, cut.Ends[0], cut.Ends[1], cut.Length + locoLength,
                new TrackPosition(into.Track, into.Length / 2));
            why = end.HasValue
                ? $"{cut.Short}: the {cut.EndNames[end.Value]} end leads toward {cut.Destination}"
                : $"no route from {cut.Short} to {cut.Destination}; coupling on the nearer end";
            return end;
        }

        private static double TrainLength(Session session) =>
            session.HasState ? session.State.Length : session.Loco.InterCouplerDistance;

        private static void Give(Session session, Waypoint waypoint, bool queue)
        {
            if (queue)
                session.Engineer.Enqueue(waypoint);
            else
                session.Engineer.GoTo(waypoint);
            Main.Log($"dispatch: {session.Name}: {(queue ? "queued" : "sent")} {waypoint}");
        }
    }
}
