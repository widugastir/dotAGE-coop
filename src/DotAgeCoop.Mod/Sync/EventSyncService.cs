using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class EventSyncService
    {
        private static readonly FieldInfo TodayEventField =
            AccessTools.Field(typeof(EventController), "todayEvent");
        private static readonly FieldInfo HasPreparedEventField =
            AccessTools.Field(typeof(EventController), "_hasPreparedEvent");
        private static readonly FieldInfo ChoiceIsMadeField =
            AccessTools.Field(typeof(PoseChoiceEventEffect), "choiceIsMade");

        public const int RngKindInt = 1;
        public const int RngKindFloat = 2;
        public const int RngKindRoll = 3;

        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;

        private bool _applyingRemote;
        private bool _checkForNewEventsEnded;
        private bool _unstick;

        private EventCommitPayload _nightCommit;
        private bool _hasNightCommit;
        private EventCommitPayload _arrivalCommit;
        private bool _hasArrivalCommit;

        private bool _rngRecording;
        private bool _rngReplaying;
        private readonly List<int> _rngTapeFlat = new List<int>();
        private readonly Queue<int> _rngReplayFlat = new Queue<int>();
        private bool _hasStageTape;
        private EventCommitPayload _stageTapeCommit;

        private bool _eventStageSoloDialogue;

        private bool _clientWaitingStageTape;

        private EventCommitPayload _boonCommit;
        private bool _hasBoonCommit;
        private EventCommitPayload _forceCommit;
        private bool _hasForceCommit;

        private readonly Queue<int> _forcedPipUids = new Queue<int>();
        private readonly Queue<int> _forcedCreatureUids = new Queue<int>();
        private readonly Queue<int> _forcedCreatureDefIds = new Queue<int>();
        private readonly Queue<Vector2Int> _forcedTerrain = new Queue<Vector2Int>();

        private readonly HashSet<ulong> _phaseAckPeers = new HashSet<ulong>();
        private byte _hostWaitingPhase;
        private bool _clientPhaseReady;
        private byte _clientReadyPhase;
        private readonly HashSet<byte> _clientAckedPhases = new HashSet<byte>();
        private readonly Dictionary<byte, HashSet<ulong>> _earlyPhaseAcks =
            new Dictionary<byte, HashSet<ulong>>();

        private bool _pendingRollAdvance;
        private bool _pendingBoonChoice;
        private int _pendingBoonPathIndex;
        private bool _pendingBoonSkip;
        private int _pendingBoonDefId;
        private string _pendingBoonPath = string.Empty;
        private bool _pendingEventChoice;
        private int _pendingEventChoiceIndex;
        private bool _pendingEventChoiceSkip;
        private string _pendingEventChoicePath = string.Empty;

        private string _waitOverlay = string.Empty;

        private bool _allowExecutionStageSync;

        public bool ApplyingRemote { get { return _applyingRemote; } }

        public bool IsEventDialogueSolo { get { return _eventStageSoloDialogue; } }

        public bool IsClientWaitingEventStage { get { return _clientWaitingStageTape; } }

        public EventSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void OnReturnedToMain()
        {
            ClearAll();
        }

        public void ForceUnstick(string reason)
        {
            _unstick = true;
            _log.Warning("[Event] ForceUnstick: " + reason);
            ClearPhaseWaits();
        }

        public void Tick()
        {
            if (!_session.Active)
                return;
            if (!_session.IsHost && _pendingEventChoice)
                TryApplyPendingEventChoice();
            if (!_session.IsHost && _pendingBoonChoice)
                TryApplyPendingBoonChoice();
        }

        public void DrawWaitingOverlay()
        {
            if (string.IsNullOrEmpty(_waitOverlay))
                return;

            float w = Mathf.Min(760f, Screen.width * 0.88f);
            float h = 140f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.16f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.82f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUIStyle title = new GUIStyle(GUI.skin.label);
            title.alignment = TextAnchor.MiddleCenter;
            title.fontSize = 30;
            title.fontStyle = FontStyle.Bold;
            title.wordWrap = true;
            title.normal.textColor = new Color(1f, 0.94f, 0.78f, 1f);

            GUIStyle sub = new GUIStyle(GUI.skin.label);
            sub.alignment = TextAnchor.MiddleCenter;
            sub.fontSize = 20;
            sub.wordWrap = true;
            sub.normal.textColor = new Color(0.92f, 0.92f, 0.92f, 1f);

            Rect titleRect = new Rect(r.x + 16f, r.y + 12f, r.width - 32f, 72f);
            Rect subRect = new Rect(r.x + 16f, r.y + 84f, r.width - 32f, 44f);
            GUI.Label(titleRect, _waitOverlay, title);
            GUI.Label(subRect, "Игра не зависла — ждём синхронизацию с напарником.", sub);
            GUI.color = prev;
        }

        public void BroadcastHardSyncState()
        {
            if (!_session.Active || !_session.IsHost)
                return;

            if (_hasNightCommit)
            {
                _session.Broadcast(CoopMessageType.EventCommit,
                    CoopProtocol.PackEventCommit(_nightCommit));
                _log.Msg("[Event] HardSync rebroadcast NightCommit def=" + _nightCommit.EventDefId);
            }
        }

        public IEnumerator WrapPassTurnEventBarriers(IEnumerator original)
        {
            _unstick = false;
            _checkForNewEventsEnded = false;
            _hasNightCommit = false;
            _hasArrivalCommit = false;

            _allowExecutionStageSync = true;
            ClearStageTapeState();
            ClearPhaseWaits();

            while (true)
            {
                bool moved;
                try
                {
                    moved = original.MoveNext();
                }
                catch (Exception ex)
                {
                    _log.Warning("[Event] PassTurnCO error: " + ex.Message);
                    _waitOverlay = string.Empty;
                    yield break;
                }

                if (!moved)
                    break;

                yield return original.Current;

                if (_checkForNewEventsEnded)
                {
                    _checkForNewEventsEnded = false;

                    yield return NightBarrierCO(original);
                }

                if (_unstick)
                {
                    _waitOverlay = string.Empty;
                    yield break;
                }
            }

            _waitOverlay = string.Empty;
        }

        public void OnHostCheckForNewEventsEnd()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;

            EventCommitPayload commit = CaptureNightCommit();
            _nightCommit = commit;
            _hasNightCommit = true;
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(commit));
            _log.Msg("[Event] NightCommit day=" + commit.Day +
                     " has=" + commit.HasEvent + " def=" + commit.EventDefId +
                     " pips=" + (commit.PipUids != null ? commit.PipUids.Length : 0) +
                     (commit.PipUids != null && commit.PipUids.Length > 0
                         ? " [" + string.Join(",", commit.PipUids) + "]"
                         : ""));
            _checkForNewEventsEnded = true;
        }

        public void OnClientCheckForNewEventsEnd()
        {
            if (!_session.Active || _session.IsHost)
                return;
            _checkForNewEventsEnded = true;
        }

        public bool ShouldBlockClientEventRng()
        {
            if (!_session.Active || !_session.HasCoopPartner || _session.IsHost || _applyingRemote)
                return false;
            if (IsNewGameIntroSetup())
                return false;
            return true;
        }

        public static bool IsNewGameIntroSetup()
        {
            try
            {
                if (Game.I == null)
                    return true;
                if (Game.I.IsGeneratingGame || Game.I.IsCurrentlyLoading)
                    return true;
                if (!Game.I.GameIsStarted())
                    return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        public bool TryForceEventDef(out EventDefinition def)
        {
            def = null;
            if (!_hasNightCommit || !_nightCommit.HasEvent || _nightCommit.EventDefId == 0)
                return false;
            if (Game.I == null || Game.I.eventController == null)
                return false;
            def = Game.I.eventController.GetEventByID(_nightCommit.EventDefId);
            return def != null;
        }

        public bool TryDequeueForcedPipo(out Pipo pip)
        {
            pip = null;
            if (_forcedPipUids.Count == 0)
                return false;
            int uid = _forcedPipUids.Dequeue();
            pip = FindPipoByUid(uid);
            return pip != null;
        }

        public bool TryDequeueForcedCreature(out Creature creature)
        {
            creature = null;
            if (_forcedCreatureUids.Count == 0)
                return false;
            int uid = _forcedCreatureUids.Dequeue();
            creature = FindCreatureByUid(uid);
            return creature != null;
        }

        public bool TryDequeueForcedTerrain(out int terrainI, out int terrainJ)
        {
            terrainI = 0;
            terrainJ = 0;
            if (_forcedTerrain.Count == 0)
                return false;
            Vector2Int ij = _forcedTerrain.Dequeue();
            terrainI = ij.x;
            terrainJ = ij.y;
            return true;
        }

        public bool TryDequeueForcedMapTerrain(out MapTerrain terrain)
        {
            terrain = null;
            int i, j;
            if (!TryDequeueForcedTerrain(out i, out j))
                return false;
            try
            {
                if (Game.I == null || Game.I.mapController == null)
                    return false;
                terrain = Game.I.mapController.GetTerrainOrNull(i, j);
            }
            catch
            {
                terrain = null;
            }
            return terrain != null;
        }

        public bool TryDequeueForcedCreatureDef(out CreatureDefinition def)
        {
            def = null;
            if (_forcedCreatureDefIds.Count == 0)
                return false;
            int id = _forcedCreatureDefIds.Dequeue();
            def = FindCreatureDefinitionById(id);
            return def != null;
        }

        public bool TryGetArrivalResult(out bool fulfilled, out float roll)
        {
            fulfilled = false;
            roll = 0f;
            if (!_hasArrivalCommit || !_arrivalCommit.HasRoll)
                return false;
            fulfilled = _arrivalCommit.Fulfilled;
            roll = _arrivalCommit.NormalizedRoll;
            return true;
        }

        public void OnHostArrivalRoll(bool fulfilled, float normalizedRoll)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;

            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.Arrival;
            c.Day = SafeDay();
            c.Flags = EventCommitPayload.MakeFlags(true, fulfilled, true);
            c.NormalizedRoll = normalizedRoll;
            if (_hasNightCommit)
            {
                c.EventDefId = _nightCommit.EventDefId;
                c.PredUid = _nightCommit.PredUid;
                c.Nature = _nightCommit.Nature;
            }
            _arrivalCommit = c;
            _hasArrivalCommit = true;
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(c));
            _log.Msg("[Event] ArrivalCommit fulfilled=" + fulfilled + " roll=" + normalizedRoll);
        }

        public void EnsureArrivalCommitReleased(bool fulfilled, string reason)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            if (_hasArrivalCommit)
                return;

            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.Arrival;
            c.Day = SafeDay();
            c.Flags = EventCommitPayload.MakeFlags(true, fulfilled, false);
            c.NormalizedRoll = 0f;
            if (_hasNightCommit)
            {
                c.EventDefId = _nightCommit.EventDefId;
                c.PredUid = _nightCommit.PredUid;
                c.Nature = _nightCommit.Nature;
            }
            _arrivalCommit = c;
            _hasArrivalCommit = true;
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(c));
            _log.Msg("[Event] ArrivalCommit no-roll release fulfilled=" + fulfilled + " (" + reason + ")");
        }

        public void TryReleaseArrivalIfNoBattle()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            if (_hasArrivalCommit)
                return;
            try
            {
                if (Game.I == null || Game.I.eventController == null)
                    return;
                EventPrediction pred = Game.I.eventController.justPassedPrediction
                    ?? Game.I.eventController.incomingPrediction;
                if (pred == null)
                {
                    EnsureArrivalCommitReleased(true, "no-prediction");
                    return;
                }
                bool battle = true;
                try { battle = pred.ShouldBattle(); }
                catch { }
                if (!battle)
                    EnsureArrivalCommitReleased(true, "no-battle");
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] TryReleaseArrivalIfNoBattle: " + ex.Message);
            }
        }

        public void HostBroadcastRollAdvance()
        {
            if (!_session.Active || !_session.IsHost)
                return;
            _session.Broadcast(CoopMessageType.EventInput,
                CoopProtocol.PackEventInput(EventInputKind.RollAdvance, 0, 0, 0f, string.Empty));
            _pendingRollAdvance = true;
        }

        public bool ConsumeRollAdvance()
        {
            if (!_pendingRollAdvance)
                return false;
            _pendingRollAdvance = false;
            return true;
        }

        public void OnHostEventExecutionEnded()
        {
            if (!_session.Active || !_session.IsHost || !_session.HasCoopPartner)
                return;
            try
            {
                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                    ModMain.Instance.GameSync.BroadcastResourcesSnapshot(forceDuringPassTurn: true);
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] Post-exec ResourcesSnapshot: " + ex.Message);
            }
            try
            {
                if (ModMain.Instance != null && ModMain.Instance.ScalesSync != null)
                    ModMain.Instance.ScalesSync.BroadcastScalesSnapshotImmediate(forceDuringPassTurn: true);
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] Post-exec ScalesSnapshot: " + ex.Message);
            }
        }

        public static bool IsEventRngStream(RandomID id)
        {
            return id == RandomID.EventEffects ||
                   id == RandomID.EventRoll ||
                   id == RandomID.EventChoice;
        }

        public IEnumerator WrapExecutionStageCO(IEnumerator original)
        {
            if (!_session.Active || !_session.HasCoopPartner ||
                !_allowExecutionStageSync || IsNewGameIntroSetup())
            {
                while (original.MoveNext())
                    yield return original.Current;
                yield break;
            }

            if (_session.IsHost)
                _hasStageTape = false;

            yield return PhaseBarrierCO(EventPhase.BeforeStartExecution, "ExecEnter");
            if (_unstick)
                yield break;

            if (_session.IsHost)
            {
                try
                {
                    if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                        ModMain.Instance.GameSync.BroadcastResourcesSnapshot(forceDuringPassTurn: true);
                }
                catch (Exception ex)
                {
                    _log.Warning("[Event] Pre-exec ResourcesSnapshot: " + ex.Message);
                }

                _eventStageSoloDialogue = true;
                BeginEventRngRecord();
                try
                {
                    while (original.MoveNext())
                        yield return original.Current;
                }
                finally
                {
                    EndEventRngRecordAndBroadcast();
                    _eventStageSoloDialogue = false;
                }

                _waitOverlay = "Клиент смотрит событие…";
            }
            else
            {
                _clientWaitingStageTape = true;
                _waitOverlay = "Хост разыгрывает событие…";
                yield return WaitStageTapeCO(120f);
                _clientWaitingStageTape = false;
                _waitOverlay = string.Empty;
                if (_unstick)
                    yield break;

                if (!_hasStageTape)
                {
                    _log.Warning("[Event] StageTape missing — falling through to local execute");
                    while (original.MoveNext())
                        yield return original.Current;
                }
                else
                {
                    _eventStageSoloDialogue = true;
                    BeginEventRngReplay();
                    try
                    {
                        while (original.MoveNext())
                            yield return original.Current;
                    }
                    finally
                    {
                        EndEventRngReplay();
                        _eventStageSoloDialogue = false;
                    }
                }
            }

            yield return PhaseBarrierCO(EventPhase.AfterStartExecution, "ExecDone");
            _waitOverlay = string.Empty;
            if (_session.IsHost)
                OnHostEventExecutionEnded();
        }

        public void BeginEventRngRecord()
        {
            _rngRecording = true;
            _rngReplaying = false;
            _rngTapeFlat.Clear();
            _hasStageTape = false;
        }

        public void EndEventRngRecordAndBroadcast()
        {
            _rngRecording = false;
            if (!_session.Active || !_session.IsHost || !_session.HasCoopPartner)
                return;

            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.StageTape;
            c.Day = SafeDay();
            c.Flags = EventCommitPayload.MakeFlags(true, false, false);
            c.EffectValue = _rngTapeFlat.Count / 2;
            c.ResourceTypeIds = _rngTapeFlat.ToArray();
            c.PipUids = new int[0];
            c.CreatureUids = new int[0];
            c.TerrainI = new int[0];
            c.TerrainJ = new int[0];
            c.BoonOptionIds = new int[0];
            c.CreatureDefIds = new int[0];
            if (_hasNightCommit)
            {
                c.EventDefId = _nightCommit.EventDefId;
                c.PredUid = _nightCommit.PredUid;
                c.Nature = _nightCommit.Nature;
            }

            _stageTapeCommit = c;
            _hasStageTape = true;
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(c));
            _log.Msg("[Event] StageTape entries=" + c.EffectValue);
        }

        public IEnumerator WaitStageTapeCO(float timeoutSec)
        {
            if (_session.IsHost || !_session.HasCoopPartner)
                yield break;
            float deadline = Time.unscaledTime + timeoutSec;
            while (!_hasStageTape && Time.unscaledTime < deadline && !_unstick)
                yield return null;
            if (!_hasStageTape)
                _log.Warning("[Event] StageTape timeout");
        }

        public void BeginEventRngReplay()
        {
            _rngReplaying = true;
            _rngRecording = false;
            _rngReplayFlat.Clear();
            if (_stageTapeCommit.ResourceTypeIds != null)
            {
                for (int i = 0; i < _stageTapeCommit.ResourceTypeIds.Length; i++)
                    _rngReplayFlat.Enqueue(_stageTapeCommit.ResourceTypeIds[i]);
            }
        }

        public void EndEventRngReplay()
        {
            _rngReplaying = false;
            if (_rngReplayFlat.Count > 0)
                _log.Warning("[Event] StageTape leftover=" + _rngReplayFlat.Count);
            _rngReplayFlat.Clear();
        }

        public void RecordEventRngInt(int value)
        {
            if (!_rngRecording)
                return;
            _rngTapeFlat.Add(RngKindInt);
            _rngTapeFlat.Add(value);
        }

        public void RecordEventRngFloat(float value)
        {
            if (!_rngRecording)
                return;
            _rngTapeFlat.Add(RngKindFloat);
            _rngTapeFlat.Add(BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        }

        public void RecordEventRngRoll(bool value)
        {
            if (!_rngRecording)
                return;
            _rngTapeFlat.Add(RngKindRoll);
            _rngTapeFlat.Add(value ? 1 : 0);
        }

        public bool TryReplayEventRngInt(out int value)
        {
            value = 0;
            int bits;
            if (!TryDequeueReplay(RngKindInt, out bits))
                return false;
            value = bits;
            return true;
        }

        public bool TryReplayEventRngFloat(out float value)
        {
            value = 0f;
            int bits;
            if (!TryDequeueReplay(RngKindFloat, out bits))
                return false;
            value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            return true;
        }

        public bool TryReplayEventRngRoll(out bool value)
        {
            value = false;
            int bits;
            if (!TryDequeueReplay(RngKindRoll, out bits))
                return false;
            value = bits != 0;
            return true;
        }

        private bool TryDequeueReplay(int expectedKind, out int bits)
        {
            bits = 0;
            if (!_rngReplaying || _rngReplayFlat.Count < 2)
            {
                if (_rngReplaying)
                    _log.Warning("[Event] StageTape underrun kind=" + expectedKind);
                return false;
            }
            int kind = _rngReplayFlat.Dequeue();
            bits = _rngReplayFlat.Dequeue();
            if (kind != expectedKind)
            {
                _log.Warning("[Event] StageTape kind mismatch want=" + expectedKind + " got=" + kind);
                return false;
            }
            return true;
        }

        private void ClearStageTapeState()
        {
            _rngRecording = false;
            _rngReplaying = false;
            _rngTapeFlat.Clear();
            _rngReplayFlat.Clear();
            _hasStageTape = false;
            _eventStageSoloDialogue = false;
            _clientWaitingStageTape = false;
        }

        public void OnHostBoonsChosen(List<EventDefinition> offers)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            int[] ids = new int[offers != null ? offers.Count : 0];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = offers[i] != null ? offers[i].ID : 0;

            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.Boon;
            c.Day = SafeDay();
            c.Flags = EventCommitPayload.MakeFlags(true, false, false);
            c.BoonOptionIds = ids;
            _boonCommit = c;
            _hasBoonCommit = true;
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(c));
            _log.Msg("[Event] BoonCommit n=" + ids.Length);
        }

        public bool TryGetBoonOffers(out List<EventDefinition> offers)
        {
            offers = null;
            if (!_hasBoonCommit || _boonCommit.BoonOptionIds == null)
                return false;
            offers = ResolveOfferDefs(_boonCommit.BoonOptionIds);
            return offers != null && offers.Count > 0;
        }

        public IEnumerator WaitForBoonOffersCO(float timeoutSec)
        {
            if (_session.IsHost || !_session.HasCoopPartner)
                yield break;
            _waitOverlay = string.Empty;
            float deadline = Time.unscaledTime + timeoutSec;
            while (!_hasBoonCommit && Time.unscaledTime < deadline && !_unstick)
                yield return null;
            _waitOverlay = string.Empty;
        }

        public void OnHostChoiceMade(PoseChoiceEventEffect pose, int pathIndex, bool skip)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || pose == null)
                return;

            string path = string.Empty;
            int defId = 0;
            try
            {
                if (!skip && pose.chosenChoice != null)
                    path = pose.chosenChoice.ChoicePath ?? string.Empty;
            }
            catch { }

            try
            {
                if (SteerChoiceEventEffect.ChosenEvent != null)
                    defId = SteerChoiceEventEffect.ChosenEvent.ID;
            }
            catch { }

            _session.Broadcast(CoopMessageType.EventInput,
                CoopProtocol.PackEventInput(EventInputKind.BoonChoice, pathIndex, defId, skip ? 1f : 0f, path));
            _log.Msg("[Event] BoonChoice pathIndex=" + pathIndex + " def=" + defId + " skip=" + skip);
        }

        public bool ShouldBlockClientChoice()
        {
            return ShouldBlockClientEventRng();
        }

        public bool ShouldBlockClientBoonReroll()
        {
            return ShouldBlockClientEventRng();
        }

        public IEnumerator WaitBeforePerformEventCO()
        {
            if (!_session.Active || !_session.HasCoopPartner)
                yield break;
            yield return PhaseBarrierCO(EventPhase.BeforePerformEvent, "PerformEvent");
        }

        private IEnumerator NightBarrierCO(IEnumerator passTurnState)
        {
            if (!_session.HasCoopPartner)
                yield break;

            if (_session.IsHost)
            {
                yield return PhaseBarrierCO(EventPhase.AfterCheckForNewEvents, "NightCommit");
                yield break;
            }

            _waitOverlay = string.Empty;
            float deadline = Time.unscaledTime + 60f;
            while (!_hasNightCommit && Time.unscaledTime < deadline && !_unstick)
                yield return null;

            if (_hasNightCommit)
            {
                ApplyNightCommitToLocal(_nightCommit);
                ForcePassTurnEventLocals(passTurnState, _nightCommit.HasEvent);
            }

            _waitOverlay = string.Empty;

            yield return PhaseBarrierCO(EventPhase.AfterCheckForNewEvents, "NightCommit");
        }

        public IEnumerator WaitArrivalCommitCO(float timeoutSec)
        {
            if (_session.IsHost || !_session.HasCoopPartner)
                yield break;

            _waitOverlay = "Хост разыгрывает событие…";
            float deadline = Time.unscaledTime + timeoutSec;
            while (!_hasArrivalCommit && Time.unscaledTime < deadline && !_unstick)
                yield return null;
            _waitOverlay = string.Empty;
        }

        public void OnHostForceEvent(EventDefinition def)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || def == null)
                return;
            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.Force;
            c.Day = SafeDay();
            c.Flags = EventCommitPayload.MakeFlags(true, false, false);
            c.EventDefId = def.ID;
            try { c.Nature = (int)def.Nature; }
            catch { }
            CaptureTargetsInto(ref c);
            _forceCommit = c;
            _hasForceCommit = true;
            EnqueueForcedTargets(c);
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(c));
            _log.Msg("[Event] ForceCommit def=" + def.ID);
        }

        public IEnumerator WaitForceCommitCO(int eventDefId, float timeoutSec)
        {
            if (_session.IsHost || !_session.HasCoopPartner)
                yield break;
            _waitOverlay = string.Empty;
            float deadline = Time.unscaledTime + timeoutSec;
            while (!_hasForceCommit && Time.unscaledTime < deadline && !_unstick)
                yield return null;
            if (_hasForceCommit)
                EnqueueForcedTargets(_forceCommit);
            _waitOverlay = string.Empty;
        }

        public void OnHostEventReplaced(EventDefinition ev)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || ev == null)
                return;
            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.Replace;
            c.Day = SafeDay();
            c.Flags = EventCommitPayload.MakeFlags(true, false, false);
            c.EventDefId = ev.ID;
            try { c.Nature = (int)ev.Nature; }
            catch { }
            CaptureTargetsInto(ref c);
            _session.Broadcast(CoopMessageType.EventCommit, CoopProtocol.PackEventCommit(c));
            _log.Msg("[Event] ReplaceCommit def=" + ev.ID);
        }

        private IEnumerator PhaseBarrierCO(byte phase, string label)
        {
            if (_session.IsHost)
            {
                _phaseAckPeers.Clear();
                _hostWaitingPhase = phase;
                _session.Broadcast(CoopMessageType.EventPhaseReady, CoopProtocol.PackEventPhase(phase));

                _phaseAckPeers.Add(_session.SelfId);
                ConsumeEarlyPhaseAcks(phase);

                float deadline = Time.unscaledTime + 60f;
                int needed = Math.Max(1, _session.CoopMemberCount);
                while (_phaseAckPeers.Count < needed && Time.unscaledTime < deadline && !_unstick)
                    yield return null;
                _hostWaitingPhase = 0;
                _log.Msg("[Event] Phase " + label + " acks " + _phaseAckPeers.Count + "/" + needed);
            }
            else
            {
                float deadline = Time.unscaledTime + 60f;
                while ((!_clientPhaseReady || _clientReadyPhase != phase) &&
                       Time.unscaledTime < deadline && !_unstick)
                    yield return null;
                SendPhaseAck(phase);
                _clientPhaseReady = false;
            }
        }

        private void ConsumeEarlyPhaseAcks(byte phase)
        {
            HashSet<ulong> early;
            if (!_earlyPhaseAcks.TryGetValue(phase, out early) || early == null || early.Count == 0)
                return;
            foreach (ulong id in early)
                _phaseAckPeers.Add(id);
            _log.Msg("[Event] Phase early-acks absorbed count=" + early.Count + " phase=" + phase);
            early.Clear();
        }

        private void SendPhaseAck(byte phase)
        {
            if (_clientAckedPhases.Contains(phase))
                return;
            _clientAckedPhases.Add(phase);
            _session.SendToHost(CoopMessageType.EventPhaseAck, CoopProtocol.PackEventPhase(phase));
        }

        private EventCommitPayload CaptureNightCommit()
        {
            EventCommitPayload c = default(EventCommitPayload);
            c.Phase = EventCommitPhase.Night;
            c.Day = SafeDay();
            c.PipUids = new int[0];
            c.CreatureUids = new int[0];
            c.TerrainI = new int[0];
            c.TerrainJ = new int[0];
            c.BoonOptionIds = new int[0];
            c.ResourceTypeIds = new int[0];
            c.CreatureDefIds = new int[0];
            c.EffectValue = 0;

            if (Game.I == null || Game.I.eventController == null)
            {
                c.Flags = EventCommitPayload.MakeFlags(false, false, false);
                return c;
            }

            EventController ec = Game.I.eventController;
            bool has = false;
            try { has = ec.HasPreparedEvent(); }
            catch { }

            EventDefinition today = null;
            try { today = ec.GetTodayEvent(); }
            catch { }

            if (!has || today == null)
            {
                c.Flags = EventCommitPayload.MakeFlags(false, false, false);
                return c;
            }

            c.Flags = EventCommitPayload.MakeFlags(true, false, false);
            c.EventDefId = today.ID;
            try { c.Nature = (int)today.Nature; }
            catch { }

            EventPrediction pred = ec.justPassedPrediction ?? ec.incomingPrediction;
            if (pred != null && pred.saveData != null)
            {
                try { c.PredUid = pred.saveData.UID; }
                catch { }
            }

            CaptureTargetsInto(ref c, pred);
            CaptureTargetsFromLogic(ref c, ec.incomingEventLogic);
            CaptureCreatureDefIdsInto(ref c, ec.incomingEventLogic);
            return c;
        }

        private void CaptureTargetsInto(ref EventCommitPayload c)
        {
            EventPrediction pred = null;
            try
            {
                if (Game.I != null && Game.I.eventController != null)
                    pred = Game.I.eventController.justPassedPrediction
                        ?? Game.I.eventController.incomingPrediction;
            }
            catch { }
            CaptureTargetsInto(ref c, pred);
        }

        private void CaptureTargetsInto(ref EventCommitPayload c, EventPrediction pred)
        {
            List<int> pips = new List<int>();
            List<int> creatures = new List<int>();
            List<int> ti = new List<int>();
            List<int> tj = new List<int>();

            if (pred != null && pred.saveData != null && pred.saveData.targetUIDs != null)
            {
                for (int i = 0; i < pred.saveData.targetUIDs.Length; i++)
                {
                    int uid = pred.saveData.targetUIDs[i];
                    if (uid == 0)
                        continue;
                    if (FindPipoByUid(uid) != null)
                        pips.Add(uid);
                    else if (FindCreatureByUid(uid) != null)
                        creatures.Add(uid);
                    else
                        pips.Add(uid);
                }
            }

            EventLogic logic = null;
            try
            {
                if (Game.I != null && Game.I.eventController != null)
                    logic = Game.I.eventController.incomingEventLogic;
            }
            catch { }

            _ = logic;

            c.PipUids = pips.ToArray();
            c.CreatureUids = creatures.ToArray();
            c.TerrainI = ti.ToArray();
            c.TerrainJ = tj.ToArray();
        }

        private void CaptureTargetsFromLogic(ref EventCommitPayload c, EventLogic logic)
        {
            if (logic == null)
                return;

            List<int> pips = new List<int>();
            List<int> creatures = new List<int>();
            List<int> ti = new List<int>();
            List<int> tj = new List<int>();
            if (c.PipUids != null)
                pips.AddRange(c.PipUids);
            if (c.CreatureUids != null)
                creatures.AddRange(c.CreatureUids);
            if (c.TerrainI != null)
                ti.AddRange(c.TerrainI);
            if (c.TerrainJ != null)
                tj.AddRange(c.TerrainJ);

            try
            {

                PipoEventLogic pipLogic = logic as PipoEventLogic;
                if (pipLogic != null && pipLogic.Targets != null)
                {
                    for (int i = 0; i < pipLogic.Targets.Count; i++)
                    {
                        Pipo p = pipLogic.Targets[i];
                        if (p != null && !pips.Contains(p.UID))
                            pips.Add(p.UID);
                    }
                }
                else
                {
                    CreatureEventLogic creatureLogic = logic as CreatureEventLogic;
                    if (creatureLogic != null && creatureLogic.Targets != null)
                    {
                        for (int i = 0; i < creatureLogic.Targets.Count; i++)
                        {
                            Creature cr = creatureLogic.Targets[i];
                            if (cr != null && !(cr is Pipo) && !creatures.Contains(cr.UID))
                                creatures.Add(cr.UID);
                        }
                    }
                    else
                    {
                        TerrainEventLogic terrainLogic = logic as TerrainEventLogic;
                        if (terrainLogic != null && terrainLogic.Targets != null)
                        {
                            for (int i = 0; i < terrainLogic.Targets.Count; i++)
                                AppendOneTarget(terrainLogic.Targets[i], pips, creatures, ti, tj);
                        }
                        else
                        {
                            AppendTargetsViaReflection(logic, pips, creatures, ti, tj);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] CaptureTargetsFromLogic: " + ex.Message);
            }

            c.PipUids = pips.ToArray();
            c.CreatureUids = creatures.ToArray();
            c.TerrainI = ti.ToArray();
            c.TerrainJ = tj.ToArray();
        }

        private void CaptureCreatureDefIdsInto(ref EventCommitPayload c, EventLogic logic)
        {
            c.CreatureDefIds = CaptureCreatureDefIdsFromLogic(logic);
        }

        private static int[] CaptureCreatureDefIdsFromLogic(EventLogic logic)
        {
            if (logic == null)
                return new int[0];

            List<int> defs = new List<int>();
            try
            {
                NewCreatureEventSelector[] selectors =
                    logic.GetComponentsInChildren<NewCreatureEventSelector>(true);
                if (selectors != null)
                {
                    for (int i = 0; i < selectors.Length; i++)
                    {
                        NewCreatureEventSelector sel = selectors[i];
                        if (sel == null || sel.creatureDefinition == null)
                            continue;
                        int id = sel.creatureDefinition.ID;
                        if (id != 0)
                            defs.Add(id);
                    }
                }

                if (defs.Count == 0)
                {
                    CreatureEventLogic creatureLogic = logic as CreatureEventLogic;
                    if (creatureLogic != null && creatureLogic.Targets != null)
                    {
                        for (int i = 0; i < creatureLogic.Targets.Count; i++)
                        {
                            Creature cr = creatureLogic.Targets[i];
                            if (cr == null || cr is Pipo || cr.definition == null)
                                continue;
                            int id = cr.definition.ID;
                            if (id == 0 || defs.Contains(id))
                                continue;
                            defs.Add(id);
                        }
                    }
                }
            }
            catch { }

            return defs.ToArray();
        }

        private static void AppendTargetsViaReflection(
            EventLogic logic, List<int> pips, List<int> creatures, List<int> ti, List<int> tj)
        {
            PropertyInfo targetsProp = AccessTools.Property(logic.GetType(), "Targets");
            object listObj = targetsProp != null ? targetsProp.GetValue(logic, null) : null;
            if (listObj == null)
                return;

            PropertyInfo countProp = AccessTools.Property(listObj.GetType(), "Count");
            MethodInfo getItem = AccessTools.Method(listObj.GetType(), "get_Item", new[] { typeof(int) });
            if (countProp != null && getItem != null)
            {
                int count = (int)countProp.GetValue(listObj, null);
                for (int i = 0; i < count; i++)
                {
                    object item = getItem.Invoke(listObj, new object[] { i });
                    AppendOneTarget(item, pips, creatures, ti, tj);
                }
                return;
            }

            IEnumerable enumerable = listObj as IEnumerable;
            if (enumerable == null)
                return;
            foreach (object item in enumerable)
                AppendOneTarget(item, pips, creatures, ti, tj);
        }

        private static void AppendOneTarget(
            object item, List<int> pips, List<int> creatures, List<int> ti, List<int> tj)
        {
            if (item == null)
                return;
            Pipo pip = item as Pipo;
            if (pip != null)
            {
                if (!pips.Contains(pip.UID))
                    pips.Add(pip.UID);
                return;
            }
            Creature creature = item as Creature;
            if (creature != null && !(creature is Pipo))
            {
                if (!creatures.Contains(creature.UID))
                    creatures.Add(creature.UID);
                return;
            }
            MapTerrain terrain = item as MapTerrain;
            if (terrain == null || terrain.cell == null)
                return;
            try
            {
                int i = terrain.cell.i;
                int j = terrain.cell.j;
                for (int k = 0; k < ti.Count; k++)
                {
                    if (ti[k] == i && tj[k] == j)
                        return;
                }
                ti.Add(i);
                tj.Add(j);
            }
            catch { }
        }

        private void ApplyNightCommitToLocal(EventCommitPayload commit)
        {
            _applyingRemote = true;
            try
            {
                EnqueueForcedTargets(commit);
                if (Game.I == null || Game.I.eventController == null)
                    return;

                EventController ec = Game.I.eventController;
                if (!commit.HasEvent || commit.EventDefId == 0)
                {
                    if (HasPreparedEventField != null)
                        HasPreparedEventField.SetValue(ec, false);
                    if (TodayEventField != null)
                        TodayEventField.SetValue(ec, null);
                    ec.incomingEventLogic = null;
                    _log.Msg("[Event] Applied NightCommit NO-event day=" + commit.Day);
                    return;
                }

                EventDefinition def = ec.GetEventByID(commit.EventDefId);
                if (def == null)
                {
                    _log.Warning("[Event] NightCommit unknown def=" + commit.EventDefId);
                    return;
                }

                EventLogic logic = ec.PrepareEvent(def);
                ec.incomingEventLogic = logic;
                if (TodayEventField != null)
                    TodayEventField.SetValue(ec, def);

                EventPrediction pred = ResolveTonightPrediction(ec, commit.PredUid);
                if (pred == null)
                {

                    try
                    {
                        pred = new EventPrediction(0, def, PredictionType.Risk);
                        try { pred.RegisterArrivalDay(); }
                        catch { }
                        _log.Msg("[Event] NightCommit fabricated prediction def=" + commit.EventDefId);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[Event] NightCommit fabricate prediction: " + ex.Message);
                    }
                }

                if (pred != null)
                {
                    try { pred.Event = def; }
                    catch { }
                    try { pred.AssignLogic(logic); }
                    catch { }
                    ec.incomingPrediction = pred;
                    ec.justPassedPrediction = pred;
                    try
                    {
                        ec.justPassedPredictions.Clear();
                        ec.justPassedPredictions.Add(pred);
                    }
                    catch { }
                    try { ec.nextPredictionsList.Remove(pred); }
                    catch { }
                }
                else
                {
                    _log.Warning("[Event] NightCommit def=" + commit.EventDefId +
                                 " predUid=" + commit.PredUid + " — no prediction to bind");
                }

                bool prepared = logic != null && ec.GetTodayEvent() != null;
                if (HasPreparedEventField != null)
                    HasPreparedEventField.SetValue(ec, prepared);

                if (!prepared)
                    _log.Warning("[Event] NightCommit def=" + commit.EventDefId +
                                 " PrepareEvent failed (incomingLogic null)");

                _log.Msg("[Event] Applied NightCommit def=" + commit.EventDefId +
                         " prepared=" + ec.HasPreparedEvent() +
                         " logic=" + (logic != null) +
                         " today=" + (ec.GetTodayEvent() != null ? ec.GetTodayEvent().ID : 0) +
                         " predUid=" + commit.PredUid +
                         " justPassed=" + (ec.justPassedPrediction != null));
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] ApplyNightCommit: " + ex.Message);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static EventPrediction ResolveTonightPrediction(EventController ec, int predUid)
        {
            if (ec == null)
                return null;

            EventPrediction pred = null;
            if (predUid != 0)
            {
                try { pred = ec.GetPredictionByUID(predUid); }
                catch { }
            }

            if (pred == null && ec.nextPredictionsList != null)
            {
                for (int i = 0; i < ec.nextPredictionsList.Count; i++)
                {
                    EventPrediction p = ec.nextPredictionsList[i];
                    if (p == null)
                        continue;
                    bool due = false;
                    try { due = p.ShouldTrigger() || p.turnsBeforeArrival <= 0; }
                    catch
                    {
                        try { due = p.turnsBeforeArrival <= 0; }
                        catch { }
                    }
                    if (due)
                    {
                        pred = p;
                        break;
                    }
                }
            }

            if (pred == null)
                pred = ec.justPassedPrediction ?? ec.incomingPrediction;
            return pred;
        }

        private void EnqueueForcedTargets(EventCommitPayload commit)
        {
            _forcedPipUids.Clear();
            _forcedCreatureUids.Clear();
            _forcedCreatureDefIds.Clear();
            _forcedTerrain.Clear();
            if (commit.PipUids != null)
            {
                for (int i = 0; i < commit.PipUids.Length; i++)
                    _forcedPipUids.Enqueue(commit.PipUids[i]);
            }
            if (commit.CreatureUids != null)
            {
                for (int i = 0; i < commit.CreatureUids.Length; i++)
                    _forcedCreatureUids.Enqueue(commit.CreatureUids[i]);
            }
            if (commit.CreatureDefIds != null)
            {
                for (int i = 0; i < commit.CreatureDefIds.Length; i++)
                    _forcedCreatureDefIds.Enqueue(commit.CreatureDefIds[i]);
            }
            if (commit.TerrainI != null && commit.TerrainJ != null)
            {
                int n = Math.Min(commit.TerrainI.Length, commit.TerrainJ.Length);
                for (int i = 0; i < n; i++)
                    _forcedTerrain.Enqueue(new Vector2Int(commit.TerrainI[i], commit.TerrainJ[i]));
            }
        }

        private void ForcePassTurnEventLocals(IEnumerator passTurnState, bool hasPreparedEvent)
        {
            if (passTurnState == null)
                return;

            bool continuous = false;
            try
            {
                if (Game.I != null && Game.I.eventController != null)
                    continuous = Game.I.eventController.HasContinuousExecutionEvents();
            }
            catch { }

            bool eventsAreIncoming = hasPreparedEvent || continuous;
            bool newEventIsIncoming = hasPreparedEvent;

            try
            {
                Type sm = passTurnState.GetType();
                FieldInfo[] fields = sm.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                int patched = 0;
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f.FieldType != typeof(bool))
                        continue;
                    string n = f.Name ?? string.Empty;
                    if (n.IndexOf("eventsAreIncoming", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        f.SetValue(passTurnState, eventsAreIncoming);
                        patched++;
                    }
                    else if (n.IndexOf("newEventIsIncoming", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        f.SetValue(passTurnState, newEventIsIncoming);
                        patched++;
                    }
                }

                _log.Msg("[Event] ForcePassTurnEventLocals prepared=" + hasPreparedEvent +
                         " continuous=" + continuous + " patched=" + patched +
                         " type=" + sm.Name);
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] ForcePassTurnEventLocals: " + ex.Message);
            }
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.EventCommit:
                    if (!_session.IsHost)
                        HandleEventCommit(payload);
                    break;
                case CoopMessageType.EventPhaseReady:
                    if (!_session.IsHost)
                        HandlePhaseReady(payload);
                    break;
                case CoopMessageType.EventPhaseAck:
                    if (_session.IsHost)
                        HandlePhaseAck(remote, payload);
                    break;
                case CoopMessageType.EventInput:
                    if (!_session.IsHost)
                        HandleEventInput(payload);
                    break;
            }
        }

        private void HandleEventCommit(byte[] payload)
        {
            EventCommitPayload data;
            if (!CoopProtocol.TryReadEventCommit(payload, out data))
            {
                _log.Warning("[Event] Bad EventCommit");
                return;
            }

            switch (data.Phase)
            {
                case EventCommitPhase.Night:
                    _nightCommit = data;
                    _hasNightCommit = true;
                    _log.Msg("[Event] Recv NightCommit day=" + data.Day +
                             " has=" + data.HasEvent + " def=" + data.EventDefId);
                    break;
                case EventCommitPhase.Targets:
                    EnqueueForcedTargets(data);
                    _log.Msg("[Event] Recv TargetCommit pips=" +
                             (data.PipUids != null ? data.PipUids.Length : 0) +
                             " creatures=" +
                             (data.CreatureUids != null ? data.CreatureUids.Length : 0));
                    break;
                case EventCommitPhase.Effect:
                    _log.Msg("[Event] Recv legacy EffectCommit (ignored — StageTape)");
                    break;
                case EventCommitPhase.StageTape:
                    _stageTapeCommit = data;
                    _hasStageTape = true;
                    _log.Msg("[Event] Recv StageTape entries=" + data.EffectValue);
                    break;
                case EventCommitPhase.Arrival:
                    _arrivalCommit = data;
                    _hasArrivalCommit = true;
                    break;
                case EventCommitPhase.Boon:
                    _boonCommit = data;
                    _hasBoonCommit = true;
                    break;
                case EventCommitPhase.Force:
                    _forceCommit = data;
                    _hasForceCommit = true;
                    EnqueueForcedTargets(data);
                    break;
                case EventCommitPhase.Replace:
                    ApplyReplaceCommit(data);
                    _log.Msg("[Event] Recv ReplaceCommit def=" + data.EventDefId);
                    break;
                case EventCommitPhase.PreTurn:
                    _log.Msg("[Event] Recv PreTurnCommit");
                    break;
            }
        }

        private void ApplyReplaceCommit(EventCommitPayload data)
        {
            if (!data.HasEvent || data.EventDefId == 0)
                return;
            _applyingRemote = true;
            try
            {
                EnqueueForcedTargets(data);
                if (Game.I == null || Game.I.eventController == null)
                    return;
                EventDefinition def = Game.I.eventController.GetEventByID(data.EventDefId);
                if (def != null)
                    Game.I.eventController.ReplaceCurrentEventWith(def);
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] ApplyReplace: " + ex.Message);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private void HandlePhaseReady(byte[] payload)
        {
            byte phase;
            if (!CoopProtocol.TryReadEventPhase(payload, out phase))
                return;
            _clientPhaseReady = true;
            _clientReadyPhase = phase;

            _clientAckedPhases.Remove(phase);
        }

        private void HandlePhaseAck(CSteamID remote, byte[] payload)
        {
            byte phase;
            if (!CoopProtocol.TryReadEventPhase(payload, out phase))
                return;
            if (_hostWaitingPhase == 0 || phase != _hostWaitingPhase)
            {
                HashSet<ulong> early;
                if (!_earlyPhaseAcks.TryGetValue(phase, out early))
                {
                    early = new HashSet<ulong>();
                    _earlyPhaseAcks[phase] = early;
                }
                early.Add(remote.m_SteamID);
                _log.Msg("[Event] Phase early-ack buffered peer phase=" + phase);
                return;
            }
            _phaseAckPeers.Add(remote.m_SteamID);
        }

        private void HandleEventInput(byte[] payload)
        {
            byte kind;
            int intA, intB;
            float floatA;
            string path;
            if (!CoopProtocol.TryReadEventInput(payload, out kind, out intA, out intB, out floatA, out path))
                return;

            switch (kind)
            {
                case EventInputKind.RollAdvance:
                    _pendingRollAdvance = true;
                    break;
                case EventInputKind.BoonChoice:
                    _pendingBoonPathIndex = intA;
                    _pendingBoonDefId = intB;
                    _pendingBoonSkip = floatA > 0.5f;
                    _pendingBoonPath = path ?? string.Empty;
                    _pendingBoonChoice = true;
                    TryApplyPendingBoonChoice();
                    break;
                case EventInputKind.EventChoicePath:
                    _pendingEventChoiceIndex = intA;
                    _pendingEventChoiceSkip = floatA > 0.5f;
                    _pendingEventChoicePath = path ?? string.Empty;
                    _pendingEventChoice = true;
                    TryApplyPendingEventChoice();
                    break;
            }
        }

        private void TryApplyPendingBoonChoice()
        {
            if (!_pendingBoonChoice)
                return;
            PoseChoiceEventEffect pose = FindActivePoseChoice();
            if (pose == null)
                return;

            bool already = false;
            try
            {
                if (ChoiceIsMadeField != null)
                    already = (bool)ChoiceIsMadeField.GetValue(pose);
            }
            catch { }
            if (already)
            {
                _pendingBoonChoice = false;
                return;
            }

            _applyingRemote = true;
            try
            {
                if (_pendingBoonDefId != 0 && Game.I != null && Game.I.eventController != null)
                {
                    EventDefinition def = Game.I.eventController.GetEventByID(_pendingBoonDefId);
                    if (def != null)
                        SteerChoiceEventEffect.ChosenEvent = def;
                }
                pose.PerformForChoice(_pendingBoonPathIndex, _pendingBoonSkip);
                _pendingBoonChoice = false;
                _log.Msg("[Event] Applied BoonChoice index=" + _pendingBoonPathIndex);
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] ApplyBoonChoice: " + ex.Message);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private void TryApplyPendingEventChoice()
        {
            if (!_pendingEventChoice)
                return;
            PoseChoiceEventEffect pose = FindActivePoseChoice();
            if (pose == null)
                return;
            _applyingRemote = true;
            try
            {
                pose.PerformForChoice(_pendingEventChoiceIndex, _pendingEventChoiceSkip);
                _pendingEventChoice = false;
            }
            catch (Exception ex)
            {
                _log.Warning("[Event] ApplyEventChoice: " + ex.Message);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static PoseChoiceEventEffect FindActivePoseChoice()
        {
            if (Game.I == null || Game.I.eventController == null)
                return null;
            EventController ec = Game.I.eventController;
            PoseChoiceEventEffect pose = null;
            if (ec.latestArrivedEventLogic != null)
                pose = ec.latestArrivedEventLogic.GetComponentInChildren<PoseChoiceEventEffect>(true);
            if (pose == null && ec.incomingEventLogic != null)
                pose = ec.incomingEventLogic.GetComponentInChildren<PoseChoiceEventEffect>(true);
            if (pose == null)
                pose = UnityEngine.Object.FindObjectOfType<PoseChoiceEventEffect>();
            return pose;
        }

        private static List<EventDefinition> ResolveOfferDefs(int[] ids)
        {
            List<EventDefinition> list = new List<EventDefinition>();
            if (ids == null || Game.I == null || Game.I.eventController == null)
                return list;
            for (int i = 0; i < ids.Length; i++)
            {
                EventDefinition def = Game.I.eventController.GetEventByID(ids[i]);
                if (def != null)
                    list.Add(def);
            }
            return list;
        }

        private static Pipo FindPipoByUid(int uid)
        {
            if (Game.I == null || Game.I.pipsHandler == null)
                return null;
            try
            {
                Worker w = Game.I.pipsHandler.GetPipByUID(uid);
                return w as Pipo;
            }
            catch { }
            return null;
        }

        private static Creature FindCreatureByUid(int uid)
        {
            try
            {
                Creature[] all = UnityEngine.Object.FindObjectsOfType<Creature>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].UID == uid)
                        return all[i];
                }
            }
            catch { }
            return null;
        }

        private static CreatureDefinition FindCreatureDefinitionById(int id)
        {
            if (id == 0 || Game.I == null || Game.I.creaturesHandler == null)
                return null;
            try
            {
                return Game.I.creaturesHandler.GetDefinitionByID(id);
            }
            catch { }
            return null;
        }

        private static int SafeDay()
        {
            try { return Game.CurrentDay; }
            catch { return 0; }
        }

        private void ClearPhaseWaits()
        {
            _phaseAckPeers.Clear();
            _hostWaitingPhase = 0;
            _clientPhaseReady = false;
            _clientReadyPhase = 0;
            _clientAckedPhases.Clear();
            _earlyPhaseAcks.Clear();
        }

        private void ClearAll()
        {
            _applyingRemote = false;
            _checkForNewEventsEnded = false;
            _unstick = false;
            _hasNightCommit = false;
            _hasArrivalCommit = false;
            _hasBoonCommit = false;
            _hasForceCommit = false;
            _forcedPipUids.Clear();
            _forcedCreatureUids.Clear();
            _forcedCreatureDefIds.Clear();
            _forcedTerrain.Clear();
            ClearStageTapeState();
            _pendingRollAdvance = false;
            _pendingBoonChoice = false;
            _pendingEventChoice = false;
            _waitOverlay = string.Empty;
            _allowExecutionStageSync = false;
            ClearStageTapeState();
            ClearPhaseWaits();
        }
    }
}
