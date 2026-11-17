using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Booklet;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using DV.Logic.Job;
using DV.Utils;
using UnityEngine;
using static AIEngineer.UI.Tablet.TabletSkin;

namespace AIEngineer.UI.Tablet
{
    /// <summary>
    /// The tablet's Map page: the game's station booklet, page by page (the
    /// route map, a station's overview, its yards), drawn by the game's own
    /// render (<see cref="BookletRenderer"/>) with the cars, the locos, the
    /// chosen loco's route, and each switch's setting over it. Station tabs
    /// down the left, nine at a time; down the right, zoom, Map, the
    /// station's plate (▲ up from a yard), its yards' letters, Show, and the
    /// arrows. A press on a car, a loco, or a track picks it and brings its
    /// orders up over the bottom of the map; a drag moves the page, never
    /// off it. The page is drawn again when it, its view, or what is on it
    /// changes, and twice a second at most while trains move.
    /// </summary>
    internal static class TabletMap
    {
        private const float MapX = 12, MapY = 84, MapW = 1056, MapH = 828;
        private const int Per = 9;

        /// <summary>Rows of yard letters the column keeps, two a row: the stock booklet's most is the Harbor's six (B to G).</summary>
        private const int LetterRows = 3;
        private const float PressRadius = 22;
        private const float RenderSeconds = 0.5f;

        // The part of the map a page is fitted into: clear of the tabs, the column, the title, and the strip.
        private static readonly PageBox FitArea = new PageBox(136, 60, MapW - 152, MapH - 84);

        private static PageRef at = PageRef.Route;
        private static bool placed;
        private static PageView view, fit;
        private static PageBox content;
        private static int tabSet;
        private static RenderTexture texture;
        private static bool dirty = true;
        private static float nextRender;
        private static Pick pick;
        private static readonly List<Verb> verbs = new List<Verb>();
        private static bool after;
        private static readonly List<DrawnCar> cars = new List<DrawnCar>();
        private static readonly List<(int Track, double From, double To)> spans = new List<(int, double, double)>();
        // The bottom's panel or strip, as last drawn: a press there is not on the map.
        private static Rect bottomShown;

        private sealed class DrawnCar
        {
            public TrainCar Car;
            public Color Color;
            public readonly List<(int Track, double From, double To)> Spans = new List<(int, double, double)>();
            public readonly List<(BookletRun Run, double From, double To)> Parts = new List<(BookletRun, double, double)>();
        }

        private enum PickKind
        {
            Track,
            Cut,
            Loco,
        }

        private sealed class Pick
        {
            public PickKind Kind;
            public TrackPosition At;
            public string Name;
            public Cut Cut;
            public TrainCar Loco;
        }

        private sealed class Verb
        {
            public string Label;
            public Action Do;
        }

        /// <summary>The next time the map is drawn, it opens on the chosen loco's page.</summary>
        public static void ShowChosenNext()
        {
            placed = false;
            dirty = true;
        }

        public static void Unpick()
        {
            pick = null;
            verbs.Clear();
            after = false;
            dirty = true;
        }

        /// <summary>The booklet's copies put away while no tablet shows the map (from the host's LateUpdate).</summary>
        public static void PutAwayIfUnseen()
        {
            if (Time.unscaledTime - shownAt > 3f)
                BookletRenderer.Hide();
        }

        private static float shownAt = -1000;

        private static Rect S(float x, float y, float w, float h) => new Rect(MapX + x, MapY + y, w, h);

        // ---- For the self-test ----

        /// <summary>What the map shows, for the self-test's log.</summary>
        public static string Describe()
        {
            var book = BookletReader.Get(Host.Network);
            var page = book != null ? PageOf(book) : null;
            return $"page {page?.Source.Name ?? "none"}, view {view}, pick {(pick != null ? pick.Kind + " " + pick.Name : "none")}, {verbs.Count} orders: {string.Join(" | ", verbs.Select(v => v.Label))}";
        }

#if !PUBLIC_RELEASE
        public static void OpenForTest(PageRef page)
        {
            var book = BookletReader.Get(Host.Network);
            if (book == null || Host.World == null)
                return;
            placed = true;
            try
            {
                Open(book, page, Host.World);
            }
            catch (TabletSurface.Redraw)
            {
            }
            dirty = true;
        }
#endif

#if !PUBLIC_RELEASE
        /// <summary>Presses one of the pick's orders, as on the panel.</summary>
        public static string DoForTest(int index)
        {
            if (pick == null || index >= verbs.Count)
                return null;
            var v = verbs[index];
            try
            {
                v.Do();
            }
            catch (TabletSurface.Redraw)
            {
            }
            Unpick();
            return v.Label;
        }
#endif

        /// <summary>The set of station tabs shown (nine a set).</summary>
        public static int TabSetShown => tabSet;

