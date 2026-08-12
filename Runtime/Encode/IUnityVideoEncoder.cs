using System;

namespace VideoStream
{
    public interface IUnityVideoEncoder : IDisposable
    {
        bool IsRunning { get; }
        event Action<EncodedFrame> FrameEncoded;
        event Action<string> Error;

        bool Start(VideoStreamConfig config);
        void PushFrame(byte[] rgba, int width, int height, long ptsUs);
        void RequestKeyFrame();
        void Stop();
    }
}
