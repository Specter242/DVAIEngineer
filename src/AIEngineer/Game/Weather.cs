using System;
using System.Linq;
using System.Reflection;

namespace AIEngineer.Game
{
    /// <summary>
    /// The weather's wetness, 0 dry to 1 soaked, read softly from the
    /// game's weather driver (which halves the rail's grip when wet): 0 if
    /// it can't be read. Read only when something is to be said about it.
    /// </summary>
    internal static class Weather
    {
        private static bool looked;
        private static PropertyInfo instance;
        private static PropertyInfo wetness;
        private static MethodInfo toNumber;
        private static PropertyInfo value;
        private static float cachedWetness;
        private static float cachedAt = float.NegativeInfinity;

        /// <summary>The weather's wetness, refreshed at most every two seconds so ticks never allocate.</summary>
        public static float WetnessCached()
        {
            var now = UnityEngine.Time.unscaledTime;
            if (now - cachedAt >= 2f)
            {
                cachedWetness = Wetness();
                cachedAt = now;
            }
            return cachedWetness;
        }

        public static float Wetness()
        {
            try
            {
                if (!looked)
                {
                    looked = true;
                    var driver = Type.GetType("DV.WeatherSystem.WeatherDriver, DV.WeatherSystem", false);
                    instance = driver?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    wetness = driver?.GetProperty("WetnessValue");
                    var type = wetness?.PropertyType;
                    toNumber = type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "op_Implicit" && (m.ReturnType == typeof(float) || m.ReturnType == typeof(double)));
                    value = toNumber == null ? type?.GetProperty("Value") : null;
                }
                var driverNow = instance?.GetValue(null);
                var w = driverNow != null ? wetness?.GetValue(driverNow) : null;
                if (w == null)
                    return 0f;
                var number = toNumber != null ? toNumber.Invoke(null, new[] { w }) : value?.GetValue(w);
                return number != null ? Convert.ToSingle(number) : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }
    }
}
