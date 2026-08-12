using System.Threading;

namespace VideoStream
{
    public sealed class FramePacketizer
    {
        int _frameId;

        public byte[] Pack(EncodedFrame frame)
        {
            ushort flags = 0;
            if (frame.IsConfig) flags |= FrameProtocol.FlagConfig;
            if (frame.IsKeyFrame) flags |= FrameProtocol.FlagIdr;

            if (string.Equals(frame.MimeType, "video/hevc", System.StringComparison.OrdinalIgnoreCase))
            {
                flags |= FrameProtocol.FlagCodecHevc;
            }
            else
            {
                flags |= FrameProtocol.FlagCodecAvc;
            }

            var frameId = Interlocked.Increment(ref _frameId) - 1;
            var header = new FrameHeader(frameId, frame.PtsUs, frame.Data.Length, flags);
            return FrameProtocol.PackFrame(header, frame.Data);
        }
    }
}
