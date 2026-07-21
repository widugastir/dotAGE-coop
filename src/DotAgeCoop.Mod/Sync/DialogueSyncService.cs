using System;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class DialogueSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private readonly HashSet<ulong> _readyPeers = new HashSet<ulong>();

        private string _activeStepId = string.Empty;
        private string _hostStepId = string.Empty;
        private string _localOpenedStepId = string.Empty;
        private bool _localReady;
        private bool _allowNextComplete;
        private bool _advancing;
        private int _readyCount;
        private int _neededCount = 1;
        private float _readyDeadline;
        private float _nextReadyResendAt;
        private string _pendingAdvanceStepId = string.Empty;
        private float _pendingAdvanceUntil;

        public bool ShowWaitingPrompt
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;

                if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                    ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                    return false;
                return _localReady && !_advancing &&
                       _neededCount > 1 && _readyCount < _neededCount;
            }
        }

        public int ReadyCount { get { return _readyCount; } }
        public int NeededCount { get { return _neededCount; } }

        public bool HostIsAheadOfLocalOpen()
        {
            if (!_session.Active || _session.IsHost || !_session.HasCoopPartner)
                return false;

            string hostStep = !string.IsNullOrEmpty(_hostStepId) ? _hostStepId : _activeStepId;
            if (string.IsNullOrEmpty(hostStep))
                return false;

            if (string.IsNullOrEmpty(_localOpenedStepId))
            {
                try
                {
                    if (Game.I != null && Game.I.tutorialController != null &&
                        Game.I.tutorialController.IsCurrentlyShowing())
                    {
                        string cur = SafeStepId(Game.I.tutorialController.CurrentTutorialInstance);
                        if (!string.IsNullOrEmpty(cur) &&
                            string.Equals(cur, hostStep, StringComparison.Ordinal))
                            return false;
                        if (!string.IsNullOrEmpty(cur) &&
                            !string.Equals(cur, hostStep, StringComparison.Ordinal))
                            return true;
                    }

                    if (Game.I != null && Game.I.EventsTreeMenu != null &&
                        Game.I.EventsTreeMenu.isActiveAndEnabled)
                        return true;
                }
                catch
                {
                }
                return false;
            }

            if (string.Equals(hostStep, _localOpenedStepId, StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrEmpty(_pendingAdvanceStepId) &&
                string.Equals(_pendingAdvanceStepId, _localOpenedStepId, StringComparison.Ordinal))
                return false;

            return true;
        }

        public DialogueSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void OnReturnedToMain()
        {
            ClearWaitingGate("return-to-main");
        }

        public void ClearWaitingGate(string reason)
        {
            _activeStepId = string.Empty;
            _hostStepId = string.Empty;
            _localOpenedStepId = string.Empty;
            _localReady = false;
            _allowNextComplete = false;
            _advancing = false;
            _readyCount = 0;
            _neededCount = 1;
            _readyDeadline = 0f;
            _nextReadyResendAt = 0f;
            _pendingAdvanceStepId = string.Empty;
            _pendingAdvanceUntil = 0f;
            _readyPeers.Clear();
            if (!string.IsNullOrEmpty(reason))
                _log.Msg("[Dialogue] Cleared waiting gate (" + reason + ")");
        }

        public void ForceAbortWait(string reason)
        {
            if (_session.IsHost)
                return;

            string stepId = !string.IsNullOrEmpty(_activeStepId)
                ? _activeStepId
                : _localOpenedStepId;

            ClearWaitingGate(null);
            _log.Warning("[Dialogue] Force abort wait (" +
                         (string.IsNullOrEmpty(reason) ? "?" : reason) + ")");

            try
            {
                _allowNextComplete = true;
                _advancing = true;
                if (!TryForceCompleteNow(stepId))
                    TryForceCompleteAnyOpen();
            }
            catch (Exception ex)
            {
                _log.Warning("[Dialogue] Force abort complete failed: " + ex.Message);
            }
            finally
            {
                _allowNextComplete = false;
                _advancing = false;
            }
        }

        private static bool TryForceCompleteAnyOpen()
        {
            if (Game.I == null || Game.I.tutorialController == null)
                return false;

            TutorialController tc = Game.I.tutorialController;
            if (!tc.IsEnabled() || !tc.IsCurrentlyShowing())
                return false;

            TutorialInstance current = tc.CurrentTutorialInstance;
            if (current == null)
                return false;

            current.Complete(forceCompletion: true);
            return true;
        }

        public void Tick()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return;

            if (!_session.IsHost &&
                ModMain.Instance != null && ModMain.Instance.TurnSync != null &&
                ModMain.Instance.TurnSync.ShouldAbortPassTurnWaits &&
                (ShowWaitingPrompt ||
                 (Game.I != null && Game.I.tutorialController != null &&
                  Game.I.tutorialController.IsCurrentlyShowing())))
            {
                ForceAbortWait("tick-host-ahead");
            }

            if (!_session.IsHost && HostIsAheadOfLocalOpen())
            {
                try
                {
                    DotAgeCoop.Hooks.CutsceneSyncPatches.TryForceCloseEventsTree();
                }
                catch
                {
                }
            }

            if (!string.IsNullOrEmpty(_pendingAdvanceStepId) && Time.unscaledTime < _pendingAdvanceUntil)
            {
                _allowNextComplete = true;
                try
                {
                    if (TryForceCompleteNow(_pendingAdvanceStepId))
                    {
                        _log.Msg("[Dialogue] Applied pending advance " + _pendingAdvanceStepId);
                        _pendingAdvanceStepId = string.Empty;
                        _pendingAdvanceUntil = 0f;
                    }
                }
                finally
                {
                    _allowNextComplete = false;
                }
            }
            else if (!string.IsNullOrEmpty(_pendingAdvanceStepId) && Time.unscaledTime >= _pendingAdvanceUntil)
            {
                _log.Warning("[Dialogue] Pending advance expired: " + _pendingAdvanceStepId);
                _pendingAdvanceStepId = string.Empty;
            }

            if (!_session.IsHost && _localReady && !_advancing &&
                _neededCount > 1 && _readyCount < _neededCount &&
                Time.unscaledTime >= _nextReadyResendAt)
            {
                _nextReadyResendAt = Time.unscaledTime + 2f;
                try
                {
                    string id = string.IsNullOrEmpty(_activeStepId) ? "dialogue" : _activeStepId;
                    _session.SendToHost(CoopMessageType.DialogueReady, CoopProtocol.StringPayload(id));
                }
                catch
                {
                }
            }

            if (_session.IsHost && _localReady && !_advancing &&
                _neededCount > 1 && _readyCount < _neededCount &&
                _readyDeadline > 0f && Time.unscaledTime >= _readyDeadline)
            {
                _log.Warning("[Dialogue] Still waiting " + _readyCount + "/" + _neededCount +
                             " for " + _activeStepId + " — resend (no force-advance)");
                byte[] statusPayload = CoopProtocol.PackDialogueReadyStatus(
                    string.IsNullOrEmpty(_activeStepId) ? _hostStepId : _activeStepId,
                    _readyCount,
                    _neededCount);
                _session.Broadcast(CoopMessageType.DialogueReadyStatus, statusPayload);

                _readyDeadline = Time.unscaledTime + 20f;
            }
        }

        public void OnDialogueShown(TutorialInstance instance)
        {
            if (!_session.Active || instance == null)
                return;

            string stepId = SafeStepId(instance);
            if (string.IsNullOrEmpty(stepId))
                return;

            if (IsHostSoloLoadingNarration(instance))
                return;

            if (_localOpenedStepId == stepId && _activeStepId == stepId)
                return;

            _activeStepId = stepId;
            _localOpenedStepId = stepId;
            _localReady = false;
            _readyCount = 0;
            _neededCount = Math.Max(1, _session.CoopMemberCount);
            _advancing = false;
            _readyDeadline = 0f;

            if (_session.IsHost)
            {
                _hostStepId = stepId;
                _readyPeers.Clear();
            }

            if (!string.IsNullOrEmpty(_pendingAdvanceStepId) &&
                (string.Equals(_pendingAdvanceStepId, stepId, StringComparison.Ordinal) ||
                 stepId.StartsWith(_pendingAdvanceStepId, StringComparison.Ordinal) ||
                 _pendingAdvanceStepId.StartsWith(stepId, StringComparison.Ordinal)))
            {
                try
                {
                    if (ModMain.Instance != null && ModMain.Instance.EventSync != null &&
                        (ModMain.Instance.EventSync.IsClientWaitingEventStage ||
                         ModMain.Instance.EventSync.IsEventDialogueSolo))
                    {
                        _pendingAdvanceStepId = string.Empty;
                    }
                }
                catch
                {
                }
            }

            if (!string.IsNullOrEmpty(_pendingAdvanceStepId) &&
                (string.Equals(_pendingAdvanceStepId, stepId, StringComparison.Ordinal) ||
                 stepId.StartsWith(_pendingAdvanceStepId, StringComparison.Ordinal) ||
                 _pendingAdvanceStepId.StartsWith(stepId, StringComparison.Ordinal)))
            {
                string pending = _pendingAdvanceStepId;
                _pendingAdvanceStepId = string.Empty;
                ApplyAdvance(pending);
            }

            _log.Msg("[Dialogue] Step open: " + stepId);
        }

        public bool ShouldAllowComplete(TutorialInstance instance, bool forceCompletion)
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return true;

            if (_allowNextComplete || _advancing)
                return true;

            try
            {
                if (ModMain.Instance != null && ModMain.Instance.EventSync != null &&
                    ModMain.Instance.EventSync.IsEventDialogueSolo)
                    return true;
            }
            catch
            {
            }

            if (IsHostSoloLoadingNarration(instance))
                return true;

            try
            {
                if (Game.I != null && Game.I.GameCutscenesHandler != null &&
                    Game.I.GameCutscenesHandler.SkipCurrentCutscene)
                {

                    string skipId = SafeStepId(instance);
                    if (string.IsNullOrEmpty(skipId))
                        skipId = _activeStepId;
                    if (string.IsNullOrEmpty(skipId))
                        skipId = "dialogue";
                    RequestLocalReady(skipId);
                    return false;
                }
            }
            catch
            {
            }

            if (!forceCompletion)
            {
                try
                {
                    TutorialMessage msg = Game.I != null && Game.I.tutorialController != null
                        ? Game.I.tutorialController.message
                        : null;
                    if (msg != null && msg.textUI != null && !msg.textUI.IsCompleted())
                        return true;
                }
                catch
                {
                    return true;
                }
            }

            string stepId = SafeStepId(instance);
            if (string.IsNullOrEmpty(stepId))
                stepId = _activeStepId;
            if (string.IsNullOrEmpty(stepId))
                stepId = "dialogue";

            RequestLocalReady(stepId);
            return false;
        }

        private static bool IsHostSoloLoadingNarration(TutorialInstance instance)
        {
            try
            {
                if (Game.I != null && Game.I.mapController != null && Game.I.mapController.isNarrating)
                    return true;
            }
            catch
            {
            }

            string key = SafeStepId(instance);
            if (string.IsNullOrEmpty(key))
                return false;

            string k = key.ToLowerInvariant();
            if (k.StartsWith("cutscene_loading") || k.StartsWith("cutscene_resume"))
                return true;

            if (k.StartsWith("loading") && k.Length <= 8 && char.IsDigit(k[k.Length - 1]))
                return true;

            if (k.StartsWith("resume") && k.Length <= 7 && char.IsDigit(k[k.Length - 1]))
                return true;

            return false;
        }

        private void RequestLocalReady(string stepId)
        {
            if (_localReady && _activeStepId == stepId)
                return;

            _activeStepId = stepId;
            _localReady = true;
            _neededCount = Math.Max(1, _session.CoopMemberCount);
            _readyCount = Math.Max(_readyCount, 1);
            _nextReadyResendAt = Time.unscaledTime + 2f;
            if (_session.IsHost)
                _readyDeadline = Time.unscaledTime + 25f;

            _log.Msg("[Dialogue] Local ready for " + stepId + " (need " + _neededCount + ")");

            if (_session.IsHost)
                MarkReady(_session.SelfId, stepId);
            else
                _session.SendToHost(CoopMessageType.DialogueReady, CoopProtocol.StringPayload(stepId));
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.DialogueReady:
                    if (!_session.IsHost)
                        return;
                    MarkReady(remote.m_SteamID, CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.DialogueReadyStatus:
                    DialogueReadyStatusPayload status;
                    if (!CoopProtocol.TryReadDialogueReadyStatus(payload, out status))
                        return;
                    if (!string.IsNullOrEmpty(status.StepId))
                    {
                        _activeStepId = status.StepId;
                        if (_session.IsHost)
                            _hostStepId = status.StepId;
                        else if (string.IsNullOrEmpty(_hostStepId) ||
                                 !string.Equals(_hostStepId, status.StepId, StringComparison.Ordinal))
                            _hostStepId = status.StepId;
                    }
                    _readyCount = status.ReadyCount;
                    _neededCount = Math.Max(1, status.NeededCount);

                    if (!_session.IsHost && HostIsAheadOfLocalOpen())
                    {
                        try
                        {
                            DotAgeCoop.Hooks.CutsceneSyncPatches.TryForceCloseEventsTree();
                        }
                        catch
                        {
                        }
                    }
                    break;

                case CoopMessageType.DialogueAdvance:
                    ApplyAdvance(CoopProtocol.ReadString(payload));
                    break;
            }
        }

        private void MarkReady(ulong peerId, string stepId)
        {
            if (string.IsNullOrEmpty(stepId))
                stepId = _hostStepId;
            if (string.IsNullOrEmpty(stepId))
                stepId = "dialogue";

            if (!string.IsNullOrEmpty(_hostStepId) &&
                !string.Equals(_hostStepId, stepId, StringComparison.Ordinal))
            {
                _log.Warning("[Dialogue] Ready step mismatch peer='" + stepId +
                             "' host='" + _hostStepId + "' — coerce to host step");
                stepId = _hostStepId;
            }
            else if (string.IsNullOrEmpty(_hostStepId))
            {
                _hostStepId = stepId;
            }

            if (peerId == 0)
            {
                _log.Warning("[Dialogue] Ignoring ready with peerId=0");
                return;
            }

            _readyPeers.Add(peerId);
            int needed = Math.Max(1, _session.CoopMemberCount);
            int ready = _readyPeers.Count;
            _readyCount = ready;
            _neededCount = needed;
            _activeStepId = stepId;
            if (_readyDeadline <= 0f)
                _readyDeadline = Time.unscaledTime + 25f;

            _log.Msg("[Dialogue] Ready " + ready + "/" + needed + " for " + stepId);

            byte[] statusPayload = CoopProtocol.PackDialogueReadyStatus(stepId, ready, needed);
            _session.Broadcast(CoopMessageType.DialogueReadyStatus, statusPayload);

            if (ready >= needed)
                HostAdvance(stepId);
        }

        private void HostAdvance(string stepId)
        {
            _log.Msg("[Dialogue] All ready — advance " + stepId);
            _session.Broadcast(CoopMessageType.DialogueAdvance, CoopProtocol.StringPayload(stepId));
            ApplyAdvance(stepId);
        }

        private void ApplyAdvance(string stepId)
        {
            if (_advancing)
                return;

            try
            {
                if (ModMain.Instance != null && ModMain.Instance.EventSync != null &&
                    ModMain.Instance.EventSync.IsClientWaitingEventStage)
                {
                    _log.Msg("[Dialogue] Drop Advance while client waits event stage: " + stepId);
                    return;
                }
            }
            catch
            {
            }

            _advancing = true;
            _localReady = false;
            _readyCount = 0;
            _readyDeadline = 0f;
            _readyPeers.Clear();
            if (!string.IsNullOrEmpty(stepId) &&
                string.Equals(_localOpenedStepId, stepId, StringComparison.Ordinal))
                _localOpenedStepId = string.Empty;

            try
            {
                _allowNextComplete = true;
                if (!TryForceCompleteNow(stepId))
                {

                    _pendingAdvanceStepId = stepId;
                    _pendingAdvanceUntil = Time.unscaledTime + 8f;
                    _log.Warning("[Dialogue] Advance deferred (box not ready): " + stepId);
                }
            }
            catch (Exception ex)
            {
                _log.Error("[Dialogue] Advance failed: " + ex);
            }
            finally
            {
                _allowNextComplete = false;
                _advancing = false;
            }
        }

        private static bool TryForceCompleteNow(string stepId)
        {
            if (Game.I == null || Game.I.tutorialController == null)
                return false;

            TutorialController tc = Game.I.tutorialController;
            if (!tc.IsEnabled() || !tc.IsCurrentlyShowing())
                return false;

            TutorialInstance current = tc.CurrentTutorialInstance;
            if (current == null)
                return false;

            string currentKey = SafeStepId(current);
            if (!string.IsNullOrEmpty(stepId) && !string.IsNullOrEmpty(currentKey) &&
                !string.Equals(stepId, currentKey, StringComparison.Ordinal) &&
                !currentKey.StartsWith(stepId, StringComparison.Ordinal) &&
                !stepId.StartsWith(currentKey, StringComparison.Ordinal))
            {
                MelonLogger.Warning("[DotAgeCoop] Dialogue advance key mismatch: got " +
                                    stepId + " current " + currentKey);
            }

            current.Complete(forceCompletion: true);
            return true;
        }

        private static string SafeStepId(TutorialInstance instance)
        {
            try
            {
                if (instance == null)
                    return string.Empty;
                if (!string.IsNullOrEmpty(instance.key))
                    return instance.key;
                return instance.name;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void DrawWaitingOverlay()
        {
            if (!ShowWaitingPrompt)
                return;

            float w = 420f;
            float h = 48f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.18f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            string text = "Диалог: готовы… (" + _readyCount + "/" + _neededCount + ")";
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 18;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.92f, 0.75f, 1f);
            GUI.Label(r, text, style);
            GUI.color = prev;
        }
    }
}
