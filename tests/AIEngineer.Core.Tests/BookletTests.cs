using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIEngineer.Core.Booklet;
using AIEngineer.Core.Tracks;
using Xunit.Abstractions;

namespace AIEngineer.Core.Tests;

/// <summary>
/// The booklet's pages read as track. The real-network checks run only when
/// AIE_BOOKLET_JSON names a booklet export (the self-test's booklet.json, or
/// scripts/booklet_from_survey.py's) and AIE_NETWORK_JSON the network export
/// from the same game; both are the game's data and stay out of the repository.
/// </summary>
public class BookletTests
{
    private readonly ITestOutputHelper output;

    public BookletTests(ITestOutputHelper output) => this.output = output;

    public static List<SchematicSource> LoadSources(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<SchematicSource>();
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        foreach (var p in doc.RootElement.GetProperty("pages").EnumerateArray())
        {
            var tiles = p.GetProperty("tiles").EnumerateArray().Select(t => new SchematicTile(t.GetProperty("s").GetString()!, t.GetProperty("x").GetDouble(), t.GetProperty("y").GetDouble(),
                t.GetProperty("r").GetDouble(), t.GetProperty("w").GetDouble(), t.GetProperty("h").GetDouble())).ToList();
            var badges = p.GetProperty("badges").EnumerateArray().Select(b => new SchematicBadge(b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(),
                Str(b, "text"), Str(b, "type"), Str(b, "color"))).ToList();
            var switches = p.GetProperty("switches").EnumerateArray().Select(s => new SchematicSwitch(s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble(), s.GetProperty("j").GetInt32())).ToList();
            list.Add(new SchematicSource(Str(p, "name")!, Str(p, "station"), Str(p, "stationName"), Str(p, "yard"), Str(p, "title"), Str(p, "color"),
                p.GetProperty("route").GetBoolean(), tiles, badges, switches));
        }
        return list;
    }

    [Fact]
    public void AYardTracksNameReadsAsTheBookletShowsIt()
    {
        Assert.True(YardTrackName.TryParse("[Y]_[SM]_[A-04-S]", out var n));
        Assert.Equal(("SM", "A", 4, "S"), (n.Station, n.Yard, n.Number, n.Kind));
        Assert.Equal("SM-A4S", n.Display);
        Assert.True(YardTrackName.TryParse("[Y]_[HB]_[T1-02-S]", out var t));
        Assert.Equal(("HB", "T1", 2), (t.Station, t.Yard, t.Number));
        Assert.False(YardTrackName.TryParse("[track through]", out _));
        Assert.False(YardTrackName.TryParse("Road 12", out _));
        Assert.False(YardTrackName.TryParse(null!, out _));
        Assert.Equal(PageRef.Overview("TS"), PageRef.OfYard("TS", "B").Up);
        Assert.Equal(PageRef.Route, PageRef.Overview("TS").Up);
    }

    [Fact]
    public void APageOpensFittedAndStaysOnItself()
    {
        // A 1056 x 828 map; the fit area clear of a 128 px tab column, the 152 px column, and the strip.
        var area = new PageBox(128, 16, 1056 - 152, 828 - 84);
        var box = new PageBox(0, 0, 500, 200);
        var fit = PageView.Fit(box, area, 1056, 828, right: 90, left: 0, cap: 2.2);
        Assert.Equal((776 - 90) / 500.0, fit.Scale, 6);
        // Its content sits in the area: left edge at the area's left plus centring.
        var (lx, _) = fit.ToScreen(box.Left, box.Top, 528, 414);
        Assert.Equal(128, lx, 6);
        // A long page at a floor opens from its left end.
        var longBox = new PageBox(0, 0, 2000, 300);
        var floor = PageView.Fit(longBox, area, 1056, 828, 90, 0, 2.2, floor: 58 / 44.0);
        Assert.Equal(58 / 44.0, floor.Scale, 6);
        Assert.Equal(128 + 8, floor.ToScreen(0, 0, 528, 414).X, 6);
        // Zoomed out past the fit, or moved off the page, it comes back.
        var away = fit.Zoomed(0.25).Moved(100000, -100000).ClampTo(fit, box);
        Assert.Equal(fit.Scale, away.Scale, 6);
        Assert.InRange(away.CenterX, box.Left, Math.Max(box.Right, fit.CenterX));
        Assert.Equal(PageView.MaxScale, fit.Zoomed(100).ClampTo(fit, box).Scale, 6);
    }

