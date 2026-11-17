using System;
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
                if (Occupied(graph, occupancy, track, end, reach, ignoreOwner, standingOnly, 0))
                    return true;
            }
            return false;
        }

        private static bool Occupied(TrackGraph graph, OccupancyIndex occupancy, int track, TrackEnd from, double reach, int ignoreOwner, bool standingOnly, int depth)
        {
            var length = graph.Length(track);
            var span = Math.Min(length, reach);
            var lo = from == TrackEnd.Start ? 0 : length - span;
            if (standingOnly ? occupancy.IsStanding(track, lo, lo + span, ignoreOwner) : occupancy.IsOccupied(track, lo, lo + span, ignoreOwner))
                return true;
            if (reach <= length || depth >= 3)
                return false;
            foreach (var link in graph.Links(track, from.Other()))
            {
                if (Occupied(graph, occupancy, link.Track, link.End, reach - length, ignoreOwner, standingOnly, depth + 1))
                    return true;
            }
            return false;
        }
    }
}
