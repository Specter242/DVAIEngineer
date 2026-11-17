using System;
using System.Collections.Generic;
using System.Globalization;
using AIEngineer.Core.Settings;
using UnityEngine;

namespace AIEngineer.UI
{
 internal static class WhistleMenu
    {
        private sealed class Row
        {
            public string Name;
            public string Label;
            public Func<Settings, float> Get;
            public Action<Settings, float> Set;
            public float Min, Max, Step;
        }

        private static readonly Row[] Rows =
        {
            new Row
            {
                Name = "aie-whistle-long", Label = "A long blast is at least (shorter is a short)",
                Get = s => s.WhistleShortestLong, Set = (s, v) => s.WhistleShortestLong = v, Min = 0.5f, Max = 5f, Step = 0.1f,
            },
            new Row
            {
                Name = "aie-whistle-end", Label = "Quiet after a blast that ends the signal",
                Get = s => s.WhistleQuietEnds, Set = (s, v) => s.WhistleQuietEnds = v, Min = 0.5f, Max = 6f, Step = 0.1f,
            },
            new Row
            {
                Name = "aie-whistle-bounce", Label = "Debounce: a dip shorter than this is the same blast",
                Get = s => s.WhistleBounce, Set = (s, v) => s.WhistleBounce = v, Min = 0f, Max = 1f, Step = 0.05f,
            },
            new Row
            {
                Name = "aie-whistle-blip", Label = "A blast shorter than this is a blip, not counted",
                Get = s => s.WhistleShortest, Set = (s, v) => s.WhistleShortest = v, Min = 0.1f, Max = 1.5f, Step = 0.05f,
            },
            new Row
            {
                Name = "aie-whistle-dip", Label = "A gap is the sound falling below this share of the blast's",
                Get = s => s.WhistleDip, Set = (s, v) => s.WhistleDip = v, Min = 0.1f, Max = 0.9f, Step = 0.05f,
            },
        };

        private static readonly Dictionary<string, string> typed = new Dictionary<string, string>();

        public static void Draw(Settings settings)
        {
            if (settings == null)
                return;
            GUILayout.Space(10);
            GUILayout.Label("Helper's whistle (seconds; type a value or use − and +):");
            var focused = GUI.GetNameOfFocusedControl();
            foreach (var row in Rows)
            {
                var value = row.Get(settings);
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Label, GUILayout.Width(430));
                if (focused != row.Name || !typed.ContainsKey(row.Name))
                    typed[row.Name] = value.ToString("0.###", CultureInfo.InvariantCulture);
                GUI.SetNextControlName(row.Name);
                var text = GUILayout.TextField(typed[row.Name], 6, GUILayout.Width(64));
                typed[row.Name] = text;
                if (TypedNumber.TryRead(text, row.Min, row.Max, out var v) && Math.Abs(v - value) > 1e-4f)
                    row.Set(settings, v);
                if (GUILayout.Button("−", GUILayout.Width(30)))
                    Step(settings, row, -row.Step);
                if (GUILayout.Button("+", GUILayout.Width(30)))
                    Step(settings, row, row.Step);
                GUILayout.Label($"  {row.Min:0.##} to {row.Max:0.##}", GUILayout.Width(110));
                GUILayout.EndHorizontal();
            }
            var t = settings.Whistle;
            GUILayout.Label($"Heard as: a blip under {t.Shortest:0.##} s, a short from {t.Shortest:0.##} s, a long from {t.ShortestLong:0.##} s; " +
                $"a signal ends {t.QuietEnds:0.##} s after its last blast.");
            if (GUILayout.Button("Whistle timings back to the defaults", GUILayout.Width(280)))
            {
                var fresh = new Settings();
                foreach (var row in Rows)
                    row.Set(settings, row.Get(fresh));
                typed.Clear();
                GUI.FocusControl(null);
            }
        }

        private static void Step(Settings settings, Row row, float by)
        {
            var v = Mathf.Clamp((float)Math.Round(row.Get(settings) + by, 3), row.Min, row.Max);
            row.Set(settings, v);
            typed.Remove(row.Name);
            GUI.FocusControl(null);
        }
    }
}
