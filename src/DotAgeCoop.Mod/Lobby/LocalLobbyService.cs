using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MelonLoader;

namespace DotAgeCoop.Lobby
{

    public sealed class LocalLobbyService
    {
        public const int DefaultPort = 27480;

        private readonly MelonLogger.Instance _log;
        private readonly object _gate = new object();
        private readonly Queue<IncomingPacket> _inbox = new Queue<IncomingPacket>();
        private readonly List<PeerConnection> _peers = new List<PeerConnection>();

        private TcpListener _listener;
        private PeerConnection _serverLink;
        private Thread _acceptThread;
        private volatile bool _active;
        private volatile bool _isHost;
        private volatile bool _stopping;
        private string _status = "Local idle";
        private int _nextPeerId = 2;

        public bool IsActive { get { return _active; } }
        public bool IsHost { get { return _isHost; } }
        public string Status { get { return _status; } }

        public int MemberCount
        {
            get
            {
                if (!_active)
                    return 0;
                lock (_gate)
                {

                    if (_isHost)
                        return 1 + _peers.Count;
                    return _serverLink != null && _serverLink.Alive ? 2 : 1;
                }
            }
        }

        public event Action LobbyChanged;

        public LocalLobbyService(MelonLogger.Instance log)
        {
            _log = log;
        }

        public void StartHost()
        {
            StartHost(DefaultPort);
        }

        public void StartHost(int port)
        {
            Leave();
            try
            {
                _stopping = false;
                _isHost = true;
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _active = true;
                _status = "LOCAL HOST on 127.0.0.1:" + port;
                _log.Msg(_status);

                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Start();

                RaiseChanged();
            }
            catch (Exception ex)
            {
                _status = "Local host failed: " + ex.Message;
                _log.Error(_status);
                Leave();
            }
        }

        public void JoinLocalhost()
        {
            Join("127.0.0.1", DefaultPort);
        }

        public void Join(string host, int port)
        {
            Leave();
            try
            {
                _stopping = false;
                _isHost = false;
                TcpClient client = new TcpClient();
                client.NoDelay = true;
                client.Connect(host, port);
                _serverLink = new PeerConnection(client, 1, OnPeerPacket, OnPeerDead);
                _serverLink.Start();
                _active = true;
                _status = "LOCAL CLIENT -> " + host + ":" + port;
                _log.Msg(_status);
                RaiseChanged();
            }
            catch (Exception ex)
            {
                _status = "Local join failed: " + ex.Message;
                _log.Error(_status);
                Leave();
            }
        }

        public void Leave()
        {
            _stopping = true;
            _active = false;

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                    _listener = null;
                }
            }
            catch
            {
            }

            lock (_gate)
            {
                if (_serverLink != null)
                {
                    _serverLink.Close();
                    _serverLink = null;
                }

                for (int i = 0; i < _peers.Count; i++)
                    _peers[i].Close();
                _peers.Clear();
            }

