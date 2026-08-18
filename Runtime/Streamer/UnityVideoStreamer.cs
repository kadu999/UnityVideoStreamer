using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace VideoStream
{
    [DisallowMultipleComponent]
    public sealed class UnityVideoStreamer : MonoBehaviour
    {
        [Header("Capture")]
        [SerializeField] Camera captureCamera;
        [SerializeField] int width = 1280;
        [SerializeField] int height = 720;
        [SerializeField] int frameRate = 30;
        [SerializeField] bool flipY = true;

        [Header("Encoding")]
        [SerializeField] bool useHevc = false;
        [SerializeField] int bitrate = 8_000_000;
        [SerializeField] int keyFrameIntervalSeconds = 2;

        [Header("UDP")]
        [SerializeField] string targetAddress = "192.168.43.129";
        [SerializeField] int targetPort = 9999;
        [SerializeField] int localPort = 9998;
        [SerializeField] bool autoStart = true;
        [SerializeField] bool connectFirstTarget = false;

        readonly object _stateLock = new object();
        readonly ConcurrentQueue<IPEndPoint> _pendingTargets = new ConcurrentQueue<IPEndPoint>();
        IUnityVideoEncoder _encoder;
        UdpTargetDiscovery _discovery;
        UnityFrameCapture _capture;
        RttProbe _rttProbe;
        Coroutine _captureCoroutine;
        Thread _gcMonitorThread;
        volatile bool _gcMonitorRunning;
        int _captureTick;
        volatile bool _streaming;
        long _encodedFrameLogCount;
        float _nextCaptureTime;
        bool _connected;
        public event Action<string> OnError;
        public event Action<bool> SearchingChanged;
        public event Action<IPEndPoint> TargetDiscovered;
        public event Action<IPEndPoint> Connected;

        public bool IsStreaming => _streaming;
        public string MimeType => useHevc ? "video/hevc" : "video/avc";
        public RenderTexture PreviewTexture => _capture != null ? _capture.TargetTexture : null;
        public Camera CaptureCamera
        {
            get => captureCamera;
            set => captureCamera = value;
        }
        public string TargetAddress
        {
            get => targetAddress;
            set => targetAddress = value;
        }
        public int TargetPort
        {
            get => targetPort;
            set => targetPort = value;
        }
        public bool AutoStart
        {
            get => autoStart;
            set => autoStart = value;
        }
        public bool UseHevc
        {
            get => useHevc;
            set => useHevc = value;
        }
        public bool ConnectFirstTarget
        {
            get => connectFirstTarget;
            set => connectFirstTarget = value;
        }
        public bool IsSearching => _discovery != null;

        void OnEnable()
        {
            if (autoStart) StartStreaming();
        }

        void OnDisable()
        {
            StopStreaming();
        }

        void OnDestroy()
        {
            StopStreaming();
        }

        void Update()
        {
            TraceUploader.Tick();
            _rttProbe?.Tick();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_encoder is NativeMediaCodecEncoder nativeEncoder && nativeEncoder.TakeIdrRequest())
            {
                TraceUploader.Log("ev=IDR_REQ_RECV");
                nativeEncoder.RequestKeyFrame();
            }
#endif

            while (_pendingTargets.TryDequeue(out var endpoint))
            {
                TargetDiscovered?.Invoke(endpoint);
                if (connectFirstTarget && !_connected)
                {
                    ConnectTo(endpoint);
                    break;
                }

                _encoder?.RequestKeyFrame();
            }
        }

        public bool StartSearching()
        {
            StopStreaming();
            StopSearching();
            ClearPendingTargets();
            _connected = false;

            _discovery = new UdpTargetDiscovery();
            _discovery.TargetDiscovered += HandleTargetDiscovered;
            if (!_discovery.Start())
            {
                const string message = "Discovery bind failed on UDP 9997";
                RaiseError(message);
                _discovery.TargetDiscovered -= HandleTargetDiscovered;
                _discovery.Dispose();
                _discovery = null;
                return false;
            }

            SearchingChanged?.Invoke(true);
            return true;
        }

        public void StopSearching()
        {
            if (_discovery == null) return;

            _discovery.TargetDiscovered -= HandleTargetDiscovered;
            _discovery.Dispose();
            _discovery = null;
            SearchingChanged?.Invoke(false);
        }

        public void ConnectTo(IPEndPoint endpoint)
        {
            if (endpoint == null) return;

            StopSearching();
            ClearPendingTargets();
            _connected = true;

            targetAddress = endpoint.Address.ToString();
            targetPort = endpoint.Port;
            StartStreaming();
            if (_streaming)
            {
                Connected?.Invoke(endpoint);
            }
            else
            {
                _connected = false;
            }
        }

        public void StartStreaming()
        {
            lock (_stateLock)
            {
                if (_streaming) return;
                if (Application.isEditor)
                {
                    Debug.Log("[VideoStream] Streaming is disabled in the Unity Editor; build to Android to run the encoder.");
                    return;
                }

                if (captureCamera == null)
                {
                    captureCamera = Camera.main;
                }

                if (captureCamera == null)
                {
                    RaiseError("No capture camera assigned and Camera.main is missing");
                    return;
                }

                width = Mathf.Max(2, width & ~1);
                height = Mathf.Max(2, height & ~1);

                var config = new VideoStreamConfig
                {
                    TargetAddress = targetAddress,
                    TargetPort = targetPort,
                    LocalPort = localPort,
                    Width = width,
                    Height = height,
                    FrameRate = frameRate,
                    Bitrate = bitrate,
                    KeyFrameIntervalSeconds = keyFrameIntervalSeconds,
                    UseHevc = useHevc,
                    FlipY = flipY
                };

                _encoder = PlatformEncoder.Create(config);
                if (_encoder == null)
                {
                    RaiseError("No encoder available; Android build required");
                    return;
                }

                _encoder.FrameEncoded += HandleFrameEncoded;
                _encoder.Error += HandleEncoderError;
                if (!_encoder.Start(config))
                {
                    CleanupStreaming();
                    return;
                }

                try
                {
                    _capture = new UnityFrameCapture(captureCamera, width, height, flipY);
                }
                catch (Exception ex)
                {
                    RaiseError("Capture setup failed: " + ex.Message);
                    CleanupStreaming();
                    return;
                }

                if (GraphicsSettings.currentRenderPipeline != null)
                {
                    Debug.Log("[VideoStream] URP capture uses the VideoStream Camera Capture renderer feature (GPU copy).");
                }

                _streaming = true;
                _nextCaptureTime = 0f;
                _captureCoroutine = StartCoroutine(CaptureLoop());
                _encoder.RequestKeyFrame();
                StartGcMonitor();

                // Test-session id: unique per connection, shared with the gateway so
                // both devices' logs land in one runs/<sessionId>/ folder.
                TraceUploader.SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + UnityEngine.Random.Range(1000, 9999);
                SendSessionId();

                // WiFi round-trip latency probe against the gateway (PIPETRACE ev=RTT).
                _rttProbe = new RttProbe(() => targetAddress, () => targetPort);
                _rttProbe.Start();

                Debug.Log($"[VideoStream] Streaming {width}x{height} {frameRate}fps static target");
            }
        }

        public void StopStreaming()
        {
            lock (_stateLock)
            {
                if (!_streaming && _encoder == null && _capture == null && _discovery == null) return;
                _streaming = false;

                if (_captureCoroutine != null)
                {
                    StopCoroutine(_captureCoroutine);
                    _captureCoroutine = null;
                }

                CleanupStreaming();
            }
        }

        /// <summary>
        /// GC pause monitor (diagnostic): a managed background thread sleeps in a
        /// tight loop; whenever the GC stops the world (all managed threads), this
        /// thread's own loop gap grows. Gaps &gt; 3ms are logged as ev=GC_PAUSE so a
        /// run can directly show how big the GC stalls actually are.
        /// </summary>
        void StartGcMonitor()
        {
            if (_gcMonitorRunning) return;
            _gcMonitorRunning = true;
            _gcMonitorThread = new Thread(() =>
            {
                long last = System.Diagnostics.Stopwatch.GetTimestamp();
                while (_gcMonitorRunning)
                {
                    Thread.Sleep(1);
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    long gapUs = (now - last) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
                    last = now;
                    if (gapUs > 3000)
                    {
                        TraceUploader.Log($"ev=GC_PAUSE ms={gapUs / 1000.0:F1}");
                    }
                }
            })
            {
                IsBackground = true,
                Name = "GcPauseMonitor"
            };
            _gcMonitorThread.Start();
        }

        void StopGcMonitor()
        {
            _gcMonitorRunning = false;
            if (_gcMonitorThread != null)
            {
                _gcMonitorThread.Join(500);
                _gcMonitorThread = null;
            }
        }

        void CleanupStreaming()
        {
            TraceUploader.FlushNow();
            StopGcMonitor();
            if (_rttProbe != null)
            {
                _rttProbe.Stop();
                _rttProbe = null;
            }

            if (_capture != null)
            {
                _capture.Dispose();
                _capture = null;
            }

            if (_encoder != null)
            {
                _encoder.FrameEncoded -= HandleFrameEncoded;
                _encoder.Error -= HandleEncoderError;
                _encoder.Dispose();
                _encoder = null;
            }

            if (_discovery != null)
            {
                _discovery.TargetDiscovered -= HandleTargetDiscovered;
                _discovery.Dispose();
                _discovery = null;
                SearchingChanged?.Invoke(false);
            }

        }

        IEnumerator CaptureLoop()
        {
            while (_streaming)
            {
                yield return new WaitForEndOfFrame();
                if (Time.unscaledTime < _nextCaptureTime) continue;
                _nextCaptureTime = Time.unscaledTime + (1f / Mathf.Max(1, frameRate));

                if (_streaming && _capture != null)
                {
                    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _capture.RenderFrameToEncoder(_encoder);
                    var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _encoder.PollEncodedFrames();
                    var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (TraceUploader.ShouldTraceFrame(_captureTick))
                    {
                        TraceUploader.TraceFrame(
                            $"ev=CAPTURE render_ms={ToMs(t1 - t0):F1} poll_ms={ToMs(t2 - t1):F1}",
                            _captureTick);
                    }
                    _captureTick++;
                }
            }
        }

        static float ToMs(long ticks)
        {
            return (float)(ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>
        /// Announce the session id to the gateway via a FLAG_SESSION (0x1000)
        /// FrameProtocol packet so gateway logs share the same runs/&lt;sessionId&gt;/ folder.
        /// </summary>
        void SendSessionId()
        {
            const ushort FlagSession = 0x1000;
            const int HeaderSize = 18;
            var id = TraceUploader.SessionId;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(targetAddress) || targetPort <= 0)
            {
                return;
            }
            var payload = System.Text.Encoding.UTF8.GetBytes(id);
            var packet = new byte[HeaderSize + payload.Length];
            // frameId=0, ptsUs=0, naluSize=payload.Length, flags=0x1000 (big-endian)
            WriteInt32(packet, 12, payload.Length);
            packet[16] = (byte)(FlagSession >> 8);
            packet[17] = (byte)(FlagSession & 0xFF);
            System.Array.Copy(payload, 0, packet, HeaderSize, payload.Length);
            try
            {
                using (var udp = new System.Net.Sockets.UdpClient())
                {
                    udp.Connect(targetAddress, targetPort);
                    udp.Send(packet, packet.Length);
                }
                Debug.Log("[VideoStream] Session id sent: " + id);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[VideoStream] Session id send failed: " + ex.Message);
            }
        }

        static void WriteInt32(byte[] buf, int off, int value)
        {
            buf[off] = (byte)(value >> 24);
            buf[off + 1] = (byte)(value >> 16);
            buf[off + 2] = (byte)(value >> 8);
            buf[off + 3] = (byte)value;
        }

        void HandleFrameEncoded(EncodedFrame frame)
        {
            var encodedCount = Interlocked.Increment(ref _encodedFrameLogCount);
            // Log cadence 1s->10s (60->600 frames): main-thread Debug.Log + logcat
            // writes were contributing to periodic 60->55fps dips.
            if (encodedCount <= 5 || encodedCount % 600 == 0)
            {
                Debug.Log("[VideoStream] Encoded frame count=" + encodedCount +
                          " key=" + frame.IsKeyFrame +
                          " bytes=" + frame.Data.Length);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (_encoder is NativeMediaCodecEncoder nativeEncoder)
            {
                nativeEncoder.SendFrame(frame);
                return;
            }
#endif
        }

        void HandleTargetDiscovered(IPEndPoint endpoint)
        {
            _pendingTargets.Enqueue(endpoint);
        }

        void ClearPendingTargets()
        {
            while (_pendingTargets.TryDequeue(out _))
            {
            }
        }

        void HandleEncoderError(string message)
        {
            RaiseError(message);
        }

        void RaiseError(string message)
        {
            Debug.LogError("[VideoStream] " + message);
            OnError?.Invoke(message);
        }

        void Reset()
        {
            width = 1280;
            height = 720;
            frameRate = 30;
            bitrate = 8_000_000;
            keyFrameIntervalSeconds = 2;
            targetAddress = "192.168.43.129";
            targetPort = 9999;
            localPort = 9998;
            useHevc = false;
            flipY = true;
        }
    }
}
