using System.Linq;
using System.Reflection;
using HarmonyLib;
using LocoSim.Implementations;
using UnityEngine;

namespace AIEngineer.Game
{
    /// <summary>
    /// A loco's traction motors as the engineer watches them: how hot they
    /// are, and whether their power fuse is on. The game's motor set keeps both
    /// to itself, so they are reached by name (checked by the adapter check).
    /// </summary>
    internal sealed class TractionMotors
    {
        /// <summary>From this hot, °C, a fuse found off is taken to have blown from the heat.</summary>
        private const float BlewFromHeat = 100f;

        // Looked up softly: a game update that renames them costs this watch, not the engineer.
        internal static readonly FieldInfo TemperatureField = AccessTools.Field(typeof(TractionMotorSet), "tmTempReader");
        internal static readonly FieldInfo FuseField = AccessTools.Field(typeof(TractionMotorSet), "powerFuseRef");
        // The generator's limit: past it the game blows its fuse at once (a DE2 powering against locked wheels).
        internal static readonly FieldInfo MaxAmpsField = AccessTools.Field(typeof(TractionGenerator), "maxAmps");
        internal static readonly FieldInfo GeneratorAmpsField = AccessTools.Field(typeof(TractionGenerator), "totalAmpsReader");

        private readonly PortReference temperature;
        private readonly FuseReference fuse;
        private readonly PortReference amps;
        private readonly float maxAmps;

        private TractionMotors(PortReference temperature, FuseReference fuse, PortReference amps, float maxAmps)
        {
            this.temperature = temperature;
            this.fuse = fuse;
            this.amps = amps;
            this.maxAmps = maxAmps;
        }

        /// <summary>The motors of a loco that has them; null for any other.</summary>
        public static TractionMotors For(TrainCar loco)
        {
            var motors = loco?.SimController?.simFlow?.OrderedSimComps.OfType<TractionMotorSet>().FirstOrDefault();
            if (motors == null || TemperatureField == null || FuseField == null)
                return null;
            if (!(TemperatureField.GetValue(motors) is PortReference temperature && FuseField.GetValue(motors) is FuseReference fuse))
                return null;
            var generator = loco.SimController.simFlow.OrderedSimComps.OfType<TractionGenerator>().FirstOrDefault();
            var amps = generator != null ? GeneratorAmpsField?.GetValue(generator) as PortReference : null;
            var most = generator != null && MaxAmpsField?.GetValue(generator) is float m ? m : 0f;
            return new TractionMotors(temperature, fuse, amps, most);
        }

        /// <summary>Their temperature, °C.</summary>
        public float Temperature => temperature.Value;

        /// <summary>The current the generator gives them, as a share of what it may give before its fuse blows; 0 when unknown.</summary>
        public float AmpsShare => amps != null && maxAmps > 0 ? Mathf.Abs(amps.Value) / maxAmps : 0f;

        /// <summary>Whether their power fuse is off: blown, or switched off in the cab.</summary>
        public bool FuseOff => !fuse.State;

        /// <summary>What to tell the player when the fuse is found off.</summary>
        public string FuseNote => Temperature >= BlewFromHeat
            ? "its traction motors overheated and blew their fuse. The engineer has shut off and is holding the train. Let them cool and reset the fuse in the cab, and it will carry on"
            : "its traction motor fuse is off. The engineer has shut off and is holding the train. Turn the fuse back on in the cab, and it will carry on";

        /// <summary>The engineer's status while it waits for the fuse.</summary>
        public const string FuseStatus = "Traction motor fuse off: waiting for you to reset it";
    }
}
