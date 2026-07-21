using System.Collections;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DotAgeCoop.Hooks
{

    [HarmonyPatch(typeof(StartGameController), "NewGame_FromName_ToLevel")]
    public static class HostNewGameFromMenuPatches
    {
        public static void Prefix()
        {
            NotifyHostBeginningNewGame();
        }

        private static void NotifyHostBeginningNewGame()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;

            mod.Bootstrap.OnHostBeginningNewGame();
        }
    }

    [HarmonyPatch(typeof(StartGameController), "NewGame_Immediate")]
    public static class HostNewGameImmediatePatches
    {
        public static void Prefix()
        {
            ModMain mod = ModMain.Instance;

            if (mod == null || mod.Bootstrap == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;
            if (mod.Bootstrap.IsJoining)
                return;

            mod.Bootstrap.OnHostBeginningNewGame();
        }

        public static void Postfix()
        {
            StartGameController.IS_LOADING_IMMEDIATE = true;
            MelonCoroutines.Start(ClearLoadingImmediateWhenReady());
        }

        private static IEnumerator ClearLoadingImmediateWhenReady()
        {
            float started = Time.unscaledTime;
            bool sawGenerating = false;
            while (Time.unscaledTime - started < 120f)
            {
                try
                {
                    Game g = Game.I;
                    if (g != null)
                    {
                        if (g.IsGeneratingGame || g.IsCurrentlyLoading)
                            sawGenerating = true;

                        if (sawGenerating && !g.IsGeneratingGame && !g.IsCurrentlyLoading)
                            break;
                        if (g.GameIsStarted() && sawGenerating)
                            break;
                    }
                }
                catch
                {
                }
                yield return null;
            }
            StartGameController.IS_LOADING_IMMEDIATE = false;
        }
    }

    [HarmonyPatch(typeof(StartGameController), "NewGame_Tutorial")]
    public static class HostNewGameTutorialPatches
    {
        public static void Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;

            mod.Bootstrap.OnHostBeginningNewGame();
        }
    }

    [HarmonyPatch(typeof(StartGameController), "LoadGame_FromIntroCO")]
    public static class HostLoadGameFromIntroCOPatch
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.LoadSync == null)
                return;
            __result = mod.LoadSync.WrapHostLoadCoroutine(__result);
        }
    }

    [HarmonyPatch(typeof(StartGameController), "LoadGame_ImmediateCO")]
    public static class HostLoadGameImmediateCOPatch
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.LoadSync == null)
                return;
            __result = mod.LoadSync.WrapHostLoadCoroutine(__result);
        }
    }

    [HarmonyPatch(typeof(Game), "StartGame_OffScreenPhase")]
    public static class HostOffScreenPatches
    {
        public static void Prefix(bool loadFromData)
        {
            if (loadFromData)
                return;

            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;

            mod.Bootstrap.OnHostBeginningNewGame();
        }

        public static void Postfix(bool loadFromData)
        {
            if (!loadFromData)
                return;

            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;

            mod.Bootstrap.OnHostBeginningLoadGame();
        }
    }

    [HarmonyPatch(typeof(Game), "StartGame_OnScreenPhase")]
    public static class HostGameplayPatches
    {
        public static void Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null)
                return;

            if (mod.Bootstrap.IsClientLoadJoin)
                mod.Bootstrap.PrepareClientLoadJoinOnScreen();
        }

        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null)
                return;

            mod.Bootstrap.OnHostEnteredGameplay();
        }
    }

    [HarmonyPatch(typeof(GameCutscenesHandler), "CutsceneGameIntroCO")]
    public static class ClientLoadJoinSkipIntroCutscenePatches
    {
        public static bool Prefix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Bootstrap == null || !mod.Bootstrap.IsClientLoadJoin)
                return true;

            MelonLogger.Msg("[DotAgeCoop] Skipping CutsceneGameIntro (client load-join)");
            __result = EmptyCoroutine();
            return false;
        }

        private static IEnumerator EmptyCoroutine()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(Game), "ReturnToMain")]
    public static class HostReturnToMainPatches
    {

        public static void Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.Bootstrap != null)
                mod.Bootstrap.OnReturningToMainBegin();
            if (mod != null && mod.LoadSync != null)
                mod.LoadSync.OnReturnedToMain();
        }
    }

    [HarmonyPatch(typeof(Game), "ReturnToMainCO")]
    public static class HostReturnToMainCOPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null)
                return;
            __result = WrapReturnToMain(__result, mod);
        }

        private static IEnumerator WrapReturnToMain(IEnumerator original, ModMain mod)
        {
            if (original != null)
            {
                while (true)
                {
                    object current;
                    bool moved;
                    try
                    {
                        moved = original.MoveNext();
                        current = moved ? original.Current : null;
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Warning("[DotAgeCoop] ReturnToMainCO: " + ex.Message);
                        break;
                    }

                    if (!moved)
                        break;
                    yield return current;
                }
            }

            if (mod.Bootstrap != null)
                mod.Bootstrap.OnReturnedToMain();
            if (mod.TurnSync != null)
                mod.TurnSync.OnReturnedToMain();
            if (mod.DialogueSync != null)
                mod.DialogueSync.OnReturnedToMain();
            if (mod.GameSync != null)
                mod.GameSync.OnReturnedToMain();
            if (mod.PipOrders != null)
                mod.PipOrders.OnReturnedToMain();
            if (mod.PipAppearance != null)
                mod.PipAppearance.OnReturnedToMain();
            if (mod.ScalesSync != null)
                mod.ScalesSync.OnReturnedToMain();
            if (mod.EventSync != null)
                mod.EventSync.OnReturnedToMain();
            if (mod.HardSync != null)
                mod.HardSync.OnReturnedToMain();
            if (mod.FirstPlacement != null)
                mod.FirstPlacement.OnReturnedToMain();
            if (mod.LoadSync != null)
                mod.LoadSync.OnReturnedToMain();
        }
    }
}