    private static SchematicTile T(string sprite, double x, double y, double rot) => new(sprite, x, y, rot, 50, 50);

    /// <summary>
    /// A passing loop, Test Station's yard B, as the booklet draws one: the
    /// main line along y 0 between two exits, switch A at x -150 (points to
    /// the west) and B at 150 (points to the east), and track 1 along y -50
    /// with its badge standing in its middle cell. The main line is drawn
    /// twice in one cell, as some pages do. When <paramref name="sidingStraight"/>
    /// the loop is drawn the other way about: the siding along y 0, straight
    /// through the switches, and the main line turning up to y 50.
    /// </summary>
    private static SchematicSource LoopPage(bool sidingStraight)
    {
        var tiles = new List<SchematicTile>
        {
            T("SM-TracksExit", -300, 0, 90), T("SM-TracksStraight", -250, 0, 90), T("SM-TracksStraight", -200, 0, 90),
            T("SM-TracksStraight", 200, 0, 90), T("SM-TracksStraight", 250, 0, 90), T("SM-TracksExit", 300, 0, 270),
            T("SM-Switch", -150, 0, 0), T("SM-Switch", 150, 0, 0),
        };
        for (var x = -100; x <= 100; x += 50)
            tiles.Add(T("SM-TracksStraight", x, 0, 90));
        tiles.Add(T("SM-TracksStraight", 50, 0, 90));
        double side = sidingStraight ? 50 : -50;
        for (var x = -100; x <= 100; x += 50)
            if (x != 0)
                tiles.Add(T("SM-TracksStraight", x, side, 90));
        if (!sidingStraight)
        {
            // The siding turns down off each switch: A's diverging leg, to the right of a train from its points.
            tiles.Add(T("SM-TracksSwitchDivergingR", -150, 0, 270));
            tiles.Add(T("SM-TracksDiagonal", -150, -50, 90));
            tiles.Add(T("SM-TracksSwitchDivergingL", 150, 0, 90));
            tiles.Add(T("SM-TracksDiagonal", 150, -50, 180));
        }
        else
        {
            // The main line turns up off each switch, to the left of a train from A's points.
            tiles.Add(T("SM-TracksSwitchDivergingL", -150, 0, 270));
            tiles.Add(T("SM-TracksDiagonal", -150, 50, 0));
            tiles.Add(T("SM-TracksSwitchDivergingR", 150, 0, 90));
            tiles.Add(T("SM-TracksDiagonal", 150, 50, 270));
        }
        var badges = new List<SchematicBadge> { new(0, sidingStraight ? 0 : -50, "1", "S", null) };
        if (sidingStraight)
        {
            // The siding's middle cell holds the badge, the main line's runs on above it.
            tiles.RemoveAll(t => t.Sprite == "SM-TracksStraight" && t.X == 0 && t.Y == 0);
            tiles.Add(T("SM-TracksStraight", 0, 50, 90));
        }
        var switches = new List<SchematicSwitch> { new(-150, 0, 0), new(150, 0, 1) };
        return new SchematicSource("[TS-B]", "TS", "Test Station", "B", "TRANSFER YARD", "#808080FF", false, tiles, badges, switches);
    }

