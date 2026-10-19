using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MotherBase.FeedbackManager;
using UnityEngine;

namespace RoseNet.Game
{
    // Attack effects (whip trails, slashes, sparks) are the game's "Feedback" assets, started per character
    // through FeedbackContainer. Effects started on our own Rose are queued here and sent to the other
    // player, who plays the same asset (looked up by name) on their puppet of us.
    internal static class EffectRelay
    {
        internal struct Event
        {
            public bool Start;
            public bool Force;
            public ushort Target;   // 0 = our Rose, otherwise one of our projectiles (ProjectileTracker id)
            public string Name;
            public string Id;
        }

        internal static readonly List<Event> Outgoing = new List<Event>();
        internal static IntPtr LocalEntity;   // set by the session each frame; only its effects are sent
        internal static bool Enabled;
        internal static string LastLocalAttack; // diagnostics: newest weapon attack effect on our own Rose

        static readonly Dictionary<string, Feedback> byName = new Dictionary<string, Feedback>();
        static float nextRefresh;

        public static Feedback Find(string name)
        {
            if (byName.TryGetValue(name, out var f) && f != null) return f;
            if (Time.unscaledTime < nextRefresh) return null;
            nextRefresh = Time.unscaledTime + 5f;
            byName.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Feedback>()))
            {
                var fb = o.TryCast<Feedback>();
                if (fb != null && !byName.ContainsKey(fb.name)) byName[fb.name] = fb;
            }
            return byName.TryGetValue(name, out f) ? f : null;
        }

        // Effect tables (VFXBindingsTable) map an effect id such as "weapon slash" to the particle effect.
        // Weapons register theirs on the character when equipped, at run time, so a clone does not have them.
        public static List<string> TableNames(Component character)
        {
            var names = new List<string>();
            var comp = character.GetComponentInChildren<VFXBindingsComponent>(true);
            if (comp == null) return names;
            if (comp._bindings != null) names.Add(comp._bindings.name);
            var caches = comp._tableCaches;
            if (caches != null)
                foreach (var kv in caches)
                    if (kv.Key != null && !names.Contains(kv.Key.name)) names.Add(kv.Key.name);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        static readonly Dictionary<string, VFXBindingsTable> tables = new Dictionary<string, VFXBindingsTable>();
        static float nextTableRefresh;

        public static VFXBindingsTable FindTable(string name)
        {
            if (tables.TryGetValue(name, out var t) && t != null) return t;
            if (Time.unscaledTime < nextTableRefresh) return null;
            nextTableRefresh = Time.unscaledTime + 5f;
            tables.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<VFXBindingsTable>()))
            {
                var tb = o.TryCast<VFXBindingsTable>();
                if (tb != null && !tables.ContainsKey(tb.name)) tables[tb.name] = tb;
            }
            return tables.TryGetValue(name, out t) ? t : null;
        }

        // 0 = our Rose, >0 = one of our tracked projectiles, -1 = not ours (enemies, puppets, ...).
        // Parts of an effect that act on the whole screen or the player's own controls: camera shake, fades
        // (falling into spikes fades to black), hit flashes, slow motion, rumble, HUD. On the other player's
        // stand-in they would hit OUR screen, so only the parts drawn on their body are played.
        static readonly HashSet<string> ScreenParts = new HashSet<string>
        {
            "CameraImpulseFeedbackFeature", "CameraShakeFeedbackFeature", "DeathBreakScreenEffectFeedbackFeature",
            "EnvironmentDarkenFeedbackFeature", "BackgroundDarkenerFeedbackFeature", "SkyThunderFeedbackFeature",
            "FadeFromBlackFeedbackFeature", "FadeFromColorFeedbackFeature", "FadeToBlackFeedbackFeature", "FadeToColorFeedbackFeature",
            "DrawFullscreenQuadFeedbackFeature", "LensDistortionFeedbackFeature", "VignetteFeedbackFeature",
            "ColorAdjustmentFeedbackFeature", "ColorFilterAdjustmentFeedbackFeature", "ContrastAdjustmentFeedbackFeature",
            "PostExposureAdjustmentFeedbackFeature", "SaturationAdjustmentFeedbackFeature", "CustomPostProcessFeedbackFeature",
            "PostProcessingFeedbackFeature", "PostProcessOverTimeFeedbackFeature", "TimeDilatationFullscreenEffectFeedbackFeature",
            "SlowMoFeedbackFeature", "TimelapseFeedbackFeature", "RumbleFeedbackFeature", "LetterBoxingFeedbackFeature",
            "UIFeedbackFeature", "ChangeAlphaUIFeedbackFeature", "PopUpFeedbackFeature", "BossHUDInvulnerabilityFeedbackFeature",
        };
        static readonly Dictionary<IntPtr, List<FeedbackFeature>> screenPartsOf = new Dictionary<IntPtr, List<FeedbackFeature>>();
        static readonly HashSet<string> loggedDrops = new HashSet<string>();

        static List<FeedbackFeature> ScreenPartsOf(Feedback f)
        {
            if (screenPartsOf.TryGetValue(f.Pointer, out var list)) return list;
            list = new List<FeedbackFeature>();
            try
            {
                var parts = f.subAssetList;
                if (parts != null)
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var part = parts[i];
                        if (part != null && ScreenParts.Contains(part.GetIl2CppType().Name)) list.Add(part);
                    }
            }
            catch { }
            screenPartsOf[f.Pointer] = list;
            if (list.Count > 0 && loggedDrops.Count < 40 && loggedDrops.Add(f.name))
            {
                var names = new List<string>();
                foreach (var p in list) names.Add(p.GetIl2CppType().Name.Replace("FeedbackFeature", ""));
                Plugin.Logger.LogInfo($"[Session] The other Rose's '{f.name}': not playing its screen parts here ({string.Join(", ", names)}).");
            }
            return list;
        }

        static bool IsScreenPart(FeedbackFeature f) => f != null && ScreenParts.Contains(f.GetIl2CppType().Name);

        // Start an effect of the other player on their stand-in, then take its screen-wide parts out of that one
        // running copy (the effect asset itself is shared with our own Rose and is never changed).
        public static Il2CppSystem.Guid StartBodyOnly(FeedbackContainer container, Feedback f)
        {
            var guid = container.StartFeedback(f);
            if (ScreenPartsOf(f).Count == 0) return guid;
            try
            {
                var running = container.activeFeedbackInstances;
                if (running == null) return guid;
                string id = guid.ToString();
                FeedbackInstance inst = null;
                for (int k = running.Count - 1; k >= 0; k--)
                    if (running[k] != null && running[k].guid.ToString() == id) { inst = running[k]; break; }
                if (inst == null) return guid;
                var unprepared = inst.unpreparedFeatures;
                if (unprepared != null)
                    for (int i = unprepared.Count - 1; i >= 0; i--)
                        if (IsScreenPart(unprepared[i])) unprepared.RemoveAt(i);
                var unstarted = inst.unstartedFeatures;
                if (unstarted != null)
                    for (int i = unstarted.Count - 1; i >= 0; i--)
                        if (unstarted[i] != null && IsScreenPart(unstarted[i].feature)) unstarted.RemoveAt(i);
                var active = inst.activeFeatures;
                if (active != null)
                {
                    MotherBase.EntityFramework.Entity owner = null;
                    try { owner = container.GetComponentInParent<MotherBase.EntityFramework.Entity>(); } catch { }
                    for (int i = active.Count - 1; i >= 0; i--)
                    {
                        var part = active[i];
                        if (part == null || !IsScreenPart(part.feature)) continue;
                        try { part.OnStop(owner); } catch { }
                        active.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                if (loggedDrops.Add("fail:" + f.name)) Plugin.Logger.LogWarning($"[Session] Could not leave out the screen parts of '{f.name}': {e.Message}");
            }
            return guid;
        }

        internal static int TargetOf(FeedbackContainer container)
        {
            if (!Enabled || LocalEntity == IntPtr.Zero) return -1;
            var e = container._entity;
            if (e == null) return -1;
            if (e.Pointer == LocalEntity) return 0;
            return ProjectileTracker.IdOf(e.Pointer);
        }
    }

    internal static class EffectPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FeedbackContainer), nameof(FeedbackContainer.StartFeedback))]
        static void Started(FeedbackContainer __instance, Feedback __0, Il2CppSystem.Guid __result)
        {
            try
            {
                if (__0 == null) return;
                int target = EffectRelay.TargetOf(__instance);
                if (target < 0) return;
                EffectRelay.Outgoing.Add(new EffectRelay.Event { Start = true, Target = (ushort)target, Name = __0.name, Id = __result.ToString() });
                if (target == 0 && __0.name.Contains("Attack")) EffectRelay.LastLocalAttack = __0.name;
                if (target == 0 && __0.name.Contains("UseSavePoint")) RestSync.OnLocalRest(); // resting at a mirror
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Effect hook: {e.Message}"); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FeedbackContainer), nameof(FeedbackContainer.StopFeedback))]
        static void Stopped(FeedbackContainer __instance, Il2CppSystem.Guid __0) => Stop(__instance, __0, false);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FeedbackContainer), nameof(FeedbackContainer.ForceStopFeedback))]
        static void ForceStopped(FeedbackContainer __instance, Il2CppSystem.Guid __0) => Stop(__instance, __0, true);

        static void Stop(FeedbackContainer container, Il2CppSystem.Guid id, bool force)
        {
            try
            {
                int target = EffectRelay.TargetOf(container);
                if (target < 0) return;
                EffectRelay.Outgoing.Add(new EffectRelay.Event { Start = false, Force = force, Target = (ushort)target, Id = id.ToString() });
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Effect hook: {e.Message}"); }
        }
    }
}
