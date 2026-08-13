#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VideoStream
{
    public sealed class VideoStreamNativeReceiver : MonoBehaviour
    {
        [SerializeField] int localPort = 9999;
        [SerializeField] string mime = "video/avc";
        [SerializeField] bool autoStart = true;
        [SerializeField] bool publishTexture = true;

        readonly byte[] _frameBuffer = new byte[32 * 1024 * 1024];
        Texture2D _outputTexture;

        bool _running;

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

        public bool PublishTexture
        {
            get => publishTexture;
            set => publishTexture = value;
        }

        public event Action<byte[], int, int, long> FrameDecoded;
        public event Action<Texture2D, long> FrameRendered;
        public Texture OutputTexture => _outputTexture;

        void OnEnable()
        {
            if (autoStart)
            {
                StartReceiver();
            }
        }

        void Update()
        {
            if (!_running)
            {
                return;
            }

            DrainDecodedFrames();
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

            var udpStarted = VideoStreamNative.VSMedia_UdpStart(localPort) == 1;
            VideoStreamNative.VSMedia_DecoderSetPreviewEnabled(publishTexture ? 1 : 0);
            _running = udpStarted && VideoStreamNative.VSMedia_DecoderStart(mime) == 1;
            if (udpStarted && !_running)
            {
                VideoStreamNative.VSMedia_UdpStop();
            }
        }

        public void StopReceiver()
        {
            if (_running)
            {
                VideoStreamNative.VSMedia_DecoderStop();
                VideoStreamNative.VSMedia_UdpStop();
                _running = false;
            }
        }

        void DrainDecodedFrames()
        {
            while (true)
            {
                int size;
                int width;
                int height;
                long ptsUs;
                if (VideoStreamNative.VSMedia_DecoderDequeueRgba(
                        _frameBuffer,
                        _frameBuffer.Length,
                        out size,
                        out width,
                        out height,
                        out ptsUs) != 1)
                {
                    break;
                }

                var rgbaSize = width * height * 4;
                if (size < rgbaSize || rgbaSize > _frameBuffer.Length)
                {
                    continue;
                }

                if (!publishTexture)
                {
                    continue;
                }

                if (FrameDecoded != null)
                {
                    var data = new byte[rgbaSize];
                    Buffer.BlockCopy(_frameBuffer, 0, data, 0, rgbaSize);
                    FrameDecoded.Invoke(data, width, height, ptsUs);
                }

                if (_outputTexture == null ||
                    _outputTexture.width != width ||
                    _outputTexture.height != height)
                {
                    if (_outputTexture != null)
                    {
                        Destroy(_outputTexture);
                    }
                    _outputTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                }

                var handle = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
                try
                {
                    _outputTexture.LoadRawTextureData(handle.AddrOfPinnedObject(), rgbaSize);
                }
                finally
                {
                    handle.Free();
                }
                _outputTexture.Apply();
                FrameRendered?.Invoke(_outputTexture, ptsUs);
            }
        }
    }
}
#endif
