using System;

namespace VideoStream
{
    public readonly struct FrameHeader
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
        public bool IsLatencyProbe => (Flags & FrameProtocol.FlagLatencyProbe) != 0;
        public bool IsAvc => (Flags & FrameProtocol.FlagCodecAvc) != 0;
        public bool IsHevc => (Flags & FrameProtocol.FlagCodecHevc) != 0;
    }

    public static class FrameProtocol
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

        public static byte[] PackFrame(in FrameHeader header, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var packet = new byte[HeaderSize + payload.Length];
            WriteInt32(packet, 0, header.FrameId);
            WriteInt64(packet, 4, header.PtsUs);
            WriteInt32(packet, 12, header.NaluSize);
            WriteUInt16(packet, 16, header.Flags);
            Buffer.BlockCopy(payload, 0, packet, HeaderSize, payload.Length);
            return packet;
        }

        public static FrameHeader ParseHeader(byte[] data, int offset, int length)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (length - offset < HeaderSize) throw new ArgumentException("Packet is shorter than FrameProtocol header");

            return new FrameHeader(
                ReadInt32(data, offset),
                ReadInt64(data, offset + 4),
                ReadInt32(data, offset + 12),
                ReadUInt16(data, offset + 16)
            );
        }

        public static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        public static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        public static void WriteInt64(byte[] buffer, int offset, long value)
        {
            WriteInt32(buffer, offset, (int)(value >> 32));
            WriteInt32(buffer, offset + 4, (int)value);
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
