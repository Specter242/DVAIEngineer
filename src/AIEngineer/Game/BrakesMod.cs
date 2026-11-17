using System;
using System.Reflection;
using HarmonyLib;

namespace AIEngineer.Game
{
 internal static class BrakesMod
    {
        private static bool looked;
        private static Func<bool> active;

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
