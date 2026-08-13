#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace VideoStream
{
    public sealed class VideoStreamNativeReceiver : MonoBehaviour
    {
        [SerializeField] int localPort = 9999;
        [SerializeField] string mime = "video/avc";

        readonly byte[] _packetBuffer = new byte[64 * 1024];
        readonly byte[] _decodedBuffer = new byte[4 * 1024 * 1024];

        bool _running;

        public event Action<byte[], int, int, long> FrameDecoded;

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
            }
        }
    }
}
#endif
