using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using DotAgeCoop.Sync;
using UnityEngine;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(EventController), "CheckForNewEvents")]
    public static class CheckForNewEventsPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;
            if (mod.Session.IsHost)
                mod.EventSync.OnHostCheckForNewEventsEnd();
            else
                mod.EventSync.OnClientCheckForNewEventsEnd();
        }
    }

    [HarmonyPatch(typeof(EventController), "CheckRNDMinorPrediction")]
    public static class CheckRNDMinorPredictionPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            return !mod.EventSync.ShouldBlockClientEventRng();
        }
    }

    [HarmonyPatch(typeof(EventController), "CheckReckoningPredictions")]
    public static class CheckReckoningPredictionsPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            return !mod.EventSync.ShouldBlockClientEventRng();
        }
    }

    [HarmonyPatch(typeof(EventController), "ChooseNewEventForPrediction")]
    public static class ChooseNewEventForPredictionPatches
    {
        public static bool Prefix(EventPrediction eventPrediction, ref EventDefinition __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return true;
            EventDefinition forced;
            if (mod.EventSync.TryForceEventDef(out forced) && forced != null)
            {
                __result = forced;
                return false;
            }
            if (mod.EventSync.ShouldBlockClientEventRng())
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EventController), "ReplaceCurrentEventWith")]
    public static class ReplaceCurrentEventWithPatches
    {
        public static void Postfix(EventDefinition ev)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return;
            mod.EventSync.OnHostEventReplaced(ev);
        }
    }

    [HarmonyPatch(typeof(PassTurnController), "ChooseBoons")]
    public static class ChooseBoonsPatches
    {
        public static void Postfix(List<EventDefinition> __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return;
            mod.EventSync.OnHostBoonsChosen(__result);
        }
    }

    [HarmonyPatch(typeof(PassTurnController), "TriggerBoon")]
    public static class TriggerBoonPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner || mod.Session.IsHost)
                return;
            __result = ClientWaitBoons(__result, mod.EventSync);
        }

        private static IEnumerator ClientWaitBoons(IEnumerator original, EventSyncService sync)
        {
            yield return sync.WaitForBoonOffersCO(60f);
            List<EventDefinition> offers;
            if (sync.TryGetBoonOffers(out offers) && offers != null && offers.Count > 0)
                SteerChoiceEventEffect.PossibleEvents = offers;
            while (original.MoveNext())
                yield return original.Current;
        }
    }

    [HarmonyPatch(typeof(PoseChoiceEventEffect), "PerformForChoice")]
    public static class PerformForChoicePatches
    {
        public static bool Prefix(PoseChoiceEventEffect __instance, int pathIndex, bool skipChoice)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return true;
            if (!mod.EventSync.ShouldBlockClientChoice())
                return true;
            return false;
        }

        public static void Postfix(PoseChoiceEventEffect __instance, int pathIndex, bool skipChoice)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return;
            mod.EventSync.OnHostChoiceMade(__instance, pathIndex, skipChoice);
        }
    }

    [HarmonyPatch(typeof(PoseChoiceEventEffect), "DoRerollBoons")]
    public static class ClientBlockBoonRerollPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null)
                return true;
            return !mod.EventSync.ShouldBlockClientBoonReroll();
        }
    }

    [HarmonyPatch(typeof(PredictionBattleHandler), "CheckIfEventFulfilled")]
    public static class CheckIfEventFulfilledPatches
    {
        public static bool Prefix(ref bool __result, ref float normalizedRoll)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return true;

            bool fulfilled;
            float roll;
            if (mod.EventSync.TryGetArrivalResult(out fulfilled, out roll))
            {
                __result = fulfilled;
                normalizedRoll = roll;
                return false;
            }

            MelonLogger.Warning("[DotAgeCoop] Client CheckIfEventFulfilled without ArrivalCommit — forcing host-pending no-op");
            __result = true;
            normalizedRoll = 0.5f;
            return false;
        }

        public static void Postfix(bool __result, float normalizedRoll)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.EventSync.ApplyingRemote)
                return;
            mod.EventSync.OnHostArrivalRoll(__result, normalizedRoll);
        }
    }

    [HarmonyPatch(typeof(PredictionBattleHandler), "PredictionArrivalCO")]
    public static class PredictionArrivalCOPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;
            __result = WrapArrival(__result, mod);
        }

        private static IEnumerator WrapArrival(IEnumerator original, ModMain mod)
        {
            EventSyncService sync = mod.EventSync;
            if (mod.Session.IsHost)
            {

                sync.TryReleaseArrivalIfNoBattle();
                while (original.MoveNext())
                    yield return original.Current;

                sync.EnsureArrivalCommitReleased(true, "arrival-end");
            }
            else
            {
                yield return sync.WaitArrivalCommitCO(60f);
                while (original.MoveNext())
                    yield return original.Current;
            }
        }
    }

    [HarmonyPatch(typeof(PredictionBattleHandler), "ReusingScaleBattleCO")]
    public static class ReusingScaleBattleCOPatches
    {
        private static readonly FieldInfo RollConfirmField =
            AccessTools.Field(typeof(PredictionBattleHandler), "rollConfirm");

        public static void Postfix(PredictionBattleHandler __instance, ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;
            __result = WrapBattle(__instance, __result, mod);
        }

        private static IEnumerator WrapBattle(
            PredictionBattleHandler self, IEnumerator original, ModMain mod)
        {
            EventSyncService sync = mod.EventSync;
            bool host = mod.Session.IsHost;
            bool sawConfirm = false;
            bool sentAdvance = false;

            bool advancedFromHost = false;

            while (original.MoveNext())
            {
                object cur = original.Current;

                try
                {
                    if (RollConfirmField != null)
                    {
                        if (host)
                        {
                            bool confirm = (bool)RollConfirmField.GetValue(self);
                            if (confirm && !sawConfirm && !sentAdvance)
                            {
                                sync.HostBroadcastRollAdvance();
                                sentAdvance = true;
                            }
                            sawConfirm = confirm;
                        }
                        else
                        {

                            NeutraleClientRollButton(self);

                            if (sync.ConsumeRollAdvance())
                                advancedFromHost = true;

                            if (advancedFromHost)
                            {
                                RollConfirmField.SetValue(self, true);
                            }
                            else if ((bool)RollConfirmField.GetValue(self))
                            {

                                RollConfirmField.SetValue(self, false);
                            }
                        }
                    }
                }
                catch { }

                yield return cur;
            }
        }

        private static void NeutraleClientRollButton(PredictionBattleHandler self)
        {
            try
            {
                ActionButton btn = self.rollButton;
                if (btn == null)
                    return;
                btn.action = delegate { };
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PassTurnController), "PerformEventCO")]
    public static class PerformEventCOPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;
            __result = WrapPerform(__result, mod.EventSync);
        }

        private static IEnumerator WrapPerform(IEnumerator original, EventSyncService sync)
        {
            yield return sync.WaitBeforePerformEventCO();
            while (original.MoveNext())
                yield return original.Current;
        }
    }

    [HarmonyPatch(typeof(RandomPipoEventSelector), "SelectInternal")]
    public static class RandomPipoSelectPatches
    {
        public static void Postfix(ref List<Pipo> __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (mod.Session.IsHost)
                return;
            if (__result == null || __result.Count == 0)
                return;

            for (int i = 0; i < __result.Count; i++)
            {
                Pipo forced;
                if (!mod.EventSync.TryDequeueForcedPipo(out forced) || forced == null)
                    break;
                __result[i] = forced;
            }
        }
    }

    [HarmonyPatch(typeof(RandomCreatureEventSelector), "SelectInternal")]
    public static class RandomCreatureSelectPatches
    {
        public static void Postfix(ref List<Creature> __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (mod.Session.IsHost)
                return;
            if (__result == null || __result.Count == 0)
                return;

            for (int i = 0; i < __result.Count; i++)
            {
                Creature forced;
                if (!mod.EventSync.TryDequeueForcedCreature(out forced) || forced == null)
                    break;
                __result[i] = forced;
            }
        }
    }

    [HarmonyPatch(typeof(NewCreatureEventSelector), "SelectInternal")]
    public static class NewCreatureSelectPatches
    {
        public static void Prefix(NewCreatureEventSelector __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (mod.Session.IsHost || !mod.Session.HasCoopPartner)
                return;

            CreatureDefinition def;
            if (!mod.EventSync.TryDequeueForcedCreatureDef(out def) || def == null)
                return;
            __instance.creatureDefinition = def;
        }
    }

    [HarmonyPatch(typeof(RandomTerrainEventSelector), nameof(RandomTerrainEventSelector.Select))]
    public static class RandomTerrainSelectPatches
    {
        public static void Postfix(ref List<MapTerrain> __result)
        {
            ApplyForcedTerrains(ref __result);
        }

        internal static void ApplyForcedTerrains(ref List<MapTerrain> __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (mod.Session.IsHost || !mod.Session.HasCoopPartner)
                return;
            if (__result == null || __result.Count == 0)
                return;

            for (int i = 0; i < __result.Count; i++)
            {
                MapTerrain forced;
                if (!mod.EventSync.TryDequeueForcedMapTerrain(out forced) || forced == null)
                    break;
                __result[i] = forced;
            }
        }
    }

    [HarmonyPatch(typeof(GridTerrainEventSelector), nameof(GridTerrainEventSelector.Select))]
    public static class GridTerrainSelectPatches
    {
        public static void Postfix(ref List<MapTerrain> __result)
        {
            RandomTerrainSelectPatches.ApplyForcedTerrains(ref __result);
        }
    }

    [HarmonyPatch(typeof(AroundBuildingTerrainEventSelector), nameof(AroundBuildingTerrainEventSelector.Select))]
    public static class AroundBuildingTerrainSelectPatches
    {
        public static void Postfix(ref List<MapTerrain> __result)
        {
            RandomTerrainSelectPatches.ApplyForcedTerrains(ref __result);
        }
    }

    [HarmonyPatch(typeof(AdjacentToTerrainTerrainEventSelector), nameof(AdjacentToTerrainTerrainEventSelector.Select))]
    public static class AdjacentTerrainSelectPatches
    {
        public static void Postfix(ref List<MapTerrain> __result)
        {
            RandomTerrainSelectPatches.ApplyForcedTerrains(ref __result);
        }
    }

    [HarmonyPatch(typeof(SelectBuildingPipoEventSelector), "SelectInternal")]
    public static class SelectBuildingPipoSelectPatches
    {
        public static void Postfix(ref List<Pipo> __result)
        {
            RandomPipoSelectPatches.Postfix(ref __result);
        }
    }

    [HarmonyPatch(typeof(SelectTerrainPipoEventSelector), "SelectInternal")]
    public static class SelectTerrainPipoSelectPatches
    {
        public static void Postfix(ref List<Pipo> __result)
        {
            RandomPipoSelectPatches.Postfix(ref __result);
        }
    }

    [HarmonyPatch(typeof(WorkingInBuildingPipoEventSelector), "SelectInternal")]
    public static class WorkingInBuildingPipoSelectPatches
    {
        public static void Postfix(ref List<Pipo> __result)
        {
            RandomPipoSelectPatches.Postfix(ref __result);
        }
    }

    [HarmonyPatch(typeof(EventController), "ForceEventExecution",
        new Type[] { typeof(EventDefinition), typeof(bool) })]
    public static class ForceEventExecutionPatches
    {
        public static void Prefix(EventDefinition e)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.EventSync.ApplyingRemote || e == null)
                return;
            mod.EventSync.OnHostForceEvent(e);
        }
    }

    [HarmonyPatch(typeof(EventController), "StartExecutionEventCO")]
    public static class StartExecutionEventCOResourceSyncPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner)
                return;
            __result = mod.EventSync.WrapExecutionStageCO(__result);
        }
    }

    [HarmonyPatch(typeof(EventController), "ForceEventExecutionCO")]
    public static class ForceEventExecutionCOPatches
    {
        public static void Postfix(EventDefinition e, ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.HasCoopPartner || e == null)
                return;
            __result = WrapForce(e, __result, mod.EventSync, mod.Session.IsHost);
        }

        private static IEnumerator WrapForce(
            EventDefinition e, IEnumerator original, EventSyncService sync, bool host)
        {
            if (!host)
                yield return sync.WaitForceCommitCO(e.ID, 60f);
            yield return sync.WrapExecutionStageCO(original);
        }
    }

    [HarmonyPatch(typeof(GameRandom), nameof(GameRandom.Int), new Type[] { typeof(int), typeof(int), typeof(RandomID), typeof(int) })]
    public static class GameRandomIntStageTapePatches
    {
        public static bool Prefix(RandomID id, ref int __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return true;
            int value;
            if (sync.TryReplayEventRngInt(out value))
            {
                __result = value;
                return false;
            }
            return true;
        }

        public static void Postfix(RandomID id, int __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return;
            sync.RecordEventRngInt(__result);
        }

        private static EventSyncService GetSync()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return null;
            if (!mod.Session.HasCoopPartner)
                return null;
            return mod.EventSync;
        }
    }

    [HarmonyPatch(typeof(GameRandom), nameof(GameRandom.Float), new Type[] { typeof(RandomID), typeof(int) })]
    public static class GameRandomFloatStageTapePatches
    {
        public static bool Prefix(RandomID id, ref float __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return true;
            float value;
            if (sync.TryReplayEventRngFloat(out value))
            {
                __result = value;
                return false;
            }
            return true;
        }

        public static void Postfix(RandomID id, float __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return;
            sync.RecordEventRngFloat(__result);
        }

        private static EventSyncService GetSync()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return null;
            if (!mod.Session.HasCoopPartner)
                return null;
            return mod.EventSync;
        }
    }

    [HarmonyPatch(typeof(GameRandom), nameof(GameRandom.Float), new Type[] { typeof(float), typeof(float), typeof(RandomID), typeof(int) })]
    public static class GameRandomFloatRangeStageTapePatches
    {
        public static bool Prefix(RandomID id, ref float __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return true;
            float value;
            if (sync.TryReplayEventRngFloat(out value))
            {
                __result = value;
                return false;
            }
            return true;
        }

        public static void Postfix(RandomID id, float __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return;
            sync.RecordEventRngFloat(__result);
        }

        private static EventSyncService GetSync()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return null;
            if (!mod.Session.HasCoopPartner)
                return null;
            return mod.EventSync;
        }
    }

    [HarmonyPatch(typeof(GameRandom), nameof(GameRandom.Roll), new Type[] { typeof(float), typeof(RandomID), typeof(int) })]
    public static class GameRandomRollStageTapePatches
    {
        public static bool Prefix(RandomID id, ref bool __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return true;
            bool value;
            if (sync.TryReplayEventRngRoll(out value))
            {
                __result = value;
                return false;
            }
            return true;
        }

        public static void Postfix(RandomID id, bool __result)
        {
            EventSyncService sync = GetSync();
            if (sync == null || !EventSyncService.IsEventRngStream(id))
                return;
            sync.RecordEventRngRoll(__result);
        }

        private static EventSyncService GetSync()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.EventSync == null || mod.Session == null || !mod.Session.Active)
                return null;
            if (!mod.Session.HasCoopPartner)
                return null;
            return mod.EventSync;
        }
    }
}
