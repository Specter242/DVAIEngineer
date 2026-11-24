using System;
using System.Reflection;
using HarmonyLib;

namespace AIEngineer.Game
{
    internal static class BrakesMod
    {
        private static bool looked;
        private static Func<bool> active;

        private static bool lookedForHandle;
        private static Type handleType;
        private static PropertyInfo handleLayout;
        private static PropertyInfo layoutStockNotches;

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
                        var getter = AccessTools.TypeByName("RealisticBrakes.Api")
                            ?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
                        if (getter != null && getter.ReturnType == typeof(bool))
                            active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
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
    }
}
