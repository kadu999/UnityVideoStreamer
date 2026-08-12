using System;

namespace VideoStream
{
    public readonly struct EncodedFrame
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
}
