using System;
using System.Collections;
using System.Net;
using UnityEngine;

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
        [SerializeField] bool useHevc = true;
        [SerializeField] int bitrate = 8_000_000;
        [SerializeField] int keyFrameIntervalSeconds = 2;
        [SerializeField] int maxQueuedFrames = 3;

        [Header("UDP")]
        [SerializeField] string targetAddress = "192.168.43.129";
        [SerializeField] int targetPort = 9999;
        [SerializeField] int localPort = 9998;
        [SerializeField] bool autoStart = true;

        readonly object _stateLock = new object();
        IUnityVideoEncoder _encoder;
        UdpVideoSender _sender;
        UnityFrameCapture _capture;
        FramePacketizer _packetizer;
        Coroutine _captureCoroutine;
        volatile bool _streaming;

        public event Action<string> OnError;

        public bool IsStreaming => _streaming;
        public string MimeType => useHevc ? "video/hevc" : "video/avc";

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

        public void StartStreaming()
        {
            lock (_stateLock)
            {
                if (_streaming) return;

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
                    MaxQueuedFrames = maxQueuedFrames,
                    UseHevc = useHevc,
                    FlipY = flipY
                };

                _encoder = PlatformEncoder.Create(config);
                if (_encoder == null)
                {
                    RaiseError("No encoder available; Android build required");
                    return;
                }

                _sender = new UdpVideoSender();
                _sender.OnIdrRequested += HandleIdrRequested;
                _sender.OnError += HandleSenderError;
                if (!_sender.Start(config.LocalPort))
                {
                    CleanupStreaming();
                    return;
                }

                if (!TryAddTarget(config.TargetAddress, config.TargetPort))
                {
                    CleanupStreaming();
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

                _capture.FrameReady += HandleFrameReady;
                _packetizer = new FramePacketizer();
                _streaming = true;
                _captureCoroutine = StartCoroutine(CaptureLoop());
                _encoder.RequestKeyFrame();

                Debug.Log($"[VideoStream] Streaming {width}x{height} {frameRate}fps to {config.TargetAddress}:{config.TargetPort}");
            }
        }

        public void StopStreaming()
        {
            lock (_stateLock)
            {
                if (!_streaming && _encoder == null && _sender == null && _capture == null) return;
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
                _capture.FrameReady -= HandleFrameReady;
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

            if (_sender != null)
            {
                _sender.OnIdrRequested -= HandleIdrRequested;
                _sender.OnError -= HandleSenderError;
                _sender.Dispose();
                _sender = null;
            }

            _packetizer = null;
        }

        IEnumerator CaptureLoop()
        {
            while (_streaming)
            {
                yield return new WaitForEndOfFrame();
                if (_streaming && _capture != null)
                {
                    _capture.CaptureFrame(NowUs());
                }
            }
        }

        void HandleFrameReady(byte[] rgba, int frameWidth, int frameHeight, long ptsUs, bool flip)
        {
            _encoder?.PushFrame(rgba, frameWidth, frameHeight, ptsUs);
        }

        void HandleFrameEncoded(EncodedFrame frame)
        {
            if (_sender == null || _packetizer == null) return;

            var packet = _packetizer.Pack(frame);
            _sender.SendFrame(packet, frame.IsKeyFrame || frame.IsConfig);
        }

        void HandleIdrRequested()
        {
            _encoder?.RequestKeyFrame();
        }

        void HandleEncoderError(string message)
        {
            RaiseError(message);
        }

        void HandleSenderError(string message)
        {
            RaiseError(message);
        }

        bool TryAddTarget(string host, int port)
        {
            if (port <= 0 || port > 65535)
            {
                RaiseError("Invalid UDP target port: " + port);
                return false;
            }

            if (string.IsNullOrWhiteSpace(host) || !IPAddress.TryParse(host, out var ip))
            {
                RaiseError("Invalid UDP target address: " + host);
                return false;
            }

            _sender.AddTarget(new IPEndPoint(ip, port));
            return true;
        }

        void RaiseError(string message)
        {
            Debug.LogError("[VideoStream] " + message);
            OnError?.Invoke(message);
        }

        static long NowUs()
        {
#if UNITY_2022_2_OR_NEWER
            return (long)(Time.realtimeSinceStartupAsDouble * 1_000_000d);
#else
            return (long)(Time.realtimeSinceStartup * 1_000_000d);
#endif
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
            useHevc = true;
            flipY = true;
        }
    }
}
