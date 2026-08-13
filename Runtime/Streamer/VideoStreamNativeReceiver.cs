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

        readonly byte[] _packetBuffer = new byte[64 * 1024];
        readonly byte[] _decodedBuffer = new byte[4 * 1024 * 1024];
        readonly byte[] _rgbaBuffer = new byte[4 * 1024 * 1024];
        Texture2D _outputTexture;

        bool _running;

        public event Action<byte[], int, int, long> FrameDecoded;
        public event Action<Texture2D, long> FrameRendered;
        public Texture OutputTexture => _outputTexture;

        void OnEnable()
        {
            if (!_running)
            {
                _running = VideoStreamNative.VSMedia_UdpStart(localPort) == 1 &&
                           VideoStreamNative.VSMedia_DecoderStart(mime) == 1;
            }
        }

        void Update()
        {
            if (!_running)
            {
                return;
            }

            DrainPackets();
            DrainDecodedFrames();
        }

        void OnDisable()
        {
            if (_running)
            {
                VideoStreamNative.VSMedia_DecoderStop();
                VideoStreamNative.VSMedia_UdpStop();
                _running = false;
            }
        }

        void DrainPackets()
        {
            while (true)
            {
                int packetSize;
                if (VideoStreamNative.VSMedia_UdpPollPacket(
                        _packetBuffer,
                        _packetBuffer.Length,
                        out packetSize) != 1)
                {
                    break;
                }

                if (packetSize < FrameProtocol.HeaderSize)
                {
                    continue;
                }

                var header = FrameProtocol.ParseHeader(_packetBuffer, 0, packetSize);
                if (!header.IsAvc && !header.IsHevc)
                {
                    continue;
                }

                var payloadSize = packetSize - FrameProtocol.HeaderSize;
                var payload = new byte[payloadSize];
                Buffer.BlockCopy(_packetBuffer, FrameProtocol.HeaderSize, payload, 0, payloadSize);
                VideoStreamNative.VSMedia_DecoderFeed(payload, payloadSize, header.PtsUs);
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
                if (VideoStreamNative.VSMedia_DecoderDequeueFrame(
                        _decodedBuffer,
                        _decodedBuffer.Length,
                        out size,
                        out width,
                        out height,
                        out ptsUs) != 1)
                {
                    break;
                }

                var data = new byte[size];
                Buffer.BlockCopy(_decodedBuffer, 0, data, 0, size);
                FrameDecoded?.Invoke(data, width, height, ptsUs);

                var rgbaSize = width * height * 4;
                if (width > 0 && height > 0 &&
                    size >= width * height * 3 / 2 &&
                    _rgbaBuffer.Length >= rgbaSize)
                {
                    VideoStreamNative.VSMedia_DecoderConvertNv12ToRgba(
                        _decodedBuffer,
                        width,
                        height,
                        _rgbaBuffer);

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

                    var handle = GCHandle.Alloc(_rgbaBuffer, GCHandleType.Pinned);
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
}
#endif
