using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Game;
using DV.ThingTypes;
using UnityEngine;
using static AIEngineer.UI.Tablet.TabletSkin;

namespace AIEngineer.UI.Tablet
{
    internal enum TabletTab
    {
        Engineers,
        Map,
        Jobs,
        Conductor,
    }

    internal static class TabletUi
    {
        private const float ListSeconds = 1f;

        public static TabletTab Page { get; set; } = TabletTab.Engineers;

        private static TrainCar chosen;
        private static readonly List<TrainCar> recent = new List<TrainCar>();
        private static readonly List<TrainCar> hotbar = new List<TrainCar>();

        /// <summary>The loco orders are for; null if the player has none.</summary>
        public static TrainCar Chosen
        {
            get => chosen;
            set
            {
                chosen = value;
                if (value != null)
                    Touch(value);
            }
        }

        /// <summary>When set, the tablet shows the Sort Yard preview sheet for this bowl.</summary>
        public static SortableBowl Sorting { get; private set; }

        private static readonly List<TrainCar> engineers = new List<TrainCar>();
        private static float nextList;
        private static string lastOrder;
        private static Action undo;
        private static bool chosenOnce;

        public static IReadOnlyList<TrainCar> Engineers => engineers;

        /// <summary>Records <paramref name="loco"/> as the most recently used engineer for the top-bar hotbar.</summary>
        public static void Touch(TrainCar loco)
        {
            if (loco == null)
                return;
            recent.Remove(loco);
            recent.Insert(0, loco);
            while (recent.Count > 16)
                recent.RemoveAt(recent.Count - 1);
        }

        /// <summary>Clears cached loco selection, list, and undo state when the world unloads.</summary>
        public static void ResetForUnload()
        {
            Page = TabletTab.Engineers;
            chosen = null;
            Sorting = null;
            chosenOnce = false;
            engineers.Clear();
            recent.Clear();
            hotbar.Clear();
            nextList = 0f;
            lastOrder = null;
            undo = null;
        }

        /// <summary>A pass of the screen: the tab bar, then the page.</summary>
        public static void Draw(World world)
        {
            Box(new Rect(0, 0, W, H), ScreenBg);
            if (world == null)
            {
                Label(new Rect(24, 24, 800, 60), "The world isn't loaded yet.", Text);
                return;
            }
            if (Time.unscaledTime >= nextList)
                RefreshList();
            DrawTabBar();
            if (Sorting != null)
            {
                TabletMap.ClearPressArea();
                TabletPages.Sort(world, Sorting);
                DrawLast(new Rect(12, H - 72, W - 24, 60));
                return;
            }
            switch (Page)
            {
                case TabletTab.Map:
                    TabletMap.Draw(world);
                    break;
                case TabletTab.Jobs:
                    TabletPages.Jobs(world);
                    DrawLast(new Rect(12, H - 72, W - 24, 60));
                    break;
                case TabletTab.Conductor:
                    TabletPages.Conductor();
                    DrawLast(new Rect(12, H - 72, W - 24, 60));
                    break;
                default:
                    TabletPages.Engineers(world);
                    DrawLast(new Rect(12, H - 72, W - 24, 60));
                    break;
            }
        }

        /// <summary>
        /// A pass of an example screen for the tablet's inventory icon
        /// (Held/EngineerTabletBody): the tab bar on Engineers with two
        /// engineers' plates, three engineers' cards, and the last order, as
        /// <see cref="Draw"/> draws them, from no world. Nothing it shows is
        /// the player's, and its plates press nothing.
        /// </summary>
        internal static void DrawSample()
        {
            Box(new Rect(0, 0, W, H), ScreenBg);
            var x = 12f;
            foreach (var (tab, label) in Tabs)
            {
                Button(new Rect(x, 12, 150, 64), label, tab == TabletTab.Engineers ? TabOn : Tab);
                x += 158;
            }
            // Two plates fit beside the four tabs, as the bar draws them for three engineers.
            var plates = new[] { ("L-042", "Driving", Ok, true), ("L-117", "Waiting", Wait, false) };
            var room = W - 12 - x;
            var width = Mathf.Min(284, (room - 8 * (plates.Length - 1)) / plates.Length);
            for (var i = 0; i < plates.Length; i++)
            {
                var (id, doing, lamp, on) = plates[i];
                var r = new Rect(x + i * (width + 8), 12, width, 64);
                Button(r, $"<b>{id}</b>\n<size=23>{Tint(doing, Soft)}</size>", on ? ChipOn : Chip);
                Lamp(r.x + 14, r.y + 25, 14, lamp);
            }
            TabletPages.SampleEngineers();
            var strip = new Rect(12, H - 72, W - 24, 60);
            Box(strip, Strip);
            Label(new Rect(strip.x + 14, strip.y, strip.width - 160, strip.height), "L-042: drive to FF-C-3", Small);
            Button(new Rect(strip.xMax - 130, strip.y, 130, strip.height), "Undo", Plate);
        }

#if !PUBLIC_RELEASE
        /// <summary>Reads the list afresh now (the self-test's).</summary>
        internal static void RefreshForTest() => RefreshList();
#endif