    /// <summary>
    /// The loop on the map, turned 30 degrees: switch A's diverging branch
    /// (listed first, as the game lists them) leads to the siding south of
    /// the main line, as drawn in <see cref="LoopPage"/>.
    /// </summary>
    private static (TrackGraph Graph, string[] Names) LoopGraph()
    {
        var turn = 30 * Math.PI / 180;
        MapPoint P(double x, double z) => new(x * Math.Cos(turn) - z * Math.Sin(turn), x * Math.Sin(turn) + z * Math.Cos(turn));
        var b = new TrackGraphBuilder();
        var names = new List<string>();
        int Add(string name, MapPoint s, MapPoint e)
        {
            names.Add(name);
            return b.AddTrack(s.DistanceTo(e), s, e);
        }
        var west = Add("Road 1", P(-600, 0), P(-150, 0));
        var ad = Add("[track diverging] 1", P(-150, 0), P(-120, -6));
        var at = Add("[track through] 1", P(-150, 0), P(-120, 0));
        var main = Add("Road 1 middle", P(-120, 0), P(120, 0));
        var siding = Add("[Y]_[TS]_[B-01-S]", P(-120, -6), P(120, -6));
        var bd = Add("[track diverging] 2", P(150, 0), P(120, -6));
        var bt = Add("[track through] 2", P(150, 0), P(120, 0));
        var east = Add("Road 1 east", P(150, 0), P(600, 0));
        b.AddJunction(west, TrackEnd.End, new[] { (ad, TrackEnd.Start), (at, TrackEnd.Start) }, P(-150, 0));
        b.AddJunction(east, TrackEnd.Start, new[] { (bd, TrackEnd.Start), (bt, TrackEnd.Start) }, P(150, 0));
        b.Join(at, TrackEnd.End, main, TrackEnd.Start);
        b.Join(main, TrackEnd.End, bt, TrackEnd.End);
        b.Join(ad, TrackEnd.End, siding, TrackEnd.Start);
        b.Join(siding, TrackEnd.End, bd, TrackEnd.End);
        return (b.Build(), names.ToArray());
    }

    [Fact]
    public void APageIsReadAsTheTrackItDraws()
    {
        var s = Schematic.Trace(LoopPage(sidingStraight: false));
        // Two switches, each named by its touch cell; runs: two exits, the main line, and the siding under its badge.
        Assert.Equal(new[] { 0, 1 }, s.Switches.Select(w => w.Junction));
        Assert.False(s.Switches[0].DivergesLeft);
        Assert.Equal(4, s.Runs.Count);
        var siding = s.Runs.Single(r => r.Badges.Any(b => b.Text == "1"));
        Assert.Equal((RunEndKind.Switch, RunEndKind.Switch), (siding.Start.Kind, siding.End.Kind));
        Assert.Equal(new[] { SwitchLeg.Diverging, SwitchLeg.Diverging }, new[] { siding.Start.Leg, siding.End.Leg });
        // Badge cell and all, the siding runs from switch to switch: from each switch's dot (15 units toward its points)
        // to its diverging edge, a diagonal, five cells.
        Assert.Equal(new[] { -165.0, 165.0 }, new[] { siding.Points[0].X, siding.Points[^1].X }.OrderBy(x => x));
        Assert.Equal(2 * Math.Sqrt(15 * 15 + 25 * 25) + 2 * 25 * Math.Sqrt(2) + 5 * 50, siding.Length, 6);
        // The main line off each end, from a switch's points to an exit.
        Assert.Equal(2, s.Runs.Count(r => (r.Start.Kind == RunEndKind.Exit) != (r.End.Kind == RunEndKind.Exit)));
    }

    [Fact]
    public void ADrawnSwitchsLegsAreTheBranchesTheyLeadAs()
    {
        var (graph, names) = LoopGraph();
        foreach (var straight in new[] { false, true })
        {
            var book = Booklet.Booklet.Build(new[] { LoopPage(straight) }, graph, t => names[t]);
            var page = book.Stations.Single().Pages.Single();
            Assert.All(page.Runs, r => Assert.True(r.Matched, r.ToString()));
            var siding = page.Runs.Single(r => r.Drawn.Badges.Any(b => b.Text == "1"));
            Assert.Equal(new[] { "[track diverging] 1", "[Y]_[TS]_[B-01-S]", "[track diverging] 2" },
                (siding.Drawn.Start.Leg == SwitchLeg.Stem ? siding.Path.Reverse() : siding.Path).Select(p => names[p.Track]).OrderBy(n => n.Contains('Y') ? 1 : n.EndsWith('1') ? 0 : 2));
            Assert.Equal(1, book.Stats.BadgesAgree);
            // Both legs lead to switch B: the drawn leg on the left is the branch on the left.
            Assert.Equal(2, book.Stats.LegsTied);
            // Switch A's diverging branch (the game's first) is the siding: drawn as its diverging leg, or straight on.
            Assert.Equal(straight ? SwitchLeg.Through : SwitchLeg.Diverging, page.LegOf(0, 0));
            Assert.Equal(straight ? SwitchLeg.Diverging : SwitchLeg.Through, page.LegOf(0, 1));
        }
    }

