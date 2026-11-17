using System.Collections.Generic;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Routing
{
    /// <summary>
    /// Whether a switch may be thrown: the game has none of this, and a
    /// switch thrown under a car splits the train. It knows which engineer
    /// holds each switch (for a few seconds after it last asked) and reads
    /// where the cars are from the <see cref="OccupancyIndex"/>. Engine-free,
    /// so a copy can answer for a plan made on another thread
    /// (<see cref="Copy"/>); the times are the caller's clock, s.
    /// </summary>
    public sealed class Interlocking
    {
        /// <summary>How close to a switch's points a car may stand before it can't be thrown, m.</summary>
        public const double FoulingDistance = 2.5;

        /// <summary>Seconds a switch stays held after its engineer stops asking for it.</summary>
        public const double HoldSeconds = 8;

        /// <summary>A moving train this close to a switch's points (m) may be about to run onto it: hands off.</summary>
        public const double ApproachDistance = 50;

        private readonly TrackGraph graph;
        private readonly OccupancyIndex occupancy;
        private readonly Dictionary<int, (int Owner, double Until)> holds = new Dictionary<int, (int, double)>();

        public Interlocking(TrackGraph graph, OccupancyIndex occupancy)
        {
            this.graph = graph;
            this.occupancy = occupancy;
        }

        /// <summary>The same holds, reading the cars from <paramref name="occupancy"/> (a copy of this one's).</summary>
        public Interlocking Copy(OccupancyIndex occupancy)
        {
            var copy = new Interlocking(graph, occupancy);
            foreach (var hold in holds)
                copy.holds[hold.Key] = hold.Value;
            return copy;
        }

 public bool CanThrow(int junction, int owner, double now)
        {
            if (holds.TryGetValue(junction, out var hold) && hold.Owner != owner && hold.Until > now)
                return false;
            return !IsFouled(junction, owner) && !Approached(junction, owner);
        }

        /// <summary>Whether another train's cars stand at rest across the switch's points.</summary>
        public bool Jammed(int junction, int owner)
        {
            var info = graph.Junction(junction);
            if (StandingNear(info.StemTrack, info.StemEnd, owner))
                return true;
            foreach (var (track, end) in info.Branches)
            {
                if (StandingNear(track, end, owner))
                    return true;
            }
            return false;
        }

        /// <summary>Whether anything stands across the switch's points, from any train but <paramref name="ignoreOwner"/>'s.</summary>
        public bool IsFouled(int junction, int ignoreOwner = int.MinValue)
        {
            var info = graph.Junction(junction);
            if (Near(info.StemTrack, info.StemEnd, ignoreOwner))
                return true;
            foreach (var (track, end) in info.Branches)
            {
                if (Near(track, end, ignoreOwner))
                    return true;
            }
            return false;
        }

 public void Hold(int junction, int owner, double now) => holds[junction] = (owner, now + HoldSeconds);

 public void Reserve(int junction, int owner, double now)
        {
            if (holds.TryGetValue(junction, out var hold) && hold.Owner != owner && hold.Until > now)
                return;
            holds[junction] = (owner, now + HoldSeconds);
        }

        /// <summary>Hands one train's holds on to another: the same train under the new id a coupling or a cut gave it.</summary>
        public void HandOn(int from, int to)
        {
            if (holds.Count == 0)
                return;
            List<int> held = null;
            foreach (var kv in holds)
            {
                if (kv.Value.Owner == from)
                    (held ?? (held = new List<int>())).Add(kv.Key);
            }
            if (held == null)
                return;
            foreach (var junction in held)
                holds[junction] = (to, holds[junction].Until);
        }

        /// <summary>Forgets the holds that have run out.</summary>
        public void Expire(double now)
        {
            if (holds.Count == 0)
                return;
            List<int> stale = null;
            foreach (var kv in holds)
            {
                if (kv.Value.Until <= now)
                    (stale ?? (stale = new List<int>())).Add(kv.Key);
            }
            if (stale == null)
                return;
            foreach (var k in stale)
                holds.Remove(k);
        }

        private bool StandingNear(int track, TrackEnd end, int ignoreOwner)
        {
            var at = graph.OffsetOf(track, end);
            return occupancy.IsStanding(track, at - FoulingDistance, at + FoulingDistance, ignoreOwner);
        }

        private bool Near(int track, TrackEnd end, int ignoreOwner)
        {
            var at = graph.OffsetOf(track, end);
            return occupancy.IsOccupied(track, at - FoulingDistance, at + FoulingDistance, ignoreOwner);
        }

        /// <summary>Whether another train is moving near the switch's points, on any of its tracks.</summary>
        private bool Approached(int junction, int owner)
        {
            var info = graph.Junction(junction);
            if (MovingNear(info.StemTrack, info.StemEnd, owner))
                return true;
            foreach (var (track, end) in info.Branches)
            {
                if (MovingNear(track, end, owner))
                    return true;
            }
            return false;
        }

        private bool MovingNear(int track, TrackEnd end, int owner)
        {
            var at = graph.OffsetOf(track, end);
            return occupancy.IsMovingWithin(track, at - ApproachDistance, at + ApproachDistance, owner);
        }
    }
}