        /// <summary>The page shown, by the render's name ("[OWN-B]").</summary>
        public static string PageShown => Host.Network != null ? BookletReader.Get(Host.Network)?.Page(at)?.Source.Name : null;

        /// <summary>Where on the canvas a car is drawn, if the page shown draws it (the self-test presses there).</summary>
        public static Vector2? CanvasPointOf(TrainCar car)
        {
            foreach (var c in cars)
            {
                if (c.Car != car || c.Parts.Count == 0)
                    continue;
                var (run, a, b) = c.Parts[0];
                var (pt, _) = run.Drawn.PointAt((a + b) / 2);
                var (x, y) = view.ToScreen(pt.X, -pt.Z, MapW / 2, MapH / 2);
                return new Vector2(MapX + (float)x, MapY + (float)y);
            }
            return null;
        }

        /// <summary>The station the map shows, or null on the route map.</summary>
        public static string StationShown => Host.Network != null ? BookletReader.Get(Host.Network)?.Page(at)?.Station : null;

#if !PUBLIC_RELEASE
        /// <summary>Opens the page a car stands on and picks it, as a press on it would.</summary>
        public static bool PickForTest(TrainCar car)
        {
            var book = BookletReader.Get(Host.Network);
            var world = Host.World;
            var p = book != null && world != null ? PositionOf(world, car) : null;
            if (!p.HasValue)
                return false;
            var page = book.PageFor(p.Value);
            if (page == null || page.IsRoute)
                return false;
            OpenForTest(book.Find(page.Station)?.Overview == page ? PageRef.Overview(page.Station) : PageRef.OfYard(page.Station, page.Yard));
            try
            {
                PickCar(world, car, p.Value);
            }
            catch (TabletSurface.Redraw)
            {
            }
            dirty = true;
            return pick != null;
        }
#endif

        // ---- Pages ----

        private static BookletPage PageOf(Booklet book) => book.Page(at) ?? book.Route ?? book.Stations.FirstOrDefault()?.Overview;

        /// <summary>Where a loco or car stands on the network: the middle of its first span.</summary>
        private static TrackPosition? PositionOf(World world, TrainCar car)
        {
            spans.Clear();
            world.SpansOf(car, spans);
            if (spans.Count == 0)
                return null;
            var (t, a, b) = spans[0];
            return new TrackPosition(t, (a + b) / 2);
        }

        /// <summary>The station the column's plates are for: the page's, or on the route map the chosen loco's (the nearest).</summary>
        private static BookletStation StationFor(Booklet book, BookletPage page, World world)
        {
            if (page.Station != null)
                return book.Find(page.Station);
            return Nearest(book, world, TabletUi.Chosen) ?? book.Stations.FirstOrDefault();
        }

        /// <summary>The station a loco is at: the one whose page draws its track, else the nearest station office.</summary>
        private static BookletStation Nearest(Booklet book, World world, TrainCar loco)
        {
            if (loco == null)
                return null;
            var p = PositionOf(world, loco);
            var code = p.HasValue ? book.PageFor(p.Value)?.Station : null;
            if (code != null)
                return book.Find(code);
            BookletStation best = null;
            var bestD = float.MaxValue;
            foreach (var st in StationController.allStations)
            {
                var info = st != null ? st.stationInfo : null;
                var found = info != null ? book.Find(info.YardID) : null;
                if (found == null)
                    continue;
                var d = (st.transform.position - loco.transform.position).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = found;
                }
            }
            return best;
        }

        private static void Open(Booklet book, PageRef page, World world)
        {
            at = page;
            var p = PageOf(book);
            content = Padded(p.Box);
            fit = PageView.Fit(content, FitArea, MapW, MapH, 0, 0, 3);
            view = fit;
            var station = StationFor(book, p, world);
            if (station != null)
                tabSet = book.SetOf(station.Code, Per);
            Unpick();
        }

        private static PageBox Padded(PageBox b) => new PageBox(b.Left - 30, b.Top - 30, b.Right + 30, b.Bottom + 30);

        /// <summary>The page a loco stands on (its yard's if one draws it), fitted, centred on it if that leaves the page in view.</summary>
        private static void Show(Booklet book, World world, TrainCar loco)
        {
            var p = loco != null ? PositionOf(world, loco) : null;
            if (!p.HasValue)
                return;
            var page = book.PageFor(p.Value);
            if (page == null)
                return;
            // Out on the line: the nearest station's overview.
            var near = page.IsRoute ? Nearest(book, world, loco) : null;
            if (near != null)
                Open(book, PageRef.Overview(near.Code), world);
            else if (page.IsRoute)
                Open(book, PageRef.Route, world);
            else if (book.Find(page.Station)?.Overview == page)
                Open(book, PageRef.Overview(page.Station), world);
            else
                Open(book, PageRef.OfYard(page.Station, page.Yard), world);
        }

