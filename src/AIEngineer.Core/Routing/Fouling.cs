using System;
using System.Collections.Generic;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Routing
{
    /// <summary>
    /// Cars fouling a switch: standing on one of its other branches near
    /// enough to the points that a train passing on this branch would strike
    /// them. Near the frog a switch's branches run within a car's width of
    /// each other (in game, 2026-11-04, a DE2 side-swiped cars standing on
    /// the other branch and derailed). Switches in Derail Valley are short
    /// turnout tracks (about 24 m), so the reach runs on past them.
    /// </summary>
    public static class Fouling
    {
        /// <summary>How far from the points a car on another branch fouls this one, m.</summary>
        public const double Reach = 35;

        /// <summary>
        /// Whether anything but <paramref name="ignoreOwner"/>'s stands
        /// within <paramref name="reach"/> of the points on a branch other
        /// than <paramref name="branch"/>; with <paramref name="standingOnly"/>,
        /// only cars at rest (a route is planned round those; a train on the
        /// move is waited for).
        /// </summary>
        public static bool OtherBranchesFouled(TrackGraph graph, OccupancyIndex occupancy, int junction, int branch, int ignoreOwner,
            double reach = Reach, bool standingOnly = false)
        {
            var info = graph.Junction(junction);
            for (var b = 0; b < info.Branches.Length; b++)
            {
                if (b == branch)
                    continue;
                var (track, end) = info.Branches[b];
                if (Occupied(graph, occupancy, track, end, reach, ignoreOwner, standingOnly))
                    return true;
            }
            return false;
        }

        private static bool OccupiedOnEnd(TrackGraph graph, OccupancyIndex occupancy, int track, TrackEnd from, double reach, int ignoreOwner, bool standingOnly)
        {
            var length = graph.Length(track);
            var span = Math.Min(length, reach);
            var lo = from == TrackEnd.Start ? 0 : length - span;
            return standingOnly
                ? occupancy.IsStanding(track, lo, lo + span, ignoreOwner)
                : occupancy.IsOccupied(track, lo, lo + span, ignoreOwner);
        }

        [ThreadStatic]
        private static Dictionary<int, double> visitedBest;

        [ThreadStatic]
        private static Queue<(int Track, TrackEnd End, double Remaining)> frontier;

        private static bool Occupied(TrackGraph graph, OccupancyIndex occupancy, int startTrack, TrackEnd startEnd, double reach, int ignoreOwner, bool standingOnly)
        {
            if (reach <= 0)
                return false;
            if (OccupiedOnEnd(graph, occupancy, startTrack, startEnd, reach, ignoreOwner, standingOnly))
                return true;
            var firstRemaining = reach - graph.Length(startTrack);
            if (firstRemaining <= 0)
                return false;

            var best = visitedBest ?? (visitedBest = new Dictionary<int, double>());
            var queue = frontier ?? (frontier = new Queue<(int, TrackEnd, double)>());
            best.Clear();
            queue.Clear();
            best[startTrack * 2 + (int)startEnd] = reach;

            var startLinks = graph.Links(startTrack, startEnd.Other());
            for (var i = 0; i < startLinks.Length; i++)
            {
                var link = startLinks[i];
                Enqueue(best, queue, link.Track, link.End, firstRemaining);
            }

            while (queue.Count > 0)
            {
                var (track, entered, remaining) = queue.Dequeue();
                if (best[track * 2 + (int)entered] > remaining)
                    continue;
                if (OccupiedOnEnd(graph, occupancy, track, entered, remaining, ignoreOwner, standingOnly))
                {
                    best.Clear();
                    queue.Clear();
                    return true;
                }
                var nextRemaining = remaining - graph.Length(track);
                if (nextRemaining <= 0)
                    continue;
                var links = graph.Links(track, entered.Other());
                for (var i = 0; i < links.Length; i++)
                {
                    var link = links[i];
                    Enqueue(best, queue, link.Track, link.End, nextRemaining);
                }
            }
            best.Clear();
            return false;
        }

        private static void Enqueue(Dictionary<int, double> best, Queue<(int Track, TrackEnd End, double Remaining)> queue, int track, TrackEnd end, double remaining)
        {
            if (remaining <= 0)
                return;
            var key = track * 2 + (int)end;
            if (best.TryGetValue(key, out var seen) && seen >= remaining)
                return;
            best[key] = remaining;
            queue.Enqueue((track, end, remaining));
        }
    }
}
