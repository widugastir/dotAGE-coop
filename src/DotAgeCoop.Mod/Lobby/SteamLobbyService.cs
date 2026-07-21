using System;
using System.IO;
using System.Text;
using MelonLoader;
using Steamworks;
using UnityEngine;

namespace DotAgeCoop.Lobby
{

    public sealed class SteamLobbyService
    {
        public const uint DefaultAppId = 480;

        private static readonly byte[] EmptyBytes = new byte[0];

        private readonly MelonLogger.Instance _log;
        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<LobbyEnter_t> _lobbyEnter;
        private Callback<LobbyChatUpdate_t> _lobbyChatUpdate;
        private Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;
        private Callback<P2PSessionRequest_t> _p2pRequest;

        private bool _steamReady;
        private bool _shuttingDown;
        private bool _isHost;
        private CSteamID _lobbyId = CSteamID.Nil;
        private string _status = "Steam not ready";

        public bool IsReady { get { return _steamReady; } }
        public bool IsInLobby { get { return _lobbyId.IsValid(); } }
        public bool IsHost { get { return _isHost; } }
        public string LobbyCode
        {
            get { return _lobbyId.IsValid() ? _lobbyId.m_SteamID.ToString() : string.Empty; }
        }
        public string Status { get { return _status; } }
        public uint AppId { get; private set; }

        public event Action LobbyChanged;
        public event Action<CSteamID> PeerJoined;
        public event Action<CSteamID> PeerLeft;

        public SteamLobbyService(MelonLogger.Instance log)
        {
            _log = log;
            AppId = DefaultAppId;
        }

        public bool TryInitialize()
        {
            try
            {
                AppId = ReadConfiguredAppId();
                EnsureSteamAppIdFile(AppId);

                if (SteamAPI.Init())
                {
                    _steamReady = true;
                    RegisterCallbacks();
                    _status = "Steam ready (AppID " + AppId + ") — F8 lobby";
                    _log.Msg(_status);
                    return true;
                }

                if (SteamAPI.IsSteamRunning())
                {
                    _steamReady = true;
                    RegisterCallbacks();
                    _status = "Steam already running (AppID " + AppId + ") — F8 lobby";
                    _log.Msg(_status);
                    return true;
                }

                _status = "SteamAPI.Init failed — is Steam running?";
                _log.Warning(_status);
                return false;
            }
            catch (Exception ex)
            {
                _status = "Steam init exception: " + ex.Message;
                _log.Error(_status);
                _log.Error(ex.ToString());
                return false;
            }
        }

        public void Tick()
        {
            if (!_steamReady || _shuttingDown)
                return;

            try
            {
                SteamAPI.RunCallbacks();
            }
            catch (Exception ex)
            {
                _log.Warning("Steam callbacks: " + ex.Message);
            }
        }

        public void Shutdown()
        {

            _shuttingDown = true;
            try
            {
                if (_lobbyId.IsValid())
                {
                    SteamMatchmaking.LeaveLobby(_lobbyId);
                    _lobbyId = CSteamID.Nil;
                }
            }
            catch
            {
            }

            _isHost = false;
            _steamReady = false;
        }

        public void CreateLobby(int maxMembers)
        {
            if (!_steamReady)
            {
                _status = "Steam not ready";
                return;
            }

            _status = "Creating lobby...";
            _isHost = true;

            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeInvisible, maxMembers);
        }

        public void CreateLobby()
        {
            CreateLobby(4);
        }

        public void JoinLobby(string code)
        {
            if (!_steamReady)
            {
                _status = "Steam not ready";
                return;
            }

            if (code == null)
                code = string.Empty;
            code = code.Trim();

            ulong id;
            if (!ulong.TryParse(code, out id) || id == 0)
            {
                _status = "Invalid lobby code";
                return;
            }

            _isHost = false;
            _status = "Joining " + code + "...";
            SteamMatchmaking.JoinLobby(new CSteamID(id));
        }

        public void LeaveLobby()
        {
            if (_lobbyId.IsValid())
            {
                SteamMatchmaking.LeaveLobby(_lobbyId);
                _log.Msg("Left lobby " + LobbyCode);
            }

            _lobbyId = CSteamID.Nil;
            _isHost = false;
            _status = "Left lobby";
            if (LobbyChanged != null)
                LobbyChanged();
        }

        public void CopyLobbyCodeToClipboard()
        {
            if (!_lobbyId.IsValid())
                return;

            GUIUtility.systemCopyBuffer = LobbyCode;
            _status = "Lobby code copied";
            _log.Msg("Copied lobby code: " + LobbyCode);
        }

        public int GetMemberCount()
        {
            if (!_lobbyId.IsValid())
                return 0;
            return SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
        }

