using System.Collections;
using HarmonyLib;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(Game), "PerformForcedPlacementCO")]
    public static class ClientSkipForcedFirstPlacementPatches
    {
        public static bool Prefix(BuildingDefinition bDef, bool skipWhileCheck, ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || mod.Session.IsHost)
                return true;

            __result = ClientWaitOnly();
            return false;
        }

        private static IEnumerator ClientWaitOnly()
        {

            yield break;
        }
    }

    [HarmonyPatch(typeof(BuildingPlacementHandler), "SelectBuilding")]
    public static class ClientBlockFirstSelectBuildingPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || mod.Session.IsHost)
                return true;

            try
            {
                if (Game.I != null && Game.I.isPerformingFirstPlacement && !Game.I.hasPlacedFirst)
                    return false;
            }
            catch
            {
            }

            return true;
        }
    }
}
