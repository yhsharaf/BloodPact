using System;
using System.Collections.Generic;
using MotherBase.EntityFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // Shared world switches: when either player opens a door, pulls a lever, turns a crank..., it changes in
    // the other world too. Every such object saves its state through SavableEntityBase (save key -> state
    // text, the same keys on both PCs). Both sides poll the loaded ones for changes and send them; the
    // receiver loads the state into the live object (as if the game had just loaded it) and writes it into
    // the world save, so rooms that aren't loaded right now get it when they load.
    internal static class WorldSync
    {
        internal struct Change { public string Key, Kind, State; }

        // Kinds of saved objects that are shared. Monster spawners have their own sync, boss triggers and
        // encounter gates follow fights, dialogue isn't world state.
        static readonly HashSet<string> SharedKinds = new HashSet<string>
        {
            "DoorSavableStateTranslator", "ZDoorSavableStateTranslator", "ActivableSavableStateTranslator",
            "AngleRotationSavableStateTranslator", "GradualStateSavableStateTranslator",
            "StateMachineSavableStateTranslator", "WaterMillSavableStateTranslator",
            "MammonBlockerSavableStateTranslator", "ResetableSecretZoneSavableStateTranslator",
        };

        internal static readonly List<Change> Outgoing = new List<Change>();

        sealed class Tracked { public SavableEntityBase Savable; public Entity Entity; public string Kind; }
        static readonly Dictionary<string, Tracked> byKey = new Dictionary<string, Tracked>();
        static readonly Dictionary<string, string> lastSeen = new Dictionary<string, string>();
        static float nextScan, nextPoll;
        static int logs = 30;

        // The other player's changes to objects whose room isn't loaded here: applied when they load.
        static readonly Dictionary<string, Change> pending = new Dictionary<string, Change>();

        static void Scan()
        {
            // Same key on a different object = its room was reloaded: that counts as just loaded too.
            var before = new Dictionary<string, IntPtr>();
            foreach (var kv in byKey) if (kv.Value.Savable != null) before[kv.Key] = kv.Value.Savable.Pointer;
            byKey.Clear();
            foreach (var o in Object.FindObjectsOfType<SavableEntityBase>())
            {
                try
                {
                    var t = o.saveStateTranslator;
                    string kind = t != null ? t.GetIl2CppType().Name : null;
                    if (kind == null || !SharedKinds.Contains(kind)) continue;
                    var entity = o.GetComponentInParent<Entity>();
                    var ks = o.GetSaveKeyAndState(entity);
                    if (string.IsNullOrEmpty(ks.Item1)) continue;
                    var tr = new Tracked { Savable = o, Entity = entity, Kind = kind };
                    byKey[ks.Item1] = tr;
                    if (before.TryGetValue(ks.Item1, out var was) && was == o.Pointer) continue;
                    // Just loaded: what it shows now is how this world has it, not something a player just
                    // did. Bring in the other player's change from while it wasn't loaded, then take that as
                    // the baseline.
                    if (pending.TryGetValue(ks.Item1, out var c)) { pending.Remove(ks.Item1); ApplyLive(tr, c); }
                    lastSeen[ks.Item1] = o.GetSaveKeyAndState(entity).Item2 ?? "";
                }
                catch { }
            }
        }

        // Called every frame while connected: collect our changes.
        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (GameAccess.IsLoadingRoom) return;
            if (now >= nextScan) { nextScan = now + 2f; Scan(); }
            if (now < nextPoll) return;
            nextPoll = now + 0.2f;
            bool resting = RestSync.Resting;
            foreach (var kv in byKey)
            {
                try
                {
                    var tr = kv.Value;
                    if (tr.Savable == null) continue;
                    var ks = tr.Savable.GetSaveKeyAndState(tr.Entity);
                    string state = ks.Item2 ?? "";
                    if (lastSeen.TryGetValue(kv.Key, out var was) && was == state) continue;
                    lastSeen[kv.Key] = state;
                    if (resting) continue;   // a rest resets things in bulk; the rest sync covers that
                    Outgoing.Add(new Change { Key = kv.Key, Kind = tr.Kind, State = state });
                    if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] World object changed here: {Short(tr.Kind)} '{tr.Savable.name}' -> sending to the other player.");
                }
                catch { }
            }
        }

        // The other player's world changed.
        public static void Apply(Change c)
        {
            if (string.IsNullOrEmpty(c.Key) || !SharedKinds.Contains(c.Kind ?? "")) return;
            lastSeen[c.Key] = c.State;   // so we don't send it back
            try
            {
                var states = RestSync.States();
                if (states != null) states[c.Key] = c.State;
            }
            catch { }
            if (!byKey.TryGetValue(c.Key, out var tr) || tr.Savable == null)
            {
                pending[c.Key] = c;
                if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player changed a {Short(c.Kind)} in a room that isn't loaded here; it will be applied when that room loads.");
                return;
            }
            ApplyLive(tr, c);
        }

        static void ApplyLive(Tracked tr, Change c)
        {
            try
            {
                var now = tr.Savable.GetSaveKeyAndState(tr.Entity).Item2;
                if (now == c.State) return;
                // Loading a state only sets what the object starts as when its room loads; a live switch
                // (lever, gate, ...) has to be activated for real so it plays its open/close action.
                var act = tr.Savable.GetComponentInParent<ActivableEntity>();
                string live = "";
                if (act != null && ActivatedIn(c.State, out bool on))
                {
                    if (on && !act.IsActivated()) { act.Activate(false); live = " (activated live)"; }
                    else if (!on && act.IsActivated() && !act.cannotBeDeactivated) { act.Deactivate(false); live = " (deactivated live)"; }
                }
                tr.Savable.LoadSaveState(tr.Entity, c.State);
                if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player changed {Short(c.Kind)} '{tr.Savable.name}' to {c.State}; applied here{live}.");
            }
            catch (Exception e)
            {
                if (logs-- > 0) Plugin.Logger.LogWarning($"[Session] Could not apply the other player's {Short(c.Kind)} change: {e.Message}");
            }
            finally
            {
                // Whatever the object now reports is the new normal here; never echo it back.
                try { lastSeen[c.Key] = tr.Savable.GetSaveKeyAndState(tr.Entity).Item2 ?? ""; } catch { }
            }
        }

        public static void Forget()
        {
            byKey.Clear();
            nextScan = 0;
        }

        // A switch's saved state, e.g. {"activated":true} (or plain True/False).
        static bool ActivatedIn(string state, out bool on)
        {
            on = false;
            if (string.IsNullOrEmpty(state)) return false;
            if (bool.TryParse(state.Trim(), out on)) return true;
            var m = System.Text.RegularExpressions.Regex.Match(state, "\"activated\"\\s*:\\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            on = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        static string Short(string kind) => (kind ?? "object").Replace("SavableStateTranslator", "").Replace("StateTranslator", "");
    }
}
