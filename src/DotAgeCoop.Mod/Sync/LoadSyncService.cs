using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class LoadSyncService
    {
        public const string CoopRecvSaveKey = "game_coop_recv";
        private const int ChunkSize = 24 * 1024;
        private const int MaxSaveBytes = 8 * 1024 * 1024;

        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;

        private bool _clientReceiving;
        private bool _clientWaitingForHost;
        private bool _saveReadyOnDisk;
        private bool _transferComplete;
        private bool _transferFailed;
        private string _transferFailReason = string.Empty;
        private int _totalBytes;
        private int _receivedBytes;
        private int _chunkCount;
        private int _chunkSize = ChunkSize;
        private byte[] _buffer;
        private bool[] _chunkGot;
        private string _recvSaveKey = CoopRecvSaveKey;
        private object _clientRoutine;
        private bool _clientLoadingGame;

        private bool _hostPreTransferActive;
        private bool _distributedForThisLoad;
        private readonly HashSet<ulong> _ackedPeers = new HashSet<ulong>();
        private int _hostSendNeeded = 1;
        private int _hostSendReady = 1;

        public bool IsClientStagedLoading
        {
            get { return _clientReceiving || _clientWaitingForHost || _clientLoadingGame; }
        }

        public bool IsHostPreTransferActive { get { return _hostPreTransferActive; } }

        public int ProgressPercent
        {
            get
            {
                if (_totalBytes <= 0)
                    return 0;
                int p = (int)((long)_receivedBytes * 100L / _totalBytes);
                if (p < 0) p = 0;
                if (p > 100) p = 100;
                return p;
            }
        }

        public string StageLabel
        {
            get
            {
                if (_hostPreTransferActive)
                    return "Sending save " + _hostSendReady + "/" + _hostSendNeeded;

                if (_transferFailed)
                    return "Receiving save: error";

                if (_clientReceiving)
                {
                    if (_transferComplete)
                        return "Receiving save: 100/100%";
                    return "Receiving save: " + ProgressPercent + "/100%";
                }

                if (_clientWaitingForHost)
                    return "Ready: host is preparing the save…";

                if (_clientLoadingGame)
                    return "Loading save…";

                return "…";
            }
        }

        public LoadSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void OnReturnedToMain()
        {
            StopClientRoutine();
            ResetClientTransfer();
            _saveReadyOnDisk = false;
            _clientWaitingForHost = false;
            _clientLoadingGame = false;
            _hostPreTransferActive = false;
            _distributedForThisLoad = false;
            _ackedPeers.Clear();
        }

        public void AbortClientTransferForNewGame()
        {
            if (_session.IsHost)
                return;

            StopClientRoutine();
            ResetClientTransfer();
            _saveReadyOnDisk = false;
            _clientWaitingForHost = false;
            _clientLoadingGame = false;
            _clientReceiving = false;
            _log.Msg("[LoadSync] Aborted client transfer state for New Game join");
        }

        public IEnumerator WrapHostLoadCoroutine(IEnumerator original)
        {
            if (_session.Active && _session.IsHost && _session.MemberCount > 1)
                yield return HostEnsureSaveDistributedCO();

            if (original == null)
                yield break;

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
                    _log.Error("[LoadSync] LoadGame CO threw: " + ex);
                    yield break;
                }

                if (!moved)
                    yield break;
                yield return current;
            }
        }

        private IEnumerator HostEnsureSaveDistributedCO()
        {
            if (_distributedForThisLoad)
            {
                _log.Msg("[LoadSync] Save already distributed this load — skip");
                yield break;
            }

            _hostPreTransferActive = true;
            _ackedPeers.Clear();
            _hostSendNeeded = Math.Max(1, _session.MemberCount);
            _hostSendReady = 1;
            float shownAt = Time.unscaledTime;

            _log.Msg("[LoadSync] Host pre-transfer before LoadGame (members=" + _hostSendNeeded + ")");

            try
            {
                byte[] bytes = null;
                string err = null;
                yield return ReadActiveSaveBytesCO(b => bytes = b, e => err = e);

                if (bytes == null)
                {
                    _log.Error("[LoadSync] Host cannot read save: " + err + " — continuing load anyway");
                    yield break;
                }

                int clientCount = Math.Max(0, _hostSendNeeded - 1);
                if (clientCount <= 0)
                {
                    _log.Msg("[LoadSync] No clients — skip push");
                }
                else
                {
                    _log.Msg("[LoadSync] Pushing " + bytes.Length + " bytes to " + clientCount + " client(s)");
                    yield return PushSaveBroadcastCO(bytes);

                    float deadline = Time.unscaledTime + 120f;
                    while (_ackedPeers.Count < clientCount && Time.unscaledTime < deadline)
                    {
                        _hostSendReady = 1 + _ackedPeers.Count;
                        _hostSendNeeded = Math.Max(_hostSendNeeded, _session.MemberCount);
                        clientCount = Math.Max(clientCount, _hostSendNeeded - 1);
                        yield return null;
                    }

                    _hostSendReady = 1 + _ackedPeers.Count;
                    if (_ackedPeers.Count < clientCount)
                    {
                        _log.Warning("[LoadSync] Timed out waiting for save ACKs (" +
                                     _ackedPeers.Count + "/" + clientCount + ") — loading anyway");
                    }
                    else
                    {
                        _log.Msg("[LoadSync] All clients have the save");
                    }
                }

                while (Time.unscaledTime - shownAt < 0.85f)
                {
                    _hostSendReady = Math.Max(_hostSendReady, 1 + _ackedPeers.Count);
                    yield return null;
                }

                _distributedForThisLoad = true;
            }
            finally
            {
                _hostPreTransferActive = false;
            }
        }

        public void BeginClientLoadFromReceivedSave(GameBootstrapPayload payload)
        {
            if (!_session.Active || _session.IsHost)
                return;

            StopClientRoutine();
            _clientLoadingGame = true;
            _clientWaitingForHost = false;
            _clientReceiving = false;
            _log.Msg("[LoadSync] Client loading received save");
            _clientRoutine = MelonCoroutines.Start(ClientLoadFromDiskCO(payload));
        }

        public void BeginClientStagedLoad(GameBootstrapPayload payload)
        {
            BeginClientLoadFromReceivedSave(payload);
        }

        private IEnumerator ReadActiveSaveBytesCO(Action<byte[]> onOk, Action<string> onErr)
        {
            byte[] bytes = null;
            string err = null;
            try
            {

                try
                {
                    if (Game.I != null && Game.I.SaveGameData != null && Game.I.GameIsStarted())
                        Game.I.SaveGameData.Save_Immediate(Game.I);
                }
                catch (Exception ex)
                {
                    _log.Warning("[LoadSync] Save_Immediate: " + ex.Message);
                }

                JSONSaveSystem ss = Game.I != null ? Game.I.saveSystemGame as JSONSaveSystem : null;
                if (ss == null)
                    throw new Exception("saveSystemGame missing");

                try { ss.ForceSaveKey("game"); }
                catch { }

                string path = ss.SavePath;
                if (!File.Exists(path))
                    path = ss.NoPrefixSavePath;
                if (!File.Exists(path))
                    throw new Exception("Save file missing: " + ss.SavePath);

                string name = Path.GetFileName(path) ?? string.Empty;
                if (name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("safenet", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception("Refusing backup path: " + name);

                bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0 || bytes.Length > MaxSaveBytes)
                    throw new Exception("Bad save size " + bytes.Length);

                _log.Msg("[LoadSync] Read save " + path + " (" + bytes.Length + " bytes)");
            }
            catch (Exception ex)
            {
                err = ex.Message;
                _log.Error("[LoadSync] Host read save: " + ex);
            }

            yield return null;
            if (bytes != null)
                onOk(bytes);
            else
                onErr(err ?? "read-fail");
        }

        private IEnumerator PushSaveBroadcastCO(byte[] bytes)
        {
            int chunkCount = (bytes.Length + ChunkSize - 1) / ChunkSize;
            _session.Broadcast(
                CoopMessageType.SaveTransferBegin,
                CoopProtocol.PackSaveTransferBegin(bytes.Length, ChunkSize, chunkCount, CoopRecvSaveKey));

            yield return null;

            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * ChunkSize;
                int len = Math.Min(ChunkSize, bytes.Length - offset);
                byte[] slice = new byte[len];
                Buffer.BlockCopy(bytes, offset, slice, 0, len);
                _session.Broadcast(
                    CoopMessageType.SaveTransferChunk,
                    CoopProtocol.PackSaveTransferChunk(i, slice));

                if ((i % 2) == 1)
                    yield return null;
            }

            yield return null;
            _session.Broadcast(
                CoopMessageType.SaveTransferEnd,
                CoopProtocol.PackSaveTransferEnd(true, "ok"));
            _log.Msg("[LoadSync] Broadcast finished (" + chunkCount + " chunks)");
        }

        private IEnumerator ClientReceivePushCO()
        {
            _clientReceiving = true;
            _clientWaitingForHost = false;
            _saveReadyOnDisk = false;

            float deadline = Time.unscaledTime + 120f;
            while (!_transferComplete && !_transferFailed && Time.unscaledTime < deadline)
                yield return null;

            if (_transferFailed || !_transferComplete || _buffer == null)
            {
                _log.Error("[LoadSync] Receive failed: " + _transferFailReason);
                _clientReceiving = false;
                yield break;
            }

            try
            {
                JSONSaveSystem ss = Game.I.saveSystemGame as JSONSaveSystem;
                if (ss == null)
                    throw new Exception("saveSystemGame is not JSONSaveSystem");

                ss.ForceSaveKey(_recvSaveKey);
                string path = ss.SavePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(path, _buffer);
                _log.Msg("[LoadSync] Wrote " + _buffer.Length + " bytes → " + path);

                ss.ForceSaveKey("game");
            }
            catch (Exception ex)
            {
                _log.Error("[LoadSync] Write save: " + ex);
                FailClient("write");
                yield break;
            }

            _saveReadyOnDisk = true;
            _clientReceiving = false;
            _clientWaitingForHost = true;
            _session.SendToHost(CoopMessageType.SaveTransferAck);
            _log.Msg("[LoadSync] SaveTransferAck sent — waiting for host LoadGame");
            _buffer = null;
        }

        private IEnumerator ClientLoadFromDiskCO(GameBootstrapPayload payload)
        {
            try
            {
                GameBootstrapService.ApplyHostConfigPublic(payload);
            }
            catch (Exception ex)
            {
                _log.Error("[LoadSync] ApplyHostConfig: " + ex);
                _clientLoadingGame = false;
                yield break;
            }

            float waitPush = Time.unscaledTime + 60f;
            while (!_saveReadyOnDisk && !_transferFailed && Time.unscaledTime < waitPush)
                yield return null;

            if (!_saveReadyOnDisk && !_clientReceiving)
            {
                _log.Warning("[LoadSync] No pre-pushed save — requesting from host");
                ResetClientTransfer();
                _clientReceiving = true;
                _session.SendToHost(CoopMessageType.SaveTransferRequest);

                float waitReq = Time.unscaledTime + 90f;
                while (!_transferComplete && !_transferFailed && Time.unscaledTime < waitReq)
                    yield return null;

                if (_transferComplete && _buffer != null)
                {
                    try
                    {
                        JSONSaveSystem ss = Game.I.saveSystemGame as JSONSaveSystem;
                        if (ss == null)
                            throw new Exception("saveSystemGame missing");
                        ss.ForceSaveKey(_recvSaveKey);
                        string path = ss.SavePath;
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllBytes(path, _buffer);
                        _saveReadyOnDisk = true;
                        _buffer = null;
                        try { ss.ForceSaveKey("game"); }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        _log.Error("[LoadSync] Fallback write: " + ex);
                    }
                }

                _clientReceiving = false;
            }

            if (!_saveReadyOnDisk)
            {
                _log.Error("[LoadSync] Cannot load — save not on disk");
                _clientLoadingGame = false;
                yield break;
            }

            yield return null;

            try
            {
                JSONSaveSystem ss = Game.I.saveSystemGame as JSONSaveSystem;
                if (ss != null)
                    ss.ForceSaveKey(_recvSaveKey);

                Game.I.startGameController.LoadGame_Immediate(checkSaveProfile: false);
            }
            catch (Exception ex)
            {
                _log.Error("[LoadSync] LoadGame_Immediate: " + ex);
                _clientLoadingGame = false;
                yield break;
            }

            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 180f)
            {
                if (Game.Ready && Game.I != null && Game.I.GameIsStarted() && !Game.I.IsCurrentlyLoading)
                    break;
                yield return null;
            }

            try
            {
                JSONSaveSystem ss = Game.I.saveSystemGame as JSONSaveSystem;
                if (ss != null)
                    ss.ForceSaveKey("game");
            }
            catch
            {
            }

            try
            {
                if (Game.I != null)
                {
                    Game.I.SetJustLoaded();
                    Game.I.hasPlacedFirst = true;
                    Game.I.isPerformingFirstPlacement = false;
                }
            }
            catch
            {
            }

            _clientLoadingGame = false;
            _clientWaitingForHost = false;
            _saveReadyOnDisk = false;
            _log.Msg("[LoadSync] Client load done — ClientInGame");
            _session.SendToHost(CoopMessageType.ClientInGame,
                CoopProtocol.StringPayload("seed=" + payload.Seed + ";load=1;save=1"));
        }

        private void FailClient(string reason)
        {
            _transferFailed = true;
            _transferFailReason = reason ?? string.Empty;
            _clientReceiving = false;
        }

        private void ResetClientTransfer()
        {
            _clientReceiving = false;
            _transferComplete = false;
            _transferFailed = false;
            _transferFailReason = string.Empty;
            _totalBytes = 0;
            _receivedBytes = 0;
            _chunkCount = 0;
            _chunkSize = ChunkSize;
            _buffer = null;
            _chunkGot = null;
            _recvSaveKey = CoopRecvSaveKey;
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.SaveTransferRequest:

                    if (_session.IsHost)
                        MelonCoroutines.Start(HostServeOneClientCO(remote));
                    break;

                case CoopMessageType.SaveTransferBegin:
                    if (!_session.IsHost)
                        OnClientBeginPush(payload);
                    break;

                case CoopMessageType.SaveTransferChunk:
                    if (!_session.IsHost)
                        HandleChunk(payload);
                    break;

                case CoopMessageType.SaveTransferEnd:
                    if (!_session.IsHost)
                        HandleEnd(payload);
                    break;

                case CoopMessageType.SaveTransferAck:
                    if (_session.IsHost)
                        OnClientAck(remote);
                    break;
            }
        }

        private void OnClientBeginPush(byte[] payload)
        {
            ResetClientTransfer();
            HandleBegin(payload);
            if (_transferFailed)
                return;

            _clientWaitingForHost = false;
            StopClientRoutine();
            _clientRoutine = MelonCoroutines.Start(ClientReceivePushCO());
        }

        private void OnClientAck(CSteamID remote)
        {
            ulong id = remote.m_SteamID;
            if (id == 0)
                id = 2UL;
            if (_ackedPeers.Add(id))
            {
                _hostSendReady = 1 + _ackedPeers.Count;
                _log.Msg("[LoadSync] Save ACK from " + id + " (" + _hostSendReady + "/" + _hostSendNeeded + ")");
            }
        }

        private void HandleBegin(byte[] payload)
        {
            int total;
            int chunkSize;
            int chunks;
            string key;
            if (!CoopProtocol.TryReadSaveTransferBegin(payload, out total, out chunkSize, out chunks, out key))
            {
                FailClient("bad-begin");
                return;
            }

            if (total <= 0 || total > MaxSaveBytes || chunks < 0 || chunks > 4096)
            {
                FailClient("begin-limits");
                return;
            }

            _totalBytes = total;
            _chunkCount = chunks;
            _chunkSize = chunkSize > 0 ? chunkSize : ChunkSize;
            _receivedBytes = 0;
            _buffer = new byte[total];
            _chunkGot = new bool[Math.Max(1, chunks)];
            if (!string.IsNullOrEmpty(key))
                _recvSaveKey = key;
            _transferComplete = false;
            _transferFailed = false;
            _log.Msg("[LoadSync] Begin transfer total=" + total + " chunks=" + chunks);
        }

        private void HandleChunk(byte[] payload)
        {
            if (_buffer == null || _chunkGot == null)
                return;

            int index;
            byte[] data;
            if (!CoopProtocol.TryReadSaveTransferChunk(payload, out index, out data))
                return;
            if (index < 0 || index >= _chunkCount)
                return;
            if (_chunkGot[index])
                return;

            int offset = index * _chunkSize;
            if (offset < 0 || offset + data.Length > _buffer.Length)
            {
                _log.Warning("[LoadSync] Chunk overflow index=" + index);
                return;
            }

            Buffer.BlockCopy(data, 0, _buffer, offset, data.Length);
            _chunkGot[index] = true;
            _receivedBytes += data.Length;
        }

        private void HandleEnd(byte[] payload)
        {
            bool ok;
            string msg;
            if (!CoopProtocol.TryReadSaveTransferEnd(payload, out ok, out msg))
            {
                FailClient("bad-end");
                return;
            }

            if (!ok)
            {
                FailClient(msg);
                return;
            }

            if (_chunkGot != null)
            {
                for (int i = 0; i < _chunkGot.Length; i++)
                {
                    if (!_chunkGot[i])
                    {
                        FailClient("missing-chunk-" + i);
                        return;
                    }
                }
            }

            _receivedBytes = _totalBytes;
            _transferComplete = true;
            _log.Msg("[LoadSync] Transfer end OK (" + _totalBytes + " bytes)");
        }

        private IEnumerator HostServeOneClientCO(CSteamID remote)
        {
            byte[] bytes = null;
            string err = null;
            yield return ReadActiveSaveBytesCO(b => bytes = b, e => err = e);

            if (bytes == null)
            {
                _session.SendTo(remote, CoopMessageType.SaveTransferEnd,
                    CoopProtocol.PackSaveTransferEnd(false, err ?? "read-fail"));
                yield break;
            }

            int chunkCount = (bytes.Length + ChunkSize - 1) / ChunkSize;
            _session.SendTo(remote, CoopMessageType.SaveTransferBegin,
                CoopProtocol.PackSaveTransferBegin(bytes.Length, ChunkSize, chunkCount, CoopRecvSaveKey));
            yield return null;

            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * ChunkSize;
                int len = Math.Min(ChunkSize, bytes.Length - offset);
                byte[] slice = new byte[len];
                Buffer.BlockCopy(bytes, offset, slice, 0, len);
                _session.SendTo(remote, CoopMessageType.SaveTransferChunk,
                    CoopProtocol.PackSaveTransferChunk(i, slice));
                if ((i % 2) == 1)
                    yield return null;
            }

            yield return null;
            _session.SendTo(remote, CoopMessageType.SaveTransferEnd,
                CoopProtocol.PackSaveTransferEnd(true, "ok"));
        }

        private void StopClientRoutine()
        {
            if (_clientRoutine == null)
                return;
            try { MelonCoroutines.Stop(_clientRoutine); }
            catch { }
            _clientRoutine = null;
        }
    }
}
