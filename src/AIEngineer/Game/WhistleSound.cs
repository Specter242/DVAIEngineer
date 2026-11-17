using System.Collections.Generic;
using System.Linq;
using DV.Simulation.Controllers;
using DV.Simulation.Ports;
using HarmonyLib;
using LocoSim.Implementations;
using UnityEngine;

namespace AIEngineer.Game
{
 internal sealed class WhistleSound
    {
        private static readonly AccessTools.FieldRef<LayeredAudioPortReader, Port> ReaderPort =
            AccessTools.FieldRefAccess<LayeredAudioPortReader, Port>("port");
        private static readonly AccessTools.FieldRef<LayeredAudioPortReader, LayeredAudio> ReaderAudio =
            AccessTools.FieldRefAccess<LayeredAudioPortReader, LayeredAudio>("layeredAudio");
        private static readonly AccessTools.FieldRef<OverridableBaseControl, Port> ControlPort =
            AccessTools.FieldRefAccess<OverridableBaseControl, Port>("controlPort");

        private readonly HornControl cord;
        // What drives the sound, most direct first: the steam through the whistle, the horn's output, the cord.
        private readonly List<Port> drivers = new List<Port>();
        private LayeredAudioPortReader reader;
        private Port heardFrom;
        private LayeredAudio audio;
        private float loudest = 1f;
        private float nextLook;
        private int looks;

        public WhistleSound(TrainCar car)
        {
            cord = car.SimController?.controlsOverrider?.Horn;
            var flow = car.SimController?.simFlow;
            if (flow != null)
            {
                var ports = flow.OrderedSimComps.SelectMany(c => c.GetAllPorts()).ToList();
                var whistle = ports.FirstOrDefault(p => p.id.EndsWith(".WHISTLE_FLOW_NORMALIZED"));
                if (whistle != null)
                    drivers.Add(whistle);
                var horn = flow.OrderedSimComps.OfType<Horn>().FirstOrDefault()?.hornReadOut;
                if (horn != null)
                    drivers.Add(horn);
            }
            var cordPort = cord != null ? ControlPort(cord) : null;
            if (cordPort != null)
                drivers.Add(cordPort);
        }

        /// <summary>Whether there is anything to hear: a whistle's sound, its steam, or its cord.</summary>
        public bool Usable => drivers.Count > 0 || cord != null;

        /// <summary>What the loudness is read from, for the log.</summary>
        public string Source =>
            audio != null ? $"its sound ({audio.layers?.Length ?? 0} layers, driven by {heardFrom?.id})"
            : drivers.Count > 0 && drivers[0] != (cord != null ? ControlPort(cord) : null) ? $"{drivers[0].id} (its sound not found)"
            : "its cord (no whistle sound or steam found)";

        /// <summary>How loud it sounds now, 0 to 1.</summary>
        public float Level(float now)
        {
            if (audio != null && (reader == null || audio == null || ReaderPort(reader) != heardFrom))
                audio = null;
            if (audio == null && now >= nextLook)
            {
                nextLook = now + (looks++ < 5 ? 2f : 15f);
                Find();
            }
            if (audio != null)
            {
                var sum = 0f;
                var layers = audio.layers;
                for (var i = 0; i < layers.Length; i++)
                    if (layers[i]?.source != null)
                        sum += layers[i].source.volume;
                return Mathf.Clamp01(sum / loudest);
            }
            if (drivers.Count > 0 && (cord == null || drivers[0] != ControlPort(cord)))
                return Mathf.Clamp01(Mathf.Abs(drivers[0].Value));
            return Helper.Sounding(cord);
        }

        private void Find()
        {
            if (drivers.Count == 0)
                return;
            LayeredAudioPortReader best = null;
            var rank = int.MaxValue;
            foreach (var r in Object.FindObjectsOfType<LayeredAudioPortReader>())
            {
                if (r == null || r.updateType == LayeredAudioPortReader.UpdateType.SET_PITCH)
                    continue;
                var i = drivers.IndexOf(ReaderPort(r));
                if (i >= 0 && i < rank && ReaderAudio(r) != null)
                {
                    best = r;
                    rank = i;
                }
            }
            if (best == null)
                return;
            reader = best;
            heardFrom = drivers[rank];
            audio = ReaderAudio(best);
            loudest = 0f;
            foreach (var layer in audio.layers ?? new LayeredAudio.Layer[0])
            {
                var keys = layer?.volumeCurve?.keys;
                if (keys != null && keys.Length > 0)
                    loudest += keys.Max(k => k.value);
            }
            loudest = Mathf.Max(0.01f, loudest);
            Main.Log($"whistle: heard from {Source}, loudest {loudest:F2}");
        }
    }
}
