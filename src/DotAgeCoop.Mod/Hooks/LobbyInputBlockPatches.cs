using HarmonyLib;
using DotAgeCoop.UI;
using UnityEngine;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(Input))]
    public static class LobbyInputBlockPatches
    {
        private static bool ShouldBlock()
        {
            return LobbyOverlay.BlocksGameInput
                || DotAgeCoop.Sync.TurnSyncService.BlocksPlayInput
                || DotAgeCoop.Sync.GameBootstrapService.BlocksPlayInput
                || DotAgeCoop.Sync.HardSyncService.BlocksPlayInput;
        }

        [HarmonyPrefix]
        [HarmonyPatch("GetMouseButton", typeof(int))]
        public static bool GetMouseButtonPrefix(int button, ref bool __result)
        {
            if (!ShouldBlock() || button < 0 || button > 2)
                return true;
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("GetMouseButtonDown", typeof(int))]
        public static bool GetMouseButtonDownPrefix(int button, ref bool __result)
        {
            if (!ShouldBlock() || button < 0 || button > 2)
                return true;
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("GetMouseButtonUp", typeof(int))]
        public static bool GetMouseButtonUpPrefix(int button, ref bool __result)
        {
            if (!ShouldBlock() || button < 0 || button > 2)
                return true;
            __result = false;
            return false;
        }
    }
}
