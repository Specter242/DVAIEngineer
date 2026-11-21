using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIEngineer.Core.Driving;
using DV.HUD;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using HarmonyLib;
using LocoSim.Implementations;
using UnityEngine;

namespace AIEngineer.Game
{
    internal sealed class SteamCrew
    {
        private const float LookSeconds = 0.5f;
        private const float ShovelSeconds = 1.2f;
        // Realistic Firing's readings build a new snapshot each call: not more often than this.
        private const float ReadingsSeconds = 2f;

        /// <summary>The fireman API's version this mod is written for (its <c>FiremanApi.Version</c>).</summary>
        internal const int KnownApiVersion = 1;

        private static readonly Type ApiType = Type.GetType("RealisticFiring.FiremanApi, RealisticFiring", false);
        // The version Realistic Firing's API says it is; null if it has none, or no API.
        internal static readonly int? ApiVersion = VersionOf(ApiType);
        private static readonly MethodInfo ApiHas = Entry(ApiType, "Has", typeof(bool));
        private static readonly MethodInfo ApiModeOf = Entry(ApiType, "ModeOf", typeof(string));
        private static readonly MethodInfo ApiSetMode = Entry(ApiType, "SetMode", typeof(bool), typeof(string));
        private static readonly MethodInfo ApiSetCall = Entry(ApiType, "SetCall", typeof(bool), typeof(string));
        // Optional (1.24.0): his call, so the one he had before is put back.
        private static readonly MethodInfo ApiCallOf = Entry(ApiType, "CallOf", typeof(string));
        private static readonly Type Api = Bindable(ApiType);
        private static readonly bool RealisticFiringPresent = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "RealisticFiring");
        // Its readings (1.24.2), for whether its fire is lit. Optional.
        private static readonly MethodInfo ApiRead = ReadEntry(Type.GetType("RealisticFiring.Api, RealisticFiring", false));

        /// <summary>An API's <c>Version</c> constant; null if it has none.</summary>
        internal static int? VersionOf(Type api) =>
            api?.GetField("Version", BindingFlags.Public | BindingFlags.Static) is FieldInfo v && v.IsLiteral && v.FieldType == typeof(int) ? (int?)(int)v.GetRawConstantValue() : null;

        /// <summary>
        /// The API, if it may be bound: its version is the one this mod knows and every entry it
        /// needs is there as that version has it (CallOf optional); else null.
        /// </summary>
        internal static Type Bindable(Type api) =>
            api != null && VersionOf(api) == KnownApiVersion && Entry(api, "Has", typeof(bool)) != null && Entry(api, "ModeOf", typeof(string)) != null
            && Entry(api, "SetMode", typeof(bool), typeof(string)) != null && Entry(api, "SetCall", typeof(bool), typeof(string)) != null ? api : null;

        /// <summary>A public static entry taking the loco (and maybe a string), returning <paramref name="returns"/>; null if it isn't so.</summary>
        internal static MethodInfo Entry(Type api, string name, Type returns, Type second = null)
        {
            var parameters = second == null ? new[] { typeof(TrainCar) } : new[] { typeof(TrainCar), second };
            var m = api?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            return m != null && m.ReturnType == returns ? m : null;
        }

