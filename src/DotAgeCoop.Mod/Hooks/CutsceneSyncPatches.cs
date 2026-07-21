using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DotAgeCoop.Hooks
{

    internal static class CutsceneSyncPatches
    {
        private static readonly FieldInfo MessagePanelMovingField =
            AccessTools.Field(typeof(MessagePanel), "_moving");

        private static bool CoopActive()
        {
            ModMain mod = ModMain.Instance;
            return mod != null && mod.Session != null && mod.Session.Active &&
                   mod.Session.HasCoopPartner;
        }

        [HarmonyPatch(typeof(GameCutscenesHandler), "ShowCutMessage",
            new Type[]
            {
                typeof(TutorialInstance), typeof(string), typeof(bool), typeof(int),
                typeof(AnimChoice), typeof(int), typeof(AudioID), typeof(bool)
            })]
        public static class ShowCutMessageCoopPatches
        {
            public static void Prefix(GameCutscenesHandler __instance)
            {
                if (!CoopActive() || __instance == null)
                    return;
                if (!__instance.SkipCurrentCutscene)
                    return;

                __instance.SkipCurrentCutscene = false;
                MelonLogger.Msg("[DotAgeCoop] [Cutscene] Cleared SkipCurrentCutscene for coop dialogue sync");
            }
        }

        [HarmonyPatch(typeof(EventsTreeMenu), "AnimateEntranceCO")]
        public static class EventsTreeAnimateEntrancePatches
        {
            public static void Postfix(EventsTreeMenu __instance, ref IEnumerator __result)
            {
                if (!CoopActive() || __result == null)
                    return;
                __result = CoopSafePanelAnim(__instance, __result, "AnimateEntranceCO", 8f);
            }
        }

        [HarmonyPatch(typeof(EventsTreeMenu), "AnimateExitCO")]
        public static class EventsTreeAnimateExitPatches
        {
            public static void Postfix(EventsTreeMenu __instance, ref IEnumerator __result)
            {
                if (!CoopActive() || __result == null)
                    return;
                __result = CoopSafePanelAnim(__instance, __result, "AnimateExitCO", 8f);
            }
        }

        [HarmonyPatch(typeof(EventsTreeMenu), "AnimateUpCO")]
        public static class EventsTreeAnimateUpPatches
        {
            public static void Postfix(EventsTreeMenu __instance, ref IEnumerator __result)
            {
                if (!CoopActive() || __result == null)
                    return;
                __result = CoopSafePanelAnim(__instance, __result, "AnimateUpCO", 10f);
            }
        }

        [HarmonyPatch(typeof(EventsTreeMenu), "AnimatePopStuffIn")]
        public static class EventsTreeAnimatePopPatches
        {
            public static void Postfix(EventsTreeMenu __instance, ref IEnumerator __result)
            {
                if (!CoopActive() || __result == null)
                    return;
                __result = CoopSafePanelAnim(__instance, __result, "AnimatePopStuffIn", 8f);
            }
        }

        private static IEnumerator CoopSafePanelAnim(
            EventsTreeMenu menu, IEnumerator original, string name, float timeoutSeconds)
        {
            float deadline = Time.unscaledTime + Mathf.Max(3f, timeoutSeconds);
            while (true)
            {
                object current;
                bool moved;
                try
                {
                    moved = original.MoveNext();
                    current = moved ? original.Current : null;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[DotAgeCoop] [Cutscene] " + name + " failed: " + ex.Message);
                    ForceStopPanelMoving(menu);
                    yield break;
                }

                if (!moved)
                    yield break;

                if (Time.unscaledTime >= deadline)
                {
                    MelonLogger.Warning("[DotAgeCoop] [Cutscene] " + name +
                                        " timed out — force continue (prophecy tree)");
                    ForceStopPanelMoving(menu);
                    yield break;
                }

                if (ShouldAbortTreeAnimForHostDialogue())
                {
                    MelonLogger.Msg("[DotAgeCoop] [Cutscene] " + name +
                                    " abort — host dialogue ahead");
                    ForceStopPanelMoving(menu);
                    TryForceCloseEventsTree(menu);
                    yield break;
                }

                yield return current;
            }
        }

        private static bool ShouldAbortTreeAnimForHostDialogue()
        {
            try
            {
                ModMain mod = ModMain.Instance;
                if (mod == null || mod.Session == null || mod.Session.IsHost)
                    return false;
                if (mod.DialogueSync == null)
                    return false;
                return mod.DialogueSync.HostIsAheadOfLocalOpen();
            }
            catch
            {
                return false;
            }
        }

        private static void ForceStopPanelMoving(EventsTreeMenu menu)
        {
            if (menu == null || MessagePanelMovingField == null)
                return;
            try
            {
                MessagePanel panel = menu.GetComponent<MessagePanel>();
                if (panel != null)
                    MessagePanelMovingField.SetValue(panel, false);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] [Cutscene] ForceStopPanelMoving: " + ex.Message);
            }
        }

        internal static void TryForceCloseEventsTree(EventsTreeMenu menu = null)
        {
            try
            {
                if (menu == null && Game.I != null)
                    menu = Game.I.EventsTreeMenu;
                if (menu == null || !menu.isActiveAndEnabled)
                    return;

                MessagePanel panel = menu.GetComponent<MessagePanel>();
                if (panel != null && panel.IsOpen)
                    panel.CloseMessage();
                ForceStopPanelMoving(menu);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] [Cutscene] ForceCloseEventsTree: " + ex.Message);
            }
        }
    }
}
