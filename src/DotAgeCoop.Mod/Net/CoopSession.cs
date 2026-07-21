using System;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Lobby;

namespace DotAgeCoop.Net
{
    public sealed class CoopSession
    {
        private readonly SteamLobbyService _steam;
        private readonly LocalLobbyService _local;
        private readonly MelonLogger.Instance _log;
        private readonly List<string> _chatLog = new List<string>();
        private readonly HashSet<ulong> _knownRemotePeers = new HashSet<ulong>();

        public bool Active
        {
            get { return _local.IsActive || _steam.IsInLobby; }
        }

        public bool IsHost
        {
            get { return _local.IsActive ? _local.IsHost : _steam.IsHost; }
        }

        public bool IsLocalMode
        {
            get { return _local.IsActive; }
        }

        public int MemberCount
        {
            get { return _local.IsActive ? _local.MemberCount : _steam.GetMemberCount(); }
        }

        public bool HasCoopPartner
        {
            get { return CoopMemberCount > 1; }
        }

        public int CoopMemberCount
        {
            get
            {
                int lobby = MemberCount;
                int fromPeers = 1 + _knownRemotePeers.Count;
                return Math.Max(lobby, fromPeers);
            }
        }

        public ulong SelfId
        {
            get
            {
                if (_local.IsActive)
                    return _local.IsHost ? 1UL : 2UL;
                try
                {
                    return Steamworks.SteamUser.GetSteamID().m_SteamID;
                }
                catch
                {
                    return 0UL;
                }
            }
        }

        public string ModeLabel
        {
            get
            {
                if (_local.IsActive)
                    return "LOCAL";
                if (_steam.IsInLobby)
                    return "STEAM";
                return "-";
            }
        }

        public IList<string> ChatLog { get { return _chatLog; } }

        public event Action<CSteamID, CoopMessageType, byte[]> MessageReceived;

        public CoopSession(SteamLobbyService steam, LocalLobbyService local, MelonLogger.Instance log)
        {
            _steam = steam;
            _local = local;
            _log = log;
            _steam.PeerJoined += OnSteamPeerJoined;
            _steam.PeerLeft += OnSteamPeerLeft;
            _steam.LobbyChanged += OnSteamLobbyChanged;
            _local.LobbyChanged += OnLocalLobbyChanged;
        }

        private void NoteRemotePeer(ulong steamId)
        {
            if (steamId == 0 || steamId == SelfId)
                return;
            _knownRemotePeers.Add(steamId);
        }

        public void Tick()
        {
            _local.Tick();

            if (_local.IsActive)
            {
                ulong peerId;
                byte[] packet;
                while (_local.TryDequeue(out peerId, out packet))
                    Dispatch(new CSteamID(peerId), packet);
                return;
            }

            if (!_steam.IsReady)
                return;

            CSteamID remote;
            byte[] steamPacket;
            while (_steam.TryReadP2P(out remote, out steamPacket))
                Dispatch(remote, steamPacket);
        }

        public void LeaveAllLobbies(string reason = null)
        {
            bool left = false;
            try
            {
                if (_local.IsActive)
                {
                    _local.Leave();
                    left = true;
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Session] Leave local: " + ex.Message);
            }

            try
            {
                if (_steam.IsInLobby)
                {
                    _steam.LeaveLobby();
                    left = true;
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Session] Leave steam: " + ex.Message);
            }

            _knownRemotePeers.Clear();
            if (left)
            {
                _log.Msg("[Session] Left all lobbies" +
                         (string.IsNullOrEmpty(reason) ? string.Empty : " (" + reason + ")"));
            }
        }

        public void Shutdown()
        {
            LeaveAllLobbies("shutdown");
        }

        public void SendChat(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0 || !Active)
                return;

            byte[] packet = CoopProtocol.Pack(CoopMessageType.Chat, CoopProtocol.StringPayload(text));
            BroadcastRaw(packet);
            AppendChat("me", text);
        }

