using System;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Driving
{
    /// <summary>
    /// The world as it stood at one moment: where the cars are, how each
    /// switch lies and who holds it. Nothing the game does later changes it,
    /// and it has a router of its own, so a plan can be worked out on
    /// another thread while the game runs on. It answers questions only: a
    /// plan throws no switch.
    /// </summary>
    public sealed class FrozenWorld : IEngineerWorld
    {
        private readonly int[] selected;
        private readonly Interlocking interlocking;
        private readonly double now;

        /// <summary>
        /// Copies what a plan reads. <paramref name="selectedBranch"/> is
        /// asked once for every switch; <paramref name="now"/> is the
        /// <paramref name="interlocking"/>'s clock at this moment.
        /// </summary>
        public FrozenWorld(TrackGraph graph, OccupancyIndex occupancy, Interlocking interlocking, Func<int, int> selectedBranch, double now)
        {
            Graph = graph;
            Router = new Router(graph);
            Occupancy = occupancy.Copy();
            this.interlocking = interlocking.Copy(Occupancy);
            this.now = now;
            selected = new int[graph.JunctionCount];
            for (var j = 0; j < selected.Length; j++)
                selected[j] = selectedBranch(j);
        }

        public TrackGraph Graph { get; }

        public Router Router { get; }

        public OccupancyIndex Occupancy { get; }

        public int SelectedBranch(int junction) => selected[junction];

        public bool CanThrow(int junction, int owner) => interlocking.CanThrow(junction, owner, now);

        public bool Jammed(int junction, int owner) => interlocking.Jammed(junction, owner);

        public bool PointsOccupied(int junction) => interlocking.IsFouled(junction);

        public void Throw(int junction, int branch, int owner) =>
            throw new InvalidOperationException("A frozen world is for planning: it throws no switch.");

        public void Reserve(int junction, int owner) =>
            throw new InvalidOperationException("A frozen world is for planning: it holds no switch.");
    }
}
