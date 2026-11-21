using System;
using System.Collections.Generic;
using System.Linq;
using AIEngineer.Core.Driving;
using AIEngineer.Game;
using DV.Logic.Job;
using UnityEngine;
using static AIEngineer.UI.Tablet.TabletSkin;

namespace AIEngineer.UI.Tablet
{
    /// <summary>
    /// The tablet's Engineers and Jobs pages. Engineers: a card for each
    /// engineer, what it is doing, its orders (each with ✕), and Give
    /// orders, Stop, Hand back. Jobs: the jobs the player holds as bills
    /// to tick, what the ticked ones are, and Add to the queue or Do it now
    /// for the chosen loco. A page shows as many cards or bills as fit, with
    /// ▲ and ▼ for the rest.
    /// </summary>
    internal static class TabletPages
    {
        private const float Top = 84, PageH = 924 - 84 - 80;

        private static readonly Color CheckBorder = Hex("8a7a5f");
        private static readonly Color CheckFill = Hex("f7f1e2");
        private static readonly Color CheckMark = Hex("3b2f1c");
        private static readonly Color BillBlocked = Hex("7e2f1b");

        private static int engineersFrom;
        private static int billsFrom;
        private static readonly List<JobView> views = new List<JobView>();
        private static readonly HashSet<string> ticked = new HashSet<string>();
        private static float nextJobs;
#if !PUBLIC_RELEASE
        /// <summary>Where each steam loco's Fire button was last drawn (the self-test's).</summary>
        internal static readonly Dictionary<TrainCar, Rect> FireButtons = new Dictionary<TrainCar, Rect>();

        /// <summary>The loco the "Give orders to the loco you're on" button was last drawn for, and where (the self-test's).</summary>
        internal static TrainCar OnButtonFor;
        internal static Rect OnButton;

        /// <summary>Where each loco's card was last drawn (the self-test's).</summary>
        internal static readonly Dictionary<TrainCar, Rect> Cards = new Dictionary<TrainCar, Rect>();
#endif

        /// <summary>Clears cached job views, selections, and paging when the world unloads.</summary>
        public static void ResetForUnload()
        {
            engineersFrom = 0;
            billsFrom = 0;
            views.Clear();
            ticked.Clear();
            nextJobs = 0f;
#if !PUBLIC_RELEASE
            FireButtons.Clear();
            Cards.Clear();
            OnButtonFor = null;
            OnButton = default;
#endif
        }

        // ---- The conductor ----

        /// <summary>The player's conductor: its plan and Execute, then what it is calling.</summary>
        public static void Conductor()
        {
            var r = new Rect(12, Top, W - 24, PageH);
            Box(r, TabletSkin.Card);
            var x = r.x + 24;
            var width = r.width - 48;
            var session = ConductorDesk.Current;
            var c = session?.Conductor;
            if (c == null)
            {
                Label(new Rect(x, r.y + 20, width, 50), "Conductor", Big);
                Label(new Rect(x, r.y + 80, width, 200),
                    "You drive; your conductor plans the route and lines the switches ahead of the train. Aboard your train, press a track on the map and choose Conductor. Nothing is touched until you press Execute.", Text);
                if (Button(new Rect(x, r.y + 300, 300, 80), "Open the map", Primary))
                    TabletUi.Go(TabletTab.Map);
                return;
            }
            var label = c.Destination?.Label ?? "the destination";
            Label(new Rect(x, r.y + 20, width, 50), $"{session.Name} to {label}", Big);
            var buttons = r.yMax - 104;
            switch (c.State)
            {
                case ConductorState.Planned:
                    PlannedRoute(session, c, new Rect(x, r.y + 84, width, buttons - r.y - 100));
                    if (Button(new Rect(x, buttons, 320, 80), "Execute", Primary))
                    {
                        TabletUi.Said(ConductorDesk.Execute());
                        Changed();
                    }
                    if (Button(new Rect(x + 332, buttons, 240, 80), "See the map", Plate))
                        TabletUi.Go(TabletTab.Map);
                    if (Button(new Rect(x + 584, buttons, 200, 80), "Cancel", Plate))
                    {
                        ConductorDesk.StandDown();
                        TabletUi.Said("Conductor: plan dropped");
                        Changed();
                    }
                    break;
                case ConductorState.Executing:
                    Label(new Rect(x, r.y + 90, width, 130), Tint($"<size=44>{ConductorDesk.Say(c, c.Call)}</size>", ColorOf(c.Call.Level)), Big);
                    if (c.NextCall.Kind != ConductorCallKind.None)
                        Label(new Rect(x, r.y + 230, width, 60), $"Then: {ConductorDesk.Say(c, c.NextCall)}", Text);
                    Label(new Rect(x, r.y + 300, width, 50), Tint($"{ConductorCall.Far(c.Remaining)} to go · {Count(c.SwitchesThrown, "switch", "switches")} lined", Soft), Text);
                    if (Button(new Rect(x, buttons, 240, 80), "See the map", Plate))
                        TabletUi.Go(TabletTab.Map);
                    if (Button(new Rect(x + 252, buttons, 260, 80), "Stand down", Plate))
                    {
                        ConductorDesk.StandDown();
                        TabletUi.Said("Conductor stood down; the switches are as they lie");
                        Changed();
                    }
                    break;
                default:
                    Label(new Rect(x, r.y + 90, width, 130), Tint($"<size=44>{ConductorDesk.Say(c, c.Call)}</size>", Ok), Big);
                    Label(new Rect(x, r.y + 230, width, 50), Tint($"{Count(c.SwitchesThrown, "switch", "switches")} lined", Soft), Text);
                    if (Button(new Rect(x, buttons, 240, 80), "Done", Primary))
                    {
                        ConductorDesk.StandDown();
                        Changed();
                    }
                    break;
            }
        }

