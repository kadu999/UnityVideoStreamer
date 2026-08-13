using System;

namespace VideoStream
{
    public interface IUnityVideoEncoder : IDisposable
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
}
