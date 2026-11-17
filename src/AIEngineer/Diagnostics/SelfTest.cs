using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AIEngineer.Core.Driving;
using AIEngineer.Core.Routing;
using AIEngineer.Core.Tracks;
using AIEngineer.Game;
using HarmonyLib;
using AIEngineer.UI;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.UserManagement;
using DV.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AIEngineer.Diagnostics
{
    /// <summary>
    /// A self-test for development, run only when a file named
    /// <c>selftest.txt</c> sits in the mod's folder (the release package
    /// never ships one). It continues the latest free-roam session from the
    /// main menu, spawns a DE2 by the player, and puts the engineer through
    /// Yard, waypoint, Road, takeover, and hand-back runs, logging each
    /// second to <c>selftest.log</c> and a PASS or FAIL line per run. It
    /// works the engineer's tablet through its own buttons, saving each
    /// page's screen, and captures the game's frames ("SCREENSHOT" lines).
    /// With "quit" in the trigger file it quits at the end.
    /// </summary>
    internal static partial class SelfTest
    {
        private const double CarLength = Session.CarLength;

        private static bool checkedTrigger;
        private static bool requested;
        private static bool quitAfter;
        private static bool continued;
        private static bool started;
        private static float nextMenuCheck;
        private static StreamWriter log;
        private static int passes;
        private static int failures;
        private static float startTime;

        private static string FolderPath => Main.Mod.Path;

        // The parts asked for, or null for all.
        private static HashSet<string> only;

        // The idle frame time, for the jobs run's measure while driving when the drive part is skipped.
        private static float idleFrameTime;

        private static bool Part(string name) => only == null || only.Contains(name);

        public static bool Requested
        {
            get
            {
                if (!checkedTrigger)
                {
                    checkedTrigger = true;
                    var trigger = Path.Combine(FolderPath, "selftest.txt");
                    requested = File.Exists(trigger);
                    if (requested)
                    {
                        var text = File.ReadAllText(trigger);
                        quitAfter = text.IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0;
                        // "only=jobs,dispatcher": just those parts (settings, drive, jobs, industry, dispatcher, tablet, steam,
                        // helper; "windows", the tablet part's old name, still runs it).
                        foreach (var line in text.Split('\n'))
                        {
                            var l = line.Trim();
                            if (l.StartsWith("only=", StringComparison.OrdinalIgnoreCase))
                                only = new HashSet<string>(l.Substring(5).Split(',').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0));
                            if (l.StartsWith("seed=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l.Substring(5).Trim(), out var seed))
                                industrySeed = seed;
                            if (l.StartsWith("station=", StringComparison.OrdinalIgnoreCase))
                                industryStation = l.Substring(8).Trim();
                        }
                    }
                }
                return requested;
            }
        }

        /// <summary>From Host.Update: at the main menu, continue the session last played.</summary>
        public static void TickMenu()
        {
            if (!Requested || continued || WorldStreamingInit.IsLoaded || Time.unscaledTime < nextMenuCheck)
                return;
            nextMenuCheck = Time.unscaledTime + 1f;
            Once("host", "self-test: requested; waiting for the main menu");
            if (Object.FindObjectOfType<DV.UI.MainMenu>() == null)
                return;
            Once("menu", "self-test: main menu found");
            // Launched with the game's own -continueGame (a developer machine): it continues itself.
            if (Array.Exists(Environment.GetCommandLineArgs(), a => a.StartsWith("-continueGame", StringComparison.Ordinal)))
            {
                Once("native", "self-test: the game continues itself (-continueGame)");
                return;
            }
            if (!menuShot)
            {
                menuShot = true;
                Capture("menu");
            }
            var users = SingletonBehaviour<UserManager>.Instance;
            var user = users != null ? users.CurrentUser : null;
            if (user == null)
            {
                Once("nouser", "self-test: no current user yet");
                return;
            }
            var session = user.CurrentSession;
            if (session?.LatestSave == null && user.CurrentSessionPerMode.TryGetValue("FreeRoam", out var freeRoam))
                session = freeRoam;
            if (session?.LatestSave == null)
            {
                Once("nosave", $"self-test: no session with a save (modes: {string.Join(", ", user.CurrentSessionPerMode.Keys)})");
                return;
            }
            continued = true;
            Main.Log($"self-test: continuing session '{session.Name}' ({session.GameMode})");
            AStartGameData.Continue(session.LatestSave, useSessionDifficulty: true).MakeCurrent();
            SceneSwitcher.SwitchToScene(DVScenes.Game);
        }

        private static readonly HashSet<string> said = new HashSet<string>();
        private static bool menuShot;

        private static void Once(string key, string text)
        {
            if (said.Add(key))
                Main.Log(text);
        }

        /// <summary>The game's own frame, windows and all, to a PNG beside the log.</summary>
        /// <summary>
        /// Some booklet pages as the tablet's Map tab draws them, fitted to its
        /// map's area, with each switch's setting drawn over: selftest-page-*.png.
        /// </summary>
        private static void CaptureBookletPages(Core.Booklet.Booklet booklet)
        {
            var wanted = new[] { "[SM-B]", "[HB-E]", "[HB-A]", "[OWN-B]", "[CS-A]", "[MF-A]", "[Route]" };
            var rt = new RenderTexture(1056, 828, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var flat = new RenderTexture(rt.width, rt.height, 0, RenderTextureFormat.ARGB32);
            var image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            var drawn = 0;
            try
            {
                foreach (var page in booklet.Pages.Where(p => wanted.Contains(p.Source.Name)))
                {
                    var b = page.Box;
                    var scale = Math.Min(rt.width / (b.Width + 100), rt.height / (b.Height + 100));
                    double cx = (b.Left + b.Right) / 2, cy = -(b.Top + b.Bottom) / 2;
                    var ok = Game.BookletRenderer.Render(page, cx, cy, scale, rt, px =>
                    {
                        var switches = page.Schematic.Switches;
                        for (var i = 0; i < switches.Count; i++)
                        {
                            var s = switches[i];
                            if (s.Junction < 0)
                                continue;
                            var leg = page.LegOf(i, Host.Network.Junctions[s.Junction].selectedBranch);
                            if (leg == null)
                                continue;
                            var d = s.LegDirections[(int)leg.Value];
                            Game.BookletRenderer.Line(px(s.Point.X, s.Point.Z), px(s.Point.X + d.X * 24, s.Point.Z + d.Z * 24), 6, new Color(0.1f, 0.6f, 0.2f, 0.9f));
                        }
                    });
                    if (!ok)
                    {
                        Line($"booklet page {page} not drawn");
                        continue;
                    }
                    Graphics.Blit(rt, flat);
                    var previous = RenderTexture.active;
                    RenderTexture.active = flat;
                    image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    image.Apply();
                    RenderTexture.active = previous;
                    File.WriteAllBytes(Path.Combine(FolderPath, $"selftest-page-{page.Source.Name.Trim('[', ']')}.png"), image.EncodeToPNG());
                    drawn++;
                }
            }
            finally
            {
                Game.BookletRenderer.Hide();
                rt.Release();
                flat.Release();
                UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(flat);
                UnityEngine.Object.Destroy(image);
            }
            Line($"booklet pages drawn: {drawn}");
        }

        private static void Capture(string name)
        {
            try
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(FolderPath, $"selftest-{name}.png"));
            }
            catch (Exception e)
            {
                Main.Warn($"self-test: couldn't capture {name}: {e.GetBaseException().Message}");
            }
        }

        public static void OnWorldLoaded()
        {
            if (!Requested || started || Host.Instance == null)
                return;
            started = true;
            Host.Instance.StartCoroutine(Guarded(Run()));
        }

        /// <summary>Runs a coroutine, logging any exception as a failure instead of losing it.</summary>
        private static IEnumerator Guarded(IEnumerator inner)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext())
                        yield break;
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    Fail("self-test", "threw " + e);
                    Finish();
                    yield break;
                }
                yield return current;
            }
        }

        private static IEnumerator Run()
        {
            log = new StreamWriter(Path.Combine(FolderPath, "selftest.log"), false) { AutoFlush = true };
            startTime = Time.realtimeSinceStartup;
            Line($"self-test start, game {Application.version}");
            yield return new WaitForSeconds(20);

            var world = Host.World;
            if (world == null)
            {
                Fail("network", "no track network");
                Finish();
                yield break;
            }
            var graph = world.Graph;
            var deadEnds = 0;
            for (var t = 0; t < graph.TrackCount; t++)
            {
                if (graph.Links(t, TrackEnd.Start).Length == 0)
                    deadEnds++;
                if (graph.Links(t, TrackEnd.End).Length == 0)
                    deadEnds++;
            }
            Pass("network", $"{graph.TrackCount} tracks, {graph.JunctionCount} switches, {deadEnds} dead ends");
            Host.Network.Export(Path.Combine(FolderPath, "selftest-network.json"));
            // The booklet's pages, read and matched to the network as the tablet's Map tab reads them.
            var booklet = Game.BookletReader.Get(Host.Network);
            if (booklet == null)
                Fail("booklet", "the game's station booklet couldn't be read (see the log)");
            else
            {
                Game.BookletReader.Export(Path.Combine(FolderPath, "selftest-booklet.json"));
                var stats = booklet.Stats;
                var ok = booklet.Stations.Count > 0 && stats.SwitchesNamed == stats.Switches && stats.Matched >= stats.Runs * 0.95;
                (ok ? (Action<string, string>)Pass : Fail)("booklet", $"{booklet.Stations.Count} stations: {stats}");
                CaptureBookletPages(booklet);
            }

            var idleFrame = 0f;
            yield return MeasureFrames(5, v => idleFrame = v);
            Line($"idle frame time {idleFrame * 1000:F2} ms");

            var loco = SpawnTestLoco();
            if (loco == null)
            {
                Fail("spawn", "no DE2 could be spawned and no drivable loco is near");
                Finish();
                yield break;
            }
            Line($"test loco {loco.ID} ({loco.carLivery?.id}) at {Abs(loco)}");
            Capture("spawned");
            yield return new WaitForSeconds(5);
            var session = Host.SessionFor(loco, true);
            if (session.Unsupported != null)
            {
                Fail("loco", session.Unsupported);
                Finish();
                yield break;
            }

            if (only != null)
                Line($"only: {string.Join(", ", only)}");
            idleFrameTime = idleFrame;
            if (Part("settings"))
                yield return SettingsPart();
            if (Part("drive"))
            {
                yield return Yard(session, forward: true, lengths: 2);
                yield return Yard(session, forward: false, lengths: 2);

                var drivingFrame = 0f;
                yield return WaypointWithSwitch(session, reversals: 0, measure: v => drivingFrame = v);
                if (drivingFrame > 0)
                    Line($"frame time while driving {drivingFrame * 1000:F2} ms (idle was {idleFrame * 1000:F2} ms)");
                yield return WaypointWithSwitch(session, reversals: 1, measure: null);
                yield return Road(session);
                yield return Takeover(session);
                yield return OffSecures(session);
            }
            if (Part("jobs"))
                yield return JobsWorked(session);
            if (Part("industry"))
                yield return Industry(session);
            if (Part("dispatcher"))
                yield return Dispatcher(session);
            if (Part("tablet") || Part("windows"))
                yield return TabletPart(session);
            if (Part("steam"))
                yield return SteamPart();
            if (Part("helper"))
                yield return HelperPart();

            Line($"slowest engineer tick {Session.SlowestTickMs:F2} ms; slowest job-run tick {Session.SlowestJobsTickMs:F2} ms");
            Finish();
        }

        /// <summary>A Settings.xml as 0.14.0 saved it, every value off its default, the old windows' keys and switches in it.</summary>
        private const string OldSettingsXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Settings xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <WindowKey><keyCode>E</keyCode><modifiers>1</modifiers></WindowKey>
  <TabletKey><keyCode>F8</keyCode><modifiers>0</modifiers></TabletKey>
  <DispatchKey><keyCode>F9</keyCode><modifiers>2</modifiers></DispatchKey>
  <MapKey><keyCode>M</keyCode><modifiers>1</modifiers></MapKey>
  <MaxSpeedKmh>61</MaxSpeedKmh>
  <YardSpeedKmh>10</YardSpeedKmh>
  <YardTrackCapKmh>25</YardTrackCapKmh>
  <CarGap>9</CarGap>
  <TrainDeceleration>0.3</TrainDeceleration>
  <LightDeceleration>0.5</LightDeceleration>
  <SlowAirDeceleration>0.1</SlowAirDeceleration>
  <TickRate>7</TickRate>
  <AutoStart>false</AutoStart>
  <CursorWithOrders>false</CursorWithOrders>
  <FlipMap>true</FlipMap>
  <WhistleShortest>0.4</WhistleShortest>
  <WhistleShortestLong>1.6</WhistleShortestLong>
  <WhistleQuietEnds>2</WhistleQuietEnds>
  <WhistleBounce>0.25</WhistleBounce>
  <WhistleDip>0.5</WhistleDip>
  <LogWhistle>false</LogWhistle>
  <WhistleVersion>1</WhistleVersion>
  <ShowNotifications>false</ShowNotifications>
  <LogRoutes>true</LogRoutes>
  <KeysVersion>2</KeysVersion>
