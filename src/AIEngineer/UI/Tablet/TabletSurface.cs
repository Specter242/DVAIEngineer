using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AIEngineer.UI.Tablet
{
    /// <summary>Where a screen's pressable plates go: the tablet's own cab buttons, one a slot.</summary>
    internal interface ITabletButtons
    {
        int Count { get; }

        /// <summary>Puts slot <paramref name="index"/> over a plate (canvas pixels), taking presses or not (a faint plate).</summary>
        void Place(int index, Rect canvas, bool active);

        void HideFrom(int index);

        /// <summary>Where slot <paramref name="index"/> was last put.</summary>
        Rect RectOf(int index);
    }

    /// <summary>
    /// One screen's canvas, drawn by a pass of the tablet's pages. The pages
    /// draw as with an immediate-mode GUI (a plate returns true when it was
    /// pressed); the surface keeps the canvas's images and texts and the
    /// screen's buttons from pass to pass, reusing them in order, and hides
    /// what a pass didn't use. A press on a button is held until the next
    /// pass, where the plate in the place pressed reports it: plates keep
    /// their places, so a press finds its plate even if other plates came
    /// or went in between.
    /// </summary>
    internal sealed class TabletSurface
    {
        /// <summary>Thrown by a page after a press that changes what it shows: the pass starts over.</summary>
        internal sealed class Redraw : Exception
        {
        }

        public static TabletSurface Current { get; private set; }

        private readonly RectTransform root;
        private readonly ITabletButtons buttons;
        private readonly List<Image> images = new List<Image>();
        private readonly List<Text> texts = new List<Text>();
        private readonly List<RawImage> pictures = new List<RawImage>();
        private int imageUsed, textUsed, pictureUsed, buttonUsed, order;
        private Rect? pending;

        public TabletSurface(RectTransform root, ITabletButtons buttons)
        {
            this.root = root;
            this.buttons = buttons;
        }

        /// <summary>A press on button slot <paramref name="slot"/>, for the next pass: on the plate where that slot was put.</summary>
        public void Press(int slot) => pending = buttons.RectOf(slot);

        /// <summary>Draws the canvas with <paramref name="draw"/>; a page that changes what it shows mid-pass is drawn again.</summary>
        public void Run(Action draw)
        {
            TabletSkin.Build();
            Current = this;
            var begun = false;
            try
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    Begin();
                    begun = true;
                    try
                    {
                        draw();
                        return;
                    }
                    catch (Redraw)
                    {
                        pending = null;
                    }
                }
            }
            finally
            {
                Current = null;
                pending = null;
                if (begun)
                    End();
            }
        }

        private void Begin()
        {
            imageUsed = textUsed = pictureUsed = buttonUsed = order = 0;
        }

        private void End()
        {
            for (var i = imageUsed; i < images.Count; i++)
                if (images[i].enabled)
                    images[i].enabled = false;
            for (var i = textUsed; i < texts.Count; i++)
                if (texts[i].enabled)
                    texts[i].enabled = false;
            for (var i = pictureUsed; i < pictures.Count; i++)
                if (pictures[i].enabled)
                    pictures[i].enabled = false;
            buttons.HideFrom(buttonUsed);
        }

        private T Node<T>(List<T> pool, ref int used, string name) where T : Graphic
        {
            T g;
            if (used < pool.Count)
                g = pool[used];
            else
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.layer = root.gameObject.layer;
                var rt = (RectTransform)go.transform;
                rt.SetParent(root, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                g = go.AddComponent<T>();
                g.raycastTarget = false;
                pool.Add(g);
            }
            used++;
            if (!g.enabled)
                g.enabled = true;
            // Later draws lie over earlier ones.
            if (g.transform.GetSiblingIndex() != order)
                g.transform.SetSiblingIndex(order);
            order++;
            return g;
        }

        private static void Place(Graphic g, Rect r)
        {
            var rt = g.rectTransform;
            var pos = new Vector2(r.x, -r.y);
            if (rt.anchoredPosition != pos)
                rt.anchoredPosition = pos;
            if (rt.sizeDelta != r.size)
                rt.sizeDelta = r.size;
        }

        public void Fill(Rect r, Color c) => Image(r, TabletSkin.White, c);

        public void Lamp(Rect r, Color c) => Image(r, TabletSkin.Disc, c);

        public void Box(Rect r, Look look)
        {
            if (look?.Sprite != null)
                Image(r, look.Sprite, look.Tint);
        }

        private void Image(Rect r, Sprite sprite, Color c)
        {
            var image = Node(images, ref imageUsed, "box");
            if (image.sprite != sprite)
            {
                image.sprite = sprite;
                image.type = sprite.border != Vector4.zero ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            }
            if (image.color != c)
                image.color = c;
            Place(image, r);
        }

        public void Picture(Rect r, Texture texture)
        {
            if (texture == null)
                return;
            var raw = Node(pictures, ref pictureUsed, "picture");
            if (raw.texture != texture)
                raw.texture = texture;
            if (raw.color != Color.white)
                raw.color = Color.white;
            Place(raw, r);
        }

        public void Label(Rect r, string text, Look look, float alpha = 1)
        {
            if (string.IsNullOrEmpty(text) || look == null)
                return;
            var t = Node(texts, ref textUsed, "text");
            if (t.font != TabletSkin.Font)
            {
                t.font = TabletSkin.Font;
                t.supportRichText = true;
                t.resizeTextForBestFit = true;
                t.resizeTextMinSize = 12;
                t.verticalOverflow = VerticalWrapMode.Truncate;
                t.lineSpacing = 0.95f;
            }
            if (t.text != text)
                t.text = text;
            if (t.fontSize != look.Size)
                t.fontSize = look.Size;
            if (t.resizeTextMaxSize != look.Size)
                t.resizeTextMaxSize = look.Size;
            var style = look.Bold ? FontStyle.Bold : FontStyle.Normal;
            if (t.fontStyle != style)
                t.fontStyle = style;
            if (t.alignment != look.Align)
                t.alignment = look.Align;
            if (t.horizontalOverflow != HorizontalWrapMode.Wrap)
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
            var c = look.Text;
            c.a *= alpha;
            if (t.color != c)
                t.color = c;
            Place(t, new Rect(r.x + look.PadLeft, r.y, Math.Max(1, r.width - look.PadLeft - look.PadRight), r.height));
        }

        /// <summary>A plate: its box, its text, and a button over it. True when the button in its slot was pressed.</summary>
        public bool Button(Rect r, string text, Look look, bool enabled)
        {
            var faint = enabled ? 1f : 0.38f;
            if (look?.Sprite != null)
            {
                var tint = look.Tint;
                tint.a *= faint;
                Image(r, look.Sprite, tint);
            }
            Label(r, text, look, faint);
            // Every plate takes its slot, pressable or not, so a slot stays the same plate from pass to pass.
            if (buttonUsed >= buttons.Count)
                return false;
            var slot = buttonUsed++;
            buttons.Place(slot, r, enabled);
            if (pending != r)
                return false;
            pending = null;
            return enabled;
        }
    }
}