        public CSteamID GetMember(int index)
        {
            return SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, index);
        }

        public CSteamID GetLobbyOwner()
        {
            if (!_lobbyId.IsValid())
                return CSteamID.Nil;
            return SteamMatchmaking.GetLobbyOwner(_lobbyId);
        }

        public bool SendP2P(CSteamID remote, byte[] data)
        {
            if (!_steamReady || data == null || data.Length == 0)
                return false;

            return SteamNetworking.SendP2PPacket(remote, data, (uint)data.Length, EP2PSend.k_EP2PSendReliable);
        }

        public bool BroadcastP2P(byte[] data)
        {
            return BroadcastP2P(data, false);
        }

        public bool BroadcastP2P(byte[] data, bool includeSelf)
        {
            if (!_lobbyId.IsValid())
                return false;

            CSteamID self = SteamUser.GetSteamID();
            int n = GetMemberCount();
            bool ok = true;
            for (int i = 0; i < n; i++)
            {
                CSteamID member = GetMember(i);
                if (!includeSelf && member == self)
                    continue;
                if (!SendP2P(member, data))
                    ok = false;
            }

            return ok;
        }

        public bool TryReadP2P(out CSteamID remote, out byte[] payload)
        {
            remote = CSteamID.Nil;
            payload = EmptyBytes;

            if (!_steamReady)
                return false;

            uint size;
            if (!SteamNetworking.IsP2PPacketAvailable(out size) || size == 0)
                return false;

            byte[] buffer = new byte[size];
            uint msgSize;
            if (!SteamNetworking.ReadP2PPacket(buffer, size, out msgSize, out remote) || msgSize == 0)
                return false;

            if (msgSize != size)
            {
                payload = new byte[msgSize];
                Buffer.BlockCopy(buffer, 0, payload, 0, (int)msgSize);
            }
            else
            {
                payload = buffer;
            }

            return true;
        }

        private void RegisterCallbacks()
        {
            _lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _p2pRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
        }

        private void OnLobbyCreated(LobbyCreated_t ev)
        {
            if (ev.m_eResult != EResult.k_EResultOK)
            {
                _status = "Create lobby failed: " + ev.m_eResult;
                _isHost = false;
                _log.Warning(_status);
                if (LobbyChanged != null)
                    LobbyChanged();
                return;
            }

            _lobbyId = new CSteamID(ev.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(_lobbyId, "name", "DotAgeCoop");
            SteamMatchmaking.SetLobbyData(_lobbyId, "ver", "0.1.1");
            _status = "Host lobby " + LobbyCode;
            _log.Msg(_status);
            if (LobbyChanged != null)
                LobbyChanged();
        }

        private void OnLobbyEnter(LobbyEnter_t ev)
        {
            if (ev.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                _status = "Join failed: " + ev.m_EChatRoomEnterResponse;
                _log.Warning(_status);
                if (LobbyChanged != null)
                    LobbyChanged();
                return;
            }

            _lobbyId = new CSteamID(ev.m_ulSteamIDLobby);
            CSteamID owner = GetLobbyOwner();
            _isHost = owner == SteamUser.GetSteamID();
            _status = (_isHost ? "Host" : "Client") + " in lobby " + LobbyCode;
            _log.Msg(_status + " (" + GetMemberCount() + " members)");
            if (LobbyChanged != null)
                LobbyChanged();
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t ev)
        {
            CSteamID user = new CSteamID(ev.m_ulSteamIDUserChanged);
            EChatMemberStateChange state = (EChatMemberStateChange)ev.m_rgfChatMemberStateChange;

            if ((state & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                _log.Msg("Peer joined: " + user.m_SteamID);
                if (PeerJoined != null)
                    PeerJoined(user);
            }
            else if ((state & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                     (state & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0 ||
                     (state & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0 ||
                     (state & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0)
            {
                _log.Msg("Peer left: " + user.m_SteamID);
                if (PeerLeft != null)
                    PeerLeft(user);
            }

            if (LobbyChanged != null)
                LobbyChanged();
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t ev)
        {
            _isHost = false;
            SteamMatchmaking.JoinLobby(ev.m_steamIDLobby);
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t ev)
        {
            SteamNetworking.AcceptP2PSessionWithUser(ev.m_steamIDRemote);
        }

        private static uint ReadConfiguredAppId()
        {
            try
            {
                string path = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DotAgeCoop");
                path = Path.Combine(path, "steam_appid.txt");
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path).Trim();
                    uint id;
                    if (uint.TryParse(text, out id) && id != 0)
                        return id;
                }
            }
            catch
            {
            }

            return DefaultAppId;
        }

        private void EnsureSteamAppIdFile(uint appId)
        {
            try
            {
                string userDir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "DotAgeCoop");
                if (!Directory.Exists(userDir))
                    Directory.CreateDirectory(userDir);

                string userFile = Path.Combine(userDir, "steam_appid.txt");
                if (!File.Exists(userFile))
                    File.WriteAllText(userFile, appId.ToString(), Encoding.ASCII);

                string gameRoot = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
                string rootFile = Path.Combine(gameRoot, "steam_appid.txt");
                if (!File.Exists(rootFile))
                    File.WriteAllText(rootFile, appId.ToString(), Encoding.ASCII);
            }
            catch (Exception ex)
            {
                _log.Warning("Could not write steam_appid.txt: " + ex.Message);
            }
        }
    }
}
