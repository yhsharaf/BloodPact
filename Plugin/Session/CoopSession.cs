using System;
using System.Diagnostics;
using System.IO;
using MotherBase.EntityFramework;
using RoseNet.Game;
using UnityEngine;

namespace RoseNet.Session
{
    internal enum Role { Off, Host, Guest }
    internal enum TransportKind { Steam, Udp }

    internal sealed class SessionSettings
    {
        public Role Role;
        public TransportKind Transport;
        public string UdpHostAddress;
        public int UdpPort;
        public ulong SteamHostId;
        public bool AllowNonTailscale;
        public float SendRate;
        public bool FollowHost;
        public Color RemoteTint = Color.white;
        public bool ShowNames = true;
        public bool ShowStatus = true;
        public bool BossLowHealthKey;   // testing: F9 drops the bosses in our room to 1 HP
        public bool ScaleDifficulty = true;
        public float[] MonsterHealth = { 1f, 1f, 1.3f, 1.5f, 1.6f };   // by player count (index)
        public float[] BossHealth = { 1f, 1f, 1.6f, 2.3f, 2.7f };
    }

    // Milestone 2: each PC plays its own Rose; both stream their Rose (room, position, bone pose and the
    // attack effects it starts) to the other, which shows it as a puppet when both are in the same room.
    // Optionally the guest follows the host between rooms. Enemies, damage and items are not synced yet.
    public class CoopSession : MonoBehaviour
    {
        public CoopSession(IntPtr ptr) : base(ptr) { }

        public const ushort ProtocolVersion = 25;
        const float HelloInterval = 1f, SkeletonRequestInterval = 1f, RemoteStaleSeconds = 2f;
        const float FollowDelay = 1f, TeleportCooldown = 3f, PingInterval = 1f;

        internal static SessionSettings Settings;
        internal static bool IsGuest => Settings != null && Settings.Role == Role.Guest;

        IPeerTransport transport;
        float nextTransportAttempt;
        bool wasConnected, welcomed, refused;

        readonly MessageWriter writer = new MessageWriter();
        readonly PoseState local = new PoseState();
        PoseState remote = new PoseState(), incoming = new PoseState();
        Skeleton localSkeleton;
        Puppet puppet;
        uint seq;
        bool haveRemote;
        float remoteTime, nextSend, nextHello, nextSkeletonRequest, nextPing;
        uint remoteSkeletonHash;
        string[] remoteSkeletonPaths, remoteRendererKeys;
        float puppetCreated, effectCheckAt;
        int puppetRebuilds, effectChecksLeft = 8;
        float mismatchSince = -1, nextTeleport;
        float nextErrorLog;
        readonly System.Collections.Generic.Dictionary<string, (MotherBase.FeedbackManager.FeedbackContainer container, Il2CppSystem.Guid guid)> remoteEffects =
            new System.Collections.Generic.Dictionary<string, (MotherBase.FeedbackManager.FeedbackContainer, Il2CppSystem.Guid)>();
        readonly ProjectileMirror projectiles = new ProjectileMirror();
        readonly System.Collections.Generic.HashSet<string> loggedEffects = new System.Collections.Generic.HashSet<string>();

        void Update()
        {
            // A boss intro held for the other player must always let go, connected or not.
            try { if (!welcomed) BossIntro.Forget(); BossIntro.Tick(); Spectate.Tick(); Victory.Tick(); BossCheat.Tick(Settings != null && Settings.BossLowHealthKey); TakeoverWatch.Tick(); } catch { }
            try { KeepWorldRunning(); } catch { }
            try { DebugReport.Tick(welcomed, remoteName); } catch { }
            try { SelfTest.Tick(); } catch (Exception e) { Plugin.Logger.LogWarning($"[Test] {e.Message}"); }
            if (welcomed && !buildSent) { buildSent = true; try { DebugReport.SendBuild(); } catch { } }
            if (!welcomed) buildSent = false;
            try { Tick(); }
            catch (Exception e)
            {
                if (Time.unscaledTime >= nextErrorLog)
                {
                    nextErrorLog = Time.unscaledTime + 5f;
                    Plugin.Logger.LogError($"[Session] {e}");
                }
            }
        }

        void Tick()
        {
            float now = Time.unscaledTime;
            if (transport == null)
            {
                if (now < nextTransportAttempt) return;
                nextTransportAttempt = now + 2f;
                if (!TryCreateTransport()) return;
            }

            transport.Tick();
            transport.Poll(OnMessage);
            UpdateWindowTitle(now);

            bool connected = transport.IsConnected;
            if (connected != wasConnected)
            {
                wasConnected = connected;
                Plugin.Logger.LogInfo(connected ? $"[Session] Connected to {transport.PeerName}." : "[Session] Disconnected.");
                if (!connected) ResetPeer();
            }

            // UDP has no connection step: the guest's Hello is what lets the host find it.
            if (Settings.Role == Role.Guest && !welcomed && !refused && now >= nextHello)
            {
                nextHello = now + HelloInterval;
                SendHello();
            }
            var character = GameAccess.MainCharacter();
            EffectRelay.Enabled = welcomed;
            EffectRelay.LocalEntity = character != null ? character.Pointer : IntPtr.Zero;
            ProjectileTracker.Enabled = welcomed;
            ProjectileTracker.LocalRose = character;
            if (!welcomed)
            {
                EffectRelay.Outgoing.Clear();
                ProjectileTracker.NewlySpawned.Clear();
                return;
            }
            projectiles.Update();

            SendEffects();
            if (character != null && now >= nextTableSend) SendEffectTables(character, now);
            if (character != null && now >= nextSend)
            {
                nextSend = now + 1f / Settings.SendRate;
                SendState(character);
                SendProjectiles();
                nextPing = now + PingInterval;
            }
            else if (now >= nextPing)
            {
                nextPing = now + PingInterval;
                writer.Begin(MsgType.Ping);
                transport.Send(writer.Buffer, writer.Length, false);
            }
            UpdatePuppet(character, now);
            if (Settings.FollowHost && Settings.Role == Role.Guest && character != null) FollowHost(character, now);
            UpdateMonsters(now);
            SendRest();
            SendWorld();
            SendBoss();
            WatchOwnDeath();
        }

        // ---- pausing: online, like the Souls games, the menu doesn't stop the world ----
        // The game pauses by stopping time. Whoever runs a room's monsters (a boss too) would freeze them for
        // both players, and fights, holds and timers would stall. While a partner is connected, time keeps going
        // (the menu still opens; our own Rose just stands there). Only our "Fight together?" question stops it.
        bool loggedPause, buildSent;

