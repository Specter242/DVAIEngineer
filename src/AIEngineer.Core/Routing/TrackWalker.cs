using System;
using System.Collections.Generic;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Routing
{
    /// <summary>Why a walk along the switches as set came to an end.</summary>
    public enum WalkEnd
    {
        /// <summary>It ran the full distance asked.</summary>
        Distance,

        /// <summary>A buffer stop or the end of laid track.</summary>
        DeadEnd,

        /// <summary>A switch, entered by a branch, set for another branch.</summary>
        SwitchAgainst,
    }

    /// <summary>Follows the track ahead the way the switches lie, as a train would with no one changing them.</summary>
    public static class TrackWalker
    {
        public static RouteLeg Walk(TrackGraph graph, TrackPosition from, TrackDirection direction, double distance,
            Func<int, int> selectedBranch, ConsistEnd leader, out WalkEnd end)
        {
            var pieces = new List<RoutePiece>();
            var switches = new List<SwitchSetting>();
            var track = from.Track;
            var offset = from.Offset;
            var dir = direction;
            var travelled = 0.0;
            end = WalkEnd.Distance;
            // A loop of track could walk forever within the distance; a cap on pieces ends it.
            for (var guard = 0; guard < 4096; guard++)
            {
                var exit = dir.ExitEnd();
                var exitOffset = graph.OffsetOf(track, exit);
                var span = Math.Abs(exitOffset - offset);
                if (travelled + span >= distance)
                {
                    pieces.Add(new RoutePiece(track, offset, offset + (int)dir * (distance - travelled)));
                    travelled = distance;
                    break;
                }
                pieces.Add(new RoutePiece(track, offset, exitOffset));
                travelled += span;
                if (!TryNext(graph, track, exit, selectedBranch, out var link, out end))
                    break;
                if (link.NeedsJunction)
                    switches.Add(new SwitchSetting(link.Junction, link.Branch, travelled));
                track = link.Track;
                offset = graph.OffsetOf(track, link.End);
                dir = link.End.DirectionInto();
            }
            // Drop empty pieces but the first: the leading end right at a track's
            // end must still be found on the walk.
            for (var i = pieces.Count - 1; i >= 1; i--)
            {
                if (pieces[i].Length <= 1e-9)
                    pieces.RemoveAt(i);
            }
            if (pieces.Count == 0)
                pieces.Add(new RoutePiece(from.Track, from.Offset, from.Offset));
            return new RouteLeg(pieces, switches, leader, false);
        }

        /// <summary>
        /// The position a distance on along the switches as set, and the
        /// direction it is then moving; stops at a dead end or a switch set
        /// against it.
        /// </summary>
        public static TrackPosition Advance(TrackGraph graph, TrackPosition from, TrackDirection direction, double distance,
            Func<int, int> selectedBranch, out TrackDirection finalDirection)
        {
            var track = from.Track;
            var offset = from.Offset;
            var dir = direction;
            for (var guard = 0; guard < 256; guard++)
            {
                var exit = dir.ExitEnd();
                var exitOffset = graph.OffsetOf(track, exit);
                var span = Math.Abs(exitOffset - offset);
                if (distance <= span)
                {
                    finalDirection = dir;
                    return new TrackPosition(track, offset + (int)dir * distance);
                }
                distance -= span;
                if (!TryNext(graph, track, exit, selectedBranch, out var link, out _))
                {
                    finalDirection = dir;
                    return new TrackPosition(track, exitOffset);
                }
                track = link.Track;
                offset = graph.OffsetOf(track, link.End);
                dir = link.End.DirectionInto();
            }
            finalDirection = dir;
            return new TrackPosition(track, offset);
        }

        /// <summary>The link a train takes leaving this track end with the switches as set.</summary>
        public static bool TryNext(TrackGraph graph, int track, TrackEnd exit, Func<int, int> selectedBranch, out TrackLink link, out WalkEnd end)
        {
            var links = graph.Links(track, exit);
            link = default;
            end = WalkEnd.Distance;
            if (links.Length == 0)
            {
                end = WalkEnd.DeadEnd;
                return false;
            }
            foreach (var l in links)
            {
                if (!l.NeedsJunction)
                {
                    link = l;
                    return true;
                }
                if (selectedBranch(l.Junction) == l.Branch)
                {
                    link = l;
                    return true;
                }
            }
            // Every link needs a switch, none set our way: entering by a
            // branch, the switch lies for another branch.
            end = WalkEnd.SwitchAgainst;
            return false;
        }
    }
}
