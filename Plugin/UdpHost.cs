using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BepInEx.Logging;

namespace RoseNet
{
    // Receives guest controller packets on a background thread and keeps the newest state per pad slot.
    // The main thread reads slots through Snapshot(); everything shared is guarded by `gate`.
    internal sealed class UdpHost
    {
        internal struct SlotState
        {
            public bool Used;          // a guest has connected to this slot at some point
            public uint ClientId;
            public uint LastSeq;
            public long LastPacketTicks;
            public PadInput Input;
            public uint Version;       // bumps on every accepted packet
            public EndPoint Remote;
            public long LastAckTicks;
        }

        readonly int port, maxPads;
        readonly bool allowAny;
        readonly long takeoverTicks;
        readonly ManualLogSource log;
        readonly object gate = new object();
        readonly SlotState[] slots;
        Socket socket;
        Thread thread;
        volatile bool running;

        public UdpHost(int port, int maxPads, bool allowAny, float takeoverSeconds, ManualLogSource log)
        {
            this.port = port;
            this.maxPads = maxPads;
            this.allowAny = allowAny;
            takeoverTicks = (long)(takeoverSeconds * Stopwatch.Frequency);
            this.log = log;
            slots = new SlotState[maxPads];
        }

        public int SlotCount => maxPads;

        public void Start()
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch (Exception e)
            {
                log.LogError($"Could not open UDP port {port}: {e.Message}. Is another program using it?");
                return;
            }

            running = true;
            thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "RoseNet UDP" };
            thread.Start();
            log.LogInfo($"Listening for guests on UDP port {port} (Tailscale only: {!allowAny}). Tailscale IPs of this PC: {DescribeTailscaleAddresses()}");
        }

        public void Stop()
        {
            running = false;
            try { socket?.Close(); } catch { }
        }

        public SlotState Snapshot(int slot)
        {
            lock (gate) return slots[slot];
        }

        void ReceiveLoop()
        {
            var buffer = new byte[256];
            var ack = new byte[Protocol.AckSize];
            EndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);

            while (running)
            {
                int n;
                try { n = socket.ReceiveFrom(buffer, ref from); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; } // ICMP from a closed guest
                catch (Exception) when (!running) { return; }
                catch (Exception e) { log.LogWarning($"UDP receive failed: {e.Message}"); continue; }

                var source = (IPEndPoint)from;
                if (!allowAny && !IsTrusted(source.Address)) continue;
                if (!Protocol.TryReadInput(new ReadOnlySpan<byte>(buffer, 0, n), out var clientId, out var seq, out var input)) continue;

                long now = Stopwatch.GetTimestamp();
                int slot;
                bool sendAck;
                lock (gate)
                {
                    slot = FindOrAssignSlot(clientId, now, source);
                    if (slot < 0)
                    {
                        sendAck = true;
                    }
                    else
                    {
                        ref var s = ref slots[slot];
                        // Drop late/duplicate packets; seq is per client so a restarted sender starts fresh.
                        if (s.Version != 0 && (int)(seq - s.LastSeq) <= 0) continue;
                        s.LastSeq = seq;
                        s.LastPacketTicks = now;
                        s.Input = input;
                        s.Version++;
                        s.Remote = source;
                        sendAck = now - s.LastAckTicks > Stopwatch.Frequency / 2;
                        if (sendAck) s.LastAckTicks = now;
                    }
                }

                if (sendAck)
                {
                    Protocol.WriteAck(ack, clientId, slot < 0 ? Protocol.SlotFull : (byte)slot);
                    try { socket.SendTo(ack, source); } catch { }
                }
            }
        }

        // Caller holds `gate`.
        int FindOrAssignSlot(uint clientId, long now, IPEndPoint source)
        {
            for (int i = 0; i < maxPads; i++)
                if (slots[i].Used && slots[i].ClientId == clientId) return i;

            // New guest: first take over the pad that has been silent longest, so a guest restarting
            // the sender keeps the same pad and with it the same Rose; otherwise use a never-used slot.
            int pick = -1;
            for (int i = 0; i < maxPads; i++)
                if (slots[i].Used && now - slots[i].LastPacketTicks > takeoverTicks
                    && (pick < 0 || slots[i].LastPacketTicks < slots[pick].LastPacketTicks))
                    pick = i;
            for (int i = 0; i < maxPads && pick < 0; i++)
                if (!slots[i].Used) pick = i;
            if (pick < 0) return -1;

            log.LogInfo($"Guest {source.Address} connected as remote pad {pick + 1}.");
            slots[pick] = new SlotState { Used = true, ClientId = clientId, LastPacketTicks = now };
            return pick;
        }

        internal static bool IsTrusted(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            var b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 100 && (b[1] & 0xC0) == 64; // 100.64.0.0/10 (Tailscale CGNAT range)
            return b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0; // fd7a:115c:a1e0::/48
        }

        static string DescribeTailscaleAddresses()
        {
            try
            {
                var found = new System.Collections.Generic.List<string>();
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    {
                        var a = u.Address;
                        if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && IsTrusted(a))
                            found.Add(a.ToString());
                    }
                return found.Count > 0 ? string.Join(", ", found) : "none found (is Tailscale running?)";
            }
            catch { return "unknown"; }
        }
    }
}
