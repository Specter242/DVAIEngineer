using System;
using System.Collections.Generic;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>
    /// The engineers' view of the world: the network, the switches as they
    /// lie, where every car stands, and which engineer holds which switch.
    /// The game has no interlocking of its own (a switch thrown under a car
    /// splits it), so a switch with a car across its points is never thrown.
    /// </summary>
    internal sealed class World : IEngineerWorld
    {
        /// <summary>A consist faster than this (m/s) counts as moving.</summary>
        private const float MovingSpeed = 0.3f;

        private readonly OccupancyIndex occupancy = new OccupancyIndex();
        // Who holds each switch, and whether it may be thrown: the rules are the Core's, so a
        // frozen copy answers the same for a plan made off the game's thread.
        private readonly Interlocking interlocking;
        private readonly Dictionary<TrainCar, CarGeometry> geometry = new Dictionary<TrainCar, CarGeometry>();
        private readonly Func<int, int> selectedBranch;

        public World(Network network)
        {
            Network = network;
            selectedBranch = SelectedBranch;
            interlocking = new Interlocking(network.Graph, occupancy);
        }

        public Network Network { get; }

        public TrackGraph Graph => Network.Graph;

        public Router Router => Network.Router;

        public OccupancyIndex Occupancy => occupancy;

        public Func<int, int> SelectedBranchFunc => selectedBranch;

        public int SelectedBranch(int junction)
        {
            var j = Network.Junctions[junction];
            return j != null ? j.selectedBranch : 0;
        }

        public bool CanThrow(int junction, int owner) => interlocking.CanThrow(junction, owner, Time.time);

        public bool Jammed(int junction, int owner) => interlocking.Jammed(junction, owner);

        public bool PointsOccupied(int junction) => interlocking.IsFouled(junction);

        public void Throw(int junction, int branch, int owner)
        {
            var j = Network.Junctions[junction];
            if (j == null)
                return;
            j.Switch(Junction.SwitchMode.REGULAR, (byte)branch);
            interlocking.Hold(junction, owner, Time.time);
        }

        /// <summary>Hands one train's switch holds on to another: the same train under the new id a coupling or a cut gave it.</summary>
        public void HandOn(int from, int to) => interlocking.HandOn(from, to);

        public void Reserve(int junction, int owner) => interlocking.Reserve(junction, owner, Time.time);

        /// <summary>Whether anything stands across the switch's points, from any train but <paramref name="ignoreOwner"/>'s.</summary>
        public bool IsFouled(int junction, int ignoreOwner = int.MinValue) => interlocking.IsFouled(junction, ignoreOwner);

        /// <summary>
        /// The world as it stands this moment, for a plan worked out on
        /// another thread: the cars as last scanned, how every switch lies,
        /// who holds which. A few hundred values copied; nothing in it is the
        /// game's.
        /// </summary>
        public FrozenWorld Freeze() => new FrozenWorld(Graph, occupancy, interlocking, LieOf, Time.time);

        private int LieOf(int junction)
        {
            var j = Network.Junctions[junction];
            return j != null ? j.selectedBranch : 0;
        }

        /// <summary>
        /// Rebuilds where every car stands: per car, the stretch from coupler
        /// to coupler on its track, or, for a car across a joint, each
        /// bogie's track from the coupler to the joint.
        /// </summary>
        public void ScanCars()
        {
            occupancy.Clear();
            var spawner = CarSpawner.Instance;
            if (spawner == null)
                return;
            foreach (var set in Trainset.allSets)
            {
                if (set?.firstCar != null && Mathf.Abs(set.firstCar.GetForwardSpeed()) > MovingSpeed)
                    occupancy.MarkMoving(set.id);
            }
            foreach (var car in spawner.AllCars)
            {
                try
                {
                    ScanCar(car);
                }
                catch (Exception e)
                {
                    // One car that can't be read (another mod's, half spawned) mustn't hide every car after it.
                    if (!scanWarned)
                        Main.Warn($"a car couldn't be placed on the network ({(car != null ? car.ID : "?")}: {e.GetBaseException().Message}); the engineers don't see it");
                    scanWarned = true;
                }
            }
            interlocking.Expire(Time.time);
            if (++scanCount >= 30 || geometry.Count > spawner.AllCars.Count + 16)
                PruneGeometry(spawner.AllCars);
        }

        private bool scanWarned;
        private int scanCount;

        private readonly HashSet<TrainCar> activeCars = new HashSet<TrainCar>();
        private readonly List<TrainCar> deadGeometry = new List<TrainCar>();
        private readonly List<(int Track, double From, double To)> scanSpans = new List<(int, double, double)>();
        private readonly List<(int Track, double From, double To)> bridgeSpans = new List<(int, double, double)>();
        private readonly List<BridgeNode> bridgeNodes = new List<BridgeNode>();

        private struct BridgeNode
        {
            public int Track;
            public TrackEnd Entry;
            public double Dist;
            public int Parent;
            public bool Expanded;
        }

        private void PruneGeometry(List<TrainCar> active)
        {
            scanCount = 0;
            activeCars.Clear();
            if (active != null)
            {
                for (var i = 0; i < active.Count; i++)
                {
                    var c = active[i];
                    if (c != null)
                        activeCars.Add(c);
                }
            }
            deadGeometry.Clear();
            foreach (var key in geometry.Keys)
            {
                if (key == null || !activeCars.Contains(key))
                    deadGeometry.Add(key);
            }
            for (var i = 0; i < deadGeometry.Count; i++)
                geometry.Remove(deadGeometry[i]);
            deadGeometry.Clear();
            activeCars.Clear();
        }

        private void ScanCar(TrainCar car)
        {
            if (car == null || car.Bogies == null || car.Bogies.Length < 2)
                return;
            var owner = car.trainset?.id ?? car.GetInstanceID();
            if (car.derailed)
            {
                AddDerailed(car, owner);
                return;
            }
            scanSpans.Clear();
            SpansOf(car, scanSpans);
            foreach (var (track, from, to) in scanSpans)
                occupancy.Add(track, new OccupiedSpan(from, to, owner));
        }

        /// <summary>
        /// Where a car on the rails stands: the stretch from coupler to
        /// coupler on its track (propagating overhang onto adjacent tracks),
        /// front bogie's track first, and for a car across one or more joints,
        /// each bogie's track plus any intermediate track(s) bridged between
        /// them. Nothing for a derailed car or one not on the network.
        /// </summary>
        public void SpansOf(TrainCar car, List<(int Track, double From, double To)> into)
        {
            if (car == null || car.derailed || car.Bogies == null || car.Bogies.Length < 2)
                return;
            var front = car.FrontBogie;
            var rear = car.RearBogie;
            if (front.track == null || rear.track == null || front.traveller == null || rear.traveller == null)
                return;
            if (!Network.TryGetTrack(front.track, out var ft) || !Network.TryGetTrack(rear.track, out var rt))
                return;
            var g = Geometry(car);
            var fs = front.traveller.Span;
            var rs = rear.traveller.Span;
            var fSign = front.TrackDirectionSign >= 0 ? 1 : -1;
            var rSign = rear.TrackDirectionSign >= 0 ? 1 : -1;
            var ftLen = Graph.Length(ft);
            var rtLen = Graph.Length(rt);

            var rawFront = fs + fSign * g.FrontOverhang;
            var rawRear = rs - rSign * g.RearOverhang;
            var frontCoupler = Clamp(ft, rawFront);
            var rearCoupler = Clamp(rt, rawRear);

            var frontExit = fSign > 0 ? TrackEnd.End : TrackEnd.Start;
            var frontExcess = fSign > 0 ? rawFront - ftLen : -rawFront;
            var rearExit = rSign > 0 ? TrackEnd.Start : TrackEnd.End;
            var rearExcess = rSign > 0 ? -rawRear : rawRear - rtLen;

            if (ft == rt)
            {
                into.Add((ft, frontCoupler, rearCoupler));
                if (frontExcess > 1e-4)
                    AddOverhang(ft, frontExit, frontExcess, into);
                if (rearExcess > 1e-4)
                    AddOverhang(rt, rearExit, rearExcess, into);
                return;
            }
            // Across one or more joints: each bogie's track from its coupler toward the other bogie,
            // plus any intermediate short track(s) between them and any excess coupler overhang.
            var fBridgeExit = fSign > 0 ? TrackEnd.Start : TrackEnd.End;
            var rBridgeExit = rSign > 0 ? TrackEnd.End : TrackEnd.Start;
            into.Add((ft, frontCoupler, Graph.OffsetOf(ft, fBridgeExit)));
            AddBridge(ft, fBridgeExit, rt, rBridgeExit, Math.Max(2.0, car.InterCouplerDistance + 2.0), into);
            into.Add((rt, rearCoupler, Graph.OffsetOf(rt, rBridgeExit)));
            if (frontExcess > 1e-4)
                AddOverhang(ft, frontExit, frontExcess, into);
            if (rearExcess > 1e-4)
                AddOverhang(rt, rearExit, rearExcess, into);
        }

        private bool TryFollowLink(int track, TrackEnd exit, out TrackLink link)
        {
            var links = Graph.Links(track, exit);
            link = default;
            if (links.Length == 0)
                return false;
            if (links.Length == 1)
            {
                link = links[0];
                return true;
            }
            for (var i = 0; i < links.Length; i++)
            {
                var l = links[i];
                if (!l.NeedsJunction || SelectedBranchFunc(l.Junction) == l.Branch)
                {
                    link = l;
                    return true;
                }
            }
            return false;
        }

        private void AddOverhang(int track, TrackEnd exit, double remaining, List<(int Track, double From, double To)> into)
        {
            for (var guard = 0; guard < 16 && remaining > 1e-4; guard++)
            {
                if (!TrackWalker.TryNext(Graph, track, exit, SelectedBranchFunc, out var link, out _))
                    break;
                var next = link.Track;
                var entryOffset = Graph.OffsetOf(next, link.End);
                var dir = link.End.DirectionInto();
                var len = Graph.Length(next);
                if (remaining <= len)
                {
                    into.Add((next, entryOffset, entryOffset + (int)dir * remaining));
                    break;
                }
                var nextExit = dir.ExitEnd();
                into.Add((next, entryOffset, Graph.OffsetOf(next, nextExit)));
                remaining -= len;
                track = next;
                exit = nextExit;
            }
        }

        private void AddBridge(int ft, TrackEnd fExit, int rt, TrackEnd rEnd, double maxDist, List<(int Track, double From, double To)> into)
        {
            var firstLinks = Graph.Links(ft, fExit);
            for (var i = 0; i < firstLinks.Length; i++)
            {
                if (firstLinks[i].Track == rt && firstLinks[i].End == rEnd)
                    return;
            }
            // Fast path: follow single links / selected switch branches from ft toward rt.
            bridgeSpans.Clear();
            var curTrack = ft;
            var curExit = fExit;
            var walked = 0.0;
            for (var step = 0; step < 16; step++)
            {
                if (!TryFollowLink(curTrack, curExit, out var link))
                    break;
                if (link.Track == rt && link.End == rEnd)
                {
                    for (var i = 0; i < bridgeSpans.Count; i++)
                        into.Add(bridgeSpans[i]);
                    return;
                }
                if (link.Track == ft || link.Track == rt)
                    break;
                var len = Graph.Length(link.Track);
                if (walked + len > maxDist)
                    break;
                var nextExit = link.End.Other();
                bridgeSpans.Add((link.Track, Graph.OffsetOf(link.Track, link.End), Graph.OffsetOf(link.Track, nextExit)));
                walked += len;
                curTrack = link.Track;
                curExit = nextExit;
            }
            // Fallback: shortest connecting path within maxDist regardless of switch lie.
            bridgeNodes.Clear();
            for (var i = 0; i < firstLinks.Length; i++)
            {
                var l = firstLinks[i];
                if (l.Track == ft || l.Track == rt)
                    continue;
                var len = Graph.Length(l.Track);
                if (len > maxDist)
                    continue;
                bridgeNodes.Add(new BridgeNode { Track = l.Track, Entry = l.End, Dist = len, Parent = -1 });
            }
            while (true)
            {
                var bestIdx = -1;
                var bestDist = double.PositiveInfinity;
                for (var i = 0; i < bridgeNodes.Count; i++)
                {
                    if (!bridgeNodes[i].Expanded && bridgeNodes[i].Dist < bestDist)
                    {
                        bestDist = bridgeNodes[i].Dist;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0)
                    return;
                var node = bridgeNodes[bestIdx];
                node.Expanded = true;
                bridgeNodes[bestIdx] = node;
                var nextLinks = Graph.Links(node.Track, node.Entry.Other());
                for (var i = 0; i < nextLinks.Length; i++)
                {
                    var l = nextLinks[i];
                    if (l.Track == rt && l.End == rEnd)
                    {
                        bridgeSpans.Clear();
                        for (var cur = bestIdx; cur >= 0; cur = bridgeNodes[cur].Parent)
                        {
                            var bn = bridgeNodes[cur];
                            bridgeSpans.Add((bn.Track, Graph.OffsetOf(bn.Track, bn.Entry), Graph.OffsetOf(bn.Track, bn.Entry.Other())));
                        }
                        for (var k = bridgeSpans.Count - 1; k >= 0; k--)
                            into.Add(bridgeSpans[k]);
                        return;
                    }
                    if (l.Track == ft || l.Track == rt)
                        continue;
                    var nextDist = node.Dist + Graph.Length(l.Track);
                    if (nextDist > maxDist)
                        continue;
                    var existing = -1;
                    for (var j = 0; j < bridgeNodes.Count; j++)
                    {
                        if (bridgeNodes[j].Track == l.Track && bridgeNodes[j].Entry == l.End)
                        {
                            existing = j;
                            break;
                        }
                    }
                    if (existing >= 0)
                    {
                        if (!bridgeNodes[existing].Expanded && nextDist < bridgeNodes[existing].Dist)
                        {
                            var updated = bridgeNodes[existing];
                            updated.Dist = nextDist;
                            updated.Parent = bestIdx;
                            bridgeNodes[existing] = updated;
                        }
                    }
                    else if (bridgeNodes.Count < 64)
                    {
                        bridgeNodes.Add(new BridgeNode { Track = l.Track, Entry = l.End, Dist = nextDist, Parent = bestIdx });
                    }
                }
            }
        }

        private double Clamp(int track, double offset) => Math.Max(0, Math.Min(Graph.Length(track), offset));

        /// <summary>A derailed car blocks the track it lies nearest, its whole length around where it lies.</summary>
        private void AddDerailed(TrainCar car, int owner)
        {
            var abs = car.transform.position - WorldMover.currentMove;
            if (!Network.TryFindNearest(new Vector2(abs.x, abs.z), 12f, out var p, out _))
                return;
            var half = car.InterCouplerDistance / 2 + 1;
            occupancy.Add(p.Track, new OccupiedSpan(Clamp(p.Track, p.Offset - half), Clamp(p.Track, p.Offset + half), owner));
        }

        /// <summary>A consist's outer coupler on the network, and the direction out of the consist there.</summary>
        public bool TryPlace(Coupler coupler, out TrackPosition position, out TrackDirection outward)
        {
            position = default;
            outward = TrackDirection.Forward;
            var car = coupler.train;
            var bogie = coupler.isFrontCoupler ? car.FrontBogie : car.RearBogie;
            if (bogie == null || bogie.track == null || bogie.traveller == null || !Network.TryGetTrack(bogie.track, out var track))
                return false;
            var sign = bogie.TrackDirectionSign >= 0 ? TrackDirection.Forward : TrackDirection.Backward;
            var dir = coupler.isFrontCoupler ? sign : sign.Reverse();
            var geometry = Geometry(car);
            var overhang = coupler.isFrontCoupler ? geometry.FrontOverhang : geometry.RearOverhang;
            var at = new TrackPosition(track, Math.Max(0, Math.Min(Graph.Length(track), bogie.traveller.Span)));
            position = TrackWalker.Advance(Graph, at, dir, overhang, SelectedBranchFunc, out outward);
            return true;
        }

        public void Forget(TrainCar car) => geometry.Remove(car);

        /// <summary>A car's coupler overhangs beyond its outer bogies, measured once.</summary>
        public CarGeometry Geometry(TrainCar car)
        {
            if (geometry.TryGetValue(car, out var g))
                return g;
            var t = car.transform;
            var frontZ = t.InverseTransformPoint(car.FrontBogie.transform.position).z;
            var rearZ = t.InverseTransformPoint(car.RearBogie.transform.position).z;
            g = new CarGeometry(
                Math.Max(0f, car.PivotToFrontCouplerZOffset - frontZ),
                Math.Max(0f, rearZ - car.PivotToRearCouplerZOffset));
            geometry[car] = g;
            return g;
        }
    }

    internal readonly struct CarGeometry
    {
        public readonly float FrontOverhang;
        public readonly float RearOverhang;

        public CarGeometry(float front, float rear)
        {
            FrontOverhang = front;
            RearOverhang = rear;
        }
    }
}
