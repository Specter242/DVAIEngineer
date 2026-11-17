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

        public int SelectedBranch(int junction) => Network.Junctions[junction].selectedBranch;

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
        }

        private bool scanWarned;

        private readonly List<(int Track, double From, double To)> scanSpans = new List<(int, double, double)>();

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
        /// coupler on its track, front coupler first, or, for a car across a
        /// joint, each bogie's track from its coupler to the joint. Nothing
        /// for a derailed car or one not on the network.
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
            var frontCoupler = Clamp(ft, fs + fSign * g.FrontOverhang);
            var rearCoupler = Clamp(rt, rs - rSign * g.RearOverhang);
            if (ft == rt)
            {
                into.Add((ft, frontCoupler, rearCoupler));
                return;
            }
            // Across a joint: each part runs from its coupler toward the other bogie, to its track's end.
            into.Add((ft, frontCoupler, fSign > 0 ? 0 : Graph.Length(ft)));
            into.Add((rt, rearCoupler, rSign > 0 ? Graph.Length(rt) : 0));
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