        /// <summary>The readings entry of Realistic Firing's <c>Api</c>, at the version this mod knows; null if it isn't so.</summary>
        internal static MethodInfo ReadEntry(Type api)
        {
            if (VersionOf(api) != KnownApiVersion)
                return null;
            var m = api.GetMethod("Read", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(TrainCar) }, null);
            return m != null && typeof(IDictionary<string, object>).IsAssignableFrom(m.ReturnType) ? m : null;
        }

        /// <summary>Whether the API is bound, and whether it can say his call (for the log and the adapter check).</summary>
        internal static bool ApiBound => Api != null;

        internal static bool CallReadable => Api != null && ApiCallOf != null;
        private static readonly FieldInfo SafetyValveField = AccessTools.Field(typeof(Boiler), "safetyValveOpeningPressure");
        private static readonly FieldInfo CrownSheetField = AccessTools.Field(typeof(Boiler), "crownSheetNormalizedWaterLevel");
        private static readonly AccessTools.FieldRef<PortReference, Port> ReferencedPort = AccessTools.FieldRefAccess<PortReference, Port>("port");

        // The span of each livery's gauge glass, learned from its cab when that is loaded.
        private static readonly Dictionary<string, GlassSpan> GlassSpans = new Dictionary<string, GlassSpan>();

        private readonly struct GlassSpan
        {
            private readonly float min, max, multiplier, offset;
            private readonly bool absolute, absoluteValue;

            public GlassSpan(Indicator glass, IndicatorPortReader reader)
            {
                min = glass.minValue;
                max = glass.maxValue;
                absolute = glass.absoluteNormalizedValue;
                multiplier = reader.valueMultiplier;
                offset = reader.valueOffset;
                absoluteValue = reader.useAbsoluteValue;
            }

            public float Show(float level)
            {
                var value = level * multiplier + offset;
                if (absoluteValue)
                    value = Mathf.Abs(value);
                return absolute ? Mathf.InverseLerp(0f, max, Mathf.Abs(value)) : Mathf.InverseLerp(min, max, value);
            }
        }

        /// <summary>Cylinders below this, °C, condense steam into water (the game's engine: under 100 °C and 100 rpm).</summary>
        private const float CylindersWarm = 100f;

        private readonly TrainCar loco;
        private readonly Boiler boiler;
        private readonly Firebox firebox;
        private readonly Port injector, blower, damper, cocks, intake;
        // The engine's own readouts: water in its cylinders (0 to 1), their temperature, and whether one has cracked.
        private readonly Port cylinderWater, cylinderHeat, cracked;
        private bool crackTold;
        private readonly float safetyValve;
        private readonly float crownSheet;
        private readonly SteamWatch watch = new SteamWatch();
        private GameObject glassSearched;
        private float nextReadings;
        private bool? readLit;
        private float readGlass = float.NaN;
        private bool engaged;
        private bool firemanCaptured;
        private string modeBefore;
        private string modeSet;
        // His call when the engineer took the loco (null if Realistic Firing can't say), and the one the engineer set.
        private string callBefore;
        private string callSet;
        private float nextLook, nextShovel, cocksUntil;
        private bool cocksOpen;

        private SteamCrew(TrainCar loco, Boiler boiler, Firebox firebox, SimulationFlow flow)
        {
            this.loco = loco;
            this.boiler = boiler;
            this.firebox = firebox;
            safetyValve = SafetyValveField != null ? (float)SafetyValveField.GetValue(boiler) : 14f;
            crownSheet = CrownSheetField != null ? (float)CrownSheetField.GetValue(boiler) : 0f;
            var engine = flow.OrderedSimComps.OfType<ReciprocatingSteamEngine>().FirstOrDefault();
            intake = engine?.intakeFlowNormalizedReadOut;
            cylinderWater = engine?.waterInCylindersNormalizedReadOut;
            cylinderHeat = engine?.cylinderTemperatureReadOut;
            cracked = engine?.isCylinderCrackedReadOut;
            foreach (var c in flow.OrderedSimComps.OfType<ExternalControl>())
            {
                var id = c.id.ToLowerInvariant();
                if (id.Contains("injector") && injector == null)
                    injector = c.controlExtIn;
                else if (id.Contains("blower") && blower == null)
                    blower = c.controlExtIn;
                else if (id.Contains("damper") && damper == null)
                    damper = c.controlExtIn;
                else if (id.Contains("cylindercock") || id.Contains("cylcock") || id == "cocks")
                    cocks = c.controlExtIn;
            }
            // The cocks the engine itself reads, whatever its control is called (a modded loco's too).
            var engineCocks = engine?.cylinderCockControl != null ? ReferencedPort(engine.cylinderCockControl) : null;
            if (engineCocks != null)
                cocks = engineCocks;
        }

        /// <summary>The crew for a steam loco (one with a boiler); null for any other.</summary>
        public static SteamCrew For(TrainCar loco)
        {
            var flow = loco?.SimController?.simFlow;
            var boiler = flow?.OrderedSimComps.OfType<Boiler>().FirstOrDefault();
            if (boiler == null)
                return null;
            return new SteamCrew(loco, boiler, flow.OrderedSimComps.OfType<Firebox>().FirstOrDefault(), flow);
        }

        /// <summary>Whether Realistic Firing fires this loco: its fireman then keeps the fire and the water.</summary>
        public bool Realistic => RealisticFiringPresent && Api != null && Invoke<bool>(ApiHas, loco);

        /// <summary>
        /// Whether the engineer fires it itself: stock steam, or a loco
        /// Realistic Firing says it doesn't fire. Never with a Realistic Firing
        /// too old to say: its own firebox may be the one burning.
        /// </summary>
        private bool FiresItself => !RealisticFiringPresent || (Api != null && !Realistic);

        /// <summary>What the player is told, once, when the engineer takes the loco.</summary>
        public string Note =>
            !RealisticFiringPresent || Api != null ? null
            : ApiType == null ? "Realistic Firing is installed but too old for the engineer to call its fireman: update it, or fire it yourself (F7)"
            : ApiVersion != KnownApiVersion ? $"Realistic Firing's fireman is version {(ApiVersion?.ToString() ?? "unknown")}, which this AI Engineer doesn't know (it knows {KnownApiVersion}): update AI Engineer, or fire it yourself (F7)"
            : "Realistic Firing's fireman can't be called as this AI Engineer expects: update both, or fire it yourself (F7)";

        /// <summary>
        /// Whether the player does the firing. Changed while the engineer has
        /// the loco, the fireman it called goes back as he was and the fire is
        /// the player's as it stands; or he is called on the next tick.
        /// </summary>
        public bool PlayerFires
        {
            get => watch.PlayerFires;
            set
            {
                if (value == watch.PlayerFires)
                    return;
                if (engaged)
                {
                    if (value)
                        StandDownFireman();
                    engaged = false;
                }
                watch.PlayerFires = value;
                watch.Reset();
                nextReadings = 0f;
            }
        }

        /// <summary>The fire and the water as they stand, for the log.</summary>
        public string Describe() =>
            $"{Pressure:F1} bar (valve {safetyValve:F1}), water {boiler.waterLevelReadOut.Value:F2}, " +
            (firebox != null ? $"coal {firebox.coalLevelNormalizedReadOut.Value:F2}, fire {(firebox.fireOnReadOut.Value > 0.5f ? "on" : "out")}, " : "") +
            $"steam to the cylinders {Working:F2}, injector {(injector != null ? injector.Value.ToString("F1") : "-")}, blower {(blower != null ? blower.Value.ToString("F1") : "-")}, cocks {(cocks == null ? "-" : cocksOpen ? "open" : "shut")}" +
            (cylinderWater != null ? $", cylinders {cylinderHeat?.Value ?? 0:F0} °C with water {cylinderWater.Value:F3}{(cracked != null && cracked.Value > 0 ? ", CRACKED" : "")}" : "") +
            (Realistic ? $", Realistic Firing's fireman {Invoke<string>(ApiModeOf, loco)}" : "") +
            (PlayerFires ? ", the player firing" : "");

        /// <summary>Steam going into the cylinders, 0 to 1 of the most they take: whether it works.</summary>
        public float Working => intake?.Value ?? 0f;

        /// <summary>Boiler pressure, bar.</summary>
        public float Pressure => boiler.pressureReadOut.Value;

        /// <summary>The pressure wanted before the train sets off, bar.</summary>
        private float StartPressure => Math.Max(4f, safetyValve * 0.45f);

        /// <summary>The boiler as it reads now, for <see cref="SteamWatch"/>.</summary>
        private BoilerReading Reading(float now)
        {
            // The fire and the glass matter only to a firing player; the crew sees to them itself.
            if (PlayerFires && now >= nextReadings)
            {
                nextReadings = now + ReadingsSeconds;
                ReadFireAndGlass();
            }
            return new BoilerReading { Pressure = Pressure, StartPressure = StartPressure, Glass = readGlass, FireLit = readLit };
        }

        private void ReadFireAndGlass()
        {
            readLit = firebox != null ? firebox.fireOnReadOut.Value > 0.5f : (bool?)null;
            if (Realistic && ApiRead != null && Invoke<IDictionary<string, object>>(ApiRead, loco) is IDictionary<string, object> all
                && all.TryGetValue("fire", out var f) && f is IDictionary<string, object> fire && fire.TryGetValue("lit", out var lit) && lit is bool on)
                readLit = on;
            // Not Realistic Firing's glass: until a cab has loaded it reads the bare boiler level, far above the crown sheet's.
            readGlass = GlassOf(boiler.waterLevelReadOut.Value);
        }

        /// <summary>The boiler's level as the cab's glass shows it, 0 to 1; before a cab of this livery has loaded, its level over the crown sheet.</summary>
        private float GlassOf(float level)
        {
            var livery = loco?.carLivery?.id;
            if (string.IsNullOrEmpty(livery))
                return Mathf.InverseLerp(crownSheet, 1f, level);
            if (!GlassSpans.ContainsKey(livery) && loco.loadedInterior != null && loco.loadedInterior != glassSearched)
            {
                glassSearched = loco.loadedInterior;
                var port = boiler.waterLevelReadOut.id;
                foreach (var reader in glassSearched.GetComponentsInChildren<IndicatorPortReader>(true))
                {
                    var glass = reader.portId == port ? reader.GetComponent<Indicator>() : null;
                    if (glass == null)
                        continue;
                    GlassSpans[livery] = new GlassSpan(glass, reader);
                    break;
                }
            }
            return GlassSpans.TryGetValue(livery, out var span) ? span.Show(level) : Mathf.InverseLerp(crownSheet, 1f, level);
        }

        /// <summary>Why the train must stand now: no fire, no steam, or (the player firing) no water in sight; null when it may go.</summary>
        public string Waiting(bool standing)
        {
            var lighting = firebox != null && firebox.fireOnReadOut.Value < 0.5f && FiresItself;
            return watch.Hold(Reading(Time.time), standing, lighting);
        }

