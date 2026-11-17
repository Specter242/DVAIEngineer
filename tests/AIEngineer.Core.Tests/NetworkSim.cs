using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;

namespace AIEngineer.Core.Tests;

/// <summary>Switches and standing cars for the engineer to work against.</summary>
public sealed class FakeWorld : IEngineerWorld
{
    public FakeWorld(TrackGraph graph)
    {
        Graph = graph;
        Router = new Router(graph);
        Selected = new int[graph.JunctionCount];
    }

    public TrackGraph Graph { get; }
    public Router Router { get; }
    public OccupancyIndex Occupancy { get; } = new();
    public int[] Selected { get; }
    public HashSet<int> Locked { get; } = new();

    /// <summary>Switches with another train's cars at rest across their points.</summary>
    public HashSet<int> JammedSwitches { get; } = new();

    /// <summary>Whether a train's own cars are across a switch: the index holds only the others'.</summary>
    public Func<int, bool>? OwnCarsAcross { get; set; }

    /// <summary>Switches thrown while <see cref="OwnCarsAcross"/> said the train was on them.</summary>
    public int ThrownUnderTheTrain { get; private set; }
    public List<(int Junction, int Branch)> Throws { get; } = new();

    public int SelectedBranch(int junction) => Selected[junction];
    public bool CanThrow(int junction, int owner) => !Locked.Contains(junction) && !JammedSwitches.Contains(junction);
    public bool Jammed(int junction, int owner) => JammedSwitches.Contains(junction);
    public bool PointsOccupied(int junction) => JammedSwitches.Contains(junction) || (OwnCarsAcross?.Invoke(junction) ?? false);

    public void Throw(int junction, int branch, int owner)
    {
        if (OwnCarsAcross?.Invoke(junction) ?? false)
            ThrownUnderTheTrain++;
        Selected[junction] = branch;
        Throws.Add((junction, branch));
    }

    public void Reserve(int junction, int owner) { }
}

/// <summary>
/// A train that moves over the network: end A leads one way, end B the
/// other, the other end found by walking back along the train. Trailing
/// switches set against it are forced over, as the game's bogies do;
/// running off a dead end derails it.
/// </summary>
public sealed class NetworkTrain
{
    private readonly FakeWorld world;

    public NetworkTrain(FakeWorld world, TrackPosition endA, TrackDirection outwardA, double length)
    {
        this.world = world;
        EndA = endA;
        OutwardA = outwardA;
        Length = length;
        // Walking back along the train, the way it ends up moving is outward at B.
        (EndB, OutwardB) = Walk(EndA, OutwardA.Reverse(), Length, force: false);
    }

    public TrackPosition EndA { get; private set; }
    public TrackDirection OutwardA { get; private set; }
    public TrackPosition EndB { get; private set; }
    public TrackDirection OutwardB { get; private set; }
    public double Length { get; private set; }
    public bool Derailed { get; private set; }
    public SimTrain Physics { get; } = new();
    public double SpeedTowardA { get; private set; }
    public int CarCount { get; set; } = 6;

    public TrainState State(bool hasTrainBrake = true) => new()
    {
        EndA = EndA,
        OutwardA = OutwardA,
        EndB = EndB,
        OutwardB = OutwardB,
        Length = Length,
        SpeedTowardA = SpeedTowardA,
        CarCount = CarCount,
        Owner = 1,
        OnTrack = !Derailed,
        CanMove = true,
        Controls = new ControlCapabilities { HasTrainBrake = hasTrainBrake },
    };

    /// <summary>Steps the physics with the engineer's controls and moves the train.</summary>
    public void Step(EngineerOutput output, ConsistEnd toward, double dt)
    {
        // The sim's speed is along the reverser's way.
        var sign = toward == ConsistEnd.A ? 1 : -1;
        Physics.Speed = SpeedTowardA * sign;
        var before = Physics.Position;
        Physics.Step(output.Controls.Throttle, Math.Max(output.Controls.TrainBrake, output.Controls.DynamicBrake * 0.6), output.Controls.LocoBrake, dt);
        SpeedTowardA = Physics.Speed * sign;
        Move((Physics.Position - before) * sign);
    }

