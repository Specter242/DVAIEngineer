using System;
using Console = System.Console;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml;
using System.Xml.Serialization;
using AIEngineer.Held;
using CommsRadioController = DV.CommsRadioController;
using DV.Items;
using DV.Shops;
using HarmonyLib;

namespace AIEngineer.AdapterCheck
{
    /// <summary>
    /// Checks the game members the mod reaches by name, which the compiler
    /// can't: the Comms Radio's private mode list and Awake (the radio mode);
    /// the engineer's tablet's load patch on the game's two item loaders; and,
    /// when given, the Multiplayer API's host flags and Skin Manager's item
    /// loaders, which the tablet's load patch must find a load in.
    /// </summary>
    internal static class Program
    {
        private static int failures;

        private static int Main(string[] args)
        {
            Check(AccessTools.Field(typeof(CommsRadioController), "allModes") != null, "CommsRadioController.allModes exists");
            Check(AccessTools.Method(typeof(CommsRadioController), "Awake") != null, "CommsRadioController.Awake exists");
            Check(typeof(Junction).GetField("selectedBranch") != null, "Junction.selectedBranch exists");
            Check(AIEngineer.Game.TractionMotors.TemperatureField != null && AIEngineer.Game.TractionMotors.FuseField != null, "the traction motors' temperature and fuse exist");
            Check(AIEngineer.Game.TractionMotors.MaxAmpsField != null && AIEngineer.Game.TractionMotors.GeneratorAmpsField != null, "the traction generator's current and limit exist");
            Check(AccessTools.Field(typeof(LocoSim.Implementations.Boiler), "crownSheetNormalizedWaterLevel") != null, "the boiler's crown sheet level exists");
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            CheckLoads(AccessTools.DeclaredMethod(typeof(StartingItemsController), "InstantiateItem"), "the game's saved-item loader");
            CheckLoads(AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(typeof(GlobalShopController), "InstantiatePurchasedItems")), "the game's purchased-item loader");
            CheckSettingsFile();
            CheckFiremanBinding();
            CheckGivenKey();
#if PUBLIC_RELEASE
            CheckNoDeveloperTools();
#endif

