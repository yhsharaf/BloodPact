using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace RoseNet.Session
{
    // One host <-> one guest message pipe. Each Send is delivered (or lost) as one whole message.
    // Everything runs on Unity's main thread from CoopSession.Update.
    internal interface IPeerTransport : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }
        string PeerName { get; }
        void Tick();
        void Send(byte[] data, int length, bool reliable);
        void Poll(Action<byte[], int> onMessage);
    }

    // Plain UDP: 127.0.0.1 for two game copies on one PC, or a Tailscale address between two PCs.
    // No reliability layer; the session re-sends anything important until it is acknowledged.
    internal sealed class UdpPeerTransport : IPeerTransport
    {
        const double PeerTimeoutSeconds = 5;
        readonly bool isHost, allowAny;
        readonly Socket socket;
        readonly byte[] buffer = new byte[65536];
        EndPoint peer;
        long lastReceive;

        public string Name => isHost ? "UDP host" : "UDP guest";
        public bool IsConnected => peer != null && Seconds(Stopwatch.GetTimestamp() - lastReceive) < PeerTimeoutSeconds;
        public string PeerName => peer?.ToString() ?? "none";

        public UdpPeerTransport(bool isHost, string hostAddress, int port, bool allowAny)
        {
            this.isHost = isHost;
            this.allowAny = allowAny;
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true, Blocking = false };
            // Ignore ICMP "port unreachable" resets while the other game is not running yet.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }

            if (isHost)
            {
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            else
            {
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                var ip = IPAddress.TryParse(hostAddress, out var parsed) ? parsed : Dns.GetHostAddresses(hostAddress)[0];
                if (ip.AddressFamily == AddressFamily.InterNetwork) ip = ip.MapToIPv6();
                peer = new IPEndPoint(ip, port);
            }
        }

        public void Tick() { }

        public void Send(byte[] data, int length, bool reliable)
        {
            if (peer == null) return;
            try { socket.SendTo(data, 0, length, SocketFlags.None, peer); } catch (SocketException) { }
        }

        public void Poll(Action<byte[], int> onMessage)
        {
            EndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (true)
            {
                int n;
                try
                {
                    if (socket.Available <= 0) return;
                    n = socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException) { return; }

                var source = (IPEndPoint)from;
                if (!allowAny && !UdpHost.IsTrusted(source.Address)) continue;

                long now = Stopwatch.GetTimestamp();
                if (isHost && (peer == null || (!source.Equals(peer) && !IsConnected)))
                {
                    peer = source;
                    Plugin.Logger.LogInfo($"[Session] Guest reached us from {source}.");
                }
                if (!source.Equals(peer)) continue;
                lastReceive = now;
                onMessage(buffer, n);
            }
        }

        public void Dispose()
        {
            try { socket.Close(); } catch { }
        }

        static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
    }
}
