using System;
using System.Collections.Generic;
using AIEngineer.Core.Driving;
using AIEngineer.Game;
using UnityEngine;

namespace AIEngineer
{
    /// <summary>
    /// Runs the engineers from Unity's update loop. Idle, it costs a key
    /// check a frame (the tablet key): the network is built when first
    /// needed, cars are scanned only while an engineer is driving, and
    /// engineers tick a few times a second, spread across frames.
    /// </summary>
    internal sealed class Host : MonoBehaviour
    {
        private const float ScanInterval = 0.5f;

        private static Network network;
        private static float nextBuildAttempt;
        private static World world;
        private static readonly List<Session> sessions = new List<Session>();
        private float nextScan;
        private int tickCursor;

        public static Host Instance { get; private set; }

        public static IReadOnlyList<Session> Sessions => sessions;

        public static Network Network => EnsureWorld() ? network : null;

        public static World World => EnsureWorld() ? world : null;

        private bool updating;

#if !PUBLIC_RELEASE
        private SettingsDrawer drawer;
#endif

        private void Awake()
        {
            Instance = this;
            // The self-test's draw of the settings window is a component of its own, switched on
            // only while asked: Unity runs a layout pass (and allocates for it) every frame for any
            // enabled behaviour with an OnGUI, drawn or not.
#if !PUBLIC_RELEASE
            drawer = gameObject.AddComponent<SettingsDrawer>();
            drawer.enabled = false;
#endif
            Main.Log("host awake");
        }

        public static void OnWorldLoaded()
        {
            // The game calls this inside its own loading coroutine, and OnUnload first among its
            // unload handlers: nothing thrown here may reach it.
            try
            {
                WorldLoaded();
            }
            catch (Exception e)
            {
                Main.Error("AI engineer couldn't set up for the loaded world", e);
            }
        }

        private static void WorldLoaded()
        {
            DisposeAll();
            network = null;
            world = null;
            try
            {
                UI.Radio.InjectAll();
            }
            catch (Exception e)
            {
                Main.Error("Couldn't add the AI engineer radio mode", e);
            }
#if !PUBLIC_RELEASE
            Diagnostics.SelfTest.OnWorldLoaded();
#endif
            // The network and the booklet read a few seconds after loading, not at the tablet's first press
            // (the booklet only when this mod's own tablet is in use: see Prepare).
            prepareAt = Time.unscaledTime + 5f;
        }

        private static float prepareAt = -1;

        /// <summary>
        /// Once per load, when the world has settled: the network built and
        /// the booklet read, ahead of the tablet. While the Comms Tablet
        /// merges this mod, the booklet is read when the Engineers app's map
        /// is first drawn instead: the Comms Tablet reads a copy of its own
        /// for its Switches app, and a session that never opens this map
        /// should not read the booklet twice.
        /// </summary>
        private static void Prepare()
        {
            if (prepareAt < 0 || Time.unscaledTime < prepareAt)
                return;
            prepareAt = -1;
            try
            {
                if (!EnsureWorld())
                    return;
                if (CommsTabletLink.Merged)
                    Main.Log("The station booklet is read when the Engineers app's map is first opened (merged with the Comms Tablet)");
                else
                    Game.BookletReader.Get(network);
            }
            catch (Exception e)
            {
                Main.Error("Couldn't read the station booklet ahead of the tablet", e);
            }
        }

        public static void OnUnload()
        {
            try
            {
                DisposeAll();
            }
            catch (Exception e)
            {
                Main.Error("AI engineer couldn't let go of its locos at unload", e);
            }
            network = null;
            world = null;
            prepareAt = -1;
            nextBuildAttempt = 0f;
            try
            {
                BookletRenderer.ResetForUnload();
                BookletReader.Reset();
                UI.Tablet.TabletMap.ResetForUnload();
                UI.Tablet.TabletUi.ResetForUnload();
                UI.Tablet.TabletPages.ResetForUnload();
                Dispatch.ResetForUnload();
                Service.ResetForUnload();
            }
            catch (Exception e)
            {
                Main.Error("AI engineer couldn't reset cached state at unload", e);
            }
        }

        private static void DisposeAll()
        {
            foreach (var s in sessions)
            {
                try
                {
                    s.Dispose();
                }
                catch (Exception e)
                {
                    Main.Error($"Couldn't let go of {s.Name}", e);
                }
            }
            sessions.Clear();
        }

