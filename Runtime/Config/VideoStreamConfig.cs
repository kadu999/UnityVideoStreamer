using System;

namespace VideoStream
{
    [Serializable]
    public sealed class VideoStreamConfig
    {
        public string TargetAddress = "192.168.43.129";
        public int TargetPort = 9999;
        public int LocalPort = 9998;
        public int Width = 1280;
        public int Height = 720;
        public int FrameRate = 30;
        public int Bitrate = 8_000_000;
        public int KeyFrameIntervalSeconds = 2;
        public int MaxQueuedFrames = 3;
        public bool UseHevc = true;
        public bool FlipY = true;

        public string MimeType => UseHevc ? "video/hevc" : "video/avc";
    }
}