        void KeepWorldRunning()
        {
            if (!welcomed || !haveRemote || BossIntro.Frozen || Time.timeScale > 0.01f) return;
            if (GameAccess.IsLoadingRoom || GameAccess.MainCharacter() == null) return;
            Time.timeScale = 1f;
            if (!loggedPause)
            {
                loggedPause = true;
                Plugin.Logger.LogInfo("[Session] The game tried to stop time (pause menu?); online the world keeps running.");
            }
        }

        // ---- deaths: a dead player's stand-in is no target, and the survivor takes over the room ----
        bool weAreDead, remoteDead;

        void WatchOwnDeath()
        {
            var me = GameAccess.MainCharacter();
            if (me == null) return;
            bool dead;
            try { var d = MobControl.DamageableOf(me); dead = d != null && !d.isAlive; } catch { return; }
            if (dead == weAreDead) return;
            weAreDead = dead;
            var w = writer.Begin(MsgType.PlayerDied);
            w.Write(dead);
            transport.Send(writer.Buffer, writer.Length, true);
            Plugin.Logger.LogInfo(dead ? "[Session] Our Rose died; telling the other player." : "[Session] Our Rose is back.");
            // We ran this room (a boss fight too) and fell: the survivor's game carries on with its monsters, so
            // ours now follows theirs (spectating shows their fight, their hits and the boss's health).
            float now = Time.unscaledTime;
            if (dead && iOwnRoom && !remoteDead && RemoteInMyRoom(now))
            {
                iOwnRoom = false;
                ownerAwaySince = -1;
                lastStreamAt = now;   // give their stream a moment before anyone claims the room again
                roomEnteredAt = now;
                describedMobs.Clear();
                Plugin.Logger.LogInfo("[Session] We fell; the other player's game runs this room's monsters now.");
            }
        }

        void OnRemoteDeath(bool dead)
        {
            remoteDead = dead;
            if (!dead) return;
            Plugin.Logger.LogInfo("[Session] The other player's Rose died.");
            // They ran this room's monsters (a boss fight too): we carry on with them, as they are now.
            if (!iOwnRoom && RemoteInMyRoom(Time.unscaledTime))
            {
                iOwnRoom = true;
                ownerAwaySince = -1;
                describedMobs.Clear();
                mobMirror.Release(healthWasScaled: true);
                Plugin.Logger.LogInfo("[Session] We run this room's monsters now (the other player fell).");
                TakeoverWatch.Begin(mobMirror.Released);
            }
        }

        void SendRest()
        {
            RestSync.Tick();
            RestSync.LateClear();
            var changes = RestSync.Outgoing;
            if (changes.Count == 0 && !RestSync.FirstBatchPending) return;
            bool first = RestSync.FirstBatchPending;
            RestSync.FirstBatchPending = false;
            const int PerMessage = 200;
            int i = 0;
            do
            {
                int n = Math.Min(PerMessage, changes.Count - i);
                var w = writer.Begin(MsgType.RestSync);
                w.Write(first);
                w.Write((ushort)n);
                for (int j = 0; j < n; j++)
                {
                    var c = changes[i + j];
                    w.Write(c.Key ?? "");
                    w.Write(c.Removed);
                    if (!c.Removed) w.Write(c.Value ?? "");
                }
                transport.Send(writer.Buffer, writer.Length, true);
                first = false;
                i += n;
            } while (i < changes.Count);
            changes.Clear();
        }

