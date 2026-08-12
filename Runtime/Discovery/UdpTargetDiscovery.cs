using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace VideoStream
{
    public sealed class UdpTargetDiscovery : IDisposable
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
