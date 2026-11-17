using System;
using System.Linq;
using DV;
using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.Utils;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AIEngineer.Held
{
    /// <summary>
    /// The engineer's tablet: a held and mountable item, made as Realistic
    /// Firing makes its firing tablet (DVRealisticFiring d0f162e9,
    /// Items/FiringTablet.cs): a copy of the stock clinometer, its model
    /// replaced by a shell and a screen (<see cref="EngineerTabletScreen"/>),
    /// with the stock gadget placement for a mounted form and the stock
    /// saving. It is saved, sold beside the clinometer, given once per save,
    /// and given by F9 to a player who has none; none of those while the
    /// Comms Tablet merges this mod (<see cref="CommsTabletApp"/>), whose
    /// Engineers app shows these pages instead.
    /// </summary>
    internal static class EngineerTablet
    {
        public const string PrefabName = "AIEngineer_Tablet";
        public const string DisplayName = "Engineer's tablet";
        internal const string SourcePrefab = "Clinometer";
        // The stock gadget body and nested-control layers.
        internal const int BodyLayer = 13, ControlLayer = 22;
        internal const float Thickness = 0.016f;
        internal static readonly Vector3 Size = new Vector3(0.34f, 0.30f, Thickness);
        private static GameObject template;
        private static bool buildFailed;

        public static GameObject Template
        {
            get
            {
                if (template == null && !buildFailed)
                {
                    template = Build();
                    buildFailed = template == null;
                }
                return template;
            }
        }

        private static GameObject Build()
        {
            GameObject holder = null;
            try
            {
                return BuildTemplate(ref holder);
            }
            catch (Exception error)
            {
                // A changed stock prefab must not abort the saved-item loader.
                if (holder != null)
                    Object.Destroy(holder);
                Main.Warn("Can't build the engineer's tablet; saved tablets will be skipped: " + error);
                return null;
            }
        }

        private static GameObject BuildTemplate(ref GameObject holder)
        {
            var source = Resources.Load<GameObject>(SourcePrefab);
            if (source == null)
            {
                Main.Warn("No '" + SourcePrefab + "' prefab to make the engineer's tablet from");
                return null;
            }
            holder = new GameObject("[AIEngineer tablet templates]");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            var item = Object.Instantiate(source, holder.transform, false);
            item.name = PrefabName;
            item.transform.localScale = Vector3.one;
            RemoveStockModel(item.transform);
            foreach (var collider in item.GetComponents<Collider>())
                Object.DestroyImmediate(collider);
            var box = item.AddComponent<BoxCollider>();
            box.size = Size;
            var spec = item.GetComponent<DV.CabControls.Spec.Item>();
            spec.colliderGameObjects = new[] { item };
            spec.rigidbodyMass = 1.4f;
            spec.allowPlayerRotationXAxisVr = true;
            spec.preventNestedGrabWhenUnGrabbed = false;
            var inventory = item.GetComponent<InventoryItemSpec>();
            AccessTools.Field(typeof(InventoryItemSpec), "itemPrefabName").SetValue(inventory, PrefabName);
            inventory.NameOverride = DisplayName;
            inventory.IsEssential = false;
            inventory.ImmuneToDumpster = false;
            inventory.PreviewBounds = new Bounds(Vector3.zero, Size);
            inventory.PreviewPrefab = EngineerTabletBody.Build(holder.transform).gameObject;
            inventory.PreviewPrefab.name = "Engineer's tablet preview";
            EngineerTabletBody.SetIcons(inventory);
            EngineerTabletBody.Build(item.transform);

            var mounted = new GameObject("AIEngineer_Tablet_Gadget");
            mounted.transform.SetParent(holder.transform, false);
            var gadget = mounted.AddComponent<EngineerTabletGadget>();
            ConfigureGadget(gadget);
            EngineerTabletBody.Build(mounted.transform);
            mounted.AddComponent<BoxCollider>().size = Size;
            mounted.layer = BodyLayer;
            mounted.AddComponent<EngineerTabletScreen>();

            var itemGadget = item.GetComponent<GadgetItem>();
            AccessTools.Field(typeof(GadgetItem), "gadgetPrefab").SetValue(itemGadget, gadget);
            AccessTools.Field(typeof(GadgetItem), "onlyPlaceInOneAxis").SetValue(itemGadget, true);
            item.AddComponent<EngineerTabletScreen>();
            item.AddComponent<EngineerTabletDescription>();
            item.AddComponent<EngineerTabletHold>();
            Main.Log("Engineer's tablet built from the stock Clinometer item; uses stock gadget placement and saves");
            return item;
        }

        private static void RemoveStockModel(Transform item)
        {
            // Inventory and distance-grab align through these stock hand
            // anchors, even with precision grabbing enabled.
            for (var i = item.childCount - 1; i >= 0; i--)
            {
                var child = item.GetChild(i);
                if (child.name != "[anchor]" && child.name != "[left anchor]" && child.name != "[right anchor]")
                    Object.DestroyImmediate(child.gameObject);
            }
        }

        private static void ConfigureGadget(GadgetBase gadget)
        {
            var bounds = new Bounds(Vector3.zero, Size);
            bounds.Encapsulate(new Vector3(0f, 0f, EngineerTabletScreen.ButtonFront));
            AccessTools.Field(typeof(GadgetBase), "boundsCenter").SetValue(gadget, bounds.center);
            AccessTools.Field(typeof(GadgetBase), "boundsSize").SetValue(gadget, bounds.size);
            AccessTools.Field(typeof(GadgetBase), "requiredMountPoints").SetValue(gadget, 2);
            AccessTools.Field(typeof(GadgetBase), "removalMethod").SetValue(gadget,
                GadgetBase.GadgetRemovalMethod.EmptyHand | GadgetBase.GadgetRemovalMethod.Remover);
            AccessTools.Field(typeof(GadgetBase), "vrIgnoreColliders").SetValue(gadget, new Collider[0]);
            AccessTools.Field(typeof(GadgetBase), "highlightMeshes").SetValue(gadget, new MeshFilter[0]);
            // A self-powered display: no wiring.
            AccessTools.Field(typeof(TrainCarCustomization.TrainCarCustomizerBase), "requirements").SetValue(gadget,
                new TrainCarCustomization.TrainCarCustomizerBase.TrainCarRequirements
                {
                    trainCarPresence = TrainCarCustomization.TrainCarCustomizerBase.CustomizerTrainCarRequirements.RequireInterior,
                });
        }

        /// <summary>Whether the player has a tablet: in hand, in the inventory, or lying or mounted in the world.</summary>
        public static bool PlayerHasOne()
        {
            foreach (var screen in Resources.FindObjectsOfTypeAll<EngineerTabletScreen>())
            {
                if (screen == null || !screen.gameObject.scene.IsValid() || template != null && screen.transform.IsChildOf(template.transform.parent))
                    continue;
                if (screen.GetComponent<GadgetBase>() != null)
                    return true;
                var spec = screen.GetComponent<InventoryItemSpec>();
                if (spec != null && spec.BelongsToPlayer)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The tablet key (F9): a tablet in front of a player who has none.
        /// While the Comms Tablet merges this mod, none is given (its own
        /// takes this one's place, and it offers guests no way to give one):
        /// the player is told where the pages are.
        /// </summary>
        public static string Requested()
        {
            if (CommsTabletLink.Merged)
                return "AI Engineer's tablet is the Engineers app on the Comms Tablet's home screen; the Comms Tablet's mod settings give you one if you have none.";
            return PlayerHasOne()
                ? "You have an engineer's tablet: in your inventory, in hand, or where you left it. Another is sold beside the clinometer."
                : GiveToPlayer();
        }

        /// <summary>
        /// Adds the tablet to the game's item catalog (<c>Globals.G.Items.items</c>), which
        /// Multiplayer builds the items it makes on clients from, as the Comms Tablet adds its
        /// own. Returns false if there is no catalog or no tablet.
        /// </summary>
        public static bool RegisterAsGameItem()
        {
            var items = Globals.G?.Items?.items;
            var spec = Template != null ? Template.GetComponent<InventoryItemSpec>() : null;
            if (items == null || spec == null || spec.ItemPrefabName != PrefabName)
                return false;
            if (!items.Contains(spec))
                items.Add(spec);
            return true;
        }

        public static string GiveToPlayer()
        {
            var source = Template;
            var camera = PlayerManager.ActiveCamera;
            if (camera == null)
                return "No tablet: load a game first";
            // A client's item would be its own; the host gives them (as the Comms Tablet).
            if (!Game.MultiplayerGate.MayDrive)
                return "In multiplayer, only the host can give engineer's tablets";
            if (source == null)
                return "The engineer's tablet couldn't be built; see the mod's log";
            var item = Object.Instantiate(source, camera.transform.position + camera.transform.forward * 0.6f, camera.transform.rotation);
            item.name = PrefabName;
            item.GetComponent<InventoryItemSpec>().BelongsToPlayer = true;
            var impl = item.GetComponent<ItemBase>();
            if (impl == null)
            {
                Object.Destroy(item);
                return "The engineer's tablet couldn't become an inventory item";
            }
            SingletonBehaviour<StorageController>.Instance.AddItemToWorldStorage(impl);
            return "An engineer's tablet is in front of you: pick it up, or fit it to a large gadget mount";
        }
    }

    /// <summary>The tablet mounted on a gadget mount: no state of its own; the pages are the mod's.</summary>
    internal sealed class EngineerTabletGadget : GadgetBase
    {
    }

    internal sealed class EngineerTabletDescription : MonoBehaviour, IInventoryItemLocalizer
    {
        private void Awake() => GetComponent<InventoryItemSpec>().NameOverride = EngineerTablet.DisplayName;

        public string GetNameParam() => EngineerTablet.DisplayName;

        public string GetCustomDescription() => "AI Engineer's tablet: your engineers, the station booklet's map, and your jobs. " +
            "Press the screen (mouse mode while held on desktop): a track, a cut, or a loco on the map brings up its orders. " +
            "Hold and move on the map to drag it. It also fits a large gadget mount.";
    }

    /// <summary>Keeps the desktop screen beside the placement ray, independently of zoom (Realistic Firing's FiringTabletHold).</summary>
    internal sealed class EngineerTabletHold : MonoBehaviour, ICustomNonVRGrabAnchor
    {
        public (Vector3 localPos, Quaternion localRot) GetGrabAnchor()
        {
            var camera = PlayerManager.ActiveCamera;
            var grabber = GetComponent<GrabHandlerItem>()?.GetGrabber();
            // The stock inventory also asks for this anchor when positioning an inventory drop.
            var attach = grabber?.Cursor?.Rig?.GetAttachPoint()
                ?? PlayerManager.PlayerTransform?.GetComponentInChildren<IPlayerRig>()?.GetAttachPoint();
            if (camera == null || attach == null)
                return (Vector3.zero, Quaternion.identity);
            var fov = GamePreferences.Get<float>(Preferences.FieldOfView);
            var sway = attach.parent != null ? attach.parent.GetComponentInChildren<ItemSway>() : null;
            var origin = sway != null
                ? new Vector3(sway.itemOffsetXY.x, sway.itemOffsetXY.y, sway.itemDistance.Evaluate(fov))
                : Quaternion.Inverse(attach.rotation) * (attach.position - camera.transform.position);
            return (CameraOffset(fov, camera.aspect) - origin, Quaternion.identity);
        }

        internal static Vector3 CameraOffset(float fieldOfView, float aspect)
        {
            var angle = Math.Max(30f, Math.Min(120f, fieldOfView)) * Math.PI / 360d;
            const float right = 0.21f;
            var halfWidth = right + EngineerTablet.Size.x * 0.5d + 0.04d;
            var halfHeight = Math.Max(0.21d, halfWidth / Math.Max(1d, aspect));
            var distance = (float)Math.Max(0.34d, halfHeight / Math.Tan(angle)) - EngineerTabletScreen.ButtonFront;
            return new Vector3(right, -0.035f, distance);
        }
    }
}
