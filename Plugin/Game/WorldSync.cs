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

        // Kinds of saved objects that are shared. Monster spawners have their own sync, boss triggers follow
        // fights, dialogue isn't world state. Locked-room gates: running fights go through EncounterSync; here
        // only "this fight is finished", so it doesn't start again for the other player.
        static readonly HashSet<string> SharedKinds = new HashSet<string>
        {
            "DoorSavableStateTranslator", "ZDoorSavableStateTranslator", "ActivableSavableStateTranslator",
            "AngleRotationSavableStateTranslator", "GradualStateSavableStateTranslator",
            "StateMachineSavableStateTranslator", "WaterMillSavableStateTranslator",
            "MammonBlockerSavableStateTranslator", "ResetableSecretZoneSavableStateTranslator",
            "EncounterGateStateTranslator",
        };

        internal static readonly List<Change> Outgoing = new List<Change>();

        sealed class Tracked { public SavableEntityBase Savable; public Entity Entity; public string Kind; }
        static readonly Dictionary<string, Tracked> byKey = new Dictionary<string, Tracked>();
        static readonly Dictionary<string, string> lastSeen = new Dictionary<string, string>();
        static float nextScan, nextPoll;
        static int logs = 30;

        // The other player's changes to objects whose room isn't loaded here: applied when they load.
        static readonly Dictionary<string, Change> pending = new Dictionary<string, Change>();
        static readonly HashSet<string> pendingGifts = new HashSet<string>();
        // Chests whose items we already have (by save key): each chest gives once.
        static readonly HashSet<string> givenKeys = new HashSet<string>();
        // Items in chests we opened, for the other player wherever they are: (chest key, item ids).
        internal static readonly List<(string key, string[] items)> OutgoingLoot = new List<(string, string[])>();

        static string[] ItemsIn(ItemGiver giver)
        {
            var ids = new List<string>();
            try
            {
                if (giver.giveMultipleItems && giver.multipleItems != null)
                    foreach (var it in giver.multipleItems) { if (it != null && !string.IsNullOrEmpty(it.itemID)) ids.Add(it.itemID); }
                else if (giver.itemData != null && !string.IsNullOrEmpty(giver.itemData.itemID)) ids.Add(giver.itemData.itemID);
            }
            catch { }
            return ids.ToArray();
        }

        static Dictionary<string, ItemData> itemsById;
        static ItemData FindItem(string id)
        {
            if (itemsById == null || !itemsById.ContainsKey(id))
            {
                itemsById = new Dictionary<string, ItemData>();
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ItemData>()))
                {
                    var d = o.TryCast<ItemData>();
                    if (d != null && !string.IsNullOrEmpty(d.itemID)) itemsById[d.itemID] = d;
                }
            }
            return itemsById.TryGetValue(id, out var item) ? item : null;
        }

        // The other player opened a chest, wherever we are: put its items straight into our inventory.
        public static void OnLoot(string key, string[] ids)
        {
            if (string.IsNullOrEmpty(key) || ids == null || ids.Length == 0 || givenKeys.Contains(key)) return;
            try
            {
                var inv = GameAccess.MainCharacter()?.GetComponent<InventoryComponent>()?.inventory;
                if (inv == null) { Plugin.Logger.LogWarning("[Session] Chest items from the other player: no inventory yet; they come when the chest's room loads."); return; }
                var items = new List<ItemData>();
                foreach (var id in ids)
                {
                    var item = FindItem(id);
                    if (item == null) { Plugin.Logger.LogWarning($"[Session] Chest item '{id}' isn't loaded here; it comes when the chest's room loads."); return; }
                    items.Add(item);
                }
                foreach (var item in items) inv.AddItemToInventory(item, true);
                givenKeys.Add(key);
                pendingGifts.Remove(key);
                Plugin.Logger.LogInfo($"[Session] The other player opened a chest: got its items ({string.Join(", ", ids)}).");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not add the chest's items: {e.Message}"); }
        }
        static int detailLogs = 200;

        static ItemGiver ChestOf(Tracked tr)
        {
            try
            {
                var root = tr.Entity != null ? tr.Entity.gameObject : tr.Savable.gameObject;
                var holder = root.GetComponentInChildren<ChestItemHolder>(true);
                if (holder != null && holder.itemGiver != null) return holder.itemGiver;
                return null;
            }
            catch { return null; }
        }

        static string GiveChestItem(ItemGiver giver, string key)
        {
            if (!givenKeys.Add(key)) return " (we already have its items)";
            try
            {
                if (giver.HasNoItemToGive()) return " (no item in it)";
                giver.GiveItem();
                return " and gave us its item";
            }
            catch (Exception e) { return $" (could not give its item: {e.Message})"; }
        }

        static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null && sb.Length < 160; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static string Parts(Tracked tr)
        {
            try
            {
                var root = tr.Entity != null ? tr.Entity.gameObject : tr.Savable.gameObject;
                var names = new HashSet<string>();
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    var n = c.GetIl2CppType().Name;
                    if (n == "Transform" || n.EndsWith("Renderer") || n == "MeshFilter") continue;
                    names.Add(n);
                    if (names.Count >= 25) break;
                }
                return string.Join(", ", names);
            }
            catch { return "?"; }
        }

        // Personal rewards: an object (not a chest) that drops experience or a pickable item when broken or taken
        // (EXP statues, health/mana upgrades). Sharing its "broken/taken" state took it away from the other player
        // without giving them anything, so each player's copy stays their own: both can break or take it.
        static readonly Dictionary<IntPtr, string> personalCache = new Dictionary<IntPtr, string>();

        internal static string PersonalReason(SavableEntityBase o, Entity entity)
        {
            if (o == null) return null;
            if (personalCache.TryGetValue(o.Pointer, out var cached)) return cached;
            string why = null;
            try
            {
                var root = entity != null ? entity.gameObject : o.gameObject;
                // Pickups that are the reward themselves: health/mana upgrades, stat books, food.
                string n = o.name ?? "", rn = root.name ?? "";
                if (n.StartsWith("GPE_UpgradeCollectible", StringComparison.Ordinal) || rn.StartsWith("GPE_UpgradeCollectible", StringComparison.Ordinal)) why = "upgrade";
                else if (n.StartsWith("GPE_Consumable", StringComparison.Ordinal) || rn.StartsWith("GPE_Consumable", StringComparison.Ordinal)) why = "pickup";
                if (why == null && root.GetComponentInChildren<ChestItemHolder>(true) == null)
                {
                    if (root.GetComponentInChildren<ExperienceHolder>(true) != null) why = "experience";
                    foreach (var dropper in root.GetComponentsInChildren<LootDropper>(true))
                    {
                        if (why != null) break;
                        var table = dropper != null ? dropper.lootTable : null;
                        if (table == null) continue;
                        if (table.guaranteedDrops != null)
                            foreach (var d in table.guaranteedDrops) { why ??= LootKind(d); }
                        if (why == null && table.randomLootEntries != null)
                            foreach (var e in table.randomLootEntries) { why ??= LootKind(e.lootData); }
                    }
                }
            }
            catch { }
            personalCache[o.Pointer] = why;
            return why;
        }

        static string LootKind(LootData d)
        {
            if (d == null) return null;
            if (d.TryCast<ExperienceLootData>() != null) return "experience";
            if (d.TryCast<PickableItemLootData>() != null) return "item";
            return null;
        }

        static readonly HashSet<string> personalLogged = new HashSet<string>();

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
                    var personal = PersonalReason(o, entity);
                    if (personal != null)
                    {
                        if (personalLogged.Add(ks.Item1) && personalLogged.Count <= 40)
                            Plugin.Logger.LogInfo($"[WorldDiag] Not shared (each player gets their own {personal}): '{PathOf(o.transform)}'.");
                        continue;
                    }
                    var tr = new Tracked { Savable = o, Entity = entity, Kind = kind };
                    byKey[ks.Item1] = tr;
                    if (before.TryGetValue(ks.Item1, out var was) && was == o.Pointer) continue;
                    // Just loaded: what it shows now is how this world has it, not something a player just
                    // did. Bring in the other player's change from while it wasn't loaded, then take that as
                    // the baseline.
                    if (pendingGifts.Remove(ks.Item1))
                    {
                        var chest = ChestOf(tr);
                        if (chest != null) Plugin.Logger.LogInfo($"[Session] Chest '{o.name}' (opened by the other player) is here now{GiveChestItem(chest, ks.Item1)}.");
                    }
                    if (pending.TryGetValue(ks.Item1, out var c)) { pending.Remove(ks.Item1); ApplyLive(tr, c, justLoaded: true); }
                    else if (kind == "EncounterGateStateTranslator" && finishedGates.Contains(ks.Item1) && !GateFinished(ks.Item2))
                        ApplyLive(tr, new Change { Key = ks.Item1, Kind = kind, State = "{\"isComplete\":true}" }, justLoaded: true);
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
                    // A finished fight: its saved state only follows when the game's end action is over (a talk can
                    // hold that up), and the other player mustn't start it again meanwhile.
                    if (tr.Kind == "EncounterGateStateTranslator" && !GateFinished(state))
                    {
                        var gate = tr.Savable.GetComponentInParent<GPE_EncounterGateComponent>() ?? tr.Savable.GetComponentInChildren<GPE_EncounterGateComponent>(true);
                        if (gate != null && gate.IsDone) state = "{\"isComplete\":true}";
                    }
                    if (lastSeen.TryGetValue(kv.Key, out var was) && was == state) continue;
                    lastSeen[kv.Key] = state;
                    if (resting) continue;   // a rest resets things in bulk; the rest sync covers that
                    // A chest we just opened: its items go to the other player too, wherever they are (sent first,
                    // so their copy of the chest, if loaded, doesn't hand them out a second time).
                    if (ActivatedIn(state, out bool opened) && opened)
                    {
                        var giver = ChestOf(tr);
                        if (giver != null) { var ids = ItemsIn(giver); if (ids.Length > 0) { givenKeys.Add(kv.Key); OutgoingLoot.Add((kv.Key, ids)); } }
                    }
                    if (tr.Kind == "EncounterGateStateTranslator" && !GateFinished(state)) continue;
                    Outgoing.Add(new Change { Key = kv.Key, Kind = tr.Kind, State = state });
                    if (!tr.Savable.name.StartsWith("GPE_DestructibleProp", StringComparison.Ordinal) && detailLogs-- > 0)
                        Plugin.Logger.LogInfo($"[WorldDiag] Changed here: {Short(tr.Kind)} '{PathOf(tr.Savable.transform)}' = {state}; parts: {Parts(tr)}");
                    else if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] World object changed here: {Short(tr.Kind)} '{tr.Savable.name}' -> sending to the other player.");
                }
                catch { }
            }
        }

        // The other player's world changed.
        public static void Apply(Change c)
        {
            if (string.IsNullOrEmpty(c.Key) || !SharedKinds.Contains(c.Kind ?? "")) return;
            if (c.Kind == "EncounterGateStateTranslator") finishedGates.Add(c.Key);
            lastSeen[c.Key] = c.State;   // so we don't send it back
            try
            {
                var states = RestSync.States();
                if (states != null) states[c.Key] = c.State;
            }
            catch { }
            // Doors into the background (a house, the Seer's Hovel...): loading one live plays its walk-through
            // (a black transition) for whoever is near it here. Their state goes into the save only, so the door
            // is as the other player left it next time its room loads.
            if (c.Kind == "ZDoorSavableStateTranslator")
            {
                if (detailLogs-- > 0) Plugin.Logger.LogInfo($"[WorldDiag] The other player used a background door ({c.State}); saved here, not played live.");
                return;
            }
            if (!byKey.TryGetValue(c.Key, out var tr) || tr.Savable == null)
            {
                pending[c.Key] = c;
                if (ActivatedIn(c.State, out bool nowOn) && nowOn) pendingGifts.Add(c.Key);   // a chest gives its item when it loads
                if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player changed a {Short(c.Kind)} in a room that isn't loaded here; it will be applied when that room loads.");
                return;
            }
            ApplyLive(tr, c);
        }

        // Fights the other player finished this session. A respawn reloads the level from before that news, so
        // the gate is marked finished again whenever its room loads; otherwise the fight would start again here.
        static readonly HashSet<string> finishedGates = new HashSet<string>();

        static bool GateFinished(string state) => (state ?? "").IndexOf("\"isComplete\":true", StringComparison.OrdinalIgnoreCase) >= 0;

        static bool AngleIndexIn(string state, out int index)
        {
            index = 0;
            var m = System.Text.RegularExpressions.Regex.Match(state ?? "", @"""angleIndex""\s*:\s*(-?\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out index);
        }

        static void ApplyLive(Tracked tr, Change c, bool justLoaded = false)
        {
            try
            {
                var now = tr.Savable.GetSaveKeyAndState(tr.Entity).Item2;
                if (now == c.State) return;
                // Loading a state only sets what the object starts as when its room loads; a live switch
                // (lever, gate, ...) has to be activated for real so it plays its open/close action.
                var act = tr.Savable.GetComponentInParent<ActivableEntity>();
                string live = "";
                var chest = ChestOf(tr);
                if (chest != null)
                {
                    // A chest the other player opened: opening it here would walk our Rose through its open
                    // and pick-up sequence. Give its item straight away and show it as opened.
                    if (ActivatedIn(c.State, out bool opened) && opened && act != null && !act.IsActivated())
                    {
                        live = GiveChestItem(chest, c.Key);
                    }
                    tr.Savable.LoadSaveState(tr.Entity, c.State);
                    if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player opened chest '{tr.Savable.name}'; marked opened here{live}.");
                    return;
                }
                if (c.Kind == "EncounterGateStateTranslator")
                {
                    tr.Savable.LoadSaveState(tr.Entity, c.State);
                    EncounterSync.OnFinished(tr.Savable, tr.Entity);
                    return;
                }
                // A drawbridge (or other turning platform): its saved angle only says how it starts next time its
                // room loads. Turn it for real, the game's own way, so it lowers/raises here as it did for them.
                var rot = tr.Savable.GetComponentInParent<GPE_AngleRotation>() ?? tr.Savable.GetComponentInChildren<GPE_AngleRotation>(true);
                if (rot != null && AngleIndexIn(c.State, out int angle))
                {
                    if (rot._currentAngleIndex != angle && rot._targetAngleIndex != angle)
                    {
                        // Just loaded (they turned it while we were elsewhere): it is simply in place.
                        if (justLoaded) rot.InstantSetAngleIndex(angle); else rot.GoToAngle(angle);
                        if (detailLogs-- > 0) Plugin.Logger.LogInfo($"[WorldDiag] The other player turned '{PathOf(tr.Savable.transform)}' to position {angle}; turning it here too.");
                    }
                    return;   // its saved state follows once it has turned (and isn't sent back: same value)
                }
                if (act != null && ActivatedIn(c.State, out bool on))
                {
                    if (on && !act.IsActivated()) { act.Activate(false); live = " (activated live)"; }
                    else if (!on && act.IsActivated() && !act.cannotBeDeactivated) { act.Deactivate(false); live = " (deactivated live)"; }
                }
                tr.Savable.LoadSaveState(tr.Entity, c.State);
                bool prop = tr.Savable.name.StartsWith("GPE_DestructibleProp", StringComparison.Ordinal);
                if (!prop && detailLogs-- > 0)
                    Plugin.Logger.LogInfo($"[WorldDiag] Applied the other player's {Short(c.Kind)} '{PathOf(tr.Savable.transform)}' = {c.State}{live}; parts: {Parts(tr)}");
                else if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player changed {Short(c.Kind)} '{tr.Savable.name}' to {c.State}; applied here{live}.");
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
