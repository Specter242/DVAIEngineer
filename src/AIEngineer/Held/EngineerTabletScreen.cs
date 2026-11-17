using System;
using AIEngineer.Game;
using AIEngineer.UI.Tablet;
using DV.CabControls;
using DV.Interaction;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AIEngineer.Held
{
    /// <summary>
    /// The tablet's screen, on the held item and on its mounted form: a
    /// canvas the tablet's pages draw on (<see cref="TabletSurface"/>),
    /// rendered by a camera of its own into a texture on a quad over the
    /// shell, as Realistic Firing's firing tablet draws its screen
    /// (FiringTabletScreen, d0f162e9). Each plate has one of the screen's
    /// buttons over it, the game's own cab buttons, so a mouse (mouse mode
    /// while held) and VR hands press them alike. The map has a button of
    /// its own over the part of it no plate covers: where it is pressed comes
    /// from where the player points. The screen is drawn only while it can
    /// be seen: on a press, and twice a second otherwise.
    /// </summary>
    internal sealed class EngineerTabletScreen : MonoBehaviour, ITabletButtons
    {
        internal const float PixelSize = 0.00028f;
        private const float SurfaceZ = -EngineerTablet.Thickness * 0.5f - 0.0005f;
        private const float ButtonDepth = 0.004f;
        internal const float ButtonFront = SurfaceZ - ButtonDepth;
        private const int Width = (int)TabletSkin.W, Height = (int)TabletSkin.H;
        private const int SlotCount = 96;
        private const float RefreshSeconds = 0.5f;

        private Canvas canvas;
        private RectTransform root;
        private TabletSurface surface;
        private MeshRenderer display, shell;
        private Material material;
        private Mesh mesh;
        private RenderTexture texture;
        private DV.Items.ItemTransparencyHandler transparency;
        private DV.Customization.Gadgets.GadgetItem item;
        private GameObject[] slots;
        private BoxCollider[] colliders;
        private Rect[] placed;
        private GameObject mapButton;
        private BoxCollider mapCollider;
        private Rect mapPlaced;
        private bool visible, dirty = true;
        private float refreshAt, lastVisibleAt;
        private bool started;

        public int Count => SlotCount;

        /// <summary>The screen's texture as last drawn (for the self-test's picture of it); null while it isn't drawn.</summary>
        internal RenderTexture Texture => texture;

        /// <summary>Whether the screen shows on the item now: drawn, and its display on.</summary>
        internal bool Showing => texture != null && display != null && display.enabled;

        private void Start()
        {
            item = GetComponent<DV.Customization.Gadgets.GadgetItem>();
            shell = GetComponentInChildren<MeshRenderer>();
            transparency = GetComponent<DV.Items.ItemTransparencyHandler>();
            started = true;
            Build();
        }

        // Put away in the inventory the item is disabled, and its canvas let go; taken out again, it is built anew.
        private void OnEnable()
        {
            dirty = true;
            lastVisibleAt = Time.unscaledTime;
            if (started)
                Build();
        }

        private void OnDisable()
        {
            ReleaseTexture();
            ReleaseCanvas();
        }

        private void OnDestroy()
        {
            ReleaseTexture();
            ReleaseCanvas();
            if (material != null)
                Object.Destroy(material);
            if (mesh != null)
                Object.Destroy(mesh);
        }

        // ---- Building ----

        private void Build()
        {
            if (canvas != null)
                return;
            var screen = new GameObject("[engineer tablet screen]", typeof(RectTransform), typeof(Canvas));
            canvas = screen.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            screen.transform.SetParent(ScreenRenderer.Acquire(), false);
            screen.transform.localPosition = Vector3.forward;
            screen.transform.localScale = Vector3.one * PixelSize;
            screen.layer = ScreenRenderer.Layer;
            canvas.worldCamera = ScreenRenderer.Camera;
            canvas.enabled = false;
            var scaler = screen.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            scaler.referencePixelsPerUnit = 100f;
            root = screen.GetComponent<RectTransform>();
            root.sizeDelta = new Vector2(Width, Height);
            if (display == null)
                BuildDisplay();
            if (slots == null)
                BuildButtons();
            surface = new TabletSurface(root, this);
            dirty = true;
            item?.Item?.ReCacheRenderers();
        }

        private void BuildDisplay()
        {
            var go = new GameObject("[engineer tablet display]");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, SurfaceZ);
            var w = Width * PixelSize * 0.5f;
            var h = Height * PixelSize * 0.5f;
            mesh = new Mesh { name = "AIEngineer_TabletScreen" };
            mesh.vertices = new[] { new Vector3(-w, -h, 0), new Vector3(-w, h, 0), new Vector3(w, h, 0), new Vector3(w, -h, 0) };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(Shader.Find("Unlit/Texture")) { name = "AIEngineer_TabletDisplay" };
            display = go.AddComponent<MeshRenderer>();
            display.sharedMaterial = material;
            display.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            display.receiveShadows = false;
            display.enabled = false;
        }

        /// <summary>The screen's buttons: a pool of slots the pages' plates take in order, and the map's own behind them.</summary>
        private void BuildButtons()
        {
            slots = new GameObject[SlotCount];
            colliders = new BoxCollider[SlotCount];
            placed = new Rect[SlotCount];
            for (var i = 0; i < SlotCount; i++)
            {
                var index = i;
                slots[i] = AddButton("slot " + i, new Rect(0, 0, 10, 10), () => Pressed(index), out colliders[i]);
                slots[i].SetActive(false);
            }
            // The map's button covers only the map no plate is over (TabletMap.PressArea), so it never takes a plate's press.
            mapButton = AddButton("map", new Rect(140, 84, 776, 700), MapPressed, out mapCollider);
            mapButton.SetActive(false);
        }

        /// <summary>
        /// A screen button: the game's cab button on an object that never
        /// moves (the button puts itself back where it woke after every
        /// press), its collider on a child that is moved to the plate it is
        /// over. Hover casts include triggers; the screen's buttons must never
        /// push controls or loose items.
        /// </summary>
        private GameObject AddButton(string name, Rect area, Action pressed, out BoxCollider collider)
        {
            var button = new GameObject("[engineer tablet " + name + "]");
            button.SetActive(false);
            button.layer = EngineerTablet.ControlLayer;
            button.transform.SetParent(transform, false);
            button.transform.localPosition = new Vector3(0f, 0f, SurfaceZ - ButtonDepth * 0.5f);
            var hit = new GameObject("[hit]");
            hit.layer = EngineerTablet.ControlLayer;
            hit.transform.SetParent(button.transform, false);
            collider = hit.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            Put(collider, area);
            var spec = button.AddComponent<DV.CabControls.Spec.Button>();
            spec.handPosesOverride = new InteractionHandPoses();
            spec.colliderGameObjects = new[] { hit };
            spec.createRigidbody = false;
            spec.useJoints = false;
            var use = button.AddComponent<TabletPress>();
            use.Pressed = pressed;
            button.SetActive(true);
            return button;
        }

        /// <summary>Moves a button's collider over a plate (canvas pixels); the button itself stays where it is.</summary>
        private static void Put(BoxCollider collider, Rect area)
        {
            collider.transform.localPosition = new Vector3((area.center.x - Width * 0.5f) * PixelSize, (Height * 0.5f - area.center.y) * PixelSize, 0f);
            collider.size = new Vector3(area.width * PixelSize, area.height * PixelSize, ButtonDepth);
        }

        public void Place(int index, Rect canvasRect, bool active)
        {
            if (placed[index] != canvasRect)
            {
                placed[index] = canvasRect;
                Put(colliders[index], canvasRect);
            }
            if (slots[index].activeSelf != active)
                slots[index].SetActive(active);
        }

        public Rect RectOf(int index) => index >= 0 && index < SlotCount ? placed[index] : default;

        public void HideFrom(int index)
        {
            for (var i = index; i < SlotCount; i++)
                if (slots[i].activeSelf)
                    slots[i].SetActive(false);
        }

        // ---- Presses ----

        private void Pressed(int slot)
        {
            surface?.Press(slot);
            dirty = true;
            refreshAt = 0;
        }

        private void MapPressed()
        {
            var at = Pointer();
            if (!at.HasValue)
                return;
            TabletMap.PressAt(at.Value);
            dirty = true;
            refreshAt = 0;
        }

        /// <summary>
        /// Where on the canvas the player points: on the desktop, the mouse
        /// in mouse mode, else the middle of the view; the point where that
        /// ray meets the screen. Null if it doesn't.
        /// </summary>
        private Vector2? Pointer()
        {
            var camera = PlayerManager.ActiveCamera;
            if (camera == null)
                return null;
            var ray = Cursor.visible ? camera.ScreenPointToRay(Input.mousePosition) : new Ray(camera.transform.position, camera.transform.forward);
            var plane = new Plane(transform.forward, transform.TransformPoint(new Vector3(0, 0, SurfaceZ)));
            if (!plane.Raycast(ray, out var distance))
                return null;
            var local = transform.InverseTransformPoint(ray.GetPoint(distance));
            return new Vector2(local.x / PixelSize + Width * 0.5f, Height * 0.5f - local.y / PixelSize);
        }

        // ---- Drawing ----

        private void Update()
        {
            if (canvas == null)
                return;
            var camera = PlayerManager.ActiveCamera;
            visible = camera != null && shell != null && shell.enabled && shell.gameObject.activeInHierarchy
                && (transparency == null || !transparency.IsTransparent)
                && (camera.transform.position - transform.position).sqrMagnitude < 64f;
            if (display != null)
                display.enabled = visible && texture != null;
            if (!visible)
            {
                if (Time.unscaledTime - lastVisibleAt > 10f)
                    ReleaseTexture();
                return;
            }
            lastVisibleAt = Time.unscaledTime;
            if (Time.unscaledTime >= refreshAt)
            {
                refreshAt = Time.unscaledTime + RefreshSeconds;
                dirty = true;
            }
        }

        private void LateUpdate()
        {
            if (!visible || !dirty || canvas == null || display == null)
                return;
            dirty = false;
            var world = Host.World;
            surface.Run(() => TabletUi.Draw(world));
            PlaceMapButton(world);
            Render();
        }

        private void Render()
        {
            if (texture == null)
            {
                texture = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "AIEngineer_TabletDisplay",
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = true,
                    autoGenerateMips = false,
                };
                texture.Create();
                material.mainTexture = texture;
            }
            var camera = ScreenRenderer.Camera;
            var previous = RenderTexture.active;
            try
            {
                canvas.enabled = true;
                camera.targetTexture = texture;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                texture.GenerateMips();
                display.enabled = true;
            }
            finally
            {
                canvas.enabled = false;
                camera.targetTexture = null;
                RenderTexture.active = previous;
            }
        }

        /// <summary>Draws the screen now, whether or not it is seen (the self-test's picture of it).</summary>
        internal void DrawNow()
        {
            if (canvas == null)
                Build();
            var world = Host.World;
            surface.Run(() => TabletUi.Draw(world));
            PlaceMapButton(world);
            Render();
        }

        private void PlaceMapButton(World world)
        {
            if (mapButton == null)
                return;
            var area = TabletUi.Page == TabletTab.Map && world != null ? TabletMap.PressArea : Rect.zero;
            var on = area.width > 0 && area.height > 0;
            if (on && area != mapPlaced)
            {
                mapPlaced = area;
                Put(mapCollider, area);
            }
            if (mapButton.activeSelf != on)
                mapButton.SetActive(on);
        }

