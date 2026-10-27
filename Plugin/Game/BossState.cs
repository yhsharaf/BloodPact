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
        sealed class Kind { public Type Type; public PropertyInfo[] Fields; }

        // Kinds by the component's game type name; null = not a state we copy. Looked up one type at a time, only
        // for the components a boss actually has. (Scanning every type of the game's assembly up front crashed
        // the .NET runtime itself on some PCs: "Internal CLR error" the moment a boss spawned, GitHub issue #1.)
        static readonly Dictionary<string, Kind> kinds = new Dictionary<string, Kind>();
        static int logs = 10;

        static Kind KindOf(Component c)
        {
            string name;
            try { name = c.GetIl2CppType().Name; } catch { return null; }
            if (kinds.TryGetValue(name, out var known)) return known;
            Kind kind = null;
            try
            {
                if ((name.EndsWith("State") || name.EndsWith("StateComponent")) && name != "AIStateComponent"
                    && !name.StartsWith("Character") && !name.StartsWith("Player"))
                {
                    string full = c.GetIl2CppType().FullName;
                    var asm = typeof(LeonState).Assembly;
                    var t = asm.GetType(full, false) ?? asm.GetType("Il2Cpp" + full, false) ?? asm.GetType(name, false);
                    if (t != null && !t.IsAbstract)
                    {
                        var fields = new List<PropertyInfo>();
                        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                        {
                            var pt = p.PropertyType;
                            if (!p.CanRead || !p.CanWrite) continue;
                            if (pt == typeof(int) || pt == typeof(bool) || pt == typeof(float) || pt.IsEnum) fields.Add(p);
                        }
                        if (fields.Count > 0) kind = new Kind { Type = t, Fields = fields.ToArray() };
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Boss state '{name}': {e.Message}"); }
            kinds[name] = kind;
            return kind;
        }

        // A boss's state components don't change during a fight: find them once per boss.
        static readonly Dictionary<int, List<(Kind kind, object state)>> statesOf = new Dictionary<int, List<(Kind, object)>>();   // by instance id (never reused, unlike a pointer)

        static List<(Kind kind, object state)> StatesOf(Entity boss)
        {
            int key = boss.GetInstanceID();
            if (statesOf.TryGetValue(key, out var cached)) return cached;
            var list = new List<(Kind, object)>();
            foreach (var ks in FindStates(boss)) list.Add(ks);
            statesOf[key] = list;
            return list;
        }

        static IEnumerable<(Kind kind, object state)> FindStates(Entity boss)
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<MonoBehaviour> parts = null;
            try { parts = boss.gameObject.GetComponentsInChildren<MonoBehaviour>(true); } catch { }
            if (parts == null) yield break;
            foreach (var c in parts)
            {
                if (c == null) continue;
                var k = KindOf(c);
                if (k == null) continue;
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

        public static void Forget() { lastSent.Clear(); statesOf.Clear(); }
    }
}
