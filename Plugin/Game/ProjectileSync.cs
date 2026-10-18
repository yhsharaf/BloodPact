using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MotherBase.EntityFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // Flying spells are separate entities (the game's SpawnFeature spawns them from a prefab). The sender
    // tracks entities that switch on right after our own Rose ran a spawn, next to her; the receiver shows a
    // look-only copy of the same prefab and moves it along the received path.
    internal static class ProjectileTracker
    {
        internal struct Spawned { public ushort Id; public string Name; public Transform T; }

        const int MaxTracked = 64;
        const float CaptureRadius = 6f;
        static readonly Dictionary<IntPtr, (ushort id, Entity entity)> tracked = new Dictionary<IntPtr, (ushort, Entity)>();
        static readonly HashSet<ushort> endSent = new HashSet<ushort>();   // stopped (hit something) but still around
        internal static readonly List<Spawned> NewlySpawned = new List<Spawned>();
        internal static readonly List<ushort> Ended = new List<ushort>();
        static ushort nextId = 1;
        static int captureUntilFrame = -1;
        static int endLogs = 10;

        internal static bool Enabled;
        internal static Entity LocalRose;

        public static int IdOf(IntPtr entity) => tracked.TryGetValue(entity, out var t) ? t.id : -1;

        // Our Rose (or one of our projectiles, for spells that split) just ran a spawn: capture what appears.
        public static void OnSpawnRun(Entity caster)
        {
            if (!Enabled || caster == null || LocalRose == null) return;
            if (caster.Pointer != LocalRose.Pointer && !tracked.ContainsKey(caster.Pointer)) return;
            captureUntilFrame = Time.frameCount + 2;
        }

        public static void OnEntityEnabled(Entity e)
        {
            if (!Enabled || e == null || Time.frameCount > captureUntilFrame || LocalRose == null) return;
            if (e.Pointer == LocalRose.Pointer || tracked.ContainsKey(e.Pointer) || tracked.Count >= MaxTracked) return;
            if (e.name.StartsWith("Remote ") || e.name.StartsWith("RoseNet")) return;
            // Loot (experience/mana/health orbs) appears next to us when a monster dies: not our spell.
            var n = e.name;
            if (n.Contains("Experience") || n.Contains("Mana") || n.Contains("Health") || n.Contains("Gold") || n.Contains("Loot") || n.Contains("Pickup")) return;
            if ((e.transform.position - LocalRose.transform.position).sqrMagnitude > CaptureRadius * CaptureRadius) return;

            ushort id = nextId++;
            if (nextId == 0) nextId = 1;
            tracked[e.Pointer] = (id, e);
            NewlySpawned.Add(new Spawned { Id = id, Name = CleanName(e.name), T = e.transform });
            if (id <= 10) Plugin.Logger.LogInfo($"[Session] Our Rose spawned '{CleanName(e.name)}'; sending it to the other player.");
        }

        // Called once per send tick: lists flying projectiles and ends the ones that stopped or went away.
        // A projectile that hit a wall stays around for its impact, so it stays tracked (its impact effect
        // is still relayed) but counts as ended for movement.
        public static void Collect(List<(ushort id, Transform t)> alive)
        {
            alive.Clear();
            List<IntPtr> gone = null;
            foreach (var kv in tracked)
            {
                var e = kv.Value.entity;
                ushort id = kv.Value.id;
                bool exists = false, flying = false;
                try
                {
                    exists = e != null && e.gameObject.activeInHierarchy && !e.destroyed && e.enabled;
                    if (exists)
                    {
                        var bullet = e.GetComponent<BulletState>();
                        flying = bullet == null || !(bullet.isStopped || bullet.hasHitWall || bullet.toDestroyNextFrame);
                    }
                }
                catch { }

                if (exists && flying && !endSent.Contains(id)) alive.Add((id, e.transform));
                else if (endSent.Add(id))
                {
                    Ended.Add(id);
                    if (endLogs-- > 0) Plugin.Logger.LogInfo($"[Session] Our projectile {id} ended ({(exists ? "stopped/hit" : "removed")}).");
                }
                if (!exists) (gone ??= new List<IntPtr>()).Add(kv.Key);
            }
            if (gone == null) return;
            foreach (var p in gone)
            {
                endSent.Remove(tracked[p].id);
                tracked.Remove(p);
            }
        }

        public static void Reset()
        {
            tracked.Clear();
            endSent.Clear();
            NewlySpawned.Clear();
            Ended.Clear();
        }

        public static string CleanName(string n)
        {
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }
    }

    // Receiver side: look-only copies of the other player's projectiles.
    internal sealed class ProjectileMirror
    {
        sealed class Copy
        {
            public GameObject Holder;
            public Transform Root;
            public MotherBase.FeedbackManager.FeedbackContainer Effects;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public float EndAt = -1;
        }

        readonly Dictionary<ushort, Copy> copies = new Dictionary<ushort, Copy>();
        readonly Dictionary<string, GameObject> sources = new Dictionary<string, GameObject>();
        readonly HashSet<string> missingLogged = new HashSet<string>();
        float nextSourceRefresh;
        int shownCount;

        public void Spawn(ushort id, string name, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            if (copies.ContainsKey(id)) return;
            var source = FindSource(name);
            if (source == null)
            {
                if (missingLogged.Add(name)) Plugin.Logger.LogWarning($"[Session] Projectile '{name}' is not loaded on this PC; not shown.");
                return;
            }
            var holder = new GameObject("RoseNet Remote Projectile");
            holder.SetActive(false);
            var clone = Object.Instantiate(source, holder.transform);
            clone.name = "Remote " + name;
            clone.SetActive(true);
            Puppet.StripGameplay(clone);
            // The game gives projectiles their effect player when it spawns them, so the prefab copy may
            // lack one; add it so the flight glow and the impact explosion can play on the copy.
            if (clone.GetComponentInChildren<MotherBase.FeedbackManager.FeedbackContainer>(true) == null)
            {
                if (clone.GetComponent<Entity>() == null) clone.AddComponent<Entity>();
                if (clone.GetComponent<VFXBindingsComponent>() == null) clone.AddComponent<VFXBindingsComponent>();
                clone.AddComponent<MotherBase.FeedbackManager.FeedbackContainer>();
            }
            clone.transform.SetPositionAndRotation(pos, rot);
            clone.transform.localScale = scale;
            holder.SetActive(true);
            copies[id] = new Copy
            {
                Holder = holder, Root = clone.transform, Position = pos, Rotation = rot, Scale = scale,
                Effects = clone.GetComponentInChildren<MotherBase.FeedbackManager.FeedbackContainer>(true),
            };
            if (shownCount++ < 10)
                Plugin.Logger.LogInfo($"[Session] Showing the other player's '{name}' ({clone.GetComponentsInChildren<Renderer>(true).Length} visible parts, effects {(copies[id].Effects != null ? "on" : "off")}).");
        }

        public void Move(ushort id, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            if (!copies.TryGetValue(id, out var c) || c.EndAt >= 0) return;
            c.Position = pos;
            c.Rotation = rot;
            c.Scale = scale;
        }

        // The projectile stopped (hit a wall or enemy): its own particles vanish at once, like in the game;
        // the copy stays a moment longer so the relayed impact effect can still play on it.
        public void End(ushort id)
        {
            if (!copies.TryGetValue(id, out var c) || c.EndAt >= 0 || c.Root == null) return;
            c.EndAt = Time.unscaledTime + 1.5f;
            c.Root.position = c.Position;   // where it stopped, not where the smoothing had got to
            foreach (var ps in c.Root.GetComponentsInChildren<ParticleSystem>(true))
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (var r in c.Root.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
            if (ended++ < 10) Plugin.Logger.LogInfo($"[Session] The other player's projectile {id} ended; cleared its copy.");
        }

        int ended;

        // Still available while fading, so the impact effect can play where the projectile stopped.
        public MotherBase.FeedbackManager.FeedbackContainer EffectsOf(ushort id) =>
            copies.TryGetValue(id, out var c) && c.Root != null ? c.Effects : null;

        public void Update()
        {
            float k = 1f - Mathf.Exp(-25f * Time.unscaledDeltaTime);
            List<ushort> remove = null;
            foreach (var kv in copies)
            {
                var c = kv.Value;
                if (c.Root == null || (c.EndAt >= 0 && Time.unscaledTime >= c.EndAt))
                {
                    if (c.Holder != null) Object.Destroy(c.Holder);
                    (remove ??= new List<ushort>()).Add(kv.Key);
                    continue;
                }
                if (c.EndAt >= 0) continue;
                // Smooth toward the newest reported position; jump if it is far (spawned elsewhere, teleported).
                var p = c.Root.position;
                c.Root.position = (c.Position - p).sqrMagnitude > 16f ? c.Position : Vector3.Lerp(p, c.Position, k);
                c.Root.rotation = Quaternion.Slerp(c.Root.rotation, c.Rotation, k);
                c.Root.localScale = c.Scale;
            }
            if (remove != null) foreach (var id in remove) copies.Remove(id);
        }

        public void Clear()
        {
            foreach (var c in copies.Values) if (c.Holder != null) Object.Destroy(c.Holder);
            copies.Clear();
        }

        GameObject FindSource(string name)
        {
            if (sources.TryGetValue(name, out var s) && s != null) return s;
            if (Time.unscaledTime < nextSourceRefresh) return null;
            nextSourceRefresh = Time.unscaledTime + 3f;
            sources.Clear();
            // Prefab assets and pooled instances both qualify; prefer the asset (not in a scene).
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Entity>()))
            {
                var e = o.TryCast<Entity>();
                if (e == null) continue;
                var n = ProjectileTracker.CleanName(e.name);
                if (n.StartsWith("Remote ")) continue;
                bool isAsset = !e.gameObject.scene.IsValid();
                if (!sources.TryGetValue(n, out var have) || (isAsset && have.scene.IsValid())) sources[n] = e.gameObject;
            }
            return sources.TryGetValue(name, out s) ? s : null;
        }
    }

    internal static class ProjectilePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpawnFeature), nameof(SpawnFeature.UpdateSpawn))]
        static void SpawnRun(Entity __0)
        {
            try { ProjectileTracker.OnSpawnRun(__0); } catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpawnFeature), nameof(SpawnFeature.PlayEffect))]
        static void SpawnPlay(Entity __0)
        {
            try { ProjectileTracker.OnSpawnRun(__0); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Entity), nameof(Entity.OnEnable))]
        static void Enabled(Entity __instance)
        {
            try { ProjectileTracker.OnEntityEnabled(__instance); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EntityContext), nameof(EntityContext.EnableEntity_Internal))]
        static void ContextEnabled(Entity __0)
        {
            try { ProjectileTracker.OnEntityEnabled(__0); } catch { }
        }
    }
}
