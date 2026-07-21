using HarmonyLib;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(CursorLocker), "ClampCursor")]
    public static class CursorClampPatches
    {
        public static bool Prefix()
        {
            return false;
        }
    }

    [HarmonyPatch(typeof(CursorLocker), "Update")]
    public static class CursorLockerUpdatePatches
    {
        public static bool Prefix(CursorLocker __instance)
        {
            try
            {
                if (OConfig.Instance != null)
                    OConfig.Instance.clampCursor = false;

                if (__instance.IsBound)
                    __instance.IsBound = false;
                else
                    CursorLocker.ReleaseCursor();
            }
            catch
            {
            }

            return false;
        }
    }
}
