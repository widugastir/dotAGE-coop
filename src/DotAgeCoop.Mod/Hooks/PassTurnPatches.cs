using System.Collections;
using HarmonyLib;
using DotAgeCoop.Sync;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(Game), "GoToNextDay")]
    public static class GoToNextDayPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.TurnSync == null)
                return true;

            return mod.TurnSync.ShouldAllowGoToNextDay();
        }
    }

    [HarmonyPatch(typeof(Game), "ActionGoToNextDay")]
    public static class ActionGoToNextDayPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.TurnSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (!mod.Session.HasCoopPartner)
                return true;

            if (mod.TurnSync.PassTurnExecuteInFlight)
                return true;
            if (mod.TurnSync.ShouldBlockPassTurnButton())
                return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(PassTurnController), "PassTurn")]
    public static class PassTurnPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.TurnSync == null)
                return true;

            return mod.TurnSync.ShouldAllowPassTurn();
        }
    }

    [HarmonyPatch(typeof(PassTurnController), "CompletePassTurn")]
    public static class CompletePassTurnPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.TurnSync != null)
                mod.TurnSync.OnLocalPassTurnMidpoint();
        }
    }

    [HarmonyPatch(typeof(PassTurnController), "PassTurnCO")]
    public static class PassTurnCOPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.TurnSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;

            MelonLoader.MelonLogger.Msg("[DotAgeCoop] [Turn] Wrapping PassTurnCO (morning barrier)");
            IEnumerator core = __result;
            if (mod.EventSync != null)
                core = mod.EventSync.WrapPassTurnEventBarriers(core);
            __result = mod.TurnSync.WrapPassTurnCoroutine(core);
        }
    }

    [HarmonyPatch(typeof(Game), "StartPhaseNightTime")]
    public static class StartPhaseNightTimePatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;

            if (mod.PipOrders != null)
                mod.PipOrders.CancelRetetherCoroutine();
        }
    }

    [HarmonyPatch(typeof(RadialBuildingActionMenu), "SetTarget")]
    public static class RadialBuildingActionMenuSetTargetPatches
    {
        public static bool Prefix(RadialBuildingActionMenu __instance, GameElement ge)
        {
            if (ge == null)
                return true;

            ModMain mod = ModMain.Instance;
            if (mod == null || mod.TurnSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (!mod.Session.HasCoopPartner)
                return true;
            if (!CoopWorldGate.BlocksContext)
                return true;

            try
            {
                __instance.Hide();
            }
            catch
            {
            }
            return false;
        }
    }
}