        private static void RefreshList()
        {
            nextList = Time.unscaledTime + ListSeconds;
            engineers.Clear();
            foreach (var loco in Dispatch.Locos())
                engineers.Add(loco);
            foreach (var s in Host.Sessions)
            {
                if (s.Loco != null && s.Engineer.Mode != EngineerMode.Off && !engineers.Contains(s.Loco))
                    engineers.Add(s.Loco);
            }
            // The loco the player is on, whoever's it is, and the locos coupled to it (one may be
            // a helper; a consist of company locos is given orders from here). 0.15.3 listed only
            // the player's own (SeleneBaby79, 2026-11-19: five DE6s, none of them listed).
            var lead = LeadOf(PlayerManager.Car);
            if (lead?.trainset != null)
                foreach (var car in lead.trainset.cars)
                    if (car != null && car.IsLoco && !engineers.Contains(car))
                        engineers.Add(car);
            // A loco picked by hand ("Give orders to it" on the map) stays picked while it exists, and has
            // its plate, though it isn't the player's own and has no engineer yet: its first order gives it
            // one. (0.15.1 dropped it within a second, and the order went to another engineer.)
            if (Chosen != null && Chosen.IsLoco && !engineers.Contains(Chosen))
                engineers.Add(Chosen);
            engineers.Sort((a, b) => string.CompareOrdinal(a.ID, b.ID));
            for (var i = recent.Count - 1; i >= 0; i--)
                if (recent[i] == null)
                    recent.RemoveAt(i);
            if (!chosenOnce)
            {
                // The first time: the loco the player is on, if it is theirs.
                chosenOnce = true;
                var mine = Host.LocoOf(PlayerManager.Car);
                if (mine != null && (mine.uniqueCar || Host.SessionFor(mine, false) != null))
                    Chosen = mine;
            }
            if (Chosen == null)
            {
                TrainCar fallback = null;
                for (var i = 0; i < recent.Count; i++)
                {
                    if (engineers.Contains(recent[i]))
                    {
                        fallback = recent[i];
                        break;
                    }
                }
                Chosen = fallback ?? (engineers.Count > 0 ? engineers[0] : null);
            }
        }

        // ---- The tab bar: the pages, then a plate for each engineer ----

        private static readonly (TabletTab Tab, string Label)[] Tabs = { (TabletTab.Engineers, "Engineers"), (TabletTab.Map, "Map"), (TabletTab.Jobs, "Jobs"), (TabletTab.Conductor, "Conductor") };