        private static void PlannedRoute(Session session, Conductor c, Rect r)
        {
            var route = c.Route;
            if (route == null)
                return;
            var state = session.Read();
            var first = route.Legs[0].Leader;
            var leads = !state.LightEngine
                ? first == state.LocoEnd ? "The loco leads" : "The cars lead"
                : first == state.LocoFront ? "Front first" : "Back first";
            var y = r.y;
            void Row(string text, Look look)
            {
                Label(new Rect(r.x, y, r.width, 50), text, look);
                y += 56;
            }
            Row($"{ConductorCall.Far(route.Length)}, {Count(route.Reversals, "reversal", "reversals")}", Text);
            Row($"{leads} from here", Text);
            Row($"{Count(c.SwitchesToThrow, "switch", "switches")} to throw", Text);
            if (c.PlanNote != null)
                Row(Tint(c.PlanNote, Wait), Text);
            if (c.Call.Kind == ConductorCallKind.NewRoute || c.Call.Kind == ConductorCallKind.NoRoute)
                Row(Tint(ConductorDesk.Say(c, c.Call), Wait), Text);
            Row(Tint("Nothing has been touched yet.", Soft), Text);
        }

        private static Color ColorOf(ConductorCallLevel level) =>
            level == ConductorCallLevel.Stop ? Alarm : level == ConductorCallLevel.Do ? Wait : Cream;

        private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

        // ---- Engineers ----

        public static void Engineers(World world)
        {
            var list = TabletUi.Engineers;
            if (list.Count == 0)
            {
                Box(new Rect(12, Top, W - 24, PageH), TabletSkin.Card);
                Label(new Rect(32, Top + 20, W - 64, 200), "No engineers yet. Your own locos (bought, or from the garage) show here; an order gives one an engineer. Get on a loco and press Give orders to give it one.", Empty);
                var mine = Host.LocoOf(PlayerManager.Car);
                if (mine != null && Button(new Rect(32, Top + 230, 420, 80), $"Give orders to {mine.ID}", Primary))
                {
                    TabletUi.Chosen = mine;
                    Host.SessionFor(mine, true);
                    TabletUi.Go(TabletTab.Map);
                }
                return;
            }
            // The loco the player is on, at hand whatever else is listed (unless it is the one
            // orders are for already: its card is lit, with its own Give orders).
            var top = Top;
            var pageH = PageH;
            var on = TabletUi.LeadOf(PlayerManager.Car);
            if (on != null && on != TabletUi.Chosen)
            {
#if !PUBLIC_RELEASE
                OnButtonFor = on;
                OnButton = new Rect(12, Top, W - 24, 64);
#endif
                if (Button(new Rect(12, Top, W - 24, 64), $"Give orders to {on.ID}, the loco you're on", Primary))
                {
                    TabletUi.Chosen = on;
                    TabletMap.ShowChosenNext();
                    TabletUi.Go(TabletTab.Map);
                }
                top += 72;
                pageH -= 72;
            }
            const float minCard = 236;
            var per = Mathf.Max(1, Mathf.Min(list.Count, (int)((pageH + 8) / (minCard + 8))));
            var paged = per < list.Count;
            engineersFrom = Mathf.Clamp(engineersFrom, 0, Math.Max(0, list.Count - per));
            var width = paged ? W - 24 - 76 : W - 24;
            var height = (pageH - 8 * (per - 1)) / per;
            for (var i = 0; i < per && engineersFrom + i < list.Count; i++)
                EngineerCard(world, list[engineersFrom + i], new Rect(12, top + i * (height + 8), width, height));
            if (paged)
            {
                if (Button(new Rect(W - 12 - 68, top, 68, 68), "▲", Plate, engineersFrom > 0))
                {
                    engineersFrom -= per;
                    Changed();
                }
                Label(new Rect(W - 12 - 68, top + 76, 68, 60), $"{engineersFrom + 1}–{Math.Min(list.Count, engineersFrom + per)}\nof {list.Count}", Centered(SoftText));
                if (Button(new Rect(W - 12 - 68, top + 144, 68, 68), "▼", Plate, engineersFrom + per < list.Count))
                {
                    engineersFrom += per;
                    Changed();
                }
            }
        }

