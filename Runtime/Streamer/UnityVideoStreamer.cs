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
        Coroutine _captureCoroutine;
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_encoder is NativeMediaCodecEncoder nativeEncoder && nativeEncoder.TakeIdrRequest())
            {
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

        void CleanupStreaming()
        {
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
                    _capture.RenderFrameToEncoder(_encoder);
                    _encoder.PollEncodedFrames();
                }
            }
        }

        void HandleFrameEncoded(EncodedFrame frame)
        {
            var encodedCount = Interlocked.Increment(ref _encodedFrameLogCount);
            if (encodedCount <= 5 || encodedCount % 60 == 0)
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