            _isHost = false;
            _status = "Local idle";
            RaiseChanged();
        }

        public void Tick()
        {
            if (!_active)
                return;

            lock (_gate)
            {
                for (int i = _peers.Count - 1; i >= 0; i--)
                {
                    if (!_peers[i].Alive)
                    {
                        _peers[i].Close();
                        _peers.RemoveAt(i);
                        RaiseChanged();
                    }
                }

                if (!_isHost && _serverLink != null && !_serverLink.Alive)
                {
                    _status = "Local disconnected";
                    _active = false;
                    RaiseChanged();
                }
            }
        }

        public bool TryDequeue(out ulong peerId, out byte[] payload)
        {
            lock (_gate)
            {
                if (_inbox.Count == 0)
                {
                    peerId = 0;
                    payload = null;
                    return false;
                }

                IncomingPacket packet = _inbox.Dequeue();
                peerId = packet.PeerId;
                payload = packet.Data;
                return true;
            }
        }

        public bool Broadcast(byte[] data)
        {
            if (!_active || data == null)
                return false;

            lock (_gate)
            {
                if (_isHost)
                {
                    bool ok = true;
                    for (int i = 0; i < _peers.Count; i++)
                    {
                        if (!_peers[i].Send(data))
                            ok = false;
                    }
                    return ok;
                }

                return _serverLink != null && _serverLink.Send(data);
            }
        }

        public bool SendToHost(byte[] data)
        {
            if (!_active || data == null)
                return false;

            if (_isHost)
                return true;

            lock (_gate)
            {
                return _serverLink != null && _serverLink.Send(data);
            }
        }

        public bool SendTo(ulong peerId, byte[] data)
        {
            if (!_active || data == null)
                return false;

            lock (_gate)
            {
                if (_isHost)
                {
                    for (int i = 0; i < _peers.Count; i++)
                    {
                        if (_peers[i].PeerId == peerId)
                            return _peers[i].Send(data);
                    }
                    return false;
                }

                if (peerId == 1)
                    return _serverLink != null && _serverLink.Send(data);
                return false;
            }
        }

        private void AcceptLoop()
        {
            while (!_stopping && _listener != null)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    int id;
                    lock (_gate)
                    {
                        id = _nextPeerId++;
                        PeerConnection peer = new PeerConnection(client, (ulong)id, OnPeerPacket, OnPeerDead);
                        _peers.Add(peer);
                        peer.Start();
                    }

                    _status = "LOCAL HOST — peers: " + (MemberCount - 1);
                    _log.Msg("Local peer connected id=" + id);
                    RaiseChanged();
                }
                catch (SocketException)
                {
                    if (_stopping)
                        break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                        _log.Warning("AcceptLoop: " + ex.Message);
                }
            }
        }

        private void OnPeerPacket(ulong peerId, byte[] data)
        {
            lock (_gate)
            {
                _inbox.Enqueue(new IncomingPacket(peerId, data));
            }
        }

        private void OnPeerDead(ulong peerId)
        {
            _log.Msg("Local peer disconnected id=" + peerId);
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Action handler = LobbyChanged;
            if (handler != null)
                handler();
        }

        private struct IncomingPacket
        {
            public readonly ulong PeerId;
            public readonly byte[] Data;

            public IncomingPacket(ulong peerId, byte[] data)
            {
                PeerId = peerId;
                Data = data;
            }
        }

        private sealed class PeerConnection
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly Action<ulong, byte[]> _onPacket;
            private readonly Action<ulong> _onDead;
            private readonly Thread _readThread;
            private volatile bool _alive = true;

            public ulong PeerId { get; private set; }
            public bool Alive { get { return _alive && _client.Connected; } }

            public PeerConnection(TcpClient client, ulong peerId, Action<ulong, byte[]> onPacket, Action<ulong> onDead)
            {
                _client = client;
                PeerId = peerId;
                _onPacket = onPacket;
                _onDead = onDead;
                _stream = client.GetStream();
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
            }

            public void Start()
            {
                _readThread.Start();
            }

            public bool Send(byte[] data)
            {
                if (!_alive)
                    return false;

                try
                {
                    byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length));
                    lock (_stream)
                    {
                        _stream.Write(header, 0, 4);
                        _stream.Write(data, 0, data.Length);
                        _stream.Flush();
                    }
                    return true;
                }
                catch
                {
                    Die();
                    return false;
                }
            }

            public void Close()
            {
                _alive = false;
                try { _stream.Close(); } catch { }
                try { _client.Close(); } catch { }
            }

            private void ReadLoop()
            {
                byte[] header = new byte[4];
                try
                {
                    while (_alive)
                    {
                        if (!ReadExact(header, 4))
                            break;

                        int len = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
                        if (len <= 0 || len > 1024 * 1024)
                            break;

                        byte[] body = new byte[len];
                        if (!ReadExact(body, len))
                            break;

                        _onPacket(PeerId, body);
                    }
                }
                catch
                {
                }

                Die();
            }

            private bool ReadExact(byte[] buffer, int size)
            {
                int read = 0;
                while (read < size)
                {
                    int n = _stream.Read(buffer, read, size - read);
                    if (n <= 0)
                        return false;
                    read += n;
                }
                return true;
            }

            private void Die()
            {
                if (!_alive)
                    return;
                _alive = false;
                try { _client.Close(); } catch { }
                if (_onDead != null)
                    _onDead(PeerId);
            }
        }
    }
}
