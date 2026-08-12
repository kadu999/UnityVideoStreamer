using System;
using System.Collections.Generic;

namespace VideoStream
{
    public static class UdpFramer
    {
        public const int HeaderSize = 10;
        public const int MaxDatagramPayload = 1400;
        public const ushort FlagIsIdr = 0x0001;

        public static List<byte[]> Fragment(byte[] packet, int sequence, bool isIdr)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            var flags = isIdr ? FlagIsIdr : (ushort)0;
            if (packet.Length <= MaxDatagramPayload)
            {
                return new List<byte[]> { MakeFragment(flags, 0, 1, sequence, packet) };
            }

            var count = (packet.Length + MaxDatagramPayload - 1) / MaxDatagramPayload;
            var fragments = new List<byte[]>(count);
            for (var index = 0; index < count; index++)
            {
                var start = index * MaxDatagramPayload;
                var end = Math.Min(start + MaxDatagramPayload, packet.Length);
                var payload = new byte[end - start];
                Buffer.BlockCopy(packet, start, payload, 0, payload.Length);
                fragments.Add(MakeFragment(flags, index, count, sequence, payload));
            }
            return fragments;
        }

        static byte[] MakeFragment(ushort flags, int index, int count, int sequence, byte[] payload)
        {
            var datagram = new byte[HeaderSize + payload.Length];
            FrameProtocol.WriteUInt16(datagram, 0, flags);
            FrameProtocol.WriteUInt16(datagram, 2, (ushort)index);
            FrameProtocol.WriteUInt16(datagram, 4, (ushort)count);
            FrameProtocol.WriteInt32(datagram, 6, sequence);
            Buffer.BlockCopy(payload, 0, datagram, HeaderSize, payload.Length);
            return datagram;
        }
    }
}
