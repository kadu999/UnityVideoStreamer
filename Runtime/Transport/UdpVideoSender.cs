using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace VideoStream
{
    internal sealed class UdpVideoSender : IDisposable
    {
        readonly object _lock = new object();
        readonly List<IPEndPoint> _targets = new List<IPEndPoint>();
        readonly ConcurrentQueue<EncodedFrame> _frames = new ConcurrentQueue<EncodedFrame>();
        readonly FramePacketizer _packetizer = new FramePacketizer();

        Socket _socket;
        Thread _sendThread;
        Thread _receiveThread;
        volatile bool _running;
        volatile bool _disposed;
        int _sequence;
        int _localPort;
        long _sentDatagrams;
        int _lastFrameId = -1;
        readonly byte[] _receiveBuffer = new byte[4096];
        EndPoint _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        public int LocalPort => _localPort;
        public event Action OnIdrRequested;
        public event Action<string> OnError;

        public bool Start(int localPort)
        {
            lock (_lock)
            {
                if (_running) return true;

                try
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
                    _socket.ReceiveBufferSize = 1024 * 1024;
                    _socket.SendBufferSize = 1024 * 1024;
                    _localPort = ((IPEndPoint)_socket.LocalEndPoint).Port;
                    _running = true;
                }
                catch (Exception ex)
                {
                    _socket?.Close();
                    _socket = null;
                    RaiseError("UDP bind failed: " + ex.Message);
                    return false;
                }
            }

            _sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "UdpVideoSend"
            };
            _sendThread.Start();

            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "UdpVideoReceive"
            };
            _receiveThread.Start();
            return true;
        }

        public void Stop()
        {
            Socket socket;
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                socket = _socket;
            }

            _sendThread?.Join(500);
            try { socket?.Close(); } catch { }
            _receiveThread?.Join(500);
            lock (_lock)
            {
                _socket = null;
            }
            _sendThread = null;
            _receiveThread = null;
        }

        public void AddTarget(IPEndPoint endpoint)
        {
            if (endpoint == null) return;

            lock (_lock)
            {
                if (_targets.Exists(t => t.Equals(endpoint))) return;
                _targets.Add(endpoint);
            }
        }

        public void RemoveTarget(IPEndPoint endpoint)
        {
            lock (_lock)
            {
                _targets.RemoveAll(t => t.Equals(endpoint));
            }
        }

        public void ClearTargets()
        {
            lock (_lock) _targets.Clear();
        }

        public void SendFrame(in EncodedFrame frame)
        {
            if (frame.Data == null || frame.Data.Length == 0) return;
            _frames.Enqueue(frame);
        }

        void SendLoop()
        {
            while (_running || !_frames.IsEmpty)
            {
                if (!_frames.TryDequeue(out var frame))
                {
                    if (!_running) break;
                    Thread.Sleep(1);
                    continue;
                }

                var packet = _packetizer.Pack(frame);
                var frameId = FrameProtocol.ParseHeader(packet, 0, packet.Length).FrameId;
                if (_lastFrameId >= 0 && frameId != _lastFrameId + 1)
                {
                    UnityEngine.Debug.LogWarning(
                        "[VideoStream] Frame gap detected send id=" + _lastFrameId +
                        " -> " + frameId);
                }
                _lastFrameId = frameId;

                var sequence = Interlocked.Increment(ref _sequence);
                var fragments = UdpFramer.Fragment(packet, sequence, frame.IsKeyFrame || frame.IsConfig);
                foreach (var datagram in fragments)
                {
                    SendDatagrams(datagram);
                }
            }
        }

        void SendDatagrams(byte[] datagram)
        {
            IPEndPoint[] targets;
            lock (_lock)
            {
                targets = _targets.ToArray();
            }

            if (targets.Length == 0) return;

            foreach (var target in targets)
            {
                try
                {
                    _socket?.SendTo(datagram, datagram.Length, SocketFlags.None, target);
                    var sent = Interlocked.Increment(ref _sentDatagrams);
                    if (sent <= 5 || sent % 60 == 0)
                    {
                        UnityEngine.Debug.Log("[VideoStream] UDP datagrams sent=" + sent);
                    }
                }
                catch (Exception ex)
                {
                    RaiseError("UDP send failed: " + ex.Message);
                }
            }
        }

        void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var length = _socket.ReceiveFrom(_receiveBuffer, ref _remoteEndPoint);
                    if (length < FrameProtocol.HeaderSize) continue;

                    var header = FrameProtocol.ParseHeader(_receiveBuffer, 0, length);
                    if (header.IsIdrRequest)
                    {
                        OnIdrRequested?.Invoke();
                    }
                    else if (header.IsPing || header.IsLatencyProbe)
                    {
                        _socket.SendTo(_receiveBuffer, length, SocketFlags.None, _remoteEndPoint);
                    }
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    if (_running) RaiseError("UDP receive failed: " + ex.Message);
                }
            }
        }

        void RaiseError(string message)
        {
            try { OnError?.Invoke(message); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    internal sealed class UdpTargetDiscovery : IDisposable
    {
        public const int DiscoveryPort = 9997;

        readonly object _lock = new object();
        readonly Dictionary<string, IPEndPoint> _targets =
            new Dictionary<string, IPEndPoint>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _localIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Socket _socket;
        Thread _thread;
        volatile bool _running;

        public event Action<IPEndPoint> TargetDiscovered;

        public IPEndPoint[] Targets
        {
            get
            {
                lock (_lock)
                {
                    var result = new IPEndPoint[_targets.Count];
                    _targets.Values.CopyTo(result, 0);
                    return result;
                }
            }
        }

        public bool Start()
        {
            lock (_lock)
            {
                if (_running) return true;

                try
                {
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    _socket.ReceiveBufferSize = 64 * 1024;
                    _running = true;
                }
                catch
                {
                    _socket?.Close();
                    _socket = null;
                    return false;
                }
            }

            RefreshLocalIps();
            _thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "UdpTargetDiscovery"
            };
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            Socket socket;
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                socket = _socket;
                _socket = null;
            }

            try { socket?.Close(); } catch { }
            _thread?.Join(500);
            _thread = null;

            lock (_lock)
            {
                _targets.Clear();
            }
        }

        void ReceiveLoop()
        {
            var buffer = new byte[512];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                try
                {
                    var length = _socket.ReceiveFrom(buffer, ref remote);
                    HandlePacket(buffer, length, (IPEndPoint)remote);
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    if (_running) UnityEngine.Debug.LogWarning("[VideoStream] Discovery receive failed: " + ex.Message);
                }
            }
        }

        void HandlePacket(byte[] data, int length, IPEndPoint remote)
        {
            if (length < FrameProtocol.HeaderSize) return;

            FrameHeader header;
            try
            {
                header = FrameProtocol.ParseHeader(data, 0, length);
            }
            catch
            {
                return;
            }

            if (!header.IsRegister || header.NaluSize < 2) return;
            var payloadEnd = FrameProtocol.HeaderSize + header.NaluSize;
            if (length < payloadEnd) return;

            var port = (data[FrameProtocol.HeaderSize] << 8) |
                       data[FrameProtocol.HeaderSize + 1];
            var ip = remote.Address;
            if (IsLocalIp(ip)) return;

            var endpoint = new IPEndPoint(ip, port);
            var key = endpoint.ToString();
            bool isNew;
            lock (_lock)
            {
                isNew = !_targets.ContainsKey(key);
                _targets[key] = endpoint;
            }

            if (isNew)
            {
                UnityEngine.Debug.Log("[VideoStream] Discovered target " + endpoint);
                TargetDiscovered?.Invoke(endpoint);
            }
        }

        bool IsLocalIp(IPAddress address)
        {
            return _localIps.Contains(address.ToString());
        }

        void RefreshLocalIps()
        {
            _localIps.Clear();
            try
            {
                var hostName = Dns.GetHostName();
                foreach (var address in Dns.GetHostAddresses(hostName))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        _localIps.Add(address.ToString());
                    }
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