#if !PUBLIC_RELEASE
        /// <summary>Presses the plate in a slot (the self-test's), through the game's own button: as a click does, it pushes the button in and lets it back.</summary>
        internal bool PressSlotForTest(int slot)
        {
            var control = slot >= 0 && slot < SlotCount ? slots[slot].GetComponent<ControlImplBase>() : null;
            if (control == null)
                return false;
            control.Use();
            return true;
        }
#endif

#if !PUBLIC_RELEASE
        /// <summary>Whether a slot's collider lies over the plate it was put on (the self-test's check after presses).</summary>
        internal bool HitInPlace(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
                return false;
            var r = placed[slot];
            var want = transform.TransformPoint(new Vector3((r.center.x - Width * 0.5f) * PixelSize, (Height * 0.5f - r.center.y) * PixelSize, SurfaceZ - ButtonDepth * 0.5f));
            return (colliders[slot].transform.position - want).magnitude < 0.002f;
        }
#endif

        /// <summary>The slot whose plate covers a canvas point; -1 if none.</summary>
        internal int SlotAt(Vector2 canvasPoint)
        {
            for (var i = 0; i < SlotCount; i++)
                if (slots[i].activeSelf && placed[i].Contains(canvasPoint))
                    return i;
            return -1;
        }

        private void ReleaseTexture()
        {
            if (display != null)
                display.enabled = false;
            if (material != null)
                material.mainTexture = null;
            if (texture == null)
                return;
            texture.Release();
            Object.Destroy(texture);
            texture = null;
            dirty = true;
        }

        private void ReleaseCanvas()
        {
            if (canvas == null)
                return;
            Object.Destroy(canvas.gameObject);
            canvas = null;
            surface = null;
            ScreenRenderer.Release();
        }
    }

    /// <summary>A screen button's press (the game's cab button, Realistic Firing's FiringTabletPageButton).</summary>
    internal sealed class TabletPress : MonoBehaviour
    {
        internal Action Pressed;
        private ControlImplBase control;

        private void Start()
        {
            control = GetComponent<ControlImplBase>();
            if (control != null)
                control.ValueChanged += Changed;
        }

        private void Changed(ValueChangedEventArgs change)
        {
            if (change.newValue >= 0.5f && change.oldValue < 0.5f)
                Pressed?.Invoke();
        }

        private void OnDestroy()
        {
            if (control != null)
                control.ValueChanged -= Changed;
        }
    }

    /// <summary>
    /// One disabled camera, shared by every engineer's tablet, that renders a
    /// screen's canvas into its texture. Its own place and layer keep it apart
    /// from Realistic Firing's and Comms Tablet's screen cameras.
    /// </summary>
    internal static class ScreenRenderer
    {
        // Unused by the stock game's cameras; only the private screen cameras see this layer.
        internal const int Layer = 30;
        private static GameObject host;
        private static Camera camera;
        private static int users;

        internal static Camera Camera => camera;

        internal static Transform Acquire()
        {
            if (host == null)
            {
                host = new GameObject("[AIEngineer tablet screen renderer]");
                Object.DontDestroyOnLoad(host);
                host.transform.position = new Vector3(0f, -1000f, -1f);
                camera = host.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = TabletSkin.H * EngineerTabletScreen.PixelSize * 0.5f;
                camera.aspect = TabletSkin.W / TabletSkin.H;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 2f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << Layer;
                camera.renderingPath = RenderingPath.Forward;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.useOcclusionCulling = false;
            }
            users++;
            // The canvas plane sits one meter in front of the camera, independently of the tablet.
            return host.transform;
        }

        internal static void Release()
        {
            if (--users > 0)
                return;
            if (host != null)
                Object.Destroy(host);
            host = null;
            camera = null;
            users = 0;
        }
    }
}
