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

    public sealed class PipOrderSyncService
    {
        private static readonly MethodInfo ActOnWhileWipMethod =
            AccessTools.Method(typeof(Building), "ActOnWhileWIP");
        private static readonly MethodInfo WorkerAppendWorkMoveMethod =
            AccessTools.Method(typeof(Worker), "AppendWorkMove");

        private static readonly FieldInfo AssignedPipoCurrentField =
            AccessTools.Field(typeof(AssignedPipoPopUp), "current");
        private static readonly MethodInfo AssignedPipoUpdateWorkersMethod =
            AccessTools.Method(typeof(AssignedPipoPopUp), "UpdateWorkers");

        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private bool _applyingRemote;

        private bool _bypassWorldGate;
        private float _lastBroadcastStamp;
        private int _lastBroadcastHash;
        private object _retetherCoroutine;

        private Building _pendingWorkBuilding;
        private float _pendingWorkBroadcastAt;
        private readonly HashSet<Building> _pendingWorkBuildings = new HashSet<Building>();
        private PipOrderPayload _pendingRetry;
        private bool _hasPendingRetry;
        private float _pendingRetryUntil;

        private readonly Dictionary<long, int> _clientPendingContextActions = new Dictionary<long, int>();

        public bool ApplyingRemote { get { return _applyingRemote; } }

        private bool AllowsHostLocalMutate()
        {
            return _applyingRemote || _bypassWorldGate;
        }

        public bool ShouldSuppressPipWorkMotion()
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
            if (ModMain.Instance != null && ModMain.Instance.TurnSync != null)
                return ModMain.Instance.TurnSync.SuppressesPipWorkMotion;
            return false;
        }

        public PipOrderSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {

            if (_hasPendingRetry && Time.unscaledTime <= _pendingRetryUntil)
            {
                Building building;
                if (TryResolveBuilding(_pendingRetry, out building) && building != null)
                {
                    _hasPendingRetry = false;
                    _applyingRemote = true;
                    try
                    {
                        bool wantActivated = (_pendingRetry.Flags & PipOrderFlags.BuildingActivated) != 0;
                        WorkAction action = (WorkAction)_pendingRetry.WorkAction;
                        if (action == WorkAction.NONE)
                            action = WorkAction.Use;
                        SyncWorkerRoster(building, action, _pendingRetry.Param, _pendingRetry.WorkerUids, wantActivated, _pendingRetry.ExchangeId);
                        _log.Msg("[PipOrder] Client retry roster OK @ " + _pendingRetry.TerrainI + "," + _pendingRetry.TerrainJ);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[PipOrder] Retry failed: " + ex.Message);
                    }
                    finally
                    {
                        _applyingRemote = false;
                    }
                }
            }
            else if (_hasPendingRetry && Time.unscaledTime > _pendingRetryUntil)
            {
                _hasPendingRetry = false;
            }

            if (_pendingWorkBuildings.Count > 0 && Time.unscaledTime >= _pendingWorkBroadcastAt)
            {
                if (!_session.Active || !_session.IsHost)
                {
                    _pendingWorkBuilding = null;
                    _pendingWorkBuildings.Clear();
                }
                else
                {

                    List<Building> toFlush = new List<Building>(_pendingWorkBuildings);
                    _pendingWorkBuildings.Clear();
                    _pendingWorkBuilding = null;
                    _log.Msg("[PipOrder] Flushing pending buildings=" + toFlush.Count);
                    for (int i = 0; i < toFlush.Count; i++)
                    {
                        if (toFlush[i] != null)
                            FlushBuildingWorkState(toFlush[i], "delayed");
                    }
                }
            }
            else if (_pendingWorkBuilding != null)
            {

                if (!_session.Active || !_session.IsHost)
                {
                    _pendingWorkBuilding = null;
                    return;
                }
                if (Time.unscaledTime < _pendingWorkBroadcastAt)
                    return;

                Building flushBuilding = _pendingWorkBuilding;
                _pendingWorkBuilding = null;
                FlushBuildingWorkState(flushBuilding, "delayed");
            }
        }

        public void CancelRetetherCoroutine()
        {
            try
            {
                if (_retetherCoroutine != null)
                {
                    MelonCoroutines.Stop(_retetherCoroutine);
                    _retetherCoroutine = null;
                }
            }
            catch
            {
            }
        }

        public void FlushFullRosterForJoin(string reason)
        {
            if (!_session.Active || !_session.IsHost)
                return;

            try
            {
                FlushAllBuildingWorkStates(reason);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] FlushFullRosterForJoin: " + ex.Message);
            }
        }

        public void SendFullRosterTo(CSteamID remote, string reason)
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (Game.I == null || Game.I.buildingsHandler == null)
                return;

            try
            {
                _lastBroadcastStamp = -1f;
                _lastBroadcastHash = 0;

                List<Building> buildings = Game.I.buildingsHandler.AllBuildings();
                int flushed = 0;
                for (int i = 0; i < buildings.Count; i++)
                {
                    Building b = buildings[i];
                    if (b == null)
                        continue;

                    bool interesting = b.Activated || b.HasWorkers() ||
                                       (b.ActionChoice != null && b.ActionChoice.HasParametricAction()) ||
                                       b._weWantThisActivated ||
                                       (b.IsBuilt && b.IsWip);
                    if (!interesting)
                        continue;

                    PipOrderPayload data;
                    if (!TryCaptureBuildingWorkState(b, out data))
                        continue;
                    data.Flags |= PipOrderFlags.WorkerRoster;
                    _session.SendTo(remote, CoopMessageType.PipOrderApplied, CoopProtocol.PackPipOrder(data));
                    flushed++;
                }
                _log.Msg("[PipOrder] Send roster → " + remote.m_SteamID + " (" + reason + ") buildings=" + flushed);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] SendFullRosterTo: " + ex.Message);
            }
        }

        public byte[] BuildMorningRosterPacket(string morningId, out int buildingCount)
        {
            buildingCount = 0;
            List<PipOrderPayload> list = CaptureInterestingBuildingStates(morningOnly: true);
            buildingCount = list.Count;
            return CoopProtocol.PackMorningRoster(morningId, list.ToArray());
        }

        public void ApplyMorningRoster(PipOrderPayload[] entries)
        {
            if (entries == null || entries.Length == 0)
            {
                _log.Msg("[PipOrder] Morning roster empty — nothing to apply");
                return;
            }

            _applyingRemote = true;
            try
            {
                int applied = 0;
                int skippedMatch = 0;
                int skippedEmptyWipe = 0;
                for (int i = 0; i < entries.Length; i++)
                {
                    PipOrderPayload data = entries[i];
                    Building building;
                    if (!TryResolveBuilding(data, out building) || building == null)
                    {
                        _log.Warning("[PipOrder] Morning roster: missing building @ " +
                                     data.TerrainI + "," + data.TerrainJ);
                        continue;
                    }

                    WorkAction action = (WorkAction)data.WorkAction;
                    if (action == WorkAction.NONE)
                        action = WorkAction.Use;
                    bool wantActivated = (data.Flags & PipOrderFlags.BuildingActivated) != 0;
                    int[] wantUids = data.WorkerUids ?? new int[0];

                    if (RosterUidsMatch(building, wantUids) &&
                        building.Activated == wantActivated)
                    {
                        skippedMatch++;
                        SyncAssignedWorkerIcons(building);
                        continue;
                    }

                    if (!wantActivated && wantUids.Length == 0 && building.HasWorkers())
                    {
                        skippedEmptyWipe++;
                        _log.Msg("[PipOrder] Morning roster skip empty wipe @ " +
                                 data.TerrainI + "," + data.TerrainJ +
                                 " (local workers=" + building.NWorkers + ")");
                        continue;
                    }

                    SyncWorkerRoster(building, action, data.Param, wantUids, wantActivated, data.ExchangeId);
                    applied++;
                }
                _log.Msg("[PipOrder] Morning roster applied=" + applied +
                         " match=" + skippedMatch +
                         " skipEmptyWipe=" + skippedEmptyWipe +
                         "/" + entries.Length);
                if (!ShouldSuppressPipWorkMotion())
                    ScheduleRetetherPasses("morning-roster");
                else
                    _log.Msg("[PipOrder] Morning roster: defer retether (suppress still active)");
            }
            catch (Exception ex)
            {
                _log.Error("[PipOrder] ApplyMorningRoster: " + ex);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static bool RosterUidsMatch(Building building, int[] wantUids)
        {
            if (building == null)
                return false;
            int localCount = building.workerPips != null ? building.workerPips.Count : 0;
            int wantCount = wantUids != null ? wantUids.Length : 0;
            if (localCount != wantCount)
                return false;
            if (wantCount == 0)
                return true;

            for (int i = 0; i < wantCount; i++)
            {
                int uid = wantUids[i];
                bool found = false;
                for (int j = 0; j < localCount; j++)
                {
                    Worker w = building.workerPips[j];
                    if (w != null && w.UID == uid)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
            return true;
        }

        public void ScheduleRetetherPasses(string reason)
        {
            if (ShouldSuppressPipWorkMotion())
            {
                _log.Msg("[PipOrder] Skip retether (" + reason + ") — night/PassTurn suppress");
                return;
            }

            try
            {
                if (_retetherCoroutine != null)
                {
                    MelonCoroutines.Stop(_retetherCoroutine);
                    _retetherCoroutine = null;
                }
            }
            catch
            {
            }

            _retetherCoroutine = MelonCoroutines.Start(RetetherPassesCO(reason));
        }

        private IEnumerator RetetherPassesCO(string reason)
        {

            float[] delays = new float[] { 0f, 0.35f, 0.9f, 1.8f, 3.2f };
            for (int i = 0; i < delays.Length; i++)
            {
                if (ShouldSuppressPipWorkMotion())
                {
                    _log.Msg("[PipOrder] Abort retether mid-pass (" + reason + ") — suppress");
                    break;
                }
                if (delays[i] > 0f)
                {
                    float until = Time.unscaledTime + delays[i];
                    while (Time.unscaledTime < until)
                        yield return null;
                }

                RetetherAllAssignedWorkers(reason + "-" + i, onlyIfAway: i > 0);

                if (i == 0 || i == delays.Length - 1)
                    RebuildEconomicReservationsFromBuildings();
            }
            _retetherCoroutine = null;
        }

        public void RetetherAllAssignedWorkers(string reason, bool onlyIfAway = false)
        {
            if (ShouldSuppressPipWorkMotion())
            {
                _log.Msg("[PipOrder] Skip RetetherAllAssignedWorkers (" + reason + ") — suppress");
                return;
            }
            if (Game.I == null || Game.I.buildingsHandler == null)
                return;

            try
            {
                List<Building> buildings = Game.I.buildingsHandler.AllBuildings();
                int moved = 0;
                int skipped = 0;
                int failed = 0;
                for (int i = 0; i < buildings.Count; i++)
                {
                    Building b = buildings[i];
                    if (b == null || b.workerPips == null || b.workerPips.Count == 0)
                        continue;

                    WorkAction action = WorkAction.Use;
                    int param = 0;
                    if (b.ActionChoice != null && b.ActionChoice.HasParametricAction())
                    {
                        action = b.ActionChoice.action;
                        param = b.ActionChoice.param;
                    }
                    else if (IsConstructionBuilding(b) && b.definition != null)
                    {
                        action = b.definition.BuildAction;
                    }

                    for (int s = 0; s < b.workerPips.Count; s++)
                    {
                        Worker w = b.workerPips[s];
                        if (w == null)
                            continue;

                        Vector3 workPos = b.GetWorkActionPosition(s);
                        if (onlyIfAway && !WorkerLooksAwayFromWork(w, b, workPos))
                        {
                            skipped++;
                            continue;
                        }

                        if (ForceWorkerToWorkSlot(b, w, s, action, param))
                            moved++;
                        else
                            failed++;
                    }
                }
                _log.Msg("[PipOrder] Retether workers (" + reason + ") ok=" + moved +
                         " skip=" + skipped + " fail=" + failed);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] RetetherAllAssignedWorkers: " + ex.Message);
            }
        }

        private static bool WorkerLooksAwayFromWork(Worker worker, Building building, Vector3 workPos)
        {
            if (worker == null)
                return false;
            try
            {
                if (worker.Free)
                    return true;
                if (worker.WorkingBuilding != building)
                    return true;
                if (worker.IsHidden())
                    return true;
                Vector3 pos = worker.transform.position;
                return (pos - workPos).sqrMagnitude > 2.5f * 2.5f;
            }
            catch
            {
                return true;
            }
        }

        private bool ForceWorkerToWorkSlot(Building building, Worker worker, int slotIndex, WorkAction action, int param)
        {
            if (ShouldSuppressPipWorkMotion())
                return false;
            if (building == null || worker == null)
                return false;

            try
            {
                try
                {
                    if (worker.IsHidden())
                        worker.ExitBuilding();
                }
                catch
                {
                }

                worker.SetOccupied(action);
                worker.SetWorkingBuilding(building, action, param);
                worker._canAcceptNewMoveRequests = true;
                try { worker.ClearPauseLogic(); }
                catch { }
                try { worker.SetCanBeMoveInterrupted(true); }
                catch { }
                try { worker.InterruptCurrentMove(); }
                catch { }

                int slot = slotIndex < 0 ? 0 : slotIndex;
                Vector3 waitPos = building.GetWorkWaitPosition(slot);
                Vector3 workPos = building.GetWorkActionPosition(slot);

                bool traveled = false;
                try
                {
                    traveled = worker.Do_TravelToPoint_ForWork(waitPos, building);
                }
                catch (Exception ex)
                {
                    _log.Warning("[PipOrder] TravelForWork failed: " + ex.Message);
                }

                if (!traveled)
                {

                    try
                    {
                        worker.TeleportPos(workPos);
                    }
                    catch
                    {
                        try { worker.transform.position = workPos; }
                        catch { }
                    }

                    if (!StartWorkAtBuildingCourse(worker))
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] ForceWorkerToWorkSlot: " + ex.Message);
                return false;
            }
        }

        private bool StartWorkAtBuildingCourse(Worker worker)
        {
            if (worker == null || WorkerAppendWorkMoveMethod == null)
                return false;

            try
            {
                List<MovementBehaviour> course = new List<MovementBehaviour>(1);
                object move = WorkerAppendWorkMoveMethod.Invoke(worker, new object[] { course });
                if (move == null || course.Count == 0)
                    return false;
                worker.PerformCourse(course);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] StartWorkAtBuildingCourse: " + ex.Message);
                return false;
            }
        }

        private List<PipOrderPayload> CaptureInterestingBuildingStates(bool morningOnly)
        {
            List<PipOrderPayload> list = new List<PipOrderPayload>();
            if (Game.I == null || Game.I.buildingsHandler == null)
                return list;

            List<Building> buildings = Game.I.buildingsHandler.AllBuildings();
            for (int i = 0; i < buildings.Count; i++)
            {
                Building b = buildings[i];
                if (b == null)
                    continue;

                if (morningOnly)
                {
                    bool dwelling = b.definition != null && b.definition.IsDwelling() && b.IsBuilt;
                    if (!b.Activated && !b.HasWorkers() && !dwelling &&
                        !(b.ActionChoice != null && b.ActionChoice.HasParametricAction()))
                        continue;
                }
                else
                {
                    bool interesting = b.Activated || b.HasWorkers() ||
                                       (b.ActionChoice != null && b.ActionChoice.HasParametricAction()) ||
                                       b._weWantThisActivated ||
                                       (b.IsBuilt && b.IsWip);
                    if (!interesting)
                        continue;
                }

                PipOrderPayload data;
                if (!TryCaptureBuildingWorkState(b, out data))
                    continue;
                data.Flags |= PipOrderFlags.WorkerRoster;
                list.Add(data);
            }
            return list;
        }

        public void OnReturnedToMain()
        {
            _applyingRemote = false;
            _pendingWorkBuilding = null;
            _pendingWorkBuildings.Clear();
            _hasPendingRetry = false;
            try
            {
                if (_retetherCoroutine != null)
                {
                    MelonCoroutines.Stop(_retetherCoroutine);
                    _retetherCoroutine = null;
                }
            }
            catch
            {
            }
            PipOrderForce.End();
        }

        private static bool IsWorldPlayBlocked()
        {
            return CoopWorldGate.Active;
        }

        public bool OnLocalTryPerform(ActionLogic logic, bool fromSideAction)
        {
            if (!_session.Active)
                return true;
            if (AllowsHostLocalMutate())
                return true;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return false;
            if (IsWorldPlayBlocked())
                return false;
            if (_session.IsHost)
                return true;

            SendClientContextAction(logic, fromSideAction);
            return false;
        }

        public bool OnLocalTryRollback(Building building, bool fromSideAction, out bool result)
        {
            result = false;

            if (!_session.Active || _session.IsHost || AllowsHostLocalMutate())
                return false;
            if (IsWorldPlayBlocked())
            {
                result = false;
                return true;
            }
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
            {
                result = false;
                return true;
            }
            if (building == null || building.ActionChoice == null || !building.ActionChoice.HasAction())
                return false;

            WorkAction action = building.ActionChoice.action;
            int param = building.ActionChoice.param;

            if (!building.ActionChoice.HasSpecialAction())
                return false;
            if (ActionUtils.IsBuildAction(action))
                return false;

            try
            {
                PipOrderPayload intent;
                if (!TryBuildPayloadFromBuilding(building, action, param, fromSideAction, rollback: true, out intent))
                {
                    _log.Warning("[PipOrder] Client click-rollback: could not resolve building");
                    return false;
                }

                intent.WorkerUids = new int[0];
                intent.Flags |= PipOrderFlags.Rollback;
                RememberClientPendingContext(intent, cancel: true);

                _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
                _log.Msg("[PipOrder] Client intent " + action +
                         " @ " + intent.TerrainI + "," + intent.TerrainJ +
                         " CANCEL (click)");

                result = true;
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Client click-rollback failed: " + ex.Message);
                return false;
            }
        }

        public bool SendClientContextAction(ActionLogic logic, bool fromSideAction)
        {
            if (!_session.Active || _session.IsHost || _applyingRemote)
                return false;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return false;
            if (IsWorldPlayBlocked())
                return false;
            if (logic == null)
                return false;

            try
            {
                bool cancel = IsBuildingPerformingAction(logic.building, logic.Action, logic.Param) ||
                              IsClientPendingContextCancel(logic);

                PipOrderPayload intent;
                if (!TryBuildPayloadFromLogic(logic, fromSideAction, rollback: cancel, out intent))
                {
                    _log.Warning("[PipOrder] Client context intent: could not resolve target");
                    return false;
                }

                intent.WorkerUids = new int[0];
                if (cancel)
                    intent.Flags |= PipOrderFlags.Rollback;

                RememberClientPendingContext(intent, cancel);

                _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
                if (intent.TargetKind == PipOrderTargetKind.Creature)
                {
                    _log.Msg("[PipOrder] Client intent " + (WorkAction)intent.WorkAction +
                             " creatureUid=" + intent.TargetCreatureUid +
                             (cancel ? " CANCEL" : "") +
                             (fromSideAction ? " (side)" : ""));
                }
                else
                {
                    _log.Msg("[PipOrder] Client intent " + (WorkAction)intent.WorkAction +
                             " @ " + intent.TerrainI + "," + intent.TerrainJ +
                             (cancel ? " CANCEL" : "") +
                             (fromSideAction ? " (side)" : ""));
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Client context intent failed: " + ex.Message);
                return false;
            }
        }

        public bool OnLocalToggleActivation(Building building)
        {
            if (!_session.Active)
                return true;
            if (AllowsHostLocalMutate())
                return true;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return false;
            if (IsWorldPlayBlocked())
                return false;

            if (_session.IsHost)
                return true;

            bool wantOn = building != null && !building.Activated;

            if (wantOn && !ClientCanActivateProduction(building))
                return false;

            PipOrderPayload intent;
            if (!TryBuildPayloadFromBuilding(building, WorkAction.Use, 0, false, false, out intent))
                return false;

            intent.Flags |= wantOn ? PipOrderFlags.WantActivate : PipOrderFlags.WantDeactivate;
            intent.WorkerUids = new int[0];
            _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
            _log.Msg("[PipOrder] Client want " + (wantOn ? "ACTIVATE" : "DEACTIVATE") +
                     " (built) @ " + intent.TerrainI + "," + intent.TerrainJ);
            return false;
        }

        public void OnClientWantProductionExchange(Building building, int direction, int absoluteExchangeId = -1)
        {
            if (!_session.Active || _session.IsHost || building == null)
                return;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return;
            if (IsWorldPlayBlocked())
                return;

            PipOrderPayload intent;
            if (!TryBuildPayloadFromBuilding(building, WorkAction.Use, direction == 0 ? 1 : direction, false, false, out intent))
            {
                _log.Warning("[PipOrder] Client exchange intent: could not resolve building");
                return;
            }

            intent.Flags |= PipOrderFlags.WantExchange;
            intent.WorkerUids = new int[0];
            intent.ExchangeId = absoluteExchangeId;
            _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
            if (absoluteExchangeId >= 0)
            {
                _log.Msg("[PipOrder] Client want EXCHANGE set=" + absoluteExchangeId +
                         " @ " + intent.TerrainI + "," + intent.TerrainJ);
            }
            else
            {
                _log.Msg("[PipOrder] Client want EXCHANGE dir=" + intent.Param +
                         " @ " + intent.TerrainI + "," + intent.TerrainJ);
            }

            _applyingRemote = true;
            try
            {
                if (HasSpendingFor(building, WorkAction.Use))
                    building.UnreserveSpending(WorkAction.Use);
                if (HasGainingFor(building, WorkAction.Use))
                    building.UnreserveGaining(WorkAction.Use);

                if (absoluteExchangeId >= 0)
                    building.ForceProductionExchange(absoluteExchangeId);
                else
                {
                    building.AdvanceToNextAvailableProductionExchange(
                        checkCurrentToo: false, checkResourcesNow: true,
                        direction: direction == 0 ? 1 : direction, onlyIfSimilar: false);
                }

                if (building.Activated)
                    EnsureEconomicActivation(building, construction: false, WorkAction.Use);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Client optimistic exchange: " + ex.Message);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        public bool OnLocalWipInteraction(Building building)
        {
            if (!_session.Active)
                return true;
            if (AllowsHostLocalMutate())
                return true;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return false;
            if (IsWorldPlayBlocked())
                return false;

            if (_session.IsHost)
                return true;

            bool wantOn = building != null && !building.HasWorkers();

            if (wantOn && !ClientCanAssignConstruction(building))
                return false;

            WorkAction buildAction = WorkAction.Build;
            if (building != null && building.definition != null)
                buildAction = building.definition.BuildAction;

            PipOrderPayload intent;
            if (!TryBuildPayloadFromBuilding(building, buildAction, 0, false, false, out intent))
                return false;

            intent.Flags |= wantOn ? PipOrderFlags.WantActivate : PipOrderFlags.WantDeactivate;
            intent.WorkerUids = new int[0];
            _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
            _log.Msg("[PipOrder] Client want " + (wantOn ? "ACTIVATE" : "DEACTIVATE") +
                     " (wip) @ " + intent.TerrainI + "," + intent.TerrainJ);
            return false;
        }

        private static bool ClientCanActivateProduction(Building building)
        {
            if (building == null)
                return false;
            try
            {
                return building.CanBeActivated(allowUnruly: false, pop: true, allowForceRemoval: true);
            }
            catch
            {
                return false;
            }
        }

        private static bool ClientCanAssignConstruction(Building building)
        {
            if (building == null || building.definition == null)
                return false;

            try
            {
                if (building.IsState(BuildingState.Planned) &&
                    !building.definition.CheckIgnoresRoads() &&
                    building.terrain != null &&
                    !building.terrain.CanBeReachedWithRoad())
                {
                    BuildingSpecialStateDefinition unreachableBss =
                        Game.I.specialStatesHandler.UnreachableBSS;
                    BasicSingleton<EH>.I.Pop("prodblocked", unreachableBss.GetPrettyName());
                    return false;
                }

                if (building.IsUnreachable)
                {
                    BuildingSpecialStateDefinition unreachableBss =
                        Game.I.specialStatesHandler.UnreachableBSS;
                    BasicSingleton<EH>.I.Pop("prodblocked", unreachableBss.GetPrettyName());
                    return false;
                }

                WorkAction buildAction = building.definition.BuildAction;
                if (!building.CanAddWorkers(buildAction, pop: true))
                    return false;

                if (building.IsState(BuildingState.Planned) &&
                    !TConfig.I.CheatNoBuildingCosts &&
                    !Game.I.resourcesHandler.CheckCanPayCost(building.BuildCost, pop: true, building))
                {
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void OnHostTryPerformSuccess(ActionLogic logic, bool fromSideAction, Building affectedBuilding = null, bool wasCancelPath = false)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            if (logic == null)
                return;

            try
            {

                if (wasCancelPath)
                {
                    Building cancelTarget = affectedBuilding ?? logic.building;
                    if (cancelTarget != null)
                        CancelPendingFlush(cancelTarget);
                    return;
                }

                if (logic.building != null)
                {
                    bool actionStillCurrent = logic.building.ActionChoice != null &&
                                              logic.building.ActionChoice.HasAction() &&
                                              logic.building.ActionChoice.action == logic.Action;
                    if (!actionStillCurrent)
                    {

                        return;
                    }
                }

                PipOrderPayload applied;
                if (!TryBuildPayloadFromLogic(logic, fromSideAction, rollback: false, out applied))
                {
                    _log.Warning("[PipOrder] Host TryPerform: could not build payload for " + logic.Action);
                    return;
                }

                applied.WorkerUids = new int[0];
                applied.Flags &= ~PipOrderFlags.WorkerRoster;
                BroadcastApplied(applied);

                Building flushTarget = affectedBuilding ?? logic.building;
                if (flushTarget != null)
                    QueueBuildingFlush(flushTarget, 0.25f, "tryperform");
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Host TryPerform broadcast failed: " + ex.Message);
            }
        }

        public bool OnLocalDirectAddAndActivate(Building building, ActionAssignment assignment, Worker prechosenWorker, bool fromPicking)
        {
            if (!_session.Active)
                return true;
            if (AllowsHostLocalMutate())
                return true;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return false;
            if (IsWorldPlayBlocked())
                return false;
            if (_session.IsHost)
                return true;

            try
            {
                WorkAction action = assignment.Action;
                int param = assignment.Param;
                PipOrderPayload intent;
                if (!TryBuildPayloadFromBuilding(building, action, param, false, false, out intent))
                    return false;

                intent.Flags |= PipOrderFlags.WorkerRoster | PipOrderFlags.WantActivate;
                if (prechosenWorker != null && prechosenWorker.UID != 0)
                    intent.WorkerUids = new int[] { prechosenWorker.UID };
                else
                    intent.WorkerUids = new int[0];

                _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
                _log.Msg("[PipOrder] Client force-assign intent " + action +
                         " uid=" + (prechosenWorker != null ? prechosenWorker.UID : 0) +
                         " @ " + intent.TerrainI + "," + intent.TerrainJ);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Client DirectAddAndActivate failed: " + ex.Message);
            }

            return false;
        }

        public bool OnLocalDirectAddWorker(Building building, ActionAssignment assignment, Worker prechosenWorker, bool atLoad, bool fromPicking)
        {
            if (!_session.Active)
                return true;
            if (AllowsHostLocalMutate() || atLoad)
                return true;
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null && ModMain.Instance.Bootstrap.IsJoining)
                return false;

            if (!fromPicking && prechosenWorker == null)
                return true;

            if (IsWorldPlayBlocked())
                return false;
            if (_session.IsHost)
                return true;

            try
            {
                WorkAction action = assignment.Action;
                int param = assignment.Param;
                PipOrderPayload intent;
                if (!TryBuildPayloadFromBuilding(building, action, param, false, false, out intent))
                    return false;

                intent.Flags |= PipOrderFlags.WorkerRoster;
                if (prechosenWorker != null && prechosenWorker.UID != 0)
                    intent.WorkerUids = new int[] { prechosenWorker.UID };
                else
                    intent.WorkerUids = new int[0];

                _session.SendToHost(CoopMessageType.PipOrderIntent, CoopProtocol.PackPipOrder(intent));
                _log.Msg("[PipOrder] Client DirectAddWorker intent " + action +
                         " uid=" + (prechosenWorker != null ? prechosenWorker.UID : 0) +
                         " @ " + intent.TerrainI + "," + intent.TerrainJ);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Client DirectAddWorker failed: " + ex.Message);
            }

            return false;
        }

        public void OnHostBuildingWorkChanged(Building building, string reason)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || building == null)
                return;

            if (reason == "manual-activate" || reason == "deactivate" ||
                reason == "want-deactivate" || reason == "direct-add-activate" ||
                reason == "deactivate-build" || reason == "booked-ready" || reason == "post-anim")
            {
                QueueBuildingFlush(building, 0f, reason);
                return;
            }

            if (reason == "toggle" || reason == "want-activate" ||
                reason == "wip-interact" || reason == "book-workers" || reason == "tryperform")
            {
                QueueBuildingFlush(building, reason == "tryperform" ? 0.25f : 0.2f, reason);
                return;
            }

            if (reason == "roster-change" || reason == "clear-intent")
            {

                bool emptied = building.workerPips == null || building.workerPips.Count == 0;
                QueueBuildingFlush(building, emptied ? 0f : 0.05f, reason);
            }
        }

        private void QueueBuildingFlush(Building building, float delaySeconds, string reason)
        {
            if (building == null)
                return;

            _pendingWorkBuildings.Add(building);
            _pendingWorkBuilding = building;
            float at = Time.unscaledTime + delaySeconds;

            if (_pendingWorkBuildings.Count == 1 || at < _pendingWorkBroadcastAt)
                _pendingWorkBroadcastAt = at;

            _log.Msg("[PipOrder] Queue flush (" + reason + ") pending=" + _pendingWorkBuildings.Count +
                     " workers=" + (building.workerPips != null ? building.workerPips.Count : 0) +
                     " activated=" + building.Activated);
        }

        private void CancelPendingFlush(Building building)
        {
            if (building == null)
                return;

            if (_pendingWorkBuildings.Remove(building))
            {
                if (_pendingWorkBuilding == building)
                    _pendingWorkBuilding = null;
                _log.Msg("[PipOrder] Cancelled pending flush for building");
            }
        }

        private void FlushBuildingWorkState(Building building, string reason)
        {
            if (building == null)
                return;

            try
            {
                PipOrderPayload data;
                if (!TryCaptureBuildingWorkState(building, out data))
                    return;

                bool emptyOff = (data.Flags & PipOrderFlags.BuildingActivated) == 0 &&
                                (data.WorkerUids == null || data.WorkerUids.Length == 0);
                if (emptyOff && ShouldDeferEmptyOffFlush())
                {
                    _log.Msg("[PipOrder] Skip empty OFF flush (" + reason + ") @ " +
                             data.TerrainI + "," + data.TerrainJ + " — morning/PassTurn");
                    return;
                }

                data.Flags |= PipOrderFlags.WorkerRoster;
                BroadcastApplied(data);
                _log.Msg("[PipOrder] Flushed (" + reason + ") " + data.TerrainI + "," + data.TerrainJ +
                         " workers=" + (data.WorkerUids != null ? data.WorkerUids.Length : 0) +
                         (((data.Flags & PipOrderFlags.BuildingActivated) != 0) ? " ON" : " off") +
                         " gameActivated=" + building.Activated);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Flush (" + reason + ") failed: " + ex.Message);
            }
        }

        private bool ShouldDeferEmptyOffFlush()
        {

            try
            {
                if (ModMain.Instance != null && ModMain.Instance.HardSync != null &&
                    ModMain.Instance.HardSync.IsActive)
                    return true;
                if (HardSyncService.BlocksPlayInput)
                    return true;
                if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                    ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                    return true;
                if (ModMain.Instance != null && ModMain.Instance.TurnSync != null &&
                    ModMain.Instance.TurnSync.IsPassTurnPipelineActive)
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private void FlushAllBuildingWorkStates(string reason)
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (Game.I == null || Game.I.buildingsHandler == null)
                return;

            try
            {

                _lastBroadcastStamp = -1f;
                _lastBroadcastHash = 0;

                List<Building> buildings = Game.I.buildingsHandler.AllBuildings();
                int flushed = 0;
                int skippedEmpty = 0;
                for (int i = 0; i < buildings.Count; i++)
                {
                    Building b = buildings[i];
                    if (b == null)
                        continue;

                    if (reason == "post-turn" || reason == "post-turn-retry")
                    {
                        if (!b.Activated && !b.HasWorkers())
                        {
                            skippedEmpty++;
                            continue;
                        }
                    }
                    else
                    {
                        bool interesting = b.Activated || b.HasWorkers() ||
                                           (b.ActionChoice != null && b.ActionChoice.HasParametricAction()) ||
                                           b._weWantThisActivated ||
                                           (b.IsBuilt && b.IsWip);
                        if (!interesting)
                            continue;
                    }

                    FlushBuildingWorkState(b, reason);
                    flushed++;
                }
                _log.Msg("[PipOrder] Full roster flush (" + reason + ") buildings=" + flushed +
                         " skippedEmpty=" + skippedEmpty);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Full roster flush failed: " + ex.Message);
            }
        }

        public void OnHostRollback(Building building, bool fromSideAction,
            WorkAction capturedAction = WorkAction.NONE, int capturedParam = 0)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || building == null)
                return;
            if (building.tryRollbackWithError)
                return;

            try
            {

                CancelPendingFlush(building);

                WorkAction action = capturedAction;
                int param = capturedParam;
                if (action == WorkAction.NONE &&
                    building.ActionChoice != null && building.ActionChoice.HasAction())
                {
                    action = building.ActionChoice.action;
                    param = building.ActionChoice.param;
                }

                RestoreAfterSpecialCancel(building, action);

                if (action == WorkAction.NONE)
                    return;

                PipOrderPayload data;
                if (!TryBuildPayloadFromBuilding(building, action, param, fromSideAction, rollback: true, out data))
                    return;

                data.Flags |= PipOrderFlags.Rollback | PipOrderFlags.WorkerRoster;
                data.Flags &= ~PipOrderFlags.BuildingActivated;
                data.WorkerUids = new int[0];
                BroadcastApplied(data);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Host rollback broadcast failed: " + ex.Message);
            }
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.PipOrderIntent:
                    if (_session.IsHost)
                        HandleIntent(remote, payload);
                    break;
                case CoopMessageType.PipOrderApplied:
                    if (!_session.IsHost)
                        ApplyRemote(payload);
                    break;
            }
        }

        private void HandleIntent(CSteamID remote, byte[] payload)
        {
            PipOrderPayload data;
            if (!CoopProtocol.TryReadPipOrder(payload, out data))
            {
                _log.Warning("[PipOrder] Bad intent from " + remote.m_SteamID);
                return;
            }

            _bypassWorldGate = true;
            try
            {
                HandleIntentBody(remote, data);
            }
            catch (Exception ex)
            {
                _log.Error("[PipOrder] HandleIntent: " + ex);
            }
            finally
            {
                _bypassWorldGate = false;
            }
        }

        private void HandleIntentBody(CSteamID remote, PipOrderPayload data)
        {

                if (data.TargetKind == PipOrderTargetKind.Creature)
                {
                    ActionLogic creatureLogic;
                    if (!TryResolveLogic(data, out creatureLogic) || creatureLogic == null)
                    {
                        _log.Warning("[PipOrder] Host could not resolve creature intent uid=" +
                                     data.TargetCreatureUid + " action=" + (WorkAction)data.WorkAction);
                        return;
                    }

                    bool fromSideCreature = (data.Flags & PipOrderFlags.FromSideAction) != 0;
                    bool okCreature = creatureLogic.TryPerform(fromSideCreature);
                    _log.Msg("[PipOrder] Host applied creature intent from " + remote.m_SteamID +
                             " " + (WorkAction)data.WorkAction + " uid=" + data.TargetCreatureUid +
                             " => " + okCreature);
                    return;
                }

                Building building;
                if (!TryResolveBuilding(data, out building) || building == null)
                {

                    if (data.TargetKind == PipOrderTargetKind.Terrain)
                    {
                        ActionLogic terrainLogic;
                        if (!TryResolveLogic(data, out terrainLogic) || terrainLogic == null)
                        {
                            _log.Warning("[PipOrder] Host could not resolve terrain intent");
                            return;
                        }
                        bool fromSideTerrain = (data.Flags & PipOrderFlags.FromSideAction) != 0;
                        bool okTerrain = terrainLogic.TryPerform(fromSideTerrain);
                        _log.Msg("[PipOrder] Host applied terrain intent from " + remote.m_SteamID +
                                 " " + (WorkAction)data.WorkAction + " => " + okTerrain);
                        return;
                    }

                    _log.Warning("[PipOrder] Host could not resolve building for intent");
                    return;
                }

                if ((data.Flags & PipOrderFlags.WantActivate) != 0 &&
                    (data.Flags & PipOrderFlags.WorkerRoster) != 0)
                {

                    ActionAssignment forceAssign = new ActionAssignment((WorkAction)data.WorkAction, data.Param);
                    Worker forceWorker = null;
                    if (data.WorkerUids != null && data.WorkerUids.Length > 0)
                        forceWorker = Game.I.pipsHandler.GetPipByUID(data.WorkerUids[0]);
                    building.DirectAddWorkerAndActivate(forceAssign, forceWorker, fromPicking: true);
                    _log.Msg("[PipOrder] Host force-assign from " + remote.m_SteamID +
                             " uid=" + (forceWorker != null ? forceWorker.UID : 0));
                    OnHostBuildingWorkChanged(building, "direct-add-activate");
                    return;
                }

                if ((data.Flags & PipOrderFlags.WantActivate) != 0)
                {
                    if (IsConstructionBuilding(building))
                    {
                        if (!building.HasWorkers())
                        {

                            InvokeActOnWhileWip(building);
                        _log.Msg("[PipOrder] Host WIP ACTIVATE from " + remote.m_SteamID +
                                 " workers=" + (building.HasWorkers() ? building.GetWorkers().Count : 0));
                    }
                        else
                        {
                            _log.Msg("[PipOrder] Host WIP already has workers — resync from " + remote.m_SteamID);
                        }
                    }
                    else if (!building.Activated)
                    {
                        if (!building.CanBeActivated(allowUnruly: false, pop: true, allowForceRemoval: true))
                        {
                            _log.Msg("[PipOrder] Host rejected ACTIVATE (cannot activate) from " + remote.m_SteamID);
                            OnHostBuildingWorkChanged(building, "want-deactivate");
                            return;
                        }
                        building.ActOnWhileBuiltLogic_ActivateLogic();
                        _log.Msg("[PipOrder] Host ACTIVATE from " + remote.m_SteamID);
                    }
                    else
                    {
                        _log.Msg("[PipOrder] Host already active — resync from " + remote.m_SteamID);
                    }
                    OnHostBuildingWorkChanged(building, "want-activate");
                    return;
                }

                if ((data.Flags & PipOrderFlags.WantDeactivate) != 0)
                {
                    if (IsConstructionBuilding(building))
                    {
                        if (building.HasWorkers() || building.Activated)
                        {
                            building.DeactivateBuildAction();
                            _log.Msg("[PipOrder] Host WIP DEACTIVATE from " + remote.m_SteamID);
                        }
                        else
                        {
                            _log.Msg("[PipOrder] Host WIP already idle — resync from " + remote.m_SteamID);
                        }

                        ClearConstructionWorkVisuals(building);
                        RefreshResourceIncomeUi(building);
                    }
                    else if (building.Activated)
                    {
                        building.ApplyDeactivation(keepWorkers: false, "coop_want_off", null, showAutoRemovalFeedback: false);
                        _log.Msg("[PipOrder] Host DEACTIVATE from " + remote.m_SteamID);
                    }
                    else
                    {
                        _log.Msg("[PipOrder] Host already off — resync from " + remote.m_SteamID);
                    }
                    OnHostBuildingWorkChanged(building, "want-deactivate");
                    return;
                }

                if ((data.Flags & PipOrderFlags.WantExchange) != 0)
                {
                    try
                    {

                        if (HasSpendingFor(building, WorkAction.Use))
                            building.UnreserveSpending(WorkAction.Use);
                        if (HasGainingFor(building, WorkAction.Use))
                            building.UnreserveGaining(WorkAction.Use);

                        if (data.ExchangeId >= 0)
                        {
                            building.ForceProductionExchange(data.ExchangeId);
                            _log.Msg("[PipOrder] Host EXCHANGE set=" + data.ExchangeId +
                                     " from " + remote.m_SteamID +
                                     " workers=" + (building.HasWorkers() ? building.GetWorkers().Count : 0) +
                                     " on=" + building.Activated);
                        }
                        else
                        {
                            int dir = data.Param != 0 ? data.Param : 1;
                            building.AdvanceToNextAvailableProductionExchange(
                                checkCurrentToo: false, checkResourcesNow: true, direction: dir, onlyIfSimilar: false);
                            _log.Msg("[PipOrder] Host EXCHANGE dir=" + dir + " now=" + building.currentExchangeId +
                                     " from " + remote.m_SteamID);
                        }

                        if (building.Activated)
                            EnsureEconomicActivation(building, construction: false, WorkAction.Use);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[PipOrder] Host EXCHANGE failed: " + ex.Message);
                    }
                    OnHostBuildingWorkChanged(building, "want-exchange");
                    return;
                }

                if ((data.Flags & PipOrderFlags.ClearWorkers) != 0)
                {
                    building.RemoveAllWorkers(keepTools: false, returnToPrevAction: false, "coop_clear");
                    OnHostBuildingWorkChanged(building, "clear-intent");
                    return;
                }

                if ((data.Flags & PipOrderFlags.RemoveOne) != 0)
                {
                    Worker chosen = null;
                    if (data.WorkerUids != null && data.WorkerUids.Length > 0)
                        chosen = Game.I.pipsHandler.GetPipByUID(data.WorkerUids[0]);
                    building.RemoveWorker(keepTools: false, chosen, "coop_remove", returnToPrevAction: false);
                    return;
                }

                if ((data.Flags & PipOrderFlags.WorkerRoster) != 0)
                {
                    ActionAssignment assignment = new ActionAssignment((WorkAction)data.WorkAction, data.Param);
                    Worker prechosen = null;
                    if (data.WorkerUids != null && data.WorkerUids.Length > 0)
                        prechosen = Game.I.pipsHandler.GetPipByUID(data.WorkerUids[0]);
                    building.DirectAddWorker(assignment, prechosen, getElder: true);
                    return;
                }

                ActionLogic logic;
                if (!TryResolveLogic(data, out logic) || logic == null)
                {
                    _log.Warning("[PipOrder] Host could not resolve intent target");
                    return;
                }

                WorkAction wantAction = (WorkAction)data.WorkAction;
                bool wantRollback = (data.Flags & PipOrderFlags.Rollback) != 0;

                bool alreadySame = IsBuildingPerformingAction(building, wantAction, data.Param);

                if (wantRollback || alreadySame)
                {
                    HostForceRollbackAction(building, wantAction);
                    _log.Msg("[PipOrder] Host rollback intent from " + remote.m_SteamID +
                             " " + wantAction + (wantRollback ? " (flag)" : " (already-same)"));
                    return;
                }

                logic.building = building;
                logic.defType = GameDefinitionType.BuildingDefinition;

                bool ok = logic.TryPerform(false);
                _log.Msg("[PipOrder] Host applied intent from " + remote.m_SteamID +
                         " " + wantAction + " => " + ok);
        }

        private void HostForceRollbackAction(Building building, WorkAction expectedAction)
        {
            if (building == null)
                return;

            CancelPendingFlush(building);

            WorkAction rolled = expectedAction;
            try
            {
                if (building.ActionChoice != null && building.ActionChoice.HasAction())
                {
                    rolled = building.ActionChoice.action;

                    building.RollbackCurrentAction();
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] HostForceRollbackAction: " + ex.Message);
            }

            RestoreAfterSpecialCancel(building, rolled != WorkAction.NONE ? rolled : expectedAction);

            BroadcastExplicitRollback(building, rolled != WorkAction.NONE ? rolled : expectedAction);
        }

        private void BroadcastExplicitRollback(Building building, WorkAction expectedAction)
        {
            if (building == null)
                return;

            try
            {
                WorkAction action = expectedAction;
                int param = 0;
                if (action == WorkAction.NONE &&
                    building.ActionChoice != null && building.ActionChoice.HasAction())
                {
                    action = building.ActionChoice.action;
                    param = building.ActionChoice.param;
                }

                PipOrderPayload data;
                if (!TryBuildPayloadFromBuilding(building, action, param, false, rollback: true, out data))
                    return;

                data.Flags |= PipOrderFlags.Rollback | PipOrderFlags.WorkerRoster;
                data.Flags &= ~PipOrderFlags.BuildingActivated;
                data.WorkerUids = new int[0];
                BroadcastApplied(data);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] BroadcastExplicitRollback: " + ex.Message);
            }
        }

        private void ApplyRemote(byte[] payload)
        {
            PipOrderPayload data;
            if (!CoopProtocol.TryReadPipOrder(payload, out data))
                return;
            if (_applyingRemote)
                return;

            _applyingRemote = true;
            try
            {
                bool rollback = (data.Flags & PipOrderFlags.Rollback) != 0;
                bool roster = (data.Flags & PipOrderFlags.WorkerRoster) != 0;
                bool fromSide = (data.Flags & PipOrderFlags.FromSideAction) != 0;

                if (data.TargetKind == PipOrderTargetKind.Creature)
                {
                    ActionLogic creatureLogic;
                    if (!TryResolveLogic(data, out creatureLogic) || creatureLogic == null)
                    {
                        _log.Warning("[PipOrder] Client missing creature uid=" + data.TargetCreatureUid);
                        return;
                    }

                    if (rollback)
                    {
                        _log.Msg("[PipOrder] Client skip creature rollback (noop)");
                        return;
                    }

                    creatureLogic.TryPerform(fromSide);
                    _log.Msg("[PipOrder] Client applied creature " + (WorkAction)data.WorkAction +
                             " uid=" + data.TargetCreatureUid);
                    return;
                }

                Building building;
                if (!TryResolveBuilding(data, out building) || building == null)
                {
                    if (data.TargetKind == PipOrderTargetKind.Terrain)
                    {
                        ActionLogic terrainLogic;
                        if (TryResolveLogic(data, out terrainLogic) && terrainLogic != null)
                        {
                            terrainLogic.TryPerform(fromSide);
                            _log.Msg("[PipOrder] Client applied terrain " + (WorkAction)data.WorkAction);
                            return;
                        }
                    }

                    _pendingRetry = data;
                    _hasPendingRetry = true;
                    _pendingRetryUntil = Time.unscaledTime + 2f;
                    _log.Warning("[PipOrder] Client missing building " + data.TerrainI + "," + data.TerrainJ + " — retry");
                    return;
                }

                if (rollback)
                {
                    WorkAction rolledAction = (WorkAction)data.WorkAction;
                    try
                    {
                        if (building.ActionChoice != null && building.ActionChoice.HasAction())
                            building.RollbackCurrentAction();

                        if (building.ActionChoice != null && building.ActionChoice.HasAction())
                            building.ClearActionChoice();
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[PipOrder] Client RollbackCurrentAction: " + ex.Message);
                    }

                    RestoreAfterSpecialCancel(building, rolledAction);
                    ClearClientPendingAt(data.TerrainI, data.TerrainJ);
                    SyncWorkerRoster(building, rolledAction == WorkAction.NONE ? WorkAction.Use : rolledAction,
                        data.Param, new int[0], activated: false, -1);
                    _log.Msg("[PipOrder] Client applied rollback @ " + data.TerrainI + "," + data.TerrainJ +
                             " action=" + rolledAction);
                    return;
                }

                if (roster)
                {
                    if (ShouldSuppressPipWorkMotion())
                    {
                        _log.Msg("[PipOrder] Defer roster apply during night/PassTurn @ " +
                                 data.TerrainI + "," + data.TerrainJ);

                        bool wantActivatedQuiet = (data.Flags & PipOrderFlags.BuildingActivated) != 0;
                        WorkAction quietAction = (WorkAction)data.WorkAction;
                        if (quietAction == WorkAction.NONE)
                            quietAction = WorkAction.Use;
                        SyncWorkerRoster(building, quietAction, data.Param, data.WorkerUids,
                            wantActivatedQuiet, data.ExchangeId);
                        return;
                    }

                    bool wantActivated = (data.Flags & PipOrderFlags.BuildingActivated) != 0;
                    WorkAction action = (WorkAction)data.WorkAction;
                    if (action == WorkAction.NONE)
                        action = WorkAction.Use;

                    SyncWorkerRoster(building, action, data.Param, data.WorkerUids, wantActivated, data.ExchangeId);
                    _log.Msg("[PipOrder] Client roster sync workers=" +
                             (data.WorkerUids != null ? data.WorkerUids.Length : 0) +
                             " activated=" + wantActivated +
                             " exchange=" + data.ExchangeId);
                    if (wantActivated && data.WorkerUids != null && data.WorkerUids.Length > 0)
                        ScheduleRetetherPasses("roster-packet");
                    return;
                }

                WorkAction performAction = (WorkAction)data.WorkAction;
                ActionLogic logic = building.ActionLogicFor(performAction, data.Param);
                if (logic == null)
                {
                    _log.Warning("[PipOrder] No ActionLogic for " + performAction);
                    return;
                }

                if (building.ActionChoice != null && building.ActionChoice.HasAsCurrentAction(performAction) &&
                    building.ActionChoice.param == data.Param)
                {

                    _log.Msg("[PipOrder] Client already has action " + performAction + " — skip re-perform");
                    return;
                }

                PipOrderForce.Begin(data.WorkerUids);
                try
                {
                    logic.TryPerform(fromSide);
                }
                finally
                {
                    PipOrderForce.End();
                }

                RememberClientPendingAt(data.TerrainI, data.TerrainJ, performAction);
                _log.Msg("[PipOrder] Client TryPerform " + performAction +
                         " @ " + data.TerrainI + "," + data.TerrainJ);
            }
            catch (Exception ex)
            {
                _log.Error("[PipOrder] ApplyRemote: " + ex);
            }
            finally
            {
                PipOrderForce.End();
                _applyingRemote = false;
            }
        }

        private void SyncWorkerRoster(Building building, WorkAction action, int param, int[] uids, bool activated, int exchangeId = -1)
        {
            if (building == null)
                return;

            int[] wantUids = uids ?? new int[0];
            bool construction = IsConstructionBuilding(building);
            bool specialAction = action != WorkAction.NONE &&
                                 action != WorkAction.Use &&
                                 !ActionUtils.IsBuildAction(action);

            if (construction && building.definition != null && !specialAction)
            {
                if (action == WorkAction.NONE || action == WorkAction.Use || ActionUtils.IsBuildAction(action))
                {
                    action = building.definition.BuildAction;
                    param = 0;
                }
            }
            else if (action == WorkAction.NONE)
            {
                action = WorkAction.Use;
            }

            if (!activated && wantUids.Length > 0)
                activated = true;

            ActionAssignment assignment = new ActionAssignment(action, param);

            if (!activated)
            {

                if (construction && !specialAction)
                {
                    if (building.HasWorkers() || building.Activated)
                        building.DeactivateBuildAction();
                    else
                    {
                        if (building.ActionChoice != null && building.ActionChoice.HasParametricAction())
                            building.ClearActionChoice();
                    }
                }
                else if (building.Activated)
                {
                    building.ApplyDeactivation(keepWorkers: false, "coop_sync_off", null, showAutoRemovalFeedback: false);
                }
                else if (building.workerPips != null && building.workerPips.Count > 0)
                {
                    building.RemoveAllWorkers(keepTools: false, returnToPrevAction: false, "coop_sync_clear");
                }

                if (specialAction &&
                    building.ActionChoice != null &&
                    building.ActionChoice.HasAction() &&
                    building.ActionChoice.action != WorkAction.Use)
                {
                    try { building.ClearActionChoice(); }
                    catch { }
                }

                ClearConstructionWorkVisuals(building);
                SyncAssignedWorkerIcons(building);
                RefreshResourceIncomeUi(building);
                return;
            }

            if (!specialAction)
                ApplyHostProductionExchange(building, exchangeId);

            if (building.workerPips != null)
            {
                for (int i = building.workerPips.Count - 1; i >= 0; i--)
                {
                    Worker w = building.workerPips[i];
                    if (w == null)
                        continue;
                    bool keep = false;
                    for (int j = 0; j < wantUids.Length; j++)
                    {
                        if (wantUids[j] == w.UID)
                        {
                            keep = true;
                            break;
                        }
                    }
                    if (!keep)
                        building.RemoveWorker(keepTools: false, w, "coop_sync_remove", returnToPrevAction: false);
                }
            }

            HashSet<int> have = new HashSet<int>();
            if (building.workerPips != null)
            {
                for (int i = 0; i < building.workerPips.Count; i++)
                {
                    if (building.workerPips[i] != null)
                        have.Add(building.workerPips[i].UID);
                }
            }

            for (int i = 0; i < wantUids.Length; i++)
            {
                int uid = wantUids[i];
                if (uid == 0)
                    continue;

                Worker worker = Game.I.pipsHandler.GetPipByUID(uid);
                if (worker == null)
                {
                    _log.Warning("[PipOrder] Missing pip UID " + uid + " for roster sync");
                    continue;
                }

                EnsureWorkerTravelsAndWorks(building, assignment, worker);
                have.Add(uid);
            }

            if (!specialAction)
                building.SetWantedActivated(true, "coop_sync_roster");

            if (specialAction)
                EnsureSpecialActionState(building, action, param);
            else
                EnsureEconomicActivation(building, construction, action);

            ShowConstructionWorkVisuals(building, construction && !specialAction);
            SyncAssignedWorkerIcons(building);
            RefreshResourceIncomeUi(building);
        }

        private void EnsureSpecialActionState(Building building, WorkAction action, int param)
        {
            if (building == null)
                return;

            try
            {
                ActionLogic logic = building.ActionLogicFor(action, param);
                if (logic == null)
                    return;

                logic.building = building;
                if (building.ActionChoice == null || !building.ActionChoice.HasAsCurrentAction(action) ||
                    building.ActionChoice.param != param)
                {
                    logic.AssignThisTo(building.ActionChoice);
                }

                if (action == WorkAction.Dismantle)
                {
                    try
                    {
                        if (building.IsBuilt || building.IsWip)
                            building.Action_Dismantle();
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[PipOrder] Action_Dismantle visual: " + ex.Message);
                    }
                }

                building.UpdateActivationFeedback();
                if (building.feedback != null)
                    building.feedback.UnsetForcedData();
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] EnsureSpecialActionState: " + ex.Message);
            }
        }

        private void EnsureWorkerTravelsAndWorks(Building building, ActionAssignment assignment, Worker worker)
        {
            if (building == null || worker == null)
                return;

            bool forced = false;
            try
            {
                if (worker.HasWorkingBuilding() && worker.WorkingBuilding != building)
                {
                    Building other = worker.WorkingBuilding;
                    if (other != null)
                        other.RemoveWorker(keepTools: false, worker, "coop_reassign", returnToPrevAction: false);
                }

                if (!worker.IsWorkable(assignment.Action))
                {
                    worker.ForcePickingForWork = true;
                    forced = true;
                }

                bool alreadyHere = building.workerPips != null && building.workerPips.Contains(worker);
                if (alreadyHere)
                {
                    int slot = building.workerPips.IndexOf(worker);
                    ForceWorkerToWorkSlot(building, worker, slot, assignment.Action, assignment.Param);
                }
                else
                {

                    if (!building.DirectAddWorker(assignment, worker, getElder: false))
                    {
                        building.TakeWorker(assignment, worker, moveHimHere: true, "coop_work");
                        int slot = building.workerPips != null ? building.workerPips.IndexOf(worker) : 0;
                        ForceWorkerToWorkSlot(building, worker, slot, assignment.Action, assignment.Param);
                    }
                    else
                    {

                        int slot = building.workerPips != null ? building.workerPips.IndexOf(worker) : 0;
                        ForceWorkerToWorkSlot(building, worker, slot, assignment.Action, assignment.Param);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] EnsureWorkerTravelsAndWorks: " + ex.Message);
            }
            finally
            {
                if (forced)
                {
                    try { worker.ForcePickingForWork = false; }
                    catch { }
                }
            }
        }

        private void SyncAssignedWorkerIcons(Building building)
        {
            if (building == null || building.feedback == null || building.feedback.assignedPipoPopUp == null)
                return;

            try
            {
                AssignedPipoPopUp popup = building.feedback.assignedPipoPopUp;
                popup.RefreshData(building);

                int want = building.NWorkers;
                if (AssignedPipoCurrentField != null)
                    AssignedPipoCurrentField.SetValue(popup, want);

                if (AssignedPipoUpdateWorkersMethod != null)
                    AssignedPipoUpdateWorkersMethod.Invoke(popup, null);
                else
                    popup.RefreshData(building);

                building.UpdateActivationFeedback();
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] SyncAssignedWorkerIcons: " + ex.Message);
            }
        }

        private void ApplyHostProductionExchange(Building building, int exchangeId)
        {
            if (building == null || exchangeId < 0 || !building.IsBuilt)
                return;
            if (building.definition == null || building.definition.NAvailableResourceExchanges <= 0)
                return;
            if (exchangeId >= building.definition.NAvailableResourceExchanges)
                return;

            try
            {
                if (building.currentExchangeId == exchangeId)
                    return;

                if (HasSpendingFor(building, WorkAction.Use))
                    building.UnreserveSpending(WorkAction.Use);
                if (HasGainingFor(building, WorkAction.Use))
                    building.UnreserveGaining(WorkAction.Use);

                building.ForceProductionExchange(exchangeId);
                _log.Msg("[PipOrder] Applied exchange " + exchangeId + " @ building");
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] ForceProductionExchange failed: " + ex.Message);
            }
        }

        private void EnsureEconomicActivation(Building building, bool construction, WorkAction action)
        {
            if (building == null)
                return;

            try
            {
                if (construction)
                {
                    bool planned = building.IsState(BuildingState.Planned);
                    if (planned && !HasSpendingFor(building, action) && !TConfig.I.CheatNoBuildingCosts)
                        building.PayForPlanned();

                    if (!building.Activated)
                        building.Activate();

                    if (building.ActionChoice != null)
                    {
                        ActionLogic actionLogic = building.ActionLogicFor(action);
                        int turnsRequired = 0;
                        if (actionLogic != null &&
                            (action == WorkAction.Plant || ActionUtils.IsBuildAction(action)))
                            turnsRequired = actionLogic.Cost().Turns;
                        bool pavement = actionLogic != null && actionLogic.AddsPavementToo;
                        building.ActionChoice.AssignAction(
                            action, 0, (Sprite)null, turnsRequired, pavement,
                            pavement && actionLogic != null ? actionLogic.PavementTurns : 0);
                    }
                    return;
                }

                if (!building.Activated)
                {
                    building.ManualApplyActivation();
                }

                RefreshBuildingProductionLists(building);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Economic activation failed: " + ex.Message);
            }
        }

        private void ForceIncomeListsFromDefinition(Building building)
        {
            if (building == null || !building.IsBuilt || !building.Activated)
                return;
            if (building.definition != null && building.definition.IsDwelling())
                return;

            try
            {

                if (HasSpendingFor(building, WorkAction.Use))
                    building.UnreserveSpending(WorkAction.Use);
                if (HasGainingFor(building, WorkAction.Use))
                    building.UnreserveGaining(WorkAction.Use);

                if (building.inputResources != null &&
                    building.inputResources.Length > 0)
                {
                    List<Resource> inputs = new List<Resource>(building.inputResources);
                    for (int i = 0; i < inputs.Count; i++)
                        inputs[i].SetToMaximum();
                    building.ReserveSpending(WorkAction.Use, inputs);
                }

                if (building.outputResources != null &&
                    building.outputResources.Length > 0)
                {
                    List<Resource> outputs = new List<Resource>(building.outputResources);
                    building.ReserveGaining(WorkAction.Use, outputs);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Force income lists failed: " + ex.Message);
            }
        }

        private static bool HasSpendingFor(Building building, WorkAction action)
        {
            if (building == null || building.SpendingResLists == null)
                return false;
            for (int i = 0; i < building.SpendingResLists.Count; i++)
            {
                if (building.SpendingResLists[i].WorkAction == action)
                    return true;
            }
            return false;
        }

        private static bool HasGainingFor(Building building, WorkAction action)
        {
            if (building == null || building.GainingResLists == null)
                return false;
            for (int i = 0; i < building.GainingResLists.Count; i++)
            {
                if (building.GainingResLists[i].WorkAction == action)
                    return true;
            }
            return false;
        }

        private void ShowConstructionWorkVisuals(Building building, bool construction)
        {
            if (building == null || building.feedback == null)
                return;

            try
            {
                building.isAnimatingAction = false;
                building.UpdateActivationFeedback();

                if (construction &&
                    building.ActionChoice != null &&
                    building.ActionChoice.HasSpecialAction())
                {
                    building.feedback.ShowForcedAction(
                        building.ActionChoice.action,
                        building.ActionChoice.param,
                        reminder: false,
                        showInfo: false,
                        forceIt: true,
                        forceShowCurrent: true);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Show work visuals failed: " + ex.Message);
            }
        }

        public void OnBuildDeactivated(Building building)
        {
            if (building == null || _applyingRemote)
                return;
            ClearConstructionWorkVisuals(building);
            RefreshResourceIncomeUi(building);
        }

        private void ClearConstructionWorkVisuals(Building building)
        {
            if (building == null)
                return;

            try
            {
                building.isAnimatingAction = false;

                if (building.ActionChoice != null && building.ActionChoice.HasParametricAction())
                    building.ClearActionChoice();

                if (building.feedback == null)
                {
                    building.UpdateActivationFeedback();
                    return;
                }

                try
                {
                    building.feedback.UnsetForcedData();
                }
                catch (Exception ex)
                {
                    _log.Warning("[PipOrder] UnsetForcedData: " + ex.Message);
                }

                building.feedback.HideAllPopUps();
                building.UpdateActivationFeedback();
                building.feedback.HideAllPopUps();
                building.feedback.ApplyShownChoices();
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Clear work visuals failed: " + ex.Message);
            }
        }

        private void RefreshResourceIncomeUi(Building building)
        {
            try
            {
                if (Game.I != null && Game.I.resourcesPoolGui != null)
                    Game.I.resourcesPoolGui.UpdateModificationsDueToBuilding(building, commit: true);

                if (Game.I != null && Game.I.buildingsHandler != null)
                {
                    Game.I.buildingsHandler.HandleBuildingStatusChange(
                        forceRefreshFood: false,
                        forceSSChecksToo: false,
                        commit: true,
                        building);
                }

                if (Game.I != null && Game.I.resourcesPoolGui != null && Game.I.resourcesHandler != null)
                {
                    HashSet<ResourceType> types = new HashSet<ResourceType>();
                    if (building != null)
                        building.RegisterPendingModifications(types);

                    if (Game.I.resourcesPoolGui.PendingTypes != null)
                    {
                        foreach (ResourceType t in Game.I.resourcesPoolGui.PendingTypes)
                            types.Add(t);
                    }
                    if (types.Count > 0)
                        Game.I.resourcesPoolGui.RefreshModifiers(types);
                    else
                        Game.I.resourcesPoolGui.UpdateModificationsDueToBuilding(null, commit: true);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Income UI refresh failed: " + ex.Message);
            }
        }

        public void RebuildEconomicReservationsFromBuildings()
        {
            if (Game.I == null || Game.I.buildingsHandler == null)
                return;

            try
            {
                List<Building> buildings = Game.I.buildingsHandler.AllBuildings();
                for (int i = 0; i < buildings.Count; i++)
                {
                    Building b = buildings[i];
                    if (b == null || !b.Activated)
                        continue;

                    if (IsConstructionBuilding(b))
                    {
                        WorkAction buildAction = b.definition != null ? b.definition.BuildAction : WorkAction.Build;
                        if (b.IsState(BuildingState.Planned) &&
                            !HasSpendingFor(b, buildAction) &&
                            !TConfig.I.CheatNoBuildingCosts)
                        {
                            b.PayForPlanned();
                        }
                    }
                    else if (b.IsBuilt)
                    {

                        RefreshBuildingProductionLists(b);
                    }
                }

                if (Game.I.resourcesPoolGui != null)
                    Game.I.resourcesPoolGui.UpdateModificationsDueToBuilding(null, commit: true);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] Rebuild reservations failed: " + ex.Message);
            }
        }

        private void RefreshBuildingProductionLists(Building building)
        {
            if (building == null || !building.IsBuilt || !building.Activated)
                return;
            if (building.definition != null && building.definition.IsDwelling())
                return;

            try
            {
                if (HasSpendingFor(building, WorkAction.Use))
                    building.UnreserveSpending(WorkAction.Use);
                if (HasGainingFor(building, WorkAction.Use))
                    building.UnreserveGaining(WorkAction.Use);

                try
                {
                    building.UpdateOutputResources();
                }
                catch
                {
                }

                if (!building.WillProduceNextTurn())
                    return;

                ForceIncomeListsFromDefinition(building);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipOrder] RefreshBuildingProductionLists: " + ex.Message);
            }
        }

        private static bool IsConstructionBuilding(Building building)
        {
            if (building == null)
                return false;
            return building.IsWip || building.IsState(BuildingState.Planned);
        }

        private static void InvokeActOnWhileWip(Building building)
        {
            if (building == null || ActOnWhileWipMethod == null)
                return;
            ActOnWhileWipMethod.Invoke(building, null);
        }

        private void BroadcastApplied(PipOrderPayload data)
        {
            int hash = HashPayload(data);
            float now = Time.unscaledTime;

            if (hash == _lastBroadcastHash && now - _lastBroadcastStamp < 0.05f)
                return;
            _lastBroadcastHash = hash;
            _lastBroadcastStamp = now;

            _session.Broadcast(CoopMessageType.PipOrderApplied, CoopProtocol.PackPipOrder(data));
            _log.Msg("[PipOrder] Broadcast " + (WorkAction)data.WorkAction +
                     " workers=" + (data.WorkerUids != null ? data.WorkerUids.Length : 0) +
                     (((data.Flags & PipOrderFlags.BuildingActivated) != 0) ? " ON" : " off"));

            if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                ModMain.Instance.GameSync.MarkResourcesDirty();
        }

        private static int HashPayload(PipOrderPayload data)
        {
            unchecked
            {
                int h = data.WorkAction;
                h = h * 397 ^ data.Param;
                h = h * 397 ^ data.Flags;
                h = h * 397 ^ data.TerrainI;
                h = h * 397 ^ data.TerrainJ;
                h = h * 397 ^ data.BuildingDefId;
                if (data.WorkerUids != null)
                {
                    for (int i = 0; i < data.WorkerUids.Length; i++)
                        h = h * 397 ^ data.WorkerUids[i];
                }
                return h;
            }
        }

        private static bool TryCaptureBuildingWorkState(Building building, out PipOrderPayload data)
        {
            data = default(PipOrderPayload);
            if (building == null || building.terrain == null || building.terrain.cell == null)
                return false;

            WorkAction action = WorkAction.Use;
            int param = 0;
            if (building.ActionChoice != null && building.ActionChoice.HasParametricAction())
            {
                action = building.ActionChoice.action;
                param = building.ActionChoice.param;
            }
            else if (building.IsWip && building.definition != null)
            {
                action = building.definition.BuildAction;
            }

            if (!TryBuildPayloadFromBuilding(building, action, param, false, false, out data))
                return false;

            FillWorkersFromBuilding(building, ref data);
            data.Flags |= PipOrderFlags.WorkerRoster;
            data.ExchangeId = building.IsBuilt ? building.currentExchangeId : -1;

            bool hasWorkers = data.WorkerUids != null && data.WorkerUids.Length > 0;
            bool pendingProduction = !building.Activated &&
                                     building.IsBuilt &&
                                     action == WorkAction.Use &&
                                     hasWorkers;
            bool pendingConstruction = !building.Activated &&
                                       IsConstructionBuilding(building) &&
                                       hasWorkers;

            bool specialActionWithWorkers = hasWorkers &&
                                            action != WorkAction.NONE &&
                                            action != WorkAction.Use;

            if (building.Activated || pendingProduction || pendingConstruction ||
                specialActionWithWorkers ||
                (building._weWantThisActivated && hasWorkers) ||
                hasWorkers)
                data.Flags |= PipOrderFlags.BuildingActivated;
            return true;
        }

        private static bool TryBuildPayloadFromLogic(ActionLogic logic, bool fromSideAction, bool rollback, out PipOrderPayload data)
        {
            data = default(PipOrderPayload);
            if (logic == null)
                return false;

            data.WorkAction = (int)logic.Action;
            data.Param = logic.Param;
            if (fromSideAction)
                data.Flags |= PipOrderFlags.FromSideAction;
            if (rollback)
                data.Flags |= PipOrderFlags.Rollback;
            data.WorkerUids = new int[0];

            if (logic.building != null)
                return TryBuildPayloadFromBuilding(logic.building, logic.Action, logic.Param, fromSideAction, rollback, out data);

            if (logic.terrain != null && logic.terrain.cell != null)
            {
                data.TargetKind = PipOrderTargetKind.Terrain;
                data.TerrainI = logic.terrain.cell.i;
                data.TerrainJ = logic.terrain.cell.j;
                return true;
            }

            if (logic.creature != null)
            {
                data.TargetKind = PipOrderTargetKind.Creature;
                data.TargetCreatureUid = logic.creature.UID;
                return true;
            }

            return false;
        }

        private static bool TryBuildPayloadFromBuilding(Building building, WorkAction action, int param, bool fromSideAction, bool rollback, out PipOrderPayload data)
        {
            data = default(PipOrderPayload);
            if (building == null || building.terrain == null || building.terrain.cell == null)
                return false;

            data.WorkAction = (int)action;
            data.Param = param;
            data.TargetKind = PipOrderTargetKind.Building;
            data.TerrainI = building.terrain.cell.i;
            data.TerrainJ = building.terrain.cell.j;
            data.BuildingDefId = building.definition != null ? building.definition.ID : 0;
            if (fromSideAction)
                data.Flags |= PipOrderFlags.FromSideAction;
            if (rollback)
                data.Flags |= PipOrderFlags.Rollback;
            data.WorkerUids = new int[0];
            data.ExchangeId = -1;
            FillWorkersFromBuilding(building, ref data);
            return true;
        }

        private static void FillWorkersFromBuilding(Building building, ref PipOrderPayload data)
        {
            if (building == null || building.workerPips == null || building.workerPips.Count == 0)
            {
                data.WorkerUids = new int[0];
                return;
            }

            List<int> uids = new List<int>(building.workerPips.Count);
            for (int i = 0; i < building.workerPips.Count; i++)
            {
                Worker w = building.workerPips[i];
                if (w != null && w.UID != 0)
                    uids.Add(w.UID);
            }
            data.WorkerUids = uids.ToArray();
        }

        private static bool TryResolveBuilding(PipOrderPayload data, out Building building)
        {
            building = null;
            if (Game.I == null || Game.I.mapController == null)
                return false;

            MapTerrain terrain = Game.I.mapController.GetTerrain(data.TerrainI, data.TerrainJ);
            if (terrain == null || !terrain.HasBuilding())
                return false;

            building = terrain.Building;
            return building != null;
        }

        private static bool TryResolveLogic(PipOrderPayload data, out ActionLogic logic)
        {
            logic = null;
            WorkAction action = (WorkAction)data.WorkAction;

            if (data.TargetKind == PipOrderTargetKind.Creature)
            {
                if (Game.I == null || Game.I.creaturesHandler == null || Game.I.buildingsHandler == null)
                    return false;

                Creature creature = Game.I.creaturesHandler.GetCreatureByUID(data.TargetCreatureUid);
                if (creature == null)
                    return false;

                logic = Game.I.buildingsHandler.GetActionLogicWithoutBuilding(action, data.Param);
                if (logic == null)
                    return false;

                logic.SetTargets(null, null, creature, creature);
                return true;
            }

            Building building;
            if (!TryResolveBuilding(data, out building) || building == null)
            {
                if (data.TargetKind == PipOrderTargetKind.Terrain && Game.I != null && Game.I.mapController != null)
                {
                    MapTerrain terrain = Game.I.mapController.GetTerrain(data.TerrainI, data.TerrainJ);
                    if (terrain == null)
                        return false;
                    if (terrain.HasBuilding() && terrain.Building != null)
                        building = terrain.Building;
                    else
                    {
                        logic = Game.I.buildingsHandler.GetActionLogic(action, data.Param, null);
                        if (logic == null)
                            return false;
                        logic.terrain = terrain;
                        logic.building = null;
                        logic.defType = GameDefinitionType.TerrainDefinition;
                        return true;
                    }
                }
                else
                    return false;
            }

            logic = building.ActionLogicFor(action, data.Param);
            return logic != null;
        }

        private static bool IsBuildingPerformingAction(Building building, WorkAction action, int param)
        {
            if (building == null || action == WorkAction.NONE)
                return false;

            try
            {
                if (building.ActionChoice != null &&
                    building.ActionChoice.HasAction() &&
                    building.ActionChoice.action == action)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static void RestoreAfterSpecialCancel(Building building, WorkAction action)
        {
            if (building == null)
                return;

            try
            {
                if (action == WorkAction.Dismantle ||
                    (action == WorkAction.NONE &&
                     building.TargetBuildingStage < building.CurrentBuildingStage))
                {
                    if (building.TargetBuildingStage < building.CurrentBuildingStage)
                        building.SetTargetBuildingStage(building.CurrentBuildingStage);
                    building.SetStateToPreviousBuiltOrWIP();
                    if (building.feedback != null)
                        building.feedback.UnsetForcedData();
                    building.UpdateActivationFeedback();
                }
            }
            catch
            {
            }
        }

        private static long CellKey(int i, int j)
        {
            return ((long)i << 32) ^ (uint)j;
        }

        private bool IsClientPendingContextCancel(ActionLogic logic)
        {
            if (logic == null || logic.building == null ||
                logic.building.terrain == null || logic.building.terrain.cell == null)
                return false;

            long key = CellKey(logic.building.terrain.cell.i, logic.building.terrain.cell.j);
            int pending;
            if (!_clientPendingContextActions.TryGetValue(key, out pending))
                return false;
            return pending == (int)logic.Action;
        }

        private void RememberClientPendingContext(PipOrderPayload intent, bool cancel)
        {
            if (intent.TargetKind == PipOrderTargetKind.Creature)
                return;

            long key = CellKey(intent.TerrainI, intent.TerrainJ);
            if (cancel)
                _clientPendingContextActions.Remove(key);
            else
                _clientPendingContextActions[key] = intent.WorkAction;
        }

        private void RememberClientPendingAt(int i, int j, WorkAction action)
        {
            if (action == WorkAction.NONE || action == WorkAction.Use)
                return;
            _clientPendingContextActions[CellKey(i, j)] = (int)action;
        }

        private void ClearClientPendingAt(int i, int j)
        {
            _clientPendingContextActions.Remove(CellKey(i, j));
        }
    }
}