        private static void EngineerCard(World world, TrainCar loco, Rect r)
        {
#if !PUBLIC_RELEASE
            Cards[loco] = r;
#endif
            var on = loco == TabletUi.Chosen;
            Box(r, on ? CardOn : TabletSkin.Card);
            var s = Host.SessionFor(loco, false);
            var engineer = s != null && s.Engineer.Mode != EngineerMode.Off;
            Lamp(r.x + 16, r.y + 33, 18, TabletUi.LampOf(loco));
            Label(new Rect(r.x + 44, r.y + 10, 150, 64), $"<b>{loco.ID}</b>", Big);
            var cars = TabletUi.CarsOf(loco);
            var where = $"{Dispatch.TypeName(loco)} on {Dispatch.Where(loco)}{(cars > 0 ? $", {cars} car{(cars == 1 ? "" : "s")}" : ", running light")}";
            var bx = r.xMax - 12;
            bx -= 170;
            if (Button(new Rect(bx, r.y + 10, 170, 64), "Hand back", Plate, engineer))
            {
                TabletUi.Said($"{loco.ID}: handed back to you");
                s.Stop(secure: true);
                Changed();
            }
            bx -= 118;
            if (Button(new Rect(bx, r.y + 10, 110, 64), "Stop", Plate, engineer))
            {
                s.StopHere();
                TabletUi.Said($"{loco.ID}: stopped where it is");
                Changed();
            }
            bx -= 208;
            if (Button(new Rect(bx, r.y + 10, 200, 64), "Give orders", on ? PlateOn : Plate))
            {
                TabletUi.Chosen = loco;
                TabletMap.ShowChosenNext();
                TabletUi.Go(TabletTab.Map);
            }
            Label(new Rect(r.x + 200, r.y + 10, bx - r.x - 208, 64), where, SoftText);
            // A steam loco coupled in the train the player drives can help it, worked by the whistle.
            var lead = TabletUi.LeadOf(PlayerManager.Car);
            var helping = s?.Helper != null;
            var canHelp = helping || (lead != null && lead != loco && lead.trainset != null && lead.trainset == loco.trainset
                && DV.ThingTypes.CarTypes.IsSteamLocomotive(loco.carLivery));
            // The steam loco's fire: the engineer's crew, or the player's.
            var steam = s != null ? s.Steam != null : DV.ThingTypes.CarTypes.IsSteamLocomotive(loco.carLivery);
            var buttons = 300f + (canHelp ? 158 : 0);
            if (steam)
            {
                buttons += 158;
                var youFire = s?.Steam != null ? s.Steam.PlayerFires : Main.Settings.PlayerFiresNewEngineers;
                var fire = new Rect(r.xMax - 12 - buttons + 8, r.y + 82, 150, 56);
#if !PUBLIC_RELEASE
                FireButtons[loco] = fire;
#endif
                if (Button(fire, youFire ? "Fire: you" : "Fire: crew", youFire ? PlateOn : Plate))
                {
                    var crew = Host.SessionFor(loco, true)?.Steam;
                    if (crew != null)
                    {
                        crew.PlayerFires = !youFire;
                        TabletUi.Said(crew.PlayerFires ? $"{loco.ID}: you're firing; the engineer leaves the fire and the water to you" : $"{loco.ID}: the crew fires it");
                    }
                    Changed();
                }
            }
            Label(new Rect(r.x + 16, r.y + 80, r.width - 32 - buttons, 36), TabletUi.Status(loco), Text);
            if (canHelp && Button(new Rect(r.xMax - 12 - 160 - 8 - 120 - 8 - 150, r.y + 82, 150, 56), helping ? "Helping" : "Helper", helping ? PlateOn : Plate))
            {
                if (helping)
                {
                    s.Stop(secure: true);
                    TabletUi.Said($"{loco.ID}: stopped helping, shut off and braked");
                }
                else
                {
                    var session = Host.SessionFor(loco, true);
                    var why = session?.SetHelper(lead);
                    TabletUi.Said(why == null ? $"{loco.ID}: helping {lead.ID}: two long to go, one short to stop, three short to back up" : $"{loco.ID}: can't help: {why}");
                }
                Changed();
            }
            // The nearest station with what it runs on, or that repairs it; loco end first.
            if (Button(new Rect(r.xMax - 12 - 160, r.y + 82, 160, 56), "Service", Plate))
            {
                TabletUi.Service(loco, world, false);
                Changed();
            }
            if (Button(new Rect(r.xMax - 12 - 160 - 8 - 120, r.y + 82, 120, 56), "Fuel", Plate))
            {
                TabletUi.Service(loco, world, true);
                Changed();
            }
            var route = s?.Engineer.Route;
            var line = s?.Jobs != null ? $"{s.Jobs.Ids}: planned together" : route != null ? $"Route {route.Length:F0} m, {route.Reversals} reversal{(route.Reversals == 1 ? "" : "s")}" : "";
            Label(new Rect(r.x + 16, r.y + 116, r.width - 32 - buttons, 30), line, Brass2Text);
            var well = new Rect(r.x + 12, r.y + 146, r.width - 24, r.height - 158);
            Box(well, Well);
            var queue = s?.Engineer.Queue;
            if (queue == null || queue.Count == 0)
            {
                Label(new Rect(well.x + 14, well.y + 4, well.width - 28, 50), engineer ? "No orders. It waits where it is." : "No engineer yet: an order puts one on.", SoftText);
                return;
            }
            var rows = Mathf.Max(1, (int)(well.height / 58));
            for (var i = 0; i < Math.Min(rows, queue.Count); i++)
            {
                var row = new Rect(well.x, well.y + i * 58, well.width, 58);
                var wp = queue[i];
                var more = i == rows - 1 && queue.Count > rows ? Tint($"  (+{queue.Count - rows} more)", Soft) : "";
                Label(new Rect(row.x + 8, row.y, 36, 58), Tint((i + 1).ToString(), i == 0 ? Brass2 : Soft), Text);
                Label(new Rect(row.x + 48, row.y, row.width - 48 - 72, 58), $"{TabletUi.Verb(wp)} {wp.Label}{more}", Text);
                var index = i;
                if (Button(new Rect(row.xMax - 66, row.y + 2, 62, 54), "✕", Plate))
                {
                    s.RemoveOrder(index);
                    TabletUi.Said($"{loco.ID}: order {index + 1} removed");
                    Changed();
                }
            }
        }

