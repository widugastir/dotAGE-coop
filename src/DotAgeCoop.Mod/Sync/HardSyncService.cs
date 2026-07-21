using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class HardSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;

        private bool _active;
        private bool _hostView;
        private float _startedAt;
        private readonly HashSet<ulong> _ackPeers = new HashSet<ulong>();
        private object _hostRoutine;

        public bool IsActive { get { return _active; } }
        public static bool BlocksPlayInput { get; private set; }

        public HardSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {
            if (!_active)
            {
                BlocksPlayInput = false;
                return;
            }

            BlocksPlayInput = true;

            if (Time.unscaledTime - _startedAt > 30f)
            {
                _log.Warning("[HardSync] Safety timeout — unlocking");
                FinishLocal("timeout");
            }
        }

        public void OnReturnedToMain()
        {
            FinishLocal("return-to-main");
        }

        public void RequestHostHardSync()
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (_active)
            {
                _log.Msg("[HardSync] Already running");
                return;
            }

            try
            {
                if (Game.I == null || !Game.I.GameIsStarted())
                {
                    _log.Warning("[HardSync] Game not started — ignore");
                    return;
                }
            }
            catch
            {
                _log.Warning("[HardSync] Game not ready — ignore");
                return;
            }

            if (_hostRoutine != null)
            {
                try { MelonCoroutines.Stop(_hostRoutine); }
                catch { }
                _hostRoutine = null;
            }

            _hostRoutine = MelonCoroutines.Start(HostHardSyncCO());
        }

        private IEnumerator HostHardSyncCO()
        {
            _active = true;
            _hostView = true;
            _startedAt = Time.unscaledTime;
            _ackPeers.Clear();
            _ackPeers.Add(_session.SelfId);
            BlocksPlayInput = true;

            _log.Msg("[HardSync] STAGE host: begin — blocking peers + pushing full world");
            try
            {
                _session.Broadcast(CoopMessageType.HardSyncBegin, CoopProtocol.StringPayload("hard-sync"));
            }
            catch (Exception ex)
            {
                _log.Warning("[HardSync] Begin broadcast: " + ex.Message);
            }

            yield return null;
            yield return null;

            try
            {
                PushFullWorldFromHost();
            }
            catch (Exception ex)
            {
                _log.Error("[HardSync] PushFullWorld: " + ex);
            }

            float waitUntil = Time.unscaledTime + 8f;
            float nextNudge = Time.unscaledTime + 1.5f;
            while (_ackPeers.Count < Math.Max(1, _session.CoopMemberCount) &&
                   Time.unscaledTime < waitUntil)
            {
                if (Time.unscaledTime >= nextNudge)
                {
                    nextNudge = Time.unscaledTime + 1.5f;
                    _log.Msg("[HardSync] Waiting ACK " + _ackPeers.Count + "/" +
                             Math.Max(1, _session.CoopMemberCount));
                }
                yield return null;
            }

            try
            {
                _session.Broadcast(CoopMessageType.HardSyncEnd, CoopProtocol.StringPayload("hard-sync-done"));
            }
            catch (Exception ex)
            {
                _log.Warning("[HardSync] End broadcast: " + ex.Message);
            }

            FinishLocal("host-done");
            _hostRoutine = null;
            _log.Msg("[HardSync] STAGE host: done acks=" + _ackPeers.Count);
        }

        private void PushFullWorldFromHost()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null)
                return;

            if (mod.GameSync != null)
                mod.GameSync.HostPushHardSyncWorld();

            if (mod.ResearchSync != null)
                mod.ResearchSync.BroadcastSnapshotImmediate();

            if (mod.MechanicsSync != null)
                mod.MechanicsSync.BroadcastSnapshotImmediate();

            if (mod.ScalesSync != null)
                mod.ScalesSync.BroadcastScalesSnapshotImmediate(forceDuringPassTurn: true);

            if (mod.EventSync != null)
                mod.EventSync.BroadcastHardSyncState();

            if (mod.PipAppearance != null)
                mod.PipAppearance.BroadcastSnapshotImmediate();

            if (mod.PipOrders != null)
                mod.PipOrders.FlushFullRosterForJoin("hard-sync");
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.HardSyncBegin:
                    if (_session.IsHost)
                        return;
                    BeginClient(CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.HardSyncEnd:
                    if (_session.IsHost)
                        return;
                    FinishLocal("host-end");
                    break;

                case CoopMessageType.HardSyncAck:
                    if (!_session.IsHost || !_active)
                        return;
                    _ackPeers.Add(remote.m_SteamID);
                    _log.Msg("[HardSync] ACK from " + remote.m_SteamID + " (" +
                             _ackPeers.Count + "/" + Math.Max(1, _session.CoopMemberCount) + ")");
                    break;
            }
        }

        private void BeginClient(string reason)
        {
            _active = true;
            _hostView = false;
            _startedAt = Time.unscaledTime;
            BlocksPlayInput = true;
            _log.Msg("[HardSync] STAGE client: begin (" + reason + ") — Host syncing…");

            MelonCoroutines.Start(ClientAckAfterSettle());
        }

        private IEnumerator ClientAckAfterSettle()
        {
            float until = Time.unscaledTime + 1.2f;
            while (Time.unscaledTime < until && _active)
                yield return null;

            if (!_active || _session.IsHost)
                yield break;

            try
            {
                _session.SendToHost(CoopMessageType.HardSyncAck, CoopProtocol.StringPayload("applied"));
                _log.Msg("[HardSync] STAGE client: sent ACK");
            }
            catch (Exception ex)
            {
                _log.Warning("[HardSync] ACK: " + ex.Message);
            }
        }

        private void FinishLocal(string reason)
        {
            if (!_active && !BlocksPlayInput)
                return;

            _active = false;
            _hostView = false;
            BlocksPlayInput = false;
            _log.Msg("[HardSync] STAGE unlock (" + reason + ")");
        }

        public void DrawOverlay()
        {
            if (!_active)
                return;

            float w = 480f;
            float h = 52f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.2f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 20;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.92f, 0.55f, 1f);
            GUI.Label(r, _hostView ? "Syncing clients..." : "Host syncing...", style);
            GUI.color = prev;
        }
    }
}
