using HarmonyLib;
using UnityEngine;

namespace AIEngineer.Held
{
    /// <summary>The shell and inventory art: Realistic Firing's FiringTabletBody (d0f162e9), a little lighter, with a map for an icon.</summary>
    internal static class EngineerTabletBody
    {
        private static Material material;
        private static Mesh mesh;
        private static Sprite icon, droppedIcon;

        internal static Transform Build(Transform parent)
        {
            var body = new GameObject("[engineer tablet shell]");
            body.transform.SetParent(parent, false);
            body.AddComponent<MeshFilter>().sharedMesh = Mesh;
            body.AddComponent<MeshRenderer>().sharedMaterial = Material;
            return body.transform;
        }

        private static Material Material
        {
            get
            {
                if (material != null)
                    return material;
                material = new Material(Shader.Find("Standard")) { name = "AIEngineer_TabletShell", color = new Color(0.16f, 0.13f, 0.09f) };
                material.SetFloat("_Metallic", 0.45f);
                material.SetFloat("_Glossiness", 0.25f);
                return material;
            }
        }

        private static Mesh Mesh
        {
            get
            {
                if (mesh != null)
                    return mesh;
                const int corners = 8;
                var halfDepth = EngineerTablet.Thickness * 0.5f;
                const float bevelDepth = 0.002f;
                var vertices = new Vector3[corners * 4 + 2];
                var triangles = new System.Collections.Generic.List<int>();
                for (var ring = 0; ring < 4; ring++)
                {
                    var inset = ring == 0 || ring == 3 ? 0.003f : 0f;
                    var w = EngineerTablet.Size.x * 0.5f - inset;
                    var h = EngineerTablet.Size.y * 0.5f - inset;
                    var z = ring == 0 ? -halfDepth : ring == 1 ? -halfDepth + bevelDepth : ring == 2 ? halfDepth - bevelDepth : halfDepth;
                    const float cut = 0.012f;
                    var outline = new[] { new Vector2(-w + cut, -h), new Vector2(w - cut, -h), new Vector2(w, -h + cut), new Vector2(w, h - cut),
                        new Vector2(w - cut, h), new Vector2(-w + cut, h), new Vector2(-w, h - cut), new Vector2(-w, -h + cut) };
                    for (var i = 0; i < corners; i++)
                    {
                        vertices[ring * corners + i] = new Vector3(outline[i].x, outline[i].y, z);
                        if (ring == 0)
                            continue;
                        var a = (ring - 1) * corners + i;
                        var b = (ring - 1) * corners + (i + 1) % corners;
                        var c = ring * corners + i;
                        var d = ring * corners + (i + 1) % corners;
                        triangles.AddRange(new[] { a, b, c, b, d, c });
                    }
                }
                vertices[32] = new Vector3(0f, 0f, -halfDepth);
                vertices[33] = new Vector3(0f, 0f, halfDepth);
                for (var i = 0; i < corners; i++)
                {
                    triangles.AddRange(new[] { 32, (i + 1) % corners, i, 33, 24 + i, 24 + (i + 1) % corners });
                }
                mesh = new Mesh { name = "AIEngineer_TabletShell" };
                mesh.vertices = vertices;
                mesh.triangles = triangles.ToArray();
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                mesh.UploadMeshData(true);
                return mesh;
            }
        }

        internal static void SetIcons(InventoryItemSpec spec)
        {
            if (icon == null)
                icon = MakeIcon(false);
            if (droppedIcon == null)
                droppedIcon = MakeIcon(true);
            AccessTools.Field(typeof(InventoryItemSpec), "itemIconSprite").SetValue(spec, icon);
            AccessTools.Field(typeof(InventoryItemSpec), "itemIconSpriteStandard").SetValue(spec, icon);
            AccessTools.Field(typeof(InventoryItemSpec), "itemIconSpriteDropped").SetValue(spec, droppedIcon);
        }

        private static Sprite MakeIcon(bool dropped)
        {
            const int size = 128;
            var pixels = new Color32[size * size];
            for (var y = 10; y < 118; y++)
                for (var x = 5; x < 123; x++)
                {
                    var edge = x < 10 || x > 117 || y < 15 || y > 112;
                    if (dropped)
                    {
                        if (edge) pixels[y * size + x] = new Color32(131, 209, 236, 160);
                    }
                    else
                        pixels[y * size + x] = edge ? new Color32(80, 87, 90, 255) : new Color32(21, 18, 15, 255);
                }
            if (!dropped)
            {
                // A yard's ladder on the screen: rows, and the diagonal joining them.
                for (var row = 0; row < 5; row++)
                    for (var x = 30 + row * 8; x < 112; x++)
                        for (var t = 0; t < 3; t++)
                            pixels[(36 + row * 14 + t) * size + x] = new Color32(224, 191, 106, 255);
                for (var k = 0; k < 64; k++)
                    for (var t = 0; t < 3; t++)
                    {
                        var x = 30 + k / 2 + t;
                        var y = 36 + (int)(k * 0.9f);
                        if (x < size && y < 118)
                            pixels[y * size + x] = new Color32(224, 191, 106, 255);
                    }
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "AIEngineer_TabletIcon", wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}
