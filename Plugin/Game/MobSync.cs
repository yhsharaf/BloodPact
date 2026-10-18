using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MotherBase.ActionFramework;
using MotherBase.EntityFramework;
using UnityEngine;

namespace RoseNet.Game
{
    // Monsters in a shared room. Both games spawn them (spawners line up on both PCs, see the probe test),
    // and each one gets the same ID on both: spawner path + position + spawn number since entering the room.
    // The room's owner runs them normally and streams their state; the other game takes over its matching
    // monsters (AI and movement off), creates any it is missing, removes extras, and drives them all
    // from that stream. So the non-owner always shows exactly the owner's monsters, even when the two
    // worlds differ (one player rested at a mirror, random horde spawns, ...).
    internal struct MobState
    {
        public uint Id;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public int ActionCount;
        public uint Action0, Action1, Action2;
        public int Frame0, Frame1, Frame2;
        public int Health, MaxHealth;
        public byte Target;   // who the owner's monster is after: 0 nobody, 1 the owner's Rose, 2 the other player

        public uint ActionAt(int i) => i == 0 ? Action0 : i == 1 ? Action1 : Action2;
        public int FrameAt(int i) => i == 0 ? Frame0 : i == 1 ? Frame1 : Frame2;
        public void SetAction(int i, uint a, int f)
        {
            if (i == 0) { Action0 = a; Frame0 = f; } else if (i == 1) { Action1 = a; Frame1 = f; } else { Action2 = a; Frame2 = f; }
        }
    }

    internal static class MobRegistry
    {
        static readonly Dictionary<IntPtr, int> spawnCounts = new Dictionary<IntPtr, int>();
        static readonly Dictionary<IntPtr, string> spawnerKeys = new Dictionary<IntPtr, string>();
        internal static readonly Dictionary<uint, Entity> ById = new Dictionary<uint, Entity>();
        internal static readonly Dictionary<IntPtr, uint> IdOf = new Dictionary<IntPtr, uint>();
        static readonly Dictionary<uint, string> roomOf = new Dictionary<uint, string>();   // room object name, e.g. "StreetsPoorV2_Room43_uid136"

        // A room's monsters spawn while the room loads, before the game reports entering it, so the list is
        // kept across rooms and filtered by room instead of being cleared on entry.
        public static bool InRoom(uint id, string roomId)
        {
            return !string.IsNullOrEmpty(roomId) && roomOf.TryGetValue(id, out var r) && !string.IsNullOrEmpty(r)
                && roomId.EndsWith(r, StringComparison.Ordinal);
        }

        // Any monster (alive or not) registered in this room: "a monster room".
        public static bool AnyIn(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return true;
            foreach (var kv in ById) if (InRoom(kv.Key, roomId) && kv.Value != null) return true;
            return false;
        }

        public static bool AnyAliveIn(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return true; // unknown: not safe
            foreach (var kv in ById)
                if (InRoom(kv.Key, roomId) && IsAlive(kv.Value) && !MobControl.IsPossessed(kv.Value)) return true;
            foreach (var kv in ById)
                if (InRoom(kv.Key, roomId) && IsAlive(kv.Value)) return true;
            return false;
        }

        // Forget monsters whose objects are gone (rooms unloaded).
        public static void Prune()
        {
            List<uint> dead = null;
            foreach (var kv in ById)
            {
                bool gone;
                try { gone = kv.Value == null || kv.Value.WasCollected || kv.Value.gameObject == null; } catch { gone = true; }
                if (gone) (dead ??= new List<uint>()).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var id in dead) { ById.Remove(id); roomOf.Remove(id); }
        }

        // A spawner that resets starts counting again, on both PCs, so respawned monsters get the same IDs.
        public static void OnSpawnerReset(BaseMobsSpawner spawner)
        {
            if (spawner != null) spawnCounts.Remove(spawner.Pointer);
        }

        public static void OnSpawned(BaseMobsSpawner spawner, Entity mob)
        {
            if (spawner == null || mob == null) return;
            spawnCounts.TryGetValue(spawner.Pointer, out var n);
            spawnCounts[spawner.Pointer] = ++n;
            var key = SpawnerKey(spawner);
            uint id = Hash(key + "#" + n);
            if (ById.TryGetValue(id, out var old) && old != null) IdOf.Remove(old.Pointer);
            ById[id] = mob;
            IdOf[mob.Pointer] = id;
            int slash = key.IndexOf('/');
            roomOf[id] = slash > 0 ? key.Substring(0, slash) : key;
        }

        // Monsters RoseNet created itself to match the room owner (they have no spawner here).
        public static void RegisterMirror(uint id, Entity mob, string roomId)
        {
            if (ById.TryGetValue(id, out var old) && old != null) IdOf.Remove(old.Pointer);
            ById[id] = mob;
            IdOf[mob.Pointer] = id;
            roomOf[id] = roomId; // a full room id ends with itself
        }

        public static string PrefabName(Entity mob)
        {
            var n = mob.name;
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }

        public static bool IsAlive(Entity mob)
        {
            try
            {
                if (mob == null || mob.destroyed || !mob.gameObject.activeInHierarchy) return false;
                var d = mob.GetComponent<Damageable>();
                return d == null || d.isAlive;
            }
            catch { return false; }
        }

        internal static string SpawnerKey(BaseMobsSpawner spawner)
        {
            if (spawnerKeys.TryGetValue(spawner.Pointer, out var key)) return key;
            var sb = new StringBuilder();
            for (var t = spawner.transform; t != null; t = t.parent)
                sb.Insert(0, sb.Length == 0 ? t.name : t.name + "/");
            var p = spawner.transform.position;
            key = string.Format(CultureInfo.InvariantCulture, "{0}@({1:0.0},{2:0.0},{3:0.0})", sb, p.x, p.y, p.z);
            spawnerKeys[spawner.Pointer] = key;
            return key;
        }

