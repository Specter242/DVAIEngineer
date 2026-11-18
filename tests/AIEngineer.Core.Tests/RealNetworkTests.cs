using System.Diagnostics;
using System.Text.Json;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using Xunit.Abstractions;

namespace AIEngineer.Core.Tests;

/// <summary>
/// Checks against a network exported from the game by the self-test
/// (selftest-network.json). Runs only when AIE_NETWORK_JSON names such a
/// file; the export is the game's map and stays out of the repository.
/// </summary>
public class RealNetworkTests
{
    private readonly ITestOutputHelper output;

    public RealNetworkTests(ITestOutputHelper output) => this.output = output;

    private static string? ExportPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("AIE_NETWORK_JSON");
            return path != null && File.Exists(path) ? path : null;
        }
    }

    /// <summary>Each track's line: its points where the export has them (from 2026-11-16), else its two ends.</summary>
    public static MapPoint[][] LoadLines(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var lines = new List<MapPoint[]>();
        foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateArray())
        {
            var pts = t.TryGetProperty("pts", out var p) && p.GetArrayLength() >= 2 ? p.EnumerateArray().Select(x => new MapPoint(x[0].GetDouble(), x[1].GetDouble())).ToArray()
                : new[] { t.GetProperty("s"), t.GetProperty("e") }.Select(x => new MapPoint(x[0].GetDouble(), x[1].GetDouble())).ToArray();
            lines.Add(pts);
        }
        return lines.ToArray();
    }

    /// <summary>Rebuilds the exported graph through the builder, as the game side does.</summary>
    public static (TrackGraph Graph, string[] Names, int[] Selected) Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var b = new TrackGraphBuilder();
        var tracks = doc.RootElement.GetProperty("tracks");
        var names = new List<string>();
        foreach (var t in tracks.EnumerateArray())
        {
            var s = t.GetProperty("s");
            var e = t.GetProperty("e");
            b.AddTrack(t.GetProperty("len").GetDouble(), new MapPoint(s[0].GetDouble(), s[1].GetDouble()), new MapPoint(e[0].GetDouble(), e[1].GetDouble()));
            names.Add(t.GetProperty("name").GetString() ?? "");
        }
        var index = 0;
        foreach (var t in tracks.EnumerateArray())
        {
            foreach (var (prop, end) in new[] { ("in", TrackEnd.Start), ("out", TrackEnd.End) })
            {
                foreach (var l in t.GetProperty(prop).EnumerateArray())
                {
                    if (l[2].GetInt32() == TrackLink.NoJunction)
                        b.Join(index, end, l[0].GetInt32(), (TrackEnd)l[1].GetInt32());
                }
            }
            index++;
        }
        var selected = new List<int>();
        foreach (var j in doc.RootElement.GetProperty("junctions").EnumerateArray())
        {
            var stem = j.GetProperty("stem");
            var branches = j.GetProperty("branches").EnumerateArray().Select(x => (x[0].GetInt32(), (TrackEnd)x[1].GetInt32())).ToArray();
            b.AddJunction(stem[0].GetInt32(), (TrackEnd)stem[1].GetInt32(), branches, new MapPoint(j.GetProperty("x").GetDouble(), j.GetProperty("z").GetDouble()));
            selected.Add(j.GetProperty("selected").GetInt32());
        }
        return (b.Build(), names.ToArray(), selected.ToArray());
    }

    [Fact]
    public void TheNetworkIsMostlyConnectedAndRoutable()
    {
        var path = ExportPath;
        if (path == null)
            return;
        var (g, names, _) = Load(path);
        // Connected components over track ends and links, ignoring switch settings.
        var component = Enumerable.Repeat(-1, g.TrackCount).ToArray();
        var sizes = new List<int>();
        for (var t = 0; t < g.TrackCount; t++)
        {
            if (component[t] >= 0)
                continue;
            var id = sizes.Count;
            var stack = new Stack<int>();
            stack.Push(t);
            component[t] = id;
            var size = 0;
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                size++;
                foreach (var end in new[] { TrackEnd.Start, TrackEnd.End })
                {
                    foreach (var l in g.Links(c, end))
                    {
                        if (component[l.Track] < 0)
                        {
                            component[l.Track] = id;
                            stack.Push(l.Track);
                        }
                    }
                }
            }
            sizes.Add(size);
        }
        var largest = sizes.Max();
        output.WriteLine($"{g.TrackCount} tracks, {g.JunctionCount} switches, {sizes.Count} pieces; the largest has {largest} tracks");
        foreach (var (size, i) in sizes.Select((s, i) => (s, i)).OrderByDescending(x => x.s).Take(8))
            output.WriteLine($"  piece {i}: {size} tracks, e.g. {names[Array.IndexOf(component, i)]}");

        // Route between random pairs of long tracks in the largest piece.
        var main = sizes.IndexOf(largest);
        var candidates = Enumerable.Range(0, g.TrackCount).Where(t => component[t] == main && g.Length(t) > 100).ToArray();
        var random = new Random(1);
        var router = new Router(g);
        var found = 0;
        var watch = Stopwatch.StartNew();
        const int tries = 60;
        for (var k = 0; k < tries; k++)
        {
            var from = candidates[random.Next(candidates.Length)];
            var to = candidates[random.Next(candidates.Length)];
            var mid = g.Length(from) / 2;
            var route = router.Find(new RouteRequest
            {
                EndA = new TrackPosition(from, mid - 20),
                OutwardA = TrackDirection.Backward,
                EndB = new TrackPosition(from, mid + 20),
                OutwardB = TrackDirection.Forward,
                TrainLength = 40,
                Goal = new TrackPosition(to, g.Length(to) / 2),
            }, out var failure);
            if (route != null)
                found++;
            else
                output.WriteLine($"  no route {names[from]} -> {names[to]}: {failure}");
        }
        output.WriteLine($"{found}/{tries} routes found, {watch.Elapsed.TotalMilliseconds / tries:F1} ms each");
        Assert.True(found >= tries * 0.9, $"only {found} of {tries} routes");
    }

    /// <summary>The router's cost on the real map is the meters the train runs, its reversals, and its shoving, each counted once.</summary>
    [Fact]
    public void TheSearchPricesShovingAsTheTrainRunsIt()
    {
        var path = ExportPath;
        if (path == null)
            return;
        var (g, _, _) = Load(path);
        var router = new Router(g);
        var candidates = Enumerable.Range(0, g.TrackCount).Where(t => g.Length(t) > 180).ToArray();
        var random = new Random(7);
        var found = 0;
        var reversing = 0;
        for (var k = 0; k < 600; k++)
        {
            var from = candidates[random.Next(candidates.Length)];
            var to = candidates[random.Next(candidates.Length)];
            var at = g.Length(from) / 2 - 50;
            var shoving = (ConsistEnd)(k % 2);
            var request = new RouteRequest
            {
                EndA = new TrackPosition(from, at),
                OutwardA = TrackDirection.Backward,
                EndB = new TrackPosition(from, at + 100),
                OutwardB = TrackDirection.Forward,
                TrainLength = 100,
                Goal = new TrackPosition(to, g.Length(to) / 2),
                LastLeader = k % 3 == 0 ? shoving : k % 3 == 1 ? shoving.Other() : null,
                ShovingEnd = shoving,
                DivergingPenalty = 0,
            };
            var route = router.Find(request);
            if (route == null)
                continue;
            found++;
            if (route.Reversals > 0)
                reversing++;
            var shoved = route.Legs.Where(l => l.Leader == shoving).Sum(l => l.Running);
            Assert.Equal(route.Running + request.ReversalPenalty * route.Reversals + shoved, route.SearchCost, 3);
        }
        output.WriteLine($"{found} routes, {reversing} with reversals");
        Assert.True(found > 300 && reversing > 50, $"{found} routes, {reversing} with reversals");
    }

    /// <summary>
    /// Drives random routes on the real network as a train's body would
    /// follow them: every piece joined by a real link (never branch to
    /// branch), each leg starting under the end that leads it and heading
    /// out of the train, no standing cars run through, no switch needed
    /// another way while the train is still across it, and the right end
    /// at the goal the right way round. A third of the trains start
    /// across joints and switches, as trains in a yard do.
    /// </summary>
    [Fact]
    public void RandomRoutesAreDrivable()
    {
        var path = ExportPath;
        if (path == null)
            return;
        var (g, names, selected) = Load(path);
        var router = new Router(g);
        var standingCars = new OccupancyIndex();
        for (var t = 0; t < g.TrackCount; t++)
        {
            // A cut on every named yard track, 15 m clear of each end.
            if (names[t].StartsWith("[Y]", StringComparison.Ordinal) && g.Length(t) > 50)
                standingCars.Add(t, new OccupiedSpan(15, g.Length(t) - 15, 1000 + t));
        }
        var candidates = Enumerable.Range(0, g.TrackCount)
            .Where(t => g.Length(t) > 12 && g.Links(t, TrackEnd.Start).Length + g.Links(t, TrackEnd.End).Length > 0).ToArray();
        var random = new Random(7);
        int routes = 0, none = 0, across = 0, pullsClear = 0, backedOnto = 0;
        var complaints = new List<string>();
        for (var k = 0; k < 4000; k++)
        {
            var from = candidates[random.Next(candidates.Length)];
            var length = new[] { 20.0, 40, 100, 200 }[random.Next(4)];
            var tail = new TrackPosition(from, 3 + random.NextDouble() * Math.Max(0, g.Length(from) - 6));
            var way = random.Next(2) == 0 ? TrackDirection.Forward : TrackDirection.Backward;
            // The train's body, tail to head, along the switches as the export has them.
            var body = TrackWalker.Walk(g, tail, way, length, j => selected[j], ConsistEnd.A, out var walkEnd);
            if (walkEnd != WalkEnd.Distance)
                continue;
            var cars = random.Next(3) > 0;
            var own = 1000 + from;
            Func<int, double, double, bool>? standing = cars ? (t, a, b) => standingCars.IsStanding(t, a, b, own) : null;
            // Not a train laid on top of standing cars.
            if (cars && body.Pieces.Any(p => p.Track != from && standingCars.IsStanding(p.Track, Math.Min(p.From, p.To), Math.Max(p.From, p.To), own)))
                continue;
            var to = candidates[random.Next(candidates.Length)];
            if (random.Next(3) == 0)
            {
                // Somewhere near: a few links away, or the same track.
                to = from;
                for (var h = random.Next(7); h > 0; h--)
                {
                    var links = g.Links(to, (TrackEnd)random.Next(2));
                    if (links.Length > 0)
                        to = links[random.Next(links.Length)].Track;
                }
            }
            var last = body.Pieces[body.Pieces.Count - 1];
            var request = new RouteRequest
            {
                EndA = new TrackPosition(last.Track, last.To),
                OutwardA = last.Direction,
                EndB = tail,
                OutwardB = way.Reverse(),
                TrainLength = length,
                Goal = new TrackPosition(to, g.Length(to) * random.NextDouble()),
                LastLeader = random.Next(3) == 0 ? (ConsistEnd)random.Next(2) : null,
                GoalDirection = random.Next(3) == 0 ? (random.Next(2) == 0 ? TrackDirection.Forward : TrackDirection.Backward) : null,
                ShovingEnd = random.Next(2) == 0 ? (ConsistEnd)random.Next(2) : null,
                Blocked = standing,
                SwitchLie = j => selected[j],
            };
            var route = router.Find(request, out _);
            if (route == null)
            {
                none++;
                continue;
            }
            routes++;
            if (body.Pieces.Count > 1)
                across++;
            var bad = Drivable(g, request, route, body.Pieces, standing, out var pulled, out var backed);
            if (pulled)
                pullsClear++;
            if (backed)
                backedOnto++;
            if (bad != null && complaints.Count < 12)
                complaints.Add($"{names[from]} #{from} ({length:F0} m, {body.Pieces.Count} tracks) to {names[to]} #{to} @ {request.Goal.Offset:F0}, last {request.LastLeader}, arriving {request.GoalDirection}: {bad}" + Dump(g, body.Pieces, route));
            else if (bad != null)
                complaints.Add("");
        }
        output.WriteLine($"{routes} routes ({across} from across a joint, {pullsClear} pulling clear of a switch first, {backedOnto} backed onto the goal), {none} with none");
        foreach (var c in complaints.Where(c => c.Length > 0))
            output.WriteLine("  " + c);
        Assert.True(routes > 1500, $"only {routes} routes");
        Assert.True(pullsClear > 0, "no route began by pulling clear of a switch");
        Assert.True(complaints.Count == 0, $"{complaints.Count} routes can't be driven; the first: {complaints.FirstOrDefault()}");
    }

    private static string Dump(TrackGraph g, IReadOnlyList<RoutePiece> body, Route route) =>
        $"\n      body {string.Join(", ", body)} (track lengths {string.Join(", ", body.Select(p => g.Length(p.Track).ToString("F1")))})"
        + string.Concat(route.Legs.Select(l => $"\n      {l.Leader}: {string.Join(", ", l.Pieces.Take(6))}{(l.Pieces.Count > 6 ? " ..." : "")}; {l.Length:F1} m; {string.Join("; ", l.Switches.Take(4))}"));

    /// <summary>Follows a route with the train's body; null if it can be driven, else the first thing wrong.</summary>
    private static string? Drivable(TrackGraph g, RouteRequest request, Route route, IReadOnlyList<RoutePiece> start,
        Func<int, double, double, bool>? standing, out bool pulledClear, out bool backedOnto)
    {
        pulledClear = false;
        backedOnto = false;
        // The body, tail to head; the head is end A to begin with.
        var body = new List<RoutePiece>(start);
        var head = ConsistEnd.A;
        var goalAmongCars = standing != null && standing(request.Goal.Track, request.Goal.Offset - 0.5, request.Goal.Offset + 0.5);
        for (var li = 0; li < route.Legs.Count; li++)
        {
            var leg = route.Legs[li];
            if (li > 0 && leg.Leader == route.Legs[li - 1].Leader)
                return $"leg {li}: the same end leads again";
            if (leg.EndsInReversal != (li < route.Legs.Count - 1))
                return $"leg {li}: EndsInReversal is wrong";
            if (leg.Leader != head)
            {
                body.Reverse();
                for (var i = 0; i < body.Count; i++)
                    body[i] = body[i].Reversed();
                head = leg.Leader;
            }
            var front = body[body.Count - 1];
            var at = new TrackPosition(front.Track, front.To);
            if (!leg.TryLocate(at, 0, 0.01, out var from, out var piece))
                return $"leg {li}: its leading end ({at}) isn't on it";
            // A turn back past a link the leading end crossed pulls the whole train and a clearance beyond it; less is a pull clear of a switch the train stood across.
            if (li == 1 && route.Legs[0].Length < request.TrainLength + request.ReversalClearance - 0.5)
                pulledClear = true;
            for (var i = piece; i < leg.Pieces.Count; i++)
            {
                var p = leg.Pieces[i];
                var lo = i == piece ? from - leg.PieceStart(i) : 0;
                if (p.Length - lo <= 1e-6)
                {
                    if (i + 1 < leg.Pieces.Count)
                    {
                        var joined = Joined(g, p, leg.Pieces[i + 1], leg, i, body, request.TrainLength);
                        if (joined != null)
                            return $"leg {li}: {joined}";
                    }
                    continue;
                }
                var driven = new RoutePiece(p.Track, p.From + (int)p.Direction * lo, p.To);
                if (i == piece && driven.Direction != front.Direction && front.Length > 1e-6)
                    return $"leg {li}: it sets off back through its own train";
                var lastOfAll = li == route.Legs.Count - 1 && i == leg.Pieces.Count - 1;
                if (standing != null && !(lastOfAll && goalAmongCars))
                {
                    var a = driven.From + (int)driven.Direction * (li == 0 && i == piece ? 0.6 : 0);
                    if (Math.Abs(driven.To - a) > 1e-6 && (driven.To - a) * (int)driven.Direction > 0 && standing(p.Track, Math.Min(a, driven.To), Math.Max(a, driven.To)))
                        return $"leg {li}: {driven} runs through standing cars";
                }
                body.Add(driven);
                Trim(body, request.TrainLength);
                if (i + 1 < leg.Pieces.Count)
                {
                    var joined = Joined(g, p, leg.Pieces[i + 1], leg, i, body, request.TrainLength);
                    if (joined != null)
                        return $"leg {li}: {joined}";
                }
            }
        }
        var lastLeg = route.Legs[route.Legs.Count - 1];
        var headPiece = body[body.Count - 1];
        var tailPiece = body[0];
        var leaderAt = new TrackPosition(headPiece.Track, headPiece.To);
        var otherAt = new TrackPosition(tailPiece.Track, tailPiece.From);
        bool At(TrackPosition p) => p.Track == request.Goal.Track && Math.Abs(p.Offset - request.Goal.Offset) < 0.01;
        ConsistEnd arrived;
        TrackDirection facing;
        if (At(leaderAt))
        {
            arrived = lastLeg.Leader;
            facing = headPiece.Direction;
        }
        else if (At(otherAt))
        {
            // Backed onto a goal that was under the train: the end standing on it faces out of the train.
            arrived = lastLeg.Leader.Other();
            facing = tailPiece.Direction.Reverse();
            backedOnto = true;
            if (route.Legs.Count != 1)
                return "backed onto the goal after a reversal";
        }
        else
        {
            return $"neither end is at the goal (leader {leaderAt}, other {otherAt})";
        }
        if (request.LastLeader.HasValue && arrived != request.LastLeader.Value)
            return "the wrong end is at the goal";
        if (request.GoalDirection.HasValue && facing != request.GoalDirection.Value)
            return "it arrives the wrong way along the goal's track";
        return null;
    }

    /// <summary>Checks the joint between two pieces of a leg: a real link, its switch listed, and the train's own body not across that switch another way.</summary>
    private static string? Joined(TrackGraph g, RoutePiece p, RoutePiece q, RouteLeg leg, int index, List<RoutePiece> body, double trainLength)
    {
        var link = LinkBetween(g, p, q);
        if (link == null)
            return $"no link from {p} to {q} (branch to branch?)";
        if (!link.Value.NeedsJunction)
            return null;
        var distance = leg.PieceStart(index + 1);
        if (!leg.Switches.Any(s => s.Junction == link.Value.Junction && s.Branch == link.Value.Branch && Math.Abs(s.AtDistance - distance) < 1e-3))
            return $"switch {link.Value.Junction} to {link.Value.Branch} at {distance:F1} m isn't listed";
        for (var i = 0; i + 1 < body.Count; i++)
        {
            var under = LinkBetween(g, body[i], body[i + 1]);
            if (under != null && under.Value.NeedsJunction && under.Value.Junction == link.Value.Junction && under.Value.Branch != link.Value.Branch)
                return $"switch {link.Value.Junction} is wanted for branch {link.Value.Branch} with the train across it on branch {under.Value.Branch}";
        }
        return null;
    }

    private static TrackLink? LinkBetween(TrackGraph g, RoutePiece p, RoutePiece q)
    {
        TrackEnd? EndAt(int track, double offset) =>
            Math.Abs(offset) < 1e-6 ? TrackEnd.Start : Math.Abs(offset - g.Length(track)) < 1e-6 ? TrackEnd.End : null;
        var leave = EndAt(p.Track, p.To);
        var enter = EndAt(q.Track, q.From);
        if (leave == null || enter == null)
            return null;
        // A track shorter than a hair has both ends at one offset: the end a piece leaves by is the one it runs toward.
        if (g.Length(p.Track) < 1e-6)
            leave = p.Direction.ExitEnd();
        foreach (var l in g.Links(p.Track, leave.Value))
        {
            if (l.Track == q.Track && l.End == enter.Value)
                return l;
        }
        return null;
    }

    /// <summary>Keeps the last <paramref name="length"/> meters of a body, tail to head.</summary>
    private static void Trim(List<RoutePiece> body, double length)
    {
        var total = body.Sum(p => p.Length);
        while (body.Count > 1 && total - body[0].Length >= length)
        {
            total -= body[0].Length;
            body.RemoveAt(0);
        }
        var extra = total - length;
        if (extra > 1e-9)
        {
            var first = body[0];
            body[0] = new RoutePiece(first.Track, first.From + (int)first.Direction * extra, first.To);
        }
    }
}