        // ---- Jobs ----

        public static void Jobs(World world)
        {
            if (Time.unscaledTime >= nextJobs)
            {
                nextJobs = Time.unscaledTime + 1.5f;
                views.Clear();
                foreach (var job in Game.Jobs.Taken())
                {
                    try
                    {
                        views.Add(Game.Jobs.Read(world, job));
                    }
                    catch (Exception e)
                    {
                        Main.Warn($"tablet: couldn't read job {job.ID}: {e.GetType().Name}: {e.Message}");
                    }
                }
                ticked.RemoveWhere(id => !views.Exists(v => v.Id == id));
            }
            var chosen = TabletUi.Chosen;
            Label(new Rect(16, Top, 700, 60), $"JOBS YOU HOLD{(chosen != null ? Tint($"  for {chosen.ID}", Cream) : "")}", Section);
            var session = chosen != null ? Host.SessionFor(chosen, false) : null;
            if (session?.Jobs != null)
                Label(new Rect(560, Top, W - 572, 60), Tint($"Working {session.Jobs.Ids}", Brass2), Small);
            var listR = new Rect(12, Top + 68, 430, PageH - 68 - 88);
            var planR = new Rect(450, Top + 68, W - 462, PageH - 68 - 88);
            Box(listR, Well);
            Box(planR, Well);
            if (views.Count == 0)
            {
                Label(new Rect(listR.x + 14, listR.y + 10, listR.width - 28, 300), "You hold no jobs. Take some at a station office (their booklets), then hand them over here.", Empty);
            }
            var per = Mathf.Max(1, (int)((listR.height - 12) / 78));
            billsFrom = Mathf.Clamp(billsFrom, 0, Math.Max(0, views.Count - per));
            for (var i = 0; i < per && billsFrom + i < views.Count; i++)
                Bill(views[billsFrom + i], new Rect(listR.x + 6, listR.y + 6 + i * 78, listR.width - 12 - (views.Count > per ? 70 : 0), 72));
            if (views.Count > per)
            {
                if (Button(new Rect(listR.xMax - 66, listR.y + 6, 60, 60), "▲", Plate, billsFrom > 0))
                {
                    billsFrom -= per;
                    Changed();
                }
                if (Button(new Rect(listR.xMax - 66, listR.y + 72, 60, 60), "▼", Plate, billsFrom + per < views.Count))
                {
                    billsFrom += per;
                    Changed();
                }
            }
            var chosenViews = views.Where(v => ticked.Contains(v.Id)).ToList();
            if (chosenViews.Count == 0)
            {
                Label(new Rect(planR.x + 14, planR.y + 10, planR.width - 28, 400), "Tick the jobs to hand over. They are planned together: the engineer uncouples what isn't theirs, fetches every job's cars (cars in the way ride along and are put back), and sets each job's cars out on its track, hand brakes on.", Empty);
            }
            else
            {
                var y = planR.y + 10;
                foreach (var v in chosenViews)
                {
                    Label(new Rect(planR.x + 14, y, planR.width - 28, 36), $"<b>{v.Id}</b>  {Tint(v.Kind, Soft)}", Text);
                    Label(new Rect(planR.x + 34, y + 34, planR.width - 48, 34), v.Blocked != null ? Tint($"Can't now: {v.Blocked}", Alarm) : v.Summary, SoftText);
                    y += 76;
                    if (y > planR.yMax - 76)
                        break;
                }
            }
            var foot = Top + PageH - 80;
            var n = chosenViews.Count(v => v.Blocked == null);
            Label(new Rect(16, foot, W - 24 - 500, 80), n > 0 ? $"{n} job{(n == 1 ? "" : "s")} ticked" : "Nothing ticked", SoftText);
            var can = n > 0 && chosen != null;
            if (Button(new Rect(W - 12 - 488, foot, 240, 80), "Add to the queue", Plate, can))
                Hand(world, chosenViews, true);
            if (Button(new Rect(W - 12 - 240, foot, 240, 80), "Do it now", Primary, can))
                Hand(world, chosenViews, false);
        }