#if !PUBLIC_RELEASE
        /// <summary>A boiler level as this loco's glass would show it (the self-test's).</summary>
        internal float GlassAt(float level) => GlassOf(level);

        internal float GlassReadForTest => readGlass;

        internal bool? FireLitForTest => readLit;
#endif

        /// <summary>The next thing to tell a player who is firing; null for nothing new.</summary>
        public string NextWarning(float now) => engaged ? watch.NextWarning(Reading(now)) : null;

        /// <summary>The engineer takes the loco: the fireman to work (Realistic Firing's, or the engineer's own firing), unless the player fires.</summary>
        public void Engage(bool road)
        {
            if (engaged)
                return;
            engaged = true;
            watch.Reset();
            nextReadings = 0f;
            if (PlayerFires)
                return;
            if (Realistic)
                SyncFiremanMode(road);
            if (damper != null && FiresItself)
                damper.ExternalValueUpdate(1f);
        }

        private void SyncFiremanMode(bool road)
        {
            var wanted = road ? "Road" : "Shunting";
            if (wanted == modeSet)
                return;
            var first = !firemanCaptured;
            var prevMode = first ? Invoke<string>(ApiModeOf, loco) : null;
            var prevCall = first && ApiCallOf != null ? Invoke<string>(ApiCallOf, loco) : null;
            if (!Invoke<bool>(ApiSetMode, loco, wanted))
                return;
            if (first)
            {
                modeBefore = prevMode;
                callBefore = prevCall;
                firemanCaptured = true;
            }
            modeSet = wanted;
            if (first)
                Main.Log($"{loco.ID}: Realistic Firing's fireman to {wanted} (was {modeBefore}, his call {callBefore ?? (ApiCallOf != null ? "none" : "unknown")})");
            else
                Main.Log($"{loco.ID}: Realistic Firing's fireman to {wanted}");
        }

        /// <summary>The fireman the engineer called goes back as he was, his call too.</summary>
        private void StandDownFireman()
        {
            if (Realistic && firemanCaptured)
            {
                if (callSet != null)
                    Invoke<bool>(ApiSetCall, loco, CallAsBefore);
                Invoke<bool>(ApiSetMode, loco, modeBefore ?? "Off");
            }
            firemanCaptured = false;
            modeBefore = null;
            modeSet = null;
            callSet = null;
            callBefore = null;
        }

        /// <summary>The engineer lets go: his mode back as it was, and his call if the engineer changed it (as it was if Realistic Firing could say, else none); the injector and blower off on stock steam; the cocks left open while there is water in the cylinders or they are cold, else shut. With the player firing, only the cocks: the fire and the water stay as the player has them.</summary>
        public void Release()
        {
            if (!engaged)
                return;
            engaged = false;
            if (!PlayerFires)
            {
                if (Realistic)
                {
                    StandDownFireman();
                }
                else if (FiresItself)
                {
                    injector?.ExternalValueUpdate(0f);
                    blower?.ExternalValueUpdate(0f);
                }
            }
            firemanCaptured = false;
            modeBefore = null;
            modeSet = null;
            callSet = null;
            callBefore = null;
            SetCocks(CylindersWet || CylindersCold);
        }

        /// <summary>
        /// A look at the fire and the water: <paramref name="regulator"/> as
        /// set, <paramref name="standing"/> whether the train stands, and
        /// <paramref name="stopAhead"/> meters to where it must stop.
        /// </summary>
        public void Tick(float now, float regulator, bool standing, double speed, double stopAhead, bool road)
        {
            if (!engaged)
                Engage(road);
            Cocks(now, regulator, standing, speed);
            if (PlayerFires || now < nextLook)
                return;
            nextLook = now + LookSeconds;
            if (Realistic)
            {
                SyncFiremanMode(road);
                if (!firemanCaptured)
                    return;
                // Ahead of a stop: let the fire burn down and fill the boiler. Otherwise his own call, as it was.
                var easing = !standing && stopAhead < 400;
                if (easing && callSet == null)
                {
                    if (Invoke<bool>(ApiSetCall, loco, "EasingOff"))
                        callSet = "EasingOff";
                }
                else if (!easing && callSet != null)
                {
                    if (Invoke<bool>(ApiSetCall, loco, CallAsBefore))
                        callSet = null;
                }
                return;
            }
            if (FiresItself)
                FireStock(now, regulator, standing);
        }

        private void FireStock(float now, float regulator, bool standing)
        {
            // Light it if it is out and there is coal on the grate to light.
            if (firebox != null && firebox.fireOnReadOut.Value < 0.5f && firebox.coalLevelNormalizedReadOut.Value > 0.05f)
                firebox.ignitionExtIn.ExternalValueUpdate(1f);
            var pressure = Pressure;
            var lifting = boiler.safetyValveReadOut.Value > 0.01f;
            // Coal: a thicker fire the harder it works; none while the safety valve lifts.
            var want = standing ? 0.35f : 0.45f + 0.4f * Mathf.Clamp01(regulator);
            if (firebox != null && !lifting && firebox.coalLevelNormalizedReadOut.Value < want && now >= nextShovel && pressure < safetyValve * 0.97f)
            {
                nextShovel = now + ShovelSeconds;
                Shovel();
            }
            // Water: in the glass, topped up harder when the valve lifts (it cools the boiler).
            var water = boiler.waterLevelReadOut.Value;
            if (injector != null)
            {
                var on = injector.Value > 0.5f;
                if (water < 0.7f || (lifting && water < 0.9f))
                    on = true;
                else if (water > 0.85f)
                    on = false;
                injector.ExternalValueUpdate(on ? 1f : 0f);
            }
            // Blower: draws the fire while standing (no exhaust to draw it), short of the valve.
            if (blower != null)
                blower.ExternalValueUpdate(standing && pressure < safetyValve * 0.9f ? 0.6f : 0f);
        }

        /// <summary>A shovelful from the loco's own pile or its tender's, as the game's one-key shoveling takes it.</summary>
        private void Shovel()
        {
            var fire = loco.SimController?.firebox;
            if (fire == null)
                return;
            var pile = Pile(loco);
            if (pile == null && loco.rearCoupler != null && loco.rearCoupler.coupledTo != null)
                pile = Pile(loco.rearCoupler.coupledTo.train);
            pile?.TransferToFirebox(fire, 2);
        }

        private static CoalPileSimController Pile(TrainCar car) => car != null ? car.GetComponentInChildren<CoalPileSimController>() : null;

        /// <summary>Whether the cylinder cocks stand open.</summary>
        public bool CocksOpen => cocksOpen;

        /// <summary>Water in the cylinders, 0 to 1, as the engine reads it; 0 without its readout.</summary>
        public float CylinderWater => cylinderWater?.Value ?? 0f;

        private bool CylindersWet => cylinderWater != null && cylinderWater.Value > 0.002f;

        private bool CylindersCold => cylinderHeat != null && cylinderHeat.Value < CylindersWarm;

        private void Cocks(float now, float regulator, bool standing, double speed)
        {
            var steamOn = regulator > 0.02f;
            if (steamOn && !lastSteamOn)
                cocksUntil = now + (cylinderWater != null ? (standing ? 3f : 0f) : 6f);
            lastSteamOn = steamOn;
            bool open;
            if (cylinderWater != null)
            {
                // Any water at all: the game counts chuffs while coasting too, regulator shut.
                open = CylindersWet || (steamOn && (CylindersCold || now < cocksUntil));
            }
            else
                open = steamOn && now < cocksUntil;
            SetCocks(open);
            if (cracked != null && cracked.Value > 0 && !crackTold)
            {
                crackTold = true;
                Main.Notify($"AI engineer: {loco.ID} has a cracked cylinder: it leaks steam until it is repaired.");
                Main.Warn($"{loco.ID}: cylinder cracked; {Describe()}");
            }
        }

        private bool lastSteamOn;
        private InteriorControlsManager cab;

        /// <summary>The cocks to <paramref name="open"/>: the engine's port, and the cab's lever when the cab is loaded so the player sees it; set again whenever either has been moved.</summary>
        private void SetCocks(bool open)
        {
            if (cocks == null)
                return;
            var value = open ? 1f : 0f;
            if (cocksOpen != open || Math.Abs(cocks.Value - value) > 0.01f)
                cocks.ExternalValueUpdate(value);
            cocksOpen = open;
            if (cab == null && loco.loadedInterior != null)
                cab = loco.loadedInterior.GetComponent<InteriorControlsManager>();
            if (cab != null && cab.TryGetControl(InteriorControlsManager.ControlType.CylCock, out var lever)
                && lever.controlImplBase != null && Math.Abs(lever.controlImplBase.Value - value) > 0.01f)
                lever.controlImplBase.SetValue(value);
        }

        /// <summary>His call before the engineer's: as Realistic Firing said it was, or none if it can't say.</summary>
        private string CallAsBefore => string.IsNullOrEmpty(callBefore) ? "None" : callBefore;

        private static T Invoke<T>(MethodInfo m, params object[] args)
        {
            try
            {
                return m != null ? (T)m.Invoke(null, args) : default;
            }
            catch (Exception e)
            {
                Main.Warn($"Realistic Firing's fireman couldn't be called ({m?.Name}): {e.GetBaseException().Message}");
                return default;
            }
        }
    }
}