        private static void DrawTabBar()
        {
            var x = 12f;
            foreach (var (tab, label) in Tabs)
            {
                if (Button(new Rect(x, 12, 150, 64), label, Sorting == null && Page == tab ? TabOn : Tab) && (Sorting != null || Page != tab))
                    Go(tab);
                x += 158;
            }
            var room = W - 12 - x;
            var n = engineers.Count;
            if (n == 0)
            {
                Label(new Rect(x + 8, 12, room - 8, 64), "No locos of your own yet: buy one, or get on one and press Give orders.", SoftText);
                return;
            }
            var width = Mathf.Min(284, (room - 8 * (n - 1)) / n);
            var fit = width >= 150 ? n : Mathf.Max(1, (int)((room + 8) / (150 + 8)));
            if (fit < n)
                width = (room - 8 * (fit - 1)) / fit;
            // Show the most recently used engineers (most recent, second most recent, ...),
            // filling any remaining slots before enough have been used with active engineers first.
            hotbar.Clear();
            for (var i = 0; i < recent.Count && hotbar.Count < fit; i++)
            {
                var loco = recent[i];
                if (loco != null && engineers.Contains(loco) && !hotbar.Contains(loco))
                    hotbar.Add(loco);
            }
            if (hotbar.Count < fit)
            {
                for (var i = 0; i < engineers.Count && hotbar.Count < fit; i++)
                {
                    var loco = engineers[i];
                    var s = Host.SessionFor(loco, false);
                    if (s != null && s.Engineer.Mode != EngineerMode.Off && !hotbar.Contains(loco))
                        hotbar.Add(loco);
                }
            }
            if (hotbar.Count < fit)
            {
                for (var i = 0; i < engineers.Count && hotbar.Count < fit; i++)
                {
                    var loco = engineers[i];
                    if (!hotbar.Contains(loco))
                        hotbar.Add(loco);
                }
            }
            for (var i = 0; i < hotbar.Count; i++)
            {
                var loco = hotbar[i];
                var r = new Rect(x + i * (width + 8), 12, width, 64);
                var on = loco == Chosen;
                if (Button(r, $"<b>{loco.ID}</b>\n<size=23>{Tint(Short(loco), Soft)}</size>", on ? ChipOn : Chip))
                    Choose(loco);
                Lamp(r.x + 14, r.y + 25, 14, LampOf(loco));
            }
        }

        public static void Go(TabletTab tab)
        {
            Sorting = null;
            Page = tab;
            TabletMap.Unpick();
            Changed();
        }

        public static void OpenSort(SortableBowl bowl)
        {
            Sorting = bowl;
            TabletPages.ResetSortPreview();
            TabletMap.Unpick();
            Changed();
        }

        public static void CloseSort()
        {
            Sorting = null;
            Changed();
        }

        public static void Choose(TrainCar loco)
        {
            Chosen = loco;
            TabletPages.ResetSortPreview();
            TabletMap.Unpick();
            Changed();
        }

        public static Color LampOf(TrainCar loco)
        {
            var s = Host.SessionFor(loco, false);
            if (s == null || s.Engineer.Mode == EngineerMode.Off)
                return Dim;
            return s.Idle ? Wait : Ok;
        }

        /// <summary>What an engineer is doing, short enough for its plate.</summary>
        public static string Short(TrainCar loco)
        {
            var s = Host.SessionFor(loco, false);
            if (s == null || s.Engineer.Mode == EngineerMode.Off)
                return "No engineer";
            var q = s.Engineer.Queue;
            if (s.Jobs != null)
                return $"Jobs: {s.Jobs.Ids}{(s.Jobs.ThenIds != null ? $", then {s.Jobs.ThenIds}" : "")}";
            if (s.Sort != null)
                return $"Sort: {s.Sort.YardName}";
            if (q.Count == 0)
                return s.Engineer.Mode == EngineerMode.Waypoints ? "Waiting" : s.Engineer.Status;
            return $"{Verb(q[0])} {q[0].Label}";
        }

        public static string Status(TrainCar loco)
        {
            var s = Host.SessionFor(loco, false);
            if (s == null || s.Engineer.Mode == EngineerMode.Off)
                return "No engineer yet: an order puts one on.";
            if (s.Jobs != null)
                return $"Working {s.Jobs.Ids}: {s.Jobs.Status}{(s.Jobs.ThenIds != null ? $" Then {s.Jobs.ThenIds}." : "")}";
            if (s.Sort != null)
                return $"Sorting {s.Sort.YardName}: {s.Sort.Status}";
            return s.Engineer.Status;
        }

        public static string Verb(Waypoint wp)
        {
            switch (wp.Action)
            {
                case WaypointAction.Couple:
                    return "Couple to";
                case WaypointAction.Uncouple:
                    return $"Leave {wp.CarCount} at";
                default:
                    return "Go to";
            }
        }

        // ---- Orders, and the strip that shows the last with Undo ----

