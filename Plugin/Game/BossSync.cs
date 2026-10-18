using System;
using System.Collections.Generic;
using HarmonyLib;
using MotherBase.EntityFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // Boss fights together: when either player sets off a boss encounter (BossFightTrigger), the other player
    // is pulled into the arena, and their game starts the same encounter on its own trigger, so both worlds
    // have the arena walls, the boss HUD and the boss. The player who started it entered the room first, so
    // their game runs the boss and the other mirrors it like any monster.
    internal static class BossSync
    {
        // Room/Position: where to put the partner (just outside the boss room, where the starter came from).
        // The room whose boss fight has started in OUR game (camera, walls, HUD set up here).
        internal static string LocalFightRoom;

        // A boss of ours that must still run its own intro: until our fight has started, we leave it to the game.
        internal static bool AwaitingLocalStart(Entity mob)
        {
            try
            {
                if (mob == null || mob.GetComponent<BossComponent>() == null) return false;
                return GameAccess.CurrentRoomId() != LocalFightRoom;
            }
            catch { return false; }
        }

        internal struct Start { public string Room, BossRoom, Trigger, Fight; public uint BossId; public Vector3 Position; }

        // Where our Rose was shortly before entering the current room, and which room that was.
        static string curRoom, prevRoom;
        static Vector3 prevPos;
        static readonly Queue<(float t, Vector3 p)> trail = new Queue<(float, Vector3)>();

        internal static void Track()
        {
            var me = GameAccess.MainCharacter();
            if (me == null || GameAccess.IsLoadingRoom) return;
            string room = GameAccess.CurrentRoomId();
            float now = Time.unscaledTime;
            if (string.IsNullOrEmpty(room)) return;
            if (room != curRoom)
            {
                if (curRoom != null && trail.Count > 0)
                {
                    // A moment before the doorway, so the partner lands inside that room, not in the passage.
                    var pick = trail.Peek().p;
                    foreach (var e in trail) { if (e.t <= now - 0.7f) pick = e.p; }
                    prevRoom = curRoom;
                    prevPos = pick;
                }
                curRoom = room;
                trail.Clear();
                // A fight we had here before (we died, left...) doesn't count for this visit.
                if (LocalFightRoom != room) LocalFightRoom = null;
            }
            trail.Enqueue((now, me.transform.position));
            while (trail.Count > 0 && trail.Peek().t < now - 2f) trail.Dequeue();
        }

        static string PathOf(Component c)
        {
            var sb = new System.Text.StringBuilder(c.name);
            for (var p = c.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static uint BossIdOf(BossFight f)
        {
            try
            {
                var boss = f._currentBossVariable != null ? f._currentBossVariable.CurrentValue : null;
                if (boss == null) return 0;
                foreach (var kv in MobRegistry.ById) if (kv.Value != null && kv.Value.Pointer == boss.Pointer) return kv.Key;
            }
            catch { }
            return 0;
        }

        // The game's boss-fight objects in the loaded scenes, including inactive ones.
        static IEnumerable<T> SceneObjects<T>() where T : Component
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>()))
            {
                var c = o.TryCast<T>();
                if (c != null && c.gameObject.scene.IsValid()) yield return c;
            }
        }

        internal static readonly List<Start> Outgoing = new List<Start>();

        static Start? pending;           // the other player's fight we are being pulled into
        static float pendingSince, nextTeleport;
        static int teleports;
        static bool startingLocally;     // our own trigger call for the other player's fight: don't send it back

        // A stable name for a trigger, the same on both PCs: its save key if it has one, else its path.
        internal static string KeyOf(BossFightTrigger t)
        {
            try
            {
                var sav = t.GetComponentInParent<SavableEntityBase>();
                if (sav != null && !string.IsNullOrEmpty(sav.Id)) return sav.Id;
            }
            catch { }
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.transform.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static float lastSent = -100f;
        static string lastSentRoom;

        // A boss fight began in our world (any of the game's entry points; t may be null).
        // True if we called the other player (so it makes sense to wait for them).
        internal static bool OnLocalStart(string via, BossFightTrigger t, Entity player, BossFight fight = null)
        {
            Plugin.Logger.LogInfo($"[Session] Boss event: {via}{(t != null ? $" ('{t.name}')" : "")}{(startingLocally ? " (ours, for the other player's fight)" : "")}.");
            // Calling the other player is now the "Fight together?" prompt's job (BossIntro).
            return false;
#pragma warning disable CS0162
            if (startingLocally || pending != null) return false;
            var me = GameAccess.MainCharacter();
            if (me == null) return false;
            if (player != null && player.Pointer != me.Pointer) return false;
            if (BossIntro.OtherPlayerHere()) return false;           // fighting together already
            // One fight fires several of these (intro, fight start...): call the other player once.
            if (GameAccess.CurrentRoomId() == lastSentRoom && Time.unscaledTime - lastSent < 180f) return false;
            // A fight without a trigger (started by a cutscene) is identified by its BossFight instead.
            lastSent = Time.unscaledTime;
            lastSentRoom = GameAccess.CurrentRoomId();
            string bossRoom = GameAccess.CurrentRoomId();
            bool haveEntry = !string.IsNullOrEmpty(prevRoom) && prevRoom != bossRoom;
            var s = new Start
            {
                Room = haveEntry ? prevRoom : bossRoom, BossRoom = bossRoom, Trigger = t != null ? KeyOf(t) : "",
                Fight = fight != null ? PathOf(fight) : "", BossId = fight != null ? BossIdOf(fight) : 0,
                Position = haveEntry ? prevPos : me.transform.position,
            };
            Outgoing.Add(s);
            Plugin.Logger.LogInfo($"[Session] Boss fight started here (in {bossRoom}); calling the other player to the gates in {s.Room}.");
            // What we already know about this boss (e.g. its name) goes along too.
            try
            {
                var data = fight != null ? fight._bossData : (t != null ? t.bossData : null);
                var prog = Object.FindObjectOfType<CharacterProgressionComponent>();
                if (data != null && data.characterID != null && prog != null)
                    CharacterSync.OnLocalStep(data.characterID, prog.GetProgressionStep(data.characterID));
            }
            catch { }
            return true;
#pragma warning restore CS0162
        }

        // "Fight together?" -> Yes: bring the other player into this boss room, next to us.
        internal static void CallPartner(string bossRoom)
        {
            var me = GameAccess.MainCharacter();
            if (me == null) return;
            Outgoing.Add(new Start { Room = bossRoom, BossRoom = bossRoom, Trigger = "", Fight = "", Position = me.transform.position + new Vector3(-1.2f, 0f, 0f) });
            try
            {
                foreach (var f in SceneObjects<BossFight>())
                {
                    var data = f._bossData;
                    var prog = Object.FindObjectOfType<CharacterProgressionComponent>();
                    if (data != null && data.characterID != null && prog != null)
                        CharacterSync.OnLocalStep(data.characterID, prog.GetProgressionStep(data.characterID));
                    break;
                }
            }
            catch { }
        }

        // The other player set off an encounter: go there, then start it here too.
        internal static void OnRemoteStart(Start s)
        {
            pending = s;
            pendingSince = Time.unscaledTime;
            teleports = 0;
            nextTeleport = 0;
            BossIntro.CalledTo(s.BossRoom);
            Plugin.Logger.LogInfo($"[Session] The other player wants to fight the boss in {s.BossRoom} together; teleporting there.");
        }

        // Every frame while connected.
        internal static void Tick()
        {
            Track();
            HoldInPlace();
            if (pending == null) return;
            var s = pending.Value;
            float now = Time.unscaledTime;
            var me = GameAccess.MainCharacter();
            if (me == null || GameAccess.IsLoadingRoom) return;
            string room = GameAccess.CurrentRoomId();
            if (room == s.BossRoom) { pending = null; return; }   // already in the arena
            if (room == s.Room && teleports > 0)
            {
                pending = null;
                Plugin.Logger.LogInfo("[Session] At the boss gates; walk in to join the fight.");
                return;
            }
            if (now - pendingSince > 30f || teleports >= 5)
            {
                Plugin.Logger.LogWarning("[Session] Could not reach the boss gates; giving up.");
                pending = null;
                return;
            }
            // Bring our Rose next to the other player in the boss room (before their fight has started).
            if (now < nextTeleport) return;
            nextTeleport = now + 3f;
            teleports++;
            var spot = s.Position + new Vector3(0f, 0.3f, 0f);
            GameAccess.Teleport(me, spot);
            StartHold(spot, s.BossRoom);
            Plugin.Logger.LogInfo($"[Session] Teleporting to the boss fight (try {teleports}).");
        }

        // After a teleport we stay on the spot until the room there has loaded (its floor too) plus a moment,
        // at least 3 seconds; otherwise we can drop through a floor that isn't there yet.
        static bool holding;
        static Vector3 holdSpot;
        static string holdRoom;
        static float holdStart, roomReadyAt;

        static void StartHold(Vector3 spot, string room)
        {
            holding = true;
            holdSpot = spot;
            holdRoom = room;
            holdStart = Time.unscaledTime;
            roomReadyAt = -1f;
        }

        static void HoldInPlace()
        {
            if (!holding) return;
            var me = GameAccess.MainCharacter();
            float now = Time.unscaledTime;
            if (me == null) return;
            bool ready = !GameAccess.IsLoadingRoom && GameAccess.CurrentRoomId() == holdRoom;
            if (!ready) roomReadyAt = -1f;
            else if (roomReadyAt < 0f) roomReadyAt = now;
            if ((roomReadyAt >= 0f && now - roomReadyAt >= 1.5f && now - holdStart >= 3f) || now - holdStart > 15f)
            {
                holding = false;
                Plugin.Logger.LogInfo($"[Session] Arrived; free to move ({now - holdStart:0.0} s after the teleport).");
                return;
            }
            GameAccess.Pin(me, holdSpot);
        }

        // Our BossFight for the other player's fight, given our copy of their boss. False = not ready yet.
        static bool TryStartFight(Start s, Entity me)
        {
            BossFight fight = null;
            foreach (var f in SceneObjects<BossFight>()) { if (PathOf(f) == s.Fight) { fight = f; break; } }
            if (fight == null) { foreach (var f in SceneObjects<BossFight>()) { fight = f; break; } }
            if (fight == null) return false;

            Entity boss = null;
            if (s.BossId != 0) MobRegistry.ById.TryGetValue(s.BossId, out boss);
            if (boss == null)
            {
                foreach (var kv in MobRegistry.ById)
                    if (kv.Value != null && MobRegistry.InRoom(kv.Key, s.Room) && kv.Value.GetComponent<BossComponent>() != null) { boss = kv.Value; break; }
            }
            if (boss == null) return false;   // our copy of the boss isn't here yet

            try
            {
                startingLocally = true;
                if (fight._currentBossVariable != null) fight._currentBossVariable.SetValue(boss);
                fight.StartBossFight();
                Plugin.Logger.LogInfo($"[Session] Joined the boss fight ('{fight.name}', boss '{boss.name}').");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Session] Could not start the boss fight here: {e.Message}");
            }
            finally { startingLocally = false; }
            return true;
        }

        internal static void Forget() { pending = null; Outgoing.Clear(); }
    }

    // What each player knows about characters (bosses too): Unknown -> Named -> Hinted -> Found -> Absorbed.
    // A boss's name shows in its health bar only once it is Named, which the intro does for whoever played
    // it. Steps up to Found are shared; Absorbed (the arcana reward) is not.
    internal static class CharacterSync
    {
        internal struct Step { public string Character; public int Value; }
        internal static readonly List<Step> Outgoing = new List<Step>();
        static bool applying;
        static int logs = 20;

        internal static void OnLocalStep(CharacterID id, CharacterProgressionStep step)
        {
            if (applying || id == null) return;
            if (step < CharacterProgressionStep.Named || step > CharacterProgressionStep.Found) return;
            Outgoing.Add(new Step { Character = id.name, Value = (int)step });
        }

        internal static void Apply(Step s)
        {
            var step = (CharacterProgressionStep)s.Value;
            if (step < CharacterProgressionStep.Named || step > CharacterProgressionStep.Found) return;
            try
            {
                CharacterID id = null;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<CharacterID>()))
                {
                    var c = o.TryCast<CharacterID>();
                    if (c != null && c.name == s.Character) { id = c; break; }
                }
                var prog = Object.FindObjectOfType<CharacterProgressionComponent>();
                if (id == null || prog == null) return;
                if (prog.GetProgressionStep(id) >= step) return;
                applying = true;
                prog.SetProgressionStep(id, step, false, false);
                if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player's game knows '{s.Character}' as {step}; now ours does too.");
                var hud = Object.FindObjectOfType<HUDBossManager>();
                if (hud != null && hud.bossCharacterDisplay != null) hud.bossCharacterDisplay.RefreshDisplay();
            }
            catch (Exception e)
            {
                if (logs-- > 0) Plugin.Logger.LogWarning($"[Session] Could not share what we know about '{s.Character}': {e.Message}");
            }
            finally { applying = false; }
        }
    }

    internal static class BossPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterProgressionComponent), nameof(CharacterProgressionComponent.SetProgressionStep))]
        static void CharacterStepSet(CharacterID __0, CharacterProgressionStep __1)
        {
            try { CharacterSync.OnLocalStep(__0, __1); } catch { }
        }

        // The game has several ways into a boss fight; whichever comes first counts.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BossFightTrigger), nameof(BossFightTrigger.OnPlayerTriggerEncounter))]
        static void EncounterTriggered(BossFightTrigger __instance, Entity __0)
        {
            try { BossSync.OnLocalStart("trigger touched", __instance, __0); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BossFightTrigger), nameof(BossFightTrigger.InitEncounter))]
        static void EncounterInit(BossFightTrigger __instance)
        {
            try { Plugin.Logger.LogInfo($"[Session] Boss event: encounter set up ('{__instance.name}', beaten: {__instance.IsBossDefeated()})."); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BossFightTrigger), nameof(BossFightTrigger.StartBossFight))]
        static void TriggerStart(BossFightTrigger __instance, Entity __0)
        {
            try { BossSync.OnLocalStart("trigger started the fight", __instance, null); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BossFight), nameof(BossFight.StartBossFight))]
        static void FightStart(BossFight __instance)
        {
            try { BossSync.LocalFightRoom = GameAccess.CurrentRoomId(); } catch { }
            try { BossSync.OnLocalStart("boss fight started", null, null, __instance); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BossFightStarter), nameof(BossFightStarter.StartBossFight))]
        static void StarterStart()
        {
            try { BossSync.OnLocalStart("boss starter", null, null); } catch { }
        }
    }
}
