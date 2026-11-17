using System;
using System.Collections.Generic;
using System.Diagnostics;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using DV.PointSet;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>
    /// The game's track network as the engine-free graph, built once per
    /// world load: tracks and junctions indexed both ways, each track's
    /// curve limits, a simplified polyline per track for the map, and a
    /// grid for finding the track nearest a point.
    /// </summary>
    internal sealed class Network
    {
        private const double GridCell = 100;

        private readonly Dictionary<RailTrack, int> trackIndex = new Dictionary<RailTrack, int>();
        private readonly Dictionary<Junction, int> junctionIndex = new Dictionary<Junction, int>();
        private readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();

        private Network() { }

        public TrackGraph Graph { get; private set; }

        public Router Router { get; private set; }

        public RailTrack[] Tracks { get; private set; }

        public Junction[] Junctions { get; private set; }

        /// <summary>Per track: map points (x, z) and their offsets, every few meters.</summary>
        public Vector2[][] Polylines { get; private set; }

        public float[][] PolylineOffsets { get; private set; }

        /// <summary>The network's extent on the map, for fitting the view.</summary>
        public Rect Bounds { get; private set; }

        public bool TryGetTrack(RailTrack track, out int index) => trackIndex.TryGetValue(track, out index);

        public bool TryGetJunction(Junction junction, out int index) => junctionIndex.TryGetValue(junction, out index);

        public static Network Build(double yardCapKmh)
        {
            var watch = Stopwatch.StartNew();
            var network = new Network();
            var tracks = RailTrackRegistryBase.RailTracks;
            var junctions = RailTrackRegistryBase.Junctions;
            var builder = new TrackGraphBuilder();
            var usable = new List<RailTrack>(tracks.Length);
            var polylines = new List<Vector2[]>(tracks.Length);
            var polyOffsets = new List<float[]>(tracks.Length);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            var offsets = new List<double>();
            var points = new List<MapPoint>();

            foreach (var track in tracks)
            {
                if (track == null)
                    continue;
                var set = track.GetKinkedPointSet();
                if (set == null || set.points == null || set.points.Length < 2 || !(set.span > 0.01))
                    continue;
                offsets.Clear();
                points.Clear();
                var line = new List<Vector2>();
                var lineOffsets = new List<float>();
                // Every fourth point (2 m) is plenty for curvature; the map takes every tenth (5 m).
                for (var i = 0; i < set.points.Length; i++)
                {
                    var last = i == set.points.Length - 1;
                    var p = set.points[i];
                    if (i % 4 == 0 || last)
                    {
                        offsets.Add(p.span);
                        points.Add(new MapPoint(p.position.x, p.position.z));
                    }
                    if (i % 10 == 0 || last)
                    {
                        var v = new Vector2((float)p.position.x, (float)p.position.z);
                        line.Add(v);
                        lineOffsets.Add((float)p.span);
                        minX = Math.Min(minX, v.x);
                        maxX = Math.Max(maxX, v.x);
                        minZ = Math.Min(minZ, v.y);
                        maxZ = Math.Max(maxZ, v.y);
                    }
                }
                var cap = IsYardTrack(track) ? yardCapKmh : CurveSpeed.StraightKmh;
                var profile = CurveSpeed.Profile(offsets, points, cap);
                var first = set.points[0].position;
                var lastPoint = set.points[set.points.Length - 1].position;
                var index = builder.AddTrack(set.span, new MapPoint(first.x, first.z), new MapPoint(lastPoint.x, lastPoint.z), profile);
                network.trackIndex[track] = index;
                usable.Add(track);
                polylines.Add(line.ToArray());
                polyOffsets.Add(lineOffsets.ToArray());
            }

            // A turntable's bridge rejoins whichever tracks it is turned to, so
            // the graph built now would go stale: bridges are left unjoined, as
            // dead ends both ways, and the engineer doesn't route over them.
            var bridges = new HashSet<RailTrack>();
            foreach (var turntable in UnityEngine.Object.FindObjectsOfType<TurntableRailTrack>())
            {
                if (turntable != null && turntable.Track != null)
                    bridges.Add(turntable.Track);
            }

            // Plain joints: each track end tied straight to another's.
            foreach (var track in usable)
            {
                if (bridges.Contains(track))
                    continue;
                var index = network.trackIndex[track];
                if (track.inJunction == null && track.inBranch?.track != null && !bridges.Contains(track.inBranch.track)
                    && network.trackIndex.TryGetValue(track.inBranch.track, out var other))
                    builder.Join(index, TrackEnd.Start, other, track.inBranch.first ? TrackEnd.Start : TrackEnd.End);
                if (track.outJunction == null && track.outBranch?.track != null && !bridges.Contains(track.outBranch.track)
                    && network.trackIndex.TryGetValue(track.outBranch.track, out other))
                    builder.Join(index, TrackEnd.End, other, track.outBranch.first ? TrackEnd.Start : TrackEnd.End);
            }

            // Switches: the stem to each branch, in the game's branch order.
            var usableJunctions = new List<Junction>();
            foreach (var junction in junctions)
            {
                if (junction == null || junction.inBranch?.track == null || junction.outBranches == null || junction.outBranches.Count == 0)
                    continue;
                if (!network.trackIndex.TryGetValue(junction.inBranch.track, out var stem))
                    continue;
                var branches = new (int, TrackEnd)[junction.outBranches.Count];
                var ok = true;
                for (var b = 0; b < branches.Length; b++)
                {
                    var branch = junction.outBranches[b];
                    if (branch?.track == null || !network.trackIndex.TryGetValue(branch.track, out var bt))
                    {
                        ok = false;
                        break;
                    }
                    branches[b] = (bt, branch.first ? TrackEnd.Start : TrackEnd.End);
                }
                if (!ok)
                    continue;
                var pos = junction.position - WorldMover.currentMove;
                var j = builder.AddJunction(stem, junction.inBranch.first ? TrackEnd.Start : TrackEnd.End, branches, new MapPoint(pos.x, pos.z));
                network.junctionIndex[junction] = j;
                usableJunctions.Add(junction);
            }

            network.Graph = builder.Build();
            network.Router = new Router(network.Graph);
            network.Tracks = usable.ToArray();
            network.Junctions = usableJunctions.ToArray();
            network.Polylines = polylines.ToArray();
            network.PolylineOffsets = polyOffsets.ToArray();
            network.Bounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            for (var t = 0; t < network.Polylines.Length; t++)
            {
                foreach (var v in network.Polylines[t])
                    network.AddToGrid(v, t);
            }
            Main.Log($"Track network: {usable.Count} tracks, {usableJunctions.Count} switches, {bridges.Count} turntables left out, built in {watch.ElapsedMilliseconds} ms");
            return network;
        }

        /// <summary>
        /// Writes the graph as JSON (tracks with their ends, lines, links,
        /// and names; switches with their branches and settings), so routing
        /// and the station booklet's pages can be checked offline against the
        /// real network.
        /// </summary>
        public void Export(string path)
        {
            var g = Graph;
            var tracks = new Newtonsoft.Json.Linq.JArray();
            for (var t = 0; t < g.TrackCount; t++)
            {
                var s = g.EndPoint(t, TrackEnd.Start);
                var e = g.EndPoint(t, TrackEnd.End);
                Newtonsoft.Json.Linq.JArray Links(TrackEnd end)
                {
                    var a = new Newtonsoft.Json.Linq.JArray();
                    foreach (var l in g.Links(t, end))
                        a.Add(new Newtonsoft.Json.Linq.JArray(l.Track, (int)l.End, l.Junction, l.Branch));
                    return a;
                }
                // The track's line, as the map draws it: the station booklet's yard pages are laid out from it.
                var pts = new Newtonsoft.Json.Linq.JArray();
                foreach (var v in Polylines[t])
                    pts.Add(new Newtonsoft.Json.Linq.JArray(Math.Round(v.x, 2), Math.Round(v.y, 2)));
                tracks.Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["name"] = Tracks[t].name,
                    ["len"] = g.Length(t),
                    ["s"] = new Newtonsoft.Json.Linq.JArray(s.X, s.Z),
                    ["e"] = new Newtonsoft.Json.Linq.JArray(e.X, e.Z),
                    ["pts"] = pts,
                    ["in"] = Links(TrackEnd.Start),
                    ["out"] = Links(TrackEnd.End),
                });
            }
            var junctions = new Newtonsoft.Json.Linq.JArray();
            for (var j = 0; j < g.JunctionCount; j++)
            {
                var info = g.Junction(j);
                var branches = new Newtonsoft.Json.Linq.JArray();
                foreach (var (track, end) in info.Branches)
                    branches.Add(new Newtonsoft.Json.Linq.JArray(track, (int)end));
                junctions.Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["stem"] = new Newtonsoft.Json.Linq.JArray(info.StemTrack, (int)info.StemEnd),
                    ["branches"] = branches,
                    ["x"] = info.Position.X,
                    ["z"] = info.Position.Z,
                    ["selected"] = Junctions[j].selectedBranch,
                });
            }
            var root = new Newtonsoft.Json.Linq.JObject { ["tracks"] = tracks, ["junctions"] = junctions };
            System.IO.File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>Yard tracks by the game's naming: "[Y]_[station]_[...]".</summary>
        private static bool IsYardTrack(RailTrack track) => track.name != null && track.name.StartsWith("[Y]", StringComparison.Ordinal);

        private static long Cell(double x, double z) =>
            ((long)Math.Floor(x / GridCell) << 32) ^ (uint)(int)Math.Floor(z / GridCell);

        private void AddToGrid(Vector2 v, int track)
        {
            var key = Cell(v.x, v.y);
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new List<int>(4);
            if (list.Count == 0 || list[list.Count - 1] != track)
            {
                if (!list.Contains(track))
                    list.Add(track);
            }
        }

        /// <summary>
        /// The track position nearest a map point, within <paramref name="maxDistance"/>
        /// meters, by the polyline (then refined on the track's own point set).
        /// </summary>
        private readonly HashSet<int> nearestSeen = new HashSet<int>();

        public bool TryFindNearest(Vector2 mapPoint, float maxDistance, out TrackPosition position, out float distance)
        {
            position = default;
            distance = float.MaxValue;
            var best = -1;
            var bestOffset = 0.0;
            var reach = (int)Math.Ceiling(maxDistance / GridCell);
            var cx = (long)Math.Floor(mapPoint.x / GridCell);
            var cz = (long)Math.Floor(mapPoint.y / GridCell);
            // One set for every lookup (the radio asks ten times a second): main thread only.
            var seen = nearestSeen;
            seen.Clear();
            for (var dx = -reach; dx <= reach; dx++)
            {
                for (var dz = -reach; dz <= reach; dz++)
                {
                    var key = ((cx + dx) << 32) ^ (uint)(int)(cz + dz);
                    if (!grid.TryGetValue(key, out var list))
                        continue;
                    foreach (var t in list)
                    {
                        if (!seen.Add(t))
                            continue;
                        var line = Polylines[t];
                        var offs = PolylineOffsets[t];
                        for (var i = 0; i + 1 < line.Length; i++)
                        {
                            var a = line[i];
                            var b = line[i + 1];
                            var ab = b - a;
                            var len2 = ab.sqrMagnitude;
                            var u = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(mapPoint - a, ab) / len2) : 0;
                            var d = (a + ab * u - mapPoint).magnitude;
                            if (d < distance)
                            {
                                distance = d;
                                best = t;
                                bestOffset = offs[i] + (offs[i + 1] - offs[i]) * u;
                            }
                        }
                    }
                }
            }
            if (best < 0 || distance > maxDistance)
                return false;
            position = new TrackPosition(best, Math.Max(0, Math.Min(Graph.Length(best), bestOffset)));
            return true;
        }

        /// <summary>A map point for a track position, from the polyline.</summary>
        public Vector2 MapPointAt(TrackPosition p)
        {
            var line = Polylines[p.Track];
            var offs = PolylineOffsets[p.Track];
            if (line.Length == 1)
                return line[0];
            for (var i = 0; i + 1 < line.Length; i++)
            {
                if (p.Offset <= offs[i + 1] || i + 2 == line.Length)
                {
                    var span = offs[i + 1] - offs[i];
                    var u = span > 1e-6f ? Mathf.Clamp01((float)((p.Offset - offs[i]) / span)) : 0;
                    return Vector2.Lerp(line[i], line[i + 1], u);
                }
            }
            return line[line.Length - 1];
        }

        /// <summary>A world position for a track position, from the track's own point set.</summary>
        public Vector3 WorldPointAt(TrackPosition p)
        {
            var set = Tracks[p.Track].GetKinkedPointSet();
            var i = set.GetPointIndexForSpan(p.Offset);
            i = Math.Max(0, Math.Min(set.points.Length - 1, i));
            return (Vector3)set.points[i].position + WorldMover.currentMove;
        }

        /// <summary>A name for the player: the yard track's ID, or "main line".</summary>
        public string Describe(int track)
        {
            var rail = Tracks[track];
            try
            {
                var logic = rail.LogicTrack();
                if (logic != null && logic.ID != null && !logic.ID.IsGeneric())
                    return logic.ID.FullDisplayID;
            }
            catch (Exception)
            {
                // Mapify maps and odd tracks may have no logic track.
            }
            return "main line";
        }
    }
}
