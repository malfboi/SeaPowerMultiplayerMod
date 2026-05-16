namespace SeapowerMultiplayer.Transport
{
    internal readonly struct LiteNetFragmentHeader
    {
        public LiteNetFragmentHeader(uint sequenceId, byte originalMessageType, byte deliveryClass,
            int chunkIndex, int totalChunks, int totalLength, int payloadLength)
        {
            SequenceId = sequenceId;
            OriginalMessageType = originalMessageType;
            DeliveryClass = deliveryClass;
            ChunkIndex = chunkIndex;
            TotalChunks = totalChunks;
            TotalLength = totalLength;
            PayloadLength = payloadLength;
        }

        public uint SequenceId { get; }
        public byte OriginalMessageType { get; }
        public byte DeliveryClass { get; }
        public int ChunkIndex { get; }
        public int TotalChunks { get; }
        public int TotalLength { get; }
        public int PayloadLength { get; }
    }

    internal static class LiteNetFragmentCodec
    {
        public const byte FragmentMarker = 0xFF;
        public const int HeaderSize = 17;
        public const int SafetyMargin = 8;
        public const int MaxFragmentChunks = ushort.MaxValue;
        public const int MaxReassembledBytes = 512 * 1024;

        public static bool IsFragmentPacket(byte[] data, int length) =>
            length >= HeaderSize && data[0] == FragmentMarker;

        public static int GetChunkPayloadMax(int maxSinglePacketSize) =>
            maxSinglePacketSize - HeaderSize - SafetyMargin;

        public static int GetChunkCount(int totalLength, int chunkPayloadMax)
        {
            if (totalLength <= 0 || chunkPayloadMax <= 0) return 0;
            return (totalLength + chunkPayloadMax - 1) / chunkPayloadMax;
        }

        public static bool IsValidChunkCount(int totalChunks) =>
            totalChunks > 0 && totalChunks <= MaxFragmentChunks;

        public static void WriteHeader(byte[] data, LiteNetFragmentHeader header)
        {
            data[0] = FragmentMarker;
            WriteUInt32(data, 1, header.SequenceId);
            data[5] = header.OriginalMessageType;
            data[6] = header.DeliveryClass;
            WriteUInt16(data, 7, (ushort)header.ChunkIndex);
            WriteUInt16(data, 9, (ushort)header.TotalChunks);
            WriteInt32(data, 11, header.TotalLength);
            WriteUInt16(data, 15, (ushort)header.PayloadLength);
        }

        public static LiteNetFragmentHeader ReadHeader(byte[] data) =>
            new LiteNetFragmentHeader(
                ReadUInt32(data, 1),
                data[5],
                data[6],
                ReadUInt16(data, 7),
                ReadUInt16(data, 9),
                ReadInt32(data, 11),
                ReadUInt16(data, 15));

        public static bool ValidateHeader(LiteNetFragmentHeader header, int packetLength, out string reason)
        {
            if (!IsValidChunkCount(header.TotalChunks))
            {
                reason = $"totalChunks={header.TotalChunks}";
                return false;
            }

            if (header.ChunkIndex < 0 || header.ChunkIndex >= header.TotalChunks)
            {
                reason = $"chunk={header.ChunkIndex}/{header.TotalChunks}";
                return false;
            }

            if (header.TotalLength <= 0 || header.TotalLength > MaxReassembledBytes)
            {
                reason = $"total={header.TotalLength}";
                return false;
            }

            if (header.PayloadLength < 0 || header.PayloadLength != packetLength - HeaderSize)
            {
                reason = $"payload={header.PayloadLength} packet={packetLength}";
                return false;
            }

            reason = "";
            return true;
        }

        private static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static int ReadUInt16(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8);

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) |
                   (data[offset + 2] << 16) | (data[offset + 3] << 24));

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static int ReadInt32(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8) |
            (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }
}