        /// <summary>
        /// Gives the chosen loco an order, putting an engineer on it first if
        /// it has none. Undo puts its orders back as they were (and hands
        /// back an engineer the order put on); an order given while it was
        /// working jobs can't be undone, the jobs being given up.
        /// </summary>
        public static void Order(string what, bool after, Action<Session> give)
        {
            var loco = Chosen;
            if (loco == null)
                return;
            Touch(loco);
            var session = Host.SessionFor(loco, true);
            if (session == null)
                return;
            var hadEngineer = session.Engineer.Mode != EngineerMode.Off;
            var before = session.Engineer.Queue.ToList();
            var hadJobs = session.Jobs != null || session.Sort != null;
            give(session);
            Said($"{loco.ID}: {what}{(after ? ", after its orders" : "")}", hadJobs ? (Action)null : () =>
            {
                if (session.Loco == null)
                    return;
                if (session.Jobs != null)
                {
                    session.Jobs.Cancel(session, "you undid it");
                    session.Jobs = null;
                }
                if (session.Sort != null)
                {
                    session.Sort.Cancel(session, "you undid it");
                    session.Sort = null;
                }
                session.Engineer.ClearQueue();
                if (!hadEngineer)
                    session.Stop(secure: true);
                else if (before.Count == 0)
                    session.StopHere();
                else
                    foreach (var wp in before)
                        session.Engineer.Enqueue(wp);
            });
        }

        /// <summary>Sends a loco for fuel or service at the nearest station that has it; says so if none is known.</summary>
        public static void Service(TrainCar loco, World world, bool fuel)
        {
            var station = Game.Service.Nearest(world, loco, fuel);
            if (station == null)
            {
                Said($"{loco.ID}: no {(fuel ? "station selling its fuel" : "service station")} seen nearby yet");
                return;
            }
            Chosen = loco;
            string done = null;
            Order("", false, s => done = Game.Service.Send(s, world, station, fuel, false));
            if (done != null)
                lastOrder = $"{loco.ID}: to {done}";
        }

        /// <summary>Shows what was done in the strip; with an undo, Undo takes it back.</summary>
        public static void Said(string text, Action undoIt = null)
        {
            lastOrder = text;
            undo = undoIt;
        }

#if !PUBLIC_RELEASE
        /// <summary>Presses Undo, as on the strip; returns whether there was something to undo.</summary>
        public static bool UndoForTest()
        {
            if (undo == null)
                return false;
            var u = undo;
            undo = null;
            lastOrder = "Undone.";
            u();
            return true;
        }
#endif

        public static void DrawLast(Rect r)
        {
            Box(r, Strip);
            Label(new Rect(r.x + 14, r.y, r.width - 160, r.height), lastOrder ?? Tint("The last order you gave shows here, with Undo.", Dim), Small);
            if (Button(new Rect(r.xMax - 130, r.y, 130, r.height), "Undo", Plate, undo != null))
            {
                var u = undo;
                undo = null;
                lastOrder = "Undone.";
                u();
                Changed();
            }
        }

        /// <summary>
        /// The loco the player drives from: the one they are in, or, from a
        /// tender, the loco it is coupled to; null if they are in neither.
        /// </summary>
        public static TrainCar LeadOf(TrainCar car)
        {
            if (car == null)
                return null;
            if (car.IsLoco)
                return car;
            if (!CarTypes.IsTender(car.carLivery))
                return null;
            foreach (var c in new[] { car.frontCoupler, car.rearCoupler })
            {
                var other = c != null && c.coupledTo != null ? c.coupledTo.train : null;
                if (other != null && other.IsLoco)
                    return other;
            }
            return null;
        }

        /// <summary>A loco, tender, or slug: what moves a train, not what it carries.</summary>
        public static bool IsPower(TrainCar car) => Session.IsPower(car);

        /// <summary>A loco's own cars: the cars coupled to it, the loco and any tender not counted.</summary>
        public static int CarsOf(TrainCar loco)
        {
            var set = loco?.trainset;
            if (set == null)
                return 0;
            var n = 0;
            foreach (var car in set.cars)
                if (car != null && !IsPower(car))
                    n++;
            return n;
        }
    }
}
