using System;
using System.Reflection;
using HarmonyLib;

namespace AIEngineer.Game
{
    internal static class BrakesMod
    {
        private static bool looked;
        private static Func<bool> active;
        private static Func<TrainCar, string> equipment;
        private static Func<TrainCar, string, float> pressure;
        private static Func<TrainCar, float> bailOff;

        private static bool lookedForHandle;
        private static Type handleType;
        private static PropertyInfo handleLayout;
        private static PropertyInfo layoutStockNotches;
        private static MethodInfo handleZoneOf;
        private static MethodInfo handleClearZone;

        /// <summary>
        /// The stock notches of the train brake handle on <paramref name="lever"/>
        /// if Realistic Brakes widened it (its <c>TrainBrakeHandle</c>, whose
        /// value keeps the stock scale); 0 if it didn't, or isn't loaded.
        /// </summary>
        public static int WidenedHandleStockNotches(UnityEngine.GameObject lever)
        {
            if (!lookedForHandle)
            {
                lookedForHandle = true;
                try
                {
                    handleType = AccessTools.TypeByName("RealisticBrakes.Controls.TrainBrakeHandle");
                    handleLayout = handleType?.GetProperty("Layout", BindingFlags.Public | BindingFlags.Instance);
                    layoutStockNotches = handleLayout?.PropertyType.GetProperty("StockNotches", BindingFlags.Public | BindingFlags.Instance);
                    handleZoneOf = handleType?.GetMethod("ZoneOf", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DV.Simulation.Brake.BrakeSystem) }, null);
                    handleClearZone = handleType?.GetMethod("ClearZone", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DV.Simulation.Brake.BrakeSystem) }, null);
                    if (handleType != null && layoutStockNotches == null)
                        Main.Warn("Realistic Brakes' train brake handle found, but not its layout (another version?): its added notches are read as none");
                }
                catch (Exception e)
                {
                    handleType = null;
                    Main.Warn($"Couldn't read Realistic Brakes' train brake handle ({e.GetType().Name}: {e.Message})");
                }
            }
            if (handleType == null || lever == null)
                return 0;
            try
            {
                var handle = lever.GetComponent(handleType);
                if (handle == null)
                    return 0;
                // Widened, but its layout unread: no notches of its own, and no spring, on the stock scale.
                var layout = handleLayout?.GetValue(handle);
                return layout != null && layoutStockNotches?.GetValue(layout) is int stock && stock >= 2 ? stock : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        public static bool Active
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    try
                    {
                        var api = AccessTools.TypeByName("RealisticBrakes.Api");
                        var getter = api?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                        if (getter != null && getter.ReturnType == typeof(bool))
                            active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
                        var eq = api?.GetMethod("Equipment", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(TrainCar) }, null);
                        if (eq != null && eq.ReturnType == typeof(string))
                            equipment = (Func<TrainCar, string>)Delegate.CreateDelegate(typeof(Func<TrainCar, string>), eq);
                        var pr = api?.GetMethod("Pressure", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(TrainCar), typeof(string) }, null);
                        if (pr != null && pr.ReturnType == typeof(float))
                            pressure = (Func<TrainCar, string, float>)Delegate.CreateDelegate(typeof(Func<TrainCar, string, float>), pr);
                        var bail = api?.GetMethod("BailOff", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(TrainCar) }, null);
                        if (bail != null && bail.ReturnType == typeof(float))
                            bailOff = (Func<TrainCar, float>)Delegate.CreateDelegate(typeof(Func<TrainCar, float>), bail);
                        Main.Log(active != null ? "Realistic Brakes found: trains with cars drive on slow air while it runs" : "Realistic Brakes not loaded");
                    }
                    catch (Exception e)
                    {
                        Main.Warn($"Couldn't read Realistic Brakes' API ({e.GetType().Name}: {e.Message}); driving on stock air");
                    }
                }
                try
                {
                    return active != null && active();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="car"/>'s independent brake is Realistic Brakes'
        /// American E-T S-6 manual-lap valve (same condition as its <c>IndependentHandle.IsS6</c>).
        /// </summary>
        public static bool IsManualLapIndependent(TrainCar car)
        {
            if (!Active || car == null || car.brakeSystem == null || equipment == null)
                return false;
            var bs = car.brakeSystem;
            if (!bs.hasTrainBrake || bs.selfLappingController || !bs.hasIndependentBrake)
                return false;
            try
            {
                return equipment(car) == "American";
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The engine's brake cylinder pressure, gauge bar (0 when released).</summary>
        public static float CylinderPressure(TrainCar car)
        {
            if (car == null || car.brakeSystem == null)
                return 0f;
            if (Active && pressure != null)
            {
                try
                {
                    var p = pressure(car, "BrakeCylinder");
                    if (!float.IsNaN(p))
                        return UnityEngine.Mathf.Max(0f, p);
                }
                catch (Exception)
                {
                }
            }
            return UnityEngine.Mathf.Max(0f, car.brakeSystem.brakeCylinderPressure - 1f);
        }

        /// <summary>
        /// Clears any added train-brake handle zone (e.g. an H-6 left in Release
        /// or Holding, which both read 0 on the stock scale and block the E-T
        /// release pipe) back to Running. Returns true if a zone was cleared.
        /// </summary>
        public static bool EnsureRunning(TrainCar car)
        {
            if (!Active || car == null || car.brakeSystem == null)
                return false;
            if (!lookedForHandle)
                WidenedHandleStockNotches(null);
            if (handleZoneOf == null || handleClearZone == null)
                return false;
            try
            {
                var zone = handleZoneOf.Invoke(null, new object[] { car.brakeSystem });
                if (zone != null && Convert.ToInt32(zone) != 0)
                {
                    handleClearZone.Invoke(null, new object[] { car.brakeSystem });
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>Bails off <paramref name="car"/>'s engine brake via Realistic Brakes' public API; returns gauge bar bailed.</summary>
        public static float BailOff(TrainCar car)
        {
            if (!Active || car == null || bailOff == null)
                return 0f;
            try
            {
                return bailOff(car);
            }
            catch (Exception)
            {
                return 0f;
            }
        }
    }
}
