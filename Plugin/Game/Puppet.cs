using System;
using System.Collections.Generic;
using System.Text;
using MotherBase.FeedbackManager;
using RoseNet.Session;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // What gets mirrored of a Rose: the bones of every skinned mesh under the visual root, and every
    // renderer under the character (weapons, whip, trails are switched on/off by the game during attacks).
    // Both lists are ordered by path so two copies of the same model list them identically; the hash lets
    // each side check that it knows the other's lists.
    internal sealed class Skeleton
    {
        public Transform Entity;
        public Transform Root;
        public Transform[] Bones;
        public string[] Paths;
        public Renderer[] Renderers;
        public string[] RendererKeys;
        public uint Hash;

        public static Skeleton Capture(Transform entity, Transform root)
        {
            var found = new Dictionary<string, Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                if (bones == null) continue;
                foreach (var b in bones)
                {
                    if (b == null) continue;
                    var path = PathOf(b, root);
                    if (path != null && !found.ContainsKey(path)) found[path] = b;
                }
            }
            var paths = new List<string>(found.Keys);
            paths.Sort(StringComparer.Ordinal);
            if (paths.Count > 512) paths.RemoveRange(512, paths.Count - 512);

            var renderers = RendererMap(entity);
            var keys = new List<string>(renderers.Keys);
            keys.Sort(StringComparer.Ordinal);
            if (keys.Count > 1024) keys.RemoveRange(1024, keys.Count - 1024);

            var s = new Skeleton
            {
                Entity = entity, Root = root,
                Paths = paths.ToArray(), Bones = new Transform[paths.Count],
                RendererKeys = keys.ToArray(), Renderers = new Renderer[keys.Count],
            };
            for (int i = 0; i < paths.Count; i++) s.Bones[i] = found[paths[i]];
            for (int i = 0; i < keys.Count; i++) s.Renderers[i] = renderers[keys[i]];
            s.Hash = HashLists(s.Paths, s.RendererKeys);
            return s;
        }

        // Renderers under entity keyed by "path#Type#n" (n = index among same-type renderers on that object).
        public static Dictionary<string, Renderer> RendererMap(Transform entity)
        {
            var map = new Dictionary<string, Renderer>();
            foreach (var r in entity.GetComponentsInChildren<Renderer>(true))
            {
                var path = PathOf(r.transform, entity);
                if (path == null) continue;
                var type = r.GetIl2CppType().Name;
                int n = 0;
                string key;
                do key = $"{path}#{type}#{n++}"; while (map.ContainsKey(key));
                map[key] = r;
            }
            return map;
        }

        public static uint HashLists(string[] bones, string[] renderers)
        {
            uint h = 2166136261;
            void Add(string p)
            {
                foreach (var b in Encoding.UTF8.GetBytes(p)) { h ^= b; h *= 16777619; }
                h ^= 0x2F; h *= 16777619;
            }
            foreach (var p in bones) Add(p);
            Add("|renderers|");
            foreach (var p in renderers) Add(p);
            return h;
        }

        // Path relative to root, or null if t is not under root.
        public static string PathOf(Transform t, Transform root)
        {
            var sb = new StringBuilder();
            for (var cur = t; cur != null; cur = cur.parent)
            {
                if (cur == root) return sb.ToString();
                sb.Insert(0, sb.Length == 0 ? cur.name : cur.name + "/");
            }
            return null;
        }

        public void Write(PoseState s)
        {
            s.EntityPosition = Entity.position;
            s.EntityRotation = Entity.rotation;
            s.EntityScale = Entity.lossyScale;
            s.RootPosition = Root.position;
            s.RootRotation = Root.rotation;
            s.RootScale = Root.lossyScale;
            s.SkeletonHash = Hash;
            s.EnsureBones(Bones.Length);
            for (int i = 0; i < Bones.Length; i++)
            {
                var b = Bones[i];
                if (b == null) { s.Rotations[i] = Quaternion.identity; continue; }
                s.Rotations[i] = b.localRotation;
                s.Positions[i] = b.localPosition;
            }
            s.EnsureRenderers(Renderers.Length);
            var toLocal = Quaternion.Inverse(Entity.rotation);
            for (int i = 0; i < Renderers.Length; i++)
            {
                var r = Renderers[i];
                s.Visible[i] = r != null && r.enabled && r.gameObject.activeInHierarchy;
                if (r == null) continue;
                var t = r.transform;
                s.PartPositions[i] = Entity.InverseTransformPoint(t.position);
                s.PartRotations[i] = toLocal * t.rotation;
            }
        }
    }

    // The other player's Rose: a clone of our own Rose with all gameplay removed. Kept: transforms and
    // renderers (to be seen), the Entity + FeedbackContainer (so the game's own attack effects can be
    // played on it), and the trail/particle components those effects drive. Posed from network data.
    internal sealed class Puppet
    {
        GameObject holder;
        Transform entityRoot, visualRoot;
        FeedbackContainer effects;
        Transform[] boneMap = new Transform[0];      // index = sender's bone index
        Renderer[] rendererMap = new Renderer[0];    // index = sender's renderer index
        bool[] movesWithBones = new bool[0];         // skinned meshes follow the bones; others are placed directly
        uint mappedHash;
        bool visible = true;

        public bool HasSkeleton(uint hash) => mappedHash == hash && boneMap.Length > 0;
        public bool Alive => holder != null && entityRoot != null && visualRoot != null;
        public bool Visible => visible;
        public bool HasEffects => effects != null;

        public static Puppet Create(Transform sourceEntity, Transform sourceVisual)
        {
            string visualPath = Skeleton.PathOf(sourceVisual, sourceEntity);
            // Clone under an inactive holder so no Awake/OnEnable runs before the scripts are stripped.
            var holder = new GameObject("RoseNet Remote Rose");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            var clone = Object.Instantiate(sourceEntity.gameObject, holder.transform);
            clone.name = "Remote Rose";
            int removed = StripGameplay(clone, keepTargetColliders: true);
            var visual = string.IsNullOrEmpty(visualPath) ? clone.transform : clone.transform.Find(visualPath);
            var puppet = new Puppet
            {
                holder = holder,
                entityRoot = clone.transform,
                visualRoot = visual != null ? visual : clone.transform,
                effects = clone.GetComponentInChildren<FeedbackContainer>(true),
            };
            var puppetEntity = clone.GetComponent<MotherBase.EntityFramework.Entity>();
            if (puppetEntity != null) { Entities.Add(puppetEntity.Pointer); Current = puppetEntity; }
            holder.SetActive(true);
            Plugin.Logger.LogInfo($"[Session] Created remote Rose puppet ({removed} gameplay components removed, " +
                $"source has effect player: {sourceEntity.GetComponentInChildren<FeedbackContainer>(true) != null}, puppet: {puppet.effects != null}).");
            Plugin.Logger.LogInfo($"[Session] Rose colliders (monsters sense these): real {DescribeColliders(sourceEntity.gameObject)}; puppet {DescribeColliders(clone)}; " +
                $"player layers 0x{PlayerLayerMask():X}, triggers count in queries: {Physics.queriesHitTriggers}.");
            return puppet;
        }

        public void Bind(uint hash, string[] bonePaths, string[] rendererKeys)
        {
            boneMap = new Transform[bonePaths.Length];
            var byPath = new Dictionary<string, Transform>();
            foreach (var t in visualRoot.GetComponentsInChildren<Transform>(true))
            {
                var p = Skeleton.PathOf(t, visualRoot);
                if (p != null && !byPath.ContainsKey(p)) byPath[p] = t;
            }
            int bones = 0;
            for (int i = 0; i < bonePaths.Length; i++)
                if (byPath.TryGetValue(bonePaths[i], out boneMap[i])) bones++;

            var mine = Skeleton.RendererMap(entityRoot);
            rendererMap = new Renderer[rendererKeys.Length];
            movesWithBones = new bool[rendererKeys.Length];
            int renderers = 0;
            for (int i = 0; i < rendererKeys.Length; i++)
            {
                if (!mine.TryGetValue(rendererKeys[i], out rendererMap[i])) continue;
                renderers++;
                movesWithBones[i] = rendererMap[i].TryCast<SkinnedMeshRenderer>() != null;
            }

            mappedHash = hash;
            Plugin.Logger.LogInfo($"[Session] Remote Rose bound: {bones}/{bonePaths.Length} bones, {renderers}/{rendererKeys.Length} visible parts matched.");
        }

        ActorPlatform platformInfo;
        bool platformLooked;

        public void Apply(PoseState s)
        {
            if (!Alive) return;
            entityRoot.SetPositionAndRotation(s.EntityPosition, s.EntityRotation);
            // Which platform we stand on: left empty, so the game looks up the one under our position (the copied
            // value would be wherever the real Rose stood when this stand-in was made).
            try
            {
                if (!platformLooked) { platformLooked = true; platformInfo = entityRoot.GetComponent<ActorPlatform>(); }
                if (platformInfo != null && platformInfo.currentPlatform != null) platformInfo.currentPlatform = null;
            }
            catch { }
            entityRoot.localScale = s.EntityScale;
            visualRoot.SetPositionAndRotation(s.RootPosition, s.RootRotation);
            int n = Math.Min(s.BoneCount, boneMap.Length);
            for (int i = 0; i < n; i++)
            {
                var t = boneMap[i];
                if (t == null) continue;
                t.localRotation = s.Rotations[i];
                t.localPosition = s.Positions[i];
            }
            int rc = Math.Min(s.RendererCount, rendererMap.Length);
            for (int i = 0; i < rc; i++)
            {
                var r = rendererMap[i];
                if (r == null) continue;
                bool show = s.Visible[i];
                if (show && !r.gameObject.activeSelf) r.gameObject.SetActive(true);
                if (r.enabled != show) r.enabled = show;
                if (show && !movesWithBones[i])
                    r.transform.SetPositionAndRotation(entityRoot.TransformPoint(s.PartPositions[i]), entityRoot.rotation * s.PartRotations[i]);
            }
        }

        // Returns the puppet-side id of the started effect, or null.
        public Il2CppSystem.Guid? StartEffect(Feedback feedback)
        {
            if (effects == null || feedback == null || !visible) return null;
            return effects.StartFeedback(feedback);
        }

        public void StopEffect(Il2CppSystem.Guid id, bool force)
        {
            if (effects == null) return;
            if (force) effects.ForceStopFeedback(id); else effects.StopFeedback(id);
        }

        // Diagnostics: every feature of the newest effect on a character, started or still waiting.
        public static string DescribeNewest(FeedbackContainer container, string onlyName = null)
        {
            var active = container?.activeFeedbackInstances;
            if (active == null) return "no effect player";
            for (int k = active.Count - 1; k >= 0; k--)
            {
                var inst = active[k];
                if (onlyName != null && inst.feedback?.name != onlyName) continue;
                var sb = new StringBuilder($"'{inst.feedback?.name}' frame {inst.elapsedFrames}:");
                if (inst.activeFeatures != null)
                    foreach (var f in inst.activeFeatures)
                    {
                        sb.Append($" started {f.GetIl2CppType().Name}@{f.startingFrame}");
                        var vfx = f.TryCast<NewVfxFeedbackInstance>();
                        if (vfx?._particleInstance != null) sb.Append($"('{vfx._particleInstance.name}' on '{vfx._attachementPoint?.name}')");
                    }
                if (inst.unstartedFeatures != null)
                    foreach (var f in inst.unstartedFeatures) sb.Append($" waiting {f.GetIl2CppType().Name}@{f.startingFrame}");
                if (inst.unpreparedFeatures != null)
                    foreach (var f in inst.unpreparedFeatures) sb.Append($" unprepared {f.GetIl2CppType().Name}@{f.startingFrame}");
                return sb.ToString();
            }
            return onlyName != null ? $"'{onlyName}' already finished" : "no active effects";
        }

        public FeedbackContainer Effects => effects;

        VFXBindingsComponent vfxBindings;
        readonly Dictionary<string, VFXBindingsTable> addedTables = new Dictionary<string, VFXBindingsTable>();

        // Register the other player's effect tables (their weapon's slash etc.) on the puppet, as equipping does.
        public void SyncEffectTables(List<string> names)
        {
            if (vfxBindings == null && entityRoot != null) vfxBindings = entityRoot.GetComponentInChildren<VFXBindingsComponent>(true);
            if (vfxBindings == null || names == null) return;
            string own = vfxBindings._bindings != null ? vfxBindings._bindings.name : null;
            foreach (var name in names)
            {
                if (name == own || addedTables.ContainsKey(name)) continue;
                var table = EffectRelay.FindTable(name);
                if (table == null) continue; // not loaded on this PC yet; retried on the next update
                vfxBindings.AddBindings(table);
                addedTables[name] = table;
                Plugin.Logger.LogInfo($"[Session] Remote Rose effect table '{name}' registered.");
            }
            var stale = new List<string>();
            foreach (var kv in addedTables) if (!names.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var name in stale)
            {
                vfxBindings.RemoveBindings(addedTables[name]);
                addedTables.Remove(name);
            }
        }

        // Diagnostics: is the game's effect system actually running the effects we start on the puppet?
        public string DescribeEffects()
        {
            if (effects == null) return "no effect player";
            var sb = new StringBuilder();
            sb.Append($"queued {effects.toStartFeedbackInstances?.Count ?? -1}, active {effects.activeFeedbackInstances?.Count ?? -1}");
            var active = effects.activeFeedbackInstances;
            if (active != null && active.Count > 0)
            {
                var inst = active[active.Count - 1];
                sb.Append($"; newest '{inst.feedback?.name}' features active {inst.activeFeatures?.Count ?? -1}, unstarted {inst.unstartedFeatures?.Count ?? -1}, unprepared {inst.unpreparedFeatures?.Count ?? -1}");
                if (inst.activeFeatures != null)
                    for (int i = 0; i < inst.activeFeatures.Count && i < 6; i++)
                    {
                        var f = inst.activeFeatures[i];
                        sb.Append(i == 0 ? " [" : ", ").Append(f.GetIl2CppType().Name);
                        var vfx = f.TryCast<NewVfxFeedbackInstance>();
                        if (vfx != null)
                        {
                            var ps = vfx._particleInstance;
                            sb.Append(ps == null ? "(no particles)"
                                : $"(particles '{ps.name}' at {ps.transform.position} active {ps.gameObject.activeInHierarchy} playing {ps.isPlaying}, attach '{(vfx._attachementPoint != null ? vfx._attachementPoint.name : "none")}')");
                        }
                        if (i == Math.Min(inst.activeFeatures.Count, 6) - 1) sb.Append(']');
                    }
            }
            if (entityRoot != null)
            {
                sb.Append($"; puppet at {entityRoot.position}");
            }
            var entity = entityRoot != null ? entityRoot.GetComponent<MotherBase.EntityFramework.Entity>() : null;
            if (entity != null)
            {
                try { sb.Append($"; puppet entity enabled {entity.context.IsEntityEnabled(entity)}"); } catch { sb.Append("; puppet entity not in a context"); }
            }
            return sb.ToString();
        }

        // Color the other Rose so the two players can be told apart. Uses the same shader color properties
        // RoseCoop uses for its players; each renderer keeps its own base color, multiplied by the tint.
        static readonly string[] TintProperties = { "_Tint", "_TintColor", "_BaseColor", "_Color", "_MainColor" };

        public void ApplyTint(Color tint)
        {
            if (entityRoot == null) return;
            var block = new MaterialPropertyBlock();
            foreach (var r in entityRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r.TryCast<ParticleSystemRenderer>() != null || r.TryCast<TrailRenderer>() != null) continue;
                var mat = r.sharedMaterial;
                if (mat == null) continue;
                r.GetPropertyBlock(block);
                bool any = false;
                foreach (var prop in TintProperties)
                {
                    if (!mat.HasProperty(prop)) continue;
                    block.SetColor(prop, mat.GetColor(prop) * tint);
                    any = true;
                }
                if (any) r.SetPropertyBlock(block);
                block.Clear();
            }
        }

        // World position just above the other Rose's head, for the name tag.
        public bool TryGetHeadPosition(out Vector3 position)
        {
            position = default;
            if (!Alive || !visible) return false;
            bool found = false;
            Bounds b = default;
            foreach (var r in visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!r.enabled) continue;
                if (!found) { b = r.bounds; found = true; } else b.Encapsulate(r.bounds);
            }
            if (!found) return false;
            position = new Vector3(b.center.x, b.max.y + 0.35f, b.center.z);
            return true;
        }

        public void SetVisible(bool show)
        {
            if (holder == null || show == visible) return;
            visible = show;
            holder.SetActive(show);
        }

        public void Destroy()
        {
            try
            {
                var e = entityRoot != null ? entityRoot.GetComponent<MotherBase.EntityFramework.Entity>() : null;
                if (e != null) Entities.Remove(e.Pointer);
                if (e != null && Current != null && Current.Pointer == e.Pointer) Current = null;
            }
            catch { }
            if (holder != null) Object.Destroy(holder);
            holder = null;
            entityRoot = visualRoot = null;
        }

        // Entities of puppets: monsters may target them (see MobTargeting).
        internal static readonly HashSet<IntPtr> Entities = new HashSet<IntPtr>();
        internal static MotherBase.EntityFramework.Entity Current;   // the other player's Rose, for monsters

        // Removes everything that plays the game (scripts, colliders, physics, lights, audio listeners),
        // keeping what is seen and what the game's effect system needs. With keepTargetColliders, the
        // colliders on the layers monsters look for players on stay, as triggers: monsters can sense the
        // puppet, but it never pushes or blocks anything.
        public static int StripGameplay(GameObject clone, bool keepTargetColliders = false)
        {
            int playerLayers = keepTargetColliders ? PlayerLayerMask() : 0;
            int removed = 0;
            // Two passes: components that others depend on ([RequireComponent]) go on the second pass.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var c in clone.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || Keep(c)) continue;
                    var col = c.TryCast<Collider>();
                    // All of the Rose's colliders (body, hurtboxes): monsters need them to sense and reach her.
                    if (col != null && keepTargetColliders)
                    {
                        col.isTrigger = true;
                        continue;
                    }
                    try { Object.DestroyImmediate(c); removed++; } catch { }
                }
            }
            return removed;
        }

        static string DescribeColliders(GameObject root)
        {
            var sb = new StringBuilder();
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null) continue;
                var where = c.gameObject == root ? "root" : c.name;
                sb.Append($"[{where} {c.GetIl2CppType().Name} layer {c.gameObject.layer} trigger {c.isTrigger} on {c.enabled}] ");
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        // The layers monster brains search for players on.
        static int PlayerLayerMask()
        {
            int mask = 0;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<BasicBrain>()))
                {
                    var b = o.TryCast<BasicBrain>();
                    if (b != null) mask |= b.playerLayerMask.value;
                }
            }
            catch { }
            return mask;
        }

        static readonly HashSet<string> KeptTypes = new HashSet<string>
        {
            "Entity", "FeedbackContainer", "FeedbackBindingsReference", "AnimationBindingComponent",
            "ActorPlatform",   // monsters' "jump near the target's platform" attacks read it (Leon); data only
            "VFXBindingsComponent", "TransformBindings",   // effects find their particles and attach points here
            "ParticleSystem", "VisualEffect", "MeshFilter",
        };

        static bool Keep(Component c)
        {
            if (c.TryCast<Transform>() != null || c.TryCast<Renderer>() != null) return true;
            var name = c.GetIl2CppType().Name;
            return KeptTypes.Contains(name) || name.Contains("Trail");
        }
    }
}
