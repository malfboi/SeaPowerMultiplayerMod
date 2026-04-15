using System;

namespace SeapowerMultiplayer.Transport
{
    public enum TransportDelivery { Unreliable, Reliable, ReliableOrdered }

    public interface ITransport
    {
        bool IsConnected { get; }
        int RttMs { get; }
        bool LastSendFailed { get; }

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
