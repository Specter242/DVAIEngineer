using System;
using System.Collections.Generic;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;

namespace AIEngineer.Game
{
    /// <summary>One move of a job's current step: its cars, and the track they go to.</summary>
    internal sealed class JobLeg
    {
        public readonly List<TrainCar> Cars = new List<TrainCar>();

        /// <summary>The destination's network track; -1 when it isn't on the network.</summary>
        public int ToTrack = -1;

        public string ToName;
        public string FromName;

        /// <summary>Every car already stands on the destination track.</summary>
        public bool There;

        /// <summary>The job's last move: done only with its cars coupled together and a hand brake on.</summary>
        public bool Last;
    }

    /// <summary>A job (a waybill) as the dispatcher shows it and the engineer works it: the moves of its current step.</summary>
    internal sealed class JobView
    {
        public Job Job;
        public string Id;
        public string Kind;
        public readonly List<JobLeg> Legs = new List<JobLeg>();

        /// <summary>Why the engineer can't work it now (the warehouse machine's turn, nothing left to move); null if it can.</summary>
        public string Blocked;

        /// <summary>When its step is the warehouse machine's: the track its cars are loaded or unloaded on ("FF-B1L"); else null.</summary>
        public string Warehouse;

        /// <summary>Every move of its step is made, and the game hasn't yet counted it done.</summary>
        public bool Settling;

        public int Cars
        {
            get
            {
                var n = 0;
                foreach (var leg in Legs)
                    n += leg.Cars.Count;
                return n;
            }
        }

        /// <summary>"4 cars, MF-B3S to HB-F2L", or several moves in brief.</summary>
        public string Summary
        {
            get
            {
                if (Legs.Count == 0)
                    return "";
                var to = new HashSet<string>();
                var from = new HashSet<string>();
                foreach (var leg in Legs)
                {
                    to.Add(leg.ToName);
                    from.Add(leg.FromName);
                }
                var cars = Cars;
                return $"{cars} car{(cars == 1 ? "" : "s")}, {string.Join("/", from)} to {string.Join("/", to)}";
            }
        }
    }

    /// <summary>
    /// The player's jobs, read from the game: the ones taken (their
    /// booklets), each down to the transport moves of the step it is on.
    /// A job is a tree of tasks; a sequence works its first task not done,
    /// a parallel all of them, and a transport task names its cars and
    /// their start and destination tracks.
    /// </summary>
    internal static class Jobs
    {
        /// <summary>The jobs the player has taken, by ID.</summary>
        public static List<Job> Taken()
        {
            var list = new List<Job>();
            var manager = SingletonBehaviour<JobsManager>.Instance;
            if (manager == null)
                return list;
            foreach (var job in manager.currentJobs)
            {
                if (job != null && job.State == JobState.InProgress)
                    list.Add(job);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.ID, b.ID));
            return list;
        }

        public static JobView Read(World world, Job job)
        {
            var view = new JobView { Job = job, Id = job.ID, Kind = Kind(job.jobType) };
            var leaves = new List<Task>();
            foreach (var task in job.tasks)
                Current(task, leaves);
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            foreach (var task in leaves)
            {
                if (task.InstanceTaskType == TaskType.Warehouse)
                {
                    var machine = task.GetTaskData();
                    var loading = machine.warehouseTaskType == WarehouseTaskType.Loading;
                    view.Warehouse = Name(machine.destinationTrack);
                    view.Blocked = $"its cars wait to be {(loading ? "loaded" : "unloaded")} at the warehouse machine on {view.Warehouse}";
                    continue;
                }
                if (task.InstanceTaskType != TaskType.Transport)
                {
                    // Another mod's task (a passenger job's stop at a platform), or one the game
                    // adds later: not a move, and not done either. Read as nothing left to do, the
                    // job would be reported delivered while it still waits.
                    view.Blocked = "its next task isn't one the engineer can do (not a move of cars)";
                    continue;
                }
                var data = task.GetTaskData();
                var leg = new JobLeg
                {
                    FromName = Name(data.startTrack),
                    ToName = Name(data.destinationTrack),
                    Last = task.IsLastTask,
                };
                var rail = data.destinationTrack?.RailTrack();
                if (rail != null && world.Network.TryGetTrack(rail, out var index))
                    leg.ToTrack = index;
                var there = true;
                foreach (var car in data.cars ?? new List<Car>())
                {
                    if (car == null || registry == null || !registry.logicCarToTrainCar.TryGetValue(car, out var trainCar) || trainCar == null)
                    {
                        view.Blocked = "a car of it isn't on the map";
                        continue;
                    }
                    leg.Cars.Add(trainCar);
                    if (car.CurrentTrack != data.destinationTrack)
                        there = false;
                }
                leg.There = there && leg.Cars.Count > 0;
                if (leg.ToTrack < 0)
                    view.Blocked = $"its destination {leg.ToName} isn't on the network";
                view.Legs.Add(leg);
            }
            if (view.Legs.Count == 0 && view.Blocked == null)
                view.Blocked = "nothing left to move: turn in its booklet";
            else if (view.Blocked == null && view.Legs.TrueForAll(l => l.There))
            {
                view.Blocked = "its cars are on their track already";
                view.Settling = true;
            }
            return view;
        }

        /// <summary>The tasks being worked now: a sequence's first not done, every one of a parallel's, the leaves themselves.</summary>
        private static void Current(Task task, List<Task> into)
        {
            if (task == null || task.state == TaskState.Done)
                return;
            switch (task.InstanceTaskType)
            {
                case TaskType.Sequential:
                    foreach (var t in task.GetTaskData().nestedTasks)
                    {
                        if (t != null && t.state != TaskState.Done)
                        {
                            Current(t, into);
                            return;
                        }
                    }
                    return;
                case TaskType.Parallel:
                    foreach (var t in task.GetTaskData().nestedTasks)
                        Current(t, into);
                    return;
                default:
                    into.Add(task);
                    return;
            }
        }

        private static string Name(Track track)
        {
            try
            {
                return track?.ID != null ? track.ID.FullDisplayID : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Kind(JobType type)
        {
            switch (type)
            {
                case JobType.Transport:
                    return "Freight haul";
                case JobType.EmptyHaul:
                    return "Logistical haul";
                case JobType.ShuntingLoad:
                    return "Shunting load";
                case JobType.ShuntingUnload:
                    return "Shunting unload";
                case JobType.ComplexTransport:
                    return "Freight haul";
                default:
                    return type.ToString();
            }
        }
    }
}
