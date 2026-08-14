#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace VideoStream
{
    public sealed class VideoStreamNativeReceiver : MonoBehaviour
    {
        [SerializeField] int localPort = 9999;
        [SerializeField] string mime = "video/avc";
        [SerializeField] bool autoStart = false;
        [SerializeField] bool decodeEnabled = true;

        bool _running;
        bool _surfaceReady;

        public int LocalPort
        {
            get => localPort;
            set => localPort = value;
        }

        public string Mime
        {
            get => mime;
            set => mime = value;
        }

        public bool AutoStart
        {
            get => autoStart;
            set => autoStart = value;
        }

        public bool DecodeEnabled
        {
            get => decodeEnabled;
            set => decodeEnabled = value;
        }

        void OnEnable()
        {
            if (autoStart)
            {
                StartReceiver();
            }
        }

        void OnDisable()
        {
            StopReceiver();
        }

        public void StartReceiver()
        {
            if (_running)
            {
                return;
            }

            if (decodeEnabled && !_surfaceReady)
            {
                Debug.LogError(
                    "[VideoStream] Hardware decode requires SetCameraSurface before StartReceiver.");
                return;
            }

            var udpStarted = VideoStreamNative.VSMedia_UdpStart(localPort) == 1;
            if (!udpStarted)
            {
                return;
            }

            _running = udpStarted &&
                       (!decodeEnabled || VideoStreamNative.VSMedia_DecoderStart(mime) == 1);
            if (!_running)
            {
                VideoStreamNative.VSMedia_UdpStop();
            }
        }

        public void StopReceiver()
        {
            if (_running)
            {
                if (decodeEnabled)
                {
                    VideoStreamNative.VSMedia_DecoderStop();
                }
                VideoStreamNative.VSMedia_UdpStop();
                _running = false;
            }
        }

        public bool SetCameraSurface(IntPtr surface, IntPtr surfaceTexture)
        {
            if (surface == IntPtr.Zero || surfaceTexture == IntPtr.Zero)
            {
                Debug.LogError(
                    "[VideoStream] Hardware decode Surface output requires both Surface and SurfaceTexture.");
                _surfaceReady = false;
                return false;
            }

            var surfaceOk = VideoStreamNative.VSMedia_DecoderSetOutputSurface(surface) == 1;
            var textureOk = VideoStreamNative.VSMedia_CameraSetSurfaceTexture(surfaceTexture) == 1;
            _surfaceReady = surfaceOk && textureOk;
            if (!_surfaceReady)
            {
                Debug.LogError(
                    "[VideoStream] Failed to configure hardware decode Surface output.");
            }
            return _surfaceReady;
        }

        public void RequestSurfaceTextureUpdate()
        {
            GL.IssuePluginEvent(
                VideoStreamNative.GetRenderEventFunc(),
                VideoStreamNative.GetCameraUpdateEventId());
        }

        public int CameraExternalTextureId
        {
            get { return VideoStreamNative.VSMedia_CameraGetExternalTexture(); }
        }

        public int OutputWidth
        {
            get { return decodeEnabled ? VideoStreamNative.VSMedia_DecoderGetOutputWidth() : 0; }
        }

        public int OutputHeight
        {
            get { return decodeEnabled ? VideoStreamNative.VSMedia_DecoderGetOutputHeight() : 0; }
        }
    }
}
#endif
