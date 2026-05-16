using System;
using SeapowerMultiplayer.Transport;

namespace SeapowerMultiplayer.Tests
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                HeaderRoundTrips();
                NonFragmentPacketIsIgnored();
                ChunkSizingUsesPeerLimitWithSafetyMargin();
                InvalidChunkIndexIsRejected();
                OversizedAssemblyIsRejected();
                PayloadLengthMismatchIsRejected();

                Console.WriteLine($"All tests passed ({_passed}).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static void HeaderRoundTrips()
        {
            var header = new LiteNetFragmentHeader(
                sequenceId: 0x12345678,
                originalMessageType: 0,
                deliveryClass: 4,
                chunkIndex: 2,
                totalChunks: 3,
                totalLength: 2793,
                payloadLength: 777);
            var packet = new byte[LiteNetFragmentCodec.HeaderSize + header.PayloadLength];

            LiteNetFragmentCodec.WriteHeader(packet, header);

            Assert(LiteNetFragmentCodec.IsFragmentPacket(packet, packet.Length), "fragment marker not detected");
            var read = LiteNetFragmentCodec.ReadHeader(packet);
            AssertEqual(header.SequenceId, read.SequenceId, "sequence id");
            AssertEqual(header.OriginalMessageType, read.OriginalMessageType, "message type");
            AssertEqual(header.DeliveryClass, read.DeliveryClass, "delivery class");
            AssertEqual(header.ChunkIndex, read.ChunkIndex, "chunk index");
            AssertEqual(header.TotalChunks, read.TotalChunks, "total chunks");
            AssertEqual(header.TotalLength, read.TotalLength, "total length");
            AssertEqual(header.PayloadLength, read.PayloadLength, "payload length");
            Assert(LiteNetFragmentCodec.ValidateHeader(read, packet.Length, out var reason), reason);
            Pass();
        }

        private static void NonFragmentPacketIsIgnored()
        {
            var packet = new byte[LiteNetFragmentCodec.HeaderSize + 10];
            packet[0] = 0;

            Assert(!LiteNetFragmentCodec.IsFragmentPacket(packet, packet.Length), "normal message detected as fragment");
            Pass();
        }

        private static void ChunkSizingUsesPeerLimitWithSafetyMargin()
        {
            int payloadMax = LiteNetFragmentCodec.GetChunkPayloadMax(1023);
            int chunks = LiteNetFragmentCodec.GetChunkCount(2793, payloadMax);

            AssertEqual(998, payloadMax, "chunk payload max");
            AssertEqual(3, chunks, "chunk count");
            Pass();
        }

        private static void InvalidChunkIndexIsRejected()
        {
            var header = new LiteNetFragmentHeader(1, 0, 0, 3, 3, 100, 10);
            var packetLength = LiteNetFragmentCodec.HeaderSize + header.PayloadLength;

            Assert(!LiteNetFragmentCodec.ValidateHeader(header, packetLength, out _), "invalid chunk index accepted");
            Pass();
        }

        private static void OversizedAssemblyIsRejected()
        {
            var header = new LiteNetFragmentHeader(
                1, 0, 0, 0, 1, LiteNetFragmentCodec.MaxReassembledBytes + 1, 10);
            var packetLength = LiteNetFragmentCodec.HeaderSize + header.PayloadLength;

            Assert(!LiteNetFragmentCodec.ValidateHeader(header, packetLength, out _), "oversized assembly accepted");
            Pass();
        }

        private static void PayloadLengthMismatchIsRejected()
        {
            var header = new LiteNetFragmentHeader(1, 0, 0, 0, 1, 100, 10);
            var packetLength = LiteNetFragmentCodec.HeaderSize + header.PayloadLength + 1;

            Assert(!LiteNetFragmentCodec.ValidateHeader(header, packetLength, out _), "payload mismatch accepted");
            Pass();
        }

        private static void Pass() => _passed++;

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"Test failed: {message}");
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!Equals(expected, actual))
                throw new InvalidOperationException($"Test failed: {message}: expected {expected}, got {actual}");
        }
    }
}