        private static bool EnsureWorld()
        {
            if (world != null)
                return true;
            if (!WorldStreamingInit.IsLoaded || RailTrackRegistryBase.Instance == null || Time.unscaledTime < nextBuildAttempt)
                return false;
            try
            {
                network = Network.Build(Mathf.Clamp(Main.Settings.YardTrackCapKmh, 5, 120));
                world = new World(network);
                return true;
            }
            catch (Exception e)
            {
                // Not again every frame: once in a while, in case the world was still settling.
                nextBuildAttempt = Time.unscaledTime + 15f;
                Main.Error("Couldn't read the track network", e);
                network = null;
                world = null;
                return false;
            }
        }

        private static float nextGateNotice;

        /// <summary>Seconds between looks at an engineer with nothing to do.</summary>
        private const float IdleInterval = 1f;

        /// <summary>The session for a loco, made if asked; null for a car that isn't a loco, or on a multiplayer client.</summary>
        public static Session SessionFor(TrainCar loco, bool create)
        {
            if (loco == null || !loco.IsLoco)
                return null;
            foreach (var s in sessions)
            {
                if (s.Loco == loco)
                    return s;
            }
            if (!create || World == null)
                return null;
            if (!MultiplayerGate.MayDrive)
            {
                // Before anything is touched: handing a client's loco jobs would uncouple cars at once.
                if (Time.unscaledTime > nextGateNotice)
                    Main.Notify("AI engineer: only the host runs engineers in multiplayer.");
                nextGateNotice = Time.unscaledTime + 10f;
                return null;
            }
            var session = new Session(loco, world, NewSettings())
            {
                NextTick = Time.time,
            };
            sessions.Add(session);
            return session;
        }

        /// <summary>Whether any loco of the train has an engineer on it, a helper included.</summary>
        public static bool EngineerOn(Trainset set)
        {
            if (set == null)
                return false;
            foreach (var s in sessions)
            {
                if (s.Loco != null && s.Loco.trainset == set && s.Engineer.Mode != EngineerMode.Off)
                    return true;
            }
            return false;
        }

        /// <summary>The loco an engineer would drive for a car: the car itself, or its consist's first loco.</summary>
        public static TrainCar LocoOf(TrainCar car)
        {
            if (car == null)
                return null;
            if (car.IsLoco)
                return car;
            var set = car.trainset;
            if (set != null && set.locoIndices.Count > 0)
                return set.cars[set.locoIndices[0]];
            return null;
        }

        public static EngineerSettings NewSettings()
        {
            var s = Main.Settings;
            return new EngineerSettings
            {
                // An emptied field in the mod's settings page reads as 0: nothing here may be.
                MaxSpeed = Mathf.Clamp(s.MaxSpeedKmh, 5, 120) / 3.6,
                YardSpeed = Mathf.Clamp(s.YardSpeedKmh, 3, 30) / 3.6,
                CarGap = Mathf.Clamp(s.CarGap, 2, 50),
                TrainBraking = new BrakingModel(Math.Max(0.05, s.TrainDeceleration), 4),
                LightBraking = new BrakingModel(Math.Max(0.05, s.LightDeceleration), 1.5),
                SlowAirBraking = new BrakingModel(Math.Max(0.03, s.SlowAirDeceleration), 10),
            };
        }

        private void Update()
        {
            try
            {
                if (!updating)
                {
                    updating = true;
#if !PUBLIC_RELEASE
                    Main.Log($"host running; self-test {(Diagnostics.SelfTest.Requested ? "requested" : "not requested")} (mod folder {Main.Mod.Path})");
#else
                    Main.Log($"host running (mod folder {Main.Mod.Path})");
#endif
                }
                if (Main.Settings.TabletKey.Down())
                    Main.Notify(Held.EngineerTablet.Requested());
#if !PUBLIC_RELEASE
                Diagnostics.SelfTest.TickMenu();
#endif
                Prepare();
#if !PUBLIC_RELEASE
                var drawing = SettingsDrawer.DrawForTest > 0;
                if (drawer != null && drawer.enabled != drawing)
                    drawer.enabled = drawing;
#endif
                if (sessions.Count == 0)
                    return;
                // A helper hears the lead's whistle every frame: a short blast can be a fifth of a second.
                for (var i = 0; i < sessions.Count; i++)
                    sessions[i].Helper?.Listen(Time.time);
                Run(Time.time);
            }
            catch (Exception e)
            {
                Main.Error("Engineer update failed", e);
            }
        }

