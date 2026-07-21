using HarmonyLib;
using MelonLoader;
using DotAgeCoop.Sync;

namespace DotAgeCoop.Hooks
{

    internal static class TurnInputGate
    {
        public static bool BlocksWorld()
        {
            return CoopWorldGate.Active;
        }

        public static bool BlocksResearch()
        {
            return CoopWorldGate.Active;
        }

        public static bool BlocksSelection()
        {
            return CoopWorldGate.Active;
        }
    }

    [HarmonyPatch(typeof(BuildingPlacementHandler), "UpdatePipHovering")]
    public static class BlockPipHoveringDuringTurnGatePatches
    {
        public static bool Prefix()
        {
            return !TurnInputGate.BlocksSelection();
        }
    }

    [HarmonyPatch(typeof(ResearchTree), "ShowTree")]
    public static class BlockResearchShowTreePatches
    {
        public static bool Prefix()
        {
            if (!TurnInputGate.BlocksResearch())
                return true;
            MelonLogger.Msg("[DotAgeCoop] Research tree blocked — coop gate active");
            return false;
        }
    }

    [HarmonyPatch(typeof(OpenResearchTreeButton), "PerformLogic")]
    public static class BlockOpenResearchTreeButtonPatches
    {
        public static bool Prefix()
        {
            if (!TurnInputGate.BlocksResearch())
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(BuildingPlacementHandler), "SelectBuilding")]
    public static class BlockSelectBuildingDuringTurnGatePatches
    {
        public static bool Prefix()
        {
            if (!TurnInputGate.BlocksWorld())
                return true;
            MelonLogger.Msg("[DotAgeCoop] SelectBuilding blocked — coop gate active");
            return false;
        }
    }
}