        public void Broadcast(CoopMessageType type)
        {
            Broadcast(type, null);
        }

        public void Broadcast(CoopMessageType type, byte[] payload)
        {
            if (!Active)
                return;
            BroadcastRaw(CoopProtocol.Pack(type, payload));
        }

        public void SendToHost(CoopMessageType type)
        {
            SendToHost(type, null);
        }

        public void SendToHost(CoopMessageType type, byte[] payload)
        {
            if (!Active)
                return;

            byte[] packet = CoopProtocol.Pack(type, payload);
            if (_local.IsActive)
            {
                _local.SendToHost(packet);
                return;
            }

            CSteamID owner = _steam.GetLobbyOwner();
            if (!owner.IsValid())
                return;
            _steam.SendP2P(owner, packet);
        }

        public void SendTo(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            byte[] packet = CoopProtocol.Pack(type, payload);
            if (_local.IsActive)
            {
                _local.SendTo(remote.m_SteamID, packet);
                return;
            }

            _steam.SendP2P(remote, packet);
        }

        private void BroadcastRaw(byte[] packet)
        {
            if (_local.IsActive)
                _local.Broadcast(packet);
            else
                _steam.BroadcastP2P(packet);
        }

        private void Dispatch(CSteamID remote, byte[] packet)
        {
            CoopMessageType type;
            byte[] payload;
            if (!CoopProtocol.TryUnpack(packet, out type, out payload))
            {
                _log.Warning("Bad packet from " + remote.m_SteamID + " (" + packet.Length + " bytes)");
                return;
            }

            NoteRemotePeer(remote.m_SteamID);
            HandleMessage(remote, type, payload);
            if (MessageReceived != null)
                MessageReceived(remote, type, payload);
        }

        private void HandleMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.Hello:
                    if (IsHost)
                    {
                        SendTo(remote, CoopMessageType.Welcome, CoopProtocol.StringPayload("DotAgeCoop host"));
                        _log.Msg("Hello from " + remote.m_SteamID);
                    }
                    break;

                case CoopMessageType.Welcome:
                    AppendChat("host", CoopProtocol.ReadString(payload));
                    break;

                case CoopMessageType.Chat:
                    AppendChat(PeerLabel(remote), CoopProtocol.ReadString(payload));
                    break;
            }
        }

        private void OnSteamPeerJoined(CSteamID peer)
        {
            NoteRemotePeer(peer.m_SteamID);
            if (_local.IsActive || !IsHost)
                return;
            SendTo(peer, CoopMessageType.Welcome, CoopProtocol.StringPayload("welcome to DotAgeCoop"));
        }

        private void OnSteamPeerLeft(CSteamID peer)
        {
            _knownRemotePeers.Remove(peer.m_SteamID);
        }

        private void OnSteamLobbyChanged()
        {
            if (_local.IsActive)
                return;
            if (!_steam.IsInLobby)
            {
                _knownRemotePeers.Clear();
                return;
            }
            if (!_steam.IsHost)
            {
                SendToHost(CoopMessageType.Hello, CoopProtocol.StringPayload("hello"));
                SendToHost(CoopMessageType.HostGameRequest);
            }
        }

        private void OnLocalLobbyChanged()
        {
            if (!_local.IsActive)
            {
                if (!_steam.IsInLobby)
                    _knownRemotePeers.Clear();
                return;
            }

            if (_local.IsHost)
            {

            }
            else
            {
                SendToHost(CoopMessageType.Hello, CoopProtocol.StringPayload("hello-local"));

                SendToHost(CoopMessageType.HostGameRequest);
            }
        }

        private static string PeerLabel(CSteamID id)
        {
            if (id.m_SteamID <= 16)
                return "peer" + id.m_SteamID;
            return id.m_SteamID.ToString();
        }

        private void AppendChat(string who, string text)
        {
            _chatLog.Add(who + ": " + text);
            if (_chatLog.Count > 40)
                _chatLog.RemoveAt(0);
        }
    }
}
