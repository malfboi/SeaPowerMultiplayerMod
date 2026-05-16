using System;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    public readonly struct NetworkDiagnosticsSnapshot
    {
        public readonly long MessagesSent;
        public readonly long PayloadBytesSent;
        public readonly long WireBytesSent;
        public readonly long MessagesReceived;
        public readonly long PayloadBytesReceived;
        public readonly long WireBytesReceived;
        public readonly long FragmentedMessagesSent;
        public readonly long FragmentsSent;
        public readonly long FragmentsReceived;
        public readonly long ReassembledMessagesReceived;
        public readonly long FragmentDrops;
        public readonly long OversizeDrops;
        public readonly int PendingFragments;
        public readonly int LastSentBytes;
        public readonly int LastReceivedBytes;
        public readonly int LastFragmentedBytes;
        public readonly int LastFragmentChunkCount;
        public readonly int LastReassembledBytes;
        public readonly int LastReassembledChunkCount;

        public NetworkDiagnosticsSnapshot(
            long messagesSent, long payloadBytesSent, long wireBytesSent,
            long messagesReceived, long payloadBytesReceived, long wireBytesReceived,
            long fragmentedMessagesSent, long fragmentsSent, long fragmentsReceived,
            long reassembledMessagesReceived, long fragmentDrops, long oversizeDrops,
            int pendingFragments, int lastSentBytes, int lastReceivedBytes,
            int lastFragmentedBytes, int lastFragmentChunkCount,
            int lastReassembledBytes, int lastReassembledChunkCount)
        {
            MessagesSent = messagesSent;
            PayloadBytesSent = payloadBytesSent;
            WireBytesSent = wireBytesSent;
            MessagesReceived = messagesReceived;
            PayloadBytesReceived = payloadBytesReceived;
            WireBytesReceived = wireBytesReceived;
            FragmentedMessagesSent = fragmentedMessagesSent;
            FragmentsSent = fragmentsSent;
            FragmentsReceived = fragmentsReceived;
            ReassembledMessagesReceived = reassembledMessagesReceived;
            FragmentDrops = fragmentDrops;
            OversizeDrops = oversizeDrops;
            PendingFragments = pendingFragments;
            LastSentBytes = lastSentBytes;
            LastReceivedBytes = lastReceivedBytes;
            LastFragmentedBytes = lastFragmentedBytes;
            LastFragmentChunkCount = lastFragmentChunkCount;
            LastReassembledBytes = lastReassembledBytes;
            LastReassembledChunkCount = lastReassembledChunkCount;
        }
    }

    public interface ITransport
    {
        bool IsConnected { get; }
        int RttMs { get; }
        bool LastSendFailed { get; }
        NetworkDiagnosticsSnapshot Diagnostics { get; }

        void Start(bool asHost);
        void Stop();
        void Poll();

        void SendToServer(byte[] data, int length, TransportDelivery delivery);
        void BroadcastToClients(byte[] data, int length, TransportDelivery delivery);

        event Action<byte[], int> OnDataReceived;
        event Action OnPeerConnected;
        event Action OnPeerDisconnected;
    }
}
