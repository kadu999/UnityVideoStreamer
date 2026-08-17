#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Threading;
using UnityEngine;

namespace VideoStream
{
    internal sealed class NativeMediaCodecEncoder : IUnityVideoEncoder
    {
        readonly object _lock = new object();
        readonly byte[] _frameBuffer = new byte[4 * 1024 * 1024];

        string _mime = "video/avc";
        int _frameId;
        int _sequence;
        volatile bool _running;

        public event Action<EncodedFrame> FrameEncoded;
        public event Action<string> Error;

        public bool IsRunning => _running;

        public bool Start(VideoStreamConfig config)
        {
            lock (_lock)
            {
                if (_running) return true;

                var ok = VideoStreamNative.VSMedia_CodecStart(
                    config.Width,
                    config.Height,
                    config.Bitrate,
                    config.FrameRate,
                    config.KeyFrameIntervalSeconds,
                    config.MimeType) == 1;
                if (!ok)
                {
                    Error?.Invoke("Native MediaCodec encoder start failed");
                    return false;
                }

                if (VideoStreamNative.VSMedia_UdpStart(config.LocalPort) != 1 ||
                    VideoStreamNative.VSMedia_UdpAddTarget(config.TargetAddress, config.TargetPort) != 1)
                {
                    Error?.Invoke("Native UDP start failed");
                    VideoStreamNative.VSMedia_CodecStop();
                    return false;
                }

                _mime = config.MimeType;
                _running = true;
                VideoStreamNative.SetActive(1);
                Debug.Log($"[VideoStream] Native encoder started: {config.Width}x{config.Height} {config.FrameRate}fps {config.MimeType}");
                return true;
            }
        }

        public void RenderFrame(IntPtr nativeTexturePtr, int width, int height, bool flipY)
        {
            if (!_running) return;
            VideoStreamNative.SetFrameInfo(nativeTexturePtr, width, height, flipY ? 1 : 0);
            GL.IssuePluginEvent(VideoStreamNative.GetRenderEventFunc(), VideoStreamNative.GetRenderEventId());
        }

        public void PollEncodedFrames()
        {
            if (!_running) return;

            while (true)
            {
                int size;
                bool isConfig;
                bool isKeyFrame;
                long ptsUs;
                if (VideoStreamNative.VSMedia_CodecDequeueFrame(
                        _frameBuffer,
                        _frameBuffer.Length,
                        out size,
                        out isConfig,
                        out isKeyFrame,
                        out ptsUs) != 1)
                {
                    break;
                }

                // Encode latency = now - encoder input PTS (covers input-surface queueing too).
                var encodeMs = (NowNs() - ptsUs * 1000L) / 1e6f;
                var data = new byte[size];
                Buffer.BlockCopy(_frameBuffer, 0, data, 0, size);
                FrameEncoded?.Invoke(new EncodedFrame(data, isConfig, isKeyFrame, _mime, ptsUs, encodeMs));
            }
        }

        static long NowNs()
        {
            // Monotonic high-res clock; double avoids long overflow, ms-grade precision is enough.
            return (long)((double)System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000_000.0
                / System.Diagnostics.Stopwatch.Frequency);
        }

        public void RequestKeyFrame()
        {
            VideoStreamNative.VSMedia_CodecRequestKeyFrame();
        }

        public void SendFrame(EncodedFrame frame)
        {
            if (!_running || frame.Data == null || frame.Data.Length == 0)
            {
                return;
            }

            var frameId = Interlocked.Increment(ref _frameId) - 1;
            var sequence = (uint)Interlocked.Increment(ref _sequence);
            if (!frame.IsConfig)
            {
                // PIPETRACE: encode-out and send events (sampled by frameId).
                TraceUploader.TraceFrame(
                    $"ev=ENC_OUT frame={frameId} pts={frame.PtsUs} size={frame.Data.Length} enc_ms={frame.EncodeMs:F1}",
                    frameId);
                TraceUploader.TraceFrame(
                    $"ev=SEND frame={frameId} bytes={frame.Data.Length} dgrams={TraceUploader.FragmentCount(frame.Data.Length)}",
                    frameId);
            }
            VideoStreamNative.VSMedia_UdpSendFrame(
                frameId,
                frame.PtsUs,
                frame.Data,
                frame.Data.Length,
                frame.IsConfig,
                frame.IsKeyFrame,
                frame.MimeType,
                sequence);
        }

        public bool TakeIdrRequest()
        {
            return VideoStreamNative.VSMedia_UdpTakeIdrRequest() > 0;
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                VideoStreamNative.SetActive(0);
                VideoStreamNative.VSMedia_CodecStop();
                VideoStreamNative.VSMedia_UdpStop();
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
#endif
