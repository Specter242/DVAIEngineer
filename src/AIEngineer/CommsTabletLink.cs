using System;
using System.Linq;
using System.Reflection;

namespace AIEngineer
{
    internal static class CommsTabletLink
    {
        private static Func<string, bool> merged;
        private static bool said;

        internal static bool Merged
        {
            get
            {
                if (merged == null)
                {
                    var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CommsTablet");
                    var method = host?.GetType("CommsTablet.GuestContract", false)?.GetMethod("Merged",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (method == null)
                        return false;
                    merged = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), method, false);
                    if (merged == null)
                        return false;
                }
                bool yes;
                try
                {
                    yes = merged(CommsTabletApp.Id);
                }
                catch (Exception)
                {
                    return false;
                }
                if (yes && !said)
                {
                    said = true;
                    Main.Log("Merged with the Comms Tablet: the Engineers app is on its home screen, and the engineer's own tablet is retired "
                        + "(not in the starting kit, not sold, not given by the tablet key; a saved one loads as the Comms Tablet)");
                }
                return yes;
            }
        }
    }
}
