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
        private static bool apiFound;
        private static bool warned;
        private static Func<bool> mayDrive;

        public static bool MayDrive
        {
            get
            {
                try
                {
                    if (!looked)
                        Look();
                    if (!apiFound)
                        return true;
                    return mayDrive != null && mayDrive();
                }
                catch (Exception e)
                {
                    if (!warned)
                    {
                        warned = true;
                        Main.Warn($"Multiplayer state couldn't be read; engineers disabled until it can: {e.GetBaseException().Message}");
                    }
                    return !apiFound;
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
            apiFound = true;
            var instance = api.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var iface = instance?.PropertyType;
            var getInstance = instance?.GetGetMethod();
            var getConnected = iface?.GetProperty("IsConnected")?.GetGetMethod();
            var getHost = iface?.GetProperty("IsHost")?.GetGetMethod();
            if (iface == null || iface.IsValueType || getInstance == null || getConnected == null || getHost == null
                || getConnected.ReturnType != typeof(bool) || getHost.ReturnType != typeof(bool))
            {
                if (!warned)
                {
                    warned = true;
                    Main.Warn("Multiplayer API found, but its host flags didn't match; engineers disabled for safety");
                }
                return;
            }
            var bind = typeof(MultiplayerGate).GetMethod(nameof(Bind), BindingFlags.NonPublic | BindingFlags.Static)?.MakeGenericMethod(iface);
            mayDrive = (Func<bool>)bind?.Invoke(null, new object[] { getInstance, getConnected, getHost });
        }

        private static Func<bool> Bind<TApi>(MethodInfo getInstance, MethodInfo getConnected, MethodInfo getHost) where TApi : class
        {
            var instance = (Func<TApi>)Delegate.CreateDelegate(typeof(Func<TApi>), getInstance);
            var connected = (Func<TApi, bool>)Delegate.CreateDelegate(typeof(Func<TApi, bool>), getConnected);
            var host = (Func<TApi, bool>)Delegate.CreateDelegate(typeof(Func<TApi, bool>), getHost);
            return () =>
            {
                var api = instance();
                return api == null || !connected(api) || host(api);
            };
        }
    }
}