    [Fact]
    public void APositionIsDrawnAsFarAlongItsRunAsAlongItsTracks()
    {
        var (graph, names) = LoopGraph();
        var page = Booklet.Booklet.Build(new[] { LoopPage(false) }, graph, t => names[t]).Stations[0].Pages[0];
        var sidingTrack = Array.IndexOf(names, "[Y]_[TS]_[B-01-S]");
        Assert.True(page.Shows(sidingTrack));
        // The siding's middle is drawn on its row (page y down: +50), by its badge.
        var (at, heading) = page.PointOf(new TrackPosition(sidingTrack, graph.Length(sidingTrack) / 2))!.Value;
        Assert.Equal(50, at.Z, 1);
        Assert.InRange(at.X, -40, 40);
        Assert.Equal(1, Math.Abs(heading.X), 6);
        // A press on the row finds the siding there, and the point it is drawn at.
        var pressed = page.TrackAt(at.X, at.Z + 8, radius: 20)!.Value;
        Assert.Equal(sidingTrack, pressed.Track);
        Assert.Equal(graph.Length(sidingTrack) / 2, pressed.Offset, 0);
        Assert.Null(page.TrackAt(0, 120, radius: 20));
        // A car on the siding's middle third is drawn along its row, by the badge.
        var third = graph.Length(sidingTrack) / 3;
        var (run, from, to) = page.Stretch(sidingTrack, third, 2 * third).Single();
        var line = run.Drawn.Between(from, to);
        Assert.All(line, p => Assert.Equal(-50, p.Z, 6));
        Assert.Equal(Math.Abs(to - from), line.First().DistanceTo(line.Last()), 6);
        // The switches can be pressed; the main line's west end goes off the page.
        Assert.Equal(0, page.SwitchAt(-150, 4, 20)!.Junction);
        Assert.Contains(page.Runs, r => r.Drawn.Start.Kind == RunEndKind.Exit && names[r.Path[0].Track] == "Road 1" || r.Drawn.End.Kind == RunEndKind.Exit && names[r.Path[^1].Track] == "Road 1");
    }

    [Fact]
    public void AnOverviewsCapsuleStandsForItsYard()
    {
        // An overview: the main line, and yard B's capsule beside it on a loop, its letter in the middle.
        var tiles = new List<SchematicTile>
        {
            T("SM-TracksExit", -300, 0, 90), T("SM-TracksStraight", -250, 0, 90), T("SM-TracksStraight", -200, 0, 90),
            T("SM-TracksSwitchDivergingR", -150, 0, 270), T("SM-TracksDiagonal", -150, -50, 90),
            T("SM-TracksYardFreightEnd", -100, -50, 270), T("SM-TracksYardFreightLoop", -50, -50, 90),
            T("SM-TracksYardFreightLoop", 50, -50, 90), T("SM-TracksYardFreightEnd", 100, -50, 90),
            T("SM-TracksSwitchDivergingL", 150, 0, 90), T("SM-TracksDiagonal", 150, -50, 180),
            T("SM-TracksStraight", 200, 0, 90), T("SM-TracksStraight", 250, 0, 90), T("SM-TracksExit", 300, 0, 270),
        };
        for (var x = -100; x <= 100; x += 50)
            tiles.Add(T("SM-TracksStraight", x, 0, 90));
        var src = new SchematicSource("[TS-A]", "TS", "Test Station", "A", "OVERVIEW", null, false, tiles,
            new[] { new SchematicBadge(0, -50, "B", null, null) }, new[] { new SchematicSwitch(-150, 0, 0), new SchematicSwitch(150, 0, 1) });
        var (graph, names) = LoopGraph();
        var book = Booklet.Booklet.Build(new[] { src }, graph, t => names[t]);
        var page = book.Page(PageRef.Overview("TS"))!;
        Assert.Equal("B", page.CapsuleAt(30, 52, 20));
        Assert.Null(page.CapsuleAt(30, 0 - 30, 20));
        // The capsule's tracks are the yard's page's to show, not the overview's.
        Assert.False(page.Shows(Array.IndexOf(names, "[Y]_[TS]_[B-01-S]")));
        Assert.True(page.Shows(Array.IndexOf(names, "Road 1 middle")));
    }

