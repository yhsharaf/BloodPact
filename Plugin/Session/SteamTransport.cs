using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoseNet.Session
{
    // Steam P2P through ISteamNetworkingSockets, called through the game's own steam_api64.dll (the game
    // has already initialised Steam). Steam handles NAT punching and falls back to its relay network,
    // so nothing else (Tailscale, port forwarding) is needed.
    //
    // Finding each other: the host publishes rich presence "rosenet" = "host"; the guest scans its
    // friends who are playing this game and connects to the first one hosting (or to SteamHostId).
    // Connection events arrive through a per-connection C callback (config value 201), which Steam
    // calls from RunCallbacks; that avoids depending on the game's own Steam callback dispatcher.
    internal sealed unsafe class SteamTransport : IPeerTransport
    {
        public const uint AppId = 4231820;
        const string RichPresenceKey = "rosenet";

        readonly bool isHost;
        readonly ulong fixedHostId;
        readonly IntPtr sockets, friends, utils;
        readonly ulong selfId;
        readonly StatusChangedFn statusCallback; // kept alive: Steam holds a raw pointer to it
        readonly IntPtr statusCallbackPtr;
        readonly IntPtr[] messageBuffer = new IntPtr[64];
        byte[] receiveCopy = new byte[65536];

        uint listenSocket;
        uint connection;
        bool connected;
        ulong peerId;
        long nextSearch;

        public string Name => isHost ? "Steam host" : "Steam guest";
        public bool IsConnected => connected;
        public string PeerName => peerId == 0 ? "none" : $"{FriendName(peerId)} ({peerId})";

        public SteamTransport(bool isHost, ulong fixedHostId)
        {
            this.isHost = isHost;
            this.fixedHostId = fixedHostId;
            sockets = SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
            friends = SteamAPI_SteamFriends_v017();
            utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
            var user = SteamAPI_SteamUser_v023();
            if (sockets == IntPtr.Zero || friends == IntPtr.Zero || user == IntPtr.Zero)
                throw new InvalidOperationException("Steam is not running or the game did not initialise it.");
            selfId = SteamAPI_ISteamUser_GetSteamID(user);

            statusCallback = OnStatusChanged;
            statusCallbackPtr = Marshal.GetFunctionPointerForDelegate(statusCallback);
            if (utils != IntPtr.Zero) SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(utils);

            if (isHost)
            {
                var opt = CallbackOption();
                listenSocket = SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(sockets, 0, 1, &opt);
                SteamAPI_ISteamFriends_SetRichPresence(friends, RichPresenceKey, "host " + Plugin.Version);
                SteamAPI_ISteamFriends_SetRichPresence(friends, "status", "Hosting Blood Pact co-op");
                Plugin.Logger.LogInfo($"[Session] Steam host ready as {selfId}. Friends playing the game can now join.");
            }
            else
            {
                Plugin.Logger.LogInfo($"[Session] Steam guest {selfId} looking for a host" +
                    (fixedHostId != 0 ? $" ({fixedHostId})." : " among friends playing the game."));
            }
        }

        public void Tick()
        {
            SteamAPI_ISteamNetworkingSockets_RunCallbacks(sockets);
            if (!isHost && connection == 0 && Stopwatch.GetTimestamp() >= nextSearch)
            {
                nextSearch = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
                var host = fixedHostId != 0 ? fixedHostId : FindHostingFriend();
                if (host != 0) Connect(host);
            }
        }

        public void Send(byte[] data, int length, bool reliable)
        {
            if (!connected) return;
            fixed (byte* p = data)
            {
                // Reliable = 8, Unreliable|NoNagle = 1 (NoDelay would drop instead of queueing).
                SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(sockets, connection, p, (uint)length, reliable ? 8 : 1, out _);
            }
        }

        public void Poll(Action<byte[], int> onMessage)
        {
            if (connection == 0) return;
            fixed (IntPtr* msgs = messageBuffer)
            {
                int n;
                while ((n = SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(sockets, connection, msgs, messageBuffer.Length)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        var msg = (NetworkingMessage*)msgs[i];
                        int size = msg->Size;
                        if (size > receiveCopy.Length) receiveCopy = new byte[size];
                        Marshal.Copy(msg->Data, receiveCopy, 0, size);
                        SteamAPI_SteamNetworkingMessage_t_Release(msgs[i]);
                        onMessage(receiveCopy, size);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (connection != 0) SteamAPI_ISteamNetworkingSockets_CloseConnection(sockets, connection, 0, "bye", false);
            if (listenSocket != 0) SteamAPI_ISteamNetworkingSockets_CloseListenSocket(sockets, listenSocket);
            if (isHost)
            {
                SteamAPI_ISteamFriends_SetRichPresence(friends, RichPresenceKey, "");
                SteamAPI_ISteamFriends_SetRichPresence(friends, "status", "");
            }
        }

        void Connect(ulong host)
        {
            var identity = stackalloc byte[IdentitySize];
            new Span<byte>(identity, IdentitySize).Clear();
            SteamAPI_SteamNetworkingIdentity_SetSteamID64((IntPtr)identity, host);
            var opt = CallbackOption();
            connection = SteamAPI_ISteamNetworkingSockets_ConnectP2P(sockets, (IntPtr)identity, 0, 1, &opt);
            peerId = host;
            Plugin.Logger.LogInfo($"[Session] Connecting to {FriendName(host)} over Steam...");
        }

        ulong FindHostingFriend()
        {
            const int Immediate = 4;
            int count = SteamAPI_ISteamFriends_GetFriendCount(friends, Immediate);
            for (int i = 0; i < count; i++)
            {
                ulong id = SteamAPI_ISteamFriends_GetFriendByIndex(friends, i, Immediate);
                FriendGameInfo info;
                if (!SteamAPI_ISteamFriends_GetFriendGamePlayed(friends, id, &info)) continue;
                if ((info.GameId & 0xFFFFFF) != AppId) continue;
                SteamAPI_ISteamFriends_RequestFriendRichPresence(friends, id);
                var rp = Marshal.PtrToStringUTF8(SteamAPI_ISteamFriends_GetFriendRichPresence(friends, id, RichPresenceKey));
                if (rp != null && rp.StartsWith("host")) return id;
            }
            return 0;
        }

        void OnStatusChanged(IntPtr info)
        {
            try
            {
                var change = (StatusChanged*)info;
                uint conn = change->Connection;
                int state = change->State;
                ulong remote = change->IdentityType == IdentityTypeSteamId ? change->RemoteSteamId : 0;

                switch (state)
                {
                    case 1: // Connecting
                        if (!isHost) break;
                        if (connection != 0 && connection != conn)
                        {
                            Plugin.Logger.LogInfo($"[Session] Refused {FriendName(remote)}: a guest is already connected.");
                            SteamAPI_ISteamNetworkingSockets_CloseConnection(sockets, conn, 0, "host full", false);
                        }
                        else if (remote != 0 && SteamAPI_ISteamFriends_GetFriendRelationship(friends, remote) != 3)
                        {
                            Plugin.Logger.LogInfo($"[Session] Refused {remote}: not on your Steam friends list.");
                            SteamAPI_ISteamNetworkingSockets_CloseConnection(sockets, conn, 0, "not a friend", false);
                        }
                        else
                        {
                            connection = conn;
                            peerId = remote;
                            SteamAPI_ISteamNetworkingSockets_AcceptConnection(sockets, conn);
                        }
                        break;
                    case 3: // Connected
                        if (conn != connection) break;
                        connected = true;
                        Plugin.Logger.LogInfo($"[Session] Steam connection to {PeerName} established.");
                        break;
                    case 4: // ClosedByPeer
                    case 5: // ProblemDetectedLocally
                        if (conn == connection)
                        {
                            Plugin.Logger.LogInfo($"[Session] Steam connection to {PeerName} closed ({(state == 4 ? "by the other side" : "connection problem")}).");
                            connection = 0;
                            connected = false;
                            if (isHost) peerId = 0;
                        }
                        SteamAPI_ISteamNetworkingSockets_CloseConnection(sockets, conn, 0, null, false);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[Session] Steam status callback failed: {e}");
            }
        }

        string FriendName(ulong id)
        {
            if (id == 0) return "unknown";
            try { return Marshal.PtrToStringUTF8(SteamAPI_ISteamFriends_GetFriendPersonaName(friends, id)) ?? id.ToString(); }
            catch { return id.ToString(); }
        }

        // Your Steam name, for the name tag the other player sees. Null if Steam is not available.
        public static string PersonaName()
        {
            try
            {
                if (!Steamworks.SteamClient.IsValid) return null;
                var friends = SteamAPI_SteamFriends_v017();
                return friends == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(SteamAPI_ISteamFriends_GetPersonaName(friends));
            }
            catch { return null; }
        }

        ConfigValue CallbackOption() => new ConfigValue { Value = 201, DataType = 5, Ptr = statusCallbackPtr };

        // ---- Steamworks flat API (steam_api64.dll shipped with the game, SDK with Sockets v012) ----

        const int IdentitySize = 136;
        const int IdentityTypeSteamId = 16;

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct ConfigValue
        {
            public int Value;      // ESteamNetworkingConfigValue
            public int DataType;   // ESteamNetworkingConfigDataType (5 = pointer)
            public IntPtr Ptr;
        }

        // SteamNetConnectionStatusChangedCallback_t, only the leading fields we read (pack 8):
        // m_hConn @0, m_info.m_identityRemote.m_eType @8, .m_steamID64 @16, ... m_info.m_eState @?.
        // m_eState sits after identity(136) + nUserData(8) + hListenSocket(4) + addrRemote(18) + pad(2)
        // + idPOPRemote(4) + idPOPRelay(4) = offset 8 + 176 = 184.
        [StructLayout(LayoutKind.Explicit)]
        struct StatusChanged
        {
            [FieldOffset(0)] public uint Connection;
            [FieldOffset(8)] public int IdentityType;
            [FieldOffset(16)] public ulong RemoteSteamId;
            [FieldOffset(184)] public int State;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NetworkingMessage
        {
            public IntPtr Data;
            public int Size;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct FriendGameInfo
        {
            public ulong GameId;
            public uint GameIp;
            public ushort GamePort, QueryPort;
            public ulong LobbyId;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void StatusChangedFn(IntPtr info);

        const string Lib = "steam_api64";

        [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingSockets_SteamAPI_v012();
        [DllImport(Lib)] static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
        [DllImport(Lib)] static extern IntPtr SteamAPI_SteamFriends_v017();
        [DllImport(Lib)] static extern IntPtr SteamAPI_SteamUser_v023();
        [DllImport(Lib)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Lib)] static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
        [DllImport(Lib)] static extern uint SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P(IntPtr self, int localVirtualPort, int nOptions, ConfigValue* options);
        [DllImport(Lib)] static extern uint SteamAPI_ISteamNetworkingSockets_ConnectP2P(IntPtr self, IntPtr identityRemote, int remoteVirtualPort, int nOptions, ConfigValue* options);
        [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingSockets_AcceptConnection(IntPtr self, uint conn);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamNetworkingSockets_CloseConnection(IntPtr self, uint conn, int reason, [MarshalAs(UnmanagedType.LPUTF8Str)] string debug, [MarshalAs(UnmanagedType.U1)] bool linger);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamNetworkingSockets_CloseListenSocket(IntPtr self, uint socket);
        [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingSockets_SendMessageToConnection(IntPtr self, uint conn, void* data, uint size, int flags, out long messageNumber);
        [DllImport(Lib)] static extern int SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection(IntPtr self, uint conn, IntPtr* messages, int max);
        [DllImport(Lib)] static extern void SteamAPI_ISteamNetworkingSockets_RunCallbacks(IntPtr self);
        [DllImport(Lib)] static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr message);
        [DllImport(Lib)] static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(IntPtr identity, ulong steamId);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Lib)] static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Lib)] static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
        [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, FriendGameInfo* info);
        [DllImport(Lib)] static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong friend);
        [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong friend, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong friend);
        [DllImport(Lib)] static extern int SteamAPI_ISteamFriends_GetFriendRelationship(IntPtr self, ulong friend);
        [DllImport(Lib)] static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
    }
}
