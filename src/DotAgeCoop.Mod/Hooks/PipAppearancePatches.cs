using HarmonyLib;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(PipsHandler), "Add")]
    public static class PipAppearanceAddPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.PipAppearance != null)
                mod.PipAppearance.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(Pipo), "ForceColor")]
    public static class PipAppearanceForceColorPatches
    {
        public static void Postfix()
        {
            MarkDirtyOnHost();
        }

        private static void MarkDirtyOnHost()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.PipAppearance != null)
                mod.PipAppearance.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(BuildingDwelling), "Complete_Growth")]
    public static class DwellingCompleteGrowthPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (mod.PipOrders != null && mod.PipOrders.ApplyingRemote)
                return true;

            return false;
        }

        public static void Postfix(BuildingDwelling __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.PipAppearance != null)
            {
                mod.PipAppearance.MarkDirty();
                mod.PipAppearance.BroadcastSnapshotImmediate();
            }
            if (mod.PipOrders != null)
                mod.PipOrders.OnHostBuildingWorkChanged(__instance, "complete-growth");
        }
    }
}