        // Doors, levers, cranks... that changed in our world go to the other player.
        void SendWorld()
        {
            WorldSync.Tick();
            foreach (var (key, items) in WorldSync.OutgoingLoot)
            {
                var wl = writer.Begin(MsgType.ChestLoot);
                wl.Write(key ?? "");
                wl.Write((byte)items.Length);
                foreach (var id in items) wl.Write(id);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            WorldSync.OutgoingLoot.Clear();
            var list = WorldSync.Outgoing;
            if (list.Count == 0) return;
            foreach (var c in list)
            {
                var w = writer.Begin(MsgType.WorldState);
                w.Write(c.Key ?? "");
                w.Write(c.Kind ?? "");
                w.Write(c.State ?? "");
                transport.Send(writer.Buffer, writer.Length, true);
            }
            list.Clear();
        }

        // Boss encounters we set off pull the other player in; theirs pull us in.
        void SendBoss()
        {
            BossSync.Tick();
            foreach (var s in BossSync.Outgoing)
            {
                var w = writer.Begin(MsgType.BossStart);
                w.Write(s.Room ?? "");
                w.Write(s.BossRoom ?? "");
                w.Write(s.Trigger ?? "");
                w.Write(s.Fight ?? "");
                w.Write(s.BossId);
                w.Write(s.Position.x); w.Write(s.Position.y); w.Write(s.Position.z);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            BossSync.Outgoing.Clear();
            foreach (var c in CharacterSync.Outgoing)
            {
                var w = writer.Begin(MsgType.CharacterStep);
                w.Write(c.Character ?? "");
                w.Write(c.Value);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            CharacterSync.Outgoing.Clear();
            foreach (var room in Victory.Outgoing)
            {
                var w = writer.Begin(MsgType.BossDefeated);
                w.Write(room ?? "");
                transport.Send(writer.Buffer, writer.Length, true);
            }
            Victory.Outgoing.Clear();
            BossIntro.IsHost = Settings.Role == Role.Host;
            if (BossIntro.SendReady)
            {
                BossIntro.SendReady = false;
                var w = writer.Begin(MsgType.BossIntroReady);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            if (BossIntro.SendSkip)
            {
                BossIntro.SendSkip = false;
                var w = writer.Begin(MsgType.IntroSkip);
                transport.Send(writer.Buffer, writer.Length, true);
            }
        }

        // ---- monsters: whoever entered the room first runs them; the other game mirrors them ----

        const float MobSendInterval = 0.05f, OwnerGoneSeconds = 1.5f;
        string trackedRoom;
        bool iOwnRoom = true;
        float nextMobSend, nextPoseSend, ownerAwaySince = -1, roomEnteredAt, lastStreamAt = -100f;
        readonly MobMirror mobMirror = new MobMirror();
        readonly System.Collections.Generic.List<MobState> mobStates = new System.Collections.Generic.List<MobState>();
        readonly System.Collections.Generic.List<(uint id, string name)> mobNames = new System.Collections.Generic.List<(uint, string)>();
        readonly System.Collections.Generic.HashSet<uint> describedMobs = new System.Collections.Generic.HashSet<uint>();

        // The "loading" flag flickers while the game streams neighbouring rooms, so it is not used here.
        bool RemoteInMyRoom(float now) =>
            haveRemote && now - remoteTime < RemoteStaleSeconds
            && !string.IsNullOrEmpty(trackedRoom) && remote.RoomId == trackedRoom;

        void UpdateMonsters(float now)
        {
            string room = GameAccess.CurrentRoomId();
            if (!string.IsNullOrEmpty(room) && room != trackedRoom && !GameAccess.IsLoadingRoom)
            {
                RestSync.OnLeftRoom();
                trackedRoom = room;
                roomEnteredAt = Time.unscaledTime;
                MobRegistry.Prune();
                mobMirror.OnRoomChanged();
                describedMobs.Clear();
                ownerAwaySince = -1;
                // The other player is already here: their game runs this room. Otherwise we claim it.
                iOwnRoom = !RemoteInMyRoom(now);
                if (iOwnRoom)
                {
                    var w = writer.Begin(MsgType.RoomClaim);
                    w.Write(room);
                    transport.Send(writer.Buffer, writer.Length, true);
                }
                Plugin.Logger.LogInfo($"[Session] Entered {room}: {(iOwnRoom ? "we run its monsters" : $"{remoteName} runs its monsters")}.");
            }
            if (string.IsNullOrEmpty(trackedRoom)) return;

            bool together = RemoteInMyRoom(now);
            if (!iOwnRoom)
            {
                // The room's owner must really be gone (another room, or silent) for a moment before we take over.
                if (together) ownerAwaySince = -1;
                else if (ownerAwaySince < 0) ownerAwaySince = now;
                if (ownerAwaySince >= 0 && now - ownerAwaySince > OwnerGoneSeconds)
                {
                    iOwnRoom = true;
                    describedMobs.Clear();
                    mobMirror.Release(healthWasScaled: true);
                    Plugin.Logger.LogInfo("[Session] The other player left the room; we run its monsters now.");
                }
                // We came in at the same moment and each took the other for the owner: nobody runs the room.
                // If no monsters arrive from them, claim it (the host first, so both don't).
                float claimAfter = Settings.Role == Role.Host ? 2f : 4f;
                if (!iOwnRoom && together && now - roomEnteredAt > claimAfter && now - lastStreamAt > claimAfter)
                {
                    iOwnRoom = true;
                    describedMobs.Clear();
                    mobMirror.Release();
                    var w = writer.Begin(MsgType.RoomClaim);
                    w.Write(trackedRoom);
                    transport.Send(writer.Buffer, writer.Length, true);
                    Plugin.Logger.LogInfo("[Session] Nobody was running this room's monsters; we run them now.");
                }
            }

            BossDiag.Tick(trackedRoom, iOwnRoom);
            if (iOwnRoom && together) MobCulling.KeepAnimating(trackedRoom);
            else MobCulling.Restore();
            // Souls-style co-op difficulty: more health while both players are in the room we run.
            if (iOwnRoom && together && Settings.ScaleDifficulty)
                CoopScaling.Apply(trackedRoom, Settings.MonsterHealth[2], Settings.BossHealth[2]);
            else CoopScaling.RevertAll();

            if (iOwnRoom)
            {
                // While our room unloads its monsters vanish; don't let the other side read that as kills.
                bool roomSteady = !GameAccess.IsLoadingRoom && GameAccess.CurrentRoomId() == trackedRoom;
                if (together && roomSteady && now >= nextMobSend)
                {
                    nextMobSend = now + MobSendInterval;
                    SendMonsters();
                }
                if (together && roomSteady && now >= nextPoseSend)
                {
                    nextPoseSend = now + 1f / 20f;
                    var w = writer.Begin(MsgType.MobPoses);
                    MobPoses.Write(w, trackedRoom);
                    transport.Send(writer.Buffer, writer.Length, false);
                }
                else if (!together) describedMobs.Clear(); // describe them again when the other player comes in
            }
            else
            {
                mobMirror.Apply(GameAccess.MainCharacter(), trackedRoom, together);
            }

            // Hits on monsters we don't run go to the room owner.
            if (MobControl.OutgoingHits.Count > 0)
            {
                if (!iOwnRoom)
                {
                    var w = writer.Begin(MsgType.MobHit);
                    w.Write(trackedRoom);
                    w.Write((byte)Math.Min(MobControl.OutgoingHits.Count, 255));
                    for (int i = 0; i < MobControl.OutgoingHits.Count && i < 255; i++)
                    {
                        w.Write(MobControl.OutgoingHits[i].id);
                        w.Write(MobControl.OutgoingHits[i].damage);
                    }
                    transport.Send(writer.Buffer, writer.Length, true);
                }
                MobControl.OutgoingHits.Clear();
            }
        }

        float nextDescribeAll;
        int hitLogs = 10;

        void SendMonsters()
        {
            // UDP can drop even "reliable" sends, so all types are re-sent every few seconds.
            if (Time.unscaledTime >= nextDescribeAll) { nextDescribeAll = Time.unscaledTime + 2f; describedMobs.Clear(); }
            MobOwner.Collect(mobStates, mobNames, trackedRoom);
            // Types first (reliable), only for monsters the other side hasn't been told about.
            int fresh = 0;
            foreach (var (id, _) in mobNames) if (!describedMobs.Contains(id)) fresh++;
            if (fresh > 0)
            {
                var wi = writer.Begin(MsgType.MobInfo);
                wi.Write(trackedRoom);
                wi.Write((byte)Math.Min(fresh, 255));
                int written = 0;
                foreach (var (id, name) in mobNames)
                {
                    if (describedMobs.Contains(id) || written >= 255) continue;
                    wi.Write(id);
                    wi.Write(name);
                    describedMobs.Add(id);
                    written++;
                }
                transport.Send(writer.Buffer, writer.Length, true);
            }

            var w = writer.Begin(MsgType.MobStates);
            w.Write(trackedRoom);
            w.Write((byte)mobStates.Count);
            foreach (var s in mobStates)
            {
                w.Write(s.Id);
                w.Write(s.Position.x); w.Write(s.Position.y); w.Write(s.Position.z);
                w.Write(PackQ(s.Rotation.x)); w.Write(PackQ(s.Rotation.y)); w.Write(PackQ(s.Rotation.z)); w.Write(PackQ(s.Rotation.w));
                w.Write((sbyte)(s.Scale.x < 0 ? -1 : 1));
                w.Write((byte)s.ActionCount);
                for (int i = 0; i < s.ActionCount; i++) { w.Write(s.ActionAt(i)); w.Write((ushort)Mathf.Clamp(s.FrameAt(i), 0, 65535)); }
                w.Write(s.Health);
                w.Write(s.MaxHealth);
                w.Write(s.Target);
            }
            transport.Send(writer.Buffer, writer.Length, false);
        }

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        void ReceiveMonsters(BinaryReader r)
        {
            string room = r.ReadString();
            int n = r.ReadByte();
            mobStates.Clear();
            for (int i = 0; i < n; i++)
            {
                var s = new MobState { Id = r.ReadUInt32() };
                s.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.Rotation = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
                s.Scale = new Vector3(r.ReadSByte(), 1, 1);
                int ac = r.ReadByte();
                for (int a = 0; a < ac; a++) { uint h = r.ReadUInt32(); int f = r.ReadUInt16(); if (a < 3) s.SetAction(s.ActionCount++, h, f); }
                s.Health = r.ReadInt32();
                s.MaxHealth = r.ReadInt32();
                s.Target = r.ReadByte();
                mobStates.Add(s);
            }
            if (room == trackedRoom) lastStreamAt = Time.unscaledTime;
            if (room == trackedRoom && !iOwnRoom) mobMirror.Receive(mobStates);
        }

        static short PackQ(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);

        bool TryCreateTransport()
        {
            try
            {
                if (Settings.Transport == TransportKind.Udp)
                {
                    transport = new UdpPeerTransport(Settings.Role == Role.Host, Settings.UdpHostAddress, Settings.UdpPort, Settings.AllowNonTailscale);
                }
                else
                {
                    if (!Steamworks.SteamClient.IsValid) return false; // the game has not started Steam yet
                    transport = new SteamTransport(Settings.Role == Role.Host, Settings.SteamHostId);
                }
                Plugin.Logger.LogInfo($"[Session] {transport.Name} started" +
                    (Settings.Transport == TransportKind.Udp ? $" ({(Settings.Role == Role.Host ? "listening on" : "sending to")} {(Settings.Role == Role.Host ? "" : Settings.UdpHostAddress + ":")}{Settings.UdpPort})." : "."));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[Session] Could not start {Settings.Transport} transport: {e.Message}");
                transport = null;
                nextTransportAttempt = Time.unscaledTime + 10f;
                return false;
            }
        }

        void ResetPeer()
        {
            welcomed = false;
            haveRemote = false;
            remoteSkeletonHash = 0;
            remoteSkeletonPaths = null;
            remoteRendererKeys = null;
            remoteTables = null;
            lastTables = "";
            remoteEffects.Clear();
            projectiles.Clear();
            ProjectileTracker.Reset();
            mobMirror.OnRoomChanged();
            describedMobs.Clear();
            iOwnRoom = true;
            trackedRoom = null;
            foreach (var s in snapshots) spare.Push(s);
            snapshots.Clear();
            haveOffset = false;
            puppetRebuilds = 0;
            puppet?.Destroy();
            puppet = null;
        }

        // ---- sending ----

        void SendHello()
        {
            var w = writer.Begin(MsgType.Hello);
            w.Write(ProtocolVersion);
            w.Write(Application.version ?? "");
            w.Write(LocalName());
            transport.Send(writer.Buffer, writer.Length, true);
        }

        void SendWelcome(bool accepted, string reason)
        {
            var w = writer.Begin(MsgType.Welcome);
            w.Write(accepted);
            w.Write(reason ?? "");
            w.Write(LocalName());
            transport.Send(writer.Buffer, writer.Length, true);
        }

        string LocalName()
        {
            string n = Settings.Transport == TransportKind.Steam ? SteamTransport.PersonaName() : null;
            return string.IsNullOrEmpty(n) ? (Environment.UserName ?? "Player") : n;
        }

        // ---- on-screen: name over the other Rose, a small status line, and the window title ----

        string remoteName = "";
        GUIStyle nameStyle, nameShadow, statusStyle, noteStyle;
        float nextTitle;
        bool guiFailed;

        void OnGUI()
        {
            if (Settings == null || guiFailed) return;
            try
            {
                if (nameStyle == null)
                {
                    nameStyle = MakeStyle(18, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.75f, 0.88f, 1f));
                    nameShadow = MakeStyle(18, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0, 0, 0, 0.8f));
                    statusStyle = MakeStyle(13, FontStyle.Normal, TextAnchor.UpperLeft, new Color(1, 1, 1, 0.7f));
                }

                if (Settings.ShowStatus)
                    GUI.Label(new Rect(10, 4, 700, 22), StatusText(), statusStyle);
                string warn = welcomed ? DebugReport.BuildWarning(remoteName) : null;
                string note = DebugReport.Notice;
                if (warn != null || note != null)
                {
                    if (noteStyle == null) { noteStyle = MakeStyle(14, FontStyle.Bold, TextAnchor.UpperLeft, new Color(1f, 0.8f, 0.3f)); noteStyle.wordWrap = true; }
                    float y = 44;
                    if (warn != null) { GUI.Label(new Rect(10, y, Screen.width - 20, 40), warn, noteStyle); y += 22; }
                    if (note != null) GUI.Label(new Rect(10, y, Screen.width - 20, 40), note, noteStyle);
                }

                if (welcomed)
                {
                    Vector3? partnerHead = null;
                    if (puppet != null && puppet.TryGetHeadPosition(out var h)) partnerHead = h;
                    var mark = Settings.RemoteTint == Color.white ? new Color(0.75f, 0.88f, 1f) : Settings.RemoteTint;
                    PartnerMap.Draw(haveRemote && remote != null, remote != null ? remote.EntityPosition : Vector3.zero, remote?.RoomId, remoteName, mark, partnerHead);
                }

                var banner = BossIntro.Banner;
                if (!string.IsNullOrEmpty(banner))
                {
                    var r = new Rect(0, Screen.height * 0.18f, Screen.width, 30);
                    GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), banner, nameShadow);
                    GUI.Label(r, banner, nameStyle);
                }

                if (Settings.ShowNames && puppet != null && puppet.TryGetHeadPosition(out var head))
                {
                    var cam = Camera.main;
                    if (cam != null)
                    {
                        var p = cam.WorldToScreenPoint(head);
                        if (p.z > 0)
                        {
                            string label = string.IsNullOrEmpty(remoteName) ? "Player 2" : remoteName;
                            var rect = new Rect(p.x - 150, Screen.height - p.y - 14, 300, 28);
                            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), label, nameShadow);
                            GUI.Label(rect, label, nameStyle);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                guiFailed = true;
                Plugin.Logger.LogWarning($"[Session] On-screen labels turned off: {e.Message}");
            }
        }

        static GUIStyle MakeStyle(int size, FontStyle fontStyle, TextAnchor anchor, Color color)
        {
            var s = new GUIStyle();
            s.font = GUI.skin.label.font;
            s.fontSize = size;
            s.fontStyle = fontStyle;
            s.alignment = anchor;
            s.normal.textColor = color;
            return s;
        }

        string StatusText()
        {
            string role = Settings.Role == Role.Host ? "Host" : "Guest";
            if (transport == null) return $"Blood Pact {role}: starting...";
            if (refused) return $"Blood Pact {role}: refused by host (see log)";
            if (!welcomed) return Settings.Role == Role.Host ? $"Blood Pact Host: waiting for a guest" : "Blood Pact Guest: looking for the host...";
            string where = haveRemote && puppet != null && !puppet.Visible ? " (in another room)" : "";
            return $"Blood Pact {role}: playing with {(string.IsNullOrEmpty(remoteName) ? "Player 2" : remoteName)}{where}   [{Plugin.Version} {DebugReport.BuildId}, F10 = bug report]";
        }

        void UpdateWindowTitle(float now)
        {
            if (now < nextTitle) return;
            nextTitle = now + 5f;
            try
            {
                var handle = Process.GetCurrentProcess().MainWindowHandle;
                if (handle == IntPtr.Zero) return;
                string role = Settings.Role == Role.Host ? "HOST" : "GUEST";
                string with = welcomed && !string.IsNullOrEmpty(remoteName) ? $" with {remoteName}" : "";
                SetWindowTextW(handle, $"Castlevania Belmont's Curse - Blood Pact {role}{with}");
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool SetWindowTextW(IntPtr hWnd, string text);

        void SendState(Entity character)
        {
            var visual = GameAccess.VisualRoot(character);
            if (localSkeleton == null || localSkeleton.Root != visual || localSkeleton.Entity != character.transform)
            {
                localSkeleton = Skeleton.Capture(character.transform, visual);
                Plugin.Logger.LogInfo($"[Session] Local Rose skeleton: {localSkeleton.Bones.Length} bones under '{visual.name}' (hash {localSkeleton.Hash:X8}).");
            }
            localSkeleton.Write(local);
            local.Seq = ++seq;
            local.SenderTime = Time.unscaledTime;
            local.RoomId = GameAccess.CurrentRoomId() ?? "";
            local.Loading = GameAccess.IsLoadingRoom;
            var w = writer.Begin(MsgType.State);
            MessageWriter.WriteState(w, local);
            transport.Send(writer.Buffer, writer.Length, false);
        }

        void SendEffects()
        {
            // Projectiles first, so effects started on a brand-new projectile find it on the other side.
            foreach (var p in ProjectileTracker.NewlySpawned)
            {
                if (p.T == null) continue;
                var w = writer.Begin(MsgType.ProjSpawn);
                w.Write(p.Id);
                w.Write(p.Name);
                WritePose(w, p.T);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            ProjectileTracker.NewlySpawned.Clear();

            foreach (var e in EffectRelay.Outgoing)
            {
                var w = writer.Begin(e.Start ? MsgType.FxStart : MsgType.FxStop);
                w.Write(e.Target);
                if (e.Start) w.Write(e.Name ?? "");
                w.Write(e.Id ?? "");
                if (!e.Start) w.Write(e.Force);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            EffectRelay.Outgoing.Clear();
        }

        readonly System.Collections.Generic.List<(ushort id, Transform t)> liveProjectiles = new System.Collections.Generic.List<(ushort, Transform)>();

        void SendProjectiles()
        {
            ProjectileTracker.Collect(liveProjectiles);
            if (liveProjectiles.Count > 0)
            {
                var w = writer.Begin(MsgType.ProjMove);
                int n = Math.Min(liveProjectiles.Count, 255);
                w.Write((byte)n);
                for (int i = 0; i < n; i++)
                {
                    w.Write(liveProjectiles[i].id);
                    WritePose(w, liveProjectiles[i].t);
                }
                transport.Send(writer.Buffer, writer.Length, false);
            }
            foreach (var id in ProjectileTracker.Ended)
            {
                var w = writer.Begin(MsgType.ProjEnd);
                w.Write(id);
                transport.Send(writer.Buffer, writer.Length, true);
            }
            ProjectileTracker.Ended.Clear();
        }

        static void WritePose(BinaryWriter w, Transform t)
        {
            var p = t.position; var q = t.rotation; var s = t.lossyScale;
            w.Write(p.x); w.Write(p.y); w.Write(p.z);
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
            w.Write(s.x); w.Write(s.y); w.Write(s.z);
        }

        static void ReadPose(BinaryReader r, out Vector3 p, out Quaternion q, out Vector3 s)
        {
            p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        // Sent when the list changes (weapon swap) and every few seconds anyway, since UDP may drop it.
        float nextTableSend, lastTableResend;
        string lastTables = "";
        System.Collections.Generic.List<string> remoteTables;

        void SendEffectTables(Entity character, float now)
        {
            nextTableSend = now + 1f;
            var names = EffectRelay.TableNames(character);
            string joined = string.Join("|", names);
            if (joined == lastTables && now - lastTableResend < 5f) return;
            lastTables = joined;
            lastTableResend = now;
            var w = writer.Begin(MsgType.VfxTables);
            w.Write((byte)Math.Min(names.Count, 255));
            for (int i = 0; i < names.Count && i < 255; i++) w.Write(names[i]);
            transport.Send(writer.Buffer, writer.Length, true);
        }

        void SendSkeleton()
        {
            if (localSkeleton == null) return;
            var w = writer.Begin(MsgType.Skeleton);
            w.Write(localSkeleton.Hash);
            w.Write((ushort)localSkeleton.Paths.Length);
            foreach (var p in localSkeleton.Paths) w.Write(p);
            w.Write((ushort)localSkeleton.RendererKeys.Length);
            foreach (var k in localSkeleton.RendererKeys) w.Write(k);
            transport.Send(writer.Buffer, writer.Length, true);
        }

        // ---- receiving ----

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        void OnMessage(byte[] data, int length)
        {
            if (length < 1) return;
            using var r = new BinaryReader(new MemoryStream(data, 1, length - 1, false));
            switch ((MsgType)data[0])
            {
                case MsgType.Hello when Settings.Role == Role.Host:
                {
                    ushort protocol = r.ReadUInt16();
                    string gameVersion = r.ReadString();
                    string name = r.ReadString();
                    string reason = protocol != ProtocolVersion ? $"Blood Pact versions differ (host {ProtocolVersion}, guest {protocol}). Update both."
                        : gameVersion != Application.version ? $"Game versions differ (host {Application.version}, guest {gameVersion}). Update both in Steam."
                        : null;
                    SendWelcome(reason == null, reason);
                    if (reason != null) { Plugin.Logger.LogWarning($"[Session] Refused {name}: {reason}"); break; }
                    if (!welcomed) Plugin.Logger.LogInfo($"[Session] {name} joined (game {gameVersion}).");
                    welcomed = true;
                    remoteName = name;
                    break;
                }
                case MsgType.Welcome when Settings.Role == Role.Guest:
                {
                    bool accepted = r.ReadBoolean();
                    string reason = r.ReadString();
                    remoteName = r.ReadString();
                    if (accepted)
                    {
                        if (!welcomed) Plugin.Logger.LogInfo("[Session] Host accepted us." + (Settings.FollowHost ? " Your game will follow the host's room." : ""));
                        welcomed = true;
                    }
                    else if (!refused)
                    {
                        refused = true;
                        Plugin.Logger.LogError($"[Session] Host refused the connection: {reason}");
                    }
                    break;
                }
                case MsgType.BuildInfo when welcomed:
                    DebugReport.OnBuild(r, remoteName);
                    break;
                case MsgType.LogRequest when welcomed:
                    DebugReport.OnRequest(r, remoteName);
                    break;
                case MsgType.LogChunk when welcomed:
                    DebugReport.OnChunk(r, remoteName);
                    break;
                case MsgType.State when welcomed:
                {
                    // Read into a scratch copy first so a late packet cannot overwrite a newer pose.
                    MessageReader.ReadState(r, incoming);
                    if (haveRemote && (int)(incoming.Seq - remote.Seq) <= 0 && incoming.Seq != 1) return; // stale (seq restarts at 1)
                    (remote, incoming) = (incoming, remote);
                    haveRemote = true;
                    remoteTime = Time.unscaledTime;
                    Buffer(remote);
                    if (remote.SkeletonHash != remoteSkeletonHash && Time.unscaledTime >= nextSkeletonRequest)
                    {
                        nextSkeletonRequest = Time.unscaledTime + SkeletonRequestInterval;
                        var w = writer.Begin(MsgType.SkeletonRequest);
                        w.Write(remote.SkeletonHash);
                        transport.Send(writer.Buffer, writer.Length, true);
                    }
                    break;
                }
                case MsgType.SkeletonRequest when welcomed:
                    if (localSkeleton != null && r.ReadUInt32() == localSkeleton.Hash) SendSkeleton();
                    break;
                case MsgType.Skeleton when welcomed:
                {
                    uint hash = r.ReadUInt32();
                    int n = r.ReadUInt16();
                    var paths = new string[n];
                    for (int i = 0; i < n; i++) paths[i] = r.ReadString();
                    int rn = r.ReadUInt16();
                    var keys = new string[rn];
                    for (int i = 0; i < rn; i++) keys[i] = r.ReadString();
                    if (Skeleton.HashLists(paths, keys) != hash) { Plugin.Logger.LogWarning("[Session] Received a damaged skeleton list; asking again."); break; }
                    remoteSkeletonHash = hash;
                    remoteSkeletonPaths = paths;
                    remoteRendererKeys = keys;
                    puppet?.Bind(hash, paths, keys);
                    break;
                }
                case MsgType.FxStart when welcomed:
                {
                    ushort target = r.ReadUInt16();
                    string name = r.ReadString();
                    string id = r.ReadString();
                    if (puppet == null || !puppet.Visible || AffectsOwnScreen(name)) break;
                    var feedback = EffectRelay.Find(name);
                    if (feedback == null)
                    {
                        if (loggedEffects.Add("missing:" + name)) Plugin.Logger.LogWarning($"[Session] Effect '{name}' is not loaded on this PC; skipped.");
                        break;
                    }
                    var container = target == 0 ? puppet.Effects : projectiles.EffectsOf(target);
                    Il2CppSystem.Guid? started = container != null ? EffectRelay.StartBodyOnly(container, feedback) : (Il2CppSystem.Guid?)null;
                    if (started.HasValue) remoteEffects[id] = (container, started.Value);
                    if (started.HasValue && name.Contains("Attack")) LastRemoteAttack = name;
                    if (loggedEffects.Count < 20 && loggedEffects.Add(name))
                    {
                        Plugin.Logger.LogInfo($"[Session] Playing the other Rose's effect '{name}' ({(started.HasValue ? "started" : "puppet has no effect player")}).");
                        if (started.HasValue && effectChecksLeft > 0) effectCheckAt = Time.unscaledTime + 0.25f;
                    }
                    break;
                }
                case MsgType.FxStop when welcomed:
                {
                    r.ReadUInt16(); // target; the effect id alone identifies it
                    string id = r.ReadString();
                    bool force = r.ReadBoolean();
                    if (remoteEffects.TryGetValue(id, out var local))
                    {
                        remoteEffects.Remove(id);
                        try
                        {
                            if (local.container != null)
                            {
                                if (force) local.container.ForceStopFeedback(local.guid); else local.container.StopFeedback(local.guid);
                            }
                        }
                        catch { } // the projectile copy may already be gone
                    }
                    break;
                }
                case MsgType.ProjSpawn when welcomed:
                {
                    ushort id = r.ReadUInt16();
                    string name = r.ReadString();
                    ReadPose(r, out var p, out var q, out var s);
                    if (puppet != null && puppet.Visible) projectiles.Spawn(id, name, p, q, s);
                    break;
                }
                case MsgType.ProjMove when welcomed:
                {
                    int n = r.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        ushort id = r.ReadUInt16();
                        ReadPose(r, out var p, out var q, out var s);
                        projectiles.Move(id, p, q, s);
                    }
                    break;
                }
                case MsgType.ProjEnd when welcomed:
                    projectiles.End(r.ReadUInt16());
                    break;
                case MsgType.VfxTables when welcomed:
                {
                    int n = r.ReadByte();
                    var names = new System.Collections.Generic.List<string>(n);
                    for (int i = 0; i < n; i++) names.Add(r.ReadString());
                    remoteTables = names;
                    puppet?.SyncEffectTables(remoteTables);
                    break;
                }
                case MsgType.RoomClaim when welcomed:
                {
                    string room = r.ReadString();
                    // Both arrived at the same moment and both claimed: the host keeps it.
                    if (room == trackedRoom && iOwnRoom && Settings.Role == Role.Guest)
                    {
                        iOwnRoom = false;
                        Plugin.Logger.LogInfo($"[Session] {remoteName} also claimed this room; the host runs its monsters.");
                    }
                    break;
                }
                case MsgType.MobStates when welcomed:
                    ReceiveMonsters(r);
                    break;
                case MsgType.MobPoses when welcomed:
                {
                    var poses = new System.Collections.Generic.Dictionary<uint, MobPose>();
                    MobPoses.Read(r, out var poseRoom, poses);
                    if (poseRoom == trackedRoom && !iOwnRoom)
                        mobMirror.ReceivePoses(poses);
                    break;
                }
                case MsgType.MobHit when welcomed:
                {
                    string room = r.ReadString();
                    int n = r.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        uint id = r.ReadUInt32();
                        int dmg = r.ReadInt32();
                        if (room == trackedRoom && iOwnRoom) MobDamage.ApplyRemoteHit(id, dmg, GameAccess.MainCharacter());
                    }
                    if (hitLogs-- > 0) Plugin.Logger.LogInfo($"[Session] {remoteName} hit {n} of our monsters.");
                    break;
                }
                case MsgType.CharacterStep when welcomed:
                    CharacterSync.Apply(new CharacterSync.Step { Character = r.ReadString(), Value = r.ReadInt32() });
                    break;
                case MsgType.BossIntroReady when welcomed:
                    BossIntro.OnOtherReady();
                    break;
                case MsgType.IntroSkip when welcomed:
                    BossIntro.OnOtherSkipped();
                    break;
                case MsgType.PlayerDied when welcomed:
                    OnRemoteDeath(r.ReadBoolean());
                    break;
                case MsgType.BossDefeated when welcomed:
                    Victory.OnRemote(r.ReadString());
                    break;
                case MsgType.BossStart when welcomed:
                    BossSync.OnRemoteStart(new BossSync.Start { Room = r.ReadString(), BossRoom = r.ReadString(), Trigger = r.ReadString(), Fight = r.ReadString(), BossId = r.ReadUInt32(), Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) });
                    break;
                case MsgType.ChestLoot when welcomed:
                {
                    string key = r.ReadString();
                    var ids = new string[r.ReadByte()];
                    for (int i = 0; i < ids.Length; i++) ids[i] = r.ReadString();
                    WorldSync.OnLoot(key, ids);
                    break;
                }
                case MsgType.WorldState when welcomed:
                    WorldSync.Apply(new WorldSync.Change { Key = r.ReadString(), Kind = r.ReadString(), State = r.ReadString() });
                    break;
                case MsgType.RestSync when welcomed:
                {
                    bool first = r.ReadBoolean();
                    int n = r.ReadUInt16();
                    var list = new System.Collections.Generic.List<RestSync.Change>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var c = new RestSync.Change { Key = r.ReadString(), Removed = r.ReadBoolean() };
                        if (!c.Removed) c.Value = r.ReadString();
                        list.Add(c);
                    }
                    RestSync.Apply(list, first);
                    break;
                }
                case MsgType.MobInfo when welcomed:
                {
                    string room = r.ReadString();
                    int n = r.ReadByte();
                    mobNames.Clear();
                    for (int i = 0; i < n; i++) mobNames.Add((r.ReadUInt32(), r.ReadString()));
                    if (room == trackedRoom) mobMirror.ReceiveNames(mobNames);
                    break;
                }
                case MsgType.Bye:
                    Plugin.Logger.LogInfo("[Session] The other player left.");
                    ResetPeer();
                    break;
            }
        }

        // Effects that are about the player's own state (screen tint, HUD, pickups, save points) would
        // land on the receiver's screen as if they were theirs; only visual effects of the body are mirrored.
        static readonly string[] OwnScreenEffects =
        {
            "LowLife", "GainXP", "FullMana", "PickMana", "PickHealth", "PickGold", "Interactible", "UseSavePoint",
            "LevelUp", "Death", "GameOver", "Heal",
        };

        static bool AffectsOwnScreen(string effect)
        {
            foreach (var s in OwnScreenEffects)
                if (effect.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ---- remote Rose ----

        void UpdatePuppet(Entity character, float now)
        {
            bool fresh = haveRemote && now - remoteTime < RemoteStaleSeconds;
            if (!fresh || character == null)
            {
                puppet?.SetVisible(false);
                return;
            }

            // Our own Rose can be a placeholder while a save loads; rebuild a puppet that came out without
            // the effect player a few times before giving up on effects.
            if (puppet != null && puppet.Alive && !puppet.HasEffects && puppetRebuilds < 3 && now - puppetCreated > 3f)
            {
                puppetRebuilds++;
                puppet.Destroy();
                puppet = null;
            }
            if (puppet == null || !puppet.Alive)
            {
                // Only copy a fully loaded Rose: while a save loads, the main character can be a placeholder,
                // and a puppet cloned from it never plays effects correctly (seen on the guest).
                if (GameAccess.IsLoadingRoom || string.IsNullOrEmpty(GameAccess.CurrentRoomId())
                    || character.GetComponentInChildren<MotherBase.FeedbackManager.FeedbackContainer>(true) == null
                    || character.GetComponentInChildren<VFXBindingsComponent>(true) == null)
                    return;
                puppet = Puppet.Create(character.transform, GameAccess.VisualRoot(character));
                puppetCreated = now;
                if (remoteSkeletonPaths != null) puppet.Bind(remoteSkeletonHash, remoteSkeletonPaths, remoteRendererKeys);
                if (remoteTables != null) puppet.SyncEffectTables(remoteTables);
                if (Settings.RemoteTint != Color.white) puppet.ApplyTint(Settings.RemoteTint);
            }

            string localRoom = GameAccess.CurrentRoomId();
            bool sameRoom = !remote.Loading && !string.IsNullOrEmpty(remote.RoomId) && remote.RoomId == localRoom;
            bool ready = puppet.HasSkeleton(remote.SkeletonHash);
            puppet.SetVisible(sameRoom && ready);
            if (sameRoom && ready && Interpolate(now)) puppet.Apply(display);

            if (effectCheckAt > 0 && now >= effectCheckAt)
            {
                effectCheckAt = 0;
                effectChecksLeft--;
                Plugin.Logger.LogInfo($"[Session] Effect check: {puppet.DescribeEffects()}");
            }
            CompareAttackEffects(character, now);
        }

        // Diagnostics for the sword slash: the same attack effect on our own Rose and on the puppet,
        // feature by feature, a few frames into the swing.
        // (The puppet shows the OTHER player's attacks, so "own" and "puppet" lines come from different PCs.)
        float ownCompareAt, puppetCompareAt;
        int ownComparesLeft = 4, puppetComparesLeft = 4;
        string ownCompareEffect, puppetCompareEffect;
        internal string LastRemoteAttack;

        void CompareAttackEffects(Entity character, float now)
        {
            if (ownCompareAt <= 0 && ownComparesLeft > 0 && EffectRelay.LastLocalAttack != null)
            {
                ownCompareEffect = EffectRelay.LastLocalAttack;
                ownCompareAt = now + 0.2f;
            }
            EffectRelay.LastLocalAttack = null;
            if (ownCompareAt > 0 && now >= ownCompareAt)
            {
                ownCompareAt = 0;
                ownComparesLeft--;
                var mine = character != null ? character.GetComponentInChildren<MotherBase.FeedbackManager.FeedbackContainer>(true) : null;
                Plugin.Logger.LogInfo($"[Session] Slash compare, own Rose: {Puppet.DescribeNewest(mine, ownCompareEffect)}");
            }

            if (puppetCompareAt <= 0 && puppetComparesLeft > 0 && LastRemoteAttack != null)
            {
                puppetCompareEffect = LastRemoteAttack;
                puppetCompareAt = now + 0.2f;
            }
            LastRemoteAttack = null;
            if (puppetCompareAt > 0 && now >= puppetCompareAt && puppet != null)
            {
                puppetCompareAt = 0;
                puppetComparesLeft--;
                Plugin.Logger.LogInfo($"[Session] Slash compare, puppet:   {Puppet.DescribeNewest(puppet.Effects, puppetCompareEffect)}");
            }
        }

        // ---- smoothing: draw the other Rose slightly in the past, blending between received snapshots ----

        const float InterpolationDelay = 0.12f;
        const int BufferSize = 32;
        readonly System.Collections.Generic.List<PoseState> snapshots = new System.Collections.Generic.List<PoseState>();
        readonly System.Collections.Generic.Stack<PoseState> spare = new System.Collections.Generic.Stack<PoseState>();
        readonly PoseState display = new PoseState();
        float clockOffset;   // local time minus sender time, the smallest seen recently (least delayed packet)
        bool haveOffset;

        void Buffer(PoseState s)
        {
            float offset = Time.unscaledTime - s.SenderTime;
            // Follow decreases at once, increases slowly: spikes of lag must not drag the timeline back.
            clockOffset = !haveOffset || offset < clockOffset ? offset : clockOffset + (offset - clockOffset) * 0.05f;
            haveOffset = true;

            var copy = spare.Count > 0 ? spare.Pop() : new PoseState();
            copy.CopyFrom(s);
            int i = snapshots.Count;
            while (i > 0 && snapshots[i - 1].SenderTime > copy.SenderTime) i--;
            snapshots.Insert(i, copy);
            while (snapshots.Count > BufferSize) { spare.Push(snapshots[0]); snapshots.RemoveAt(0); }
        }

        bool Interpolate(float now)
        {
            if (snapshots.Count == 0) return false;
            float renderTime = now - clockOffset - InterpolationDelay;

            // Drop snapshots that are entirely in the past (keep one before renderTime to blend from).
            while (snapshots.Count > 2 && snapshots[1].SenderTime <= renderTime) { spare.Push(snapshots[0]); snapshots.RemoveAt(0); }

            var a = snapshots[0];
            if (snapshots.Count == 1 || renderTime <= a.SenderTime) { display.CopyFrom(a); return true; }
            var b = snapshots[1];
            if (renderTime >= b.SenderTime) { display.CopyFrom(snapshots[snapshots.Count - 1]); return true; }

            float t = (renderTime - a.SenderTime) / Mathf.Max(1e-4f, b.SenderTime - a.SenderTime);
            // Teleports, respawns and room changes snap instead of sliding across the screen.
            if ((b.EntityPosition - a.EntityPosition).sqrMagnitude > 25f || a.RoomId != b.RoomId) t = 1f;
            display.Blend(a, b, t);
            return true;
        }

        // Guest only: when the host is in another room for a moment, move our Rose to the host's position;
        // the game's room streaming then loads the host's room.
        void FollowHost(Entity character, float now)
        {
            string localRoom = GameAccess.CurrentRoomId();
            bool comparable = haveRemote && now - remoteTime < RemoteStaleSeconds && !remote.Loading && !GameAccess.IsLoadingRoom
                && !string.IsNullOrEmpty(remote.RoomId) && !string.IsNullOrEmpty(localRoom);
            if (!comparable || remote.RoomId == localRoom)
            {
                mismatchSince = -1;
                return;
            }
            if (mismatchSince < 0) mismatchSince = now;
            if (now - mismatchSince < FollowDelay || now < nextTeleport) return;

            nextTeleport = now + TeleportCooldown;
            Plugin.Logger.LogInfo($"[Session] Host is in room {remote.RoomId}, we are in {localRoom}: moving to the host.");
            GameAccess.Teleport(character, remote.RootPosition);
        }

        void Start()
        {
            MobPatches.AfterGameLateUpdate = () => { if (welcomed && !iOwnRoom) mobMirror.LateApply(trackedRoom); };
            MobPatches.AfterMobAnimated = mob => { if (welcomed && !iOwnRoom) mobMirror.AfterAnimated(mob); };
            MobPatches.ShowAgain = mob => mobMirror.ShowAgain(mob);
            PausePatches.KeepRunning = () => welcomed && haveRemote && !BossIntro.Frozen && !GameAccess.IsLoadingRoom && GameAccess.MainCharacter() != null;
            // Compared with the room we are in right now (trackedRoom lags behind while a room loads).
            BossIntro.OtherPlayerHere = () => welcomed && haveRemote && Time.unscaledTime - remoteTime < RemoteStaleSeconds
                && !string.IsNullOrEmpty(remote.RoomId) && remote.RoomId == GameAccess.CurrentRoomId();
            DebugReport.Send = (buf, len) => { if (transport != null) transport.Send(buf, len, true); };
            SelfTest.IsHost = Settings.Role == Role.Host;
            SelfTest.Connected = () => welcomed && haveRemote;
            SelfTest.PartnerDead = () => remoteDead;
            SelfTest.OwnRoom = () => iOwnRoom;
            BossIntro.Connected = () => welcomed;
            BossIntro.PartnerInThisArea = () => haveRemote && remote != null && !string.IsNullOrEmpty(remote.RoomId)
                && PartnerMap.Area(remote.RoomId) == PartnerMap.Area(GameAccess.CurrentRoomId());
            Spectate.WeAreDead = () => weAreDead;
            Spectate.PartnerFighting = () => welcomed && !remoteDead && BossIntro.OtherPlayerHere();
            MobTargeting.PuppetHere = () => welcomed && !remoteDead && RemoteInMyRoom(Time.unscaledTime);
            BossIntro.IsHost = Settings != null && Settings.Role == Role.Host;
        }

        void OnDestroy()
        {
            try
            {
                if (transport != null && welcomed)
                {
                    writer.Begin(MsgType.Bye);
                    transport.Send(writer.Buffer, writer.Length, true);
                }
                transport?.Dispose();
            }
            catch { }
        }
    }
}
