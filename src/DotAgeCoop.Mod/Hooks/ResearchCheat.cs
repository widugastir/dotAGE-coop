using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace DotAgeCoop.Hooks
{

    public static class ResearchCheat
    {
        private static readonly MethodInfo RefreshSectionTabsMethod =
            AccessTools.Method(typeof(ResearchTree), "RefreshSectionTabs");

        private static readonly MechanicID[] TreeTabMechanics =
        {
            MechanicID.ResearchTree_Growth,
            MechanicID.ResearchTree_Education,
            MechanicID.ResearchTree_Construction,
            MechanicID.ResearchTree_Community,
            MechanicID.ResearchTree_Randomization,
            MechanicID.Expedition
        };

        public static bool IsHostAllowed()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null)
                return false;
            if (mod.Session == null || !mod.Session.Active)
                return true;
            return mod.Session.IsHost;
        }

        public static void DrawCheatButton()
        {
            if (!IsHostAllowed())
                return;

            ResearchTree tree = null;
            try
            {
                if (Game.I != null)
                    tree = Game.I.researchTree;
            }
            catch
            {
                return;
            }

            if (tree == null || !tree.IsOpen)
                return;

            float w = 320f;
            float h = 36f;
            Rect r = new Rect((Screen.width - w) * 0.5f, 12f, w, h);
            if (GUI.Button(r, "CHEAT: Unlock all tiers & tabs"))
            {
                HostUnlockAllTiersAndTabs();
            }
        }

        public static void HostUnlockAllTiersAndTabs()
        {
            if (!IsHostAllowed())
                return;

            MelonLogger.Msg("[DotAgeCoop] Host research cheat: unlock all tiers & tabs");
            ApplyUnlockAllTiersAndTabs();

            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active || !mod.Session.IsHost)
                return;

            try
            {
                mod.Session.Broadcast(DotAgeCoop.Net.CoopMessageType.ResearchCheatUnlockAll);
                if (mod.MechanicsSync != null)
                    mod.MechanicsSync.BroadcastSnapshotImmediate();
                if (mod.ResearchSync != null)
                    mod.ResearchSync.BroadcastSnapshotImmediate();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] Research cheat broadcast: " + ex.Message);
            }
        }

        public static void ApplyUnlockAllTiersAndTabs()
        {
            if (Game.I == null || Game.I.researchHandler == null || Game.I.PlayerProfileData == null)
                return;

            try
            {
                ResearchHandler rh = Game.I.researchHandler;
                List<ResearchTier> tiers = rh.GetAllTiers();
                if (tiers != null)
                {
                    for (int i = 0; i < tiers.Count; i++)
                    {
                        ResearchTier tier = tiers[i];
                        if (tier == null)
                            continue;
                        Game.I.PlayerProfileData.SetTierUnlocked(tier.index, choice: true);
                    }
                }

                for (int i = 0; i < 16; i++)
                    Game.I.PlayerProfileData.SetTierUnlocked(i, choice: true);

                MechanicsHandler mh = MonoSingleton<MechanicsHandler>.I;
                if (mh != null)
                {
                    for (int i = 0; i < TreeTabMechanics.Length; i++)
                    {
                        try
                        {
                            mh.Unlock(TreeTabMechanics[i]);
                        }
                        catch
                        {
                        }
                    }
                }

                try
                {
                    rh.CheckTierMechanicsUnlock();
                }
                catch
                {
                }

                RefreshTreeUi();
                MelonLogger.Msg("[DotAgeCoop] Research tiers & tabs unlocked locally");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] ApplyUnlockAllTiersAndTabs: " + ex.Message);
            }
        }

        private static void RefreshTreeUi()
        {
            try
            {
                ResearchTree tree = Game.I != null ? Game.I.researchTree : null;
                if (tree == null)
                    return;

                if (RefreshSectionTabsMethod != null)
                    RefreshSectionTabsMethod.Invoke(tree, null);

                if (tree.IsOpen)
                    tree.UpdateInstances();

                ResearchTreeSection[] sections =
                    tree.GetComponentsInChildren<ResearchTreeSection>(true);
                if (sections != null)
                {
                    for (int i = 0; i < sections.Length; i++)
                    {
                        if (sections[i] != null)
                            sections[i].CheckTierUnlocking();
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] RefreshTreeUi: " + ex.Message);
            }
        }
    }
}