        public static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
            return h;
        }
    }

    // Monsters this game does not run right now: the room owner's stream drives them.
    internal static class MobControl
    {
        internal static readonly HashSet<IntPtr> Possessed = new HashSet<IntPtr>();
        internal static bool AllowStart;   // set while RoseNet itself starts an action on a possessed monster
        internal static bool AllowDeath;   // set while RoseNet plays the owner's kill on a possessed monster
        internal static readonly HashSet<IntPtr> OwnerKilled = new HashSet<IntPtr>();

        // Damage our player dealt to monsters we don't own, to be sent to the room owner.
        internal static readonly List<(uint id, int damage)> OutgoingHits = new List<(uint, int)>();
        internal static readonly Dictionary<IntPtr, float> LastLocalHit = new Dictionary<IntPtr, float>();

        // A real hit of our own Rose (the game's damage and death need one; an empty one crashes them).
        // Reused to apply the other player's hits and to play deaths the owner reports.
        internal static DamageData LastPlayerDamage;
        internal static DamageSystem.Connection LastPlayerConnection;

        public static bool IsPossessed(Entity e) => e != null && Possessed.Contains(e.Pointer);

        public static Damageable DamageableOf(Entity mob)
        {
            try { return mob.GetComponent<Damageable>() ?? mob.GetComponentInChildren<Damageable>(true); } catch { return null; }
        }
    }

    // Monster bodies: the owner streams each monster's bone pose (like the Rose puppet), the other game
    // applies it after its own animation. Actions still run underneath for attack timing; what you see
    // matches the owner exactly, whatever the animation system does.
    internal sealed class MobPose
    {
        public uint Hash;
        public Vector3 EntityScale = Vector3.one, RootScale = Vector3.one;
        public Vector3 RootPos, EntityPos;
        public Quaternion EntityRot = Quaternion.identity;   // which way the monster is turned
        public Quaternion RootRot;
        public Quaternion[] Rot = new Quaternion[0];
        public Vector3[] Pos = new Vector3[0];
        // Objects between the monster and its model (incl. FlipModel and its reference), exact local values.
        public uint ChainHash;
        public Vector3[] ChainPos = new Vector3[0], ChainScale = new Vector3[0];
        public Quaternion[] ChainRot = new Quaternion[0];
    }

    // The transforms that decide where a monster's model sits and which way it faces: everything from the
    // monster down to its model root, plus each FlipModelComponent (it mirrors the model every frame from the
    // sign of its reference's forward) and that reference, with their parents up to the monster.
    internal sealed class MobChain
    {
        public Transform[] Items;
        public string[] Paths;
        public uint Hash;

        public static MobChain Capture(Transform entity, Transform root)
        {
            var set = new Dictionary<string, Transform>();
            void AddUp(Transform t)
            {
                for (var cur = t; cur != null && cur != entity; cur = cur.parent)
                {
                    var path = Skeleton.PathOf(cur, entity);
                    if (path == null) return;
                    set[path] = cur;
                }
            }
            if (root != null) AddUp(root);
            foreach (var f in entity.GetComponentsInChildren<FlipModelComponent>(true))
            {
                AddUp(f.transform);
                try { if (f.referenceForward != null) AddUp(f.referenceForward); } catch { }
            }
            var paths = new List<string>(set.Keys);
            paths.Sort(StringComparer.Ordinal);
            if (paths.Count > 32) paths.RemoveRange(32, paths.Count - 32);
            var c = new MobChain { Paths = paths.ToArray(), Items = new Transform[paths.Count] };
            for (int i = 0; i < paths.Count; i++) c.Items[i] = set[paths[i]];
            c.Hash = Skeleton.HashLists(c.Paths, new string[0]);
            c.Flips = entity.GetComponentsInChildren<FlipModelComponent>(true);
            return c;
        }

        public FlipModelComponent[] Flips = new FlipModelComponent[0];

        // Mirror the model to match which way the monster is turned, as FlipModelComponent.Update would.
        // That Update doesn't run for monsters far from the local Rose, which leaves them drawn backwards
        // (walking one way, facing the other) with their weapon on the wrong side.
        public void Flip()
        {
            foreach (var f in Flips)
            {
                try { if (f != null) f.UpdateFlip(false); } catch { }
            }
        }

        public string Describe(Transform entity)
        {
            var sb = new StringBuilder();
            sb.Append($"entity rot {entity.eulerAngles.y:0} scale {entity.localScale.x:0.##},{entity.localScale.z:0.##}");
            for (int i = 0; i < Items.Length; i++)
            {
                var t = Items[i];
                if (t == null) continue;
                sb.Append($" | {Paths[i]}: rot {t.localEulerAngles.x:0},{t.localEulerAngles.y:0},{t.localEulerAngles.z:0} scale {t.localScale.x:0.##},{t.localScale.y:0.##},{t.localScale.z:0.##}");
            }
            foreach (var f in entity.GetComponentsInChildren<FlipModelComponent>(true))
            {
                try { sb.Append($" | flip on {f.name} (axis {(int)f.flipAxis}, ref {(f.referenceForward != null ? f.referenceForward.name + " fwd.x " + f.referenceForward.forward.x.ToString("0.##") : "none")})"); } catch { }
            }
            return sb.ToString();
        }
    }

    internal static class MobPoses
    {
        static readonly Dictionary<IntPtr, Skeleton> skeletons = new Dictionary<IntPtr, Skeleton>();
        static readonly Dictionary<IntPtr, MobChain> chains = new Dictionary<IntPtr, MobChain>();

        public static MobChain ChainOf(Entity mob)
        {
            if (chains.TryGetValue(mob.Pointer, out var c) && c != null) return c;
            c = MobChain.Capture(mob.transform, GameAccess.VisualRoot(mob));
            chains[mob.Pointer] = c;
            return c;
        }

        static float nextDescribeOwner, nextDescribeMirror;
        static void Describe(bool owner, Entity mob)
        {
            if (Time.unscaledTime < (owner ? nextDescribeOwner : nextDescribeMirror)) return;
            if (owner) nextDescribeOwner = Time.unscaledTime + 5f; else nextDescribeMirror = Time.unscaledTime + 5f;
            try
            {
                Plugin.Logger.LogInfo($"[Session] Facing check ({(owner ? "owner" : "mirror")}) '{mob.name}' at x {mob.transform.position.x:0.0}: {ChainOf(mob).Describe(mob.transform)}");
            }
            catch { }
        }

        public static Skeleton SkeletonOf(Entity mob)
        {
            if (skeletons.TryGetValue(mob.Pointer, out var s) && s != null && s.Root != null) return s;
            s = Skeleton.Capture(mob.transform, GameAccess.VisualRoot(mob));
            // Monsters are matched by bones only: their effect renderers can differ between the two games.
            s.Hash = Skeleton.HashLists(s.Paths, new string[0]);
            skeletons[mob.Pointer] = s;
            return s;
        }

        static int applied, mismatched;
        static float nextReport;

        static void Report()
        {
            if (Time.unscaledTime < nextReport || applied + mismatched == 0) return;
            nextReport = Time.unscaledTime + 10f;
            Plugin.Logger.LogInfo($"[Session] Monster poses: {applied} applied, {mismatched} skeleton mismatches; facing chain {chainApplied} copied, {chainMismatched} mismatched (last 10 s).");
            applied = mismatched = chainApplied = chainMismatched = 0;
        }

        // Owner: write the poses of up to 10 live monsters in the room.
        public static void Write(System.IO.BinaryWriter w, string roomId)
        {
            var list = new List<(uint id, Entity mob)>();
            foreach (var kv in MobRegistry.ById)
                if (MobRegistry.InRoom(kv.Key, roomId) && MobRegistry.IsAlive(kv.Value)) { list.Add((kv.Key, kv.Value)); if (list.Count >= 10) break; }
            w.Write(roomId);
            w.Write((byte)list.Count);
            foreach (var (id, mob) in list)
            {
                var s = SkeletonOf(mob);
                int n = Math.Min(s.Bones.Length, 255);
                w.Write(id);
                w.Write(s.Hash);
                var rp = s.Root.position; var rr = s.Root.rotation;
                w.Write(rp.x); w.Write(rp.y); w.Write(rp.z);
                w.Write(Pack(rr.x)); w.Write(Pack(rr.y)); w.Write(Pack(rr.z)); w.Write(Pack(rr.w));
                // Overall scale of the model incl. mirroring by objects in between (that's how they face left/right).
                var es = mob.transform.localScale; var rs = s.Root.lossyScale;
                w.Write(es.x); w.Write(es.y); w.Write(es.z);
                w.Write(rs.x); w.Write(rs.y); w.Write(rs.z);
                var ep = mob.transform.position;
                w.Write(ep.x); w.Write(ep.y); w.Write(ep.z);
                var er = mob.transform.rotation;
                w.Write(Pack(er.x)); w.Write(Pack(er.y)); w.Write(Pack(er.z)); w.Write(Pack(er.w));
                w.Write((byte)n);
                for (int i = 0; i < n; i++)
                {
                    var b = s.Bones[i];
                    var q = b != null ? b.localRotation : Quaternion.identity;
                    var p = b != null ? b.localPosition : Vector3.zero;
                    w.Write(Pack(q.x)); w.Write(Pack(q.y)); w.Write(Pack(q.z)); w.Write(Pack(q.w));
                    w.Write(BitConverter.HalfToInt16Bits((Half)p.x)); w.Write(BitConverter.HalfToInt16Bits((Half)p.y)); w.Write(BitConverter.HalfToInt16Bits((Half)p.z));
                }
                var c = ChainOf(mob);
                c.Flip();
                w.Write(c.Hash);
                w.Write((byte)c.Items.Length);
                foreach (var t in c.Items)
                {
                    var lp = t != null ? t.localPosition : Vector3.zero;
                    var lr = t != null ? t.localRotation : Quaternion.identity;
                    var ls = t != null ? t.localScale : Vector3.one;
                    w.Write(lp.x); w.Write(lp.y); w.Write(lp.z);
                    w.Write(Pack(lr.x)); w.Write(Pack(lr.y)); w.Write(Pack(lr.z)); w.Write(Pack(lr.w));
                    w.Write(ls.x); w.Write(ls.y); w.Write(ls.z);
                }
                if (id == list[0].id) Describe(true, mob);
                MoonwalkProbe.Observe(mob, c, "owner");
            }
        }

        public static void Read(System.IO.BinaryReader r, out string roomId, Dictionary<uint, MobPose> into)
        {
            roomId = r.ReadString();
            int count = r.ReadByte();
            for (int k = 0; k < count; k++)
            {
                uint id = r.ReadUInt32();
                if (!into.TryGetValue(id, out var pose)) into[id] = pose = new MobPose();
                pose.Hash = r.ReadUInt32();
                pose.RootPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                pose.RootRot = Q(r);
                pose.EntityScale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                pose.RootScale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                pose.EntityPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                pose.EntityRot = Q(r);
                int n = r.ReadByte();
                if (pose.Rot.Length != n) { pose.Rot = new Quaternion[n]; pose.Pos = new Vector3[n]; }
                for (int i = 0; i < n; i++)
                {
                    pose.Rot[i] = Q(r);
                    pose.Pos[i] = new Vector3((float)BitConverter.Int16BitsToHalf(r.ReadInt16()), (float)BitConverter.Int16BitsToHalf(r.ReadInt16()), (float)BitConverter.Int16BitsToHalf(r.ReadInt16()));
                }
                pose.ChainHash = r.ReadUInt32();
                int m = r.ReadByte();
                if (pose.ChainPos.Length != m) { pose.ChainPos = new Vector3[m]; pose.ChainScale = new Vector3[m]; pose.ChainRot = new Quaternion[m]; }
                for (int i = 0; i < m; i++)
                {
                    pose.ChainPos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    pose.ChainRot[i] = Q(r);
                    pose.ChainScale[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
            }
        }

        // Non-owner: blend our monster's bones toward the owner's pose (called after animation).
        public static bool Fits(Entity mob, MobPose pose)
        {
            var s = SkeletonOf(mob);
            return s.Hash == pose.Hash && s.Bones.Length >= pose.Rot.Length;
        }

        // Shows the owner's monster as it was between snapshots a and b (f = 0..1): position, facing and
        // bones all from the same moment, so feet stay planted and swings are where the owner had them.
        public static void Apply(Entity mob, MobPose a, MobPose b, float f)
        {
            var s = SkeletonOf(mob);
            Report();
            if (!Fits(mob, a)) { mismatched++; return; }
            if (b == null || !Fits(mob, b) || b.Rot.Length != a.Rot.Length || b.ChainPos.Length != a.ChainPos.Length) { b = a; f = 0f; }
            applied++;
            var near = f < 0.5f ? a : b;   // mirroring flips are never blended
            mob.transform.localScale = near.EntityScale;
            // Turned the way the owner's monster was at this moment: our copy's own action logic turns it
            // toward its local target, which can disagree with where the owner's monster walks.
            mob.transform.rotation = Quaternion.Angle(a.EntityRot, b.EntityRot) > 90f ? near.EntityRot : Quaternion.Slerp(a.EntityRot, b.EntityRot, f);
            var c = ChainOf(mob);
            if (c.Hash == a.ChainHash && c.Items.Length == a.ChainPos.Length)
            {
                // Same objects on both sides: copy them exactly (mirroring included), so the model and the
                // game's own facing logic agree with the owner.
                for (int i = 0; i < c.Items.Length; i++)
                {
                    var t = c.Items[i];
                    if (t == null) continue;
                    t.localScale = near.ChainScale[i];
                    t.localRotation = Quaternion.Angle(a.ChainRot[i], b.ChainRot[i]) > 90f ? near.ChainRot[i] : Quaternion.Slerp(a.ChainRot[i], b.ChainRot[i], f);
                    t.localPosition = Vector3.Lerp(a.ChainPos[i], b.ChainPos[i], f);
                }
                c.Flip();   // facing follows our copy's turn, whatever the owner's model showed
                chainApplied++;
            }
            else
            {
                chainMismatched++;
                ApplyRootFallback(s, near, 1f);
            }
            for (int i = 0; i < a.Rot.Length; i++)
            {
                var bone = s.Bones[i];
                if (bone == null) continue;
                bone.localRotation = Quaternion.Slerp(a.Rot[i], b.Rot[i], f);
                bone.localPosition = Vector3.Lerp(a.Pos[i], b.Pos[i], f);
            }
            Describe(false, mob);
            MoonwalkProbe.Observe(mob, ChainOf(mob), "mirror");
        }

        public static Vector3 PositionAt(MobPose a, MobPose b, float f) =>
            b == null || (b.EntityPos - a.EntityPos).sqrMagnitude > 9f ? (b ?? a).EntityPos : Vector3.Lerp(a.EntityPos, b.EntityPos, f);

        static int chainApplied, chainMismatched;

        // Older behaviour when the two sides' objects differ: match the model's overall scale and world pose.
        static void ApplyRootFallback(Skeleton s, MobPose pose, float k)
        {
            var parent = s.Root.parent;
            var ps = parent != null ? parent.lossyScale : Vector3.one;
            s.Root.localScale = new Vector3(
                ps.x != 0 ? pose.RootScale.x / ps.x : pose.RootScale.x,
                ps.y != 0 ? pose.RootScale.y / ps.y : pose.RootScale.y,
                ps.z != 0 ? pose.RootScale.z / ps.z : pose.RootScale.z);
            s.Root.SetPositionAndRotation(Vector3.Lerp(s.Root.position, pose.RootPos, k), Quaternion.Slerp(s.Root.rotation, pose.RootRot, k));
        }

        public static void Forget() { skeletons.Clear(); chains.Clear(); }

        static short Pack(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);
        static Quaternion Q(System.IO.BinaryReader r)
        {
            var q = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 1e-4f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }
    }

    // Owner side: monsters choose between our Rose and the other player's puppet by distance.
    internal static class MobTargeting
    {
        const float Range = 10f, SwitchMargin = 1.5f;
        static readonly Dictionary<IntPtr, float> nextCheck = new Dictionary<IntPtr, float>();
        static int logs = 20;

        public static bool Due(Entity mob)
        {
            if (Puppet.Current == null || MobControl.IsPossessed(mob)) return false;
            nextCheck.TryGetValue(mob.Pointer, out var t);
            if (Time.unscaledTime < t) return false;
            nextCheck[mob.Pointer] = Time.unscaledTime + 1f;
            return true;
        }

        // Whether the other player is in our room and alive (set by the session).
        internal static Func<bool> PuppetHere = () => true;

        public static bool TryPickCloser(Entity mob, AIStateComponent ai, bool acquiring)
        {
            var puppet = Puppet.Current;
            var me = GameAccess.MainCharacter();
            if (puppet == null || MobControl.IsPossessed(mob)) return false;
            var current = ai.target;
            if (!PuppetHere() || !puppet.gameObject.activeInHierarchy)
            {
                // The other player left (or died): their stand-in is somewhere else now; never chase it.
                if (current == null || current.Pointer != puppet.Pointer || me == null) return false;
                ai.target = me;
                if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] '{MobRegistry.PrefabName(mob)}' lost the other player; back to our Rose.");
                return true;
            }
            // Only choose between the two players; never steal a target the game set for other reasons.
            if (current != null && current.Pointer != puppet.Pointer && (me == null || current.Pointer != me.Pointer)) return false;

            var p = mob.transform.position;
            float toPuppet = Vector3.Distance(p, puppet.transform.position);
            float toMe = me != null && me.gameObject.activeInHierarchy ? Vector3.Distance(p, me.transform.position) : float.MaxValue;

            if (current != null && current.Pointer == puppet.Pointer)
            {
                // Chasing the other player: come back to ours when ours is clearly closer (fair both ways).
                if (me == null || toMe > Range || toMe + SwitchMargin >= toPuppet) return false;
                ai.target = me;
                if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] '{MobRegistry.PrefabName(mob)}' now targets our Rose ({toMe:0.0} away, other player {toPuppet:0.0}).");
                return true;
            }

            if (toPuppet > Range) return false;
            float toCurrent = current != null ? toMe : float.MaxValue;
            if (toPuppet + SwitchMargin >= toCurrent) return false;

            ai.target = puppet;
            if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] '{MobRegistry.PrefabName(mob)}' now targets the other player ({toPuppet:0.0} away, ours {toMe:0.0}).");
            return true;
        }
    }

    // Owner side: damage the other player dealt to our monsters.
    internal static class MobDamage
    {
        static int failLogs = 5;

        // A hit's "connection" names its target; reusing a copied one would hit the old target (or be ignored
        // as a duplicate). Build a new one with the same hit details, aimed at this monster.
        static DamageSystem.Connection FreshConnection(DamageSystem.Connection template, Entity target, Entity source)
        {
            var c = new DamageSystem.Connection();
            c.target = target;
            c.source = source;
            c.damageData = template.damageData;
            c.hitDirection = template.hitDirection;
            c.hitSide = template.hitSide;
            c.preciseHitSide = template.preciseHitSide;
            c.sourceCaster = template.sourceCaster;
            c.sourceColliderIndex = template.sourceColliderIndex;
            c.targetColliderMask = template.targetColliderMask;
            c.result = template.result;
            c.preventPiercing = template.preventPiercing;
            c.splitDmgs = template.splitDmgs;
            c.closestCollider = template.closestCollider;
            c.closestColliderDist = template.closestColliderDist;
            return c;
        }

        public static void ApplyRemoteHit(uint id, int damage, Entity attacker)
        {
            if (!MobRegistry.ById.TryGetValue(id, out var mob) || mob == null || !MobRegistry.IsAlive(mob)) return;
            var d = MobControl.DamageableOf(mob);
            if (d == null || damage <= 0) return;
            try
            {
                // A real hit through the game's own damage path: stagger, numbers and death all play
                // normally. Our own Rose stands in as the attacker, so the owner's stats set the damage.
                // Health directly; at 0 the game's own Die (which only needs a fresh, zeroed hit record;
                // it copies the record, so it must not be empty).
                int hp = d.currentHealth - damage;
                d.currentHealth = Math.Max(0, hp);
                BossHud.Refresh(mob, d);
                if (hp <= 0 && d.isAlive) d.Die(mob, attacker, new DamageData());
            }
            catch (Exception e)
            {
                if (failLogs-- > 0) Plugin.Logger.LogWarning($"[Session] Applying the other player's hit failed: {e.Message}");
            }
        }
    }

    // Owner side: the state of every live monster that spawned since we entered the room.
    internal static class MobOwner
    {
        public static void Collect(List<MobState> into, List<(uint id, string name)> names, string roomId)
        {
            into.Clear();
            names.Clear();
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (!MobRegistry.InRoom(kv.Key, roomId) || !MobRegistry.IsAlive(mob)) continue;
                var t = mob.transform;
                var s = new MobState { Id = kv.Key, Position = t.position, Rotation = t.rotation, Scale = t.lossyScale };
                try
                {
                    var actor = mob.GetComponent<Actor>();
                    var actions = actor != null ? actor.activeActions : null;
                    if (actions != null)
                        for (int i = 0; i < actions.Count && s.ActionCount < 3; i++)
                        {
                            var a = actions[i];
                            if (a.data == null) continue;
                            s.SetAction(s.ActionCount++, MobRegistry.Hash(a.data.name), a.frameInfo.integerFrame);
                        }
                    var d = mob.GetComponent<Damageable>();
                    if (d != null) { s.Health = d.currentHealth; s.MaxHealth = d.maxHealth; }
                    var ai = mob.GetComponent<AIStateComponent>();
                    var target = ai != null ? ai.target : null;
                    var me = GameAccess.MainCharacter();
                    if (target != null && me != null && target.Pointer == me.Pointer) s.Target = 1;
                    else if (target != null && Puppet.Current != null && target.Pointer == Puppet.Current.Pointer) s.Target = 2;
                }
                catch { }
                into.Add(s);
                names.Add((kv.Key, MobRegistry.PrefabName(mob)));
                if (into.Count >= 64) break;
            }
        }
    }

    // Non-owner side: make our monsters match the owner's stream.
    internal sealed class MobMirror
    {
        static readonly Vector3 Away = new Vector3(0, -10000, 0);

        readonly Dictionary<uint, MobState> latest = new Dictionary<uint, MobState>();
        readonly HashSet<uint> inStream = new HashSet<uint>();
        readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
        readonly Dictionary<uint, ActionData> actionsByHash = new Dictionary<uint, ActionData>();
        readonly Dictionary<string, Entity> prefabs = new Dictionary<string, Entity>();
        readonly HashSet<IntPtr> hidden = new HashSet<IntPtr>();
        readonly HashSet<uint> failedSpawns = new HashSet<uint>();
        float nextActionRefresh, nextPrefabRefresh;
        string room;
        int missingLogs = 10, spawnLogs = 10;
        public bool Active { get; private set; }

        public void ReceiveNames(List<(uint id, string name)> list)
        {
            foreach (var (id, name) in list) names[id] = name;
        }

        public void Receive(List<MobState> states)
        {
            inStream.Clear();
            foreach (var s in states)
            {
                latest[s.Id] = s;
                inStream.Add(s.Id);
            }
            if (!Active) { activeSince = Time.unscaledTime; summaryLogged = false; }
            Active = true;
        }

        // Called every frame while we are not the owner and the owner is in our room.
        public void Apply(Entity anyLocalEntity, string roomId, bool ownerHere = true)
        {
            if (!Active) return;
            room = roomId;

            // Create the owner's monsters that our world does not have (a few per frame).
            int created = 0;
            foreach (var id in inStream)
            {
                if (created >= 3) break;
                if (MobRegistry.ById.TryGetValue(id, out var have) && have != null) continue;
                if (failedSpawns.Contains(id) || !names.TryGetValue(id, out var name) || !latest.TryGetValue(id, out var s)) continue;
                if (TrySpawn(id, name, s, anyLocalEntity)) created++;
                else failedSpawns.Add(id);
            }

            float k = 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime);
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || !MobRegistry.InRoom(kv.Key, roomId)) continue;
                // A boss plays its own intro here first: that starts our fight (arena camera, walls, HUD).
                if (BossSync.AwaitingLocalStart(mob)) { MobControl.Possessed.Remove(mob.Pointer); continue; }
                MobControl.Possessed.Add(mob.Pointer);
                idOf[mob.Pointer] = kv.Key;

                if (!inStream.Contains(kv.Key) || !latest.TryGetValue(kv.Key, out var s))
                {
                    // The owner is leaving: its monsters vanish from its stream as its room unloads. They are
                    // not dead; leave them be until we take them over.
                    if (!ownerHere) continue;
                    // Gone from the owner's world. If we just saw it alive, the owner killed it: play the death
                    // here, then take it out of the level once the death has played.
                    if (wasStreamed.Remove(kv.Key) && MobRegistry.IsAlive(mob)) PlayDeath(mob, anyLocalEntity);
                    else if (!dying.TryGetValue(mob.Pointer, out var until) || Time.unscaledTime > until) Hide(mob);
                    continue;
                }
                wasStreamed.Add(kv.Key);
                bool wasHidden = Unhide(mob);

                // Our own smoothed position, set every frame: bumps from our Rose (the game's repel) can't push it.
                var t = mob.transform;
                Vector3 pos;
                if (BufferedPosition(kv.Key, mob, out var buffered)) pos = buffered;
                else if (wasHidden || !smoothed.TryGetValue(mob.Pointer, out pos) || (s.Position - pos).sqrMagnitude > 9f) pos = s.Position;
                else pos = Vector3.Lerp(pos, s.Position, k);
                smoothed[mob.Pointer] = pos;
                t.position = pos;
                t.rotation = s.Rotation;
                SyncHealth(mob, s);
                SyncTarget(mob, s, anyLocalEntity);
                // Facing comes with the streamed pose (exact scale), not a separate flip.

                SyncActions(mob, s);
            }

            if (!summaryLogged && Time.unscaledTime - activeSince > 2f)
            {
                summaryLogged = true;
                int mine = 0, matched = 0;
                foreach (var kv in MobRegistry.ById)
                {
                    if (!MobRegistry.InRoom(kv.Key, roomId)) continue;
                    mine++;
                    if (inStream.Contains(kv.Key)) matched++;
                }
                Plugin.Logger.LogInfo($"[Session] Room owner has {inStream.Count} monsters; ours: {mine} ({matched} matched, {hidden.Count} hidden, {created} created this frame).");
            }
        }

        bool summaryLogged;
        float activeSince;
        readonly Dictionary<IntPtr, Vector3> smoothed = new Dictionary<IntPtr, Vector3>();
        readonly HashSet<uint> wasStreamed = new HashSet<uint>();
        readonly Dictionary<IntPtr, float> dying = new Dictionary<IntPtr, float>();
        bool healthSyncBroken;
        int actionLogs = 12;
        float nextActionLog;

        // The owner's health wins, except right after our own hit (the owner hasn't counted it yet).
        void SyncHealth(Entity mob, MobState s)
        {
            if (s.MaxHealth <= 0) return;
            var d = MobControl.DamageableOf(mob);
            if (d == null || d.currentHealth == s.Health) return;
            if (MobControl.LastLocalHit.TryGetValue(mob.Pointer, out var hitAt) && Time.unscaledTime - hitAt < 0.6f && s.Health > d.currentHealth) return;
            if (healthSyncBroken) return;
            try
            {
                if (s.MaxHealth > 0 && d.maxHealth != s.MaxHealth) d.maxHealth = s.MaxHealth;   // co-op scaling
                d.currentHealth = Math.Max(1, s.Health);
                BossHud.Refresh(mob, d);
            }
            catch (Exception e)
            {
                healthSyncBroken = true; // don't try (and fail) every frame
                Plugin.Logger.LogWarning($"[Session] Monster health sync is off: {e.Message}");
            }
        }

        // Attacks aim at the monster's own target: give our copy the same target as the owner's monster,
        // seen from this side (the owner's Rose is our puppet of them; "the other player" is us).
        static void SyncTarget(Entity mob, MobState s, Entity me)
        {
            try
            {
                var ai = mob.GetComponent<AIStateComponent>();
                if (ai == null) return;
                Entity want = s.Target == 1 ? Puppet.Current : s.Target == 2 ? me : null;
                var have = ai.target;
                if ((want == null && have == null) || (want != null && have != null && want.Pointer == have.Pointer)) return;
                ai.target = want;
            }
            catch { }
        }

        void PlayDeath(Entity mob, Entity killer)
        {
            var d = MobControl.DamageableOf(mob);
            dying[mob.Pointer] = Time.unscaledTime + 3f;
            if (d == null) { dying.Remove(mob.Pointer); Hide(mob); return; }
            try
            {
                // The game's own death (allowed for monsters the owner killed).
                MobControl.OwnerKilled.Add(mob.Pointer);
                MobControl.AllowDeath = true;
                MobControl.AllowStart = true;
                d.currentHealth = 0;
                d.Die(mob, killer, new DamageData());
            }
            catch (Exception e)
            {
                if (missingLogs-- > 0) Plugin.Logger.LogWarning($"[Session] Could not play a monster's death: {e.Message}");
                dying.Remove(mob.Pointer);
                Hide(mob);
            }
            finally { MobControl.AllowDeath = false; MobControl.AllowStart = false; }
        }

        // Physics and the game's repel run after our Update; put mirrored monsters back where the owner has them.
        public void LateApply(string roomId)
        {
            if (!Active) return;
            lateFrame = Time.frameCount;
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || hidden.Contains(mob.Pointer) || dying.ContainsKey(mob.Pointer)) continue;
                if (!MobRegistry.InRoom(kv.Key, roomId) || !smoothed.TryGetValue(mob.Pointer, out var pos)) continue;
                idOf[mob.Pointer] = kv.Key;
                if (Sample(kv.Key, out var a, out var b, out var f) && MobPoses.Fits(mob, a))
                {
                    pos = MobPoses.PositionAt(a, b, f);
                    smoothed[mob.Pointer] = pos;
                    mob.transform.position = pos;
                    try { MobPoses.Apply(mob, a, b, f); } catch { }
                }
                else mob.transform.position = pos;
            }
        }

        internal readonly Dictionary<uint, MobPose> Poses = new Dictionary<uint, MobPose>();

        // Snapshots per monster, by arrival time. Shown PoseDelay seconds late so there is always a pair to
        // blend between; the monster's actions (and so its hitboxes) run the same amount late.
        internal const float PoseDelay = 0.12f;
        const int ActionFps = 30;
        internal static readonly int DelayFrames = (int)Math.Round(PoseDelay * ActionFps);
        readonly Dictionary<uint, List<(float t, MobPose p)>> poseBuffer = new Dictionary<uint, List<(float, MobPose)>>();

        public void ReceivePoses(Dictionary<uint, MobPose> poses)
        {
            float now = Time.unscaledTime;
            foreach (var kv in poses)
            {
                Poses[kv.Key] = kv.Value;
                if (!poseBuffer.TryGetValue(kv.Key, out var list)) poseBuffer[kv.Key] = list = new List<(float, MobPose)>();
                list.Add((now, kv.Value));
                while (list.Count > 2 && list[1].t < now - 1f) list.RemoveAt(0);
                if (list.Count > 40) list.RemoveAt(0);
            }
        }

        bool Sample(uint id, out MobPose a, out MobPose b, out float f)
        {
            a = b = null; f = 0f;
            if (!poseBuffer.TryGetValue(id, out var list) || list.Count == 0) return false;
            float at = Time.unscaledTime - PoseDelay;
            if (at <= list[0].t) { a = b = list[0].p; return true; }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].t > at) continue;
                a = list[i].p;
                if (i + 1 < list.Count)
                {
                    b = list[i + 1].p;
                    float span = list[i + 1].t - list[i].t;
                    f = span > 1e-4f ? Mathf.Clamp01((at - list[i].t) / span) : 1f;
                }
                else b = a;
                return true;
            }
            a = b = list[list.Count - 1].p;
            return true;
        }

        // The game animates monsters itself (AnimatedActor.UpdateAnimation). Our copies have their movement
        // off, so the game would draw them standing/walking on the spot; whenever it animates one, put the
        // owner's pose straight back on, so what is drawn and where its weapon bones are (for hitboxes)
        // always match the owner.
        readonly Dictionary<IntPtr, uint> idOf = new Dictionary<IntPtr, uint>();
        int lateFrame = -1, animAfterLate, animTotal;
        float nextAnimReport;

        public void AfterAnimated(Entity mob)
        {
            if (!Active || mob == null) return;
            animTotal++;
            if (Time.frameCount == lateFrame) animAfterLate++;
            if (Time.unscaledTime >= nextAnimReport)
            {
                nextAnimReport = Time.unscaledTime + 10f;
                Plugin.Logger.LogInfo($"[Session] Monster animation passes: {animTotal}, {animAfterLate} of them after our late pose (last 10 s).");
                animTotal = animAfterLate = 0;
            }
            if (!idOf.TryGetValue(mob.Pointer, out var id) || hidden.Contains(mob.Pointer) || dying.ContainsKey(mob.Pointer)) return;
            if (!Sample(id, out var a, out var b, out var f) || !MobPoses.Fits(mob, a)) return;
            var pos = MobPoses.PositionAt(a, b, f);
            smoothed[mob.Pointer] = pos;
            mob.transform.position = pos;
            MobPoses.Apply(mob, a, b, f);
        }

        // Where the owner's monster was PoseDelay ago, if we have its snapshots.
        bool BufferedPosition(uint id, Entity mob, out Vector3 pos)
        {
            pos = default;
            if (!Sample(id, out var a, out var b, out var f) || !MobPoses.Fits(mob, a)) return false;
            pos = MobPoses.PositionAt(a, b, f);
            return true;
        }

        bool TrySpawn(uint id, string name, MobState s, Entity anyLocalEntity)
        {
            try
            {
                var prefab = FindPrefab(name);
                if (prefab == null || anyLocalEntity == null)
                {
                    if (spawnLogs-- > 0) Plugin.Logger.LogWarning($"[Session] Can't create the owner's '{name}' here (not loaded).");
                    return false;
                }
                var mob = anyLocalEntity.context.InstantiateEntity(prefab, true);
                if (mob == null) return false;
                mob.transform.SetPositionAndRotation(s.Position, s.Rotation);
                MobControl.Possessed.Add(mob.Pointer);
                MobRegistry.RegisterMirror(id, mob, room);
                if (spawnLogs-- > 0) Plugin.Logger.LogInfo($"[Session] Created the owner's '{name}' (missing in our world).");
                return true;
            }
            catch (Exception e)
            {
                if (spawnLogs-- > 0) Plugin.Logger.LogWarning($"[Session] Creating '{name}' failed: {e.Message}");
                return false;
            }
        }

        Entity FindPrefab(string name)
        {
            if (prefabs.TryGetValue(name, out var p) && p != null) return p;
            if (Time.unscaledTime < nextPrefabRefresh) return null;
            nextPrefabRefresh = Time.unscaledTime + 2f;
            prefabs.Clear();
            // Prefer the prefab asset (not in a scene) over a live or pooled instance.
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Entity>()))
            {
                var e = o.TryCast<Entity>();
                if (e == null || e.GetComponent<MobComponent>() == null) continue;
                var n = MobRegistry.PrefabName(e);
                bool isAsset = !e.gameObject.scene.IsValid();
                if (!prefabs.TryGetValue(n, out var have) || (isAsset && have.gameObject.scene.IsValid())) prefabs[n] = e;
            }
            return prefabs.TryGetValue(name, out p) ? p : null;
        }

        void SyncActions(Entity mob, MobState s)
        {
            Actor actor;
            try { actor = mob.GetComponent<Actor>(); } catch { return; }
            if (actor == null || s.ActionCount == 0) return;
            var active = actor.activeActions;
            if (actionLogs > 0 && Time.unscaledTime >= nextActionLog)
            {
                // Diagnostics for slide-walking: what the owner's monster runs vs what ours runs.
                actionLogs--;
                nextActionLog = Time.unscaledTime + 1.5f;
                var owner = new StringBuilder();
                for (int i = 0; i < s.ActionCount; i++) owner.Append(FindAction(s.ActionAt(i))?.name ?? $"?{s.ActionAt(i):X8}").Append($"@{s.FrameAt(i)} ");
                var mine = new StringBuilder();
                if (active != null) for (int j = 0; j < active.Count; j++) mine.Append(active[j].data?.name).Append($"@{active[j].frameInfo.integerFrame} ");
                Plugin.Logger.LogInfo($"[Session] Monster '{MobRegistry.PrefabName(mob)}' owner runs: {owner}| ours: {mine}");
            }
            for (int i = 0; i < s.ActionCount; i++)
            {
                uint want = s.ActionAt(i);
                int frame = s.FrameAt(i);
                int localFrame = -1;
                ActionData data = null;
                if (active != null)
                    for (int j = 0; j < active.Count; j++)
                    {
                        var a = active[j];
                        if (a.data != null && MobRegistry.Hash(a.data.name) == want) { localFrame = a.frameInfo.integerFrame; data = a.data; break; }
                    }

                try
                {
                    MobControl.AllowStart = true;
                    // Run it as far as the owner was PoseDelay ago: the hitboxes then come out exactly when
                    // the swing is shown. A brand-new action waits until it is that old.
                    int target = frame - DelayFrames;
                    if (data == null)
                    {
                        if (target < 0) continue;
                        data = FindAction(want);
                        if (data == null) continue;
                        actor.StartAction(mob, data);
                        actor.SetActionFrame(mob, data, target);
                    }
                    else if (Math.Abs(localFrame - target) > 2)
                    {
                        actor.SetActionFrame(mob, data, Math.Max(0, target));
                    }
                }
                catch (Exception e)
                {
                    if (missingLogs-- > 0) Plugin.Logger.LogWarning($"[Session] Could not mirror a monster action: {e.Message}");
                }
                finally { MobControl.AllowStart = false; }
            }
        }

        ActionData FindAction(uint hash)
        {
            if (actionsByHash.TryGetValue(hash, out var a) && a != null) return a;
            if (Time.unscaledTime < nextActionRefresh) return null;
            nextActionRefresh = Time.unscaledTime + 2f;
            actionsByHash.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ActionData>()))
            {
                var d = o.TryCast<ActionData>();
                if (d == null) continue;
                uint h = MobRegistry.Hash(d.name);
                if (!actionsByHash.ContainsKey(h)) actionsByHash[h] = d;
            }
            if (!actionsByHash.TryGetValue(hash, out a) && missingLogs-- > 0)
                Plugin.Logger.LogWarning($"[Session] A monster action of the room owner is not loaded here (hash {hash:X8}).");
            return a;
        }

        // Hidden = invisible AND moved out of the level, so its body and attacks can't touch anyone.
        void Hide(Entity mob)
        {
            if (!hidden.Add(mob.Pointer)) return;
            foreach (var r in mob.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            mob.transform.position = Away;
        }

        bool Unhide(Entity mob)
        {
            if (!hidden.Remove(mob.Pointer)) return false;
            foreach (var r in mob.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            return true;
        }

        // We became the owner (or the other player left): the visible monsters go back to their AI where
        // they are. Hidden ones were dead in the owner's world, so they stay hidden and under our control.
        public void Release()
        {
            MobControl.Possessed.Clear();
            foreach (var p in hidden) MobControl.Possessed.Add(p);
            latest.Clear();
            inStream.Clear();
            failedSpawns.Clear();
            Active = false;
        }

        public void OnRoomChanged()
        {
            Release();
            hidden.Clear();
            smoothed.Clear();
            wasStreamed.Clear();
            dying.Clear();
            Poses.Clear();
            poseBuffer.Clear();
            idOf.Clear();
            MobPoses.Forget();
            MobControl.Possessed.Clear();
            names.Clear();
        }
    }

    // The game stops animating monsters its camera can't see (AnimatedActor.cullingMode: CullVisualsOnly
    // freezes the pose, CullVisualsAndLogic also pauses their actions). The room owner's camera often isn't
    // where the other player is fighting, so while both share a room the owner keeps every monster there
    // fully animated, and puts the setting back afterwards.
    // Diagnostics for moonwalking, over every monster: "drawn backwards" = the model's mirroring doesn't
    // match the way the monster is turned; "walks backwards" = it moves opposite to the way it is turned.
    internal static class MoonwalkProbe
    {
        static readonly Dictionary<IntPtr, (Vector3 p, float t)> last = new Dictionary<IntPtr, (Vector3, float)>();
        static readonly HashSet<string> who = new HashSet<string>();
        static int samples, drawnBack, walkBack;
        static float next;

        public static void Observe(Entity mob, MobChain c, string side)
        {
            try
            {
                float now = Time.unscaledTime;
                var p = mob.transform.position;
                float fx = mob.transform.forward.x;
                if (!last.TryGetValue(mob.Pointer, out var l)) { last[mob.Pointer] = (p, now); return; }
                if (now - l.t < 0.05f) return;
                float vx = (p.x - l.p.x) / (now - l.t);
                last[mob.Pointer] = (p, now);
                samples++;
                var flip = c.Flips.Length > 0 ? c.Flips[0] : null;
                float mesh = flip != null ? flip.transform.localScale.x : fx;
                if (Mathf.Abs(fx) > 0.5f && Mathf.Sign(mesh) != Mathf.Sign(fx)) { drawnBack++; who.Add($"{mob.name} drawn backwards"); }
                if (Mathf.Abs(vx) > 0.8f && Mathf.Abs(fx) > 0.5f && Mathf.Sign(vx) != Mathf.Sign(fx)) { walkBack++; who.Add($"{mob.name} walks backwards (vx {vx:0.0})"); }
                if (now >= next)
                {
                    next = now + 5f;
                    if (samples > 0)
                        Plugin.Logger.LogInfo($"[Session] Moonwalk check ({side}): {samples} samples, {drawnBack} drawn backwards, {walkBack} walking backwards" +
                            (who.Count > 0 ? ": " + string.Join("; ", who) : "") + " (last 5 s).");
                    samples = drawnBack = walkBack = 0;
                    who.Clear();
                }
            }
            catch { }
        }
    }

    // The boss health bar only redraws on the game's health events; health we set from the other player's
    // hits doesn't send one, so tell the bar to re-read the boss's health.
    internal static class BossHud
    {
        static HUDBossManager hud;
        static bool broken;

        public static void Refresh(Entity mob, Damageable d)
        {
            if (broken || mob == null || d == null || mob.GetComponent<BossComponent>() == null) return;
            try
            {
                if (hud == null) hud = UnityEngine.Object.FindObjectOfType<HUDBossManager>();
                var bar = hud != null ? hud.bossHealthController : null;
                if (bar == null) return;
                var ev = new EntityInitHealthEvent { isPlayer = false, e = mob, damageable = d };
                bar.ProcessInitHealthEvent(ev);
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.Logger.LogWarning($"[Session] Could not refresh the boss health bar: {e.Message}");
            }
        }
    }

    // Co-op difficulty, like the Souls games: while both players are in a room we run, its monsters (and more so
    // its boss) get more health; when the other player leaves, they go back. The other game copies health.
    internal static class CoopScaling
    {
        static readonly Dictionary<IntPtr, (Entity mob, float factor)> scaled = new Dictionary<IntPtr, (Entity, float)>();
        static float nextCheck;
        static int logs = 10;

        static void Scale(Entity mob, Damageable d, float f)
        {
            int max = d.maxHealth, cur = d.currentHealth;
            d.maxHealth = Mathf.Max(1, Mathf.RoundToInt(max * f));
            if (cur > 0) d.currentHealth = Mathf.Clamp(Mathf.RoundToInt(cur * f), 1, d.maxHealth);
            BossHud.Refresh(mob, d);
        }

        public static void Apply(string roomId, float monster, float boss)
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || scaled.ContainsKey(mob.Pointer) || !MobRegistry.InRoom(kv.Key, roomId) || !MobRegistry.IsAlive(mob)) continue;
                try
                {
                    var d = MobControl.DamageableOf(mob);
                    if (d == null || d.maxHealth <= 0) continue;
                    float f = mob.GetComponent<BossComponent>() != null ? boss : monster;
                    if (Mathf.Approximately(f, 1f)) continue;
                    int before = d.maxHealth;
                    Scale(mob, d, f);
                    scaled[mob.Pointer] = (mob, f);
                    if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] Co-op difficulty: '{MobRegistry.PrefabName(mob)}' health {before} -> {d.maxHealth} (x{f:0.##}).");
                }
                catch { }
            }
        }

        public static void RevertAll()
        {
            if (scaled.Count == 0) return;
            foreach (var kv in scaled)
            {
                try
                {
                    var mob = kv.Value.mob;
                    if (mob == null || !MobRegistry.IsAlive(mob)) continue;
                    var d = MobControl.DamageableOf(mob);
                    if (d != null) Scale(mob, d, 1f / kv.Value.factor);
                }
                catch { }
            }
            scaled.Clear();
        }
    }

    internal static class MobCulling
    {
        static readonly Dictionary<IntPtr, (AnimatedActor actor, AnimatedActor.CullingMode mode)> changed =
            new Dictionary<IntPtr, (AnimatedActor, AnimatedActor.CullingMode)>();
        static readonly Dictionary<IntPtr, (Animator animator, AnimatorCullingMode mode)> animators =
            new Dictionary<IntPtr, (Animator, AnimatorCullingMode)>();
        static float nextRefresh;
        static bool logged;

        public static void KeepAnimating(string roomId)
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.5f;
            int n = 0;
            awake.Clear();
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || !MobRegistry.InRoom(kv.Key, roomId)) continue;
                awake.Add(mob.Pointer);
                try
                {
                    // Unity's own Animator culling: off every camera it stops moving the bones, so a monster
                    // walking up to the other player glides with frozen legs.
                    foreach (var an in mob.GetComponentsInChildren<Animator>(true))
                    {
                        if (an == null || an.cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
                        if (!animators.ContainsKey(an.Pointer)) animators[an.Pointer] = (an, an.cullingMode);
                        an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        n++;
                    }
                    foreach (var a in mob.GetComponentsInChildren<AnimatedActor>(true))
                    {
                        if (a == null || a.cullingMode == AnimatedActor.CullingMode.DontCull) continue;
                        if (!changed.ContainsKey(a.Pointer)) changed[a.Pointer] = (a, a.cullingMode);
                        a.cullingMode = AnimatedActor.CullingMode.DontCull;
                        n++;
                    }
                }
                catch { }
            }
            if (n > 0 && !logged) { logged = true; Plugin.Logger.LogInfo($"[Session] Keeping {n} monster animators running off-screen while the other player is in the room."); }
        }

        // The room manager also culls whole map chunks away from our Rose: their monsters get the Culled tag
        // and stop animating. Monsters of the shared room stay awake while the other player is in it.
        static readonly HashSet<IntPtr> awake = new HashSet<IntPtr>();
        static int woken;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(RoomManager.RoomManagerSystem), "UpdateEntityCulling")]
        static bool StayAwake(Entity __0, AbstractRedWineCullable __1)
        {
            if (__0 == null || awake.Count == 0 || !awake.Contains(__0.Pointer)) return true;
            try
            {
                var culled = Il2CppType.Of<Culled>();
                if (__0.HasComponentData(culled))
                {
                    __0.RemoveComponentDataIfAny(culled);
                    if (__1 != null) __1.UpdateCulling(__0, false);
                    if (woken++ < 3) Plugin.Logger.LogInfo($"[Session] Woke up monster '{__0.name}' (its map chunk was culled on our side).");
                }
                return false;
            }
            catch { return true; }
        }

        public static void Restore()
        {
            awake.Clear();
            foreach (var kv in changed)
            {
                try { if (kv.Value.actor != null) kv.Value.actor.cullingMode = kv.Value.mode; } catch { }
            }
            changed.Clear();
            foreach (var kv in animators)
            {
                try { if (kv.Value.animator != null) kv.Value.animator.cullingMode = kv.Value.mode; } catch { }
            }
            animators.Clear();
            logged = false;
        }
    }

    internal static class MobPatches
    {
        // Runs after all of the game's late systems (animation included) each frame.
        internal static Action AfterGameLateUpdate;
        // Runs right after the game animates a monster we mirror.
        internal static Action<Entity> AfterMobAnimated;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimatedActor), nameof(AnimatedActor.UpdateAnimation))]
        static void MobAnimated(Entity __0)
        {
            if (AfterMobAnimated == null || !MobControl.IsPossessed(__0)) return;
            try { AfterMobAnimated(__0); } catch { }
        }

        // "Close enough to swing" for melee monsters is a physics overlap on the player layers that must find a
        // collider on the target's own object. The other Rose's colliders are triggers (so she never shoves
        // anything), and the game's queries may skip triggers: count them while a monster checks her.
        static bool savedHitTriggers;
        static int puppetInRange, puppetOutOfRange, lastSyncFrame = -1;
        static float nextRangeReport;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AIConditionCheckDetectionShape), nameof(AIConditionCheckDetectionShape.IsConditionValidated))]
        static void RangeCheckStart(Entity __0, out bool __state)
        {
            __state = false;
            try
            {
                var puppet = Puppet.Current;
                if (puppet == null || __0 == null) return;
                var ai = __0.GetComponent<AIStateComponent>();
                if (ai == null || ai.target == null || ai.target.Pointer != puppet.Pointer) return;
                __state = true;
                savedHitTriggers = Physics.queriesHitTriggers;
                Physics.queriesHitTriggers = true;
                // The puppet is moved by script; make sure physics sees where she is now.
                if (lastSyncFrame != Time.frameCount) { lastSyncFrame = Time.frameCount; Physics.SyncTransforms(); }
            }
            catch { }
        }

        // Monsters only chase and attack while they are on screen (AIConditionCheckVisible tests the local
        // camera). A monster after the other player is on THEIR screen, not ours: count it as visible when it
        // would be in our camera's view if our camera were centred on the other Rose the way it is on ours.
        static int visibleForOther;
        static float nextVisibleReport;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIConditionCheckVisible), nameof(AIConditionCheckVisible.IsConditionValidated))]
        static void VisibleForOtherPlayer(Entity __0, ref bool __result)
        {
            if (__result) return;
            try
            {
                var puppet = Puppet.Current;
                if (puppet == null || __0 == null) return;
                var ai = __0.GetComponent<AIStateComponent>();
                if (ai == null || ai.target == null || ai.target.Pointer != puppet.Pointer) return;
                var cam = Camera.main;
                var me = GameAccess.MainCharacter();
                if (cam == null || me == null) return;
                // Where the monster sits relative to the other Rose, put next to our Rose instead.
                var shifted = __0.transform.position - puppet.transform.position + me.transform.position;
                var v = cam.WorldToViewportPoint(shifted);
                if (v.z > 0f && v.x > -0.05f && v.x < 1.05f && v.y > -0.05f && v.y < 1.05f)
                {
                    __result = true;
                    visibleForOther++;
                }
                if (Time.unscaledTime >= nextVisibleReport && visibleForOther > 0)
                {
                    nextVisibleReport = Time.unscaledTime + 10f;
                    Plugin.Logger.LogInfo($"[Session] Monsters kept active on the other player's screen: {visibleForOther} checks (last 10 s).");
                    visibleForOther = 0;
                }
            }
            catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIConditionCheckDetectionShape), nameof(AIConditionCheckDetectionShape.IsConditionValidated))]
        static void RangeCheckDone(bool __state, bool __result)
        {
            if (!__state) return;
            Physics.queriesHitTriggers = savedHitTriggers;
            if (__result) puppetInRange++; else puppetOutOfRange++;
            if (Time.unscaledTime >= nextRangeReport)
            {
                nextRangeReport = Time.unscaledTime + 10f;
                Plugin.Logger.LogInfo($"[Session] Monster range checks on the other Rose: {puppetInRange} in reach, {puppetOutOfRange} not (last 10 s).");
                puppetInRange = puppetOutOfRange = 0;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SystemManager), nameof(SystemManager.LateUpdate))]
        static void GameLateUpdateDone()
        {
            try { AfterGameLateUpdate?.Invoke(); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseMobsSpawner), "SpawnMobInternal")]
        static void Spawned(BaseMobsSpawner __instance, Entity __result)
        {
            try { MobRegistry.OnSpawned(__instance, __result); } catch { }
        }

        // A spawner (re)initializing as its room loads starts counting from 1, on both PCs.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseMobsSpawner), nameof(BaseMobsSpawner.InitializeSpawner))]
        static void SpawnerInit(BaseMobsSpawner __instance) => MobRegistry.OnSpawnerReset(__instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobsSpawner), nameof(MobsSpawner.ResetSpawner))]
        static void SpawnerReset(MobsSpawner __instance) => MobRegistry.OnSpawnerReset(__instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobsSpawner), nameof(MobsSpawner.ResetSpawnedMobsCount))]
        static void SpawnerCountReset(MobsSpawner __instance) => MobRegistry.OnSpawnerReset(__instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobsSpawner), nameof(MobsSpawner.DoSoftResetRespawn))]
        static void SpawnerSoftReset(MobsSpawner __instance) => MobRegistry.OnSpawnerReset(__instance);

        // Possessed monsters: no AI decisions and no own movement; only RoseNet starts their actions.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Actor), nameof(Actor.AutoControlUpdate))]
        static bool NoAi(Entity __0) => !MobControl.IsPossessed(__0);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Actor), nameof(Actor.MovementUpdate))]
        static bool NoMovement(Entity __0) => !MobControl.IsPossessed(__0);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Actor), nameof(Actor.StartActionInternal))]
        static bool OnlyMirroredActions(Entity __0) => MobControl.AllowStart || !MobControl.IsPossessed(__0);

        // Our hits on monsters the other player's game runs: they land here (effects, numbers), the damage
        // is sent to the owner, and the monster can't die here except when the owner says so.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Damageable), nameof(Damageable.ApplyDamages))]
        static void BeforeDamage(Damageable __instance, Entity __0, out int __state)
        {
            __state = -1;
            try { if (MobControl.IsPossessed(__0)) __state = __instance.currentHealth; } catch { }
        }

        // Separate hooks to keep a copy of a real hit (its data and its "connection"). Kept apart from the
        // hit detection above so that if one of them can't be hooked, the other still works.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Damageable), nameof(Damageable.ApplyDamages))]
        static void KeepHitData(Entity __1, DamageData __2)
        {
            try
            {
                if (__2 == null) return;
                var me = GameAccess.MainCharacter();
                if (MobControl.LastPlayerDamage == null || (me != null && __1 != null && __1.Pointer == me.Pointer))
                    MobControl.LastPlayerDamage = __2; // prefer our own Rose's hits; until then any real hit
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Damageable), nameof(Damageable.ApplyDamages))]
        static void KeepHitConnection(DamageSystem.Connection __3)
        {
            try { if (__3 != null) MobControl.LastPlayerConnection = __3; } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Damageable), nameof(Damageable.ApplyDamages))]
        static void AfterDamage(Damageable __instance, Entity __0, int __state)
        {
            if (__state < 0) return;
            try
            {
                int dealt = __state - __instance.currentHealth;
                if (dealt <= 0 || !MobRegistry.IdOf.TryGetValue(__0.Pointer, out var id)) return;
                MobControl.OutgoingHits.Add((id, dealt));
                MobControl.LastLocalHit[__0.Pointer] = Time.unscaledTime;
                // It can't die here (Die is blocked for possessed monsters); the owner decides the kill.
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Damageable), nameof(Damageable.Die))]
        static bool NoLocalDeath(Entity __0) => MobControl.AllowDeath || !MobControl.IsPossessed(__0) || (__0 != null && MobControl.OwnerKilled.Contains(__0.Pointer));

        // Monsters look mainly at the main character, so the other player's puppet is offered explicitly:
        // when a monster acquires a target, and about once a second while it chases one of the two players,
        // it goes for whichever is closer. Its attacks are mirrored to the other player's game, where they
        // hit the real Rose.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BasicBrain), nameof(BasicBrain.TargetAcquisition))]
        static void OfferPuppet(BasicBrain.ThinkingContext __0, ref bool __result)
        {
            try
            {
                if (__0 == null || __0.aiState == null || __0.entity == null) return;
                if (MobTargeting.TryPickCloser(__0.entity, __0.aiState, acquiring: true)) __result = true;
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BasicBrain), nameof(BasicBrain.Think))]
        static void RetargetNearest(Entity __0)
        {
            try
            {
                if (__0 == null) return;
                var ai = __0.GetComponent<AIStateComponent>();
                if (ai == null || ai.target == null) return;
                // Dropping a stand-in that is no longer here can't wait for the next once-a-second check.
                bool stale = Puppet.Current != null && ai.target.Pointer == Puppet.Current.Pointer && !MobTargeting.PuppetHere();
                if (!stale && !MobTargeting.Due(__0)) return;
                MobTargeting.TryPickCloser(__0, ai, acquiring: false);
            }
            catch { }
        }

        // (Kept: these accept the puppet where the game checks a target by its health/team.)
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BasicBrain), nameof(BasicBrain.CanBeTargeted))]
        static void PuppetIsTargetable(Entity __1, ref bool __result)
        {
            if (!__result && __1 != null && Puppet.Entities.Contains(__1.Pointer) && MobTargeting.PuppetHere()) __result = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MobPackComponent), nameof(MobPackComponent.CanTargetEntity))]
        static void PackMayTargetPuppet(Entity __0, ref bool __result)
        {
            if (!__result && __0 != null && Puppet.Entities.Contains(__0.Pointer) && MobTargeting.PuppetHere()) __result = true;
        }
    }
}
