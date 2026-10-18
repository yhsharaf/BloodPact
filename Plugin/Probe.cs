using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using MotherBase.EntityFramework;
using UnityEngine;

namespace RoseNet
{
    // Diagnostic mode for milestone 2: writes every room entered and every mob spawned/killed, in order,
    // so the logs of two PCs walking the same rooms can be diffed. The question it answers: do spawners
    // produce the same mobs in the same order on both machines (so a spawner+ordinal can be a network ID)?
    internal static class Probe
    {
        static StreamWriter writer;
        static string room = "(?)";
        static int roomFrame;
        static readonly Dictionary<IntPtr, string> spawnerKeys = new Dictionary<IntPtr, string>();
        static readonly Dictionary<IntPtr, int> spawnCounts = new Dictionary<IntPtr, int>();
        static readonly Dictionary<IntPtr, string> mobIds = new Dictionary<IntPtr, string>();

        public static string FilePath { get; private set; }

        public static void Start(Harmony harmony)
        {
            var dir = Path.Combine(Paths.BepInExRootPath, "RoseNetProbe");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, $"probe_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            writer = new StreamWriter(FilePath, false, new UTF8Encoding(false)) { AutoFlush = true };

            Line($"# RoseNet probe {Plugin.Version} | game {Safe(() => Application.version)} | {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var saveDir = Safe(() => Application.persistentDataPath);
            Line($"# save folder {saveDir}");
            try
            {
                foreach (var f in new DirectoryInfo(saveDir).GetFiles())
                    Line($"# save file {f.Name} {f.Length} bytes {f.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception e) { Line($"# save folder unreadable: {e.Message}"); }

            harmony.PatchAll(typeof(ProbePatches));
            Plugin.Logger.LogInfo($"Probe mode on, writing {FilePath}");
        }

        internal static void EnterRoom(Vector2Int chunk)
        {
            room = $"({chunk.x},{chunk.y})";
            roomFrame = Time.frameCount;
            spawnCounts.Clear();
            Line($"ROOM {room}");
        }

        internal static void Spawned(BaseMobsSpawner spawner, Entity mob, Vector3 requested)
        {
            var key = SpawnerKey(spawner);
            spawnCounts.TryGetValue(spawner.Pointer, out var n);
            spawnCounts[spawner.Pointer] = ++n;
            var id = $"{key}#{n}";
            if (mob != null) mobIds[mob.Pointer] = id;
            var name = mob != null ? mob.name : "null";
            var pos = mob != null ? mob.transform.position : requested;
            Line($"SPAWN room={room} spawner={key} n={n} mob={name} pos={V(pos)} frame=+{Time.frameCount - roomFrame}");
        }

        internal static void Died(Entity mob)
        {
            if (mob == null) return;
            mobIds.TryGetValue(mob.Pointer, out var id);
            Line($"DEATH room={room} mob={mob.name} id={id ?? "?"} pos={V(mob.transform.position)} frame=+{Time.frameCount - roomFrame}");
        }

        // Spawners have no ID of their own; their scene path plus position is stable if both PCs load the same room.
        static string SpawnerKey(BaseMobsSpawner spawner)
        {
            if (spawnerKeys.TryGetValue(spawner.Pointer, out var key)) return key;
            var sb = new StringBuilder();
            for (var t = spawner.transform; t != null; t = t.parent)
                sb.Insert(0, sb.Length == 0 ? t.name : t.name + "/");
            key = $"{sb}@{V(spawner.transform.position)}";
            spawnerKeys[spawner.Pointer] = key;
            return key;
        }

        static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.0},{1:0.0},{2:0.0})", v.x, v.y, v.z);

        static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return "unknown: " + e.Message; }
        }

        static void Line(string s)
        {
            try { writer?.WriteLine(s); } catch { }
        }
    }

    // Postfixes only observe; every body is guarded so a probe failure can never break the game.
    internal static class ProbePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomManager.RoomManagerSystem), "OnEnterRoom")]
        static void EnterRoom(RoomManager.RoomManagerSystem __instance)
        {
            try { Probe.EnterRoom(__instance._currentChunk); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Probe room hook: {e.Message}"); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseMobsSpawner), "SpawnMobInternal")]
        static void Spawned(BaseMobsSpawner __instance, Entity __result, Vector3 __1)
        {
            try { Probe.Spawned(__instance, __result, __1); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Probe spawn hook: {e.Message}"); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MobsSpawner), "OnSpawnedMobDeath")]
        static void Died(Entity __0)
        {
            try { Probe.Died(__0); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Probe death hook: {e.Message}"); }
        }
    }
}
