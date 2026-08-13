using System;
using System.Collections.Concurrent;
using System.Net;
using UnityEngine;

namespace VideoStream
{
    [DisallowMultipleComponent]
    public sealed class UnityVideoStreamerAutoConnect : MonoBehaviour
    {
        [SerializeField] UnityVideoStreamer streamer;
        [SerializeField] Camera captureCamera;
        [SerializeField] bool startSearchOnStart = true;
        [SerializeField] bool connectFirstTarget = true;
        [SerializeField] bool useHevc = false;

        readonly ConcurrentQueue<IPEndPoint> _pendingTargets = new ConcurrentQueue<IPEndPoint>();

        UdpTargetDiscovery _discovery;
        bool _connected;

        public UnityVideoStreamer Streamer
        {
            get
            {
                EnsureStreamer();
                return streamer;
            }
            set => streamer = value;
        }

        public Camera CaptureCamera
        {
            get => captureCamera;
            set => captureCamera = value;
        }

        public bool StartSearchOnStart
        {
            get => startSearchOnStart;
            set => startSearchOnStart = value;
        }

        public bool ConnectFirstTarget
        {
            get => connectFirstTarget;
            set => connectFirstTarget = value;
        }

        public bool UseHevc
        {
            get => useHevc;
            set => useHevc = value;
        }

        public bool IsSearching => _discovery != null;
        public bool IsConnected => _connected;

        public event Action<bool> SearchingChanged;
        public event Action<IPEndPoint> TargetDiscovered;
        public event Action<IPEndPoint> Connected;
        public event Action<string> Error;

        void Start()
        {
            if (startSearchOnStart)
            {
                StartSearching();
            }
        }

        void Update()
        {
            while (_pendingTargets.TryDequeue(out var endpoint))
            {
                TargetDiscovered?.Invoke(endpoint);
                if (connectFirstTarget && !_connected)
                {
                    ConnectTo(endpoint);
                }
            }
        }

        void OnDestroy()
        {
            StopSearching();
            streamer?.StopStreaming();
        }

        public void StartSearching()
        {
            EnsureStreamer();
            StopSearching();
            ClearPendingTargets();

            _connected = false;
            _discovery = new UdpTargetDiscovery();
            _discovery.TargetDiscovered += HandleTargetDiscovered;
            if (!_discovery.Start())
            {
                const string message = "Discovery bind failed on UDP 9997";
                Debug.LogError("[VideoStream] " + message);
                Error?.Invoke(message);
                _discovery.Dispose();
                _discovery = null;
                return;
            }

            SearchingChanged?.Invoke(true);
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

            EnsureStreamer();
            StopSearching();
            ClearPendingTargets();
            _connected = true;

            streamer.StopStreaming();
            streamer.AutoStart = false;
            streamer.AutoDiscovery = false;
            streamer.UseHevc = useHevc;
            if (captureCamera != null)
            {
                streamer.CaptureCamera = captureCamera;
            }

            streamer.TargetAddress = endpoint.Address.ToString();
            streamer.TargetPort = endpoint.Port;
            streamer.StartStreaming();
            Connected?.Invoke(endpoint);
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

        void EnsureStreamer()
        {
            if (streamer != null) return;
            streamer = GetComponent<UnityVideoStreamer>();
            if (streamer == null)
            {
                streamer = gameObject.AddComponent<UnityVideoStreamer>();
            }
        }
    }
}
