using AIEngineer.Core.Driving;
using UnityModManagerNet;
using UnityEngine;

namespace AIEngineer
{
    /// <summary>
    /// The mod's settings, drawn by Unity Mod Manager's window and, from the
    /// same Draw attributes, by the Comms Tablet's Settings app (DVCommsTablet
    /// decision 20, 2026-11-17): Driving, Braking, Display, and Keys, short
    /// labels with the rest in each tooltip, and a last Developer section only
    /// in this mod's own window, behind "Show developer settings", and never in
    /// the public release. Field names
    /// are what Settings.xml keeps; a field removed (the old windows' keys,
    /// CursorWithOrders, FlipMap) is ignored in an old file.
    /// </summary>
    public sealed class Settings : UnityModManager.ModSettings, IDrawable
    {
        // ---- Driving ----

        [Header("Driving")]
        [Draw("Top speed on runs, km/h", Min = 5, Max = 120, Precision = 0,
            Tooltip = "The top speed for a run to a track, a cut, or a job's cars, under the posted\nlimits. An engineer put on after a change takes it.")]
        public float MaxSpeedKmh = 50;

        [Draw("Speed for yard moves, km/h", Min = 3, Max = 30, Precision = 0,
            Tooltip = "The speed for a move of a few car lengths (Back or Ahead on the map). An\nengineer put on after a change takes it.")]
        public float YardSpeedKmh = 12;

        [Draw("Speed cap on yard tracks, km/h (reload)", Min = 5, Max = 120, Precision = 0,
            Tooltip = "The engineer's limit on every yard track. Read when a save loads: a change takes\neffect at the next load.")]
        public float YardTrackCapKmh = 30;

        [Draw("Stop short of standing cars, m", Min = 2, Max = 50, Precision = 0,
            Tooltip = "How far short of standing cars the engineer stops when it isn't coupling to\nthem. An engineer put on after a change takes it.")]
        public float CarGap = 8;

        [Draw("Start a shut-down loco when given orders",
            Tooltip = "Off: the engineer waits for you to start the loco yourself.")]
        public bool AutoStart = true;

        // ---- Braking ----

        [Header("Braking")]
        [Draw("Planned braking with cars, m/s²", Min = 0.05f, Max = 1f, Precision = 2,
            Tooltip = "How hard the engineer plans to brake for a stop with cars behind it: lower\nstarts braking earlier. An engineer put on after a change takes it.")]
        public float TrainDeceleration = 0.25f;

        [Draw("Planned braking running light, m/s²", Min = 0.05f, Max = 1.5f, Precision = 2,
            Tooltip = "How hard the engineer plans to brake for a stop with no cars. An engineer put on\nafter a change takes it.")]
        public float LightDeceleration = 0.45f;

        [Draw("Braking with cars on Realistic Brakes, m/s²", Min = 0.03f, Max = 0.5f, Precision = 2,
            Tooltip = "Planned braking with cars when Realistic Brakes is installed. A loaded car on\nAmerican equipment, Period loading, makes about 0.18 at full service. An\nengineer put on after a change takes it.")]
        public float SlowAirDeceleration = 0.12f;

        // ---- Display ----

        [Header("Display")]
        [Draw("Show notifications",
            Tooltip = "The engineers' messages in the game's notification area. They go to the mod's\nlog either way.")]
        public bool ShowNotifications = true;

        // ---- Keys ----

        // F9: the game uses none; Snow Plows' fit key is also F9 (out of scope here). Ctrl+D
        // opened the dispatcher while crouch-walking right (Ctrl crouches, D strafes).
        // Hidden while merged: the Comms Tablet's Engineers app takes the tablet's place.
        [Header("Keys")]
        [Draw("Engineer's tablet", InvisibleOn = "#Merged|True",
            Tooltip = "Gives you an engineer's tablet if you have none, or says where yours is.")]
        public KeyBinding TabletKey = Bind(KeyCode.F9);

#if !PUBLIC_RELEASE
        // ---- Developer: only in this mod's own window, behind its switch; not in the public release ----

        [Header("Developer")]
        [Draw("Show developer settings", VisibleOn = "#InModWindow|True",
            Tooltip = "Logging, the engineers' tick rate, and the helper's whistle timings: for tuning\nand bug reports.")]
        public bool ShowDeveloper;

        [Draw("Engineer ticks per second", Min = 1, Max = 20, VisibleOn = "#DeveloperShown|True",
            Tooltip = "How often a driving engineer reads its train and works the controls.")]
        public int TickRate = 5;

