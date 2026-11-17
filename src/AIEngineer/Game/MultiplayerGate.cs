using System;
using System.Reflection;

namespace AIEngineer.Game
{
    /// <summary>
    /// Whether this game may run engineers: always alone, and only on the
    /// host in a Multiplayer session (the host simulates every train, and
    /// the Multiplayer mod shares the controls and switches the engineer
    /// sets). Read by reflection, so the mod needs no Multiplayer assembly.
    /// </summary>
    internal static class MultiplayerGate
    {
        private static bool looked;
        private static PropertyInfo instance;
        private static PropertyInfo isConnected;
        private static PropertyInfo isHost;

        public static bool MayDrive
        {
            get
            {
                try
                {
                    if (!looked)
                        Look();
                    var api = instance?.GetValue(null);
                    if (api == null)
                        return true;
                    var connected = (bool)isConnected.GetValue(api);
                    return !connected || (bool)isHost.GetValue(api);
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private static void Look()
        {
            looked = true;
            Type api = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                api = assembly.GetType("MPAPI.MultiplayerAPI", false);
                if (api != null)
                    break;
            }
            if (api == null)
                return;
            instance = api.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var iface = instance?.PropertyType;
            isConnected = iface?.GetProperty("IsConnected");
            isHost = iface?.GetProperty("IsHost");
            if (isConnected == null || isHost == null)
                instance = null;
        }
    }
}
