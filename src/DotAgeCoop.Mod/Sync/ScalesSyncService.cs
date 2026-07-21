using System;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class ScalesSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private bool _applyingRemote;
        private bool _pendingScalesBroadcast;
        private float _lastScalesBroadcastAt;

        private byte[] _deferredScalesPayload;

        public bool ApplyingRemote { get { return _applyingRemote; } }

        public ScalesSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void OnReturnedToMain()
        {
            _pendingScalesBroadcast = false;
            _deferredScalesPayload = null;
        }

        public void Tick()
        {
            if (!_session.Active)
                return;

            if (!_session.IsHost && _deferredScalesPayload != null &&
                !ShouldSuppressPassTurnScalesSync() &&
                !GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
            {
                byte[] pending = _deferredScalesPayload;
                _deferredScalesPayload = null;
                ApplyScalesSnapshot(pending);
            }

            if (!_session.IsHost || !_pendingScalesBroadcast)
                return;
            if (ShouldSuppressPassTurnScalesSync())
                return;
            if (Time.unscaledTime - _lastScalesBroadcastAt < 0.2f)
                return;
            _pendingScalesBroadcast = false;
            BroadcastScalesSnapshot();
        }

        public void MarkScalesDirty()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;

            _pendingScalesBroadcast = true;
        }

        public void BroadcastScalesSnapshotImmediate()
        {
            BroadcastScalesSnapshotImmediate(forceDuringPassTurn: false);
        }

        public void BroadcastScalesSnapshotImmediate(bool forceDuringPassTurn)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            if (!forceDuringPassTurn && ShouldSuppressPassTurnScalesSync())
                return;
            _pendingScalesBroadcast = false;
            BroadcastScalesSnapshot();
        }

        public static bool ShouldSuppressPassTurnScalesSync()
        {
            return GameSyncService.ShouldSuppressPassTurnResourceSync();
        }

        public void SendScalesSnapshotTo(CSteamID remote)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;

            try
            {
                int scenario;
                ScaleBalanceNet[] balances;
                if (!TryCaptureScales(out scenario, out balances))
                    return;
                _session.SendTo(remote, CoopMessageType.ScalesSnapshot,
                    CoopProtocol.PackScalesSnapshot(scenario, balances));
                _log.Msg("[Scales] Send → " + remote.m_SteamID +
                         " (n=" + balances.Length + " scenario=" + scenario + ")");
            }
            catch (Exception ex)
            {
                _log.Warning("[Scales] SendScalesSnapshotTo: " + ex.Message);
            }
        }

        private void BroadcastScalesSnapshot()
        {
            try
            {
                int scenario;
                ScaleBalanceNet[] balances;
                if (!TryCaptureScales(out scenario, out balances))
                    return;
                _lastScalesBroadcastAt = Time.unscaledTime;
                _session.Broadcast(CoopMessageType.ScalesSnapshot,
                    CoopProtocol.PackScalesSnapshot(scenario, balances));
                _log.Msg("[Scales] Broadcast n=" + balances.Length + " scenario=" + scenario);
            }
            catch (Exception ex)
            {
                _log.Warning("[Scales] Broadcast: " + ex.Message);
            }
        }

        private static bool TryCaptureScales(out int scenarioIndex, out ScaleBalanceNet[] balances)
        {
            scenarioIndex = 0;
            balances = new ScaleBalanceNet[0];
            if (Game.I == null || Game.I.scalesHandler == null)
                return false;

            ScalesHandler sh = Game.I.scalesHandler;
            scenarioIndex = sh.chosenScenarioIndex;
            List<ScaleBalance> all = sh.AllBalances;
            if (all == null || all.Count == 0)
                return false;

            balances = new ScaleBalanceNet[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                ScaleBalance bal = all[i];
                ScaleBalanceSaveData sd = bal.saveData;
                balances[i].ScaleId = bal.scale != null ? bal.scale.ID : 0;
                balances[i].Enabled = sd.Enabled;
                balances[i].Visible = sd.Visible;
                balances[i].Destroyed = sd.Destroyed;
                balances[i].FlowValue = sd.flowValue;
                balances[i].SnowballValue = sd.snowballValue;
                balances[i].TemporaryValue = sd.temporaryValue;
                balances[i].SeasonalValue = sd.seasonalValue;
                balances[i].FailedEventsInARow = sd.nFailedEventsInARow;
            }
            return true;
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            if (type != CoopMessageType.ScalesSnapshot || _session.IsHost)
                return;

            if (GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
            {
                if (Time.frameCount % 180 == 0)
                    _log.Msg("[Scales] STAGE defer snapshot (joining/generating)");
                return;
            }

            ApplyScalesSnapshot(payload);
        }

        private void ApplyScalesSnapshot(byte[] payload)
        {
            int scenario;
            ScaleBalanceNet[] balances;
            if (!CoopProtocol.TryReadScalesSnapshot(payload, out scenario, out balances))
            {
                _log.Warning("[Scales] Bad snapshot");
                return;
            }
            if (Game.I == null || Game.I.scalesHandler == null)
                return;

            if (ShouldSuppressPassTurnScalesSync())
            {
                _deferredScalesPayload = payload;
                if (Time.frameCount % 180 == 0)
                    _log.Msg("[Scales] STAGE defer ScalesSnapshot (pass-turn) — queued");
                return;
            }

            _deferredScalesPayload = null;
            _applyingRemote = true;
            try
            {
                ScalesHandler sh = Game.I.scalesHandler;
                if (scenario >= 0 && scenario != sh.chosenScenarioIndex)
                    sh.ForceScenarioIndex(scenario);

                int changed = 0;
                for (int i = 0; i < balances.Length; i++)
                {
                    ScaleBalanceNet net = balances[i];
                    ScaleDefinition def = FindScaleById(sh, net.ScaleId);
                    if (def == null)
                        continue;

                    ScaleBalance bal = sh.GetBalance(def);
                    if (bal == null || bal.saveData == null)
                        continue;

                    ScaleBalanceSaveData sd = bal.saveData;
                    int oldActual = bal.ActualScaleValue;
                    sd.Enabled = net.Enabled;
                    sd.Visible = net.Visible;
                    sd.Destroyed = net.Destroyed;
                    try { sh.ForceValue(def, net.FlowValue, FlowType.Flow); }
                    catch { sd.flowValue = net.FlowValue; }
                    try { sh.ForceValue(def, net.SnowballValue, FlowType.Snowball); }
                    catch { sd.snowballValue = net.SnowballValue; }
                    try { sh.ForceValue(def, net.TemporaryValue, FlowType.Temp); }
                    catch { sd.temporaryValue = net.TemporaryValue; }
                    sd.seasonalValue = net.SeasonalValue;
                    sd.nFailedEventsInARow = net.FailedEventsInARow;

                    int delta = bal.ActualScaleValue - oldActual;
                    sh.NotifyValueChange(def, delta, animating: false);
                    if (delta != 0)
                    {
                        changed++;
                        string scaleName = "?";
                        try { scaleName = !string.IsNullOrEmpty(def.name) ? def.name : ("id=" + net.ScaleId); }
                        catch { scaleName = "id=" + net.ScaleId; }
                        EconDebugLog.ScaleSnap(scaleName, oldActual, bal.ActualScaleValue,
                            net.FlowValue, net.SnowballValue, net.TemporaryValue);
                    }
                }

                if (Game.I.scalesHandlerGUI != null)
                {
                    Game.I.scalesHandlerGUI.CheckAllBalanceVisibility();
                    Game.I.scalesHandlerGUI.RefreshThreatValues(gemsToo: true, forced: true);
                }

                _log.Msg("[Scales] Applied n=" + balances.Length +
                         " changed=" + changed + " scenario=" + scenario);
            }
            catch (Exception ex)
            {
                _log.Warning("[Scales] Apply: " + ex.Message);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static ScaleDefinition FindScaleById(ScalesHandler sh, int scaleId)
        {
            if (sh == null || sh.AllBalances == null)
                return null;
            for (int i = 0; i < sh.AllBalances.Count; i++)
            {
                ScaleBalance bal = sh.AllBalances[i];
                if (bal != null && bal.scale != null && bal.scale.ID == scaleId)
                    return bal.scale;
            }
            return null;
        }
    }
}
