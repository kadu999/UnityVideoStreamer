using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace VideoStream
{
    /// <summary>
    /// WiFi round-trip latency probe against the gateway.
    ///
    /// Sends FrameProtocol latency-probe packets (header + 12-byte payload
    /// [id, sentNs]) that the gateway's FrameReceiver already echoes back
    /// unconditionally, then measures RTT and emits PIPETRACE `ev=RTT` lines
    /// through <see cref="TraceUploader"/>.
    /// </summary>
    public sealed class RttProbe : IDisposable
    {
        public const ushort FlagLatencyProbe = 0x0200;
        public const int HeaderSize = 18;
        const int ProbePayloadSize = 12;
        const float ProbeInterval = 0.5f; // 2 Hz
        const float MaxPlausibleRttMs = 10000f;

        readonly Func<string> _getAddress;
        readonly Func<int> _getPort;
        readonly object _lock = new object();
        UdpClient _client;
        Thread _receiveThread;
        volatile bool _running;
        int _probeId;
        float _nextProbeTime;
        string _cachedAddress;
        int _cachedPort;
        IPEndPoint _cachedEp;

        public RttProbe(Func<string> getAddress, Func<int> getPort)
        {
            _getAddress = getAddress;
            _getPort = getPort;
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                try
                {
                    _client = new UdpClient(); // auto-binds a local port; gateway echoes to it
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[RttProbe] udp client failed: " + ex.Message);
                    _running = false;
                    return;
                }
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "RttProbe" };
                _receiveThread.Start();
            }
        }

        /// <summary>Drive from MonoBehaviour Update().</summary>
        public void Tick()
        {
            if (!_running) return;
            if (Time.unscaledTime < _nextProbeTime) return;
            _nextProbeTime = Time.unscaledTime + ProbeInterval;
            SendProbe();
        }

        void SendProbe()
        {
            var address = _getAddress();
            var port = _getPort();
            if (string.IsNullOrEmpty(address) || port <= 0) return;

            var id = Interlocked.Increment(ref _probeId);
            var sentNs = NowNs();
            var packet = BuildProbePacket(id, sentNs);
            try
            {
                lock (_lock)
                {
                    if (_client == null) return;
                    if (_cachedEp == null || _cachedAddress != address || _cachedPort != port)
                    {
                        _cachedAddress = address;
                        _cachedPort = port;
                        _cachedEp = new IPEndPoint(IPAddress.Parse(address), port);
                    }
                    _client.Send(packet, packet.Length, _cachedEp);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[RttProbe] send failed: " + ex.Message);
            }
        }

        void ReceiveLoop()
        {
            // 复用接收缓冲：UdpClient.Receive(ref ep) 每次调用都 new 一个 8KB
            // 数组（2Hz 探针 = 持续分配，是 GC 抖动来源之一）。直接用底层
            // Socket.Receive(byte[]) 填现有缓冲（返回实际字节数，不分配）。
            var buffer = new byte[HeaderSize + ProbePayloadSize + 64];
            while (_running)
            {
                try
                {
                    UdpClient client;
                    lock (_lock) { client = _client; }
                    if (client == null) break;

                    var received = client.Client.Receive(buffer);
                    if (received < HeaderSize + ProbePayloadSize) continue;

                    var id = ReadInt32(buffer, HeaderSize);
                    var sentNs = ReadInt64(buffer, HeaderSize + 4);
                    var rttMs = (NowNs() - sentNs) / 1e6f;
                    if (rttMs >= 0f && rttMs < MaxPlausibleRttMs)
                    {
                        TraceUploader.Log($"ev=RTT id={id} rtt_ms={rttMs:F1}");
                    }
                }
                catch (Exception ex)
                {
                    if (_running) UnityEngine.Debug.LogWarning("[RttProbe] receive error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Builds a UdpFramer single-fragment datagram:
        /// [flags:u16][index:u16][count:u16][sequence:u32][FrameProtocol packet]
        /// The gateway reassembler strips the 10-byte fragment header and echoes the
        /// inner FrameProtocol packet back, so the receiver parses payload at HeaderSize.
        /// </summary>
        static byte[] BuildProbePacket(int id, long sentNs)
        {
            var fp = new byte[HeaderSize + ProbePayloadSize];
            WriteInt32(fp, 12, ProbePayloadSize); // naluSize
            fp[16] = (byte)(FlagLatencyProbe >> 8);
            fp[17] = (byte)(FlagLatencyProbe & 0xFF);
            WriteInt32(fp, HeaderSize, id);
            WriteInt64(fp, HeaderSize + 4, sentNs);

            var datagram = new byte[10 + fp.Length];
            WriteInt16(datagram, 4, 1);  // fragment count = 1 (single fragment)
            WriteInt32(datagram, 6, id); // sequence id
            Array.Copy(fp, 0, datagram, 10, fp.Length);
            return datagram;
        }

        static long NowNs()
        {
            return (long)((double)Stopwatch.GetTimestamp() * 1_000_000_000.0 / Stopwatch.Frequency);
        }

        static void WriteInt32(byte[] buf, int off, int value)
        {
            buf[off] = (byte)(value >> 24);
            buf[off + 1] = (byte)(value >> 16);
            buf[off + 2] = (byte)(value >> 8);
            buf[off + 3] = (byte)value;
        }

        static void WriteInt16(byte[] buf, int off, int value)
        {
            buf[off] = (byte)(value >> 8);
            buf[off + 1] = (byte)(value & 0xFF);
        }

        static void WriteInt64(byte[] buf, int off, long value)
        {
            WriteInt32(buf, off, (int)(value >> 32));
            WriteInt32(buf, off + 4, (int)value);
        }

        static int ReadInt32(byte[] buf, int off)
        {
            return (buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3];
        }

        static long ReadInt64(byte[] buf, int off)
        {
            return ((long)ReadInt32(buf, off) << 32) | (uint)ReadInt32(buf, off + 4);
        }

        public void Stop()
        {
            lock (_lock)
            {
                _running = false;
                if (_client != null)
                {
                    try { _client.Close(); } catch (Exception) { }
                    _client = null;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