        // ---- Drawing the page into its texture (from LateUpdate) ----

        /// <summary>Draws the page into the map's texture, if it changed or its time has come.</summary>
        public static void RenderIfNeeded()
        {
            shownAt = Time.unscaledTime;
            var network = Host.Network;
            var world = Host.World;
            var book = BookletReader.Get(network);
            if (book == null || world == null)
                return;
            if (!placed)
            {
                placed = true;
                if (TabletUi.Chosen != null)
                    Show(book, world, TabletUi.Chosen);
                if (PageOf(book) == null || content.Width <= 0)
                    Open(book, PageRef.Route, world);
            }
            int w = (int)MapW, h = (int)MapH;
            if (texture == null || texture.width != w || texture.height != h)
            {
                if (texture != null)
                {
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                }
                texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "AIEngineer tablet map" };
                dirty = true;
            }
            if (!dirty && Time.unscaledTime < nextRender)
                return;
            dirty = false;
            nextRender = Time.unscaledTime + RenderSeconds;
            var page = PageOf(book);
            if (page == null)
                return;
            ReadCars(world, page);
            var chosen = TabletUi.Chosen != null ? Host.SessionFor(TabletUi.Chosen, false) : null;
            BookletRenderer.Render(page, view.CenterX, -view.CenterY, view.Scale, texture, px => Overlay(page, network, chosen, px));
        }

        private static readonly Color RouteColor = new Color(1f, 0.74f, 0.1f, 0.95f);
        private static readonly Color PickColor = new Color(0.88f, 0.75f, 0.42f, 0.85f);
        private static readonly Color SetColor = new Color(0.12f, 0.58f, 0.22f, 0.95f);
        private static readonly Color LocoColor = Hex("efe6cf");
        private static readonly Color LocoEdge = Hex("1d160c");
        private static readonly Color CarEdge = new Color(0.08f, 0.07f, 0.06f, 0.9f);

        /// <summary>Every car the page draws a track of, and where on the page it stands.</summary>
        private static void ReadCars(World world, BookletPage page)
        {
            cars.Clear();
            var spawner = CarSpawner.Instance;
            if (spawner == null)
                return;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            foreach (var car in spawner.AllCars)
            {
                if (car == null)
                    continue;
                spans.Clear();
                world.SpansOf(car, spans);
                DrawnCar drawn = null;
                foreach (var (t, a, b) in spans)
                {
                    if (!page.Shows(t))
                        continue;
                    if (drawn == null)
                        drawn = new DrawnCar { Car = car, Color = ColorOf(car, jobs) };
                    drawn.Spans.Add((t, a, b));
                    foreach (var part in page.Stretch(t, a, b))
                        drawn.Parts.Add(part);
                }
                if (drawn != null)
                    cars.Add(drawn);
            }
        }

        private static Color ColorOf(TrainCar car, JobsManager jobs)
        {
            if (TabletUi.IsPower(car))
                return LocoColor;
            Job job = null;
            try
            {
                job = jobs != null && car.logicCar != null ? jobs.GetJobOfCar(car.logicCar) : null;
            }
            catch (Exception)
            {
                // A car between jobs.
            }
            if (job == null)
                return Hex("8a8478");
            switch (job.jobType)
            {
                case DV.ThingTypes.JobType.Transport:
                case DV.ThingTypes.JobType.ComplexTransport:
                    return FreightHaul;
                case DV.ThingTypes.JobType.EmptyHaul:
                    return LogisticHaul;
                case DV.ThingTypes.JobType.ShuntingLoad:
                    return ShuntLoad;
                case DV.ThingTypes.JobType.ShuntingUnload:
                    return ShuntUnload;
                default:
                    return Hex("8a8478");
            }
        }

        /// <summary>What the tablet draws over the page: the pick, the chosen loco's route, the cars, and the switches' settings.</summary>
        private static void Overlay(BookletPage page, Network network, Session chosen, Func<double, double, Vector2> px)
        {
            const float k = 1;
            void Along(BookletRun run, double a, double b, float width, Color color)
            {
                var line = run.Drawn.Between(a, b);
                for (var i = 1; i < line.Length; i++)
                    BookletRenderer.Line(px(line[i - 1].X, line[i - 1].Z), px(line[i].X, line[i].Z), width, color);
            }
            // The pick, under everything: a track's stretch, or the cars of a cut or a loco's train.
            if (pick != null)
            {
                if (pick.Kind == PickKind.Track)
                {
                    foreach (var (run, a, b) in page.Stretch(pick.At.Track, 0, network.Graph.Length(pick.At.Track)))
                        Along(run, a, b, 18 * k, PickColor);
                }
                else
                {
                    var set = pick.Kind == PickKind.Cut ? pick.Cut?.Set : pick.Loco?.trainset;
                    foreach (var c in cars)
                        if (set != null && c.Car.trainset == set)
                            foreach (var (run, a, b) in c.Parts)
                                Along(run, a, b, 22 * k, PickColor);
                }
            }
            // The chosen loco's route, from the leg it is on.
            var route = chosen?.Engineer.Route;
            if (route != null)
            {
                for (var l = Math.Max(0, chosen.Engineer.LegIndex); l < route.Legs.Count; l++)
                    foreach (var piece in route.Legs[l].Pieces)
                        foreach (var (run, a, b) in page.Stretch(piece.Track, piece.From, piece.To))
                            Along(run, a, b, 6 * k, RouteColor);
            }
            // Each switch's setting: a bar along the leg it is set to.
            var switches = page.Schematic.Switches;
            for (var i = 0; i < switches.Count; i++)
            {
                var s = switches[i];
                if (s.Junction < 0 || s.Junction >= network.Junctions.Length || network.Junctions[s.Junction] == null)
                    continue;
                var leg = page.LegOf(i, network.Junctions[s.Junction].selectedBranch);
                if (leg == null)
                    continue;
                var d = s.LegDirections[(int)leg.Value];
                BookletRenderer.Line(px(s.Point.X, s.Point.Z), px(s.Point.X + d.X * 22, s.Point.Z + d.Z * 22), 6 * k, SetColor);
            }
            // The cars: each a short bar along its track, a little gap at each end; locos light with a dark edge.
            foreach (var c in cars)
            {
                var chosenOne = TabletUi.Chosen != null && c.Car == TabletUi.Chosen;
                foreach (var (run, a, b) in c.Parts)
                {
                    var perMeter = run.PathLength > 0 ? run.Drawn.Length / run.PathLength : 0;
                    var gap = Math.Min(0.6 * perMeter, Math.Abs(b - a) * 0.2);
                    double lo = Math.Min(a, b) + gap, hi = Math.Max(a, b) - gap;
                    var power = c.Color == LocoColor;
                    Along(run, lo, hi, (power ? 16 : 13) * k, power ? (chosenOne ? Brass2 : LocoEdge) : CarEdge);
                    Along(run, lo, hi, (power ? 11 : 9) * k, power && chosenOne ? Brass2 : c.Color);
                }
            }
        }

        // ---- The page on the tablet ----

        public static void Draw(World world)
        {
            var book = BookletReader.Get(Host.Network);
            if (book == null)
            {
                Box(S(0, 0, MapW, MapH), Well);
                Label(S(24, 24, MapW - 48, 60), "The station booklet couldn't be read (see the mod's log).", SoftText);
                return;
            }
            // The page drawn now, in this pass, so a press that opens a page shows it at once.
            RenderIfNeeded();
            var page = PageOf(book);
            Box(S(0, 0, MapW, MapH), Well);
            if (texture != null && placed)
                Picture(S(0, 0, MapW, MapH), texture);
            if (page == null)
                return;
            DrawMarks(world, page);
            DrawTitle(page);
            DrawTabs(book, page, world);
            DrawColumn(book, page, world);
            if (pick != null)
                bottomShown = DrawPanel(world);
            else
            {
                bottomShown = S(0, MapH - 60, MapW, 60);
                TabletUi.DrawLast(bottomShown);
            }
            // Between the tabs and the column, above the bottom: no plate there.
            PressArea = S(128, 0, MapW - 128 - 152, bottomShown.y - MapY - 4);
        }

        private static void DrawTitle(BookletPage page)
        {
            var title = page.IsRoute ? "Route map" : $"{page.Source.StationName} · {page.Station}-{page.Yard} · {Title(page.Title)}";
            Label(S(136, 16, 760, 40), title, TitleLook);
        }

        /// <summary>The title in dark brown, not the soft beige: legible on the map's light, flat background (as the Comms Tablet's map).</summary>
        private static Look TitleLook => titleLook ?? (titleLook = Section.With(TextAnchor.MiddleLeft, true).Tinted(Hex("4b3a29")));

        private static Look titleLook;

        private static string Title(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

        /// <summary>The locos' numbers by them, and the chosen loco's orders where they end.</summary>
        private static void DrawMarks(World world, BookletPage page)
        {
            foreach (var c in cars)
            {
                if (!c.Car.IsLoco || c.Parts.Count == 0)
                    continue;
                var (run, a, b) = c.Parts[0];
                var (pt, _) = run.Drawn.PointAt((a + b) / 2);
                var (x, y) = view.ToScreen(pt.X, -pt.Z, MapW / 2, MapH / 2);
                if (x < 0 || y < 0 || x > MapW || y > MapH)
                    continue;
                var on = c.Car == TabletUi.Chosen;
                var r = S((float)x - 60, (float)y - 46, 120, 30);
                Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), Tint(c.Car.ID, Color.black), Centered(Small));
                Label(r, Tint(c.Car.ID, on ? Brass2 : Cream), Centered(Small));
            }
            var chosen = TabletUi.Chosen != null ? Host.SessionFor(TabletUi.Chosen, false) : null;
            if (chosen == null)
                return;
            var queue = chosen.Engineer.Queue;
            for (var i = 0; i < queue.Count; i++)
            {
                var p = page.PointOf(queue[i].Position);
                if (p == null)
                    continue;
                var (x, y) = view.ToScreen(p.Value.At.X, p.Value.At.Z, MapW / 2, MapH / 2);
                if (x < 0 || y < 0 || x > MapW || y > MapH)
                    continue;
                Lamp(MapX + (float)x - 17, MapY + (float)y - 17, 34, Ink);
                Lamp(MapX + (float)x - 14, MapY + (float)y - 14, 28, i == 0 ? Brass2 : Brass);
                Label(S((float)x - 17, (float)y - 17, 34, 34), Tint((i + 1).ToString(), Ink), Centered(Small));
            }
        }

        /// <summary>Nine station tabs at a time, in the booklet's order, with ▲ and ▼ for the sets before and after.</summary>
        private static void DrawTabs(Booklet book, BookletPage page, World world)
        {
            var sets = book.TabSets(Per);
            tabSet = Mathf.Clamp(tabSet, 0, Math.Max(0, sets - 1));
            if (Button(S(16, 16, 104, 52), "▲", Plate, tabSet > 0))
            {
                tabSet--;
                Changed();
            }
            var list = book.TabSet(tabSet, Per);
            for (var i = 0; i < Per; i++)
            {
                if (i >= list.Count)
                    continue;
                var st = list[i];
                var r = S(16, 16 + 58 * (i + 1), 104, 52);
                var onOverview = !page.IsRoute && page.Station == st.Code && st.Overview == page;
                var inYard = !page.IsRoute && page.Station == st.Code && !onOverview;
                if (Button(r, onOverview ? Tint(st.Code, Brass2) : inYard ? Tint(st.Code, Brass2) : st.Code, onOverview ? Here : inYard ? PlateOn : Plate) && !onOverview)
                    Nav(book, PageRef.Overview(st.Code), world);
            }
            if (Button(S(16, 16 + 58 * (Per + 1), 104, 52), "▼", Plate, tabSet < sets - 1))
            {
                tabSet++;
                Changed();
            }
        }

        private static void Nav(Booklet book, PageRef page, World world)
        {
            Open(book, page, world);
            Changed();
        }

        /// <summary>The column down the right edge; every plate in the same place on every page.</summary>
        private static void DrawColumn(Booklet book, BookletPage page, World world)
        {
            const float x = MapW - 16 - 128;
            if (Button(S(x, 16, 60, 60), "+", Plate, view.Scale < PageView.MaxScale - 1e-6))
                Zoom(2, MapW / 2, MapH / 2);
            if (Button(S(x + 68, 16, 60, 60), "–", Plate, view.Scale > fit.Scale + 1e-6))
                Zoom(0.5, MapW / 2, MapH / 2);
            if (Button(S(x, 82, 128, 72), page.IsRoute ? Tint("Map", Brass2) : "Map", page.IsRoute ? Here : Plate) && !page.IsRoute)
                Nav(book, PageRef.Route, world);
            var station = StationFor(book, page, world);
            if (station != null)
            {
                var onOverview = page.Station == station.Code && station.Overview == page;
                var inYard = page.Station == station.Code && !onOverview;
                var plate = Two(onOverview ? Tint(station.Code, Brass2) : station.Code, inYard ? "▲ up" : "overview");
                if (Button(S(x, 160, 128, 72), plate, onOverview ? Here : Plate) && !onOverview)
                    Nav(book, PageRef.Overview(station.Code), world);
                // Its yards' letters, two a row; the one shown is lit, and a press on it goes up a level.
                var yards = station.Yards.ToList();
                for (var i = 0; i < Math.Min(2 * LetterRows, yards.Count); i++)
                {
                    var y = yards[i];
                    var on = page == y;
                    var r = S(x + (i % 2) * 68, 238 + (i / 2) * 66, 60, 60);
                    if (Button(r, on ? Tint(y.Yard, Brass2) : y.Yard, on ? PlateOn : Plate))
                        Nav(book, on ? PageRef.Overview(station.Code) : PageRef.OfYard(station.Code, y.Yard), world);
                }
            }
            var chosen = TabletUi.Chosen;
            if (Button(S(x, 238 + LetterRows * 66, 128, 72), $"<size=23>{Tint("Show", Soft)}</size>\n<b>{(chosen != null ? chosen.ID : "—")}</b>", Plate, chosen != null))
            {
                Show(book, world, chosen);
                Changed();
            }
            var ay = 238 + LetterRows * 66 + 78;
            if (Button(S(x, ay, 60, 60), "◀", Plate))
                Step(-1, 0);
            if (Button(S(x + 68, ay, 60, 60), "▶", Plate))
                Step(1, 0);
            if (Button(S(x, ay + 66, 60, 60), "▲", Plate))
                Step(0, -1);
            if (Button(S(x + 68, ay + 66, 60, 60), "▼", Plate))
                Step(0, 1);
        }

        /// <summary>A step with an arrow: three fifths of what the map shows.</summary>
        private static void Step(int dx, int dy)
        {
            var wd = (FitArea.Width) / view.Scale * 0.6;
            var ht = (FitArea.Height) / view.Scale * 0.6;
            view = new PageView(view.CenterX + dx * wd, view.CenterY + dy * ht, view.Scale).ClampTo(fit, content);
            dirty = true;
            Changed();
        }

        /// <summary>Zooms about a point of the map (map pixels), the page point under it staying put.</summary>
        private static void Zoom(double factor, double lx, double ly, bool exit = true)
        {
            var (px, py) = view.ToPage(lx, ly, MapW / 2, MapH / 2);
            var z = view.Zoomed(factor).ClampTo(fit, content);
            view = new PageView(px - (lx - MapW / 2) / z.Scale, py - (ly - MapH / 2) / z.Scale, z.Scale).ClampTo(fit, content);
            dirty = true;
            if (exit)
                Changed();
        }

        // ---- Presses and drags on the map ----

        /// <summary>The part of the map no plate is over (canvas pixels), where a press picks on the map; empty off the map page.</summary>
        public static Rect PressArea { get; private set; }

        /// <summary>A press on the map, at a canvas point: picks what is there, or opens a station or a yard.</summary>
        public static void PressAt(Vector2 canvas)
        {
            var book = BookletReader.Get(Host.Network);
            var world = Host.World;
            if (book == null || world == null)
                return;
            var page = PageOf(book);
            if (page == null)
                return;
            var lx = canvas.x - MapX;
            var ly = canvas.y - MapY;
            // A press on the tabs, the column, or the panel is theirs, between their plates too.
            if (lx < 0 || ly < 0 || lx > MapW || ly > MapH || bottomShown.Contains(canvas)
                || S(16, 16, 104, 58 * (Per + 2)).Contains(canvas) || S(MapW - 16 - 128, 16, 128, 630).Contains(canvas))
                return;
            try
            {
                Press(book, world, page, lx, ly);
            }
            catch (TabletSurface.Redraw)
            {
            }
            dirty = true;
        }

        private static void Press(Booklet book, World world, BookletPage page, float lx, float ly)
        {
            var (px, py) = view.ToPage(lx, ly, MapW / 2, MapH / 2);
            var radius = PressRadius / view.Scale;
            if (page.IsRoute)
            {
                // A station's badge opens its overview.
                SchematicBadge best = null;
                var bestD = radius * 1.6;
                foreach (var b in page.Source.Badges)
                {
                    var d = Math.Sqrt((b.X - px) * (b.X - px) + (-b.Y - py) * (-b.Y - py));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = b;
                    }
                }
                var code = best?.Text.Replace("\n", "").Replace(" ", "");
                if (code != null && book.Find(code) != null)
                {
                    Nav(book, PageRef.Overview(code), world);
                    return;
                }
            }
            var tp = page.TrackAt(px, py, radius);
            if (tp.HasValue)
            {
                var car = CarAt(tp.Value);
                if (car != null)
                {
                    PickCar(world, car, tp.Value);
                    return;
                }
            }
            var capsule = page.CapsuleAt(px, py, radius);
            if (capsule != null && page.Station != null && book.Find(page.Station)?.Find(capsule) != null)
            {
                Nav(book, PageRef.OfYard(page.Station, capsule), world);
                return;
            }
            if (tp.HasValue)
            {
                PickTrack(world, tp.Value);
                return;
            }
            Unpick();
        }

        private static TrainCar CarAt(TrackPosition p)
        {
            foreach (var c in cars)
                foreach (var (t, a, b) in c.Spans)
                    if (t == p.Track && p.Offset >= Math.Min(a, b) - 1 && p.Offset <= Math.Max(a, b) + 1)
                        return c.Car;
            return null;
        }

        // ---- Picks and their orders ----

        private static void Set(Pick p, IEnumerable<Verb> list)
        {
            pick = p;
            verbs.Clear();
            verbs.AddRange(list);
            dirty = true;
        }

        private static void PickTrack(World world, TrackPosition p)
        {
            var network = world.Network;
            var named = Dispatch.Yards(network, Vector2.zero).SelectMany(y => y.Tracks).FirstOrDefault(t => t.Track == p.Track);
            var name = named != null ? named.Id : network.Describe(p.Track);
            var list = new List<Verb>();
            if (named != null)
            {
                list.Add(new Verb { Label = "Go to", Do = () => TabletUi.Order($"Go to {named.Id}", after, s => Dispatch.GoToTrack(s, world, named, after, Arrival.CarsFirstIntoDeadEnd)) });
                list.Add(new Verb { Label = "Go to, cars first", Do = () => TabletUi.Order($"Go to {named.Id} (cars first)", after, s => Dispatch.GoToTrack(s, world, named, after, Arrival.CarsFirst)) });
                list.Add(new Verb { Label = "Go to, loco first", Do = () => TabletUi.Order($"Go to {named.Id} (loco first)", after, s => Dispatch.GoToTrack(s, world, named, after, Arrival.LocoFirst)) });
                // Its leading end at the spot pressed, not the whole train onto the track.
                list.Add(new Verb { Label = "Stop here", Do = () => TabletUi.Order($"Stop on {name}", after, s => Give(s, new Waypoint(p) { Label = name })) });
            }
            else
            {
                list.Add(new Verb { Label = "Go here", Do = () => TabletUi.Order($"Go to {name}", after, s => Give(s, new Waypoint(p) { Label = name })) });
            }
            var n = TabletUi.CarsOf(TabletUi.Chosen);
            if (n > 0)
                list.Add(new Verb { Label = $"Leave my {Plural(n, "car")} here", Do = () => TabletUi.Order($"Leave {Plural(n, "car")} on {name}", after, s => Give(s, new Waypoint(p, WaypointAction.Uncouple) { CarCount = n, Label = name })) });
            var room = named != null ? $", {named.Length:F0} m{(named.DeadEnd ? ", stub" : "")}" : "";
            Set(new Pick { Kind = PickKind.Track, At = p, Name = $"<b>{name}</b>{room}" }, list);
            Changed();
        }

        private static void Give(Session s, Waypoint wp)
        {
            if (after)
                s.Engineer.Enqueue(wp);
            else
                s.Engineer.GoTo(wp);
        }

        private static void PickCar(World world, TrainCar car, TrackPosition p)
        {
            var loco = Host.LocoOf(car);
            if (loco != null)
            {
                PickLoco(loco);
                return;
            }
            var cut = Dispatch.Cuts(world, Dispatch.MapPosition(car), 100, null).FirstOrDefault(c => c.Set == car.trainset);
            if (cut == null)
            {
                PickTrack(world, p);
                return;
            }
            var network = world.Network;
            var here = network.MapPointAt(p);
            var near = (network.MapPointAt(cut.Ends[0].Position) - here).sqrMagnitude <= (network.MapPointAt(cut.Ends[1].Position) - here).sqrMagnitude ? 0 : 1;
            var list = new List<Verb>();
            foreach (var end in new[] { near, 1 - near })
            {
                var side = end == 0 ? CutSide.End0 : CutSide.End1;
                var label = $"Couple, {cut.EndNames[end]} end";
                list.Add(new Verb { Label = label, Do = () => TabletUi.Order($"Couple to {cut.Short}, {cut.EndNames[end]} end", after, s => Dispatch.PickUp(s, world, cut, after, side)) });
            }
            // Its cars' jobs the player holds: one press hands one over.
            var held = Jobs.Taken();
            foreach (var job in JobsOf(cut.Set).Where(j => held.Contains(j)).Take(3))
            {
                var j = job;
                list.Add(new Verb { Label = $"Work {j.ID}", Do = () => WorkJobs(world, new List<Job> { j }) });
            }
            Set(new Pick { Kind = PickKind.Cut, At = p, Cut = cut, Name = $"<b>{cut.Short}</b>{(cut.Jobs != null ? ", " + cut.Jobs : "")}" }, list);
            Changed();
        }

        private static IEnumerable<Job> JobsOf(Trainset set)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var seen = new HashSet<Job>();
            if (jobs == null || set == null)
                yield break;
            foreach (var car in set.cars)
            {
                Job job = null;
                try
                {
                    job = car?.logicCar != null ? jobs.GetJobOfCar(car.logicCar) : null;
                }
                catch (Exception)
                {
                }
                if (job != null && seen.Add(job))
                    yield return job;
            }
        }

        /// <summary>Hands jobs to the chosen loco, planned together (<see cref="JobRun"/>).</summary>
        public static void WorkJobs(World world, List<Job> jobs, bool? queue = null)
        {
            var views = new List<JobView>();
            foreach (var job in jobs)
            {
                try
                {
                    views.Add(Jobs.Read(world, job));
                }
                catch (Exception e)
                {
                    Main.Warn($"tablet: couldn't read job {job.ID}: {e.GetType().Name}: {e.Message}");
                }
            }
            if (views.Count == 0)
                return;
            var add = queue ?? after;
            var ids = string.Join(", ", views.Select(v => v.Id));
            TabletUi.Order($"working {ids}", add, s =>
            {
                if (s.Jobs != null)
                    s.Jobs.Cancel(s, "it was handed other jobs");
                s.Jobs = null;
                var started = JobRun.Start(s, world, views, add, Arrival.BackFirst, out var problems);
                if (problems.Count > 0)
                    Main.Notify($"AI engineer: {s.Name}: left out: {string.Join("; ", problems)}.");
                if (started == null)
                    TabletUi.Said($"Nothing {s.Name} can do for {ids}.");
            });
        }

        private static void PickLoco(TrainCar loco)
        {
            var list = new List<Verb>();
            var s = Host.SessionFor(loco, false);
            if (loco == TabletUi.Chosen)
            {
                foreach (var (forward, n) in new[] { (false, 3), (false, 1), (true, 1), (true, 3) })
                {
                    var f = forward;
                    var count = n;
                    var label = $"{(f ? "Ahead" : "Back")} {Plural(count, "car length")}";
                    list.Add(new Verb { Label = label, Do = () => TabletUi.Order(label, false, x => x.SetYard(f, count * Session.CarLength)) });
                }
                list.Add(new Verb { Label = "Jobs…", Do = () => TabletUi.Go(TabletTab.Jobs) });
                if (s != null && s.Engineer.Mode != EngineerMode.Off)
                    list.Add(new Verb { Label = "Stop here", Do = () => { s.StopHere(); TabletUi.Said($"{loco.ID}: stopped where it is"); } });
            }
            else
            {
                list.Add(new Verb { Label = "Give orders to it", Do = () => TabletUi.Choose(loco) });
                var lead = TabletUi.LeadOf(PlayerManager.Car);
                if (lead != null && lead != loco && lead.trainset != null && lead.trainset == loco.trainset && DV.ThingTypes.CarTypes.IsSteamLocomotive(loco.carLivery)
                    && (s == null || s.Helper == null))
                {
                    list.Add(new Verb
                    {
                        Label = "Be my helper",
                        Do = () =>
                        {
                            var why = Host.SessionFor(loco, true)?.SetHelper(lead);
                            TabletUi.Said(why == null ? $"{loco.ID}: helping {lead.ID}: two long to go, one short to stop, three short to back up" : $"{loco.ID}: can't help: {why}");
                        },
                    });
                }
                if (s != null && s.Engineer.Mode != EngineerMode.Off)
                    list.Add(new Verb { Label = "Stop it here", Do = () => { s.StopHere(); TabletUi.Said($"{loco.ID}: stopped where it is"); } });
            }
            var cars = TabletUi.CarsOf(loco);
            Set(new Pick { Kind = PickKind.Loco, Loco = loco, Name = $"<b>{loco.ID}</b> ({Dispatch.TypeName(loco)}){(cars > 0 ? $", with {Plural(cars, "car")}" : "")} · {TabletUi.Short(loco)}" }, list);
            Changed();
        }

        /// <summary>The pick's name and its orders, over the bottom of the map; a press sends one. Returns the panel's rect.</summary>
        private static Rect DrawPanel(World world)
        {
            var rows = Math.Min(1, (verbs.Count + 5) / 6);
            var height = 8 + 60 + 6 + rows * 88;
            var r = S(0, MapH - height, MapW, height);
            Box(r, Panel);
            var forLoco = pick.Kind == PickKind.Loco;
            var head = pick.Name;
            if (!forLoco && TabletUi.Chosen != null)
                head += Tint($" · for {TabletUi.Chosen.ID}", Soft);
            Label(new Rect(r.x + 14, r.y + 8, MapW - 14 - 360 - 80, 60), head, Text);
            if (!forLoco)
            {
                if (Button(new Rect(r.xMax - 10 - 64 - 8 - 240 - 110, r.y + 8, 110, 60), "Now", after ? Plate : PlateOn))
                {
                    after = false;
                    Changed();
                }
                if (Button(new Rect(r.xMax - 10 - 64 - 8 - 240, r.y + 8, 240, 60), "After its orders", after ? PlateOn : Plate))
                {
                    after = true;
                    Changed();
                }
            }
            if (Button(new Rect(r.xMax - 10 - 64, r.y + 8, 64, 60), "✕", Plate))
            {
                Unpick();
                Changed();
            }
            var w = (MapW - 20 - 8 * 5) / 6f;
            for (var i = 0; i < Math.Min(6, verbs.Count); i++)
            {
                var v = verbs[i];
                var cell = new Rect(r.x + 10 + (i % 6) * (w + 8), r.y + 8 + 60 + 6 + (i / 6) * 88, w, 80);
                if (Button(cell, v.Label, TabletSkin.Verb, TabletUi.Chosen != null || forLoco))
                {
                    v.Do();
                    Unpick();
                    Changed();
                }
            }
            return r;
        }

        private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
    }
}
