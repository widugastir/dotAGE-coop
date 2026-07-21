using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public enum TurnPhase
    {

        Idle = 0,

        EndTurnGathering = 1,

        PassTurnRunning = 2,

        MorningLocal = 3,

        MorningBarrier = 4,

        RosterSync = 5
    }

    public sealed class TurnSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private readonly HashSet<ulong> _readyPeers = new HashSet<ulong>();
        private readonly HashSet<ulong> _morningDonePeers = new HashSet<ulong>();
        private readonly HashSet<ulong> _rosterAckPeers = new HashSet<ulong>();

        private string _turnId = string.Empty;
        private string _morningId = string.Empty;
        private bool _localReady;
        private bool _allowExecute;
        private bool _executing;

        private bool _passTurnPastMidpoint;
        private int _readyCount;
        private int _neededCount = 1;

        private bool _waitingForMorningPeers;
        private bool _morningUnlocked;
        private bool _rosterSyncing;

        private bool _localMorningDone;

        private bool _blockWorldUntilMorningReady;
        private int _morningReadyCount;
        private int _morningNeededCount = 1;
        private float _morningWaitDeadline;
        private float _rosterSyncDeadline;
        private float _nextPeerDoneResendAt;
        private float _peerDoneSentAt;
        private bool _morningPullReceived;
        private float _nextMorningAllReadyResendAt;
        private int _morningAllReadyResendsLeft;
        private byte[] _lastMorningAllReadyPacket;

        private bool _morningWorldSyncStarted;
        private float _nextMorningSnapshotResendAt;

        private float _nextMorningPeerNudgeAt;

        private bool _abortPassTurnWaits;

        private bool _abortPassTurnSameDay;
        private int _abortPassTurnHostDay;

        public static bool BlocksPlayInput { get; private set; }

        public TurnPhase CurrentPhase
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return TurnPhase.Idle;

                if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                    ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                    return TurnPhase.Idle;

                if (_rosterSyncing && !_morningUnlocked)
                    return TurnPhase.RosterSync;

                bool passRunning = false;
                try { passRunning = IsPassTurnRunning(); }
                catch { }

                if (_executing || passRunning)
                {

                    if (_passTurnPastMidpoint && _blockWorldUntilMorningReady && !_morningUnlocked)
                        return TurnPhase.MorningLocal;
                    return TurnPhase.PassTurnRunning;
                }

                if (_waitingForMorningPeers && !_morningUnlocked && _localMorningDone)
                    return TurnPhase.MorningBarrier;

                if (!_executing &&
                    _neededCount > 1 &&
                    _readyCount > 0 &&
                    _readyCount < _neededCount)
                    return TurnPhase.EndTurnGathering;

                if (_blockWorldUntilMorningReady && !_morningUnlocked)
                    return TurnPhase.MorningBarrier;

                return TurnPhase.Idle;
            }
        }

        public bool BlocksWorldInteraction
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;
                return IsWorldPlayLocked;
            }
        }

        private bool IsWorldPlayLocked
        {
            get
            {
                TurnPhase phase = CurrentPhase;
                if (phase == TurnPhase.Idle)
                    return false;

                if (phase == TurnPhase.EndTurnGathering)
                    return _localReady;
                return true;
            }
        }

        public bool BlocksWorldPlayUntilMorningReady
        {
            get { return BlocksWorldInteraction; }
        }

        public bool HardBlockGameMouse
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;
                return CurrentPhase == TurnPhase.RosterSync;
            }
        }

        public bool ShowWaitingPrompt
        {
            get { return HardBlockGameMouse; }
        }

        public bool ShowReadyStatusBanner
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;
                if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                    ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                    return false;

                TurnPhase phase = CurrentPhase;
                return phase == TurnPhase.EndTurnGathering ||
                       phase == TurnPhase.MorningBarrier ||
                       phase == TurnPhase.RosterSync ||
                       phase == TurnPhase.MorningLocal;
            }
        }

        public int ReadyCount
        {
            get
            {
                TurnPhase phase = CurrentPhase;
                if (phase == TurnPhase.RosterSync)
                    return _rosterAckPeers.Count > 0 ? _rosterAckPeers.Count : 1;

                if (phase == TurnPhase.MorningBarrier)
                    return _morningReadyCount;
                return _readyCount;
            }
        }

        public int NeededCount
        {
            get
            {
                TurnPhase phase = CurrentPhase;
                if (phase == TurnPhase.RosterSync)
                    return Math.Max(1, _session.CoopMemberCount);
                if (phase == TurnPhase.MorningBarrier)
                    return Math.Max(1, _morningNeededCount);
                return _neededCount;
            }
        }

        public bool IsPassTurnPipelineActive
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;
                TurnPhase phase = CurrentPhase;
                return phase == TurnPhase.PassTurnRunning ||
                       phase == TurnPhase.MorningLocal ||
                       phase == TurnPhase.MorningBarrier ||
                       phase == TurnPhase.RosterSync;
            }
        }

        public bool PassTurnInFlight
        {
            get { return IsPassTurnPipelineActive; }
        }

        public bool SuppressesPipWorkMotion
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;
                try
                {
                    if (Game.I != null && Game.I.IsPassNightTime())
                        return true;
                }
                catch
                {
                }
                TurnPhase phase = CurrentPhase;
                return phase == TurnPhase.PassTurnRunning;
            }
        }

        public bool PassTurnExecuteInFlight
        {
            get { return _allowExecute || _executing; }
        }

        public bool AllowsMorningResourceApply
        {
            get
            {
                if (_morningUnlocked || _localMorningDone)
                    return true;
                TurnPhase phase = CurrentPhase;
                return phase == TurnPhase.MorningLocal ||
                       phase == TurnPhase.MorningBarrier ||
                       phase == TurnPhase.RosterSync;
            }
        }

        public bool ShouldAbortPassTurnWaits
        {
            get
            {
                if (!_abortPassTurnWaits || _session.IsHost)
                    return false;
                if (_abortPassTurnSameDay)
                    return true;
                int local = SafeCurrentDayInt();
                return _abortPassTurnHostDay > local;
            }
        }

        public void ClearAbortPassTurnWaits()
        {
            _abortPassTurnWaits = false;
            _abortPassTurnSameDay = false;
            _abortPassTurnHostDay = 0;
        }

        public void RequestAbortPassTurnWaits(int hostDay, string reason, bool sameDayOk = false)
        {
            if (_session.IsHost || !_session.Active)
                return;

            _abortPassTurnWaits = true;
            _abortPassTurnSameDay = sameDayOk;
            if (hostDay > 0)
                _abortPassTurnHostDay = Math.Max(_abortPassTurnHostDay, hostDay);

            _log.Warning("[Turn] Abort PassTurn waits (" +
                         (string.IsNullOrEmpty(reason) ? "?" : reason) +
                         ", hostDay=" + hostDay + ", sameDay=" + sameDayOk + ")");
            try
            {
                if (ModMain.Instance != null && ModMain.Instance.DialogueSync != null)
                    ModMain.Instance.DialogueSync.ForceAbortWait(reason);
                if (ModMain.Instance != null && ModMain.Instance.EventSync != null)
                    ModMain.Instance.EventSync.ForceUnstick(reason);
            }
            catch
            {
            }
        }

        public bool BlocksContextActions
        {
            get
            {
                if (!_session.Active || !_session.HasCoopPartner)
                    return false;
                if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                    ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                    return true;
                return IsWorldPlayLocked;
            }
        }

        public TurnSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {
            BlocksPlayInput = HardBlockGameMouse;
            TryClearStaleMorningLock();
            RefreshPassTurnButtonInteractable();
            if (BlocksContextActions)
                TryHideContextActionMenu();

            if (!_session.IsHost && _abortPassTurnWaits)
            {
                int localDay = SafeCurrentDayInt();
                bool passRunning = IsPassTurnRunning() || _executing;
                if (_abortPassTurnSameDay)
                {

                    if (!passRunning || _localMorningDone || _morningUnlocked)
                        ClearAbortPassTurnWaits();
                }
                else if (_abortPassTurnHostDay > 0 && _abortPassTurnHostDay <= localDay)
                {
                    ClearAbortPassTurnWaits();
                }
                else if (_abortPassTurnHostDay > localDay && !passRunning)
                {
                    _log.Warning("[Turn] Host was on day " + _abortPassTurnHostDay +
                                 " while we lagged on " + localDay + " — catch-up now");
                    int catchDay = _abortPassTurnHostDay;
                    ClearAbortPassTurnWaits();
                    ApplyExecute("catchup_after_abort_" + catchDay);
                }
            }

            if (_session.IsHost && _morningAllReadyResendsLeft > 0 &&
                _lastMorningAllReadyPacket != null &&
                Time.unscaledTime >= _nextMorningAllReadyResendAt)
            {
                _morningAllReadyResendsLeft--;
                _nextMorningAllReadyResendAt = Time.unscaledTime + 1.5f;
                try
                {
                    _session.Broadcast(CoopMessageType.PassTurnMorningAllReady, _lastMorningAllReadyPacket);
                    _log.Msg("[Turn] Resend MorningAllReady (left=" + _morningAllReadyResendsLeft + ")");
                }
                catch
                {
                }
            }

            if (!_session.IsHost && _waitingForMorningPeers && !_morningUnlocked && _localMorningDone &&
                Time.unscaledTime >= _nextPeerDoneResendAt)
            {
                _nextPeerDoneResendAt = Time.unscaledTime + 2f;
                try
                {
                    string id = string.IsNullOrEmpty(_morningId)
                        ? "morning_" + SafeCurrentDay()
                        : _morningId;
                    _session.SendToHost(CoopMessageType.PassTurnPeerDone, CoopProtocol.StringPayload(id));

                    if (_morningReadyCount >= _morningNeededCount && !_morningPullReceived)
                    {
                        _session.SendToHost(
                            CoopMessageType.MorningStateRequest,
                            CoopProtocol.StringPayload(id));
                    }
                }
                catch
                {
                }
            }

            if (_session.IsHost && _morningWorldSyncStarted && !_morningUnlocked &&
                _rosterAckPeers.Count < Math.Max(1, _session.CoopMemberCount) &&
                Time.unscaledTime >= _nextMorningSnapshotResendAt)
            {
                _nextMorningSnapshotResendAt = Time.unscaledTime + 3f;
                string id = string.IsNullOrEmpty(_morningId) ? "morning_" + SafeCurrentDay() : _morningId;
                PushMorningRosterOnlyBroadcast(id);
            }

            if (!_session.IsHost && !_morningUnlocked && _localMorningDone &&
                _morningPullReceived &&
                _morningNeededCount > 1 && _morningReadyCount >= _morningNeededCount &&
                _peerDoneSentAt > 0f && Time.unscaledTime >= _peerDoneSentAt + 8f)
            {
                _log.Warning("[Turn] Roster applied + 2/2 but no AllReady — unlocking locally");
                ApplyMorningUnlocked(string.IsNullOrEmpty(_morningId)
                    ? "morning_" + SafeCurrentDay()
                    : _morningId);
            }

            if (_session.IsHost && _waitingForMorningPeers && !_morningWorldSyncStarted &&
                !_morningUnlocked && _localMorningDone &&
                _morningDonePeers.Count < Math.Max(1, _session.CoopMemberCount) &&
                Time.unscaledTime >= _nextMorningPeerNudgeAt)
            {
                _nextMorningPeerNudgeAt = Time.unscaledTime + 8f;
                try
                {
                    string mid = string.IsNullOrEmpty(_morningId)
                        ? "morning_" + SafeCurrentDay()
                        : _morningId;
                    byte[] statusPayload = CoopProtocol.PackDialogueReadyStatus(
                        mid, _morningDonePeers.Count, Math.Max(1, _session.CoopMemberCount));
                    _session.Broadcast(CoopMessageType.PassTurnReadyStatus, statusPayload);
                    _session.Broadcast(
                        CoopMessageType.PassTurnCompleted,
                        CoopProtocol.StringPayload(SafeCurrentDay()));
                    _log.Msg("[Turn] Nudge lagging peer — morning " +
                             _morningDonePeers.Count + "/" + _session.CoopMemberCount);
                }
                catch
                {
                }
            }

            if (_session.IsHost && _waitingForMorningPeers && !_morningWorldSyncStarted &&
                !_morningUnlocked && Time.unscaledTime >= _morningWaitDeadline)
            {
                int needed = Math.Max(1, _session.CoopMemberCount);
                int have = _morningDonePeers.Count;
                if (have >= needed)
                {
                    HostBeginMorningWorldSync();
                }
                else
                {
                    _log.Warning("[Turn] Morning still " + have + "/" + needed +
                                 " — extending wait + nudge client PassTurn");
                    _morningWaitDeadline = Time.unscaledTime + 60f;
                    try
                    {
                        string mid = string.IsNullOrEmpty(_morningId)
                            ? "morning_" + SafeCurrentDay()
                            : _morningId;
                        byte[] statusPayload = CoopProtocol.PackDialogueReadyStatus(mid, have, needed);
                        _session.Broadcast(CoopMessageType.PassTurnReadyStatus, statusPayload);

                        _session.Broadcast(
                            CoopMessageType.PassTurnCompleted,
                            CoopProtocol.StringPayload(SafeCurrentDay()));
                    }
                    catch
                    {
                    }
                }
            }

            if (_session.IsHost && _rosterSyncing && !_morningUnlocked &&
                Time.unscaledTime >= _rosterSyncDeadline)
            {
                _log.Warning("[Turn] Roster-ack timeout — unlocking day (snapshot was sent)");
                HostFinishMorningUnlock();
            }

            if (!_session.IsHost && _waitingForMorningPeers && !_morningUnlocked && _localMorningDone &&
                Time.unscaledTime >= _morningWaitDeadline)
            {
                _log.Warning("[Turn] Client still waiting morning " + _morningReadyCount + "/" +
                             _morningNeededCount + " roster=" + _morningPullReceived + " — extending");
                _morningWaitDeadline = Time.unscaledTime + 60f;
                try
                {
                    string id = string.IsNullOrEmpty(_morningId)
                        ? "morning_" + SafeCurrentDay()
                        : _morningId;
                    _session.SendToHost(CoopMessageType.PassTurnPeerDone, CoopProtocol.StringPayload(id));
                    if (_morningReadyCount >= _morningNeededCount)
                    {
                        _session.SendToHost(
                            CoopMessageType.MorningStateRequest,
                            CoopProtocol.StringPayload(id));
                    }
                }
                catch
                {
                }
            }
        }

        public void OnReturnedToMain()
        {
            ClearWaitingGate("return-to-main");
        }

        public void ClearWaitingGate(string reason)
        {
            ResetGate();
            ResetMorningGate();
            _blockWorldUntilMorningReady = false;
            BlocksPlayInput = false;
            ClearAbortPassTurnWaits();
            if (!string.IsNullOrEmpty(reason))
                _log.Msg("[Turn] Cleared waiting gate (" + reason + ")");
        }

        public bool ShouldAllowGoToNextDay()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return true;

            if (_allowExecute || _executing)
                return true;

            if (ShouldBlockPassTurnButton())
                return false;

            RequestLocalReady();
            return false;
        }

        public bool ShouldAllowPassTurn()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return true;

            if (_allowExecute || _executing)
                return true;

            if (ShouldBlockPassTurnButton())
                return false;

            RequestLocalReady();
            return false;
        }

        public bool ShouldBlockPassTurnButton()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return false;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                return true;
            try
            {
                if (HardSyncService.BlocksPlayInput)
                    return true;
                if (ModMain.Instance != null && ModMain.Instance.HardSync != null &&
                    ModMain.Instance.HardSync.IsActive)
                    return true;
            }
            catch
            {
            }

            TurnPhase phase = CurrentPhase;
            if (phase == TurnPhase.Idle)
                return false;

            if (phase == TurnPhase.EndTurnGathering && !_localReady)
                return false;
            return true;
        }

        private void TryClearStaleMorningLock()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return;
            if (_morningUnlocked && !_blockWorldUntilMorningReady)
                return;
            if (_waitingForMorningPeers || _rosterSyncing || _executing || _localReady)
                return;
            try
            {
                if (IsPassTurnRunning())
                    return;
                if (Game.I == null || !Game.I.IsPlayTime())
                    return;
            }
            catch
            {
                return;
            }

            _log.Warning("[Turn] Stale morning lock during play time — clearing " +
                         "(unlocked=" + _morningUnlocked + " block=" + _blockWorldUntilMorningReady + ")");
            ApplyMorningUnlocked(string.IsNullOrEmpty(_morningId)
                ? "morning_" + SafeCurrentDay()
                : _morningId);
        }

        private static void TryHideContextActionMenu()
        {
            try
            {
                if (Game.I == null || Game.I.buildingActionMenu == null)
                    return;
                if (!Game.I.buildingActionMenu.IsHidden())
                    Game.I.buildingActionMenu.Hide();
            }
            catch
            {
            }
        }

        public void RefreshPassTurnButtonInteractable()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return;
            try
            {
                if (Game.I == null || Game.I.dayTimeAnimation == null)
                    return;
                GameObject btn = Game.I.dayTimeAnimation.nextDayButton;
                if (btn == null)
                    return;

                bool allow = !ShouldBlockPassTurnButton();

                var selectable = btn.GetComponent<UnityEngine.UI.Selectable>();
                if (selectable != null)
                    selectable.interactable = allow;

                var graphic = btn.GetComponent<UnityEngine.UI.Graphic>();
                if (graphic != null)
                    graphic.raycastTarget = allow;

                if (graphic != null)
                {
                    Color c = graphic.color;
                    c.a = allow ? 1f : 0.45f;
                    graphic.color = c;
                }
            }
            catch
            {
            }
        }

        public void OnLocalPassTurnMidpoint()
        {
            if (!_session.Active)
            {
                ResetGate();
                return;
            }

            _morningUnlocked = false;
            _blockWorldUntilMorningReady = true;
            _passTurnPastMidpoint = true;
            ArmMorningWorldBlock("pass-turn-midpoint");

            string day = SafeCurrentDay();
            if (_session.IsHost)
            {
                _session.Broadcast(CoopMessageType.PassTurnCompleted, CoopProtocol.StringPayload(day));
                _log.Msg("[Turn] Host CompletePassTurn midpoint (day " + day + ") — unlocks may still show");

                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                {
                    int dayNum;
                    if (int.TryParse(day, out dayNum))
                        ModMain.Instance.GameSync.NotifyHostDayAdvanced(dayNum);
                }

                if (ModMain.Instance != null && ModMain.Instance.ResearchSync != null)
                    ModMain.Instance.ResearchSync.MarkDirty();
                if (ModMain.Instance != null && ModMain.Instance.ScalesSync != null)
                    ModMain.Instance.ScalesSync.MarkScalesDirty();

            }
            else
            {
                _log.Msg("[Turn] Client CompletePassTurn midpoint (day " + day + ")");
            }

            ResetGate();
        }

        public IEnumerator WrapPassTurnCoroutine(IEnumerator original)
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
                        _log.Error("[Turn] PassTurnCO threw: " + ex);
                        break;
                    }

                    if (!moved)
                        break;
                    yield return current;
                }
            }

            if (!_session.Active || !_session.HasCoopPartner)
                yield break;

            yield return WaitForMorningPeersCO();
        }

        private IEnumerator WaitForMorningPeersCO()
        {
            string morningId = "morning_" + SafeCurrentDay();

            if (_morningUnlocked)
            {
                _log.Msg("[Turn] Morning already unlocked (id=" +
                         (string.IsNullOrEmpty(_morningId) ? "?" : _morningId) +
                         ", local=" + morningId + ") — skip peer-wait re-entry");
                ReleaseMorningWaitLocals();
                yield break;
            }

            if (!_blockWorldUntilMorningReady)
                ArmMorningWorldBlock("pass-turn-finished");

            _waitingForMorningPeers = true;
            _rosterSyncing = false;
            _morningId = morningId;
            _morningNeededCount = Math.Max(1, _session.CoopMemberCount);
            _morningReadyCount = Math.Max(1, _morningDonePeers.Count);
            _morningWaitDeadline = Time.unscaledTime + 120f;
            _nextMorningPeerNudgeAt = Time.unscaledTime + 8f;

            _localMorningDone = true;
            _nextPeerDoneResendAt = Time.unscaledTime + 1.5f;
            _peerDoneSentAt = Time.unscaledTime;

            _rosterSyncing = false;

            _log.Msg("[Turn] Local morning fully done — PeerDone, waiting for morning sync (" +
                     _morningId + ")");

            if (_session.IsHost)
            {

                MarkMorningDone(_session.SelfId, _morningId);
            }
            else
            {
                _session.SendToHost(CoopMessageType.PassTurnPeerDone, CoopProtocol.StringPayload(_morningId));
            }

            while (!_morningUnlocked)
                yield return null;

            _log.Msg("[Turn] Morning barrier released — day play unlocked");
        }

        private void HandleMorningStateRequest(CSteamID remote, byte[] payload)
        {
            if (!_session.IsHost || _morningUnlocked)
                return;
            if (!_morningWorldSyncStarted && _morningDonePeers.Count < Math.Max(1, _session.CoopMemberCount))
            {
                _log.Msg("[Turn] MorningStateRequest ignored — waiting for all PeerDone first");
                return;
            }

            string morningId = CoopProtocol.ReadString(payload);
            if (string.IsNullOrEmpty(morningId))
                morningId = string.IsNullOrEmpty(_morningId) ? "morning_" + SafeCurrentDay() : _morningId;

            PushMorningWorldSnapshotTo(remote, morningId, "client-request");
        }

        private void HostBeginMorningWorldSync()
        {
            if (!_session.IsHost || _morningUnlocked || _morningWorldSyncStarted)
                return;

            _morningWorldSyncStarted = true;
            _rosterSyncing = true;
            _rosterAckPeers.Clear();
            _rosterSyncDeadline = Time.unscaledTime + 60f;
            _nextMorningSnapshotResendAt = Time.unscaledTime + 3f;

            string morningId = string.IsNullOrEmpty(_morningId) ? "morning_" + SafeCurrentDay() : _morningId;
            _log.Msg("[Turn] All PeerDone — pushing morning world snapshot (" + morningId + ")");

            PushMorningWorldSnapshotBroadcast(morningId);

            MarkRosterAck(_session.SelfId, morningId);
        }

        private void PushMorningWorldSnapshotBroadcast(string morningId)
        {
            PushMorningWorldSnapshotTo(CSteamID.Nil, morningId, "broadcast");
        }

        private void PushMorningRosterOnlyBroadcast(string morningId)
        {
            if (!_session.IsHost)
                return;
            try
            {
                int buildingCount = 0;
                byte[] rosterPacket;
                if (ModMain.Instance != null && ModMain.Instance.PipOrders != null)
                    rosterPacket = ModMain.Instance.PipOrders.BuildMorningRosterPacket(morningId, out buildingCount);
                else
                    rosterPacket = CoopProtocol.PackMorningRoster(morningId, new PipOrderPayload[0]);
                _session.Broadcast(CoopMessageType.PassTurnMorningRoster, rosterPacket);
                _log.Msg("[Turn] Morning roster resend buildings=" + buildingCount);
            }
            catch (Exception ex)
            {
                _log.Warning("[Turn] Morning roster resend failed: " + ex.Message);
            }
        }

        private void PushMorningWorldSnapshotTo(CSteamID remote, string morningId, string reason)
        {
            if (!_session.IsHost)
                return;

            bool broadcast = !remote.IsValid() || remote == CSteamID.Nil;

            try
            {

                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                {
                    if (broadcast)
                        ModMain.Instance.GameSync.BroadcastWorldBuildingsSnapshot();
                    else
                        ModMain.Instance.GameSync.SendBuildingsTo(remote);
                }

                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                {
                    if (broadcast)
                    {
                        ModMain.Instance.GameSync.BroadcastFoodSnapshot();
                        ModMain.Instance.GameSync.BroadcastResourcesSnapshot(forceDuringPassTurn: true);
                    }
                    else
                    {
                        ModMain.Instance.GameSync.SendResourcesAndFoodTo(remote);
                    }
                }

                if (ModMain.Instance != null && ModMain.Instance.ResearchSync != null)
                {
                    if (broadcast)
                        ModMain.Instance.ResearchSync.BroadcastSnapshotImmediate();
                    else
                        ModMain.Instance.ResearchSync.SendSnapshotTo(remote);
                }

                if (ModMain.Instance != null && ModMain.Instance.ScalesSync != null)
                    ModMain.Instance.ScalesSync.BroadcastScalesSnapshotImmediate(forceDuringPassTurn: true);

                int buildingCount = 0;
                byte[] rosterPacket;
                if (ModMain.Instance != null && ModMain.Instance.PipOrders != null)
                    rosterPacket = ModMain.Instance.PipOrders.BuildMorningRosterPacket(morningId, out buildingCount);
                else
                    rosterPacket = CoopProtocol.PackMorningRoster(morningId, new PipOrderPayload[0]);

                if (broadcast)
                    _session.Broadcast(CoopMessageType.PassTurnMorningRoster, rosterPacket);
                else
                    _session.SendTo(remote, CoopMessageType.PassTurnMorningRoster, rosterPacket);

                _log.Msg("[Turn] Morning world snapshot (" + reason + ") rosterBuildings=" +
                         buildingCount + (broadcast ? " broadcast" : " → " + remote.m_SteamID));
            }
            catch (Exception ex)
            {
                _log.Warning("[Turn] Morning world snapshot failed: " + ex.Message);
            }
        }

        private void ArmMorningWorldBlock(string reason)
        {
            if (!_session.Active || !_session.HasCoopPartner)
                return;

            if (_morningUnlocked)
            {
                _log.Msg("[Turn] Skip arm world-block — already unlocked (" + reason + ")");
                return;
            }

            _blockWorldUntilMorningReady = true;

            _log.Msg("[Turn] Armed morning world-block (" + reason + ")");
        }

        private void ReleaseMorningWaitLocals()
        {
            _waitingForMorningPeers = false;
            _rosterSyncing = false;
            _localMorningDone = false;
            _blockWorldUntilMorningReady = false;
            _passTurnPastMidpoint = false;
            BlocksPlayInput = false;
            _peerDoneSentAt = 0f;
        }

        private void RequestLocalReady()
        {
            string turnId = CurrentTurnId();
            if (_localReady && _turnId == turnId)
                return;

            if (_morningUnlocked || (!_waitingForMorningPeers && !_rosterSyncing))
                ResetMorningGateSoft();

            _turnId = turnId;
            _localReady = true;
            _neededCount = Math.Max(1, _session.CoopMemberCount);
            _readyCount = Math.Max(_readyCount, 1);

            _log.Msg("[Turn] Local ready for " + turnId + " (need " + _neededCount + ")");

            if (_session.IsHost)
                MarkReady(_session.SelfId, turnId);
            else
                _session.SendToHost(CoopMessageType.PassTurnRequest, CoopProtocol.StringPayload(turnId));
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.PassTurnRequest:
                    if (!_session.IsHost)
                        return;
                    MarkReady(remote.m_SteamID, CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.PassTurnReadyStatus:
                    DialogueReadyStatusPayload status;
                    if (!CoopProtocol.TryReadDialogueReadyStatus(payload, out status))
                        return;
                    if (!string.IsNullOrEmpty(status.StepId))
                    {
                        if (status.StepId.StartsWith("morning_", StringComparison.Ordinal))
                        {
                            _morningId = status.StepId;
                            _morningReadyCount = status.ReadyCount;
                            _morningNeededCount = Math.Max(1, status.NeededCount);

                            if (_localMorningDone || _morningUnlocked)
                                _waitingForMorningPeers = true;

                            TryCatchUpFromMorningId(status.StepId);

                            if (!_session.IsHost &&
                                status.ReadyCount >= 1 &&
                                (IsPassTurnRunning() || _executing) &&
                                !_localMorningDone &&
                                _passTurnPastMidpoint)
                            {
                                int hostDay = ParseMorningDay(status.StepId);
                                RequestAbortPassTurnWaits(
                                    hostDay > 0 ? hostDay : SafeCurrentDayInt(),
                                    "host-morning-peer-done",
                                    sameDayOk: true);
                            }
                        }
                        else
                        {
                            _turnId = status.StepId;
                            _readyCount = status.ReadyCount;
                            _neededCount = Math.Max(1, status.NeededCount);

                            _log.Msg("[Turn] Peer ready status " + status.ReadyCount + "/" +
                                     status.NeededCount + " for " + status.StepId +
                                     " (localReady=" + _localReady + ")");
                        }
                    }
                    else
                    {
                        _readyCount = status.ReadyCount;
                        _neededCount = Math.Max(1, status.NeededCount);
                    }
                    break;

                case CoopMessageType.PassTurnStarted:
                    ApplyExecute(CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.PassTurnPeerDone:
                    if (!_session.IsHost)
                        return;
                    MarkMorningDone(remote.m_SteamID, CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.PassTurnMorningRoster:
                    HandleMorningRoster(payload);
                    break;

                case CoopMessageType.PassTurnRosterAck:
                    if (!_session.IsHost)
                        return;
                    MarkRosterAck(remote.m_SteamID, CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.PassTurnMorningAllReady:
                    ApplyMorningUnlocked(CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.PassTurnCompleted:
                    OnHostPassTurnCompleted(CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.MorningStateRequest:
                    if (!_session.IsHost)
                        return;
                    HandleMorningStateRequest(remote, payload);
                    break;
            }
        }

        private void MarkMorningDone(ulong peerId, string morningId)
        {
            if (!_session.IsHost)
                return;
            if (_morningUnlocked)
                return;

            if (!string.IsNullOrEmpty(morningId) && morningId.StartsWith("morning_", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(_morningId) || !_morningId.StartsWith("morning_", StringComparison.Ordinal))
                {
                    _morningId = morningId;
                }
                else if (!string.Equals(_morningId, morningId, StringComparison.Ordinal))
                {

                    int curDay = ParseMorningDay(_morningId);
                    int incomingDay = ParseMorningDay(morningId);
                    if (incomingDay >= curDay)
                    {
                        _log.Msg("[Turn] Morning id migrate " + _morningId + " → " + morningId);
                        _morningId = morningId;
                    }
                    else
                    {
                        _log.Msg("[Turn] Accepting PeerDone id " + morningId +
                                 " (keeping " + _morningId + ")");
                    }
                }
            }

            if (peerId == 0)
            {
                _log.Warning("[Turn] Ignoring morning PeerDone with peerId=0");
                return;
            }

            _morningDonePeers.Add(peerId);
            int needed = Math.Max(1, _session.CoopMemberCount);
            _morningReadyCount = _morningDonePeers.Count;
            _morningNeededCount = needed;

            if (_localMorningDone)
                _waitingForMorningPeers = true;

            _log.Msg("[Turn] Morning done " + _morningReadyCount + "/" + needed + " (" + peerId +
                     ")" + (_localMorningDone ? "" : " — early, barrier deferred"));

            byte[] statusPayload = CoopProtocol.PackDialogueReadyStatus(
                string.IsNullOrEmpty(_morningId) ? "morning_" + SafeCurrentDay() : _morningId,
                _morningReadyCount,
                needed);
            _session.Broadcast(CoopMessageType.PassTurnReadyStatus, statusPayload);

            if (_morningDonePeers.Count >= needed)
                HostBeginMorningWorldSync();
        }

        private static int ParseMorningDay(string morningId)
        {
            if (string.IsNullOrEmpty(morningId) || !morningId.StartsWith("morning_", StringComparison.Ordinal))
                return -1;
            int day;
            if (int.TryParse(morningId.Substring("morning_".Length), out day))
                return day;
            return -1;
        }

        private void HandleMorningRoster(byte[] payload)
        {
            string morningId;
            PipOrderPayload[] entries;
            if (!CoopProtocol.TryReadMorningRoster(payload, out morningId, out entries))
            {
                _log.Warning("[Turn] Bad morning roster packet");
                return;
            }

            if (!string.IsNullOrEmpty(morningId))
                _morningId = morningId;

            TryCatchUpFromMorningId(morningId);

            if (!_morningUnlocked)
            {
                _blockWorldUntilMorningReady = true;
                _rosterSyncing = true;
            }

            _log.Msg("[Turn] Received host morning roster (" + morningId + ", buildings=" +
                     (entries != null ? entries.Length : 0) + ")");

            if (!_session.IsHost)
            {
                if (ModMain.Instance != null && ModMain.Instance.PipOrders != null)
                    ModMain.Instance.PipOrders.ApplyMorningRoster(entries);

                _morningPullReceived = true;

                try
                {
                    _session.SendToHost(CoopMessageType.PassTurnRosterAck, CoopProtocol.StringPayload(morningId));
                }
                catch
                {
                }
            }
        }

        private void MarkRosterAck(ulong peerId, string morningId)
        {
            if (!_session.IsHost)
                return;

            if (!_rosterSyncing && !_morningUnlocked)
            {

                if (string.IsNullOrEmpty(_morningId) && !string.IsNullOrEmpty(morningId))
                    _morningId = morningId;
            }

            if (!string.IsNullOrEmpty(morningId) &&
                !string.IsNullOrEmpty(_morningId) &&
                !string.Equals(_morningId, morningId, StringComparison.Ordinal))
            {

                int curDay = ParseMorningDay(_morningId);
                int incomingDay = ParseMorningDay(morningId);
                if (incomingDay >= curDay)
                {
                    _log.Msg("[Turn] Roster ACK id migrate " + _morningId + " → " + morningId);
                    _morningId = morningId;
                }
                else
                {
                    _log.Msg("[Turn] Accepting Roster ACK id " + morningId +
                             " (keeping " + _morningId + ")");
                }
            }

            _rosterAckPeers.Add(peerId);
            int needed = Math.Max(1, _session.CoopMemberCount);
            _log.Msg("[Turn] Roster ACK " + _rosterAckPeers.Count + "/" + needed + " (" + peerId + ")");

            if (_rosterAckPeers.Count >= needed)
                HostFinishMorningUnlock();
        }

        private void HostFinishMorningUnlock()
        {
            if (!_session.IsHost || _morningUnlocked)
                return;

            string morningId = string.IsNullOrEmpty(_morningId) ? "morning_" + SafeCurrentDay() : _morningId;
            _log.Msg("[Turn] All morning-ready — unlock day play (" + morningId + ")");

            if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
            {

                ModMain.Instance.GameSync.BroadcastFoodSnapshot();
                ModMain.Instance.GameSync.BroadcastResourcesSnapshot(forceDuringPassTurn: true);
            }

            _session.Broadcast(CoopMessageType.PassTurnMorningAllReady, CoopProtocol.StringPayload(morningId));
            _lastMorningAllReadyPacket = CoopProtocol.StringPayload(morningId);
            _morningAllReadyResendsLeft = 4;
            _nextMorningAllReadyResendAt = Time.unscaledTime + 0.75f;
            if (ModMain.Instance != null && ModMain.Instance.ScalesSync != null)
                ModMain.Instance.ScalesSync.BroadcastScalesSnapshotImmediate(forceDuringPassTurn: true);
            if (ModMain.Instance != null && ModMain.Instance.PipOrders != null)
                ModMain.Instance.PipOrders.ScheduleRetetherPasses("morning-unlock-host");
            ApplyMorningUnlocked(morningId);
        }

        private void ApplyMorningUnlocked(string morningId)
        {
            TryCatchUpFromMorningId(morningId);

            _morningUnlocked = true;
            ReleaseMorningWaitLocals();
            _morningDonePeers.Clear();
            _rosterAckPeers.Clear();
            _morningReadyCount = 0;
            _morningWorldSyncStarted = false;
            _morningPullReceived = false;
            if (!string.IsNullOrEmpty(morningId))
                _morningId = morningId;
            _log.Msg("[Turn] Applied morning unlock (" + morningId + ")");

            if (!_session.IsHost)
            {
                if (ModMain.Instance != null && ModMain.Instance.PipOrders != null)
                    ModMain.Instance.PipOrders.ScheduleRetetherPasses("morning-unlock");
                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                {

                    ModMain.Instance.GameSync.RefreshFoodUiFromCache();
                    ModMain.Instance.GameSync.ScheduleFoodUiRefresh(0.5f);
                }
            }
        }

        private void TryCatchUpFromMorningId(string morningId)
        {
            if (_session.IsHost || string.IsNullOrEmpty(morningId))
                return;
            if (!morningId.StartsWith("morning_", StringComparison.Ordinal))
                return;

            int hostDay;
            if (!int.TryParse(morningId.Substring("morning_".Length), out hostDay))
                return;

            int localDay = SafeCurrentDayInt();
            if (hostDay <= localDay)
                return;
            if (_executing || IsPassTurnRunning())
            {

                if (!_passTurnPastMidpoint)
                {
                    _log.Msg("[Turn] Morning day-ahead while pre-midpoint — not aborting");
                    return;
                }
                RequestAbortPassTurnWaits(hostDay, "morning-day-ahead", sameDayOk: false);
                return;
            }

            _log.Warning("[Turn] Morning packet for day " + hostDay + " but local day is " +
                         localDay + " — catch-up");
            ApplyExecute("catchup_morning_" + hostDay);
        }

        private void MarkReady(ulong peerId, string turnId)
        {
            if (string.IsNullOrEmpty(turnId))
                turnId = CurrentTurnId();

            if (_turnId != turnId)
            {
                _turnId = turnId;
                _readyPeers.Clear();
            }

            _readyPeers.Add(peerId);
            int needed = Math.Max(1, _session.CoopMemberCount);
            int ready = _readyPeers.Count;
            _readyCount = ready;
            _neededCount = needed;
            _turnId = turnId;

            _log.Msg("[Turn] Ready " + ready + "/" + needed + " for " + turnId);

            byte[] statusPayload = CoopProtocol.PackDialogueReadyStatus(turnId, ready, needed);
            _session.Broadcast(CoopMessageType.PassTurnReadyStatus, statusPayload);

            if (ready >= needed)
                HostExecute(turnId);
        }

        private void HostExecute(string turnId)
        {
            _log.Msg("[Turn] All ready — execute " + turnId);

            ResetMorningGate();
            ArmMorningWorldBlock("host-execute");
            _session.Broadcast(CoopMessageType.PassTurnStarted, CoopProtocol.StringPayload(turnId));
            ApplyExecute(turnId);
        }

        private void ApplyExecute(string turnId)
        {
            if (_executing)
                return;

            _executing = true;
            _localReady = false;

            _waitingForMorningPeers = false;
            _rosterSyncing = false;
            _localMorningDone = false;
            _passTurnPastMidpoint = false;
            _morningDonePeers.Clear();
            _rosterAckPeers.Clear();
            _morningReadyCount = 0;
            _morningNeededCount = Math.Max(1, _session.CoopMemberCount);
            _morningUnlocked = false;
            _peerDoneSentAt = 0f;
            _morningAllReadyResendsLeft = 0;
            _lastMorningAllReadyPacket = null;
            ArmMorningWorldBlock("apply-execute");

            try
            {
                _allowExecute = true;
                TryInvokeGoToNextDay();
            }
            catch (Exception ex)
            {
                _log.Error("[Turn] Execute failed: " + ex);
                ResetGate();
                _blockWorldUntilMorningReady = false;
                _passTurnPastMidpoint = false;
            }
            finally
            {
                _allowExecute = false;

            }

            if (ModMain.Instance != null)
            {
                try
                {
                    MelonLoader.MelonCoroutines.Start(ConfirmPassTurnStartedCO());
                }
                catch (Exception ex)
                {
                    _log.Warning("[Turn] ConfirmPassTurnStarted schedule: " + ex.Message);
                }
            }
        }

        private IEnumerator ConfirmPassTurnStartedCO()
        {

            yield return null;
            yield return null;
            yield return null;

            if (!_executing)
                yield break;

            if (IsPassTurnRunning())
                yield break;

            _log.Warning("[Turn] PassTurn still not running after execute — one deferred retry");
            try
            {
                if (Game.I != null && Game.I.passTurnController != null)
                {
                    _allowExecute = true;
                    Game.I.passTurnController.PassTurn();
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Turn] Deferred PassTurn retry: " + ex.Message);
            }
            finally
            {
                _allowExecute = false;
            }

            yield return null;
            yield return null;

            if (_executing && !IsPassTurnRunning())
            {
                _log.Error("[Turn] PassTurn failed to start — releasing false morning lock");
                ResetGate();
                _blockWorldUntilMorningReady = false;
                _passTurnPastMidpoint = false;
            }
        }

        private void OnHostPassTurnCompleted(string dayStr)
        {
            _log.Msg("[Turn] PassTurnCompleted signal (day " + dayStr + ")");
            if (_session.IsHost || !_session.Active)
                return;

            int hostDay;
            if (!int.TryParse(dayStr, out hostDay))
                return;

            int localDay = SafeCurrentDayInt();
            if (hostDay < localDay)
                return;

            if (hostDay == localDay &&
                (IsPassTurnRunning() || _executing) &&
                !_localMorningDone)
            {
                if (!_passTurnPastMidpoint)
                {
                    _log.Msg("[Turn] Host PassTurnCompleted same-day pre-midpoint — not aborting");
                    return;
                }
                RequestAbortPassTurnWaits(hostDay, "host-pass-turn-completed-same-day", sameDayOk: true);
                return;
            }

            if (hostDay <= localDay)
                return;

            if (_executing || IsPassTurnRunning())
            {
                if (!_passTurnPastMidpoint)
                {
                    _log.Msg("[Turn] Host day-ahead while pre-midpoint — not aborting");
                    return;
                }

                RequestAbortPassTurnWaits(hostDay, "host-day-ahead", sameDayOk: false);
                return;
            }

            _log.Warning("[Turn] Client behind host day " + hostDay + " (local " + localDay +
                         ") — forcing catch-up PassTurn");
            ApplyExecute("catchup_" + hostDay);
        }

        private static int SafeCurrentDayInt()
        {
            try
            {
                return Game.CurrentDay;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsPassTurnRunning()
        {
            try
            {
                return Game.I != null &&
                       Game.I.passTurnController != null &&
                       Game.I.IsAnyPassTurnTime();
            }
            catch
            {
                return false;
            }
        }

        private void TryInvokeGoToNextDay()
        {
            try
            {
                Game game = Game.I;
                if (game == null)
                {
                    MelonLogger.Warning("[DotAgeCoop] Game not ready for end turn");
                    return;
                }

                int dayBefore = SafeCurrentDayInt();

                game.GoToNextDay();

                bool playAfter = false;
                try { playAfter = game.IsPlayTime(); } catch { }
                if (playAfter && SafeCurrentDayInt() == dayBefore &&
                    game.passTurnController != null)
                {
                    MelonLogger.Warning("[DotAgeCoop] GoToNextDay no-op — forcing PassTurn");
                    game.passTurnController.PassTurn();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DotAgeCoop] GoToNextDay invoke failed: " + ex);
            }
        }

        private void ResetGate()
        {
            _localReady = false;
            _allowExecute = false;
            _executing = false;
            _readyCount = 0;
            _neededCount = Math.Max(1, _session.Active ? _session.CoopMemberCount : 1);
            _readyPeers.Clear();
            _turnId = string.Empty;
        }

        private void ResetMorningGate()
        {
            ResetMorningGateSoft();
            _morningUnlocked = false;
            _blockWorldUntilMorningReady = false;
            _passTurnPastMidpoint = false;
            _peerDoneSentAt = 0f;
            _morningAllReadyResendsLeft = 0;
            _lastMorningAllReadyPacket = null;
            _morningWorldSyncStarted = false;
            _morningPullReceived = false;
        }

        private void ResetMorningGateSoft()
        {
            _waitingForMorningPeers = false;
            _rosterSyncing = false;
            _localMorningDone = false;
            _passTurnPastMidpoint = false;
            _morningDonePeers.Clear();
            _rosterAckPeers.Clear();
            _morningReadyCount = 0;
            _morningNeededCount = Math.Max(1, _session.Active ? _session.CoopMemberCount : 1);
            _morningId = string.Empty;
        }

        private static string CurrentTurnId()
        {
            try
            {
                return "turn_" + Game.CurrentDay + "_" + Game.CurrentTurn;
            }
            catch
            {
                return "turn";
            }
        }

        private static string SafeCurrentDay()
        {
            try
            {
                return Game.CurrentDay.ToString();
            }
            catch
            {
                return "?";
            }
        }

        public void DrawWaitingOverlay()
        {
            if (!ShowWaitingPrompt && !ShowReadyStatusBanner)
                return;

            TurnPhase phase = CurrentPhase;
            bool endTurnReadyBanner = phase == TurnPhase.EndTurnGathering;
            bool softMorningCorner = phase == TurnPhase.MorningBarrier ||
                                     phase == TurnPhase.MorningLocal ||
                                     phase == TurnPhase.RosterSync;

            Color prev = GUI.color;
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.92f, 0.75f, 1f);

            if (endTurnReadyBanner || (softMorningCorner && phase != TurnPhase.RosterSync))
            {
                string text;
                if (endTurnReadyBanner)
                    text = "Готовы: " + ReadyCount + "/" + NeededCount;
                else if (phase == TurnPhase.MorningLocal)
                    text = "Утро…";
                else
                    text = "Утро: " + ReadyCount + "/" + NeededCount;
                style.alignment = TextAnchor.MiddleRight;
                style.fontSize = 16;
                float pad = 12f;
                Vector2 size = style.CalcSize(new GUIContent(text));
                float w = Mathf.Max(90f, size.x + pad * 2f);
                float h = Mathf.Max(28f, size.y + pad);
                float marginRight = 28f;
                float marginBottom = 120f;
                Rect r = new Rect(
                    Screen.width - w - marginRight,
                    Screen.height - h - marginBottom,
                    w,
                    h);

                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x, r.y, r.width - pad, r.height), text, style);
                GUI.color = prev;
                return;
            }

            float boxW = 520f;
            float boxH = 56f;
            Rect box = new Rect((Screen.width - boxW) * 0.5f, Screen.height * 0.18f, boxW, boxH);

            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;

            string blockingText = phase == TurnPhase.RosterSync
                ? "Готовы: работники… (" + ReadyCount + "/" + NeededCount + ")"
                : "Утро: готовы " + ReadyCount + "/" + NeededCount +
                  (_localMorningDone ? " — не все готовы" : " — дождитесь конца утра");

            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 18;
            GUI.Label(box, blockingText, style);
            GUI.color = prev;
        }
    }
}

