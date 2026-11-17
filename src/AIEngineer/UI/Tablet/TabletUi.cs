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
    }

 internal static class TabletUi
    {
        private const float ListSeconds = 1f;

        public static TabletTab Page { get; set; } = TabletTab.Engineers;

        /// <summary>The loco orders are for; null if the player has none.</summary>
        public static TrainCar Chosen { get; set; }

        private static readonly List<TrainCar> engineers = new List<TrainCar>();
        private static float nextList;
        private static string lastOrder;
        private static Action undo;
        private static bool chosenOnce;

        public static IReadOnlyList<TrainCar> Engineers => engineers;

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
            switch (Page)
            {
                case TabletTab.Map:
                    TabletMap.Draw(world);
                    break;
                case TabletTab.Jobs:
                    TabletPages.Jobs(world);
                    DrawLast(new Rect(12, H - 72, W - 24, 60));
                    break;
                default:
                    TabletPages.Engineers(world);
                    DrawLast(new Rect(12, H - 72, W - 24, 60));
                    break;
            }
        }

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
            // The other locos in the train the player drives: one may be a helper.
            var lead = LeadOf(PlayerManager.Car);
            if (lead?.trainset != null)
                foreach (var car in lead.trainset.cars)
                    if (car != null && car != lead && car.IsLoco && !engineers.Contains(car))
                        engineers.Add(car);
            // A loco picked by hand ("Give orders to it" on the map) stays picked while it exists, and has
            // its plate, though it isn't the player's own and has no engineer yet: its first order gives it
            // one. (0.15.1 dropped it within a second, and the order went to another engineer.)
            if (Chosen != null && Chosen.IsLoco && !engineers.Contains(Chosen))
                engineers.Add(Chosen);
            engineers.Sort((a, b) => string.CompareOrdinal(a.ID, b.ID));
            if (!chosenOnce)
            {
                // The first time: the loco the player is on, if it is theirs.
                chosenOnce = true;
                var mine = Host.LocoOf(PlayerManager.Car);
                if (mine != null && (mine.uniqueCar || Host.SessionFor(mine, false) != null))
                    Chosen = mine;
            }
            if (Chosen == null)
                Chosen = engineers.Count > 0 ? engineers[0] : null;
        }

        // ---- The tab bar: the three pages, then a plate for each engineer ----

        private static readonly (TabletTab Tab, string Label)[] Tabs = { (TabletTab.Engineers, "Engineers"), (TabletTab.Map, "Map"), (TabletTab.Jobs, "Jobs") };

        private static void DrawTabBar()
        {
            var x = 12f;
            foreach (var (tab, label) in Tabs)
            {
                if (Button(new Rect(x, 12, 150, 64), label, Page == tab ? TabOn : Tab) && Page != tab)
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
            // More than fit: the first ones, and the chosen loco's plate always in the last place.
            var chosenAt = Chosen != null ? engineers.IndexOf(Chosen) : -1;
            for (var i = 0; i < fit; i++)
            {
                var loco = i == fit - 1 && chosenAt >= fit ? Chosen : engineers[i];
                var r = new Rect(x + i * (width + 8), 12, width, 64);
                var on = loco == Chosen;
                if (Button(r, $"<b>{loco.ID}</b>\n<size=23>{Tint(Short(loco), Soft)}</size>", on ? ChipOn : Chip))
                    Choose(loco);
                Lamp(r.x + 14, r.y + 25, 14, LampOf(loco));
            }
        }

        public static void Go(TabletTab tab)
        {
            Page = tab;
            TabletMap.Unpick();
            Changed();
        }

        public static void Choose(TrainCar loco)
        {
            Chosen = loco;
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
                return $"Jobs: {s.Jobs.Ids}";
            if (q.Count == 0)
                return s.Engineer.Mode == EngineerMode.Waypoints ? "Waiting" : s.Engineer.Status;
            return $"{Verb(q[0])} {q[0].Label}";
        }

        public static string Status(TrainCar loco)
        {
            var s = Host.SessionFor(loco, false);
            if (s == null || s.Engineer.Mode == EngineerMode.Off)
                return "No engineer yet: an order puts one on.";
            return s.Jobs != null ? $"Working {s.Jobs.Ids}: {s.Jobs.Status}" : s.Engineer.Status;
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
            var session = Host.SessionFor(loco, true);
            if (session == null)
                return;
            var hadEngineer = session.Engineer.Mode != EngineerMode.Off;
            var before = session.Engineer.Queue.ToList();
            var hadJobs = session.Jobs != null;
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
        public static bool IsPower(TrainCar car) =>
            car != null && (car.IsLoco || CarTypes.IsAnyLocomotiveOrTender(car.carLivery) || CarTypes.IsSlug(car.carLivery));

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