    [Fact]
    public void TheTabsComeNineAtATimeInChapterOrder()
    {
        var codes = new[] { "CME", "CMS", "CP", "CS", "CW", "FF", "FM", "FRC", "FRS", "GF", "HB", "IME", "IMW", "MB", "MF", "OR", "OWC", "OWN", "SM", "SW" };
        var (graph, names) = LoopGraph();
        var sources = codes.Select(c => new SchematicSource($"[{c}-A]", c, c, "A", "OVERVIEW", null, false, new SchematicTile[0], new SchematicBadge[0], new SchematicSwitch[0])).ToList();
        var book = Booklet.Booklet.Build(sources, graph, t => names[t]);
        Assert.Equal(3, book.TabSets(9));
        Assert.Equal(codes.Take(9), book.TabSet(0, 9).Select(s => s.Code));
        Assert.Equal(new[] { "SM", "SW" }, book.TabSet(2, 9).Skip(0).Select(s => s.Code).TakeLast(2));
        Assert.Equal(1, book.SetOf("HB", 9));
        Assert.Equal(0, book.SetOf("XX", 9));
    }

    private static (string Booklet, string Network)? RealPaths
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("AIE_BOOKLET_JSON");
            var n = Environment.GetEnvironmentVariable("AIE_NETWORK_JSON");
            return b != null && n != null && File.Exists(b) && File.Exists(n) ? (b, n) : null;
        }
    }

    [Fact]
    public void EveryPageOfTheRealBookletIsReadAsTrack()
    {
        if (RealPaths is not var (bookletPath, networkPath))
            return;
        var sources = LoadSources(bookletPath);
        var (graph, names, _) = RealNetworkTests.Load(networkPath);
        var watch = Stopwatch.StartNew();
        var book = Booklet.Booklet.Build(sources, graph, t => names[t]);
        watch.Stop();
        output.WriteLine($"{sources.Count} pages in {watch.ElapsedMilliseconds} ms: {book.Stats}");
        foreach (var p in book.Stats.Problems.Take(40))
            output.WriteLine("  " + p);
        foreach (var page in book.Pages)
        {
            var unmatched = page.Runs.Where(r => !r.Matched).ToList();
            if (unmatched.Count > 0)
                output.WriteLine($"{page}: {unmatched.Count} of {page.Runs.Count} runs unmatched: {string.Join("; ", unmatched.Take(4).Select(r => r.Drawn))}");
        }
        // Every yard track the booklet should show: on some page, as one of a run's tracks.
        var yardTracks = Enumerable.Range(0, graph.TrackCount).Where(t => YardTrackName.TryParse(names[t], out _)).ToList();
        var drawn = new HashSet<int>(book.Pages.SelectMany(p => p.Tracks));
        var missing = yardTracks.Where(t => !drawn.Contains(t)).Select(t => names[t]).ToList();
        output.WriteLine($"yard tracks drawn: {yardTracks.Count - missing.Count}/{yardTracks.Count}; not drawn: {string.Join(", ", missing.Take(60))}");
        // A track is one run's on a page, never two runs'.
        foreach (var page in book.Pages)
        {
            var twice = page.Runs.Where(r => !r.IsCapsule).SelectMany(r => r.Path.Select(p => p.Track).Distinct()).GroupBy(t => t).Where(g => g.Count() > 1).Select(g => names[g.Key]).ToList();
            if (twice.Count > 0)
                output.WriteLine($"{page}: tracks in two runs: {string.Join(", ", twice.Take(8))}");
        }
        var dir = Environment.GetEnvironmentVariable("AIE_PREVIEW_DIR");
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            foreach (var page in book.Pages)
            {
                File.WriteAllText(Path.Combine(dir, $"book-{page.Source.Name.Trim('[', ']')}.svg"), Svg(page, names));
                var runs = page.Runs.Select(r => r.Drawn.Points.Select(p => new[] { p.X, p.Z }).ToArray()).ToArray();
                var box = new[] { page.Box.Left, page.Box.Top, page.Box.Right, page.Box.Bottom };
                File.WriteAllText(Path.Combine(dir, $"runs-{page.Source.Name.Trim('[', ']')}.json"), JsonSerializer.Serialize(new { box, runs }));
            }
        }
        Assert.Equal(20, book.Stations.Count);
        Assert.Equal(book.Stats.Switches, book.Stats.SwitchesNamed);
        // All but a page's odd decoration matched; every badge names a track its run has; no page drawn mirrored.
        Assert.True(book.Stats.Matched >= book.Stats.Runs * 0.99, book.Stats.ToString());
        Assert.Equal(book.Stats.BadgesChecked, book.Stats.BadgesAgree);
        Assert.Equal(book.Stats.LegsSettled, book.Stats.SideAgrees);
    }

    /// <summary>A page as the tests see it: each run in a color by whether it matched, labeled with its tracks' names.</summary>
    private static string Svg(BookletPage page, string[] names)
    {
        var b = page.Box;
        double pad = 60, w = b.Width + 2 * pad, h = b.Height + 2 * pad;
        var sb = new StringBuilder();
        string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{F(b.Left - pad)} {F(b.Top - pad)} {F(w)} {F(h)}' width='{F(w * 1.5)}' height='{F(h * 1.5)}' font-family='sans-serif'>");
        sb.Append($"<rect x='{F(b.Left - pad)}' y='{F(b.Top - pad)}' width='{F(w)}' height='{F(h)}' fill='#e4eaec'/>");
        sb.Append($"<text x='{F(b.Left - pad + 6)}' y='{F(b.Top - pad + 16)}' font-size='14'>{page.Source.Name} {page.Title}</text>");
        var palette = new[] { "#1b5e20", "#0d47a1", "#4a148c", "#b71c1c", "#e65100", "#006064", "#3e2723", "#33691e" };
        var i = 0;
        foreach (var r in page.Runs)
        {
            var pts = string.Join(" ", r.Drawn.Points.Select(p => $"{F(p.X)},{F(-p.Z)}"));
            var color = !r.Matched ? "#ff00ff" : r.IsCapsule ? "#c08030" : palette[i++ % palette.Length];
            sb.Append($"<polyline points='{pts}' fill='none' stroke='{color}' stroke-width='{(r.Matched ? 3 : 5)}' stroke-linejoin='round'/>");
            if (r.Matched && !r.IsCapsule)
            {
                var named = r.Path.Select(p => names[p.Track]).FirstOrDefault(n => YardTrackName.TryParse(n, out _));
                var label = named != null && YardTrackName.TryParse(named, out var yn) ? yn.Short : $"{r.Path.Count}t {r.PathLength:F0}m";
                var (at, _) = r.Drawn.PointAt(r.Drawn.Length * 0.3);
                sb.Append($"<text x='{F(at.X)}' y='{F(-at.Z - 4)}' font-size='9' fill='{color}'>{label}</text>");
            }
        }
        foreach (var s in page.Schematic.Switches)
            sb.Append($"<circle cx='{F(s.X)}' cy='{F(-s.Y)}' r='6' fill='{(s.Junction >= 0 ? "#b05050" : "#ff00ff")}' opacity='0.7'/>");
        foreach (var bd in page.Source.Badges)
            sb.Append($"<text x='{F(bd.X - 6)}' y='{F(-bd.Y + 5)}' font-size='12' font-weight='bold'>{System.Security.SecurityElement.Escape(bd.Text)}{bd.Kind}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
