using System;
using System.Collections.Generic;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Routing
{
    /// <summary>
    /// Shortest distances along the track from one point, ignoring how the
    /// switches lie and which way the train faces: a cheap estimate for
    /// ordering work (which cut to fetch first), not a route. One search
    /// covers the whole network, a millisecond or so for the stock map.
    /// </summary>
    public sealed class GraphDistances
    {
        private readonly TrackGraph graph;
        private readonly TrackPosition source;
        private readonly double[] toEnd;

        private GraphDistances(TrackGraph graph, TrackPosition source, double[] toEnd)
        {
            this.graph = graph;
            this.source = source;
            this.toEnd = toEnd;
        }

        /// <summary>Searches the network from <paramref name="from"/>.</summary>
        public static GraphDistances From(TrackGraph graph, TrackPosition from)
        {
            var count = graph.TrackCount * 2;
            var dist = new double[count];
            for (var i = 0; i < count; i++)
                dist[i] = double.PositiveInfinity;
            var heap = new MinHeap();
            void Reach(int node, double d)
            {
                if (d < dist[node])
                {
                    dist[node] = d;
                    heap.Push(node, d);
                }
            }
            var length = graph.Length(from.Track);
            Reach(from.Track * 2 + (int)TrackEnd.Start, Math.Max(0, from.Offset));
            Reach(from.Track * 2 + (int)TrackEnd.End, Math.Max(0, length - from.Offset));
            while (heap.TryPop(out var node, out var d))
            {
                if (d > dist[node])
                    continue;
                var track = node / 2;
                var end = (TrackEnd)(node % 2);
                // Along the track to its other end.
                Reach(track * 2 + (int)end.Other(), d + graph.Length(track));
                // Across every link from this end, whichever way its switch lies.
                foreach (var link in graph.Links(track, end))
                    Reach(link.Track * 2 + (int)link.End, d);
            }
            return new GraphDistances(graph, from, dist);
        }

        /// <summary>The distance to a point; infinity when no track joins them.</summary>
        public double To(TrackPosition to)
        {
            var viaStart = toEnd[to.Track * 2 + (int)TrackEnd.Start] + to.Offset;
            var viaEnd = toEnd[to.Track * 2 + (int)TrackEnd.End] + graph.Length(to.Track) - to.Offset;
            var best = Math.Min(viaStart, viaEnd);
            if (to.Track == source.Track)
                best = Math.Min(best, Math.Abs(to.Offset - source.Offset));
            return best;
        }

        /// <summary>A binary heap of (node, distance), smallest distance first.</summary>
        private sealed class MinHeap
        {
            private readonly List<(int Node, double Key)> items = new List<(int, double)>();

            public void Push(int node, double key)
            {
                items.Add((node, key));
                var i = items.Count - 1;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (items[parent].Key <= items[i].Key)
                        break;
                    (items[parent], items[i]) = (items[i], items[parent]);
                    i = parent;
                }
            }

            public bool TryPop(out int node, out double key)
            {
                if (items.Count == 0)
                {
                    node = -1;
                    key = 0;
                    return false;
                }
                (node, key) = items[0];
                var last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (items.Count > 0)
                {
                    items[0] = last;
                    var i = 0;
                    while (true)
                    {
                        var l = i * 2 + 1;
                        var r = l + 1;
                        var smallest = i;
                        if (l < items.Count && items[l].Key < items[smallest].Key)
                            smallest = l;
                        if (r < items.Count && items[r].Key < items[smallest].Key)
                            smallest = r;
                        if (smallest == i)
                            break;
                        (items[smallest], items[i]) = (items[i], items[smallest]);
                        i = smallest;
                    }
                }
                return true;
            }
        }
    }
}
