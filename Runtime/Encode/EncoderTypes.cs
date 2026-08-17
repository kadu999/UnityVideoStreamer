using System;
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
        public readonly float EncodeMs;

        public EncodedFrame(byte[] data, bool isConfig, bool isKeyFrame, string mimeType, long ptsUs, float encodeMs = 0f)
        {
            Data = data;
            IsConfig = isConfig;
            IsKeyFrame = isKeyFrame;
            MimeType = mimeType ?? "video/avc";
            PtsUs = ptsUs;
            EncodeMs = encodeMs;
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
            return new NativeMediaCodecEncoder();
#else
            Debug.Log("[VideoStream] Streaming requires an Android build; disabled in editor/desktop.");
            return null;
#endif
        }
    }
}