            foreach (var path in args)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"skip: {path} not installed");
                    continue;
                }
                if (Path.GetFileName(path) == "SkinManagerMod.dll")
                {
                    CheckSkinManager(path);
                    continue;
                }
                if (Path.GetFileName(path) == "RealisticFiring.dll")
                {
                    CheckRealisticFiring(path);
                    continue;
                }
                if (Path.GetFileName(path) == "Multiplayer.dll")
                {
                    CheckMultiplayerItems(path);
                    continue;
                }
                var api = Assembly.LoadFrom(path).GetType("MPAPI.MultiplayerAPI", false);
                Check(api != null, $"{Path.GetFileName(path)} has MPAPI.MultiplayerAPI");
                var instance = api?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                Check(instance != null, "MultiplayerAPI.Instance exists");
                var iface = instance?.PropertyType;
                Check(iface?.GetProperty("IsConnected")?.PropertyType == typeof(bool), "IMultiplayerAPI.IsConnected is a bool");
                Check(iface?.GetProperty("IsHost")?.PropertyType == typeof(bool), "IMultiplayerAPI.IsHost is a bool");
            }

            Console.WriteLine(failures == 0 ? "adapter checks passed" : $"{failures} adapter checks FAILED");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>The tablet's load patch, run on a method's own instructions, wraps at least one load in it.</summary>
        private static void CheckLoads(MethodBase method, string what)
        {
            var after = AccessTools.DeclaredMethod(typeof(EngineerTabletLoading), nameof(EngineerTabletLoading.After));
            var wrapped = -1;
            try
            {
                if (method != null)
                    wrapped = EngineerTabletLoading.Transpiler(PatchProcessor.GetOriginalInstructions(method), method)
                        .Count(i => i.opcode == OpCodes.Call && Equals(i.operand, after));
            }
            catch (Exception e)
            {
                Console.WriteLine($"     {e.GetType().Name}: {e.Message}");
            }
            Check(wrapped > 0, $"the tablet's load patch wraps a load in {what} ({(wrapped < 0 ? "not read" : wrapped.ToString())})");
        }

        /// <summary>Skin Manager loaded as the game would: both its item loaders are found, and each has a load the tablet's patch wraps.</summary>
        private static void CheckSkinManager(string path)
        {
            skinManagerDir = Path.GetDirectoryName(Path.GetFullPath(path));
            var assembly = Assembly.LoadFrom(path);
            Console.WriteLine($"     {assembly.GetName().Name} {assembly.GetName().Version}");
            var targets = SkinManagerTabletLoading.Targets();
            Check(SkinManagerTabletLoading.Present && targets.Count == SkinManagerTabletLoading.Loaders.Length,
                $"Skin Manager's item loaders found ({targets.Count} of {SkinManagerTabletLoading.Loaders.Length})");
            foreach (var target in targets)
                CheckLoads(target, $"Skin Manager's {target.DeclaringType?.Name}.{target.Name}");
        }

        /// <summary>
        /// The Multiplayer mod loaded as the game would: the item lookup it builds client items from
        /// is found by the tablet's patch, and it reads the game's item catalog, which the tablet joins.
        /// </summary>
        private static void CheckMultiplayerItems(string path)
        {
            modDirs.Add(Path.GetDirectoryName(Path.GetFullPath(path)));
            MethodBase target = null;
            var readsCatalog = false;
            try
            {
                Assembly.LoadFrom(path);
                target = EngineerTabletMultiplayerItems.Target();
                readsCatalog = target != null && PatchProcessor.GetOriginalInstructions(target)
                    .Any(i => i.operand is FieldInfo f && f.Name == "items" && f.DeclaringType == typeof(ItemsConfig));
            }
            catch (Exception e)
            {
                Console.WriteLine($"     {e.GetType().Name}: {e.Message}");
            }
            Check(target != null && EngineerTabletMultiplayerItems.Prepare(), $"Multiplayer's {EngineerTabletMultiplayerItems.Manager.Split('.').Last()}.BuildPrefabLookup is found for the tablet's catalog patch");
            Check(readsCatalog, "it builds its item lookup from the game's item catalog (ItemsConfig.items)");
        }

        private static readonly List<string> modDirs = new List<string>();

        // Stand-ins for Realistic Firing's FiremanApi: as version 1 has it, at another version, and with an entry changed.
        public static class FiremanV1
        {
            public const int Version = 1;
            public static bool Has(TrainCar loco) => false;
            public static string ModeOf(TrainCar loco) => null;
            public static bool SetMode(TrainCar loco, string mode) => false;
            public static bool SetCall(TrainCar loco, string call) => false;
        }

        public static class FiremanV1WithCall
        {
            public const int Version = 1;
            public static bool Has(TrainCar loco) => false;
            public static string ModeOf(TrainCar loco) => null;
            public static bool SetMode(TrainCar loco, string mode) => false;
            public static bool SetCall(TrainCar loco, string call) => false;
            public static string CallOf(TrainCar loco) => null;
        }

        public static class FiremanV2
        {
            public const int Version = 2;
            public static bool Has(TrainCar loco) => false;
            public static string ModeOf(TrainCar loco) => null;
            public static bool SetMode(TrainCar loco, string mode) => false;
            public static bool SetCall(TrainCar loco, string call) => false;
        }

        public static class FiremanNoVersion
        {
            public static bool Has(TrainCar loco) => false;
            public static string ModeOf(TrainCar loco) => null;
            public static bool SetMode(TrainCar loco, string mode) => false;
            public static bool SetCall(TrainCar loco, string call) => false;
        }

        public static class FiremanChanged
        {
            public const int Version = 1;
            public static bool Has(TrainCar loco) => false;
            public static string ModeOf(TrainCar loco) => null;
            public static bool SetMode(TrainCar loco, int mode) => false;
            public static bool SetCall(TrainCar loco, string call) => false;
        }

        private static void CheckGivenKey()
        {
            var key = EngineerTabletStartingItems.GivenKey;
            var prefabName = AccessTools.Field(typeof(InventoryItemSpec), "itemPrefabName");
            InventoryItemSpec Spec(string name)
            {
                var spec = (InventoryItemSpec)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(InventoryItemSpec));
                prefabName.SetValue(spec, name);
                return spec;
            }
            var kit = new List<StartingItems.StartingItem>
            {
                new StartingItems.StartingItem { item = Spec("Radio") },
                new StartingItems.StartingItem { item = Spec("shovel"), preferredRelativeSlot = 3 },
            };
            var tablet = Spec(EngineerTablet.PrefabName);
            var built = 0;
            InventoryItemSpec Create() { built++; return tablet; }

            Check(!CommsTabletLink.Merged, "without the Comms Tablet the engineer's tablet is not retired");
            var answer = EmitCommsTablet();
            answer.SetValue(null, true);
            Check(CommsTabletLink.Merged, "with a Comms Tablet that has bound the app, the link says merged");

            var fresh = new SaveGameData();
            var result = EngineerTabletStartingItems.WithTabletOnce(fresh, kit, Create);
            Check(ReferenceEquals(result, kit) && built == 0 && fresh.GetBool(key) == null,
                "merged, a new save: the kit as it is, no tablet built, the given key unset");

            var before = new SaveGameData();
            before.SetBool(key, true);
            result = EngineerTabletStartingItems.WithTabletOnce(before, kit, Create);
            Check(ReferenceEquals(result, kit) && built == 0 && before.GetBool(key) == null,
                "merged, a save given the tablet before the merge: the kit as it is, the given key removed");

            answer.SetValue(null, false);
            Check(!CommsTabletLink.Merged, "with the Comms Tablet gone (not merged), the link says so");
            result = EngineerTabletStartingItems.WithTabletOnce(before, kit, Create);
            var again = EngineerTabletStartingItems.WithTabletOnce(before, result, Create);
            Check(result.Count == kit.Count + 1 && result.Count(i => i.item == tablet) == 1 && built == 1 && before.GetBool(key) == true
                && kit.Count == 2 && ReferenceEquals(again, result),
                "then not merged: the engineer's tablet added once, the key set, the stock list untouched, and not again");
        }

        /// <summary>The Comms Tablet's GuestContract.Merged(id), in an assembly of its name: true for this mod's id while Answer is.</summary>
        private static FieldInfo EmitCommsTablet()
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("CommsTablet"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("CommsTablet");
            var type = module.DefineType("CommsTablet.GuestContract", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
            var answer = type.DefineField("Answer", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
            var merged = type.DefineMethod("Merged", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), new[] { typeof(string) });
            var il = merged.GetILGenerator();
            var no = il.DefineLabel();
            il.Emit(OpCodes.Ldsfld, answer);
            il.Emit(OpCodes.Brfalse_S, no);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, CommsTabletApp.Id);
            il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }));
            il.Emit(OpCodes.Ret);
            il.MarkLabel(no);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            return type.CreateType().GetField("Answer");
        }

        /// <summary>The fireman API is bound only at the version this mod knows, with each entry as that version has it; CallOf is optional.</summary>
        private static void CheckFiremanBinding()
        {
            Check(AIEngineer.Game.SteamCrew.Bindable(typeof(FiremanV1)) != null, "the fireman API binds at version 1, without CallOf");
            Check(AIEngineer.Game.SteamCrew.Bindable(typeof(FiremanV1WithCall)) != null
                && AIEngineer.Game.SteamCrew.Entry(typeof(FiremanV1WithCall), "CallOf", typeof(string)) != null
                && AIEngineer.Game.SteamCrew.Entry(typeof(FiremanV1), "CallOf", typeof(string)) == null, "and with CallOf, which is optional");
            Check(AIEngineer.Game.SteamCrew.Bindable(typeof(FiremanV2)) == null && AIEngineer.Game.SteamCrew.Bindable(typeof(FiremanNoVersion)) == null,
                "not at another version, or with none");
            Check(AIEngineer.Game.SteamCrew.Bindable(typeof(FiremanChanged)) == null, "not with an entry changed (SetMode taking a number)");
        }

        /// <summary>A real Realistic Firing build: its fireman API binds or not, and says its call or not.</summary>
        private static void CheckRealisticFiring(string path)
        {
            modDirs.Add(Path.GetDirectoryName(Path.GetFullPath(path)));
            try
            {
                var assembly = Assembly.LoadFrom(path);
                var api = assembly.GetType("RealisticFiring.FiremanApi", false);
                Console.WriteLine($"     {path}: {(api == null ? "no FiremanApi" : $"FiremanApi version {AIEngineer.Game.SteamCrew.VersionOf(api)?.ToString() ?? "none"}")}");
                if (api == null)
                {
                    Check(true, "Realistic Firing without the fireman API: the engineer won't call him (too old)");
                    return;
                }
                Check(AIEngineer.Game.SteamCrew.Bindable(api) != null, "Realistic Firing's fireman API binds");
                var readings = assembly.GetType("RealisticFiring.Api", false);
                Console.WriteLine($"     its readings: {(AIEngineer.Game.SteamCrew.ReadEntry(readings) != null ? "bound (whether the fire is lit, for a firing player)" : "not there (no warning of a fire gone out)")}");
                var callOf = AIEngineer.Game.SteamCrew.Entry(api, "CallOf", typeof(string)) != null;
                Console.WriteLine($"     its CallOf: {(callOf ? "bound (his call put back as it was)" : "not there (his call put back as none)")}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"     {e.GetType().Name}: {e.Message}");
                Check(false, "Realistic Firing's assembly read");
            }
        }

        private static string skinManagerDir;

        // The developer settings as 0.15.0 saved them, every one off its default.
        private static readonly string[] DeveloperFields =
            { "ShowDeveloper", "TickRate", "LogRoutes", "LogWhistle", "WhistleShortest", "WhistleShortestLong", "WhistleQuietEnds", "WhistleBounce", "WhistleDip", "WhistleVersion" };

        private const string OldSettings = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Settings xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <MaxSpeedKmh>61</MaxSpeedKmh>
  <YardSpeedKmh>12</YardSpeedKmh>
  <YardTrackCapKmh>30</YardTrackCapKmh>
  <CarGap>9</CarGap>
  <AutoStart>false</AutoStart>
  <TrainDeceleration>0.25</TrainDeceleration>
  <LightDeceleration>0.45</LightDeceleration>
  <SlowAirDeceleration>0.13</SlowAirDeceleration>
  <ShowNotifications>true</ShowNotifications>
  <ShowDeveloper>true</ShowDeveloper>
  <TickRate>7</TickRate>
  <LogRoutes>true</LogRoutes>
  <LogWhistle>false</LogWhistle>
  <WhistleShortest>0.3</WhistleShortest>
  <WhistleShortestLong>1.3</WhistleShortestLong>
  <WhistleQuietEnds>2.5</WhistleQuietEnds>
  <WhistleBounce>0.2</WhistleBounce>
  <WhistleDip>0.6</WhistleDip>
  <WhistleVersion>1</WhistleVersion>
  <KeysVersion>2</KeysVersion>
</Settings>";

        /// <summary>
        /// A Settings.xml as 0.15.0 saved it, developer fields and all, loads with the serializer Unity
        /// Mod Manager uses: the player's values kept, and in the public release the developer fields
        /// ignored (their fixed values in force) and not written back.
        /// </summary>
        private static void CheckSettingsFile()
        {
            Settings loaded = null;
            string written = null;
            try
            {
                var serializer = new XmlSerializer(typeof(Settings));
                using (var reader = new StringReader(OldSettings))
                    loaded = (Settings)serializer.Deserialize(reader);
                using (var writer = new StringWriter())
                {
                    serializer.Serialize(writer, loaded);
                    written = writer.ToString();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"     {e.GetType().Name}: {e.Message}");
            }
            Check(loaded != null && loaded.MaxSpeedKmh == 61 && loaded.CarGap == 9 && !loaded.AutoStart && Math.Abs(loaded.SlowAirDeceleration - 0.13f) < 1e-4,
                "a Settings.xml saved with the developer fields loads, the player's values kept");
#if PUBLIC_RELEASE
            Check(loaded != null && loaded.TickRate == 5 && !loaded.LogRoutes && loaded.LogWhistle && loaded.Whistle.ShortestLong == 1.5,
                "its developer fields ignored: the fixed tick rate, logs, and whistle timings in force");
            var left = written == null ? DeveloperFields : DeveloperFields.Where(f => written.Contains("<" + f + ">")).ToArray();
            Check(written != null && left.Length == 0, $"saved again without the developer fields{(left.Length > 0 ? ": " + string.Join(", ", left) : "")}");
#else
            Check(loaded != null && loaded.TickRate == 7 && loaded.LogRoutes && loaded.WhistleShortestLong == 1.3f,
                "its developer fields read (a development build)");
#endif
        }

#if PUBLIC_RELEASE
        private static void CheckNoDeveloperTools()
        {
            const BindingFlags AnyDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var assembly = typeof(Settings).Assembly;
            var saved = DeveloperFields.Where(f => typeof(Settings).GetField(f, BindingFlags.Public | BindingFlags.Instance) != null
                || typeof(Settings).GetProperty(f, BindingFlags.Public | BindingFlags.Instance)?.CanWrite == true).ToArray();
            Check(saved.Length == 0, $"no developer setting is a saved or drawn setting{(saved.Length > 0 ? ": " + string.Join(", ", saved) : "")}");
            var types = new[] { "AIEngineer.UI.WhistleMenu", "AIEngineer.Diagnostics.SelfTest", "AIEngineer.SettingsDrawer" }.Where(t => assembly.GetType(t, false) != null).ToArray();
            Check(types.Length == 0, $"no whistle menu, settings drawer, or self-test{(types.Length > 0 ? ": " + string.Join(", ", types) : "")}");

            var members = assembly.GetTypes()
                .SelectMany(t =>
                    t.GetMethods(AnyDeclared).Where(m => !m.IsSpecialName).Cast<MemberInfo>()
                        .Concat(t.GetFields(AnyDeclared))
                        .Concat(t.GetProperties(AnyDeclared)))
                .ToArray();

            var guardedMembers = new (string TypeName, string MemberName, MemberTypes Kind)[]
            {
                ("AIEngineer.Main", "LastNotified", MemberTypes.Field),
                ("AIEngineer.Host", "drawer", MemberTypes.Field),
                ("AIEngineer.Settings", "ShowDeveloper", MemberTypes.Field),
                ("AIEngineer.Settings", "DeveloperShown", MemberTypes.Property),
                ("AIEngineer.Settings", "MoveOldWhistle", MemberTypes.Method),
                ("AIEngineer.Settings", "WhistleShortest", MemberTypes.Field),
                ("AIEngineer.Settings", "WhistleShortestLong", MemberTypes.Field),
                ("AIEngineer.Settings", "WhistleQuietEnds", MemberTypes.Field),
                ("AIEngineer.Settings", "WhistleBounce", MemberTypes.Field),
                ("AIEngineer.Settings", "WhistleDip", MemberTypes.Field),
                ("AIEngineer.Settings", "WhistleVersion", MemberTypes.Field),
                ("AIEngineer.Game.BookletRenderer", "PaperReplaced", MemberTypes.Field),
                ("AIEngineer.Game.ConductorDesk", "LocoForTest", MemberTypes.Field),
                ("AIEngineer.Game.SteamCrew", "GlassAt", MemberTypes.Method),
                ("AIEngineer.Game.SteamCrew", "GlassReadForTest", MemberTypes.Property),
                ("AIEngineer.Game.SteamCrew", "FireLitForTest", MemberTypes.Property),
                ("AIEngineer.Held.EngineerTabletScreen", "PressSlotForTest", MemberTypes.Method),
                ("AIEngineer.Held.EngineerTabletScreen", "HitInPlace", MemberTypes.Method),
                ("AIEngineer.UI.Tablet.TabletMap", "OpenForTest", MemberTypes.Method),
                ("AIEngineer.UI.Tablet.TabletMap", "PickTrackForTest", MemberTypes.Method),
                ("AIEngineer.UI.Tablet.TabletMap", "DoForTest", MemberTypes.Method),
                ("AIEngineer.UI.Tablet.TabletMap", "PickForTest", MemberTypes.Method),
                ("AIEngineer.UI.Tablet.TabletPages", "FireButtons", MemberTypes.Field),
                ("AIEngineer.UI.Tablet.TabletPages", "OnButtonFor", MemberTypes.Field),
                ("AIEngineer.UI.Tablet.TabletPages", "OnButton", MemberTypes.Field),
                ("AIEngineer.UI.Tablet.TabletPages", "Cards", MemberTypes.Field),
                ("AIEngineer.UI.Tablet.TabletUi", "RefreshForTest", MemberTypes.Method),
                ("AIEngineer.UI.Tablet.TabletUi", "UndoForTest", MemberTypes.Method),
            };
            var guardedKeys = new HashSet<(string, string, MemberTypes)>(guardedMembers);
            foreach (var (typeName, memberName, kind) in guardedMembers)
            {
                var found = members
                    .Where(m => string.Equals(m.DeclaringType?.FullName, typeName, StringComparison.Ordinal)
                        && string.Equals(m.Name, memberName, StringComparison.Ordinal)
                        && m.MemberType == kind)
                    .Select(m => $"{m.DeclaringType.FullName}.{m.Name} ({m.MemberType})")
                    .Distinct()
                    .ToArray();
                Check(found.Length == 0, $"no guarded self-test member {typeName}.{memberName} ({kind}){(found.Length > 0 ? ": " + string.Join(", ", found) : "")}");
            }

            var otherForTest = members
                .Where(m => m.Name.EndsWith("ForTest", StringComparison.Ordinal)
                    && !guardedKeys.Contains((m.DeclaringType?.FullName, m.Name, m.MemberType)))
                .Select(m => $"{m.DeclaringType.FullName}.{m.Name} ({m.MemberType})")
                .Distinct()
                .ToArray();
            Check(otherForTest.Length == 0, $"no other *ForTest methods, fields, or properties{(otherForTest.Length > 0 ? ": " + string.Join(", ", otherForTest) : "")}");
        }
#endif

        /// <summary>A given mod's own references: beside it, then among the game's and the mod manager's.</summary>
        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var dv = Environment.GetEnvironmentVariable("DV_INSTALL_DIR") ?? "";
            foreach (var dir in modDirs.Concat(new[] { skinManagerDir, Path.Combine(dv, "DerailValley_Data", "Managed"), Path.Combine(dv, "DerailValley_Data", "Managed", "UnityModManager") }))
            {
                var file = dir == null ? null : Path.Combine(dir, name);
                if (file != null && File.Exists(file))
                    return Assembly.LoadFrom(file);
            }
            return null;
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok)
                failures++;
        }
    }
}
