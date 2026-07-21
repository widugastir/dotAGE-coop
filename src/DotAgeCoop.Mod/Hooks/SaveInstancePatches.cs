using HarmonyLib;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(LocalDataManager), "LoadSlot")]
    public static class SaveInstanceLoadSlotPatch
    {
        public static void Prefix(ref int slot)
        {
            if (SaveInstanceConfig.TryRewriteSlot(ref slot))
            {
                MelonLoader.MelonLogger.Msg(
                    "[DotAgeCoop] LoadSlot → forced profile " + SaveInstanceConfig.Profile +
                    " (slot " + slot + ")");
            }
        }
    }

    [HarmonyPatch(typeof(LocalDataManager), "LoadSlotCO")]
    public static class SaveInstanceLoadSlotCOPatch
    {
        public static void Prefix(ref int slot)
        {
            SaveInstanceConfig.TryRewriteSlot(ref slot);
        }
    }

    [HarmonyPatch(typeof(LocalDataManager), "SaveSerializedData")]
    public static class SaveInstanceSaveLocalPatch
    {
        public static void Prefix(LocalDataManager __instance)
        {
            if (!SaveInstanceConfig.Enabled || __instance == null || __instance.SaveData == null)
                return;

            __instance.SaveData.lastSlot = SaveInstanceConfig.Slot;
        }
    }
}
