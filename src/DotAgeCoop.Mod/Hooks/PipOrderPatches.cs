using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using DotAgeCoop.Sync;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(ParametricAction_BuildingActionButton), "Action")]
    public static class ParametricSideActionCoopPatches
    {
        public static bool Prefix(ParametricAction_BuildingActionButton __instance, ActionContext context, ref bool __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (mod.PipOrders != null && mod.PipOrders.ApplyingRemote)
                return true;
            if (mod.Bootstrap != null && mod.Bootstrap.IsJoining)
            {
                __result = false;
                return false;
            }

            try
            {
                if (AdvancedInteractionHandler.GlobalizeActionOn &&
                    ActionUtils.IsGlobalizable(__instance.action))
                    return true;
            }
            catch
            {
            }

            if (Game.I == null || Game.I.buildingsHandler == null)
            {
                __result = false;
                return false;
            }

            Building building = context != null ? context.building : null;
            ActionLogic logic = Game.I.buildingsHandler.GetActionLogic(
                __instance.action, __instance.param, building);
            if (logic == null)
            {
                __result = false;
                return false;
            }

            if (context != null)
                logic.SetTargets(context.terrain, context.building, context.creature, context.target);

            __result = mod.PipOrders.SendClientContextAction(logic, fromSideAction: true);
            return false;
        }
    }

    [HarmonyPatch(typeof(Building), "TakeWorker")]
    public static class PipTakeWorkerPatches
    {
        public static void Prefix(ref Worker alreadyChosenPip)
        {
            if (!PipOrderForce.Active)
                return;
            if (alreadyChosenPip != null)
                return;

            int uid;
            if (!PipOrderForce.TryTakeNextUid(out uid))
                return;

            Worker forced = ResolveWorker(uid);
            if (forced != null)
                alreadyChosenPip = forced;
            else
                MelonLogger.Warning("[DotAgeCoop] Forced worker UID " + uid + " not found");
        }

        private static Worker ResolveWorker(int uid)
        {
            try
            {
                if (Game.I == null || Game.I.pipsHandler == null)
                    return null;
                return Game.I.pipsHandler.GetPipByUID(uid);
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(ActionLogic), "TryPerform")]
    public static class PipOrderTryPerformPatches
    {

        [ThreadStatic]
        private static Building _workingBuildingBefore;

        [ThreadStatic]
        private static bool _wasCancelPath;

        public static bool Prefix(ActionLogic __instance, bool fromSideAction, ActionContext context)
        {
            _workingBuildingBefore = null;
            _wasCancelPath = false;
            try
            {

                if (context != null)
                {
                    __instance.building = context.building;
                    __instance.terrain = context.terrain;
                    __instance.creature = context.creature;
                    __instance.ge = context.target;
                    if (context.target is Creature)
                        __instance.defType = GameDefinitionType.CreatureDefinition;
                    else if (__instance.building != null)
                        __instance.defType = GameDefinitionType.BuildingDefinition;
                    else if (context.target is MapTerrain)
                        __instance.defType = GameDefinitionType.TerrainDefinition;
                }

                if (__instance.creature is Worker worker && worker.HasWorkingBuilding())
                    _workingBuildingBefore = worker.WorkingBuilding;
                else if (__instance.building != null)
                    _workingBuildingBefore = __instance.building;

                if (__instance.building != null &&
                    __instance.defType == GameDefinitionType.BuildingDefinition &&
                    __instance.building.ActionChoice != null &&
                    __instance.building.ActionChoice.action == __instance.Action)
                {
                    _wasCancelPath = true;
                }
            }
            catch
            {
            }

            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return true;
            return mod.PipOrders.OnLocalTryPerform(__instance, fromSideAction);
        }

        public static void Postfix(ActionLogic __instance, bool fromSideAction, bool __result)
        {
            Building affected = _workingBuildingBefore;
            bool wasCancel = _wasCancelPath;
            _workingBuildingBefore = null;
            _wasCancelPath = false;
            if (!__result)
                return;
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostTryPerformSuccess(__instance, fromSideAction, affected, wasCancel);
        }
    }

    [HarmonyPatch(typeof(Building), "Action_ToggleActivation")]
    public static class PipToggleActivationPatches
    {
        public static bool Prefix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return true;
            return mod.PipOrders.OnLocalToggleActivation(__instance);
        }

        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;

            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "toggle");
        }
    }

    [HarmonyPatch(typeof(Building), "ActOnWhileWIP")]
    public static class PipActOnWhileWipPatches
    {
        public static bool Prefix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return true;
            return mod.PipOrders.OnLocalWipInteraction(__instance);
        }

        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "wip-interact");
        }
    }

    [HarmonyPatch(typeof(Building), "DeactivateBuildAction")]
    public static class PipDeactivateBuildPatches
    {
        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;

            mod.PipOrders.OnBuildDeactivated(__instance);
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "deactivate-build");
        }
    }

    [HarmonyPatch(typeof(Building), "PerformBasicAction")]
    public static class PipPerformBasicActionPatches
    {
        public static void Postfix(Building __instance, bool __result)
        {
            if (!__result || __instance == null)
                return;
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;

            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "book-workers");
        }
    }

    [HarmonyPatch(typeof(Building), "AddBookedWorkers")]
    public static class PipAddBookedWorkersPatches
    {
        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "booked-ready");
        }
    }

    [HarmonyPatch(typeof(Building), "FinalActionPostAnimation")]
    public static class PipFinalActionPostAnimationPatches
    {
        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "post-anim");
        }
    }

    [HarmonyPatch(typeof(Building), "InitialiseCorrectProductionExchange")]
    public static class PipInitProductionExchangePatches
    {
        public static bool Prefix()
        {
            return PipExchangeGate.AllowClientLocalExchangeChange();
        }
    }

    [HarmonyPatch(typeof(Building), "AdvanceToNextAvailableProductionExchange")]
    public static class PipAdvanceProductionExchangePatches
    {
        public static bool Prefix(Building __instance, bool checkCurrentToo, bool checkResourcesNow, int direction, bool onlyIfSimilar)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (mod.PipOrders != null && mod.PipOrders.ApplyingRemote)
                return true;

            if (mod.PipOrders != null)
                mod.PipOrders.OnClientWantProductionExchange(__instance, direction, absoluteExchangeId: -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(Building), "ForceProductionExchange")]
    public static class PipForceProductionExchangePatches
    {
        public static bool Prefix(Building __instance, int exchangeId)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (mod.PipOrders != null && mod.PipOrders.ApplyingRemote)
                return true;

            if (mod.PipOrders != null)
                mod.PipOrders.OnClientWantProductionExchange(__instance, direction: 1, absoluteExchangeId: exchangeId);
            return false;
        }
    }

    [HarmonyPatch(typeof(ExchangeSelectionUI), "OnConfirm")]
    public static class ExchangeSelectionConfirmPatches
    {
        private static readonly FieldInfo BuildingField =
            AccessTools.Field(typeof(ExchangeSelectionUI), "building");
        private static readonly FieldInfo SelectedOptionField =
            AccessTools.Field(typeof(GenericSelectionUI), "selectedOptionIndex");

        public static bool Prefix(ExchangeSelectionUI __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (mod.PipOrders == null)
                return true;
            if (mod.PipOrders.ApplyingRemote)
                return true;

            Building building = null;
            int exchangeId = -1;
            try
            {
                if (BuildingField != null)
                    building = BuildingField.GetValue(__instance) as Building;
                if (SelectedOptionField != null)
                    exchangeId = (int)SelectedOptionField.GetValue(__instance);
            }
            catch { }

            if (building == null || exchangeId < 0)
                return true;

            mod.PipOrders.OnClientWantProductionExchange(building, direction: 1, absoluteExchangeId: exchangeId);
            return false;
        }
    }

    [HarmonyPatch(typeof(ChangeProduction_BuildingActionButton), "Action")]
    public static class ChangeProductionActionPatches
    {
        private static readonly FieldInfo DirField =
            AccessTools.Field(typeof(ChangeProduction_BuildingActionButton), "dir");

        public static bool Prefix(ChangeProduction_BuildingActionButton __instance, ActionContext context, ref bool __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (mod.PipOrders == null || mod.PipOrders.ApplyingRemote)
                return true;

            int dir = 0;
            try
            {
                if (DirField != null)
                    dir = (int)DirField.GetValue(__instance);
            }
            catch { }

            bool cyclingHotkey = false;
            try { cyclingHotkey = AdvancedInteractionHandler.ForceActionOn; }
            catch { }

            if (dir == 0 && !cyclingHotkey)
                return true;

            Building building = null;
            try
            {
                if (context != null)
                    building = context.building;
            }
            catch { }

            if (building == null)
            {
                __result = false;
                return false;
            }

            int cycleDir = dir != 0 ? dir : 1;
            mod.PipOrders.OnClientWantProductionExchange(building, cycleDir, absoluteExchangeId: -1);
            __result = true;
            return false;
        }
    }

    public static class PipExchangeGate
    {
        public static bool AllowClientLocalExchangeChange()
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
    }

    [HarmonyPatch(typeof(Building), "AutoAssignWorkers")]
    public static class PipAutoAssignWorkersPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.PipOrders != null && mod.PipOrders.ApplyingRemote)
                return true;
            if (mod.Session.IsHost)
                return true;

            try
            {
                if (Game.I != null && Game.I.IsAnyPassTurnTime())
                    return true;
            }
            catch
            {
            }

            if (mod.TurnSync != null && mod.TurnSync.PassTurnInFlight)
                return true;

            return false;
        }
    }

    [HarmonyPatch(typeof(Building), "ManualApplyActivation")]
    public static class PipManualActivatePatches
    {
        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "manual-activate");
        }
    }

    [HarmonyPatch(typeof(Building), "ApplyDeactivation")]
    public static class PipApplyDeactivatePatches
    {
        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "deactivate");
        }
    }

    [HarmonyPatch(typeof(Building), "DirectAddWorker")]
    public static class PipDirectAddWorkerPatches
    {
        public static bool Prefix(Building __instance, ActionAssignment action, Worker prechosenWorker, bool getElder, bool atLoad, bool fromPicking)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return true;
            return mod.PipOrders.OnLocalDirectAddWorker(__instance, action, prechosenWorker, atLoad, fromPicking);
        }

        public static void Postfix(Building __instance, bool __result)
        {
            if (!__result || __instance == null)
                return;
            if (__instance.workerPips == null || __instance.workerPips.Count == 0)
                return;
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "roster-change");
        }
    }

    [HarmonyPatch(typeof(Building), "RemoveWorker")]
    public static class PipRemoveWorkerPatches
    {
        public static void Postfix(Building __instance, bool __result)
        {
            if (!__result || __instance == null)
                return;
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "roster-change");
        }
    }

    [HarmonyPatch(typeof(Building), "DirectAddWorkerAndActivate")]
    public static class PipDirectAddAndActivatePatches
    {
        public static bool Prefix(Building __instance, ActionAssignment action, Worker prechosenWorker, bool fromPicking)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return true;
            return mod.PipOrders.OnLocalDirectAddAndActivate(__instance, action, prechosenWorker, fromPicking);
        }

        public static void Postfix(Building __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostBuildingWorkChanged(__instance, "direct-add-activate");
        }
    }

    [HarmonyPatch(typeof(Building), "TryRollbackCurrentAction")]
    public static class PipRollbackPatches
    {
        [ThreadStatic]
        private static WorkAction _actionBeforeRollback;

        [ThreadStatic]
        private static int _paramBeforeRollback;

        public static bool Prefix(Building __instance, bool fromSideAction, ref bool __result)
        {
            _actionBeforeRollback = WorkAction.NONE;
            _paramBeforeRollback = 0;
            try
            {
                if (__instance != null && __instance.ActionChoice != null &&
                    __instance.ActionChoice.HasAction())
                {
                    _actionBeforeRollback = __instance.ActionChoice.action;
                    _paramBeforeRollback = __instance.ActionChoice.param;
                }
            }
            catch
            {
            }

            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return true;

            bool result;
            if (mod.PipOrders.OnLocalTryRollback(__instance, fromSideAction, out result))
            {
                __result = result;
                return false;
            }

            return true;
        }

        public static void Postfix(Building __instance, bool fromSideAction, bool __result)
        {
            WorkAction captured = _actionBeforeRollback;
            int capturedParam = _paramBeforeRollback;
            _actionBeforeRollback = WorkAction.NONE;
            _paramBeforeRollback = 0;

            if (!__result)
                return;
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.PipOrders == null)
                return;
            mod.PipOrders.OnHostRollback(__instance, fromSideAction, captured, capturedParam);
        }
    }
}
