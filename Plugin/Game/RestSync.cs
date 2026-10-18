using System;
using System.Collections.Generic;
using HarmonyLib;
using MotherBase.EntityFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // Shared resting, like Souls co-op: when either player rests at a mirror, the monsters come back in
    // both worlds (bosses don't; the game does not mark them resettable).
    //
    // What resting changes lives in the world save (SaveLogic.RuntimeWorldSave.savableEntityStates: object
    // id -> state text, the same ids on both PCs). The resting side snapshots it before the rest, diffs it
    // a few seconds later and sends the changed/cleared entries; the other side applies them and resets the
    // resettable objects of the rooms it has loaded right now.
    internal static class RestSync
    {
        internal struct Change { public string Key; public bool Removed; public string Value; }

        static Dictionary<string, string> snapshot;
        static readonly Dictionary<string, string> sent = new Dictionary<string, string>();
        static float startedAt = -1;
        static int checksDone;
        static readonly float[] CheckAfter = { 2f, 5f, 10f };
        internal static readonly List<Change> Outgoing = new List<Change>();

        internal static Il2CppSystem.Collections.Generic.Dictionary<string, string> States()
        {
            try { return SingletonScriptableObject<SaveLogic>.Get()?.RuntimeWorldSave?.savableEntityStates?.m_dict; }
            catch { return null; }
        }

        internal static bool FirstBatchPending;   // the first message of a rest also resets loaded rooms

        // A rest (ours) is being processed: world objects change in bulk, which the rest sync covers.
        internal static bool Resting => startedAt >= 0 && Time.unscaledTime - startedAt < 12f;

        // Our player started resting.
        public static void OnLocalRest()
        {
            if (snapshot != null && Time.unscaledTime - startedAt < 3f) return; // already watching this rest
            var states = States();
            if (states == null) return;
            snapshot = new Dictionary<string, string>();
            foreach (var kv in states) snapshot[kv.Key] = kv.Value;
            sent.Clear();
            startedAt = Time.unscaledTime;
            checksDone = 0;
            Plugin.Logger.LogInfo($"[Session] Resting: watching {snapshot.Count} world states to share the reset.");
            int shown = 0;
            foreach (var kv in snapshot)
            {
                if (shown++ >= 20) break;
                var v = kv.Value ?? "";
                Plugin.Logger.LogInfo($"[RestDiag]   state {kv.Key} = {(v.Length > 100 ? v.Substring(0, 100) + "..." : v)}");
            }
            RestDiag.Open();
        }

        // Called every frame by the session: after the rest, queue what changed for the other player.
        public static void Tick()
        {
            if (snapshot == null || checksDone >= CheckAfter.Length || Time.unscaledTime - startedAt < CheckAfter[checksDone]) return;
            checksDone++;
            if (checksDone == 1) FirstBatchPending = true;
            var states = States();
            if (states == null) return;

            var now = new Dictionary<string, string>();
            foreach (var kv in states) now[kv.Key] = kv.Value;
            int changed = 0;
            foreach (var kv in now)
            {
                if (snapshot.TryGetValue(kv.Key, out var before) && before == kv.Value) continue;
                if (sent.TryGetValue(kv.Key, out var already) && already == kv.Value) continue;
                Outgoing.Add(new Change { Key = kv.Key, Value = kv.Value });
                sent[kv.Key] = kv.Value;
                changed++;
            }
            foreach (var key in snapshot.Keys)
            {
                if (now.ContainsKey(key) || (sent.TryGetValue(key, out var v) && v == null)) continue;
                Outgoing.Add(new Change { Key = key, Removed = true });
                sent[key] = null;
                changed++;
            }
            if (changed > 0 || checksDone == 1)
                Plugin.Logger.LogInfo($"[Session] Rest check {checksDone}: {changed} world states changed; sharing them.");
            if (checksDone >= CheckAfter.Length) snapshot = null;
        }

        // How resting works in the game (read from its code): the rest action sends an event, and the game's
        // ResetSystem calls ResetableEntity.ResetEntity() on every resettable object it knows - including
        // those in rooms kept loaded but switched off, whose reset action then runs when you re-enter.
        // Resting also clears the saved states of resettable objects, so rooms that are fully unloaded load
        // fresh. The other player's rest is applied the same way: their cleared/changed states, then
        // ResetEntity on all our resettable objects (active or not). Bosses are not resettable.
        public static void Apply(List<Change> changes, bool restStarted)
        {
            var states = States();
            if (states != null)
            {
                foreach (var c in changes)
                {
                    if (c.Removed) states.Remove(c.Key);
                    else states[c.Key] = c.Value;
                }
            }
            if (!restStarted)
            {
                if (changes.Count > 0) Plugin.Logger.LogInfo($"[Session] The other player's rest: {changes.Count} more world states applied.");
                return;
            }
            // From the game's code: each time a room loads, InitializeSpawner spawns everything fresh when the
            // spawner's persistentData.HasSpawned is false, and otherwise only respawns what its saved list
            // says (dead stay dead). So the other player's rest = HasSpawned=false on every resettable spawner
            // (bosses are not resettable), except the room we stand in, which gets it when we leave.
            int reset = 0;
            pendingSpawners.Clear();
            pendingKeys.Clear();
            string here = GameAccess.CurrentRoomId();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ResetableEntity>()))
            {
                var r = o.TryCast<ResetableEntity>();
                if (r == null || !r.gameObject.scene.IsValid() || !r.resetOnSave) continue;
                var spawner = r.GetComponent<BaseMobsSpawner>();
                if (spawner == null) continue;
                if (InRoom(spawner, here))
                {
                    pendingSpawners.Add(spawner);
                    // The room may be unloaded by the time we leave; remember its save IDs now.
                    var savable = spawner.GetComponent<SavableEntityBase>();
                    if (savable != null && !string.IsNullOrEmpty(savable.Id)) pendingKeys.Add(savable.Id);
                }
                else if (MarkForFreshSpawn(spawner)) reset++;
            }
            Plugin.Logger.LogInfo($"[Session] The other player rested: {changes.Count} world states applied, {reset} spawners will respawn when their room loads" +
                (pendingSpawners.Count > 0 ? $", {pendingSpawners.Count} more when we leave this room." : "."));
        }

        static readonly List<BaseMobsSpawner> pendingSpawners = new List<BaseMobsSpawner>();

        // Called by the session when we leave a room.
        static readonly List<string> pendingKeys = new List<string>();

        public static void OnLeftRoom()
        {
            if (pendingSpawners.Count == 0 && pendingKeys.Count == 0) return;
            int n = 0;
            foreach (var s in pendingSpawners)
                if (s != null && MarkForFreshSpawn(s)) n++;
            // Leaving saves the room's state ("already spawned"), which would load back on re-entry:
            // drop the spawners' saved entries (now, and again after the game's own save) so it loads fresh.
            leftKeys.Clear();
            leftKeys.AddRange(pendingKeys);
            DropSavedStates();
            clearAgain = 2;
            clearAgainAt = Time.unscaledTime + 1f;
            Plugin.Logger.LogInfo($"[Session] Left the room: it respawns next time (the other player's rest; {n} spawners still loaded, {leftKeys.Count} saved states dropped).");
            pendingSpawners.Clear();
            pendingKeys.Clear();
        }

        static readonly List<string> leftKeys = new List<string>();
        static float clearAgainAt = -1;
        static int clearAgain;

        // Called every frame: repeat after the game's own save on leaving (at +1 s and +3 s).
        public static void LateClear()
        {
            if (clearAgainAt < 0 || Time.unscaledTime < clearAgainAt) return;
            DropSavedStates();
            if (--clearAgain > 0) clearAgainAt = Time.unscaledTime + 2f;
            else { clearAgainAt = -1; leftKeys.Clear(); }
        }

        static void DropSavedStates()
        {
            var states = States();
            if (states == null) return;
            foreach (var k in leftKeys) states.Remove(k);
        }

        static bool InRoom(BaseMobsSpawner s, string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return false;
            var root = s.transform;
            while (root.parent != null) root = root.parent;
            return roomId.EndsWith(root.name, StringComparison.Ordinal);
        }

        static bool MarkForFreshSpawn(BaseMobsSpawner s)
        {
            try
            {
                var pd = s.persistentData;
                if (!pd.HasSpawned) return false;
                pd.HasSpawned = false;
                s.persistentData = pd;
                return true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not mark '{s.name}' for respawn: {e.Message}"); return false; }
        }

        // No longer used (sending the game's rest event could crash), kept for reference.
        static void SendResetEvent(int changeCount)
        {
            // The game's own rest reset: the same event the rest action sends, which ResetSystem acts on.
            string how;
            try
            {
                var me = GameAccess.MainCharacter();
                if (me == null) throw new InvalidOperationException("no character loaded");
                var evt = new UseSavePointEvent();
                evt.userEntity = me;
                evt.resetEntities = true;
                me.context.events.Send<UseSavePointEvent>(evt);
                how = "game reset event sent";
            }
            catch (Exception e)
            {
                // Fallback: reset the resettable objects directly.
                int reset = 0;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ResetableEntity>()))
                {
                    var r = o.TryCast<ResetableEntity>();
                    if (r == null || !r.gameObject.scene.IsValid() || !r.resetOnSave) continue;
                    try { r.ResetEntity(); reset++; } catch { }
                }
                how = $"event failed ({e.Message}); reset {reset} objects directly";
            }
            Plugin.Logger.LogInfo($"[Session] Respawned monsters for the other player's rest ({changeCount} world states, {how}).");
            RestDiag.Open();
        }
    }

    // Diagnostics: what the game's spawners do after a rest (ours or the other player's), to learn how the
    // game brings monsters back. Logs for 90 seconds, at most 120 lines per rest.
    internal static class RestDiag
    {
        static float until;
        static int lines;

        public static void Open() { until = Time.unscaledTime + 90f; lines = 120; }

        public static void Log(string what)
        {
            if (Time.unscaledTime > until || lines-- <= 0) return;
            Plugin.Logger.LogInfo($"[RestDiag] +{90f - (until - Time.unscaledTime):0.0}s {what}");
        }

        static string Name(Component c) { try { return c != null ? c.gameObject.name : "?"; } catch { return "?"; } }

        [HarmonyPostfix, HarmonyPatch(typeof(BaseMobsSpawner), nameof(BaseMobsSpawner.InitializeSpawner))]
        static void Init(BaseMobsSpawner __instance, bool __2) => Log($"InitializeSpawner {Name(__instance)} reset={__2} hasSpawned={Safe(() => __instance.persistentData.HasSpawned)}");

        [HarmonyPostfix, HarmonyPatch(typeof(MobsSpawner), nameof(MobsSpawner.SpawnInit))]
        static void SpawnInit(MobsSpawner __instance) => Log($"SpawnInit {Name(__instance)} spawned={Safe(() => __instance.spawnedMobsCount)} dead={Safe(() => __instance.deadMobsCount)}");

        [HarmonyPostfix, HarmonyPatch(typeof(MobsSpawner), nameof(MobsSpawner.ResetSpawner))]
        static void Reset(MobsSpawner __instance) => Log($"ResetSpawner {Name(__instance)}");

        [HarmonyPostfix, HarmonyPatch(typeof(MobsSpawner), nameof(MobsSpawner.DoSoftResetRespawn))]
        static void SoftReset(MobsSpawner __instance) => Log($"DoSoftResetRespawn {Name(__instance)}");

        [HarmonyPostfix, HarmonyPatch(typeof(ResetableEntity), nameof(ResetableEntity.ResetEntity))]
        static void ResetEntity(ResetableEntity __instance) => Log($"ResetableEntity.ResetEntity {Name(__instance)} resetOnSave={Safe(() => __instance.resetOnSave)}");

        [HarmonyPostfix, HarmonyPatch(typeof(MobSpawnerSavableStateTranslator), nameof(MobSpawnerSavableStateTranslator.LoadSaveStateString))]
        static void Load(SavableEntityBase __1, string __2) => Log($"spawner state loaded {Name(__1)}: {Short(__2)}");

        [HarmonyPostfix, HarmonyPatch(typeof(MobSpawnerSavableStateTranslator), nameof(MobSpawnerSavableStateTranslator.GetSaveStateString))]
        static void Save(SavableEntityBase __1, string __result) => Log($"spawner state saved {Name(__1)}: {Short(__result)}");

        static string Short(string s) => s == null ? "null" : s.Length > 90 ? s.Substring(0, 90) + "..." : s;
        static string Safe<T>(Func<T> f) { try { return f()?.ToString() ?? "null"; } catch { return "?"; } }
    }

    internal static class RestPatches
    {

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SavePoint), nameof(SavePoint.UseSavePoint), new[] { typeof(Entity) })]
        static void UsedSavePoint(Entity __0)
        {
            try
            {
                var me = GameAccess.MainCharacter();
                if (me != null && __0 != null && __0.Pointer == me.Pointer) RestSync.OnLocalRest();
            }
            catch { }
        }
    }
}
