using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIEngineer.UI.Tablet
{
    /// <summary>How a plate, a box, or a line of text looks: its nine-sliced background, and its text.</summary>
    internal sealed class Look
    {
        public Sprite Sprite;
        public Color Tint = Color.white;
        public Color Text;
        public int Size;
        public bool Bold;
        public TextAnchor Align = TextAnchor.MiddleCenter;
        public bool Wrap;
        public float PadLeft = 4, PadRight = 4;

        public Look With(TextAnchor align, bool? bold = null)
        {
            var l = (Look)MemberwiseClone();
            l.Align = align;
            if (bold.HasValue)
                l.Bold = bold.Value;
            return l;
        }

        public Look Tinted(Color text)
        {
            var l = (Look)MemberwiseClone();
            l.Text = text;
            return l;
        }
    }

    internal static class TabletSkin
    {
        public const float W = 1080, H = 924;

        public static readonly Color Cream = Hex("efe6cf"), Soft = Hex("b8ab92"), Dim = Hex("8f8574"), Brass = Hex("c9a24a"), Brass2 = Hex("e0bf6a");
        public static readonly Color Ok = Hex("9fd36a"), Wait = Hex("f4c34c"), Alarm = Hex("ff7b5c"), Edge = Hex("4b3a29");
        public static readonly Color Ink = Hex("2a241c"), Paper = Hex("ece3cc");
        public static readonly Color FreightHaul = Hex("3f72b8"), LogisticHaul = Hex("5e8f3a"), ShuntLoad = Hex("c7792a"), ShuntUnload = Hex("a8473b");

        public static Look Verb, Plate, PlateOn, Here, Primary, Tab, TabOn, Chip, ChipOn, Text, Small, SoftText, Brass2Text, Section, Big, BillText, BillSmall, Card, CardOn, Well, Strip, Panel, ScreenBg, Empty, Invisible;

        public static Sprite White, Disc;
        public static Font Font;

        private static bool built;

        public static void Build()
        {
            if (built)
                return;
            built = true;
            Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            White = Sliced(Solid(Color.white), 0);
            Disc = Sliced(DiscTexture(64), 0);
            var plate = Sliced(Rounded(Hex("3a332b"), Hex("2a251f"), Hex("5a4a33"), 6), 10);
            var on = Sliced(Rounded(Hex("57462b"), Hex("3d311f"), Brass2, 6), 10);
            var here = Sliced(Rounded(Hex("2a2116"), Hex("2a2116"), Brass2, 6, Brass), 10);
            var primary = Sliced(Rounded(Hex("d9b45a"), Hex("b38b37"), Hex("e7c977"), 6), 10);
            var chip = Sliced(Rounded(Hex("14110e"), Hex("14110e"), Edge, 6), 10);
            var chipOn = Sliced(Rounded(Hex("2a2116"), Hex("2a2116"), Brass2, 6, Brass), 10);
            var card = Sliced(Rounded(Hex("14110e"), Hex("14110e"), Edge, 8), 10);
            var cardOn = Sliced(Rounded(Hex("14110e"), Hex("14110e"), Brass, 8), 10);
            var well = Sliced(Rounded(Hex("0b0a09"), Hex("0b0a09"), Hex("3a3027"), 6), 10);
            var panel = Sliced(Rounded(Hex("100e0c"), Hex("100e0c"), Hex("5a4a33"), 6), 10);
            var screen = Sliced(Rounded(Hex("171412"), Hex("100e0c"), Hex("4d4130"), 5), 10);

            Look Button(Sprite bg, Color text, int size, bool bold = false) => new Look { Sprite = bg, Text = text, Size = size, Bold = bold, Align = TextAnchor.MiddleCenter };
            Plate = Button(plate, Cream, 27);
            Verb = Button(plate, Cream, 27);
            Verb.Wrap = true;
            Verb.PadLeft = Verb.PadRight = 8;
            PlateOn = Button(on, Brass2, 27, true);
            Here = Button(here, Brass2, 27, true);
            Primary = Button(primary, Hex("1d160c"), 27, true);
            Tab = Button(plate, Cream, 27);
            TabOn = Button(on, Brass2, 27, true);
            Chip = Button(chip, Cream, 26);
            Chip.Align = TextAnchor.MiddleLeft;
            Chip.PadLeft = 40;
            ChipOn = Button(chipOn, Cream, 26);
            ChipOn.Align = TextAnchor.MiddleLeft;
            ChipOn.PadLeft = 40;

            Look Label(Color c, int size, bool bold = false) => new Look { Text = c, Size = size, Bold = bold, Align = TextAnchor.MiddleLeft, PadLeft = 0, PadRight = 0 };
            Text = Label(Cream, 28);
            Small = Label(Cream, 25);
            SoftText = Label(Soft, 26);
            Brass2Text = Label(Brass2, 26);
            Section = Label(Soft, 26, true);
            Big = Label(Cream, 32, true);
            BillText = Label(Ink, 28, true);
            BillSmall = Label(Hex("5d5240"), 25);
            Empty = Label(Soft, 26);
            Empty.Wrap = true;
            Empty.Align = TextAnchor.UpperLeft;

            Look Box(Sprite bg) => new Look { Sprite = bg };
            Card = Box(card);
            CardOn = Box(cardOn);
            Well = Box(well);
            Strip = Box(well);
            Panel = Box(panel);
            ScreenBg = Box(screen);
            Invisible = new Look();
        }

        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            return c;
        }

        private static Sprite Sliced(Texture2D t, int border)
        {
            var s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[16];
            for (var i = 0; i < 16; i++)
                px[i] = c;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        /// <summary>A rounded plate, 32 pixels, its fill shaded top to bottom, a one-pixel edge, and (lit plates) a brass bar down its left.</summary>
        private static Texture2D Rounded(Color top, Color bottom, Color edge, int radius, Color? bar = null)
        {
            const int n = 32;
            radius = Math.Min(radius, 14);
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[n * n];
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, n - radius), cy = Mathf.Clamp(y + 0.5f, radius, n - radius);
                    var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy)) - radius;
                    var inside = Mathf.Clamp01(0.5f - d);
                    var fill = Color.Lerp(bottom, top, y / (float)(n - 1));
                    if (bar.HasValue && x < 5)
                        fill = bar.Value;
                    var c = d > -1.5f ? edge : fill;
                    c.a *= inside;
                    px[y * n + x] = c;
                }
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D DiscTexture(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var d = Mathf.Sqrt((x + 0.5f - n / 2f) * (x + 0.5f - n / 2f) + (y + 0.5f - n / 2f) * (y + 0.5f - n / 2f));
                    px[y * n + x] = new Color(1, 1, 1, Mathf.Clamp01(n / 2f - d));
                }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // ---- Drawing, onto the screen being drawn; all in canvas pixels ----

        public static void Fill(Rect r, Color c) => TabletSurface.Current?.Fill(r, c);

        public static void Lamp(float x, float y, float size, Color c) => TabletSurface.Current?.Lamp(new Rect(x, y, size, size), c);

        public static void Box(Rect r, Look look) => TabletSurface.Current?.Box(r, look);

        public static void Label(Rect r, string text, Look look) => TabletSurface.Current?.Label(r, text, look);

        public static void Picture(Rect r, Texture texture) => TabletSurface.Current?.Picture(r, texture);

        /// <summary>A plate: true when it was pressed. A disabled plate is drawn faint and takes no press.</summary>
        public static bool Button(Rect r, string text, Look look, bool enabled = true) => TabletSurface.Current != null && TabletSurface.Current.Button(r, text, look, enabled);

        /// <summary>After a press that changes what the page shows: the pass starts again on what it is now.</summary>
        public static void Changed() => throw new TabletSurface.Redraw();

        /// <summary>Two lines on a plate: a bold first, a smaller soft second.</summary>
        public static string Two(string top, string bottom) => $"<b>{top}</b>\n<size=22><color=#{ColorUtility.ToHtmlStringRGB(Soft)}>{bottom}</color></size>";

        public static string Tint(string text, Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text}</color>";

        private static readonly Dictionary<Look, Look> centered = new Dictionary<Look, Look>();

        /// <summary>A look's text centred and bold, for numbers by a mark.</summary>
        public static Look Centered(Look look)
        {
            if (!centered.TryGetValue(look, out var c))
                centered[look] = c = look.With(TextAnchor.MiddleCenter, true);
            return c;
        }

        /// <summary>The stripe color of a job kind, as its booklet's.</summary>
        public static Color KindColor(string kind)
        {
            switch (kind)
            {
                case "Freight haul":
                    return FreightHaul;
                case "Logistical haul":
                    return LogisticHaul;
                case "Shunting load":
                    return ShuntLoad;
                case "Shunting unload":
                    return ShuntUnload;
                default:
                    return Dim;
            }
        }
    }
}