        private static void Hand(World world, List<JobView> chosen, bool queue)
        {
            var jobs = chosen.Where(v => v.Blocked == null).Select(v => v.Job).ToList();
            TabletMap.WorkJobs(world, jobs, queue);
            ticked.Clear();
            TabletUi.Go(TabletTab.Engineers);
        }

        private static void Bill(JobView v, Rect r)
        {
            var on = ticked.Contains(v.Id);
            Fill(r, Paper);
            Fill(new Rect(r.x, r.y, 10, r.height), KindColor(v.Kind));
            if (on)
            {
                Fill(new Rect(r.x - 3, r.y - 3, r.width + 6, 3), Brass2);
                Fill(new Rect(r.x - 3, r.yMax, r.width + 6, 3), Brass2);
                Fill(new Rect(r.x - 3, r.y, 3, r.height), Brass2);
                Fill(new Rect(r.xMax, r.y, 3, r.height), Brass2);
            }
            Fill(new Rect(r.x + 22, r.y + 20, 32, 32), CheckBorder);
            Fill(new Rect(r.x + 23, r.y + 21, 30, 30), CheckFill);
            if (on)
                Label(new Rect(r.x + 22, r.y + 20, 32, 32), Tint("✓", CheckMark), Centered(Text));
            Label(new Rect(r.x + 66, r.y + 4, r.width - 72, 34), $"<b>{v.Id}</b>  <size=23>{v.Kind}</size>", BillText);
            Label(new Rect(r.x + 66, r.y + 36, r.width - 72, 32), v.Blocked != null ? Tint("Not now: " + v.Blocked, BillBlocked) : v.Summary, BillSmall);
            if (Button(r, "", Invisible))
            {
                if (!ticked.Remove(v.Id))
                    ticked.Add(v.Id);
                Changed();
            }
        }
    }
}