        private void Run(float now)
        {
            var anyDriving = false;
            var anyOn = false;
            for (var i = sessions.Count - 1; i >= 0; i--)
            {
                var s = sessions[i];
                if (s.Loco == null)
                {
                    s.Dispose();
                    sessions.RemoveAt(i);
                    continue;
                }
                // One crew on a train: an engineer put on it relieves the conductor.
                if (s.ConductorWorking && EngineerOn(s.Loco.trainset))
                    s.StandConductorDown("an engineer has the train");
                if (s.Engineer.Mode == EngineerMode.Off && !s.ConductorWorking)
                    continue;
                anyOn = true;
                // Standing with no orders it needs no car scan, and only a look now and then
                // (for the player's hand on a lever, or the train rolling off).
                if (!s.Idle)
                    anyDriving = true;
            }
            if (!anyOn || world == null)
                return;
            if (anyDriving && now >= nextScan)
            {
                nextScan = now + ScanInterval;
                world.ScanCars();
                // A scan is this frame's work; engineers tick on the next.
                return;
            }
            // At most two engineers a frame, round robin, each at its own rate.
            var interval = 1f / Mathf.Clamp(Main.Settings.TickRate, 1, 20);
            var budget = 2;
            for (var k = 0; k < sessions.Count && budget > 0; k++)
            {
                tickCursor = (tickCursor + 1) % sessions.Count;
                var s = sessions[tickCursor];
                var conducting = s.Engineer.Mode == EngineerMode.Off;
                if (conducting && !s.ConductorWorking)
                    continue;
                var idle = s.Idle;
                // Given work while idle: at once, not at its next look.
                if (!idle && s.NextTick > now + interval)
                    s.NextTick = now;
                if (now < s.NextTick)
                    continue;
                s.NextTick = now + (idle ? IdleInterval : interval);
                budget--;
                if (conducting)
                {
                    // It looks after its own failures, and has no train to stop.
                    s.TickConductor(now);
                    continue;
                }
                try
                {
                    s.Tick(now);
                }
                catch (Exception e)
                {
                    Main.Error($"Engineer on {s.Name} failed; it stops the train and lets go", e);
                    Main.Notify($"AI engineer: {s.Name}'s engineer hit an error and has stopped the train and let go (see the mod's log).");
                    try
                    {
                        s.Stop(secure: true);
                    }
                    catch (Exception again)
                    {
                        Main.Error($"Couldn't secure {s.Name}", again);
                    }
                }
            }
        }

        private void LateUpdate()
        {
            UI.Tablet.TabletMap.PutAwayIfUnseen();
        }
    }

#if !PUBLIC_RELEASE
    /// <summary>
    /// For the self-test: the mod's settings window drawn as Unity Mod
    /// Manager draws it (<see cref="Main.DrawSettings"/>), the developer
    /// settings shown, for a few GUI passes, and any fault kept. Enabled only
    /// while asked (see <see cref="Host"/>).
    /// </summary>
    internal sealed class SettingsDrawer : MonoBehaviour
    {
        /// <summary>Set by the self-test: the GUI passes left to draw the settings in.</summary>
        internal static int DrawForTest;

        /// <summary>What the self-test's draw found: null until drawn, "" if it drew cleanly.</summary>
        internal static string Drawn;

        private void OnGUI()
        {
            if (DrawForTest <= 0)
                return;
            DrawForTest--;
            var shown = Main.Settings.ShowDeveloper;
            try
            {
                Main.Settings.ShowDeveloper = true;
                GUILayout.BeginArea(new Rect(20, 20, 1000, 900));
                Main.DrawSettings(Main.Mod);
                GUILayout.EndArea();
                Drawn = Drawn ?? "";
            }
            catch (ExitGUIException)
            {
            }
            catch (Exception e)
            {
                Drawn = e.ToString();
            }
            finally
            {
                Main.Settings.ShowDeveloper = shown;
            }
        }
    }
#endif
}
