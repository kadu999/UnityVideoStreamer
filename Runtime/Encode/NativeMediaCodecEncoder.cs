#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace VideoStream
{
    internal sealed class NativeMediaCodecEncoder : IUnityVideoEncoder
    {
        readonly object _lock = new object();
        readonly byte[] _frameBuffer = new byte[4 * 1024 * 1024];

        string _mime = "video/avc";
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

                _mime = config.MimeType;
                _running = true;
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

                var data = new byte[size];
                Buffer.BlockCopy(_frameBuffer, 0, data, 0, size);
                FrameEncoded?.Invoke(new EncodedFrame(data, isConfig, isKeyFrame, _mime, ptsUs));
            }
        }

        public void RequestKeyFrame()
        {
            VideoStreamNative.VSMedia_CodecRequestKeyFrame();
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                VideoStreamNative.VSMedia_CodecStop();
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
#endif
