using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(MechanicsHandler), "Unlock", typeof(MechanicID))]
    public static class MechanicsUnlockIdPatches
    {
        public static void Postfix(MechanicID id)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.MechanicsSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.MechanicsSync.ApplyingRemote)
                return;
            mod.MechanicsSync.OnHostUnlock();
        }
    }

    [HarmonyPatch(typeof(MechanicsHandler), "Unlock", typeof(MechanicDefinition))]
    public static class MechanicsUnlockDefPatches
    {
        public static void Postfix(MechanicDefinition def)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.MechanicsSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.MechanicsSync.ApplyingRemote)
                return;
            mod.MechanicsSync.OnHostUnlock();
        }
    }

    [HarmonyPatch(typeof(UIUnlockMechanic), "TryUnlock")]
    public static class ClientBlockMemoryTryUnlockPatches
    {
        public static bool Prefix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.MechanicsSync == null)
                return true;
            if (!mod.MechanicsSync.ShouldBlockClientMemoryUnlock())
                return true;

            MelonLogger.Msg("[DotAgeCoop] Memory unlock blocked on client — only host can open memories");
            return false;
        }
    }

    [HarmonyPatch(typeof(UIUnlockMechanic), "UnlockCO")]
    public static class MemoryUnlockCOPatches
    {
        private static readonly FieldInfo MechanicDefField =
            AccessTools.Field(typeof(UIUnlockMechanic), "mechanicDef");

        public static bool Prefix(UIUnlockMechanic __instance, ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.MechanicsSync != null && mod.MechanicsSync.ShouldBlockClientMemoryUnlock())
            {
                __result = Empty();
                return false;
            }

            if (MemoryCheat.IsHostShiftFreeUnlock())
            {
                __result = FreeUnlockCO(__instance);
                return false;
            }

            return true;
        }

        private static IEnumerator FreeUnlockCO(UIUnlockMechanic self)
        {
            MechanicDefinition mechanicDef = MechanicDefField != null
                ? MechanicDefField.GetValue(self) as MechanicDefinition
                : null;
            if (mechanicDef == null || mechanicDef.IsUnlocked())
                yield break;

            MelonLogger.Msg("[DotAgeCoop] Host free memory unlock: " + mechanicDef.name);

            List<MechanicDefinition> prereqs = null;
            try
            {
                List<MechanicDefinition> all = MonoSingleton<MechanicsHandler>.I.GetAllMechanicDefs();
                prereqs = new List<MechanicDefinition>();
                for (int i = 0; i < all.Count; i++)
                {
                    MechanicDefinition item = all[i];
                    if (item == null || item.AutoSort == 0 || item.unlockedFromStart)
                        continue;
                    if (item.AutoSort >= mechanicDef.AutoSort)
                        continue;
                    if (item.IsUnlocked())
                        continue;
                    prereqs.Add(item);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] FreeUnlock prereqs: " + ex.Message);
            }

            if (prereqs != null)
            {
                for (int i = 0; i < prereqs.Count; i++)
                    yield return Game.I.unlocksHandler.UnlockMechanicAndItsDefs(prereqs[i]);
            }

            yield return Game.I.unlocksHandler.UnlockMechanicAndItsDefs(mechanicDef);

            bool showPending = false;
            try
            {
                self.Reinit();
                if (Game.I.GameIsPlaying())
                    Game.I.researchHandler.RefreshPaths(newGame: false);

                if (Game.I.UnlocksMenu != null)
                    Game.I.UnlocksMenu.Refresh();
                showPending = !Game.I.GameIsPlaying() && Game.I.passTurnController != null;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] FreeUnlock finish: " + ex.Message);
            }

            if (showPending)
                yield return Game.I.passTurnController.ShowPendingUnlocksAndInstructionsCO();
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(UIUnlockMechanic), "Update")]
    public static class MemoryCheatEnableButtonPatches
    {
        public static void Postfix(UIUnlockMechanic __instance)
        {
            MemoryCheat.ForceButtonIfShift(__instance);
        }
    }

    [HarmonyPatch(typeof(UnlocksHandler), "UnlockMechanicAndItsDefs", typeof(MechanicDefinition))]
    public static class UnlockMechanicAndItsDefsPatches
    {
        public static void Postfix(ref IEnumerator __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.MechanicsSync == null || mod.Session == null || !mod.Session.Active)
                return;

            if (!mod.Session.IsHost)
            {
                if (mod.MechanicsSync.ShouldBlockClientMemoryUnlock())
                    __result = Empty();
                return;
            }

            __result = WrapHost(__result, mod);
        }

        private static IEnumerator WrapHost(IEnumerator original, ModMain mod)
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
                    catch (Exception ex)
                    {
                        MelonLogger.Warning("[DotAgeCoop] UnlockMechanicAndItsDefs: " + ex.Message);
                        break;
                    }

                    if (!moved)
                        break;
                    yield return current;
                }
            }

            if (mod.MechanicsSync != null)
                mod.MechanicsSync.OnHostMemoryUnlocked();
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(UnlocksHandler), "UnlockNext")]
    public static class ClientBlockUnlockNextPatches
    {
        public static bool Prefix(ref List<GameElementDefinition> __result)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.MechanicsSync == null)
                return true;
            if (!mod.MechanicsSync.ShouldBlockClientMemoryUnlock())
                return true;

            __result = new List<GameElementDefinition>();
            return false;
        }
    }

    [HarmonyPatch(typeof(UnlocksMenu), "Update")]
    public static class UnlocksMenuMemoryCheatPatches
    {
        public static void Postfix(UnlocksMenu __instance)
        {
            MemoryCheat.OnUnlocksMenuUpdate(__instance);
        }
    }

    [HarmonyPatch(typeof(UIUnlockMechanic), "RefreshPoints")]
    public static class MemoryCheatRefreshPointsPatches
    {
        public static void Postfix(UIUnlockMechanic __instance)
        {
            MemoryCheat.ForceButtonIfShift(__instance);
        }
    }

    public static class MemoryCheat
    {
        private static readonly FieldInfo MechanicDefField =
            AccessTools.Field(typeof(UIUnlockMechanic), "mechanicDef");

        private static bool _menuOpen;
        private static bool _loggedOpen;
        private static bool _shiftWasHeld;
        private static bool _restoringUi;

        public static bool IsHostAllowed()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null)
                return false;
            if (mod.Session == null || !mod.Session.Active)
                return true;
            return mod.Session.IsHost;
        }

        public static bool IsShiftHeld()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        }

        public static bool IsHostShiftFreeUnlock()
        {
            return IsHostAllowed() && IsShiftHeld();
        }

        public static void OnUnlocksMenuUpdate(UnlocksMenu menu)
        {
            if (menu == null || !menu.IsOpen)
            {
                _menuOpen = false;
                _loggedOpen = false;
                _shiftWasHeld = false;
                return;
            }

            _menuOpen = true;
            if (!IsHostAllowed())
                return;

            if (!_loggedOpen)
            {
                _loggedOpen = true;
                MelonLogger.Msg("[DotAgeCoop] Unlocks menu open — host cheat: hold Shift for FREE unlock, Shift+P = +100 points");
            }

            bool shift = IsShiftHeld();

            if (shift && Input.GetKeyDown(KeyCode.P))
            {
                try
                {
                    if (Game.I != null && Game.I.PlayerProfileData != null)
                    {
                        Game.I.PlayerProfileData.saveData.unlockProgressionCounter += 100;
                        menu.Refresh();
                        MelonLogger.Msg("[DotAgeCoop] Host cheat +100 memory points");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[DotAgeCoop] +100 points: " + ex.Message);
                }
            }

            try
            {
                List<UIUnlockMechanic_Placeholder> placeholders = menu.Placeholders;
                if (placeholders == null)
                    return;

                if (shift)
                {
                    for (int i = 0; i < placeholders.Count; i++)
                    {
                        UIUnlockMechanic_Placeholder ph = placeholders[i];
                        if (ph == null)
                            continue;
                        if (ph.uiRef.isSet)
                            ForceButtonIfShift(ph.uiRef.reference);
                        if (ph.uiRef2.isSet)
                            ForceButtonIfShift(ph.uiRef2.reference);
                    }
                }
                else if (_shiftWasHeld)
                {

                    RestoreUnlockButtons(placeholders);
                }
            }
            catch
            {
            }

            _shiftWasHeld = shift;
        }

        private static void RestoreUnlockButtons(List<UIUnlockMechanic_Placeholder> placeholders)
        {
            _restoringUi = true;
            try
            {
                for (int i = 0; i < placeholders.Count; i++)
                {
                    UIUnlockMechanic_Placeholder ph = placeholders[i];
                    if (ph == null)
                        continue;
                    try
                    {
                        ph.RefreshPoints();
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                _restoringUi = false;
            }
        }

        public static void ForceButtonIfShift(UIUnlockMechanic ui)
        {
            if (_restoringUi || ui == null || !IsHostShiftFreeUnlock())
                return;
            if (ui.UnlockButton == null)
                return;

            MechanicDefinition def = MechanicDefField != null
                ? MechanicDefField.GetValue(ui) as MechanicDefinition
                : null;
            if (def != null && def.IsUnlocked())
                return;

            try
            {
                ui.UnlockButton.SetActive(true);
                ui.UnlockButton.SetInteractable(true, changeEnabledToo: true);
                if (ui.UnlockButton.textUINew != null)
                    ui.UnlockButton.textUINew.Text = "FREE (Shift)";
            }
            catch
            {
            }
        }

        public static void DrawHint()
        {
            if (!IsHostAllowed())
                return;

            UnlocksMenu menu = GetOpenUnlocksMenu();
            if (menu == null)
                return;

            float w = 560f;
            float h = 28f;
            Rect tip = new Rect((Screen.width - w) * 0.5f, 6f, w, h);
            DrawBar(tip, IsShiftHeld()
                ? "SHIFT: click Unlock = FREE  |  Shift+P = +100 points"
                : "Host cheat: hold SHIFT for free memory unlock (or Shift+P = +100 points)");

            if (!IsShiftHeld())
                return;

            try
            {
                List<UIUnlockMechanic_Placeholder> placeholders = menu.Placeholders;
                if (placeholders == null)
                    return;

                float y = 38f;
                int drawn = 0;
                for (int i = 0; i < placeholders.Count && drawn < 12; i++)
                {
                    UIUnlockMechanic_Placeholder ph = placeholders[i];
                    if (ph == null || ph.mechanicDefs == null)
                        continue;

                    if (!ph.uiRef.isSet && !ph.uiRef2.isSet)
                        continue;

                    for (int j = 0; j < ph.mechanicDefs.Length && drawn < 12; j++)
                    {
                        MechanicDefinition def = ph.mechanicDefs[j];
                        if (def == null || def.IsUnlocked())
                            continue;

                        string label = "FREE: " + SafeName(def);
                        Rect br = new Rect((Screen.width - 480f) * 0.5f, y, 480f, 26f);
                        if (GUI.Button(br, label))
                            MelonCoroutines.Start(FreeUnlockDefCO(def));
                        y += 28f;
                        drawn++;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] MemoryCheat DrawHint: " + ex.Message);
            }
        }

        private static UnlocksMenu GetOpenUnlocksMenu()
        {
            try
            {
                if (Game.I != null && Game.I.UnlocksMenu != null && Game.I.UnlocksMenu.IsOpen)
                    return Game.I.UnlocksMenu;
            }
            catch
            {
            }

            try
            {
                if (UnlocksMenu.I != null && UnlocksMenu.I.IsOpen)
                    return UnlocksMenu.I;
            }
            catch
            {
            }

            return _menuOpen ? UnlocksMenu.I : null;
        }

        private static string SafeName(MechanicDefinition def)
        {
            try
            {
                string pretty = def.GetPrettyName();
                if (!string.IsNullOrEmpty(pretty))
                    return pretty;
            }
            catch
            {
            }
            return def.name;
        }

        private static void DrawBar(Rect r, string text)
        {
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 14;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.85f, 0.35f, 1f);
            GUI.Label(r, text, style);
            GUI.color = prev;
        }

        private static IEnumerator FreeUnlockDefCO(MechanicDefinition mechanicDef)
        {
            if (mechanicDef == null || mechanicDef.IsUnlocked())
                yield break;
            if (!IsHostAllowed())
                yield break;

            MelonLogger.Msg("[DotAgeCoop] Host FREE unlock (GUI): " + mechanicDef.name);

            List<MechanicDefinition> prereqs = new List<MechanicDefinition>();
            try
            {
                List<MechanicDefinition> all = MonoSingleton<MechanicsHandler>.I.GetAllMechanicDefs();
                for (int i = 0; i < all.Count; i++)
                {
                    MechanicDefinition item = all[i];
                    if (item == null || item.AutoSort == 0 || item.unlockedFromStart)
                        continue;
                    if (item.AutoSort >= mechanicDef.AutoSort)
                        continue;
                    if (item.IsUnlocked())
                        continue;
                    prereqs.Add(item);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] FreeUnlockDef prereqs: " + ex.Message);
            }

            for (int i = 0; i < prereqs.Count; i++)
                yield return Game.I.unlocksHandler.UnlockMechanicAndItsDefs(prereqs[i]);

            yield return Game.I.unlocksHandler.UnlockMechanicAndItsDefs(mechanicDef);

            try
            {
                if (Game.I.GameIsPlaying())
                    Game.I.researchHandler.RefreshPaths(newGame: false);
                if (Game.I.UnlocksMenu != null)
                    Game.I.UnlocksMenu.Refresh();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] FreeUnlockDef finish: " + ex.Message);
            }
        }
    }
}
