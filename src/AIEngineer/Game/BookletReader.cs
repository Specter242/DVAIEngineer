using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AIEngineer.Core.Booklet;
using DV.RenderTextureSystem.BookletRender;
using DV.Utils;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AIEngineer.Game
{
    /// <summary>
    /// The game's station booklet and route map, read page by page from their
    /// render prefabs (LevelInfo's world-specific prefabs) for the tablet's
    /// Map tab: each page's schematic tiles and badges, in the schematic's own
    /// units, and the switches its touch cells name (the level's generated
    /// junction data), as the network's indices.
    ///
    /// Other mods draw on the books as the game renders them (Double Track
    /// Maps and Interchange Yards patch the render's page list and the
    /// junction data), so the pages are read from a copy of each render
    /// prefab that has been through that step. The copies are kept, inactive
    /// and far below the world, for <see cref="BookletRenderer"/> to draw the
    /// pages from. Read once per network, the first time a page is wanted.
    /// </summary>
    internal static class BookletReader
    {
        private static readonly Regex PageName = new Regex(@"^\[(\w+)-(\w+)\]$", RegexOptions.CultureInvariant);

        private static readonly MethodInfo PaperData = typeof(StaticTextureRenderBase).GetMethod("GetStaticTemplatePaperData", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Network readFor;
        private static Booklet booklet;
        private static List<SchematicSource> sources;
        private static GameObject holder;

        /// <summary>The copies of the two renders the pages were read from; null until read.</summary>
        public static StaticPagesRender BookletCopy { get; private set; }

        public static StaticPagesRender RouteCopy { get; private set; }

        /// <summary>What holds the copies: inactive but while a page is drawn.</summary>
        public static GameObject Holder => holder;

        /// <summary>Where the copies stand: far below the world, out of any other camera's way.</summary>
        public static readonly Vector3 Below = new Vector3(0, -30000, 0);

        /// <summary>The booklet matched to the network; null if the game's couldn't be read (the reason is logged).</summary>
        public static Booklet Get(Network network)
        {
            if (network == null)
                return null;
            if (readFor == network)
                return booklet;
            readFor = network;
            booklet = null;
            sources = null;
            if (holder != null)
                Object.Destroy(holder);
            holder = null;
            BookletCopy = RouteCopy = null;
            var watch = Stopwatch.StartNew();
            try
            {
                sources = Read(network, out var problem);
                if (sources == null)
                {
                    Main.Log($"Booklet not read: {problem}");
                    return null;
                }
                var read = watch.ElapsedMilliseconds;
                booklet = Booklet.Build(sources, network.Graph, t => network.Tracks[t].name);
                Main.Log($"Booklet: {booklet.Stations.Count} stations, {sources.Count} pages, read in {read} ms, matched in {watch.ElapsedMilliseconds - read} ms: {booklet.Stats}; touch grids: {Grids}");
            }
            catch (Exception e)
            {
                Main.Log($"Booklet not read: {e}");
                booklet = null;
            }
            return booklet;
        }

        /// <summary>The pages as read, for the self-test's export; null until <see cref="Get"/> has read them.</summary>
        public static IReadOnlyList<SchematicSource> Sources => sources;

        private static List<SchematicSource> Read(Network network, out string problem)
        {
            problem = null;
            var data = SingletonBehaviour<LevelInfo>.Instance != null ? SingletonBehaviour<LevelInfo>.Instance.junctionData : null;
            if (data == null)
            {
                problem = "the level has no junction data";
                return null;
            }
            var result = new List<SchematicSource>();
            holder = new GameObject("AIEngineer booklet");
            holder.SetActive(false);
            holder.transform.position = Below;
            Object.DontDestroyOnLoad(holder);
            {
                var stationPages = data.GetPageData(DV.Items.JunctionMap.JunctionMapType.Station);
                var worldPages = data.GetPageData(DV.Items.JunctionMap.JunctionMapType.World);
                var booklet = BookletCopy = Copy(LevelInfo.WorldSpecificPrefabs.SchematicMapRender, holder.transform);
                if (booklet == null)
                {
                    problem = "the level has no station booklet render";
                    return null;
                }
                if (stationPages == null || stationPages.Length != booklet.staticPages.Count)
                {
                    problem = $"the booklet's render has {booklet.staticPages.Count} pages and its switch data {stationPages?.Length ?? 0}";
                    return null;
                }
                var grid = data.GetMapGridSize(DV.Items.JunctionMap.JunctionMapType.Station);
                for (var i = 0; i < booklet.staticPages.Count; i++)
                {
                    var page = booklet.staticPages[i];
                    var m = PageName.Match(page.gameObject.name);
                    if (!m.Success || page.gameObject.name == "[Cover]" || page.gameObject.name == "[Legend]")
                        continue;
                    var roots = page.GetComponentsInChildren<RectTransform>(true).Where(t => t.name == "SMap" && ActiveUnder(t, page.transform)).ToList();
                    if (roots.Count == 0)
                        continue;
                    var src = ReadPage(network, page.transform, roots, stationPages[i], grid, false);
                    if (src != null)
                        result.Add(src);
                }
                var route = RouteCopy = Copy(LevelInfo.WorldSpecificPrefabs.RouteMapRender, holder.transform);
                if (route != null && route.staticPages.Count > 0 && worldPages != null && worldPages.Length > 0)
                {
                    var page = route.staticPages[0];
                    var root = page.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(t => t.name == "OuterWrapper");
                    if (root != null)
                    {
                        var src = ReadPage(network, page.transform, new List<RectTransform> { root }, worldPages[0], data.GetMapGridSize(DV.Items.JunctionMap.JunctionMapType.World), true);
                        if (src != null)
                            result.Add(src);
                    }
                }
            }
            if (result.Count == 0)
            {
                problem = "no pages";
                return null;
            }
            return result;
        }

        /// <summary>A copy of a render prefab, inactive under the holder, through the step where the game (and other mods) fill its pages.</summary>
        private static StaticPagesRender Copy(LevelInfo.WorldSpecificPrefabs which, Transform holder)
        {
            var prefab = LevelInfo.GetWorldSpecificPrefab(which);
            if (prefab == null)
                return null;
            var copy = Object.Instantiate(prefab, holder);
            var render = copy.GetComponent<StaticPagesRender>();
            if (render == null)
                return null;
            try
            {
                PaperData?.Invoke(render, null);
            }
            catch (Exception e)
            {
                Main.Log($"Booklet: the render's page step failed on a copy, reading it as it is: {e.InnerException?.Message ?? e.Message}");
            }
            return render;
        }

        private static bool ActiveUnder(Transform t, Transform top)
        {
            for (var x = t; x != null && x != top; x = x.parent)
                if (!x.gameObject.activeSelf)
                    return false;
            return true;
        }

        private static string TextOf(Transform page, string name)
        {
            var t = page.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(x => x.gameObject.name == name);
            return t != null ? t.text.Trim() : null;
        }

        /// <summary>The item's touch area, in canvas units and centred on the page's canvas: the area the game divides by
        /// the grid, the same whatever the grid (the stock grids in cells of 50). The render pages don't hold the item's
        /// collider; the self-test checks these by every drawn switch being named.</summary>
        private static readonly Vector2 RouteTouchArea = new Vector2(1600, 1600), BookletTouchArea = new Vector2(1150, 1550);

        private static readonly Dictionary<string, string> grids = new Dictionary<string, string>();

        /// <summary>The touch grids read, each page kind's last: its cells, on its touch area, of its canvas
        /// ("route map 40 x 40 on 1600 x 1600 of 1600 x 1600").</summary>
        public static string Grids => string.Join(", ", grids.Select(kv => $"{kv.Key} {kv.Value}"));

        private static SchematicSource ReadPage(Network network, Transform page, List<RectTransform> roots, JunctionGeneratedDataRuntime.JunctionPageData cells, Vector2Int grid, bool isRoute)
        {
            var schematic = roots[0];
            var tiles = new List<SchematicTile>();
            var badges = new List<SchematicBadge>();
            foreach (var root in roots)
            {
                foreach (var image in root.GetComponentsInChildren<Image>(true))
                {
                    var rt = image.rectTransform;
                    if (rt == root || image.sprite == null || !image.enabled || !ActiveUnder(rt, root) || InBadge(rt, root))
                        continue;
                    var at = schematic.InverseTransformPoint(rt.position);
                    if (image.sprite.name == "SM-ID")
                    {
                        badges.Add(ReadBadge(rt, at, image.color));
                        continue;
                    }
                    var turn = (Quaternion.Inverse(schematic.rotation) * rt.rotation).eulerAngles.z;
                    var scale = new Vector2(rt.lossyScale.x / schematic.lossyScale.x, rt.lossyScale.y / schematic.lossyScale.y);
                    tiles.Add(new SchematicTile(image.sprite.name, at.x, at.y, turn, rt.rect.width * scale.x, rt.rect.height * scale.y));
                }
            }
            // Each touch cell's middle on the page, then on the schematic: the game divides the item's touch area by
            // the grid (Double Track's route map: 40 cells of 40 on the same 1,600, not the stock 32 of 50).
            var canvas = page.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(t => t.name == "Template Canvas") ?? (RectTransform)page;
            var size = canvas.rect.size;
            var area = isRoute ? RouteTouchArea : BookletTouchArea;
            grids[isRoute ? "route map" : "booklet"] = $"{grid.x} x {grid.y} on {area.x:F0} x {area.y:F0} of {size.x:F0} x {size.y:F0}";
            if (grid.x <= 0 || grid.y <= 0)
            {
                Main.Warn($"Booklet page {page.name}: its touch grid is {grid.x} x {grid.y}; its switches are left unnamed");
                grid = Vector2Int.zero;
            }
            var switches = new List<SchematicSwitch>();
            var ordered = RailTrackRegistryBase.Junctions;
            foreach (var c in cells.junctionDataCoords ?? new List<JunctionGeneratedDataRuntime.DataCoord>())
            {
                var index = c.data.junctionIndex;
                if (index < 0 || index >= ordered.Length || ordered[index] == null || !network.TryGetJunction(ordered[index], out var j))
                    continue;
                if (grid == Vector2Int.zero)
                    break;
                var (cx, cy) = TouchGrid.CellMiddle(area.x, area.y, grid.x, grid.y, c.coord.x, c.coord.y);
                var onCanvas = new Vector3((float)cx, (float)cy, 0);
                // The canvas's own pivot is its middle on the game's pages; allow for one that isn't.
                onCanvas += new Vector3((0.5f - canvas.pivot.x) * size.x, (0.5f - canvas.pivot.y) * size.y, 0);
                var at = schematic.InverseTransformPoint(canvas.TransformPoint(onCanvas));
                switches.Add(new SchematicSwitch(at.x, at.y, j));
            }
            if (isRoute)
                return new SchematicSource("[Route]", null, null, null, "ROUTE MAP", null, true, tiles, badges, switches);
            var color = page.GetComponentsInChildren<Image>(true).FirstOrDefault(x => x.gameObject.name == "StationBg");
            var code = TextOf(page, "Text-StationID");
            var m = PageName.Match(page.gameObject.name);
            return new SchematicSource(page.gameObject.name, code ?? m.Groups[1].Value, TextOf(page, "Text-StationName"), TextOf(page, "Text-YardID") ?? m.Groups[2].Value,
                                       TextOf(page, "Text-YardType"), color != null ? "#" + ColorUtility.ToHtmlStringRGBA(color.color) : null, false, tiles, badges, switches);
        }

        private static bool InBadge(Transform t, Transform root)
        {
            for (var x = t.parent; x != null && x != root; x = x.parent)
            {
                var image = x.GetComponent<Image>();
                if (image != null && image.sprite != null && image.sprite.name == "SM-ID")
                    return true;
            }
            return false;
        }

        /// <summary>A badge's label as drawn: a two-digit number's first digit is a text of its own, before the rest; its kind is the one kind plate shown.</summary>
        private static SchematicBadge ReadBadge(RectTransform badge, Vector3 at, Color color)
        {
            string first = "", rest = "", kind = null;
            foreach (Transform child in badge)
            {
                if (!child.gameObject.activeSelf)
                    continue;
                var name = child.name;
                var text = child.GetComponent<TMP_Text>();
                if (name.StartsWith("text-2digit", StringComparison.Ordinal) && text != null && first.Length == 0)
                    first = text.text.Trim();
                else if (name.StartsWith("text-ID", StringComparison.Ordinal) && text != null && rest.Length == 0)
                    rest = text.text.Trim();
                else if (name.StartsWith("Type-", StringComparison.Ordinal))
                    kind = name.Substring("Type-".Length);
            }
            return new SchematicBadge(at.x, at.y, first + rest, kind, "#" + ColorUtility.ToHtmlStringRGBA(color));
        }

        /// <summary>The pages as read, in the form the tests read (scripts/booklet_from_survey.py writes the same).</summary>
        public static void Export(string path)
        {
            if (sources == null)
                return;
            var pages = new JArray();
            foreach (var s in sources)
            {
                pages.Add(new JObject
                {
                    ["name"] = s.Name, ["station"] = s.Station, ["stationName"] = s.StationName, ["yard"] = s.Yard, ["title"] = s.Title, ["color"] = s.Color, ["route"] = s.IsRoute,
                    ["tiles"] = new JArray(s.Tiles.Select(t => new JObject
                    {
                        ["s"] = t.Sprite, ["x"] = Math.Round(t.X, 2), ["y"] = Math.Round(t.Y, 2), ["r"] = Math.Round(t.Rotation, 2), ["w"] = Math.Round(t.Width, 2), ["h"] = Math.Round(t.Height, 2),
                    })),
                    ["badges"] = new JArray(s.Badges.Select(b => new JObject { ["x"] = Math.Round(b.X, 2), ["y"] = Math.Round(b.Y, 2), ["text"] = b.Text, ["type"] = b.Kind, ["color"] = b.Color })),
                    ["switches"] = new JArray(s.Switches.Select(w => new JObject { ["x"] = Math.Round(w.X, 2), ["y"] = Math.Round(w.Y, 2), ["j"] = w.Junction })),
                });
            }
            File.WriteAllText(path, new JObject { ["source"] = "game", ["pages"] = pages }.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
