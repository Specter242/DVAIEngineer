using System;
using System.Collections;
using System.Reflection;

namespace AIEngineer.Game
{
    internal static class PersistentJobs
    {
        private static bool looked;
        private static FieldInfo suspendedByStation;
        private static FieldInfo resuming;
        private static FieldInfo suspending;
        private static MethodInfo resume;

        /// <summary>Whether Persistent Jobs is loaded and its far-car suspension can be read.</summary>
        public static bool Present
        {
            get
            {
                Look();
                return suspendedByStation != null && resume != null;
            }
        }

        private static void Look()
        {
            if (looked)
                return;
            looked = true;
            try
            {
                var type = Type.GetType("PersistentJobsMod.Optimization.FarCarOpt, PersistentJobsMod", false);
                if (type == null)
                    return;
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                suspendedByStation = type.GetField("StationIDtoSuspendedCarGUID", any);
                resuming = type.GetField("ResumeCoroRunning", any);
                suspending = type.GetField("SuspendCoroRunning", any);
                resume = type.GetMethod("ResumeCarsInStation", any, null, new[] { typeof(string), typeof(Action<bool>) }, null);
                Main.Log(Present
                    ? "Persistent Jobs found: cars it suspends far from the player are brought back for an engineer's order"
                    : "Persistent Jobs found, but not its far-car suspension (another version?): an engineer may find a far yard empty");
            }
            catch (Exception e)
            {
                Main.Warn($"Persistent Jobs couldn't be read: {e.GetBaseException().Message}");
                suspendedByStation = null;
                resume = null;
            }
        }

        /// <summary>Whether Persistent Jobs holds cars of <paramref name="yardId"/> suspended now.</summary>
        public static bool Suspended(string yardId)
        {
            if (string.IsNullOrEmpty(yardId) || !Present)
                return false;
            try
            {
                if (!(suspendedByStation.GetValue(null) is IDictionary map))
                    return false;
                var key = yardId.Trim().ToUpperInvariant();
                return map.Contains(key) && map[key] is ICollection cars && cars.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Whether it is suspending or bringing back cars right now.</summary>
        public static bool Busy
        {
            get
            {
                if (!Present)
                    return false;
                try
                {
                    return (resuming?.GetValue(null) is bool r && r) || (suspending?.GetValue(null) is bool s && s);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Asks Persistent Jobs to bring back the cars it suspended at <paramref name="yardId"/>.</summary>
        public static bool Resume(string yardId)
        {
            if (string.IsNullOrEmpty(yardId) || !Present)
                return false;
            try
            {
                return resume.Invoke(null, new object[] { yardId, null }) is bool started && started;
            }
            catch (Exception e)
            {
                Main.Warn($"Persistent Jobs wouldn't bring back the cars at {yardId}: {e.GetBaseException().Message}");
                return false;
            }
        }
    }
}
