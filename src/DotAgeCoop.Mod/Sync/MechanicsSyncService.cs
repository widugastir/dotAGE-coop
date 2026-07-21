using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class MechanicsSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private bool _applyingRemote;
        private bool _pendingBroadcast;
        private float _lastBroadcastAt;
        private bool _deferredUnlockUi;

        public bool ApplyingRemote { get { return _applyingRemote; } }

        public MechanicsSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {
            if (!_session.Active || !_session.IsHost || !_pendingBroadcast)
                return;
            if (Time.unscaledTime - _lastBroadcastAt < 0.2f)
                return;
            _pendingBroadcast = false;
            BroadcastSnapshot();
        }

        public void MarkDirty()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            _pendingBroadcast = true;
        }

        public void BroadcastSnapshotImmediate()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            _pendingBroadcast = false;
            BroadcastSnapshot();
        }

        public void SendSnapshotTo(CSteamID remote)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;

            try
            {
                int[] ids = CaptureUnlockedIds();
                if (ids == null)
                    return;
                int progression = CaptureProgression();
                _session.SendTo(remote, CoopMessageType.MechanicsSnapshot,
                    CoopProtocol.PackMechanicsSnapshot(ids, progression));
                _log.Msg("[Mechanics] Send snapshot → " + remote.m_SteamID +
                         " (unlocked=" + ids.Length + " prog=" + progression + ")");
            }
            catch (Exception ex)
            {
                _log.Warning("[Mechanics] SendSnapshotTo: " + ex.Message);
            }
        }

        public void OnHostUnlock()
        {
            MarkDirty();
        }

        public void OnHostMemoryUnlocked()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            BroadcastSnapshotImmediate();
        }

        public bool ShouldBlockClientMemoryUnlock()
        {
            return _session.Active && !_session.IsHost && !_applyingRemote;
        }

        private void BroadcastSnapshot()
        {
            try
            {
                int[] ids = CaptureUnlockedIds();
                if (ids == null)
                    return;
                int progression = CaptureProgression();
                _lastBroadcastAt = Time.unscaledTime;
                _session.Broadcast(CoopMessageType.MechanicsSnapshot,
                    CoopProtocol.PackMechanicsSnapshot(ids, progression));
                _log.Msg("[Mechanics] Broadcast unlocked=" + ids.Length + " prog=" + progression);
            }
            catch (Exception ex)
            {
                _log.Warning("[Mechanics] Broadcast: " + ex.Message);
            }
        }

        private static int CaptureProgression()
        {
            try
            {
                if (Game.I != null && Game.I.PlayerProfileData != null)
                    return Game.I.PlayerProfileData.CurrentProgressionCounter;
            }
            catch
            {
            }

            return 0;
        }

        private static int[] CaptureUnlockedIds()
        {
            MechanicsHandler mh = MonoSingleton<MechanicsHandler>.I;
            if (mh == null)
                return null;

            List<MechanicDefinition> defs = mh.GetAllMechanicDefs();
            if (defs == null || defs.Count == 0)
                return new int[0];

            List<int> unlocked = new List<int>(defs.Count);
            for (int i = 0; i < defs.Count; i++)
            {
                MechanicDefinition def = defs[i];
                if (def == null)
                    continue;
                try
                {
                    if (mh.HasUnlocked(def, skipError: true))
                        unlocked.Add(def.ID);
                }
                catch
                {
                }
            }

            return unlocked.ToArray();
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            if (type != CoopMessageType.MechanicsSnapshot)
                return;
            if (_session.IsHost)
                return;
            if (GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
            {
                if (Time.frameCount % 180 == 0)
                    _log.Msg("[Mechanics] STAGE client: defer snapshot (joining/generating)");
                return;
            }

            int[] ids;
            int progression;
            if (!CoopProtocol.TryReadMechanicsSnapshot(payload, out ids, out progression))
            {
                _log.Warning("[Mechanics] Bad snapshot");
                return;
            }

            ApplySnapshot(ids, progression);
        }

        private void ApplySnapshot(int[] ids, int progressionCounter)
        {
            if (ids == null)
                return;

            MechanicsHandler mh = MonoSingleton<MechanicsHandler>.I;
            if (mh == null || Game.I == null || Game.I.unlocksHandler == null)
            {
                _log.Warning("[Mechanics] Handlers missing — defer");
                return;
            }

            _applyingRemote = true;
            int applied = 0;
            try
            {
                if (progressionCounter >= 0 && Game.I.PlayerProfileData != null)
                {
                    try
                    {
                        Game.I.PlayerProfileData.ForceProgressionCounterNoSave(progressionCounter);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[Mechanics] ForceProgression: " + ex.Message);
                    }
                }

                UnlocksHandler uh = Game.I.unlocksHandler;
                for (int i = 0; i < ids.Length; i++)
                {
                    int id = ids[i];
                    try
                    {
                        if (mh.HasUnlocked((MechanicID)id, skipError: true))
                            continue;

                        MechanicDefinition def = mh.GetMechanicDef(id);
                        if (def == null)
                            continue;

                        ApplyMechanicUnlock(mh, uh, def);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[Mechanics] Unlock id=" + id + ": " + ex.Message);
                    }
                }

                try
                {
                    if (UnlocksMenu.I != null)
                        UnlocksMenu.I.Refresh();
                }
                catch
                {
                }

                try
                {
                    if (Game.I.passTurnController != null && applied > 0)
                    {
                        if (ShouldDeferUnlockUi())
                        {
                            _deferredUnlockUi = true;
                            _log.Msg("[Mechanics] Defer unlock UI until load gate opens");
                        }
                        else
                        {
                            MelonCoroutines.Start(ShowPendingUnlocksCO());
                        }
                    }
                }
                catch
                {
                }
            }
            finally
            {
                _applyingRemote = false;
            }

            _log.Msg("[Mechanics] Applied memories: +" + applied + " / " + ids.Length +
                     " prog=" + progressionCounter);
        }

        private static void ApplyMechanicUnlock(MechanicsHandler mh, UnlocksHandler uh, MechanicDefinition def)
        {
            mh.Unlock(def);

            bool prev = uh.unlockOutOfGame;
            uh.unlockOutOfGame = true;
            try
            {
                GameElementDefinition[] elements = def.UnlockedElements;
                if (elements == null)
                    return;

                for (int i = 0; i < elements.Length; i++)
                {
                    GameElementDefinition el = elements[i];
                    if (el == null)
                        continue;

                    if (el.DefType == GameDefinitionType.EventDefinition ||
                        el.DefType == GameDefinitionType.ExpeditionDefinition)
                        uh.UnlockAndEncounterGED(el, unlockOnly: true);
                    else
                        uh.UnlockAndEncounterGED(el);
                }
            }
            finally
            {
                uh.unlockOutOfGame = prev;
            }
        }

        public void FlushDeferredUnlockUi()
        {
            if (!_deferredUnlockUi)
                return;
            _deferredUnlockUi = false;
            try
            {
                if (Game.I != null && Game.I.passTurnController != null)
                    MelonCoroutines.Start(ShowPendingUnlocksCO());
            }
            catch (Exception ex)
            {
                _log.Warning("[Mechanics] FlushDeferredUnlockUi: " + ex.Message);
            }
        }

        private static bool ShouldDeferUnlockUi()
        {
            try
            {
                ModMain mod = ModMain.Instance;
                if (mod == null || mod.Bootstrap == null)
                    return false;
                return mod.Bootstrap.IsPeerLoadWaitActive;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerator ShowPendingUnlocksCO()
        {
            if (Game.I == null || Game.I.passTurnController == null)
                yield break;

            IEnumerator co = null;
            try
            {
                co = Game.I.passTurnController.ShowPendingUnlocksAndInstructionsCO();
            }
            catch
            {
                yield break;
            }

            if (co == null)
                yield break;

            while (true)
            {
                object current;
                bool moved;
                try
                {
                    moved = co.MoveNext();
                    current = moved ? co.Current : null;
                }
                catch
                {
                    yield break;
                }

                if (!moved)
                    yield break;
                yield return current;
            }
        }
    }
}