</Settings>";

        /// <summary>
        /// The settings (DVCommsTablet decision 20): a Settings.xml saved by
        /// 0.14.0 loads in the game's own runtime, as Unity Mod Manager loads
        /// it, keeping its values and dropping the old windows' (nor are the
        /// visibility rules saved); the rows the Comms Tablet's Settings app
        /// reads are Driving, Braking, Display, and Keys (Keys hidden while
        /// merged), none a developer's, no label over 60 characters and no
        /// tooltip line over 80 (Unity Mod Manager's tooltip box doesn't wrap);
        /// this mod's window adds the developer settings; and the window, its
        /// developer settings shown, draws as Unity Mod Manager draws it.
        /// </summary>
        private static IEnumerator SettingsPart()
        {
            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Settings));
                Settings old;
                using (var reader = new StringReader(OldSettingsXml))
                    old = (Settings)serializer.Deserialize(reader);
                string written;
                using (var writer = new StringWriter())
                {
                    serializer.Serialize(writer, old);
                    written = writer.ToString();
                }
                var kept = old.TabletKey != null && old.TabletKey.keyCode == KeyCode.F8 && old.TabletKey.modifiers == 0 && old.MaxSpeedKmh == 61 && old.YardSpeedKmh == 10
                    && old.YardTrackCapKmh == 25 && old.CarGap == 9 && Math.Abs(old.SlowAirDeceleration - 0.1f) < 1e-5f && old.TickRate == 7 && !old.AutoStart
                    && !old.ShowNotifications && old.LogRoutes && !old.LogWhistle && Math.Abs(old.WhistleShortestLong - 1.6f) < 1e-5f && old.KeysVersion == 2 && !old.ShowDeveloper;
                var saved = new[] { "WindowKey", "DispatchKey", "MapKey", "CursorWithOrders", "FlipMap", "InModWindow", "DeveloperShown", "Merged" }
                    .Where(n => written.Contains("<" + n + ">") || written.Contains("<" + n + " ")).ToList();
                (kept && saved.Count == 0 ? (Action<string, string>)Pass : Fail)("settings old file",
                    $"0.14.0's file loaded: values {(kept ? "kept" : "NOT kept")} (top speed {old.MaxSpeedKmh}, tablet key {old.TabletKey?.keyCode}, ticks {old.TickRate}, log routes {old.LogRoutes}); " +
                    $"saved again {(saved.Count == 0 ? "without the old windows' fields or the rules" : "WITH " + string.Join(", ", saved))}");
            }
            catch (Exception e)
            {
                Fail("settings old file", $"{e.GetType().Name}: {e.GetBaseException().Message}");
            }

            var merged = CommsTabletLink.Merged;
            var developer = new[] { "Show developer settings", "Engineer ticks per second", "Log each route planned", "Log what the helper's whistle hears" };
            var tablet = SettingsRows(Main.Settings, inWindow: false, developerShown: false);
            var window = SettingsRows(Main.Settings, inWindow: true, developerShown: false);
            var open = SettingsRows(Main.Settings, inWindow: true, developerShown: true);
            var headings = tablet.Where(r => r.StartsWith("# ", StringComparison.Ordinal)).Select(r => r.Substring(2)).ToList();
            var wanted = merged ? new[] { "Driving", "Braking", "Display" } : new[] { "Driving", "Braking", "Display", "Keys" };
            var long_ = open.Where(r => r.Length > 60 && !r.StartsWith("? ", StringComparison.Ordinal)
                || r.StartsWith("? ", StringComparison.Ordinal) && r.Substring(2).Split('\n').Any(l => l.Length > 80)).ToList();
            tablet.RemoveAll(r => r.StartsWith("? ", StringComparison.Ordinal));
            window.RemoveAll(r => r.StartsWith("? ", StringComparison.Ordinal));
            open.RemoveAll(r => r.StartsWith("? ", StringComparison.Ordinal));
            var ok = headings.SequenceEqual(wanted) && !tablet.Intersect(developer).Any() && tablet.Contains("Engineer's tablet") != merged
                && window.Contains("Show developer settings") && !window.Intersect(developer.Skip(1)).Any() && developer.All(open.Contains)
                && open.Contains("# Developer") && long_.Count == 0;
            (ok ? (Action<string, string>)Pass : Fail)("settings rows",
                $"merged {merged}; the Comms Tablet's: {string.Join(" | ", tablet)}; the window's adds: {string.Join(" | ", open.Except(tablet))}" +
                (long_.Count > 0 ? $"; too long: {string.Join(" | ", long_)}" : ""));

            SettingsDrawer.Drawn = null;
            SettingsDrawer.DrawForTest = 20;
            for (var i = 0; i < 60 && SettingsDrawer.DrawForTest > 0; i++)
                yield return null;
            (SettingsDrawer.Drawn == "" ? (Action<string, string>)Pass : Fail)("settings window",
                SettingsDrawer.Drawn == "" ? "drawn as Unity Mod Manager draws it, the developer settings and the whistle timings shown" : SettingsDrawer.Drawn ?? "never drawn");
        }

        /// <summary>
        /// The settings' rows as Unity Mod Manager and the Comms Tablet read
        /// them: each Draw field in order, under its Header ("# Driving"),
        /// then its tooltip ("? ..."), skipped by its VisibleOn or InvisibleOn
        /// rule, with the window's flag and the developer switch set as asked.
        /// </summary>
        private static List<string> SettingsRows(Settings settings, bool inWindow, bool developerShown)
        {
            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var rows = new List<string>();
            var wasShown = settings.ShowDeveloper;
            Main.InSettingsWindow = inWindow;
            settings.ShowDeveloper = developerShown;
            try
            {
                bool Holds(string rule)
                {
                    var parts = rule.Split('|');
                    var name = parts[0].TrimStart('#');
                    var value = parts[0].StartsWith("#", StringComparison.Ordinal)
                        ? typeof(Settings).GetProperty(name, Instance).GetValue(settings, null)
                        : typeof(Settings).GetField(name, Instance).GetValue(settings);
                    return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), parts[1], StringComparison.OrdinalIgnoreCase);
                }
                foreach (var f in typeof(Settings).GetFields(Instance))
                {
                    var draw = f.GetCustomAttributes(typeof(UnityModManagerNet.DrawAttribute), false).OfType<UnityModManagerNet.DrawAttribute>().LastOrDefault();
                    if (draw == null || (!string.IsNullOrEmpty(draw.VisibleOn) ? !Holds(draw.VisibleOn) : !string.IsNullOrEmpty(draw.InvisibleOn) && Holds(draw.InvisibleOn)))
                        continue;
                    foreach (HeaderAttribute header in f.GetCustomAttributes(typeof(HeaderAttribute), false))
                        rows.Add("# " + header.header);
                    rows.Add(draw.Label ?? f.Name);
                    if (!string.IsNullOrEmpty(draw.Tooltip))
                        rows.Add("? " + draw.Tooltip);
                }
            }
            finally
            {
                Main.InSettingsWindow = false;
                settings.ShowDeveloper = wasShown;
            }
            return rows;
        }

        /// <summary>
        /// A stock S060 (a tank engine: its own coal and water), brought up
        /// hot as the game's Startup does, moved three car lengths forward and
        /// back by the engineer, which fires it itself (no Realistic Firing
        /// in the clone): its fire, water, and controls logged as it goes.
        /// </summary>
        private static IEnumerator SteamPart()
        {
            var steam = SpawnLoco("LocoS060");
            if (steam == null)
            {
                Fail("steam", "no S060 could be spawned");
                yield break;
            }
            yield return new WaitForSeconds(3);
            DV.Simulation.Controllers.StartupHelper.Startup(steam);
            yield return new WaitForSeconds(8);
            var s = Host.SessionFor(steam, true);
            if (s == null || s.Unsupported != null || s.Steam == null)
            {
                Fail("steam", s == null ? "no session" : s.Unsupported ?? "no steam crew for a boiler loco");
                yield break;
            }
            Line($"steam: {steam.ID} ({steam.carLivery.id}): {s.Steam.Describe()}");
            foreach (var forward in new[] { true, false })
            {
                var start = steam.transform.position;
                s.SetYard(forward, 3 * CarLength);
                var moved = 0f;
                for (var i = 0; i < 24; i++)
                {
                    yield return new WaitForSeconds(5);
                    moved = (steam.transform.position - start).magnitude;
                    Line($"steam {(forward ? "forward" : "back")} {i * 5 + 5} s: {moved:F1} m, {s.Engineer.Status}; regulator {s.Controls.Throttle:F2}, reverser {s.Controls.Reverser:F2}; {s.Steam.Describe()}");
                    if (moved > 30 && (s.Engineer.Status ?? "").EndsWith("done", StringComparison.Ordinal))
                        break;
                }
                (moved > 25 ? (Action<string, string>)Pass : Fail)($"steam yard {(forward ? "forward" : "back")}", $"{steam.ID} moved {moved:F1} m of {3 * CarLength:F0}; {s.Steam.Describe()}");
            }
            s.Stop(secure: true);
            yield return new WaitForSeconds(2);
            Line($"steam: let go: {s.Steam.Describe()}");
        }

 private static IEnumerator HelperPart()
        {
            var t = Main.Settings.Whistle;
            Line($"helper: whistle timings: a blip under {t.Shortest:0.##} s, long from {t.ShortestLong:0.##} s, a signal ends {t.QuietEnds:0.##} s after, debounce {t.Bounce:0.##} s, settings version {Main.Settings.WhistleVersion}");
            var used = new HashSet<int>();
            yield return HelperPair("helper", new[] { "LocoDE2", "LocoS060" }, new[] { false, false }, 1, false, used);
            yield return HelperPair("helper s282", new[] { "LocoS060", "LocoS282A", "LocoS282B" }, new[] { false, false, false }, 1, true, used);
        }

        private static readonly FieldInfo CylinderDamage = AccessTools.Field(typeof(LocoSim.Implementations.ReciprocatingSteamEngine), "waterInCylindersDamageMeter");
        private static readonly FieldInfo SimParams = AccessTools.Field(typeof(LocoSim.Implementations.SimComponent), "gameParams");

        private static IEnumerator HelperPair(string name, string[] liveries, bool[] reversed, int helperAt, bool rough, HashSet<int> used)
        {
            var world = Host.World;
            var from = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position - WorldMover.currentMove : Vector3.zero;
            var spot = Dispatch.Yards(world.Network, new Vector2(from.x, from.z)).SelectMany(y => y.Tracks)
                .FirstOrDefault(t => t.Length > 200 && !used.Contains(t.Track) && !world.Occupancy.IsOccupied(t.Track, int.MinValue) && !t.DeadEnd);
            var types = liveries.Select(id => DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id == id)).ToList();
            if (spot == null || types.Any(t => t == null))
            {
                Fail(name, $"no free track, or no {string.Join(" or ", liveries)} livery");
                yield break;
            }
            used.Add(spot.Track);
            var cars = CarSpawner.Instance.SpawnCarTypesOnTrack(types, reversed.ToList(), world.Network.Tracks[spot.Track], false, false);
            if (cars == null || cars.Count < liveries.Length)
            {
                Fail(name, "the train couldn't be spawned");
                yield break;
            }
            var lead = cars[0];
            var helperLoco = cars[helperAt];
            yield return new WaitForSeconds(3);
            DV.Simulation.Controllers.StartupHelper.Startup(lead);
            DV.Simulation.Controllers.StartupHelper.Startup(helperLoco);
            yield return new WaitForSeconds(10);
            var coupled = lead.trainset != null && lead.trainset == helperLoco.trainset;
            Line($"{name}: {lead.ID} (lead) and {helperLoco.ID} on {spot.Id}, {(coupled ? "coupled" : "not coupled")}, " +
                $"facing {(Vector3.Dot(lead.transform.forward, helperLoco.transform.forward) > 0 ? "the same way" : "the other way")}");
            var leadControls = lead.SimController.controlsOverrider;
            var horn = leadControls.Horn;
            var ownWhistle = helperLoco.SimController.controlsOverrider.Horn;

            // The lead's whistle swept from 0 to 1, as it sounds: how far a player eases the cord for a gap the ear hears.
            var sound = new WhistleSound(lead);
            var sweep = new List<(float Cord, float Loud)>();
            for (var c = 0; c <= 10; c++)
            {
                for (var t0 = Time.time; Time.time - t0 < 0.5f;)
                {
                    horn.Set(c / 10f);
                    sound.Level(Time.time);
                    yield return null;
                }
                sweep.Add((c / 10f, sound.Level(Time.time)));
            }
            horn.Set(0f);
            var full = Mathf.Max(0.01f, sweep[sweep.Count - 1].Loud);
            var eased = sweep.Where(p => p.Cord > 0 && p.Loud <= 0.4f * full).Select(p => p.Cord).DefaultIfEmpty(0.1f).Max();
            Line($"{name}: lead whistle at rest {horn.Value:F2}, neutral at 0 {horn.neutralAt0}; heard from {sound.Source}; " +
                $"loudness by cord: {string.Join(" ", sweep.Select(p => $"{p.Cord:F1}:{p.Loud:F2}"))}; eased between blasts to {eased:F1}");
            yield return new WaitForSeconds(3);

            var s = Host.SessionFor(helperLoco, true);
            var why = s?.SetHelper(lead);
            if (why != null || s == null)
            {
                Fail(name, why ?? "no session");
                yield break;
            }
            var engine = helperLoco.SimController.simFlow.OrderedSimComps.OfType<LocoSim.Implementations.ReciprocatingSteamEngine>().FirstOrDefault();
            float Damage() => engine != null && CylinderDamage != null ? (float)CylinderDamage.GetValue(engine) : 0f;
            var failures = engine != null && SimParams?.GetValue(engine) is LocoSim.Implementations.SimGameParams gp && gp.DrivetrainFailuresAllowed;
            Line($"{name}: drivetrain failures {(failures ? "allowed: water in shut cylinders would damage them" : "not allowed in this session: the damage meter can't rise")}");

            // The player's hand on the cord, each frame. Rough: each blast quilled (swelling between
            // 70% and full), the cord bouncing (out a tenth of a second every half second), and eased
            // between blasts to a sigh, not let go; else clean blasts, let go between.
            IEnumerator Blow(params float[] blasts)
            {
                foreach (var b in blasts)
                {
                    for (var t0 = Time.time; Time.time - t0 < b;)
                    {
                        var t = Time.time - t0;
                        var level = rough ? 0.85f + 0.15f * Mathf.Cos(t * 2 * Mathf.PI / 0.65f) : 1f;
                        horn.Set(rough && t > 0.3f && t % 0.5f < 0.1f ? 0f : level);
                        yield return null;
                    }
                    for (var t0 = Time.time; Time.time - t0 < 0.8f;)
                    {
                        horn.Set(rough ? eased : 0f);
                        yield return null;
                    }
                }
                horn.Set(0f);
            }
            // Watches for its answer while the train does what it does: the most steam it took, its cocks, its cylinders' water and damage.
            var answered = false;
            var working = 0f;
            var cocksSeen = false;
            var water = 0f;
            var damage = 0f;
            IEnumerator Watch(float seconds)
            {
                answered = false;
                working = 0f;
                cocksSeen = false;
                for (var t0 = Time.time; Time.time - t0 < seconds;)
                {
                    answered |= Game.Helper.Sounding(ownWhistle) > 0.5f;
                    working = Mathf.Max(working, s.Steam?.Working ?? 0f);
                    cocksSeen |= s.Steam != null && s.Steam.CocksOpen;
                    water = Mathf.Max(water, s.Steam?.CylinderWater ?? 0f);
                    damage = Mathf.Max(damage, Damage());
                    yield return null;
                }
            }
            float Along(Vector3 at) => Vector3.Dot(Abs(lead) - at, lead.transform.forward);

            // Two long, the lead set ahead, its brakes off and throttle open: the helper answers, takes steam, and the train goes ahead.
            leadControls.Reverser.Set(1f);
            leadControls.IndependentBrake?.Set(0f);
            leadControls.Brake?.Set(0f);
            var at = Abs(lead);
            yield return Blow(2.4f, 2.4f);
            leadControls.Throttle.Set(0.5f);
            yield return Watch(6);
            var regulator = s.Controls.Throttle;
            var moved = Along(at);
            var reverserOk = (s.Controls.Reverser > 0.5f) == (Vector3.Dot(lead.transform.forward, helperLoco.transform.forward) > 0);
            Line($"{name} after two long: {s.Engineer.Status}; regulator {regulator:F2}, reverser {s.Controls.Reverser:F2}; answered {answered}; most steam {working:F2}; " +
                $"cocks opened {cocksSeen}, most water in its cylinders {water:F3}, damage {damage:F3}; lead moved {moved:F1} m; {s.Steam?.Describe()}");
            (regulator > 0.3f && answered && reverserOk ? (Action<string, string>)Pass : Fail)($"{name} go",
                $"two long: answered {answered}, regulator {regulator:F2} with the lead's at 0.50, reverser {s.Controls.Reverser:F2} ({(reverserOk ? "right" : "wrong")} for the way it faces); {s.Engineer.Status}");
            (working > 0.05f && moved > 0.3f ? (Action<string, string>)Pass : Fail)($"{name} pulls", $"most steam to its cylinders {working:F2}; the lead moved {moved:F1} m ahead");
            (cocksSeen && damage < 0.01f ? (Action<string, string>)Pass : Fail)($"{name} cocks",
                $"cocks opened {cocksSeen} taking steam; most water in its cylinders {water:F3}, damage meter {damage:F3}; {s.Steam?.Describe()}");
            if (s.Steam != null && s.Steam.Realistic)
            {
                var d = s.Steam.Describe();
                (d.Contains("fireman Road") ? (Action<string, string>)Pass : Fail)($"{name} fireman", d);
            }

            // One short, the lead shut off and its brake on: the helper shuts off.
            leadControls.Throttle.Set(0f);
            yield return Blow(0.7f);
            leadControls.IndependentBrake?.Set(1f);
            yield return Watch(4);
            Line($"{name} after one short: {s.Engineer.Status}; regulator {s.Controls.Throttle:F2}; answered {answered}");
            (s.Controls.Throttle < 0.05f && answered ? (Action<string, string>)Pass : Fail)($"{name} stop", $"one short: regulator {s.Controls.Throttle:F2}, answered {answered}; {s.Engineer.Status}");
            if (rough)
            {
                leadControls.Throttle.Set(0f);
                s.Stop(secure: true);
                yield return new WaitForSeconds(2);
                yield break;
            }

            // A blip (a brush of the cord): nothing heard, no answer.
            var status = s.Engineer.Status;
            for (var t0 = Time.time; Time.time - t0 < 0.2f;)
            {
                horn.Set(1f);
                yield return null;
            }
            horn.Set(0f);
            yield return Watch(4);
            (!answered && s.Engineer.Status == status ? (Action<string, string>)Pass : Fail)($"{name} blip", $"a 0.2 s blip: answered {answered}; {s.Engineer.Status}");

            // Standing, three short, the lead set back, its brake off: it works the other way and the train backs.
            for (var i = 0; i < 30 && Math.Abs(s.State.SpeedTowardA) > 0.1; i++)
                yield return new WaitForSeconds(1);
            var before = s.Controls.Reverser;
            leadControls.Reverser.Set(0f);
            yield return Blow(0.7f, 0.7f, 0.7f);
            // As a player would: the answer heard, then the brake off and the throttle open.
            yield return Watch(3);
            var answeredBack = answered;
            leadControls.IndependentBrake?.Set(0f);
            at = Abs(lead);
            leadControls.Throttle.Set(0.4f);
            yield return Watch(6);
            answered = answeredBack;
            var turned = (before > 0.5f) != (s.Controls.Reverser > 0.5f);
            moved = Along(at);
            Line($"{name} after three short: {s.Engineer.Status}; regulator {s.Controls.Throttle:F2}, reverser {before:F2} to {s.Controls.Reverser:F2}; answered {answered}; most steam {working:F2}; lead moved {moved:F1} m");
            (turned && s.Controls.Throttle > 0.2f && answered && moved < -0.3f ? (Action<string, string>)Pass : Fail)($"{name} back",
                $"three short: reverser {before:F2} to {s.Controls.Reverser:F2}, regulator {s.Controls.Throttle:F2}, answered {answered}, the lead moved {moved:F1} m; {s.Engineer.Status}");
            leadControls.Throttle.Set(0f);
            leadControls.IndependentBrake?.Set(1f);
            s.Stop(secure: true);
            yield return new WaitForSeconds(2);
        }

        private static void Finish()
        {
            Line($"self-test done: {passes} passed, {failures} failed, {Time.realtimeSinceStartup - startTime:F0} s");
            log?.Dispose();
            log = null;
            if (quitAfter)
                Host.Instance.StartCoroutine(QuitSoon());
        }

        private static IEnumerator QuitSoon()
        {
            yield return new WaitForSeconds(3);
            Application.Quit();
        }

        // ---- The runs ----

        private static IEnumerator Yard(Session s, bool forward, int lengths)
        {
            var name = $"yard {(forward ? "forward" : "back")} {lengths} lengths";
            var loco = s.Loco;
            var start = Abs(loco);
            var axis = loco.transform.forward;
            var expected = (forward ? 1 : -1) * lengths * CarLength;
            s.SetYard(forward, lengths * CarLength);
            var maxSpeed = 0f;
            var elapsed = 0f;
            var moved = false;
            while (elapsed < 150)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                var v = Mathf.Abs(loco.GetForwardSpeed());
                maxSpeed = Mathf.Max(maxSpeed, v);
                moved |= v > 0.3f;
                Trace(s, name, elapsed);
                if (moved && v < 0.05f && (s.Engineer.Status == "Yard move done" || s.Engineer.Status.StartsWith("Stopped", StringComparison.Ordinal)))
                    break;
            }
            var along = Vector3.Dot(Abs(loco) - start, axis);
            var detail = $"moved {along:F1} m (asked {expected:F1}), top {maxSpeed * 3.6f:F1} km/h, {elapsed:F0} s, status '{s.Engineer.Status}'";
            var rightWay = Math.Sign(along) == Math.Sign(expected) && Math.Abs(along) > 2;
            var stoppedShort = s.Engineer.Status.StartsWith("Stopped", StringComparison.Ordinal);
            if (rightWay && (Math.Abs(along - expected) < 4 || stoppedShort))
                Pass(name, detail);
            else
                Fail(name, detail);
        }

        /// <summary>
        /// The nearest goal, within 900 m, down a switch's branch it isn't set
        /// for, that a route reaches with the given number of reversals.
        /// </summary>
        private static Route FindSwitchGoal(TrainState state, MapPoint here, int reversals, out TrackPosition goal, out int junction, out int branch)
        {
            var world = Host.World;
            var graph = world.Graph;
            Route best = null;
            goal = default;
            junction = -1;
            branch = -1;
            for (var j = 0; j < graph.JunctionCount; j++)
            {
                var info = graph.Junction(j);
                if (info.Position.DistanceTo(here) > 900)
                    continue;
                var selected = world.SelectedBranch(j);
                for (var b = 0; b < info.Branches.Length; b++)
                {
                    if (b == selected)
                        continue;
                    // A branch is the switch's own short turnout piece (about 24 m in the
                    // game): the goal goes 70 m on past the points, along the track as set.
                    var (track, end) = info.Branches[b];
                    var candidate = TrackWalker.Advance(graph, new TrackPosition(track, graph.OffsetOf(track, end)),
                        end.DirectionInto(), 70, world.SelectedBranchFunc, out _);
                    if (graph.Length(candidate.Track) < 20 || world.Occupancy.IsOccupied(candidate.Track, state.Owner))
                        continue;
                    var route = world.Router.Find(new RouteRequest
                    {
                        EndA = state.EndA,
                        OutwardA = state.OutwardA,
                        EndB = state.EndB,
                        OutwardB = state.OutwardB,
                        TrainLength = state.Length,
                        Goal = candidate,
                        CanSetJunction = (jj, bb) => SwitchRules.Usable(world, jj, bb, state.Owner),
                    });
                    if (route == null || route.Reversals != reversals || route.Length > 1500)
                        continue;
                    var throws = false;
                    foreach (var leg in route.Legs)
                    {
                        foreach (var sw in leg.Switches)
                            throws |= sw.Junction == j && sw.Branch == b;
                    }
                    if (!throws || (best != null && route.Length >= best.Length))
                        continue;
                    best = route;
                    goal = candidate;
                    junction = j;
                    branch = b;
                }
            }
            return best;
        }

        private static IEnumerator WaypointWithSwitch(Session s, int reversals, Action<float> measure)
        {
            var name = reversals == 0 ? "waypoint through a switch" : "waypoint by a switchback";
            var world = Host.World;
            world.ScanCars();
            var state = s.Read();
            var here = Abs(s.Loco);
            var best = FindSwitchGoal(state, new MapPoint(here.x, here.z), reversals, out var goal, out var bestJunction, out var bestBranch);
            if (best == null)
            {
                Line($"  train: A {state.EndA} out {state.OutwardA}, B {state.EndB} out {state.OutwardB}, length {state.Length:F1}, owner {state.Owner}");
                Line($"SKIP {name}: no such route within 900 m");
                yield break;
            }
            Line($"{name}: goal {goal} ({Host.Network.Describe(goal.Track)}), route {best.Length:F0} m, {best.Reversals} reversals, switch {bestJunction} to {bestBranch}");
            s.Engineer.GoTo(new Waypoint(goal) { Label = "self-test" });
            var elapsed = 0f;
            var frames = 0f;
            var frameCount = 0;
            while (elapsed < 420 && s.Engineer.Queue.Count > 0 && s.Engineer.Mode == EngineerMode.Waypoints)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                frames += Time.unscaledDeltaTime;
                frameCount++;
                Trace(s, name, elapsed);
            }
            measure?.Invoke(frameCount > 0 ? frames / frameCount : 0);
            var after = s.Read();
            var gap = Math.Min(Gap(after.EndA, goal), Gap(after.EndB, goal));
            var detail = $"{elapsed:F0} s, leading end {gap:F1} m from the goal, switch now {world.SelectedBranch(bestJunction)}, status '{s.Engineer.Status}'";
            if (s.Engineer.Queue.Count == 0 && gap < 6 && world.SelectedBranch(bestJunction) == bestBranch)
                Pass(name, detail);
            else
                Fail(name, detail);
            if (s.Engineer.Mode != EngineerMode.Off)
                s.Engineer.SetWaypoints();
        }

        private static double Gap(TrackPosition end, TrackPosition goal) =>
            end.Track == goal.Track ? Math.Abs(end.Offset - goal.Offset) : double.PositiveInfinity;

        private static IEnumerator Road(Session s)
        {
            const string name = "road 20 km/h then 0";
            var forward = RoomierWay(s);
            s.SetRoad(forward, 20 / 3.6);
            var top = 0f;
            var elapsed = 0f;
            while (elapsed < 45)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                top = Mathf.Max(top, Mathf.Abs(s.Loco.GetForwardSpeed()));
                Trace(s, name, elapsed);
                if (elapsed > 10 && s.Engineer.Status.StartsWith("Stopped", StringComparison.Ordinal))
                    break;
            }
            var stoppedEarly = s.Engineer.Status.StartsWith("Stopped", StringComparison.Ordinal);
            s.SetRoad(forward, 0);
            elapsed = 0;
            while (elapsed < 45 && Mathf.Abs(s.Loco.GetForwardSpeed()) > 0.05f)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                Trace(s, name, elapsed);
            }
            var held = Abs(s.Loco);
            yield return new WaitForSeconds(8);
            var drift = (Abs(s.Loco) - held).magnitude;
            var detail = $"top {top * 3.6f:F1} km/h{(stoppedEarly ? " (stopped early: " + s.Engineer.Status + ")" : "")}, stopped in {elapsed:F0} s, drift while held {drift:F2} m";
            var speedOk = stoppedEarly || (top * 3.6f > 14 && top * 3.6f < 23.5f);
            if (speedOk && drift < 0.3f && Mathf.Abs(s.Loco.GetForwardSpeed()) < 0.05f)
                Pass(name, detail);
            else
                Fail(name, detail);
        }

        private static IEnumerator Takeover(Session s)
        {
            const string name = "player takeover";
            s.SetRoad(RoomierWay(s), 10 / 3.6);
            var elapsed = 0f;
            while (elapsed < 40 && Mathf.Abs(s.Loco.GetForwardSpeed()) < 1f)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                Trace(s, name, elapsed);
            }
            yield return new WaitForSeconds(3);
            var throttle = s.Loco.SimController.controlsOverrider.Throttle;
            // With no one aboard, the throttle eased a notch (as the wheel-slip gadget does): followed, not let go of.
            var eased = throttle.Value >= 0.12f;
            if (eased)
            {
                throttle.Set(Mathf.Max(0f, throttle.Value - 0.15f));
                yield return new WaitForSeconds(2);
                if (s.Engineer.Mode == EngineerMode.Off)
                {
                    Fail(name, "the engineer let go when the throttle was only eased, with no one aboard");
                    yield break;
                }
            }
            // And moved up, by no one aboard: a quarter more than the engineer has it. It stops the train and lets go.
            throttle.Set(Mathf.Clamp01(throttle.Value + 0.25f));
            yield return new WaitForSeconds(3);
            var off = s.Engineer.Mode == EngineerMode.Off;
            var secured = throttle.Value < 0.01f;
            // Stop the loco as a player would.
            var o = s.Loco.SimController.controlsOverrider;
            o.Throttle.Set(0);
            o.IndependentBrake?.Set(1);
            o.Brake?.Set(1);
            elapsed = 0;
            while (elapsed < 30 && Mathf.Abs(s.Loco.GetForwardSpeed()) > 0.05f)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
            }
            if (off && secured)
                Pass(name, $"the engineer let go within 3 s of the throttle moving up, power off{(eased ? "; an eased throttle it followed" : "")}");
            else if (off)
                Fail(name, "the engineer let go with no one aboard and left the throttle open");
            else
                Fail(name, $"the engineer kept driving: mode {s.Engineer.Mode}, status '{s.Engineer.Status}'");
        }

        private static IEnumerator OffSecures(Session s)
        {
            const string name = "off stops the train";
            s.SetRoad(RoomierWay(s), 12 / 3.6);
            var elapsed = 0f;
            while (elapsed < 40 && Mathf.Abs(s.Loco.GetForwardSpeed()) < 2f)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                Trace(s, name, elapsed);
            }
            var speed = Mathf.Abs(s.Loco.GetForwardSpeed());
            s.Stop(secure: true);
            elapsed = 0;
            while (elapsed < 60 && Mathf.Abs(s.Loco.GetForwardSpeed()) > 0.05f)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
            }
            var detail = $"from {speed * 3.6f:F1} km/h, stopped after {elapsed:F0} s";
            if (Mathf.Abs(s.Loco.GetForwardSpeed()) <= 0.05f)
                Pass(name, detail);
            else
                Fail(name, detail);
        }

        /// <summary>
        /// The tablet part: the engineer given a route to draw (to a switch's
        /// far branch, running), a cut spawned in a yard to pick on the map,
        /// then the tablet's tour.
        /// </summary>
        private static IEnumerator TabletPart(Session s)
        {
            Host.World.ScanCars();
            var state = s.Read();
            var here = Abs(s.Loco);
            if (FindSwitchGoal(state, new MapPoint(here.x, here.z), 0, out var goal, out _, out _) != null)
                s.Engineer.GoTo(new Waypoint(goal) { Label = "map test" });
            yield return new WaitForSeconds(4);
            var world = Host.World;
            var from = Dispatch.MapPosition(s.Loco);
            List<TrainCar> spawned = null;
            foreach (var yard in Dispatch.Yards(world.Network, from))
            {
                var spot = yard.Tracks.Find(t => t.Length > 80 && !world.Occupancy.IsOccupied(t.Track, int.MinValue));
                if (spot == null)
                    continue;
                var flat = TrainCarType.FlatbedEmpty.ToV2();
                spawned = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat, flat }, new List<bool> { false, false, false },
                    world.Network.Tracks[spot.Track], true, true);
                break;
            }
            yield return new WaitForSeconds(3);
            yield return TabletTour(s, spawned);
            s.Stop(secure: true);
            Line("tablet put away");
        }

        /// <summary>
        /// The engineer's tablet (Held/, UI/Tablet): an item made in front of
        /// the camera, its screen facing it; each page's screen saved as drawn
        /// (selftest-tablet-*.png) and the game's frame with it in view. A tab
        /// is pressed through the screen's own button; on the map, a spawned
        /// cut is picked, its first order given, and undone.
        /// </summary>
        private static IEnumerator TabletTour(Session s, List<TrainCar> cut)
        {
            var camera = PlayerManager.ActiveCamera;
            var template = Held.EngineerTablet.Template;
            if (camera == null || template == null)
            {
                Fail("tablet", template == null ? "the engineer's tablet couldn't be built" : "no camera");
                yield break;
            }
            Standalone(template);
            var at = camera.transform.position + camera.transform.forward * 0.42f;
            var tablet = UnityEngine.Object.Instantiate(template, at, Quaternion.LookRotation(camera.transform.forward, camera.transform.up));
            tablet.name = Held.EngineerTablet.PrefabName;
            // As a tablet given to the player is: in the world's storage, the player's.
            tablet.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            var impl = tablet.GetComponent<DV.CabControls.ItemBase>();
            if (impl != null)
                DV.Utils.SingletonBehaviour<StorageController>.Instance.AddItemToWorldStorage(impl);
            yield return null;
            foreach (var body in tablet.GetComponentsInChildren<Rigidbody>())
                body.isKinematic = true;
            tablet.transform.SetPositionAndRotation(at, Quaternion.LookRotation(camera.transform.forward, camera.transform.up));
            yield return new WaitForSeconds(1);
            var renderers = tablet.GetComponentsInChildren<Renderer>(true);
            Line($"tablet at {tablet.transform.position - WorldMover.currentMove} (camera at {camera.transform.position - WorldMover.currentMove}, {camera.name}, " +
                $"{(tablet.transform.position - camera.transform.position).magnitude:F2} m), active {tablet.activeInHierarchy}, layer {tablet.layer}, " +
                $"renderers {string.Join(", ", renderers.Select(r => $"{r.name} {(r.enabled ? "on" : "off")}{(r.isVisible ? " seen" : "")} layer {r.gameObject.layer}"))}; " +
                $"camera culls {Convert.ToString(camera.cullingMask, 2)}; main {(Camera.main != null ? Camera.main.name : "none")}");
            var screen = tablet.GetComponent<Held.EngineerTabletScreen>();
            // Put away and taken out again, as the inventory does: the screen must come back by itself, unforced.
            tablet.SetActive(false);
            yield return null;
            tablet.SetActive(true);
            yield return HoldUp(tablet, camera);
            yield return new WaitForSeconds(1);
            yield return HoldUp(tablet, camera);
            (screen.Showing ? (Action<string, string>)Pass : Fail)("tablet screen", screen.Showing ? "drawn on the item by itself after it was put away and taken out" : "not drawn after it was put away and taken out");
            UI.Tablet.TabletUi.Chosen = s.Loco;
            UI.Tablet.TabletUi.Page = UI.Tablet.TabletTab.Engineers;
            yield return new WaitForSeconds(2);
            SaveScreen(screen, "tablet-engineers");
            yield return HoldUp(tablet, camera);
            Line("SCREENSHOT tablet-world");
            Capture("tablet-world");
            yield return new WaitForSeconds(1);
            // Fuel on the loco's card: the nearest station selling diesel, the loco sent there; then undone.
            var stations = Game.Service.Stations(Host.Network);
            Line($"service stations seen: {stations.Count}: {string.Join("; ", stations.Select(st => $"{st.Where} ({string.Join("/", st.Offers)})"))}");
            var fuelSlot = screen.SlotAt(new Vector2(12 + 1056 - 12 - 160 - 8 - 60, 84 + 82 + 28));
            var queued = s.Engineer.Queue.Count;
            screen.PressSlotForTest(fuelSlot);
            yield return new WaitForSeconds(1);
            var fuelHead = s.Engineer.Queue.Count > 0 ? s.Engineer.Queue[0].Label : "nothing";
            (fuelHead.StartsWith("fuel at", StringComparison.Ordinal) ? (Action<string, string>)Pass : Fail)("tablet fuel", $"Fuel (slot {fuelSlot}) clicked: the first order is {fuelHead}");
            SaveScreen(screen, "tablet-fuel");
            UI.Tablet.TabletUi.UndoForTest();
            yield return new WaitForSeconds(0.5f);
            Line($"after undo: {s.Engineer.Queue.Count} orders ({queued} before)");
            // A press on the Jobs tab, through the game's own button over it (as a click: pushed in and let back).
            var slot = screen.SlotAt(new Vector2(12 + 2 * 158 + 75, 12 + 32));
            var used = screen.PressSlotForTest(slot);
            yield return new WaitForSeconds(1);
            (used && UI.Tablet.TabletUi.Page == UI.Tablet.TabletTab.Jobs ? (Action<string, string>)Pass : Fail)("tablet press",
                $"the Jobs tab's button (slot {slot}) {(used ? "clicked" : "had no control")}: the page is {UI.Tablet.TabletUi.Page}");
            SaveScreen(screen, "tablet-jobs");
            UI.Tablet.TabletUi.Page = UI.Tablet.TabletTab.Map;
            UI.Tablet.TabletMap.ShowChosenNext();
            yield return new WaitForSeconds(2);
            SaveScreen(screen, "tablet-map");
            Line($"tablet-map: {UI.Tablet.TabletMap.Describe()}");
            // The map's own plates, clicked through their buttons: the station tabs' arrows, down, up, and down
            // again (a button clicked a second time), a yard's letter, the lit letter again (up a level).
            var set = UI.Tablet.TabletMap.TabSetShown;
            var down = new Vector2(12 + 16 + 52, 84 + 16 + 58 * 10 + 26);
            var up = new Vector2(12 + 16 + 52, 84 + 16 + 26);
            var sets = new List<int>();
            foreach (var arrow in new[] { down, up, down })
            {
                screen.PressSlotForTest(screen.SlotAt(arrow));
                yield return new WaitForSeconds(0.5f);
                sets.Add(UI.Tablet.TabletMap.TabSetShown);
            }
            // The up arrow, clicked twice in this sequence and pressable on any set but the first: its button must still lie over it.
            var upSlot = screen.SlotAt(up);
            (sets.SequenceEqual(new[] { set + 1, set, set + 1 }) && screen.HitInPlace(upSlot) ? (Action<string, string>)Pass : Fail)("tablet tabs",
                $"the station tabs' arrows clicked down, up, down: set {set}, then {string.Join(", ", sets)}; the up arrow's button (slot {upSlot}) {(screen.HitInPlace(upSlot) ? "still over it" : "moved off it")}");
            var overview = UI.Tablet.TabletMap.PageShown;
            var letter = new Vector2(12 + 912 + 30, 84 + 238 + 30);
            screen.PressSlotForTest(screen.SlotAt(letter));
            yield return new WaitForSeconds(0.5f);
            var yard = UI.Tablet.TabletMap.PageShown;
            screen.PressSlotForTest(screen.SlotAt(letter));
            yield return new WaitForSeconds(0.5f);
            var back = UI.Tablet.TabletMap.PageShown;
            (yard != overview && back == overview ? (Action<string, string>)Pass : Fail)("tablet up a level", $"a yard's letter pressed: {overview} to {yard}; pressed again: {back}");
            var car = cut != null && cut.Count > 0 ? cut[0] : null;
            // The cut, pressed on the map where it is drawn, through the map's press.
            var picked = false;
            if (car != null && UI.Tablet.TabletMap.PickForTest(car))
            {
                UI.Tablet.TabletMap.Unpick();
                screen.DrawNow();
                var point = UI.Tablet.TabletMap.CanvasPointOf(car);
                if (point.HasValue)
                {
                    UI.Tablet.TabletMap.PressAt(point.Value);
                    picked = UI.Tablet.TabletMap.Describe().Contains("pick Cut");
                }
                (picked ? (Action<string, string>)Pass : Fail)("tablet map press", $"pressed at {point}: {UI.Tablet.TabletMap.Describe()}");
            }
            if (picked)
            {
                yield return new WaitForSeconds(1);
                SaveScreen(screen, "tablet-pick");
                Line($"tablet-pick: {UI.Tablet.TabletMap.Describe()}");
                var before = s.Engineer.Queue.Count;
                var pressed = UI.Tablet.TabletMap.DoForTest(0);
                var after = s.Engineer.Queue.Count;
                var head = after > 0 ? $"{s.Engineer.Queue[0].Action} {s.Engineer.Queue[0].Label}" : "nothing";
                (pressed != null && after > 0 && s.Engineer.Queue[0].Action == WaypointAction.Couple ? (Action<string, string>)Pass : Fail)("tablet order",
                    $"pressed \"{pressed}\": {before} orders before, {after} after, first {head}");
                yield return new WaitForSeconds(2);
                SaveScreen(screen, "tablet-ordered");
                yield return HoldUp(tablet, camera);
                Line("SCREENSHOT tablet-world-map");
                Capture("tablet-world-map");
                yield return new WaitForSeconds(1);
                var undone = UI.Tablet.TabletUi.UndoForTest();
                (undone && s.Engineer.Queue.Count == before ? (Action<string, string>)Pass : Fail)("tablet undo", $"undo {(undone ? "pressed" : "not offered")}: {s.Engineer.Queue.Count} orders, {before} before the order");
                var station = UI.Tablet.TabletMap.StationShown;
                if (station != null)
                {
                    UI.Tablet.TabletMap.OpenForTest(Core.Booklet.PageRef.OfYard(station, "B"));
                    yield return new WaitForSeconds(1);
                    SaveScreen(screen, "tablet-yard");
                }
            }
            else
                Line("tablet: the spawned cut couldn't be picked");
            UI.Tablet.TabletMap.OpenForTest(Core.Booklet.PageRef.Route);
            yield return new WaitForSeconds(1);
            SaveScreen(screen, "tablet-route");
            UnityEngine.Object.Destroy(tablet);
            UI.Tablet.TabletUi.Page = UI.Tablet.TabletTab.Engineers;
        }

        /// <summary>
        /// Without the Comms Tablet (only this mod in the clone) the engineer's
        /// own tablet is not retired: not merged, a new save's starting kit
        /// holds it once with its key set, and the shops sell it. With it
        /// (merged), the kit holds none and the key is cleared, a save given
        /// the tablet before the merge too.
        /// </summary>
        private static void Standalone(GameObject template)
        {
            var spec = template.GetComponent<InventoryItemSpec>();
            var kitOf = AccessTools.Method(typeof(StartingItemsController), "GetStartingItemsPrefabs");
            var controller = SingletonBehaviour<StartingItemsController>.Instance;
            var data = new SaveGameData();
            data.SetInt("Starting_items", (int)GameParams.StartingItemsType.Basic);
            var kit = controller != null ? (List<DV.Items.StartingItems.StartingItem>)kitOf.Invoke(controller, new object[] { data }) : null;
            var inKit = kit?.Count(k => k.item?.ItemPrefabName == Held.EngineerTablet.PrefabName) ?? -1;
            var shops = SingletonBehaviour<DV.Shops.GlobalShopController>.Instance;
            var sold = shops?.shopItemsData?.Any(d => d?.item == spec) == true;
            var tags = shops?.globalShopList?.Where(x => x?.scanItemResourceModules != null).SelectMany(x => x.scanItemResourceModules).Count(t => t?.sellingItemSpec == spec) ?? 0;
            var merged = CommsTabletLink.Merged;
            if (merged)
            {
                var before = new SaveGameData();
                before.SetInt("Starting_items", (int)GameParams.StartingItemsType.Basic);
                before.SetBool(Held.EngineerTabletStartingItems.GivenKey, true);
                var kitBefore = (List<DV.Items.StartingItems.StartingItem>)kitOf.Invoke(controller, new object[] { before });
                var inKitBefore = kitBefore?.Count(k => k.item?.ItemPrefabName == Held.EngineerTablet.PrefabName) ?? -1;
                (inKit == 0 && data.GetBool(Held.EngineerTabletStartingItems.GivenKey) == null && inKitBefore == 0
                    && before.GetBool(Held.EngineerTabletStartingItems.GivenKey) == null ? (Action<string, string>)Pass : Fail)("tablet merged",
                    $"in a new save's kit {inKit} time(s), key {data.GetBool(Held.EngineerTabletStartingItems.GivenKey)}; a save given it before: in its kit {inKitBefore} time(s), key {before.GetBool(Held.EngineerTabletStartingItems.GivenKey)}");
                return;
            }
            (inKit == 1 && data.GetBool(Held.EngineerTabletStartingItems.GivenKey) == true && sold && tags > 0 ? (Action<string, string>)Pass : Fail)("tablet standalone",
                $"merged with a Comms Tablet: {merged}; in a new save's kit {inKit} time(s), key {data.GetBool(Held.EngineerTabletStartingItems.GivenKey)}; sold {sold}, {tags} shelf tags");
        }

        /// <summary>Holds the test's tablet up before the camera for a few frames, its screen to it, as a held tablet is seen.</summary>
        private static IEnumerator HoldUp(GameObject tablet, Camera camera)
        {
            for (var i = 0; i < 20; i++)
            {
                foreach (var body in tablet.GetComponentsInChildren<Rigidbody>())
                {
                    body.isKinematic = true;
                    body.velocity = Vector3.zero;
                }
                tablet.transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * 0.45f - camera.transform.up * 0.02f,
                    Quaternion.LookRotation(camera.transform.forward, camera.transform.up));
                yield return null;
            }
        }

        /// <summary>A tablet's screen as it draws it, to selftest-NAME.png.</summary>
        private static void SaveScreen(Held.EngineerTabletScreen screen, string name)
        {
            try
            {
                screen.DrawNow();
                var rt = screen.Texture;
                if (rt == null)
                {
                    Line($"{name}: the screen wasn't drawn");
                    return;
                }
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                image.Apply();
                RenderTexture.active = previous;
                File.WriteAllBytes(Path.Combine(FolderPath, $"selftest-{name}.png"), image.EncodeToPNG());
                UnityEngine.Object.Destroy(image);
                Line($"SCREEN {name}");
            }
            catch (Exception e)
            {
                Fail(name, $"the screen couldn't be saved: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// The dispatcher's orders: the owned loco listed, sent to a named
        /// track, then sent to couple to a cut spawned on another, on the
        /// end farther by route and facing away from the cars, and (queued)
        /// to take it into a stub, cars first.
        /// </summary>
        private static IEnumerator Dispatcher(Session s)
        {
            var owned = Dispatch.Locos();
            if (owned.Contains(s.Loco))
                Pass("dispatcher lists the owned loco", $"{owned.Count} owned loco{(owned.Count == 1 ? "" : "s")} listed");
            else
                Fail("dispatcher lists the owned loco", $"{s.Loco.ID} not among {owned.Count} (uniqueCar {s.Loco.uniqueCar})");

            var world = Host.World;
            world.ScanCars();
            var state = s.Read();
            var at = Dispatch.MapPosition(s.Loco);
            NamedTrack target = null, cutTrack = null, stub = null;
            var yardsLooked = 0;
            foreach (var yard in Dispatch.Yards(world.Network, at))
            {
                // Four yards for the first two tracks, eight for a stub (yards have few).
                if (++yardsLooked > (target != null && cutTrack != null ? 8 : 4) || stub != null)
                    break;
                foreach (var track in yard.Tracks)
                {
                    var forStub = target != null && cutTrack != null;
                    if (track.Length < (forStub ? 70 : 120) || world.Occupancy.IsOccupied(track.Track, state.Owner))
                        continue;
                    if (forStub && (stub != null || !track.DeadEnd))
                        continue;
                    var route = RouteTo(state, OrderGoals.OnTrack(world.Graph, track.Track, state.Length + 40));
                    if (route == null || route.Length > (forStub ? 4000 : 3000))
                        continue;
                    if (target == null)
                        target = track;
                    else if (cutTrack == null)
                        cutTrack = track;
                    else
                        stub = track;
                }
            }
            if (target == null)
            {
                Line("SKIP dispatcher to a track: no free named track with a route within four yards");
                yield break;
            }

            const string toTrack = "dispatcher to a track";
            Line($"{toTrack}: {target.Id} ({target.Length:F0} m)");
            Dispatch.GoToTrack(s, world, target, queue: false);
            yield return UntilQueueEmpty(s, toTrack, 420);
            var after = s.Read();
            var detail = $"ends on tracks {after.EndA.Track} and {after.EndB.Track}, {target.Id} is {target.Track}; status '{s.Engineer.Status}'";
            if (after.EndA.Track == target.Track && after.EndB.Track == target.Track)
                Pass(toTrack, detail);
            else
                Fail(toTrack, detail);

            const string pickUp = "dispatcher picks up a cut on the end asked, facing away";
            const string intoStub = "dispatcher takes the cut into a stub, cars first";
            if (cutTrack == null)
            {
                Line($"SKIP {pickUp}: no second free track");
                yield break;
            }
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var spawned = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat }, new List<bool> { false, false },
                world.Network.Tracks[cutTrack.Track], true, true);
            if (spawned == null || spawned.Count == 0)
            {
                Fail(pickUp, $"no cut could be spawned on {cutTrack.Id}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            world.ScanCars();
            var cut = Dispatch.Cuts(world, at, 5000, s.Loco.trainset).Find(c => c.Set.cars.Contains(spawned[0]));
            if (cut == null)
            {
                Fail(pickUp, $"the cut spawned on {cutTrack.Id} isn't in the dispatcher's list");
                yield break;
            }
            Line($"{pickUp}: {cut.Label}, {cut.Distance:F0} m away");

            // The end toward a destination: as if the cars were bound for the nearest other yard.
            const string toward = "dispatcher finds the end toward a destination";
            var elsewhere = Dispatch.Yards(world.Network, at).Find(y => y.Id != cut.YardId && y.Tracks.Count > 0);
            if (elsewhere != null)
            {
                cut.Destination = elsewhere.Id;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var end = Dispatch.EndOf(world, cut, CutSide.Auto, state.Length, out var why);
                cut.Destination = null;
                if (end.HasValue)
                    Pass(toward, $"{why} ({watch.Elapsed.TotalMilliseconds:F1} ms)");
                else
                    Fail(toward, why ?? "no end");
            }

            // The end farther by route, so the choice is the dispatcher's and not the router's.
            state = s.Read();
            var near0 = RouteTo(state, OrderGoals.ToCouple(world.Graph, new[] { cut.Ends[0] }, world.SelectedBranchFunc));
            var near1 = RouteTo(state, OrderGoals.ToCouple(world.Graph, new[] { cut.Ends[1] }, world.SelectedBranchFunc));
            var side = near0 != null && near1 != null ? (Cost(near0) >= Cost(near1) ? 0 : 1) : (near0 != null ? 0 : 1);
            var endCoupler = side == 0 ? cut.Set.GetEndmost(true) : cut.Set.GetEndmost(false);
            Line($"{pickUp}: the {cut.EndNames[side]} end ({(near0 != null ? Cost(near0).ToString("F0") : "-")} against {(near1 != null ? Cost(near1).ToString("F0") : "-")})");
            var pickup = Dispatch.PickUp(s, world, cut, queue: false, side == 0 ? CutSide.End0 : CutSide.End1, Arrival.BackFirst);
            var into = stub ?? target;
            var goInto = Dispatch.GoToTrack(s, world, into, queue: true);
            var elapsed = 0f;
            while (elapsed < 600 && s.Engineer.Queue.Count > 1 && s.Engineer.Mode == EngineerMode.Waypoints)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                Trace(s, pickUp, elapsed);
            }
            var by = endCoupler.coupledTo;
            var onEnd = by != null && by.train == s.Loco;
            var loco = s.Loco;
            var coupler = by == null ? "nothing" : by == loco.rearCoupler ? "the loco's back" : by == loco.frontCoupler ? "the loco's front" : by.train.ID;
            detail = $"the {cut.EndNames[side]} end coupled to {coupler}; arrival {(pickup.ArrivalMet ? "met" : "NOT met, went anyway")}; status '{s.Engineer.Status}'";
            if (onEnd && by == loco.rearCoupler)
                Pass(pickUp, detail);
            else if (onEnd && !pickup.ArrivalMet)
                Pass(pickUp, "no way to turn the loco here, as reported: " + detail);
            else
                Fail(pickUp, detail);

            Line($"{intoStub}: {into.Id}{(into.DeadEnd ? " (a stub)" : " (no stub nearby: a through track)")}");
            yield return UntilQueueEmpty(s, intoStub, 600);
            after = s.Read();
            var withCars = s.Loco.trainset != null && s.Loco.trainset.cars.Contains(spawned[0]) && s.Loco.trainset.cars.Contains(spawned[spawned.Count - 1]);
            var onIt = after.EndA.Track == into.Track && after.EndB.Track == into.Track;
            var carsFirst = true;
            if (into.DeadEnd && onIt)
            {
                var buffer = world.Graph.Links(into.Track, TrackEnd.Start).Length == 0 ? 0 : world.Graph.Length(into.Track);
                var inner = Math.Abs(after.EndA.Offset - buffer) < Math.Abs(after.EndB.Offset - buffer) ? ConsistEnd.A : ConsistEnd.B;
                carsFirst = inner != after.LocoEnd;
            }
            detail = $"cut {(withCars ? "attached" : "NOT attached")}, {after.CarCount} cars, ends on tracks {after.EndA.Track} and {after.EndB.Track} ({into.Id} is {into.Track}), " +
                $"{(carsFirst ? "cars" : "LOCO")} at the inner end, arrival {(goInto.ArrivalMet ? "met" : "not met")}; status '{s.Engineer.Status}'";
            if (withCars && onIt && (carsFirst || !goInto.ArrivalMet))
                Pass(intoStub, detail);
            else
                Fail(intoStub, detail);

            yield return BlockedRoute(s, into);
        }

        private static double Cost(Route route) => route.Length + 400 * route.Reversals;

        /// <summary>
        /// Jobs handed over and worked through, as the dispatcher's Work jobs
        /// panel does, with jobs made (as Railroad Contracts makes its moves)
        /// for cars spawned at the nearest station, hand brakes on. The game's
        /// own task check says whether each was delivered: the cars on the
        /// track, stopped, coupled together, a hand brake on.
        /// A: the loco starts coupled to a cut of three, the job its two
        /// nearest cars: the far one, in the way, is set aside where it stands.
        /// B: a job's two flatcars in the middle of a cut of four (one beyond
        /// them cut off where it stands, one riding along and put back after),
        /// and a whole cut of two hoppers loaded with coal (heavy enough to
        /// need the train brake); a car standing on the flatcars' destination
        /// may make the set-out couple to it and shove it in.
        /// </summary>
        private static IEnumerator JobsWorked(Session s)
        {
            var world = Host.World;
            world.ScanCars();
            var state = s.Read();
            var at = Dispatch.MapPosition(s.Loco);
            Line($"jobs: brakes {(BrakesMod.Active ? "Realistic Brakes" : "stock")}; the loco has {state.CarCount - 1} cars");
            StationController station = null;
            var tracks = new List<NamedTrack>();
            foreach (var yard in Dispatch.Yards(world.Network, at).Take(4))
            {
                var st = StationController.allStations?.Find(c => c != null && c.stationInfo != null && c.stationInfo.YardID == yard.Id);
                if (st == null)
                    continue;
                tracks.Clear();
                foreach (var track in yard.Tracks.OrderBy(t => t.Length))
                {
                    if (track.Length < 100 || world.Occupancy.IsOccupied(track.Track, state.Owner))
                        continue;
                    var route = RouteTo(state, OrderGoals.OnTrack(world.Graph, track.Track, state.Length + 60));
                    if (route == null || route.Length > 3000)
                        continue;
                    tracks.Add(track);
                    if (tracks.Count == 6)
                        break;
                }
                if (tracks.Count >= 4)
                {
                    station = st;
                    break;
                }
            }
            if (station == null)
            {
                Line("SKIP jobs: no station within four yards with four free tracks of 100 m in reach");
                yield break;
            }
            Line($"jobs: at {station.stationInfo.YardID}, free tracks {string.Join(", ", tracks.Select(t => $"{t.Id} ({t.Length:F0} m{(t.DeadEnd ? ", stub" : "")})"))}");
            if (tracks.Count >= 6)
                yield return StartingCoupled(s, world, station, tracks[4], tracks[5]);
            else
                Line("SKIP jobs A (starting coupled): fewer than six free tracks");
            yield return MiddleAndHeavy(s, world, station, tracks[0], tracks[1], tracks[2], tracks[3]);
        }

        /// <summary>A: the loco starts coupled to a cut; the job is the two cars nearest it; the far one is in the way.</summary>
        private static IEnumerator StartingCoupled(Session s, World world, StationController station, NamedTrack from, NamedTrack to)
        {
            const string worked = "jobs A: starting coupled, the job delivered";
            const string aside = "jobs A: the car in the way set aside where it stood";
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var spawned = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat, flat }, new List<bool> { false, false, false },
                world.Network.Tracks[from.Track], true, true);
            if (spawned == null || spawned.Count < 3)
            {
                Fail(worked, $"couldn't spawn the cut on {from.Id}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            world.ScanCars();
            var cut = Dispatch.Cuts(world, Dispatch.MapPosition(s.Loco), 5000, s.Loco.trainset).Find(c => c.Set.cars.Contains(spawned[0]));
            if (cut == null)
            {
                Fail(worked, $"the cut on {from.Id} isn't listed");
                yield break;
            }
            Dispatch.PickUp(s, world, cut, queue: false, CutSide.Nearer, Arrival.Any);
            yield return UntilQueueEmpty(s, "jobs A pickup", 900);
            var set = s.Loco.trainset;
            if (!set.cars.Contains(spawned[0]))
            {
                Fail(worked, $"the loco didn't couple to the cut: status '{s.Engineer.Status}'");
                yield break;
            }
            // The two nearest the loco are the job's; the far one stands in their way.
            var locoIndex = set.cars.IndexOf(s.Loco);
            var fromA = locoIndex >= set.cars.Count - 1 - locoIndex;
            var farToNear = fromA ? set.cars.Take(locoIndex).ToList() : set.cars.Skip(locoIndex + 1).Reverse().ToList();
            var stranger = farToNear[0];
            var jobCars = farToNear.Skip(1).ToList();
            Job job;
            try
            {
                job = MakeHaul(world, station, jobCars, from, to);
            }
            catch (Exception e)
            {
                Fail(worked, $"couldn't make the job: {e.GetType().Name}: {e.Message}");
                yield break;
            }
            Line($"jobs A: {job.ID} takes {string.Join(", ", jobCars.Select(c => c.ID))} (nearest the loco) to {to.Id}; {stranger.ID} at the far end is in the way");
            var run = JobRun.Start(s, world, new List<JobView> { Jobs.Read(world, job) }, queue: false, Arrival.BackFirst, out var problems);
            if (run == null)
            {
                Fail(worked, "the run didn't start" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
                yield break;
            }
            yield return UntilRunDone(s, "jobs A", 1500);
            var done = job.tasks.TrueForAll(t => t.state == TaskState.Done);
            var detail = $"{job.ID} {(done ? "done" : "NOT done: " + Jobs.Read(world, job).Summary)}; status '{s.Engineer.Status}'";
            if (done)
                Pass(worked, detail);
            else
                Fail(worked, detail);
            detail = $"{stranger.ID} {(stranger.trainset == s.Loco.trainset ? "is on the train" : "stays")} on {Dispatch.Where(stranger)}, hand brake {stranger.brakeSystem?.handbrakePosition:F2}";
            if (stranger.trainset != s.Loco.trainset && stranger.logicCar?.FrontBogieTrack == world.Network.Tracks[from.Track].LogicTrack() && stranger.brakeSystem?.handbrakePosition > 0.9f)
                Pass(aside, detail);
            else
                Fail(aside, detail);
            yield return ClearCars(s, spawned);
        }

        /// <summary>Deletes a part's spawned cars (those off the loco's train), so the parts after find the tracks free.</summary>
        private static IEnumerator ClearCars(Session s, IEnumerable<TrainCar> cars)
        {
            var gone = cars.Where(c => c != null && c.trainset != s.Loco.trainset).ToList();
            if (gone.Count > 0)
                CarSpawner.Instance.DeleteTrainCars(gone, true);
            yield return new WaitForSeconds(3);
            Host.World?.ScanCars();
        }

        /// <summary>B: a job's cars in the middle of a cut, a heavy cut, and maybe a set-out shoved in.</summary>
        private static IEnumerator MiddleAndHeavy(Session s, World world, StationController station, NamedTrack from1, NamedTrack from2, NamedTrack to1, NamedTrack to2)
        {
            const string worked = "jobs B: both delivered, by the game's own check";
            const string kept = "jobs B: the cut's other cars left (or put back) where they stood, braked";
            const string free = "jobs B: the loco left free of the jobs' cars";
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var cut1 = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat, flat, flat }, new List<bool> { false, false, false, false },
                world.Network.Tracks[from1.Track], true, true);
            var hopper = TrainCarType.HopperBrown.ToV2();
            var cut2 = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { hopper, hopper }, new List<bool> { false, false },
                world.Network.Tracks[from2.Track], true, true);
            // A car standing on the flatcars' destination.
            var blocker = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat }, new List<bool> { false },
                world.Network.Tracks[to1.Track], true, true);
            if (cut1 == null || cut1.Count < 4 || cut2 == null || cut2.Count < 2)
            {
                Fail(worked, $"couldn't spawn the cuts on {from1.Id} and {from2.Id}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            foreach (var car in cut2)
                car.logicCar.LoadCargo(car.logicCar.capacity, CargoType.Coal);
            yield return new WaitForSeconds(2);
            world.ScanCars();
            var standing = new List<OccupiedSpan>();
            world.Occupancy.Standing(to1.Track, int.MinValue, standing);
            var jobCars = cut1.Skip(1).Take(2).ToList();
            var need = jobCars.Sum(c => (double)c.InterCouplerDistance);
            Line($"jobs B: the hoppers weigh {cut2.Sum(c => c.massController != null ? c.massController.TotalMass : 0f) / 1000:F0} t, the loco {(s.Loco.massController != null ? s.Loco.massController.TotalMass : 0f) / 1000:F0} t; " +
                 $"on {to1.Id} a car stands at {string.Join(", ", standing.Select(x => $"{x.From:F0}-{x.To:F0} m"))}: the set-out {(OrderGoals.SpotOn(world.Graph, to1.Track, need, standing).Count > 0 ? "fits beside it" : OrderGoals.ShoveIn(world.Graph, to1.Track, need, standing) != null ? "must shove it in" : "has no room")}");
            Job job1, job2;
            try
            {
                job1 = MakeHaul(world, station, jobCars, from1, to1);
                job2 = MakeHaul(world, station, cut2, from2, to2);
            }
            catch (Exception e)
            {
                Fail(worked, $"couldn't make the jobs: {e.GetType().Name}: {e.Message}");
                yield break;
            }
            Line($"jobs B: {job1.ID} takes the middle 2 of 4 flatcars from {from1.Id} to {to1.Id}; {job2.ID} 2 loaded hoppers from {from2.Id} to {to2.Id}");
            var views = new List<JobView> { Jobs.Read(world, job1), Jobs.Read(world, job2) };
            foreach (var v in views)
                Line($"jobs B: {v.Id} reads as {v.Kind}: {v.Blocked ?? v.Summary}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var run = JobRun.Start(s, world, views, queue: false, Arrival.BackFirst, out var problems);
            // The hand-over reads the jobs and cars and asks for the plan, which is worked out on another thread.
            Line($"jobs B: handed over in {watch.Elapsed.TotalMilliseconds:F1} ms on the game's thread{(problems.Count > 0 ? "; left out: " + string.Join("; ", problems) : "")}");
            if (run == null)
            {
                Fail(worked, "the run didn't start");
                yield break;
            }
            yield return UntilRunDone(s, "jobs B", 3000);
            var done1 = job1.tasks.TrueForAll(t => t.state == TaskState.Done);
            var done2 = job2.tasks.TrueForAll(t => t.state == TaskState.Done);
            var detail = $"{job1.ID} {(done1 ? "done" : "NOT done: " + Jobs.Read(world, job1).Summary)}, {job2.ID} {(done2 ? "done" : "NOT done: " + Jobs.Read(world, job2).Summary)}; status '{s.Engineer.Status}'";
            if (done1 && done2)
                Pass(worked, detail);
            else
                Fail(worked, detail);
            var others = new[] { cut1[0], cut1[3] };
            var home = world.Network.Tracks[from1.Track].LogicTrack();
            var ok = others.All(c => c != null && c.trainset != s.Loco.trainset && c.logicCar?.FrontBogieTrack == home && c.brakeSystem?.handbrakePosition > 0.9f);
            detail = string.Join("; ", others.Select(c => c == null ? "gone" : $"{c.ID} {(c.trainset == s.Loco.trainset ? "on the train" : "off it")} on {Dispatch.Where(c)}, hand brake {c.brakeSystem?.handbrakePosition:F2}"));
            if (ok)
                Pass(kept, detail);
            else
                Fail(kept, detail);
            if (blocker != null && blocker.Count > 0)
                Line($"jobs B: the car on {to1.Id} {(blocker[0].trainset == jobCars[0].trainset ? "was coupled to and shoved in with the set-out" : "was left as it stood")}, now on {Dispatch.Where(blocker[0])}");
            var carried = s.Loco.trainset.cars.Count(c => jobCars.Contains(c) || cut2.Contains(c));
            detail = $"{carried} of the jobs' cars on the train; {s.Loco.trainset.cars.Count} cars in all; slowest tick so far {Session.SlowestTickMs:F2} ms";
            if (carried == 0)
                Pass(free, detail);
            else
                Fail(free, detail);
            yield return ClearCars(s, cut1.Concat(cut2).Concat(blocker ?? new List<TrainCar>()));
        }

        private static IEnumerator UntilRunDone(Session s, string name, float seconds)
        {
            var elapsed = 0f;
            while (elapsed < seconds && s.Jobs != null)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                if ((int)elapsed % 5 == 0)
                    Trace(s, name, elapsed);
                if (elapsed == 30 && name == "jobs B")
                {
                    var frame = 0f;
                    yield return MeasureFrames(5, v => frame = v);
                    elapsed += 5;
                    Line($"frame time while working jobs {frame * 1000:F2} ms (idle was {idleFrameTime * 1000:F2} ms)");
                }
            }
            Line($"{name}: the run {(s.Jobs == null ? "finished" : "still going: " + s.Jobs.Status)} after {elapsed:F0} s");
        }

        /// <summary>A logistical haul of <paramref name="cars"/> within a station, taken: as Railroad Contracts builds its moves.</summary>
        private static Job MakeHaul(World world, StationController station, List<TrainCar> cars, NamedTrack from, NamedTrack to)
        {
            var logicCars = TrainCar.ExtractLogicCars(cars).ToList();
            var loaded = logicCars.All(c => c.CurrentCargoTypeInCar != CargoType.None);
            var go = new GameObject($"ChainJob[{(loaded ? "Transport" : "EmptyHaul")}]: AI Engineer self-test to {to.Id}");
            go.transform.SetParent(station.transform);
            var chain = new JobChainController(go) { carsForJobChain = logicCars.ToList() };
            var chainData = new StationsChainData(station.stationInfo.YardID, station.stationInfo.YardID);
            StaticJobDefinition definition;
            if (loaded)
            {
                var transport = go.AddComponent<StaticTransportJobDefinition>();
                transport.PopulateBaseJobDefinition(station.logicStation, 7200f, 100f, chainData, JobLicenses.Basic);
                transport.startingTrack = world.Network.Tracks[from.Track].LogicTrack();
                transport.destinationTrack = world.Network.Tracks[to.Track].LogicTrack();
                transport.carsToTransport = logicCars.ToList();
                transport.transportedCargoPerCar = logicCars.Select(c => c.CurrentCargoTypeInCar).ToList();
                transport.cargoAmountPerCar = logicCars.Select(c => c.LoadedCargoAmount).ToList();
                transport.forceCorrectCargoStateOnCars = false;
                definition = transport;
            }
            else
            {
                var haul = go.AddComponent<StaticEmptyHaulJobDefinition>();
                haul.PopulateBaseJobDefinition(station.logicStation, 7200f, 100f, chainData, JobLicenses.Basic);
                haul.startingTrack = world.Network.Tracks[from.Track].LogicTrack();
                haul.destinationTrack = world.Network.Tracks[to.Track].LogicTrack();
                haul.carsToTransport = logicCars.ToList();
                definition = haul;
            }
            chain.AddJobDefinitionToChain(definition);
            chain.FinalizeSetupAndGenerateFirstJob();
            var job = definition.job ?? throw new InvalidOperationException("the chain made no job");
            SingletonBehaviour<JobsManager>.Instance.TakeJob(job);
            return job;
        }

        /// <summary>
        /// Cars standing across the route (the second run of 2026-11-04 ran
        /// into cars after replanning round them): the engineer must stop
        /// short, and then find a way round or wait, never hit them.
        /// </summary>
        private static IEnumerator BlockedRoute(Session s, NamedTrack from)
        {
            const string name = "cars across the route";
            var world = Host.World;
            world.ScanCars();
            var state = s.Read();
            var at = Dispatch.MapPosition(s.Loco);
            NamedTrack goal = null;
            int blockTrack = -1;
            foreach (var yard in Dispatch.Yards(world.Network, at))
            {
                foreach (var track in yard.Tracks)
                {
                    if (track.Track == from.Track || track.Length < 80 || world.Occupancy.IsOccupied(track.Track, state.Owner))
                        continue;
                    var route = RouteTo(state, OrderGoals.OnTrack(world.Graph, track.Track, state.Length));
                    if (route == null || route.Length < 400 || route.Length > 2500)
                        continue;
                    // A track the first leg crosses well along, long enough to hold a cut.
                    var leg = route.Legs[0];
                    for (var i = 1; i < leg.Pieces.Count - 1 && blockTrack < 0; i++)
                    {
                        var piece = leg.Pieces[i];
                        if (leg.PieceStart(i) > 150 && world.Graph.Length(piece.Track) >= 80 && piece.Track != track.Track)
                            blockTrack = piece.Track;
                    }
                    if (blockTrack >= 0)
                    {
                        goal = track;
                        break;
                    }
                }
                if (goal != null)
                    break;
            }
            if (goal == null)
            {
                Line($"SKIP {name}: no route with a track to block");
                yield break;
            }
            var flat = TrainCarType.FlatbedEmpty.ToV2();
            var blockers = CarSpawner.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { flat, flat }, new List<bool> { false, false },
                world.Network.Tracks[blockTrack], true, true);
            if (blockers == null || blockers.Count == 0)
            {
                Line($"SKIP {name}: no cut could be spawned on track {blockTrack}");
                yield break;
            }
            yield return new WaitForSeconds(5);
            Line($"{name}: to {goal.Id}, a cut on track {blockTrack} ({Host.Network.Describe(blockTrack)}) in the way");
            Dispatch.GoToTrack(s, world, goal, queue: false);
            var elapsed = 0f;
            var hit = false;
            var last = 0f;
            while (elapsed < 360 && s.Engineer.Queue.Count > 0 && s.Engineer.Mode == EngineerMode.Waypoints)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                Trace(s, name, elapsed);
                var v = Mathf.Abs(s.Loco.GetForwardSpeed());
                if (s.Loco.derailed || last - v > 2.5f)
                    hit = true;
                foreach (var car in blockers)
                    hit |= car != null && car.derailed;
                last = v;
                if (hit)
                    break;
            }
            var detail = $"{elapsed:F0} s, status '{s.Engineer.Status}', derailed {s.Loco.derailed}, queue {s.Engineer.Queue.Count}";
            if (hit)
                Fail(name, "hit the cut or derailed: " + detail);
            else
                Pass(name, (s.Engineer.Queue.Count == 0 ? "went round it: " : "held short of it: ") + detail);
        }

        private static IEnumerator UntilQueueEmpty(Session s, string name, float seconds)
        {
            var elapsed = 0f;
            while (elapsed < seconds && s.Engineer.Queue.Count > 0 && s.Engineer.Mode == EngineerMode.Waypoints)
            {
                yield return new WaitForSeconds(1);
                elapsed += 1;
                Trace(s, name, elapsed);
            }
        }

        /// <summary>The cheapest route to any of the goals, or null.</summary>
        private static Route RouteTo(TrainState state, List<WaypointGoal> goals)
        {
            var world = Host.World;
            Route best = null;
            foreach (var goal in goals)
            {
                var route = world.Router.Find(new RouteRequest
                {
                    EndA = state.EndA,
                    OutwardA = state.OutwardA,
                    EndB = state.EndB,
                    OutwardB = state.OutwardB,
                    TrainLength = state.Length,
                    Goal = goal.Position,
                    GoalDirection = goal.Arrive,
                    CanSetJunction = (j, b) => SwitchRules.Usable(world, j, b, state.Owner),
                    Blocked = (t, from, to) => world.Occupancy.IsStanding(t, from, to, state.Owner),
                });
                if (route != null && (best == null || route.Length < best.Length))
                    best = route;
            }
            return best;
        }

        // ---- Helpers ----

        /// <summary>Forward or back, whichever has more track ahead on the switches as set.</summary>
        private static bool RoomierWay(Session s)
        {
            var world = Host.World;
            var state = s.Read();
            var front = s.FrontEnd;
            double Room(ConsistEnd end)
            {
                var p = end == ConsistEnd.A ? state.EndA : state.EndB;
                var d = end == ConsistEnd.A ? state.OutwardA : state.OutwardB;
                return TrackWalker.Walk(world.Graph, p, d, 2000, world.SelectedBranchFunc, end, out _).Length;
            }
            return Room(front) >= Room(front.Other());
        }

        /// <summary>
        /// A DE2 spawned mid-way along a track near the player that has
        /// track at both ends and a switch nearby a route can be set
        /// through; else the nearest drivable loco.
        /// </summary>
        private static TrainCar SpawnTestLoco() => SpawnLoco("LocoDE2");

        /// <summary>An owned loco of a livery, mid-way along a free, connected track near the player with a routable switch nearby.</summary>
        private static TrainCar SpawnLoco(string liveryId)
        {
            var player = PlayerManager.PlayerTransform;
            var spawner = CarSpawner.Instance;
            var world = Host.World;
            if (player == null || spawner == null || world == null)
                return null;
            var graph = world.Graph;
            world.ScanCars();
            var at = player.position - WorldMover.currentMove;
            var here = new MapPoint(at.x, at.z);
            var spots = new List<(double Distance, int Track)>();
            for (var t = 0; t < graph.TrackCount; t++)
            {
                if (graph.Length(t) < 120 || graph.Links(t, TrackEnd.Start).Length == 0 || graph.Links(t, TrackEnd.End).Length == 0)
                    continue;
                if (world.Occupancy.IsOccupied(t, int.MinValue))
                    continue;
                var mid = Host.Network.MapPointAt(new TrackPosition(t, graph.Length(t) / 2));
                var d = here.DistanceTo(new MapPoint(mid.x, mid.y));
                if (d < 1500)
                    spots.Add((d, t));
            }
            spots.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            TrainCarLivery de2 = null;
            foreach (var livery in DV.Globals.G.Types.Liveries)
            {
                if (livery.id == liveryId)
                    de2 = livery;
            }
            for (var k = 0; k < spots.Count && k < 60 && de2 != null; k++)
            {
                var t = spots[k].Track;
                var mid = graph.Length(t) / 2;
                var probe = new TrainState
                {
                    EndA = new TrackPosition(t, mid - 7),
                    OutwardA = TrackDirection.Backward,
                    EndB = new TrackPosition(t, mid + 7),
                    OutwardB = TrackDirection.Forward,
                    Length = 14,
                    Owner = int.MinValue + 1,
                };
                var p = Host.Network.MapPointAt(new TrackPosition(t, mid));
                if (FindSwitchGoal(probe, new MapPoint(p.x, p.y), 0, out _, out _, out _) == null)
                    continue;
                try
                {
                    // An owned loco, as one bought at a Railroad Contracts kiosk is (and a garage's).
                    var car = spawner.SpawnCarOnClosestTrack(Host.Network.WorldPointAt(new TrackPosition(t, mid)), de2, false, true, true);
                    if (car != null)
                    {
                        Line($"spawned on {Host.Network.Describe(t)} (track {t}, {graph.Length(t):F0} m), {spots[k].Distance:F0} m from the player");
                        return car;
                    }
                }
                catch (Exception e)
                {
                    Line($"spawning a {liveryId} failed: " + e.GetBaseException().Message);
                    break;
                }
            }
            // Otherwise the nearest drivable loco.
            TrainCar best = null;
            var bestDistance = 3000f;
            foreach (var loco in spawner.AllLocos)
            {
                if (loco == null || loco.derailed)
                    continue;
                var d = Vector3.Distance(loco.transform.position, player.position);
                if (d < bestDistance && Session.WhyUnsupported(loco) == null)
                {
                    best = loco;
                    bestDistance = d;
                }
            }
            return best;
        }

        private static Vector3 Abs(TrainCar car) => car.transform.position - WorldMover.currentMove;

        private static IEnumerator MeasureFrames(float seconds, Action<float> result)
        {
            var total = 0f;
            var count = 0;
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                total += Time.unscaledDeltaTime;
                count++;
            }
            result(count > 0 ? total / count : 0);
        }

        private static void Trace(Session s, string run, float t)
        {
            var o = s.Loco.SimController?.controlsOverrider;
            var e = s.Engineer;
            Line(string.Format(CultureInfo.InvariantCulture,
                "  {0} t={1:F0} v={2:F1} km/h target={3:F1} progress={4:F1} stopAt={5:F1} thr={6:F2} brake={7:F2} ind={8:F2} rev={9:F2} pipe={10:F2} | {11}",
                run, t, s.Loco.GetForwardSpeed() * 3.6f, e.TargetSpeed * 3.6, e.Progress, e.StopAt,
                o?.Throttle?.Value ?? -1, o?.Brake?.Value ?? -1, o?.IndependentBrake?.Value ?? -1, o?.Reverser?.Value ?? -1,
                s.Loco.brakeSystem != null ? s.Loco.brakeSystem.brakePipePressure : -1, e.Status));
        }

        private static void Pass(string name, string detail)
        {
            passes++;
            Line($"PASS {name}: {detail}");
        }

        private static void Fail(string name, string detail)
        {
            failures++;
            Line($"FAIL {name}: {detail}");
        }

        private static void Line(string text)
        {
            var line = $"[{Time.realtimeSinceStartup - startTime,7:F1}] {text}";
            Main.Log("self-test " + line);
            log?.WriteLine(line);
        }
    }
}
