using System;
using HarmonyLib;
using DotAgeCoop.Sync;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(BuildingPlacementHandler), "TryConfirmPosition")]
    public static class BuildingConfirmPositionPatches
    {
        public static bool Prefix(BuildingPlacementHandler __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;

            if (CoopWorldGate.Active)
                return false;

            if (mod.Session.IsHost)
                return true;

            if (mod.GameSync == null)
                return true;

            return mod.GameSync.OnClientTryConfirmPosition(__instance);
        }
    }

    [HarmonyPatch(typeof(Building), "_ConfirmPlacementAction")]
    public static class BuildingConfirmPlacementPatches
    {
        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.GameSync == null || mod.GameSync.ApplyingRemote)
                return;

            mod.GameSync.OnHostBuildingConfirmed(__instance);

            if (mod.FirstPlacement != null)
                mod.FirstPlacement.OnHostConfirmedPlacement(__instance);
        }
    }

    [HarmonyPatch(typeof(Building), "FinalDestroy")]
    public static class BuildingFinalDestroyPatches
    {
        public static void Postfix(Building __instance, bool clearForReset)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.GameSync == null || mod.GameSync.ApplyingRemote)
                return;

            mod.GameSync.OnHostBuildingDestroyed(__instance, clearForReset);
        }
    }

    [HarmonyPatch(typeof(ResourcesHandler), "ForceTo")]
    public static class ResourcesForceToPatches
    {
        public static void Postfix()
        {
            MarkResourcesDirty();
        }

        private static void MarkResourcesDirty()
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.GameSync != null)
                mod.GameSync.MarkResourcesDirty();
        }
    }

    [HarmonyPatch(typeof(ResourcesHandler), "Add", new[] { typeof(ResourceType), typeof(int), typeof(bool) })]
    public static class ResourcesAddPatches
    {
        public static void Postfix(ResourceType type, int value, bool forceImmediate)
        {
            LogRes("local+", type, value);
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.GameSync != null)
                mod.GameSync.MarkResourcesDirty();
        }

        private static void LogRes(string source, ResourceType type, int delta)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return;
            if (mod.GameSync != null && mod.GameSync.ApplyingRemote)
                return;
            if (delta == 0 || type == null)
                return;
            EconDebugLog.ResourceDelta(source, type, delta, EconDebugLog.CurrentResourceStock(type));
        }
    }

    [HarmonyPatch(typeof(ResourcesHandler), "Remove", new[] { typeof(ResourceType), typeof(int), typeof(bool) })]
    public static class ResourcesRemovePatches
    {
        public static void Postfix(ResourceType type, int value, bool fixActualRemoval)
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.Session != null && mod.Session.Active &&
                (mod.GameSync == null || !mod.GameSync.ApplyingRemote) &&
                type != null && value != 0)
            {
                EconDebugLog.ResourceDelta("local-", type, -Math.Abs(value),
                    EconDebugLog.CurrentResourceStock(type));
            }

            if (mod != null && mod.GameSync != null)
                mod.GameSync.MarkResourcesDirty();
        }
    }

    [HarmonyPatch(typeof(BuildingsHandler), "RefreshFood")]
    public static class RefreshFoodPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.GameSync != null && mod.GameSync.ApplyingFood)
                return true;
            if (mod.Session.IsHost)
                return true;
            return false;
        }

        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.GameSync == null || mod.GameSync.ApplyingFood || mod.GameSync.ApplyingRemote)
                return;

            mod.GameSync.BroadcastFoodSnapshot();
        }
    }

    [HarmonyPatch(typeof(ResourceType), "set_DisabledAsFood_Pips")]
    public static class FoodBanPipsPatches
    {
        public static void Postfix(ResourceType __instance)
        {
            NotifyFoodBanChanged(__instance);
        }

        private static void NotifyFoodBanChanged(ResourceType type)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || mod.GameSync == null)
                return;
            if (mod.GameSync.ApplyingFoodBans || mod.GameSync.ApplyingFood)
                return;

            if (mod.Session.IsHost)
                mod.GameSync.OnHostFoodBanChanged(type);
            else
                mod.GameSync.SendFoodBanIntent(type);
        }
    }

    [HarmonyPatch(typeof(ResourceType), "set_DisabledAsFood_Creatures")]
    public static class FoodBanCreaturesPatches
    {
        public static void Postfix(ResourceType __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || mod.GameSync == null)
                return;
            if (mod.GameSync.ApplyingFoodBans || mod.GameSync.ApplyingFood)
                return;

            if (mod.Session.IsHost)
                mod.GameSync.OnHostFoodBanChanged(__instance);
            else
                mod.GameSync.SendFoodBanIntent(__instance);
        }
    }
}
