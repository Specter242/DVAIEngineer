using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using DV.CashRegister;
using DV.Items;
using DV.Shops;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AIEngineer.Held
{
    /// <summary>
    /// Saved and bought items are made from their prefab name with
    /// <c>Resources.Load</c>, which can't find a mod's item, so the game's two
    /// load sites (the saved-item and the purchased-item loaders) are taught
    /// the tablet. Realistic Firing and Comms Tablet rewrite the same sites,
    /// each replacing the load with its own; whichever ran last would find
    /// nothing left to replace. This one leaves every load in place and asks
    /// after it: a load that found nothing, for the tablet's name, gets the
    /// tablet. That works before, after, or between the others.
    /// </summary>
    [HarmonyPatch]
    internal static class EngineerTabletLoading
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(StartingItemsController), "InstantiateItem");
            yield return AccessTools.EnumeratorMoveNext(AccessTools.DeclaredMethod(typeof(GlobalShopController), "InstantiatePurchasedItems"));
        }

        /// <summary>What a load found, or the tablet if it found nothing and was asked for it.</summary>
        internal static Object After(string path, Object found) =>
            found != null ? found : path == EngineerTablet.PrefabName ? EngineerTablet.Template : null;

        /// <summary>Whether a call loads a prefab by name: the stock load, or another mod's in its place (its name and perhaps a number).</summary>
        private static bool IsLoad(MethodInfo m)
        {
            if (m == null || !m.IsStatic || m.ReturnType != typeof(Object) || m.IsGenericMethodDefinition)
                return false;
            var p = m.GetParameters();
            if (p.Length == 0 || p[0].ParameterType != typeof(string) || p.Length > 2 || (p.Length == 2 && p[1].ParameterType != typeof(int)))
                return false;
            return m.DeclaringType == typeof(Resources) ? m.Name == nameof(Resources.Load) && p.Length == 1 : m.Name.Contains("Load");
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("RealisticFiring", "CommsTablet")]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = instructions.ToList();
            var after = AccessTools.DeclaredMethod(typeof(EngineerTabletLoading), nameof(After));
            var wrapped = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i].operand is MethodInfo m) || (list[i].opcode != OpCodes.Call && list[i].opcode != OpCodes.Callvirt) || !IsLoad(m))
                    continue;
                // The name is on the stack under the load's other argument, if it has one (a constant): keep a copy of it.
                var takesNumber = m.GetParameters().Length == 2;
                var at = takesNumber ? i - 1 : i;
                if (takesNumber && (at < 0 || !IsConstant(list[at])))
                    continue;
                var dup = new CodeInstruction(OpCodes.Dup);
                dup.MoveLabelsFrom(list[at]);
                list.Insert(at, dup);
                i++;
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, after));
                i++;
                wrapped++;
            }
            if (wrapped == 0)
            {
                var where = $"{original?.DeclaringType?.Name}.{original?.Name}";
                // Skin Manager replaces both game sites' loads with its own helpers, which are wrapped instead.
                if (SkinManagerTabletLoading.Present && original?.DeclaringType?.Assembly != SkinManagerTabletLoading.Assembly)
                    Main.Log($"No prefab load left in {where}: Skin Manager makes these items, through its loaders, wrapped instead");
                else
                    Main.Warn($"The engineer's tablet's lookup found no prefab load in {where}: saved or bought tablets won't appear there");
            }
            return list;
        }

        private static bool IsConstant(CodeInstruction c) =>
            c.opcode == OpCodes.Ldc_I4 || c.opcode == OpCodes.Ldc_I4_S || c.opcode == OpCodes.Ldc_I4_0 || c.opcode == OpCodes.Ldc_I4_1 || c.opcode == OpCodes.Ldc_I4_2
            || c.opcode == OpCodes.Ldc_I4_3 || c.opcode == OpCodes.Ldc_I4_4 || c.opcode == OpCodes.Ldc_I4_5 || c.opcode == OpCodes.Ldc_I4_6 || c.opcode == OpCodes.Ldc_I4_7
            || c.opcode == OpCodes.Ldc_I4_8 || c.opcode == OpCodes.Ldc_I4_M1;
    }

    /// <summary>
    /// Skin Manager (SkinManagerMod 4.4.1) makes saved and bought items through
    /// its own helpers instead of the game's two load sites, so with it
    /// installed a saved tablet was dropped on every load and a bought one
    /// never came (Realistic Firing met this with its fire iron, 2026-10-17;
    /// its <c>SkinManagerFireIronLoading</c>, and the Comms Tablet's
    /// <c>SkinManagerTabletLoading</c>). The loads inside those helpers are
    /// wrapped the same way. Info.json loads this mod after Skin Manager so its
    /// assembly is there to find.
    /// </summary>
    [HarmonyPatch]
    internal static class SkinManagerTabletLoading
    {
        internal static readonly (string Type, string Method)[] Loaders =
        {
            ("SkinManagerMod.Patches.ShopPatches", "InstantiatePaintCan"),
            ("SkinManagerMod.Patches.InventoryPatches", "CustomInstantiateItem"),
        };

        internal static Assembly Assembly =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SkinManagerMod");

        internal static bool Present => Assembly != null;

        /// <summary>Skin Manager's two item loaders, those that are there.</summary>
        internal static List<MethodBase> Targets()
        {
            var assembly = Assembly;
            if (assembly == null)
                return new List<MethodBase>();
            return Loaders.Select(l => assembly.GetType(l.Type, false) is Type type ? (MethodBase)AccessTools.DeclaredMethod(type, l.Method) : null)
                .Where(m => m != null).ToList();
        }

        internal static bool Prepare() => Targets().Count > 0;

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = Targets();
            if (targets.Count < Loaders.Length)
                Main.Warn("Skin Manager's item loaders have changed: the engineer's tablet may not be found in its shops or saves");
            return targets;
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("RealisticFiring", "CommsTablet")]
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            EngineerTabletLoading.Transpiler(instructions, original);
    }

    /// <summary>
    /// Multiplayer makes items on clients from the game's item catalog
    /// (<c>NetworkedItemManager.BuildPrefabLookup</c> reads
    /// <c>Globals.G.Items.items</c>), which a mod's item isn't in, so a host's
    /// engineer's tablet couldn't appear on a client. The tablet joins the
    /// catalog just before that lookup is built, on the host and on clients, as
    /// the Comms Tablet's does (its <c>MultiplayerItems</c>). Only with the
    /// Multiplayer mod; Info.json loads this mod after it.
    /// </summary>
    [HarmonyPatch]
    internal static class EngineerTabletMultiplayerItems
    {
        internal const string Manager = "Multiplayer.Components.Networking.World.NetworkedItemManager";

        internal static MethodBase Target() =>
            AccessTools.TypeByName(Manager) is Type manager ? AccessTools.DeclaredMethod(manager, "BuildPrefabLookup") : null;

        internal static bool Prepare() => Target() != null;

        internal static MethodBase TargetMethod() => Target();

        private static void Prefix()
        {
            try
            {
                if (!EngineerTablet.RegisterAsGameItem())
                    Main.Warn("The engineer's tablet couldn't join the item catalog; Multiplayer can't make it on clients");
            }
            catch (Exception e)
            {
                // Multiplayer's item lookup must be built whatever happens here.
                Main.Error("Couldn't add the engineer's tablet to the item catalog", e);
            }
        }
    }

 [HarmonyPatch(typeof(StartingItemsController), "GetStartingItemsPrefabs")]
    internal static class EngineerTabletStartingItems
    {
        internal const string GivenKey = "AIEngineer_TabletGiven";

        private static void Postfix(SaveGameData data, ref List<StartingItems.StartingItem> __result)
        {
            try
            {
                __result = WithTabletOnce(data, __result, () => EngineerTablet.Template?.GetComponent<InventoryItemSpec>());
            }
            catch (Exception e)
            {
                // A tablet failure must not interrupt the stock saved-item loader.
                Main.Error("Couldn't add the engineer's tablet to the starting equipment", e);
            }
        }

        /// <summary>
        /// The starting kit with the engineer's tablet added once per save: unchanged and the key
        /// cleared while merged; unchanged once given; else the tablet added (from
        /// <paramref name="create"/>) and the key set.
        /// </summary>
        internal static List<StartingItems.StartingItem> WithTabletOnce(SaveGameData data, List<StartingItems.StartingItem> items, Func<InventoryItemSpec> create)
        {
            if (data == null || items == null)
                return items;
            if (CommsTabletLink.Merged)
            {
                // Merged, this mod's tablet is not in the save (the Comms Tablet loads it as its own), so the grant
                // is not spent: a player who later removes the Comms Tablet gets this one once, as a new player.
                data.RemoveData(GivenKey);
                return items;
            }
            if (data.GetBool(GivenKey) == true)
                return items;
            if (items.Any(i => i.item?.ItemPrefabName == EngineerTablet.PrefabName))
            {
                data.SetBool(GivenKey, true);
                return items;
            }
            var spec = create();
            if (spec?.ItemPrefabName != EngineerTablet.PrefabName)
                return items;
            data.SetBool(GivenKey, true);
            // GetStartingItems can return the asset's own list: keep it intact.
            return new List<StartingItems.StartingItem>(items)
            {
                new StartingItems.StartingItem { item = spec, preferredRelativeSlot = 0, backpackPriority = true, allowDuplicates = true },
            };
        }
    }

    /// <summary>
    /// The shops' teardown with the tablet gone first: the tablet's template
    /// lives until the game quits, and at quit Unity may destroy it before
    /// the shop controller, whose OnDestroy then reads the destroyed item
    /// (a NullReferenceException in the log, self-test run 31). Entries
    /// whose item is already destroyed are dropped before it looks.
    /// </summary>
    [HarmonyPatch(typeof(GlobalShopController), "OnDestroy")]
    internal static class EngineerTabletShopTeardown
    {
        private static void Prefix(GlobalShopController __instance)
        {
            try
            {
                __instance.shopItemsData?.RemoveAll(d => d == null || d.item == null);
            }
            catch (Exception e)
            {
                Main.Warn($"the shops' items couldn't be checked at teardown: {e.GetBaseException().Message}");
            }
        }
    }

    /// <summary>
    /// Sells the tablet beside the stock clinometer, at its price (Realistic
    /// Firing's FiringTabletShop); neither in the shops' items nor on their
    /// shelves while the Comms Tablet merges this mod.
    /// </summary>
    [HarmonyPatch(typeof(GlobalShopController), "Awake")]
    internal static class EngineerTabletShop
    {
        private static void Prefix(GlobalShopController __instance)
        {
            try
            {
                if (CommsTabletLink.Merged)
                    return;
                var spec = EngineerTablet.Template?.GetComponent<InventoryItemSpec>();
                if (spec == null || __instance.shopItemsData == null || __instance.shopItemsData.Any(d => d?.item == spec))
                    return;
                var source = __instance.shopItemsData.FirstOrDefault(d => d?.item?.ItemPrefabName == EngineerTablet.SourcePrefab);
                if (source == null)
                    return;
                var data = new ShopItemData
                {
                    item = spec,
                    basePrice = source.basePrice,
                    isGlobal = source.isGlobal,
                    careerOnly = source.careerOnly,
                    soldOnlyAt = new List<Shop>(source.soldOnlyAt ?? new List<Shop>()),
                };
                var amount = AccessTools.Field(typeof(ShopItemData), "amount");
                amount.SetValue(data, amount.GetValue(source));
                __instance.shopItemsData.Add(data);
            }
            catch (Exception e)
            {
                Main.Error("Couldn't add the engineer's tablet to the shops' items", e);
            }
        }

        private static readonly AccessTools.FieldRef<CashRegisterWithModules, DV.CabControls.ButtonBase> BuyButton =
            AccessTools.FieldRefAccess<CashRegisterWithModules, DV.CabControls.ButtonBase>("buyButton");

        /// <summary>A register already on hooked up its shelf tags before this one came; it hears it now (Realistic Firing's FireIronShop.HearTag).</summary>
        private static void HearTag(CashRegisterWithModules register, CashRegisterModule tag)
        {
            if (!register.isActiveAndEnabled || BuyButton(register) == null)
                return;
            var heard = AccessTools.MethodDelegate<Action>(AccessTools.Method(typeof(CashRegisterWithModules), "OnUnitsToBuyChanged"), register);
            tag.OnUnitsToBuyChanged -= heard;
            tag.OnUnitsToBuyChanged += heard;
        }

        private static void Postfix(GlobalShopController __instance)
        {
            try
            {
                if (CommsTabletLink.Merged)
                {
                    Main.Log("Engineer's tablet not sold: the Comms Tablet's Engineers app takes its place");
                    return;
                }
                var spec = EngineerTablet.Template?.GetComponent<InventoryItemSpec>();
                if (spec == null || __instance.globalShopList == null)
                    return;
                var count = 0;
                foreach (var shop in __instance.globalShopList)
                {
                    if (shop == null || shop.scanItemResourceModules == null || shop.scanItemResourceModules.Any(m => m?.sellingItemSpec == spec))
                        continue;
                    var source = shop.scanItemResourceModules.FirstOrDefault(m => m?.sellingItemSpec?.ItemPrefabName == EngineerTablet.SourcePrefab);
                    if (source == null)
                        continue;
                    var holder = new GameObject("[AIEngineer tablet shelf template]");
                    holder.SetActive(false);
                    var copy = Object.Instantiate(source.gameObject, holder.transform, false);
                    copy.name = "AIEngineer_Tablet_ShelfItem";
                    var tag = copy.GetComponent<ScanItemCashRegisterModule>();
                    tag.sellingItemSpec = spec;
                    var display = copy.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "gadget_clinometer_base");
                    if (display != null)
                    {
                        foreach (var renderer in display.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                        EngineerTabletBody.Build(display).localScale = Vector3.one * 0.42f;
                    }
                    copy.transform.SetParent(source.transform.parent, false);
                    // Beside the clinometer's shelf spot, so another mod's tablet there isn't hidden.
                    copy.transform.localPosition = source.transform.localPosition + new Vector3(0f, 0f, 0.25f);
                    copy.transform.localRotation = source.transform.localRotation;
                    Object.Destroy(holder);
                    shop.scanItemResourceModules = shop.scanItemResourceModules.Append(tag).ToArray();
                    if (shop.cashRegister != null && shop.cashRegister.registerModules != null)
                    {
                        shop.cashRegister.registerModules = shop.cashRegister.registerModules.Append(tag).ToArray();
                        HearTag(shop.cashRegister, tag);
                    }
                    tag.ItemPurchased += __instance.AddItemToInstantiationQueue;
                    count++;
                }
                Main.Log($"Engineer's tablet for sale beside the clinometer in {count} shops");
            }
            catch (Exception e)
            {
                Main.Error("Couldn't put the engineer's tablet on the shops' shelves", e);
            }
        }
    }
}
