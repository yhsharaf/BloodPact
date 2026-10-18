using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RoseNet.Session
{
    // Session messages (first byte = type). Separate from the milestone-1 controller packets in Protocol.cs.
    internal enum MsgType : byte
    {
        Hello = 1,     // guest -> host: protocol, game version, name
        Welcome = 2,   // host -> guest: accepted (or refused with a reason)
        State = 3,     // both ways, ~30/s: room + Rose root + bone pose + which parts are visible
        SkeletonRequest = 4, // "I don't know your skeleton with this hash"
        Skeleton = 5,  // bone and renderer paths for a hash, so the receiver can map them by name
        Bye = 6,
        Ping = 7,      // keeps the link alive while no Rose is in game (menus, loading)
        FxStart = 8,   // an effect (game "Feedback" asset, by name) started on the sender's Rose
        FxStop = 9,    // that effect was stopped
        VfxTables = 10, // names of the effect tables registered on the sender's Rose (base + equipped weapon)
        ProjSpawn = 11, // one of the sender's flying spells appeared: id, prefab name, pose
        ProjMove = 12,  // ~30/s: poses of the sender's live projectiles
        ProjEnd = 13,   // a projectile is gone
        RoomClaim = 14, // "I entered this room first, I run its monsters"
        MobStates = 15, // ~20/s from the room owner: every live monster's pose, actions and health
        MobInfo = 16,   // monster types (prefab names) for ids the owner hasn't described yet
        RestSync = 17,  // the sender rested at a mirror: world states the rest changed (shared respawn)
        MobHit = 18,    // non-owner -> owner: damage the sender's player dealt to the owner's monsters
        MobPoses = 19,  // ~12/s from the room owner: bone poses of the monsters, for exact visuals
        WorldState = 20, // a door, lever, crank... changed in the sender's world: save key, kind, new state
        CharacterStep = 22, // the sender's game got further with a character (e.g. learned a boss's name)
        BossIntroReady = 23, // our boss intro began after being called: the other player's held intro may go on
        IntroSkip = 24,  // host -> guest: skip the current intro dialogue line
        PlayerDied = 25, // the sender's Rose died (bool: true = died, false = alive again)
        BossStart = 21,  // the sender set off a boss encounter: room, trigger key, where they stand (pull the partner in)
    }

    internal sealed class PoseState
    {
        public uint Seq;
        public float SenderTime;          // sender's clock, for smoothing on the receiver
        public string RoomId = "";
        public bool Loading;
        public Vector3 EntityPosition;    // the character's root object (effects spawn relative to it)
        public Quaternion EntityRotation;
        public Vector3 EntityScale;
        public Vector3 RootPosition;      // the animated model under it
        public Quaternion RootRotation;
        public Vector3 RootScale;
        public uint SkeletonHash;
        public int BoneCount;
        public Quaternion[] Rotations = new Quaternion[0];
        public Vector3[] Positions = new Vector3[0];
        public int RendererCount;
        public bool[] Visible = new bool[0];  // per renderer: enabled and its object active
        public Vector3[] PartPositions = new Vector3[0];     // per renderer, relative to the character root:
        public Quaternion[] PartRotations = new Quaternion[0]; // weapons are moved to the hand by script

        public void EnsureBones(int n)
        {
            if (Rotations.Length < n) { Rotations = new Quaternion[n]; Positions = new Vector3[n]; }
            BoneCount = n;
        }

        public void EnsureRenderers(int n)
        {
            if (Visible.Length < n)
            {
                Visible = new bool[n];
                PartPositions = new Vector3[n];
                PartRotations = new Quaternion[n];
            }
            RendererCount = n;
        }

        public void CopyFrom(PoseState o)
        {
            Seq = o.Seq; SenderTime = o.SenderTime; RoomId = o.RoomId; Loading = o.Loading;
            EntityPosition = o.EntityPosition; EntityRotation = o.EntityRotation; EntityScale = o.EntityScale;
            RootPosition = o.RootPosition; RootRotation = o.RootRotation; RootScale = o.RootScale;
            SkeletonHash = o.SkeletonHash;
            EnsureBones(o.BoneCount);
            Array.Copy(o.Rotations, Rotations, o.BoneCount);
            Array.Copy(o.Positions, Positions, o.BoneCount);
            EnsureRenderers(o.RendererCount);
            Array.Copy(o.Visible, Visible, o.RendererCount);
            Array.Copy(o.PartPositions, PartPositions, o.RendererCount);
            Array.Copy(o.PartRotations, PartRotations, o.RendererCount);
        }

        // this = a..b at t (0..1). Discrete data (room, visibility) comes from the nearer snapshot.
        public void Blend(PoseState a, PoseState b, float t)
        {
            var near = t < 0.5f ? a : b;
            CopyFrom(near);
            if (a.SkeletonHash != b.SkeletonHash || a.BoneCount != b.BoneCount) return;
            EntityPosition = Vector3.LerpUnclamped(a.EntityPosition, b.EntityPosition, t);
            EntityRotation = Quaternion.Slerp(a.EntityRotation, b.EntityRotation, t);
            EntityScale = Vector3.LerpUnclamped(a.EntityScale, b.EntityScale, t);
            RootPosition = Vector3.LerpUnclamped(a.RootPosition, b.RootPosition, t);
            RootRotation = Quaternion.Slerp(a.RootRotation, b.RootRotation, t);
            for (int i = 0; i < BoneCount; i++)
            {
                Rotations[i] = Quaternion.Slerp(a.Rotations[i], b.Rotations[i], t);
                Positions[i] = Vector3.LerpUnclamped(a.Positions[i], b.Positions[i], t);
            }
            if (a.RendererCount != b.RendererCount) return;
            for (int i = 0; i < RendererCount; i++)
            {
                PartPositions[i] = Vector3.LerpUnclamped(a.PartPositions[i], b.PartPositions[i], t);
                PartRotations[i] = Quaternion.Slerp(a.PartRotations[i], b.PartRotations[i], t);
            }
        }
    }

    internal sealed class MessageWriter
    {
        readonly MemoryStream stream = new MemoryStream(4096);
        readonly BinaryWriter w;
        public MessageWriter() { w = new BinaryWriter(stream, Encoding.UTF8, true); }

        public byte[] Buffer => stream.GetBuffer();
        public int Length => (int)stream.Length;

        public BinaryWriter Begin(MsgType type)
        {
            stream.SetLength(0);
            w.Write((byte)type);
            return w;
        }

        public static void WriteState(BinaryWriter w, PoseState s)
        {
            w.Write(s.Seq);
            w.Write(s.SenderTime);
            w.Write(s.RoomId ?? "");
            w.Write(s.Loading);
            WriteV3(w, s.EntityPosition);
            WriteQ(w, s.EntityRotation);
            WriteV3(w, s.EntityScale);
            WriteV3(w, s.RootPosition);
            WriteQ(w, s.RootRotation);
            WriteV3(w, s.RootScale);
            w.Write(s.SkeletonHash);
            w.Write((ushort)s.BoneCount);
            for (int i = 0; i < s.BoneCount; i++)
            {
                var q = s.Rotations[i];
                w.Write(Pack(q.x)); w.Write(Pack(q.y)); w.Write(Pack(q.z)); w.Write(Pack(q.w));
                var p = s.Positions[i];
                w.Write(BitConverter.HalfToInt16Bits((Half)p.x));
                w.Write(BitConverter.HalfToInt16Bits((Half)p.y));
                w.Write(BitConverter.HalfToInt16Bits((Half)p.z));
            }
            w.Write((ushort)s.RendererCount);
            for (int i = 0; i < s.RendererCount; i += 8)
            {
                byte bits = 0;
                for (int j = 0; j < 8 && i + j < s.RendererCount; j++)
                    if (s.Visible[i + j]) bits |= (byte)(1 << j);
                w.Write(bits);
            }
            for (int i = 0; i < s.RendererCount; i++)
            {
                var p = s.PartPositions[i];
                w.Write(BitConverter.HalfToInt16Bits((Half)p.x));
                w.Write(BitConverter.HalfToInt16Bits((Half)p.y));
                w.Write(BitConverter.HalfToInt16Bits((Half)p.z));
                var q = s.PartRotations[i];
                w.Write(Pack(q.x)); w.Write(Pack(q.y)); w.Write(Pack(q.z)); w.Write(Pack(q.w));
            }
        }

        static short Pack(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);
        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
    }

    internal static class MessageReader
    {
        public static void ReadState(BinaryReader r, PoseState s)
        {
            s.Seq = r.ReadUInt32();
            s.SenderTime = r.ReadSingle();
            s.RoomId = r.ReadString();
            s.Loading = r.ReadBoolean();
            s.EntityPosition = ReadV3(r);
            s.EntityRotation = ReadQ(r);
            s.EntityScale = ReadV3(r);
            s.RootPosition = ReadV3(r);
            s.RootRotation = ReadQ(r);
            s.RootScale = ReadV3(r);
            s.SkeletonHash = r.ReadUInt32();
            int n = r.ReadUInt16();
            s.EnsureBones(n);
            for (int i = 0; i < n; i++)
            {
                var q = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
                s.Rotations[i] = Normalize(q);
                s.Positions[i] = new Vector3(
                    (float)BitConverter.Int16BitsToHalf(r.ReadInt16()),
                    (float)BitConverter.Int16BitsToHalf(r.ReadInt16()),
                    (float)BitConverter.Int16BitsToHalf(r.ReadInt16()));
            }
            int rc = r.ReadUInt16();
            s.EnsureRenderers(rc);
            for (int i = 0; i < rc; i += 8)
            {
                byte bits = r.ReadByte();
                for (int j = 0; j < 8 && i + j < rc; j++) s.Visible[i + j] = (bits & (1 << j)) != 0;
            }
            for (int i = 0; i < rc; i++)
            {
                s.PartPositions[i] = new Vector3(
                    (float)BitConverter.Int16BitsToHalf(r.ReadInt16()),
                    (float)BitConverter.Int16BitsToHalf(r.ReadInt16()),
                    (float)BitConverter.Int16BitsToHalf(r.ReadInt16()));
                s.PartRotations[i] = Normalize(new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f));
            }
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 1e-4f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Quaternion ReadQ(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
