namespace VideoStream
{
    internal readonly struct FrameHeader
    {
        public readonly int FrameId;
        public readonly long PtsUs;
        public readonly int NaluSize;
        public readonly ushort Flags;

        public FrameHeader(int frameId, long ptsUs, int naluSize, ushort flags)
        {
            FrameId = frameId;
            PtsUs = ptsUs;
            NaluSize = naluSize;
            Flags = flags;
        }

        public bool IsIdr => (Flags & FrameProtocol.FlagIdr) != 0;
        public bool IsConfig => (Flags & FrameProtocol.FlagConfig) != 0;
        public bool IsIdrRequest => (Flags & FrameProtocol.FlagIdrRequest) != 0;
        public bool IsPing => (Flags & FrameProtocol.FlagPing) != 0;
        public bool IsRegister => (Flags & FrameProtocol.FlagRegister) != 0;
        public bool IsLatencyProbe => (Flags & FrameProtocol.FlagLatencyProbe) != 0;
        public bool IsAvc => (Flags & FrameProtocol.FlagCodecAvc) != 0;
        public bool IsHevc => (Flags & FrameProtocol.FlagCodecHevc) != 0;
    }

    internal static class FrameProtocol
    {
        public const int HeaderSize = 18;

        public const ushort FlagIdr = 0x0001;
        public const ushort FlagConfig = 0x0002;
        public const ushort FlagPing = 0x0004;
        public const ushort FlagRegister = 0x0008;
        public const ushort FlagCodecAvc = 0x0010;
        public const ushort FlagCodecHevc = 0x0020;
        public const ushort FlagIdrRequest = 0x0040;
        public const ushort FlagDisconnect = 0x0080;
        public const ushort FlagCameraSubscribe = 0x0100;
        public const ushort FlagLatencyProbe = 0x0200;

        public static FrameHeader ParseHeader(byte[] data, int offset, int length)
        {
            if (data == null) throw new System.ArgumentNullException(nameof(data));
            if (length - offset < HeaderSize) throw new System.ArgumentException("Packet is shorter than FrameProtocol header");

            return new FrameHeader(
                ReadInt32(data, offset),
                ReadInt64(data, offset + 4),
                ReadInt32(data, offset + 12),
                ReadUInt16(data, offset + 16)
            );
        }

        static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        static int ReadInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) |
                   (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) |
                   data[offset + 3];
        }

        static long ReadInt64(byte[] data, int offset)
        {
            var hi = (long)ReadInt32(data, offset);
            var lo = (uint)ReadInt32(data, offset + 4);
            return (hi << 32) | lo;
        }
    }
}