    /// <summary>Couples a cut onto the given end: the train grows by its length, that end moving out.</summary>
    public void CoupleOn(ConsistEnd end, double length, int cars)
    {
        Length += length;
        CarCount += cars;
        if (end == ConsistEnd.A)
        {
            (EndA, OutwardA) = Walk(EndA, OutwardA, length, force: false);
        }
        else
        {
            (EndB, OutwardB) = Walk(EndB, OutwardB, length, force: false);
        }
    }

    /// <summary>Sets the train rolling, for tests that start in motion.</summary>
    public void SetSpeed(double towardA) => SpeedTowardA = towardA;

    public void Move(double towardA)
    {
        if (Derailed || towardA == 0)
            return;
        if (towardA > 0)
        {
            (EndA, OutwardA) = Walk(EndA, OutwardA, towardA, force: true);
            var (b, arrive) = Walk(EndA, OutwardA.Reverse(), Length, force: false);
            EndB = b;
            OutwardB = arrive;
        }
        else
        {
            (EndB, OutwardB) = Walk(EndB, OutwardB, -towardA, force: true);
            var (a, arrive) = Walk(EndB, OutwardB.Reverse(), Length, force: false);
            EndA = a;
            OutwardA = arrive;
        }
    }

    /// <summary>Walks a distance along the switches as set; returns where it ends and the direction it was moving.</summary>
    private (TrackPosition, TrackDirection) Walk(TrackPosition from, TrackDirection dir, double distance, bool force)
    {
        var g = world.Graph;
        var track = from.Track;
        var offset = from.Offset;
        for (var guard = 0; guard < 10000; guard++)
        {
            var exit = dir.ExitEnd();
            var exitOffset = g.OffsetOf(track, exit);
            var span = Math.Abs(exitOffset - offset);
            if (distance <= span)
                return (new TrackPosition(track, offset + (int)dir * distance), dir);
            distance -= span;
            var links = g.Links(track, exit);
            TrackLink? next = null;
            foreach (var l in links)
            {
                if (!l.NeedsJunction || world.Selected[l.Junction] == l.Branch)
                {
                    next = l;
                    break;
                }
            }
            if (next == null && links.Length > 0)
            {
                // Entering by a branch set against us: force the switch over.
                var l = links[0];
                if (force)
                    world.Selected[l.Junction] = l.Branch;
                next = l;
            }
            if (next == null)
            {
                if (force)
                    Derailed = true;
                return (new TrackPosition(track, exitOffset), dir);
            }
            track = next.Value.Track;
            offset = g.OffsetOf(track, next.Value.End);
            dir = next.Value.End.DirectionInto();
        }
        throw new InvalidOperationException("walked too far");
    }
}

/// <summary>Runs an engineer and a network train together until the engineer settles.</summary>
public static class Drive
{
    public static double Run(Engineer engineer, NetworkTrain train, FakeWorld world, double maxSeconds = 1200,
        Func<bool>? until = null, bool hasTrainBrake = true, Action<EngineerOutput>? onOutput = null,
        Func<TrainState, TrainState>? state = null)
    {
        const double physicsDt = 0.02;
        const double tick = 0.2;
        var output = new EngineerOutput();
        var toward = train.SpeedTowardA < 0 ? ConsistEnd.B : ConsistEnd.A;
        var time = 0.0;
        for (var since = tick; time < maxSeconds; time += physicsDt, since += physicsDt)
        {
            if (since >= tick)
            {
                since = 0;
                var now = train.State(hasTrainBrake);
                now.Reverser = toward;
                if (state != null)
                    now = state(now);
                output = engineer.Tick(tick, now, world);
                onOutput?.Invoke(output);
                if (output.Toward.HasValue)
                {
                    // A real reverser only changes when stopped; the engineer stops before reversing.
                    if (output.Toward.Value != toward && Math.Abs(train.SpeedTowardA) > 0.1)
                        throw new InvalidOperationException($"reversed at {train.SpeedTowardA:F2} m/s, t={time:F1}");
                    toward = output.Toward.Value;
                }
                if (until != null && until())
                    break;
            }
            train.Step(output, toward, physicsDt);
            if (train.Derailed)
                throw new InvalidOperationException($"derailed at t={time:F1}: {engineer.Status}");
        }
        return time;
    }
}
