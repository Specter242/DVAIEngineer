using System;
using DV.UI;
using DV.UIFramework;
using DV.Utils;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using Object = UnityEngine.Object;

namespace AIEngineer
{
    /// <summary>
    /// Unity Mod Manager entry. It reads tracks, cars, and switches, works
    /// the locos through the game's own control overrider, and throws
    /// switches through Junction.Switch, which the Multiplayer mod already
    /// shares. Its one patch adds a mode to the Comms Radio.
    /// </summary>
    public static class Main
    {
        private static bool notifyWarned;

        public static UnityModManager.ModEntry Mod { get; private set; }

        public static Settings Settings { get; private set; }

        /// <summary>
        /// True only while <see cref="DrawSettings"/> draws this mod's own
        /// settings window: the Comms Tablet's Settings app reads the same
        /// fields with this false, so the window-only rows (the developer
        /// settings) never show there.
        /// </summary>
        internal static bool InSettingsWindow { get; set; }

        /// <summary>
        /// The mod's settings window in Unity Mod Manager: the Draw fields,
        /// then, under the developer settings once shown, the helper's
        /// whistle timings (not in the public release).
        /// </summary>
        internal static void DrawSettings(UnityModManager.ModEntry entry)
        {
            InSettingsWindow = true;
            try
            {
                Settings.Draw(entry);
#if !PUBLIC_RELEASE
                if (Settings.DeveloperShown)
                    UI.WhistleMenu.Draw(Settings);
#endif
            }
            finally
            {
                InSettingsWindow = false;
            }
        }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            var moved = Settings.MoveOldKeys();
#if !PUBLIC_RELEASE
            moved |= Settings.MoveOldWhistle();
#endif
            if (moved)
                Settings.Save(modEntry);
            modEntry.OnGUI = DrawSettings;
            modEntry.OnSaveGUI = entry => Settings.Save(entry);
            WorldStreamingInit.LoadingFinished += Host.OnWorldLoaded;
            UnloadWatcher.UnloadRequested += Host.OnUnload;
            var harmony = new Harmony(modEntry.Info.Id);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Main).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), inherit: true))
                    continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Error($"Couldn't apply Harmony patch {type.FullName}", e);
                }
            }
            var host = new GameObject("AIEngineer");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Host>();
            Log($"loaded {modEntry.Info.Version}");
            return true;
        }

        public static void Log(string message) => Mod?.Logger.Log(message);

        public static void Warn(string message) => Mod?.Logger.Warning(message);

        public static void Error(string message, Exception e) => Mod?.Logger.Error($"{message}: {e}");

        /// <summary>Shows a line in the game's notification area, as the autosave notice does.</summary>
#if !PUBLIC_RELEASE
        /// <summary>The last notification shown (the self-test's).</summary>
        internal static string LastNotified;
#endif

        public static void Notify(string text)
        {
#if !PUBLIC_RELEASE
            LastNotified = text;
#endif
            Log(text);
            if (!Settings.ShowNotifications)
                return;
            try
            {
                var canvas = SingletonBehaviour<ACanvasController<CanvasController.ElementType>>.Instance;
                canvas?.NotificationManager?.ShowNotification(text, null, 5f, clearExisting: false, localize: false);
            }
            catch (Exception e)
            {
                if (!notifyWarned)
                    Warn($"game notifications unavailable ({e.GetBaseException().Message}); messages go to the log");
                notifyWarned = true;
            }
        }
    }
}
