using System;
using System.Collections.Generic;

namespace AIEngineer.Core.Routing
{
    /// <summary>A stretch of a track something stands on, and whose it is.</summary>
    public readonly struct OccupiedSpan
    {
        public readonly double From;
        public readonly double To;
        public readonly int Owner;

        public OccupiedSpan(double from, double to, int owner)
        {
            From = Math.Min(from, to);
            To = Math.Max(from, to);
            Owner = owner;
        }
    }

    /// <summary>
    /// Where the cars stand, by track: rebuilt a few times a second by the
    /// game side and read by every engineer, so finding the first car ahead
    /// costs only the tracks on the way.
    /// </summary>
    public sealed class OccupancyIndex
    {
        private readonly Dictionary<int, List<OccupiedSpan>> byTrack = new Dictionary<int, List<OccupiedSpan>>();
        private readonly Stack<List<OccupiedSpan>> spare = new Stack<List<OccupiedSpan>>();
        private readonly HashSet<int> moving = new HashSet<int>();

        private bool stampStale = true;
        private int stamp;

        /// <summary>
        /// A number that changes when the cars at rest do: where they
        /// stand (to a few meters), not whose they are. An order with no
        /// route looks again when it changes, and seldom otherwise.
        /// </summary>
        public int StandingStamp
        {
            get
            {
                if (!stampStale)
                    return stamp;
                var h = 0;
                foreach (var entry in byTrack)
                {
                    foreach (var s in entry.Value)
                    {
                        if (!moving.Contains(s.Owner))
                            h ^= unchecked((entry.Key * 73856093) ^ ((int)Math.Floor(s.From / 4) * 19349663) ^ 0x5bd1e995);
                    }
                }
                stamp = h;
                stampStale = false;
                return stamp;
            }
        }

        /// <summary>
        /// A copy that no later scan changes: for planning on another thread
        /// while the game goes on moving cars.
        /// </summary>
        public OccupancyIndex Copy()
        {
            var copy = new OccupancyIndex();
            foreach (var entry in byTrack)
                copy.byTrack[entry.Key] = new List<OccupiedSpan>(entry.Value);
            foreach (var owner in moving)
                copy.moving.Add(owner);
            return copy;
        }

        /// <summary>Whether any train but <paramref name="ignoreOwner"/>'s is on the move within a stretch of a track.</summary>
        public bool IsMovingWithin(int track, double from, double to, int ignoreOwner)
        {
            if (moving.Count == 0 || !byTrack.TryGetValue(track, out var list))
                return false;
            var lo = Math.Min(from, to);
            var hi = Math.Max(from, to);
            foreach (var s in list)
            {
                if (s.Owner != ignoreOwner && s.To >= lo && s.From <= hi && moving.Contains(s.Owner))
                    return true;
            }
            return false;
        }

        public void Clear()
        {
            stampStale = true;
            moving.Clear();
            foreach (var list in byTrack.Values)
            {
                list.Clear();
                spare.Push(list);
            }
            byTrack.Clear();
        }

        public void Add(int track, OccupiedSpan span)
        {
            if (!byTrack.TryGetValue(track, out var list))
            {
                list = spare.Count > 0 ? spare.Pop() : new List<OccupiedSpan>();
                byTrack[track] = list;
            }
            list.Add(span);
            stampStale = true;
        }

        public bool IsOccupied(int track, int ignoreOwner)
        {
            if (!byTrack.TryGetValue(track, out var list))
                return false;
            foreach (var s in list)
            {
                if (s.Owner != ignoreOwner)
                    return true;
            }
            return false;
        }

        /// <summary>Whether anything but <paramref name="ignoreOwner"/> stands within a stretch of a track.</summary>
        public bool IsOccupied(int track, double from, double to, int ignoreOwner)
        {
            if (!byTrack.TryGetValue(track, out var list))
                return false;
            var lo = Math.Min(from, to);
            var hi = Math.Max(from, to);
            foreach (var s in list)
            {
                if (s.Owner != ignoreOwner && s.To >= lo && s.From <= hi)
                    return true;
            }
            return false;
        }

 public void MarkMoving(int owner)
        {
            moving.Add(owner);
            stampStale = true;
        }

        /// <summary>Whether cars at rest, not <paramref name="ignoreOwner"/>'s, stand within a stretch of a track.</summary>
        public bool IsStanding(int track, double from, double to, int ignoreOwner)
        {
            if (!byTrack.TryGetValue(track, out var list))
                return false;
            var lo = Math.Min(from, to);
            var hi = Math.Max(from, to);
            foreach (var s in list)
            {
                if (s.Owner != ignoreOwner && s.To >= lo && s.From <= hi && !moving.Contains(s.Owner))
                    return true;
            }
            return false;
        }

        /// <summary>The stretches of a track where cars at rest stand, not <paramref name="ignoreOwner"/>'s, into <paramref name="into"/>.</summary>
        public void Standing(int track, int ignoreOwner, List<OccupiedSpan> into)
        {
            into.Clear();
            if (!byTrack.TryGetValue(track, out var list))
                return;
            foreach (var s in list)
            {
                if (s.Owner != ignoreOwner && !moving.Contains(s.Owner))
                    into.Add(s);
            }
        }

 public bool IsOccupiedBy(int track, double from, double to, int owner)
        {
            if (!byTrack.TryGetValue(track, out var list))
                return false;
            var lo = Math.Min(from, to);
            var hi = Math.Max(from, to);
            foreach (var s in list)
            {
                if (s.Owner == owner && s.To >= lo && s.From <= hi)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The distance along the leg to the nearest thing ahead of
        /// <paramref name="position"/> that isn't <paramref name="ignoreOwner"/>'s,
        /// looking no further than <paramref name="range"/>; infinity if clear.
        /// </summary>
        public double FirstAhead(RouteLeg leg, double position, int startPiece, double range, int ignoreOwner, out int owner)
        {
            owner = -1;
            var limit = position + range;
            var best = double.PositiveInfinity;
            for (var i = Math.Max(0, startPiece); i < leg.Pieces.Count; i++)
            {
                var start = leg.PieceStart(i);
                if (start > limit || start > best)
                    break;
                var piece = leg.Pieces[i];
                if (!byTrack.TryGetValue(piece.Track, out var list))
                    continue;
                var sign = (int)piece.Direction;
                foreach (var s in list)
                {
                    if (s.Owner == ignoreOwner)
                        continue;
                    // The span's near edge, as seen moving along the piece.
                    var near = sign > 0 ? s.From : s.To;
                    var far = sign > 0 ? s.To : s.From;
                    var nearAlong = (near - piece.From) * sign;
                    var farAlong = (far - piece.From) * sign;
                    if (farAlong < 0 || nearAlong > piece.Length)
                        continue;
                    var nearD = start + Math.Max(0, nearAlong);
                    var farD = start + Math.Min(piece.Length, farAlong);
                    // Wholly behind the leading end: not in the way.
                    if (farD < position)
                        continue;
                    // One the leading end already touches is in the way now.
                    var d = Math.Max(nearD, position);
                    if (d > limit)
                        continue;
                    if (d < best)
                    {
                        best = d;
                        owner = s.Owner;
                    }
                }
            }
            return best;
        }
    }
}
