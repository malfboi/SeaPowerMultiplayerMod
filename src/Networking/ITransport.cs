using System;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    public interface ITransport
    {
        bool IsConnected { get; }
        int RttMs { get; }

        void Start(bool asHost);
        void Stop();
        void Poll();

        void SendToServer(byte[] data, int length, TransportDelivery delivery);
        void BroadcastToClients(byte[] data, int length, TransportDelivery delivery);
        void SendToClient(int connectionId, byte[] data, int length, TransportDelivery delivery);
        void SendToAllExcept(int excludeConnectionId, byte[] data, int length, TransportDelivery delivery);

        /// <summary>Args: data, length, connectionId (-1 for server/unknown)</summary>
        event Action<byte[], int, int> OnDataReceived;
        /// <summary>Args: connectionId</summary>
        event Action<int> OnPeerConnected;
        /// <summary>Args: connectionId</summary>
        event Action<int> OnPeerDisconnected;
    }
}
