using HarmonyLib;
using MelonLoader;
using DotAgeCoop.Net;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(ResearchTreeInstance_Tree), "PerformLogic")]
    public static class ResearchPerformLogicPatches
    {
        public static bool Prefix(ResearchTreeInstance_Tree __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return true;
            if (mod.Session.IsHost)
                return true;

            try
            {
                if (Game.I.IsAnyPassTurnTime())
                    return false;

                BuildingResearchContainer container = __instance.Container;
                if (container == null)
                    return false;

                if (__instance.IsChoiceToBeChosen)
                {
                    if (__instance.IsHoldingQueueButton)
                    {
                        mod.ResearchSync.SendIntent(
                            ResearchIntentKind.QueueAdd,
                            0,
                            container.Id);
                    }
                    else
                    {
                        mod.ResearchSync.SendIntent(
                            ResearchIntentKind.SetCurrent,
                            0,
                            container.Id);
                    }
                    return false;
                }

                if (__instance.IsHoldingQueueButton)
                {
                    bool queued = Game.I.researchTree != null && Game.I.researchTree.IsQueued(container);
                    mod.ResearchSync.SendIntent(
                        queued ? ResearchIntentKind.QueueRemove : ResearchIntentKind.QueueAdd,
                        0,
                        container.Id);
                    return false;
                }

                bool canResearch = __instance.IsResearching() || __instance.CanResearchNow() ||
                                   ResearchTree.DEBUG_LAYOUT || TConfig.I.CheatInstantResearch;
                if (__instance.CanResearchAgain)
                    canResearch = true;

                if (__instance.IsDiscovered() && !__instance.CanResearchAgain)
                    return true;

                if (!canResearch)
                    return false;

                BuildingDefinition def = container.CurrentDefinition;
                int defId = def != null ? def.ID : 0;

                if (TConfig.I.CheatInstantResearch)
                {
                    mod.ResearchSync.SendIntent(ResearchIntentKind.SetCurrent, defId, container.Id);
                    return false;
                }

                if (!__instance.IsResearching())
                {
                    mod.ResearchSync.SendIntent(ResearchIntentKind.SetCurrent, defId, container.Id);
                    return false;
                }

                if (Game.I.researchTree != null && Game.I.researchTree.IsQueued(container))
                {
                    mod.ResearchSync.SendIntent(ResearchIntentKind.QueueRemove, 0, container.Id);
                    return false;
                }

                mod.ResearchSync.SendIntent(ResearchIntentKind.SetCurrent, 0, 0);
                return false;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] Research PerformLogic: " + ex.Message);
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(BuildingResearchContainer), "SelectChoice")]
    public static class ResearchSelectChoicePatches
    {
        public static bool Prefix(BuildingResearchContainer __instance, int index, bool isLoading)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return true;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return true;
            if (mod.Session.IsHost)
                return true;
            if (isLoading || index <= 0)
                return true;

            try
            {
                System.Collections.Generic.List<BuildingDefinition> unlocked = __instance.GetAllUnlockedDefinitions();
                int idx = index - 1;
                if (idx < 0 || unlocked == null || idx >= unlocked.Count)
                    return false;
                BuildingDefinition def = unlocked[idx];
                if (def == null)
                    return false;
                mod.ResearchSync.SendIntent(ResearchIntentKind.SelectChoice, def.ID, __instance.Id);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] Research SelectChoice: " + ex.Message);
            }
            return false;
        }

        public static void Postfix(BuildingResearchContainer __instance, bool isLoading)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;
            if (isLoading)
                return;

            mod.ResearchSync.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(ResearchTree), "SetCurrentResearchingBuilding")]
    public static class ResearchSetCurrentPatches
    {
        public static void Postfix(BuildingDefinition def)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;

            mod.ResearchSync.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(ResearchHandler), "CompleteCurrentResearch")]
    public static class ResearchCompletePatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;

            mod.ResearchSync.BroadcastSnapshotImmediate();
        }
    }

    [HarmonyPatch(typeof(ResearchHandler), "EnableKnowledgeOfBuilding", new[] { typeof(BuildingDefinition), typeof(bool), typeof(bool) })]
    public static class ResearchEnableKnowledgePatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;

            mod.ResearchSync.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(ResearchTree), "AddToResearchQueue")]
    public static class ResearchQueueAddPatches
    {
        public static void Postfix()
        {
            MarkResearchDirty();
        }

        private static void MarkResearchDirty()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;
            mod.ResearchSync.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(ResearchTree), "RemoveFromResearchQueue")]
    public static class ResearchQueueRemovePatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;
            mod.ResearchSync.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(ResearchTree), "ClearQueue")]
    public static class ResearchQueueClearPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.ResearchSync == null || mod.ResearchSync.ApplyingRemote)
                return;
            mod.ResearchSync.MarkDirty();
        }
    }
}
