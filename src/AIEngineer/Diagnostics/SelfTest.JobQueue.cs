using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Game;
using AIEngineer.UI.Tablet;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using UnityEngine;

namespace AIEngineer.Diagnostics
{
    internal static partial class SelfTest
    {
        private static IEnumerator QueuedJob(Session s, World world, StationController station, NamedTrack from1, NamedTrack to1, NamedTrack from2, NamedTrack to2)
        {
            const string kept = "jobs D: a job added to the queue keeps the one being worked";
            const string worked = "jobs D: both worked, the first one first";
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var car1 = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat }, new List<bool> { false }, world.Network.Tracks[from1.Track], true, true);
            var car2 = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat }, new List<bool> { false }, world.Network.Tracks[from2.Track], true, true);
            if (car1 == null || car1.Count < 1 || car2 == null || car2.Count < 1)
            {
                Fail(kept, $"couldn't spawn the flatcars on {from1.Id} and {from2.Id}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            world.ScanCars();
            Job job1, job2;
            try
            {
                job1 = MakeHaul(world, station, car1, from1, to1);
                job2 = MakeHaul(world, station, car2, from2, to2);
            }
            catch (System.Exception e)
            {
                Fail(kept, $"couldn't make the jobs: {e.GetType().Name}: {e.Message}");
                yield break;
            }
            Line($"jobs D: {job1.ID} takes {car1[0].ID} from {from1.Id} to {to1.Id}; {job2.ID}, added to the queue, {car2[0].ID} from {from2.Id} to {to2.Id}");
            var chosen = TabletUi.Chosen;
            TabletUi.Chosen = s.Loco;
            TabletMap.WorkJobs(world, new List<Job> { job1 }, queue: false);
            var first = s.Jobs;
            yield return new WaitForSeconds(20);
            TabletMap.WorkJobs(world, new List<Job> { job2 }, queue: true);
            TabletUi.Chosen = chosen;
            var same = first != null && s.Jobs == first;
            Check(kept, same && first.ThenIds == job2.ID,
                $"run kept {same}; working '{s.Jobs?.Ids}', then '{s.Jobs?.ThenIds}'; status '{s.Engineer.Status}'");
            var elapsed = 0f;
            var firstDoneAt = -1f;
            var secondStartedAt = -1f;
            while (elapsed < 2400 && s.Jobs != null)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                if (firstDoneAt < 0 && job1.tasks.TrueForAll(t => t.state == TaskState.Done))
                    firstDoneAt = elapsed;
                if (secondStartedAt < 0 && s.Jobs != null && s.Jobs != first)
                    secondStartedAt = elapsed;
                if ((int)elapsed % 15 == 0)
                    Trace(s, "jobs D", elapsed);
            }
            var done1 = job1.tasks.TrueForAll(t => t.state == TaskState.Done);
            var done2 = job2.tasks.TrueForAll(t => t.state == TaskState.Done);
            Check(worked, done1 && done2 && firstDoneAt >= 0 && secondStartedAt >= firstDoneAt,
                $"{job1.ID} {(done1 ? "done" : "NOT done: " + Jobs.Read(world, job1).Summary)} at {firstDoneAt:F0} s, {job2.ID} {(done2 ? "done" : "NOT done: " + Jobs.Read(world, job2).Summary)}, its run began at {secondStartedAt:F0} s; " +
                $"{(s.Jobs == null ? "finished" : "still going: " + s.Jobs.Status)} after {elapsed:F0} s; status '{s.Engineer.Status}'");
            yield return ClearCars(s, car1.Concat(car2));
        }
    }
}
