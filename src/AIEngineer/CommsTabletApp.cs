using System;
using System.Collections.Generic;
using AIEngineer.Game;
using AIEngineer.UI.Tablet;
using UnityEngine;

namespace AIEngineer
{
 public static class CommsTabletApp
    {
        public const int Contract = 1;
        public const string Id = "ai-engineer";
        public const string Name = "Engineers";

        /// <summary>The engineer's own tablet, retired while merged.</summary>
        public const string Tablet = global::AIEngineer.Held.EngineerTablet.PrefabName;

        /// <summary>As the engineer's tablet's screen: drawn again twice a second while it is seen.</summary>
        private const float RefreshSeconds = 0.5f;

        private static readonly Dictionary<int, View> views = new Dictionary<int, View>();
        private static int next;
        private static Texture2D icon;
        private static float nextComplaint;

        // One pass of the pages; made once, so a draw allocates no closure.
        private static readonly Action drawPages = () => TabletUi.Draw(Host.World);

        /// <summary>Errors caught in the app's calls this session (the self-tests check none).</summary>
        internal static int Faults { get; private set; }

        /// <summary>The views open now (the self-tests').</summary>
        internal static int OpenViews => views.Count;

        /// <summary>
        /// One view: the Comms Tablet's slots in the place of the screen's
        /// buttons. A slot keeps the plate it was last put on, pressable or
        /// not, so a press finds its plate (<see cref="TabletSurface.Press"/>).
        /// </summary>
        private sealed class View : ITabletButtons
        {
            internal TabletSurface Surface;
            internal Action Invalidate;
            internal Action<int, Rect, bool> PlaceSlot;
            internal Action<int> HideSlotsFrom;
            internal Rect[] Placed;
            internal bool Shown, Held, Vr;
            internal float RefreshAt;

            public int Count => Placed.Length;

            public void Place(int index, Rect canvas, bool active)
            {
                Placed[index] = canvas;
                PlaceSlot(index, canvas, active);
            }

            public void HideFrom(int index) => HideSlotsFrom(index);

            public Rect RectOf(int index) => index >= 0 && index < Placed.Length ? Placed[index] : default;
        }

        /// <summary>A man in an engineer's cap, white on clear, in the Comms Tablet's filled style; made once.</summary>
        public static Texture2D Icon()
        {
            if (icon == null)
                icon = EngineerGlyph(192);
            return icon;
        }

        /// <summary>Offered wherever this game may run engineers: alone, or as a Multiplayer session's host.</summary>
        public static bool Available() => MultiplayerGate.MayDrive;

        public static int Open(RectTransform region, GameObject tablet, Action invalidate, Action<int, Rect, bool> placeSlot,
            Action<int> hideSlotsFrom, int slotCount, bool vr)
        {
            var view = new View
            {
                Invalidate = invalidate,
                PlaceSlot = placeSlot,
                HideSlotsFrom = hideSlotsFrom,
                Placed = new Rect[Math.Max(0, slotCount)],
                Vr = vr,
            };
            // Everything the surface makes goes under the region, on its layer (the tablet's screen layer).
            view.Surface = new TabletSurface(region, view);
            var id = ++next;
            views[id] = view;
            return id;
        }

        /// <summary>The region and all under it are destroyed by the Comms Tablet right after.</summary>
        public static void Close(int view) => views.Remove(view);

        public static void Shown(int view, bool shown)
        {
            if (!views.TryGetValue(view, out var v))
                return;
            v.Shown = shown;
            // Shown, it is drawn at once; the next refresh after that.
            v.RefreshAt = Time.unscaledTime + RefreshSeconds;
        }

        public static void Held(int view, bool held)
        {
            if (views.TryGetValue(view, out var v))
                v.Held = held;
        }

        /// <summary>While shown and seen: drawn again twice a second, as the engineer's tablet is (lists, lamps, and the map's trains move).</summary>
        public static void Update(int view)
        {
            if (!views.TryGetValue(view, out var v))
                return;
            var now = Time.unscaledTime;
            if (now < v.RefreshAt)
                return;
            v.RefreshAt = now + RefreshSeconds;
            v.Invalidate();
        }

        /// <summary>The pages, drawn as on the engineer's tablet; a press since the last draw is handled in this pass.</summary>
        public static void Draw(int view)
        {
            if (!views.TryGetValue(view, out var v))
                return;
            try
            {
                v.Surface.Run(drawPages);
            }
            catch (Exception e)
            {
                // As on the engineer's own tablet, a failed pass leaves the screen as it was and the next is tried.
                Complain("drawing its pages", e);
            }
        }

        /// <summary>A plate's slot pressed: held for the next draw, which the Comms Tablet makes at once.</summary>
        public static void Pressed(int view, int slot)
        {
            if (views.TryGetValue(view, out var v))
                v.Surface.Press(slot);
        }

        /// <summary>The map's part no plate covers, where a press is a point (as the engineer's tablet's map button); none off the map.</summary>
        public static Rect PressArea(int view) =>
            views.ContainsKey(view) && TabletUi.Page == TabletTab.Map && Host.World != null ? TabletMap.PressArea : Rect.zero;

        public static void PressAt(int view, Vector2 canvas)
        {
            if (!views.ContainsKey(view))
                return;
            try
            {
                TabletMap.PressAt(canvas);
            }
            catch (Exception e)
            {
                Complain("handling a press on its map", e);
            }
        }

        private static void Complain(string what, Exception e)
        {
            Faults++;
            if (Time.unscaledTime < nextComplaint)
                return;
            nextComplaint = Time.unscaledTime + 30f;
            Main.Error($"The Comms Tablet's Engineers app failed {what}", e);
        }

        // ---- The icon ----------------------------------------------------------
        // A few shapes in a unit square, y up, drawn by their distance fields,
        // as the Comms Tablet draws its own apps' symbols: a bust, a head, and
        // a railroad cap (a puffed crown and a short visor).

        private static Texture2D EngineerGlyph(int size)
        {
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    var d = Mathf.Max(Disc(u, v, 0.5f, 0.02f, 0.32f), 0.13f - v);
                    d = Mathf.Min(d, Disc(u, v, 0.5f, 0.48f, 0.12f));
                    d = Mathf.Min(d, RoundBox(u, v, 0.5f, 0.72f, 0.15f, 0.05f, 0.02f));
                    d = Mathf.Min(d, Ellipse(u, v, 0.5f, 0.77f, 0.19f, 0.06f));
                    d = Mathf.Min(d, Ellipse(u, v, 0.5f, 0.655f, 0.16f, 0.03f));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d * size) * 255f));
                }
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "AIEngineer_CommsTabletIcon",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        private static float Disc(float x, float y, float cx, float cy, float r) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

        private static float RoundBox(float x, float y, float cx, float cy, float halfWidth, float halfHeight, float radius)
        {
            float qx = Mathf.Abs(x - cx) - halfWidth + radius, qy = Mathf.Abs(y - cy) - halfHeight + radius;
            return new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        /// <summary>An ellipse's distance, near enough at its edge for the edge's smoothing.</summary>
        private static float Ellipse(float x, float y, float cx, float cy, float rx, float ry)
        {
            float px = x - cx, py = y - cy;
            var k0 = Mathf.Sqrt(px * px / (rx * rx) + py * py / (ry * ry));
            var k1 = Mathf.Sqrt(px * px / (rx * rx * rx * rx) + py * py / (ry * ry * ry * ry));
            return k1 > 0f ? k0 * (k0 - 1f) / k1 : -Mathf.Min(rx, ry);
        }
    }
}
