using System;
using System.Collections.Generic;
using System.Reflection;
using MotherBase.EntityFramework;
using UnityEngine;

namespace RoseNet.Game
{
    // A boss's own fight state (its phase, which side of the arena, counters...) lives in a "...State" component
    // on the boss (LeonState, JeanneState, MephistoState, ...). The game running the fight sends the simple values
    // of that component; the other game copies them onto its copy of the boss, so whoever takes the boss over
    // carries on in the right phase (a boss left in an old phase finds no move allowed and stands still).
    internal static class BossState
    {
        sealed class Kind { public Type Type; public Il2CppSystem.Type Il2Cpp; public PropertyInfo[] Fields; }

        static List<Kind> kinds;
        static int logs = 10;

        // Every MonoBehaviour type of the game named ...State / ...StateComponent with simple fields (bosses' states).
        static List<Kind> Kinds()
        {
            if (kinds != null) return kinds;
            kinds = new List<Kind>();
            try
            {
                foreach (var t in typeof(LeonState).Assembly.GetTypes())
                {
                    if (!t.IsSubclassOf(typeof(MonoBehaviour)) || t.IsAbstract) continue;
                    if (!(t.Name.EndsWith("State") || t.Name.EndsWith("StateComponent"))) continue;
                    if (t.Name == "AIStateComponent" || t.Name.StartsWith("Character") || t.Name.StartsWith("Player")) continue;
                    var fields = new List<PropertyInfo>();
                    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        var pt = p.PropertyType;
                        if (!p.CanRead || !p.CanWrite) continue;
                        if (pt == typeof(int) || pt == typeof(bool) || pt == typeof(float) || pt.IsEnum) fields.Add(p);
                    }
                    if (fields.Count == 0) continue;
                    Il2CppSystem.Type il2 = null;
                    try { il2 = Il2CppInterop.Runtime.Il2CppType.From(t); } catch { }
                    if (il2 != null) kinds.Add(new Kind { Type = t, Il2Cpp = il2, Fields = fields.ToArray() });
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Boss states: {e.Message}"); }
            return kinds;
        }

        static IEnumerable<(Kind kind, object state)> StatesOf(Entity boss)
        {
            foreach (var k in Kinds())
            {
                Component c = null;
                try { c = boss.gameObject.GetComponentInChildren(k.Il2Cpp, true); } catch { }
                if (c == null) continue;
                object state = null;
                try { state = Activator.CreateInstance(k.Type, c.Pointer); } catch { }
                if (state != null) yield return (k, state);
            }
        }

        // Owner: the boss's state as "Type.field=value;..." (empty when it has none).
        public static string Capture(Entity boss)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                foreach (var (k, st) in StatesOf(boss))
                    foreach (var f in k.Fields)
                    {
                        object v;
                        try { v = f.GetValue(st); } catch { continue; }
                        if (v == null) continue;
                        string text = v is float fl ? fl.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                            : v is Enum ? Convert.ToInt64(v).ToString() : v.ToString();
                        sb.Append(k.Type.Name).Append('.').Append(f.Name).Append('=').Append(text).Append(';');
                    }
            }
            catch { }
            return sb.ToString();
        }

        // Other game: set those values on our copy of the boss.
        public static void Apply(Entity boss, string snapshot)
        {
            if (boss == null || string.IsNullOrEmpty(snapshot)) return;
            int set = 0;
            try
            {
                var values = new Dictionary<string, string>();
                foreach (var part in snapshot.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) values[part.Substring(0, eq)] = part.Substring(eq + 1);
                }
                foreach (var (k, st) in StatesOf(boss))
                    foreach (var f in k.Fields)
                    {
                        if (!values.TryGetValue(k.Type.Name + "." + f.Name, out var text)) continue;
                        try
                        {
                            object v;
                            var pt = f.PropertyType;
                            if (pt == typeof(int)) v = int.Parse(text);
                            else if (pt == typeof(bool)) v = bool.Parse(text);
                            else if (pt == typeof(float)) v = float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                            else v = Enum.ToObject(pt, long.Parse(text));
                            if (!Equals(f.GetValue(st), v)) { f.SetValue(st, v); set++; }
                        }
                        catch { }
                    }
            }
            catch { }
            if (set > 0 && logs-- > 0) Plugin.Logger.LogInfo($"[Session] Boss '{MobRegistry.PrefabName(boss)}': copied {set} state value(s) from the game running the fight.");
        }

        // ---- sending (owner) ----

        static readonly Dictionary<uint, string> lastSent = new Dictionary<uint, string>();
        static float nextCheck;

        public static void Send(string roomId, Action<uint, string> send)
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.25f;
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                try
                {
                    if (mob == null || !MobRegistry.InRoom(kv.Key, roomId) || mob.GetComponent<BossComponent>() == null) continue;
                    string snap = Capture(mob);
                    if (snap.Length == 0) continue;
                    if (lastSent.TryGetValue(kv.Key, out var was) && was == snap) continue;
                    lastSent[kv.Key] = snap;
                    send(kv.Key, snap);
                }
                catch { }
            }
        }

        public static void Forget() => lastSent.Clear();
    }
}
