using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RoseNet.Sender
{
    // Guest side of RoseNet: reads a local XInput controller and streams it to the host's
    // RoseNet plugin over Tailscale.
    //
    //   RoseNetSender <host> [--port 27850] [--pad 1-4]
    //
    // <host> is the host's Tailscale IP (100.x.y.z) or MagicDNS name.
    internal static class Program
    {
        const int PollIntervalMs = 4;          // ~250 Hz polling
        const int SendIntervalMs = 8;          // at most ~125 packets/s while the pad changes
        const int KeepAliveMs = 50;            // 20 packets/s when idle, well under the host's release timeout
        static volatile bool quit;

        static int Main(string[] args)
        {
            Console.Title = "RoseNet Sender";
            string host = null;
            int port = Protocol.DefaultPort;
            int fixedPad = -1;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                    case "--pad" when i + 1 < args.Length: fixedPad = int.Parse(args[++i]) - 1; break;
                    case "-h": case "--help": Usage(); return 0;
                    default: host = args[i]; break;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                Console.Write("Host's Tailscale IP or name (e.g. 100.101.102.103): ");
                host = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(host)) { Usage(); return 1; }
            }

            IPEndPoint target;
            try { target = new IPEndPoint(Resolve(host), port); }
            catch (Exception e) { Console.Error.WriteLine($"Could not find host '{host}': {e.Message}"); return 1; }

            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            // Ignore ICMP "port unreachable" resets while the host's game is not running yet.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
            socket.Connect(target);

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit = true; };
            Console.WriteLine($"Sending controller to {target}. Press Ctrl+C to stop.");
            Console.WriteLine("Keep this window open while you play; it works in the background.");

            var clientId = (uint)Random.Shared.Next(1, int.MaxValue);
            var packet = new byte[Protocol.InputSize];
            var ackBuffer = new byte[64];
            uint seq = 0;
            uint lastPacketNumber = uint.MaxValue;
            var lastSent = Stopwatch.StartNew();
            var lastAck = Stopwatch.StartNew();
            bool everAcked = false;
            byte slot = Protocol.SlotFull;
            int activePad = -2; // forces the first "Using controller" / "No controller" message
            var status = Stopwatch.StartNew();

            while (!quit)
            {
                // Pick the controller: --pad if given, else the first connected one.
                PadInput input = default;
                uint packetNumber = 0;
                int found = -1;
                for (int i = 0; i < 4; i++)
                {
                    int idx = fixedPad >= 0 ? fixedPad : i;
                    if (XInput.TryGetState(idx, out var st)) { found = idx; input = XInput.ToPadInput(st.Gamepad); packetNumber = st.dwPacketNumber; break; }
                    if (fixedPad >= 0) break;
                }
                if (found != activePad)
                {
                    activePad = found;
                    lastPacketNumber = uint.MaxValue;
                    Console.WriteLine();
                    Console.WriteLine(found >= 0 ? $"Using controller {found + 1}." : "No Xbox-compatible controller found. Plug one in (see the guide for PlayStation pads).");
                }

                bool changed = packetNumber != lastPacketNumber;
                long since = lastSent.ElapsedMilliseconds;
                if ((changed && since >= SendIntervalMs) || since >= KeepAliveMs)
                {
                    Protocol.WriteInput(packet, clientId, ++seq, input);
                    try { socket.Send(packet); } catch (SocketException) { }
                    lastPacketNumber = packetNumber;
                    lastSent.Restart();
                }

                // Read acks so the guest can see whether the host is receiving.
                try
                {
                    while (socket.Available > 0)
                    {
                        int n = socket.Receive(ackBuffer);
                        if (Protocol.TryReadAck(ackBuffer.AsSpan(0, n), out var id, out var s) && id == clientId)
                        {
                            if (!everAcked || s != slot)
                            {
                                Console.WriteLine();
                                Console.WriteLine(s == Protocol.SlotFull
                                    ? "Host is full (all remote pads taken)."
                                    : $"Connected! You are remote pad {s + 1}. In the game, press A on the RoseCoop join screen.");
                            }
                            everAcked = true;
                            slot = s;
                            lastAck.Restart();
                        }
                    }
                }
                catch (SocketException) { }

                if (status.ElapsedMilliseconds > 250)
                {
                    status.Restart();
                    string link = !everAcked ? "waiting for host..." : lastAck.ElapsedMilliseconds > 2000 ? "host not answering" : "connected";
                    Console.Write($"\r[{link}] buttons {input.Buttons:X4}  LX {input.LX,6}  LY {input.LY,6}        ");
                }

                Thread.Sleep(PollIntervalMs);
            }

            // Release everything on the host before leaving.
            for (int i = 0; i < 3; i++)
            {
                Protocol.WriteInput(packet, clientId, ++seq, default);
                try { socket.Send(packet); } catch (SocketException) { }
                Thread.Sleep(10);
            }
            Console.WriteLine();
            Console.WriteLine("Stopped.");
            return 0;
        }

        static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            var all = Dns.GetHostAddresses(host);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
        }

        static void Usage()
        {
            Console.WriteLine("RoseNetSender <host Tailscale IP or name> [--port 27850] [--pad 1-4]");
        }
    }
}