        [Draw("Log each route planned", VisibleOn = "#DeveloperShown|True")]
        public bool LogRoutes = false;

        [Draw("Log what the helper's whistle hears", VisibleOn = "#DeveloperShown|True",
            Tooltip = "Each signal's loudness, for tuning the whistle timings below.")]
        public bool LogWhistle = true;

        public float WhistleShortest = 0.4f;

        public float WhistleShortestLong = 1.5f;

        public float WhistleQuietEnds = 2.0f;

        public float WhistleBounce = 0.25f;

        public float WhistleDip = 0.5f;

        // Which whistle timings the settings were saved with: 0 before 0.13.3. Not in the menu.
        public int WhistleVersion;

        private const int CurrentWhistle = 1;
#else
        // The public release has no developer settings (owner, 2026-11-17: "No developer options or
        // testbeds"): their tuned values are fixed. Get-only and internal, so Settings.xml neither
        // writes them nor reads them; a file saved with the developer fields loads, those ignored.
        internal int TickRate => 5;

        internal bool LogRoutes => false;

        internal bool LogWhistle => true;
#endif

        // Which keys the settings were saved with: 0 the dispatcher on Ctrl+D, 1 on F9, 2 the
        // tablet on F9 and the dispatcher on Shift+F9. The dispatcher and its key are gone
        // (0.15.0); kept so a later move knows what it moves from. Not in the menu.
        public int KeysVersion;

        private const int CurrentKeys = 2;

        // ---- Visibility rules (get-only: not saved) ----

        /// <summary>True only while this mod's own window draws the settings (<see cref="Main.DrawSettings"/>), not on the Comms Tablet.</summary>
        public bool InModWindow => Main.InSettingsWindow;

#if !PUBLIC_RELEASE
        /// <summary>The developer rows: in this mod's own window, once its switch is on.</summary>
        public bool DeveloperShown => Main.InSettingsWindow && ShowDeveloper;
#endif

        /// <summary>
        /// The Comms Tablet shows this mod's Engineers app, and the engineer's
        /// own tablet is retired. Asked once a second at most: the settings
        /// window reads it in every GUI pass while open.
        /// </summary>
        public bool Merged
        {
            get
            {
                if (Time.unscaledTime >= mergedAskAgain)
                {
                    merged = CommsTabletLink.Merged;
                    mergedAskAgain = Time.unscaledTime + 1f;
                }
                return merged;
            }
        }

        private static bool merged;
        private static float mergedAskAgain = float.MinValue;

#if !PUBLIC_RELEASE
        /// <summary>The whistle timings above, put in order.</summary>
        public WhistleTiming Whistle => new WhistleTiming
        {
            Shortest = WhistleShortest,
            ShortestLong = WhistleShortestLong,
            QuietEnds = WhistleQuietEnds,
            Bounce = WhistleBounce,
            Dip = WhistleDip,
        }.Sane();
#else
        /// <summary>The owner's whistle timings of 2026-11-16, fixed.</summary>
        internal WhistleTiming Whistle => WhistleTiming.Default;
#endif

#if !PUBLIC_RELEASE
 public bool MoveOldWhistle()
        {
            if (WhistleVersion >= CurrentWhistle)
                return false;
            var fresh = new Settings();
            WhistleShortestLong = fresh.WhistleShortestLong;
            WhistleQuietEnds = fresh.WhistleQuietEnds;
            WhistleBounce = fresh.WhistleBounce;
            if (WhistleShortest < 0.1f || WhistleShortest > 1f)
                WhistleShortest = fresh.WhistleShortest;
            if (WhistleDip < 0.1f || WhistleDip > 0.9f)
                WhistleDip = fresh.WhistleDip;
            WhistleVersion = CurrentWhistle;
            return true;
        }
#endif

        /// <summary>
        /// Records the current keys version, once. The keys it once moved
        /// (the old dispatcher's) are gone; the tablet key an older file
        /// lacks takes its default. Returns whether anything changed.
        /// </summary>
        public bool MoveOldKeys()
        {
            if (KeysVersion >= CurrentKeys)
                return false;
            KeysVersion = CurrentKeys;
            return true;
        }

        // Change() also caches the key for UMM's new-input-system path.
        private static KeyBinding Bind(KeyCode key, bool ctrl = false, bool shift = false, bool alt = false)
        {
            var binding = new KeyBinding();
            binding.Change(key, ctrl, shift, alt);
            return binding;
        }

        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);

        public void OnChange()
        {
        }
    }
}
