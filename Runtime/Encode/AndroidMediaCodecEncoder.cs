using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VideoStream
{
    [Serializable]
    internal sealed class VideoStreamConfig
    {
        public string TargetAddress = "192.168.43.129";
        public int TargetPort = 9999;
        public int LocalPort = 9998;
        public int Width = 1280;
        public int Height = 720;
        public int FrameRate = 30;
        public int Bitrate = 8_000_000;
        public int KeyFrameIntervalSeconds = 2;
        public bool UseHevc = false;
        public bool FlipY = true;

        public string MimeType => UseHevc ? "video/hevc" : "video/avc";
    }

    internal readonly struct EncodedFrame
    {
        public readonly byte[] Data;
        public readonly bool IsConfig;
        public readonly bool IsKeyFrame;
        public readonly string MimeType;
        public readonly long PtsUs;

        public EncodedFrame(byte[] data, bool isConfig, bool isKeyFrame, string mimeType, long ptsUs)
        {
            Data = data;
            IsConfig = isConfig;
            IsKeyFrame = isKeyFrame;
            MimeType = mimeType ?? "video/avc";
            PtsUs = ptsUs;
        }
    }

    internal interface IUnityVideoEncoder : IDisposable
    {
        bool IsRunning { get; }
        event Action<EncodedFrame> FrameEncoded;
        event Action<string> Error;

        bool Start(VideoStreamConfig config);
        void RenderFrame(IntPtr nativeTexturePtr, int width, int height, bool flipY);
        void PollEncodedFrames();
        void RequestKeyFrame();
        void Stop();
    }

    internal static class PlatformEncoder
    {
        public static IUnityVideoEncoder Create(VideoStreamConfig config)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidMediaCodecEncoder();
#else
            Debug.Log("[VideoStream] Streaming requires an Android build; disabled in editor/desktop.");
            return null;
#endif
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    internal sealed class AndroidMediaCodecEncoder : IUnityVideoEncoder
    {
        readonly object _lock = new object();
        AndroidJavaObject _javaEncoder;
        JavaCallbackProxy _proxy;
        volatile bool _running;

        public event Action<EncodedFrame> FrameEncoded;
        public event Action<string> Error;

        public bool IsRunning => _running;

        public bool Start(VideoStreamConfig config)
        {
            lock (_lock)
            {
                if (_running) return true;

                try
                {
                    _proxy = new JavaCallbackProxy(this);
                    _javaEncoder = new AndroidJavaObject("com.videostream.stream.VideoStreamEncoder");
                    _javaEncoder.Call("setCallback", _proxy);

                    var ok = _javaEncoder.Call<bool>(
                        "open",
                        config.Width,
                        config.Height,
                        config.Bitrate,
                        config.FrameRate,
                        config.KeyFrameIntervalSeconds,
                        config.MimeType
                    );

                    if (!ok)
                    {
                        DisposeJava();
                        return false;
                    }

                    _running = true;
#if UNITY_ANDROID && !UNITY_EDITOR
                    VideoStreamNative.SetActive(1);
#endif
                    Debug.Log($"[VideoStream] Android encoder started: {config.Width}x{config.Height} {config.FrameRate}fps {config.MimeType}");
                    return true;
                }
                catch (Exception ex)
                {
                    Error?.Invoke("Android encoder start failed: " + ex.Message);
                    DisposeJava();
                    return false;
                }
            }
        }

        public void RenderFrame(IntPtr nativeTexturePtr, int width, int height, bool flipY)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_running) return;
            VideoStreamNative.SetFrameInfo(nativeTexturePtr, width, height, flipY ? 1 : 0);
            GL.IssuePluginEvent(VideoStreamNative.GetRenderEventFunc(), VideoStreamNative.GetRenderEventId());
#endif
        }

        public void PollEncodedFrames()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var encoder = _javaEncoder;
            if (!_running || encoder == null) return;

            while (true)
            {
                sbyte[] rawFrame;
                try
                {
                    rawFrame = encoder.Call<sbyte[]>("pollFrameBytes");
                }
                catch (Exception ex)
                {
                    Error?.Invoke("Poll encoded frame failed: " + ex.Message);
                    return;
                }

                if (rawFrame == null || rawFrame.Length == 0) break;

                try
                {
                    const int headerSize = 14;
                    if (rawFrame.Length <= headerSize) continue;

                    var data = new byte[rawFrame.Length - headerSize];
                    Buffer.BlockCopy(rawFrame, headerSize, data, 0, data.Length);

                    var mimeCode = ((rawFrame[2] & 0xff) << 24) |
                                   ((rawFrame[3] & 0xff) << 16) |
                                   ((rawFrame[4] & 0xff) << 8) |
                                   (rawFrame[5] & 0xff);
                    long ptsUs = 0;
                    for (var i = 6; i < headerSize; i++)
                    {
                        ptsUs = (ptsUs << 8) | (rawFrame[i] & 0xffL);
                    }

                    RaiseFrameEncoded(
                        data,
                        rawFrame[0] != 0,
                        rawFrame[1] != 0,
                        mimeCode == 1 ? "video/hevc" : "video/avc",
                        ptsUs);
                }
                catch (Exception ex)
                {
                    Error?.Invoke("Decode encoded frame failed: " + ex.Message);
                    return;
                }
            }
#endif
        }

        public void RequestKeyFrame()
        {
            try { _javaEncoder?.Call("requestKeyFrame"); }
            catch (Exception ex)
            {
                Error?.Invoke("Request key frame failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running && _javaEncoder == null) return;
                _running = false;
#if UNITY_ANDROID && !UNITY_EDITOR
                VideoStreamNative.SetActive(0);
#endif
                DisposeJava();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        void DisposeJava()
        {
            try { _javaEncoder?.Call("close"); } catch { }
            try { _javaEncoder?.Dispose(); } catch { }
            _javaEncoder = null;
            _proxy = null;
        }

        internal void RaiseFrameEncoded(
            byte[] data,
            bool isConfig,
            bool isKeyFrame,
            string mimeType,
            long ptsUs
        )
        {
            FrameEncoded?.Invoke(new EncodedFrame(data, isConfig, isKeyFrame, mimeType, ptsUs));
        }

        internal void RaiseError(string message)
        {
            Error?.Invoke(message);
        }
    }

    sealed class JavaCallbackProxy : AndroidJavaProxy
    {
        readonly AndroidMediaCodecEncoder _owner;

        public JavaCallbackProxy(AndroidMediaCodecEncoder owner)
            : base("com.videostream.stream.VideoStreamCallback")
        {
            _owner = owner;
        }

        void onError(string message)
        {
            _owner.RaiseError(message);
        }
    }

    internal static class VideoStreamNative
    {
        const string Library = "unity-video-streamer-native";

        [DllImport(Library)]
        internal static extern IntPtr GetRenderEventFunc();

        [DllImport(Library)]
        internal static extern int GetRenderEventId();

        [DllImport(Library)]
        internal static extern void SetActive(int active);

        [DllImport(Library)]
        internal static extern void SetFrameInfo(IntPtr texture, int width, int height, int flipY);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpStart(int localPort);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpStop();

        [DllImport(Library)]
        internal static extern int VSMedia_UdpAddTarget(
            [MarshalAs(UnmanagedType.LPStr)] string ip,
            int port);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpSendFrame(
            int frameId,
            long ptsUs,
            [In] byte[] data,
            int size,
            bool isConfig,
            bool isKeyFrame,
            [MarshalAs(UnmanagedType.LPStr)] string mime,
            uint sequence);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpPollPacket(
            [Out] byte[] buffer,
            int capacity,
            out int size);

        [DllImport(Library)]
        internal static extern int VSMedia_UdpTakeIdrRequest();

        [DllImport(Library)]
        internal static extern int VSMedia_CodecStart(
            int width,
            int height,
            int bitrate,
            int frameRate,
            int iFrameIntervalSeconds,
            [MarshalAs(UnmanagedType.LPStr)] string mime);

        [DllImport(Library)]
        internal static extern int VSMedia_CodecStop();

        [DllImport(Library)]
        internal static extern IntPtr VSMedia_CodecGetInputSurface();

        [DllImport(Library)]
        internal static extern int VSMedia_CodecDequeueFrame(
            [Out] byte[] buffer,
            int capacity,
            out int size,
            out bool isConfig,
            out bool isKeyFrame,
            out long ptsUs);

        [DllImport(Library)]
        internal static extern void VSMedia_CodecRequestKeyFrame();

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderStart(
            [MarshalAs(UnmanagedType.LPStr)] string mime);

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderFeed(
            [In] byte[] data,
            int size,
            long ptsUs);

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderDequeueFrame(
            [Out] byte[] buffer,
            int capacity,
            out int size,
            out int width,
            out int height,
            out long ptsUs);

        [DllImport(Library)]
        internal static extern int VSMedia_DecoderStop();
    }
#endif
}
