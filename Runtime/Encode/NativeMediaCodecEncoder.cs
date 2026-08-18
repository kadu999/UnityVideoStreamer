#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
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
        volatile bool _running;

        // 帧缓冲对象池：PollEncodedFrames 每帧 new byte[size] 造成 60 次/秒分配，
        // 周期性触发 GC 停顿（表现为每隔几秒 60->55fps 计数跳动）。池化后复用缓冲区。
        const int PoolMaxBuffers = 8;
        const int PoolMaxTotalBytes = 1 << 20; // 1MB
        readonly object _poolLock = new object();
        readonly List<byte[]> _pool = new List<byte[]>();
        int _poolBytes;

        public event Action<EncodedFrame> FrameEncoded;
        public event Action<string> Error;

        public bool IsRunning => _running;

        byte[] RentBuffer(int minSize)
        {
            lock (_poolLock)
            {
                for (int i = 0; i < _pool.Count; i++)
                {
                    if (_pool[i].Length >= minSize)
                    {
                        var buf = _pool[i];
                        _pool.RemoveAt(i);
                        _poolBytes -= buf.Length;
                        return buf;
                    }
                }
            }
            return new byte[minSize];
        }

        void ReturnBuffer(byte[] buf)
        {
            lock (_poolLock)
            {
                if (_pool.Count >= PoolMaxBuffers || _poolBytes + buf.Length > PoolMaxTotalBytes)
                {
                    return; // 超上限直接丢弃，交给 GC
                }
                _pool.Add(buf);
                _poolBytes += buf.Length;
            }
        }

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
                // Native direct send: let the codec loop push frames straight to
                // UDP once targets are set (C4/native-send).
                VideoStreamNative.VSMedia_CodecSetUdpReady(1);
                VideoStreamNative.SetActive(1);
                Debug.Log($"[VideoStream] Native encoder started: {config.Width}x{config.Height} {config.FrameRate}fps {config.MimeType} native-send=on");
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
                float encodeMs;
                if (VideoStreamNative.VSMedia_CodecDequeueFrame(
                        _frameBuffer,
                        _frameBuffer.Length,
                        out size,
                        out isConfig,
                        out isKeyFrame,
                        out ptsUs,
                        out encodeMs) != 1)
                {
                    break;
                }

                // A3: encode latency now comes from the native side (same plugin clock,
                // measured at dequeue time) — no C# poll-cadence noise in enc_ms.
                // 帧缓冲对象池：复用缓冲区，消除每帧 new byte[] 的 GC 压力。
                var data = RentBuffer(size);
                Buffer.BlockCopy(_frameBuffer, 0, data, 0, size);
                FrameEncoded?.Invoke(new EncodedFrame(data, isConfig, isKeyFrame, _mime, ptsUs, encodeMs));
                // 事件消费方（UnityVideoStreamer.HandleFrameEncoded）是同步的且不持有
                // 引用（native-send 已把数据发出，C# 侧只用 size 做 trace），可安全回收。
                ReturnBuffer(data);
            }
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
            // Native direct send (C4/native-send): the codec loop already pushed
            // this frame to UDP at dequeue time; this method only keeps the trace
            // (sampled) alive. The wire frameId/sequence now live in native code.
            if (!frame.IsConfig && TraceUploader.ShouldTraceFrame(frameId))
            {
                TraceUploader.TraceFrame(
                    $"ev=ENC_OUT frame={frameId} pts={frame.PtsUs} size={frame.Data.Length} enc_ms={frame.EncodeMs:F1}",
                    frameId);
                TraceUploader.TraceFrame(
                    $"ev=SEND frame={frameId} bytes={frame.Data.Length} dgrams={TraceUploader.FragmentCount(frame.Data.Length)}",
                    frameId);
            }
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
                VideoStreamNative.VSMedia_CodecSetUdpReady(0);
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
