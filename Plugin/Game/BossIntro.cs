using System;
using HarmonyLib;
using MotherBase.ActionFramework;
using MotherBase.EntityFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // Boss fights together. Walking into a boss room with a living boss stops our game and asks "Fight
    // together?". Yes: the other player is teleported next to us, and our game stays stopped until they are
    // in the room, so the fight (and its intro) starts for both at once. No: we fight alone. While both are
    // in a boss room, the host decides when to skip intro dialogue lines.
    internal static class BossIntro
    {
        internal static Func<bool> OtherPlayerHere = () => false;
        internal static Func<bool> Connected = () => false;
        // Whether the other player is somewhere our teleport can reach (the same area, e.g. World_Biome_01).
        internal static Func<bool> PartnerInThisArea = () => true;
        static float calledAt, noticeUntil;
        internal static bool IsHost;

        internal static bool SendReady, SendSkip;   // picked up by the session
        internal static string Banner;               // shown on screen

        enum Stage { None, Checking, Asking, Waiting }
        static Stage stage;
        static float stageUntil, savedTimeScale = 1f, sharedUntil;
        static string lastRoom, declinedRoom, askRoom;
        static bool frozen, shared, remoteSkip;

        internal static bool IsBossRoom(string room) => !string.IsNullOrEmpty(room) && room.IndexOf("_Boss_", StringComparison.OrdinalIgnoreCase) >= 0;

        static bool LiveBossIn(string room)
        {
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || !MobRegistry.InRoom(kv.Key, room) || !MobRegistry.IsAlive(mob)) continue;
                try { if (mob.GetComponent<BossComponent>() != null) return true; } catch { }
            }
            return false;
        }

        internal static bool Frozen => frozen;

        static void Freeze()
        {
            if (frozen) return;
            savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            frozen = true;
        }

        static void Unfreeze()
        {
            if (!frozen) return;
            Time.timeScale = savedTimeScale;
            frozen = false;
        }

        static void End(string why)
        {
            if (stage != Stage.None && stage != Stage.Checking) Plugin.Logger.LogInfo($"[Session] Boss: {why}.");
            stage = Stage.None;
            Banner = null;
            Unfreeze();
        }

        static bool Pressed(bool yes)
        {
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (yes) return (kb != null && kb.yKey.wasPressedThisFrame) || (pad != null && pad.buttonSouth.wasPressedThisFrame);
                return (kb != null && kb.nKey.wasPressedThisFrame) || (pad != null && pad.buttonEast.wasPressedThisFrame);
            }
            catch { return false; }
        }

        // Called by BossSync when the other player asks us to fight with them.
        internal static void CalledTo(string bossRoom)
        {
            Banner = "Joining the boss fight...";
            askRoom = bossRoom;
            calledAt = Time.unscaledTime;
        }

        // The call to a fight is over (arrived, gave up, or couldn't go): never leave its message up.
        internal static void CallOver(string notice = null)
        {
            askRoom = null;
            if (Banner != null && Banner.StartsWith("Joining")) Banner = null;
            if (notice != null && stage == Stage.None) { Banner = notice; noticeUntil = Time.unscaledTime + 6f; }
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            string room = GameAccess.CurrentRoomId();
            bool loading = GameAccess.IsLoadingRoom;

            // Called to a fight: clear our banner once we are there.
            if (stage == Stage.None && Banner != null && askRoom != null && room == askRoom && !loading) { Banner = null; askRoom = null; }
            if (askRoom != null && now - calledAt > 40f) CallOver();
            if (noticeUntil > 0f && now > noticeUntil) { noticeUntil = 0f; if (stage == Stage.None && askRoom == null) Banner = null; }

            // Just walked into a boss room on our own: stop and see whether there is a boss to fight.
            if (!loading && !string.IsNullOrEmpty(room) && room != lastRoom)
            {
                lastRoom = room;
                if (stage != Stage.None) End("left the room");
                if (IsBossRoom(room) && room != declinedRoom && Connected() && !OtherPlayerHere() && PartnerInThisArea())
                {
                    stage = Stage.Checking;
                    stageUntil = now + 1.5f;
                    Freeze();
                }
            }
            if (shared && now > sharedUntil && !OtherPlayerHere()) shared = false;
            if (OtherPlayerHere() && IsBossRoom(room)) { shared = true; sharedUntil = now + 30f; }

            switch (stage)
            {
                case Stage.Checking:
                    if (LiveBossIn(room))
                    {
                        stage = Stage.Asking;
                        Banner = "Fight this boss together?   Yes: Y / (A)     No: N / (B)";
                        Plugin.Logger.LogInfo($"[Session] Boss room {room}: asking whether to fight together.");
                    }
                    else if (now > stageUntil) End("no living boss here");
                    break;

                case Stage.Asking:
                    if (!Connected()) { End("not connected"); break; }
                    if (OtherPlayerHere()) { End("the other player is already here"); break; }
                    if (Pressed(true) || SelfTest.Active)
                    {
                        BossSync.CallPartner(room);
                        stage = Stage.Waiting;
                        stageUntil = now + 45f;
                        Plugin.Logger.LogInfo("[Session] Boss: fighting together; calling the other player.");
                    }
                    else if (Pressed(false))
                    {
                        declinedRoom = room;
                        End("fighting alone");
                    }
                    break;

                case Stage.Waiting:
                    if (OtherPlayerHere()) { End("the other player arrived; the fight begins"); break; }
                    if (Pressed(false) || now > stageUntil || !Connected()) { declinedRoom = room; End("going on alone"); break; }
                    Banner = $"Waiting for the other player...  {Mathf.CeilToInt(stageUntil - now)}     (go alone: N / (B))";
                    break;
            }
        }

        internal static void Forget()
        {
            if (stage != Stage.None) End("disconnected");
            Banner = null;
            shared = false;
        }

        // Kept for the old message; nothing waits on it any more.
        internal static void OnOtherReady() { }
        internal static void OnIntroStarted(CutscenePlayer player) { }
        internal static void OnIntroFinished() { }
        internal static void OnBossAction(Entity boss, ActionData action) { }

        // The host skipped a line: do the same here.
        static float lastRemoteSkipAt = -100f;

        internal static void OnOtherSkipped()
        {
            lastRemoteSkipAt = Time.unscaledTime;
            if (!shared) return;
            try
            {
                foreach (var r in Object.FindObjectsOfType<CutsceneLineReader>())
                {
                    if (r == null || !r._isActive) continue;
                    remoteSkip = true;
                    r.HandleUserSkipped(new UnityEngine.InputSystem.InputAction.CallbackContext());
                }
            }
            catch (Exception e)
            {
                // Can't skip for the guest: let them skip their own lines from now on, so nobody gets stuck.
                if (forwardWorks) Plugin.Logger.LogWarning($"[Session] Could not skip the intro line for the host ({e.Message}); the guest skips their own lines.");
                forwardWorks = false;
            }
            finally { remoteSkip = false; }
        }

        static bool forwardWorks = true;

        // Our player pressed skip. False = ignore it (the host skips for both, as long as that works).
        internal static bool AllowLocalSkip()
        {
            if (!shared || remoteSkip) return true;
            // The guest waits for the host's skips only while the host is actually skipping (the host may have
            // no dialogue at all, e.g. having seen it before) and forwarding works.
            if (!IsHost) return !forwardWorks || Time.unscaledTime - lastRemoteSkipAt > 15f;
            SendSkip = true;
            return true;
        }
    }

    // Spectating: when we die in a boss fight our partner is still fighting, the game's switch back to the
    // save room (an ApplicationFlowManager state load) waits; our camera stays in the arena. When the fight is
    // over for us (boss gone, partner down or gone, or 5 minutes), the held switch goes ahead.
    internal static class Spectate
    {
        internal static Func<bool> WeAreDead = () => false, PartnerFighting = () => false;
        static Action held;
        static string heldWhat;
        static float since;
        internal static bool Replaying;

        static bool BossAliveHere()
        {
            string room = GameAccess.CurrentRoomId();
            if (string.IsNullOrEmpty(room) || room.IndexOf("_Boss_", StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || !MobRegistry.InRoom(kv.Key, room) || !MobRegistry.IsAlive(mob)) continue;
                try { if (mob.GetComponent<BossComponent>() != null) return true; } catch { }
            }
            return false;
        }

        static bool ShouldWatch() => WeAreDead() && PartnerFighting() && BossAliveHere();

        // A state load the game wants to do. True = let it happen now.
        internal static bool Allow(string what, Action replay)
        {
            Plugin.Logger.LogInfo($"[Session] Game state change: {what}{(Replaying ? " (going ahead now)" : "")}.");
            if (Replaying || held != null) return held == null || Replaying;
            if (!ShouldWatch()) return true;
            held = replay;
            heldWhat = what;
            since = Time.unscaledTime;
            BossIntro.Banner = "You fell. Watching your partner fight...";
            Plugin.Logger.LogInfo($"[Session] We fell in the boss fight; spectating instead of '{what}' for now.");
            return false;
        }

        // Our partner won while we were down: no trip back to the save room; we get up where we fell.
        // Our partner won while we were down. Getting up on the spot fights the game's own death sequence (the
        // player can stay frozen), so the respawn the game wanted goes ahead as normal; the win waits for us:
        // back in the boss room, our copy of the boss falls and we get its reward (see Victory).
        internal static float RespawningUntil;

        internal static void OnVictory()
        {
            if (held == null) return;
            var go = held;
            held = null;
            RespawningUntil = Time.unscaledTime + 10f;
            BossIntro.CallOver("Your partner won! Go back to the boss room for your reward.");
            Plugin.Logger.LogInfo($"[Session] The other player won while we were down; '{heldWhat}' goes ahead (respawn), the boss falls for us when we are back in its room.");
            try { Replaying = true; go(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not respawn after the win: {e.Message}"); }
            finally { Replaying = false; }
        }

        // Dying hides the player HUD and shows the death screen; normally the respawn's level reload brings it
        // back. Getting up in place skips that reload, so put the HUD back ourselves.
        internal static void RestoreHud()
        {
            try { foreach (var c in Object.FindObjectsOfType<HUDDeathController>()) c.ResetHUDDeath(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not clear the death screen: {e.Message}"); }
            try { Doozy.Runtime.UIManager.Containers.UIView.Hide(Doozy.Runtime.UIManager.UIViewId.GameplayHUD.DeathHUD, false); } catch { }
            try
            {
                Doozy.Runtime.UIManager.Containers.UIView.Show(Doozy.Runtime.UIManager.UIViewId.GameplayHUD.HUDPlayer, false);
                Doozy.Runtime.UIManager.Containers.UIView.Show(Doozy.Runtime.UIManager.UIViewId.GameplayHUD.CurrencyHUD, false);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not show the HUD again: {e.Message}"); }
        }

        // The respawn we held back had already started: the game pushed its "loading" state, which blocks all
        // input (moving, the pause menu, picking up the boss reward) until the load ends. That load never
        // happens now, so take the state off ourselves.
        static void ReleaseLoadingLock()
        {
            // The respawn's first half already ran: the flow manager marked a level load as started (that flag
            // blocks the player's input) and told the game it began. Cancel it and tell the game it ended.
            try
            {
                var afm = Object.FindObjectOfType<MotherBase.ApplicationFlowManager.ApplicationFlowManager>();
                bool wasLoading = MotherBase.ApplicationFlowManager.ApplicationFlowManager._isLoading;
                if (afm != null && wasLoading)
                {
                    MotherBase.ApplicationFlowManager.ApplicationFlowManager._isLoading = false;
                    afm._nextStateLogic = null;
                    try { afm.onEndLoadState?.Invoke(); } catch (Exception e) { Plugin.Logger.LogWarning($"[Session] End-of-load signal: {e.Message}"); }
                }
                Plugin.Logger.LogInfo($"[Session] Level load in progress when getting up: {wasLoading}{(wasLoading ? " (cancelled)" : "")}.");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not cancel the held respawn: {e.Message}"); }
            try
            {
                var gsm = GameStateManager.GetInstance();
                if (gsm == null) return;
                string before = StateName(gsm);
                for (int i = 0; i < 3 && gsm.IsLoadingState(); i++) gsm.TryPopLoadingStateFromApplicationFlow();
                // The death may have left the player as a dead player: no input, in stasis, HUD hidden, the
                // gameplay controls switched off. Put the player back as the current state wants them.
                string maps = ActiveMaps(gsm);
                var st = gsm.activeState;
                if (st != null) gsm.ApplyPlayerState(st);
                Plugin.Logger.LogInfo($"[Session] Game state after getting up: {before} -> {StateName(gsm)}{(gsm.IsLoadingState() ? " (still loading: input may stay blocked)" : "")}; controls on before: {maps}; after: {ActiveMaps(gsm)}; our Rose runs: {RoseMoves()}.");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not unblock input after getting up: {e.Message}"); }
        }

        static string ActiveMaps(GameStateManager gsm)
        {
            try
            {
                var asset = gsm.inputActionAsset;
                if (asset == null) return "?";
                var on = new System.Collections.Generic.List<string>();
                foreach (var m in asset.actionMaps) if (m.enabled) on.Add(m.name);
                return on.Count > 0 ? string.Join(",", on) : "none";
            }
            catch { return "?"; }
        }

        static string RoseMoves()
        {
            try
            {
                var me = GameAccess.MainCharacter();
                var list = me?.GetComponent<Actor>()?.activeActions;
                if (list == null) return "?";
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < list.Count; i++) names.Add(list[i].data != null ? list[i].data.name : "?");
                return names.Count > 0 ? string.Join(" ", names) : "none";
            }
            catch { return "?"; }
        }

        static string StateName(GameStateManager gsm)
        {
            try { var st = gsm.activeState; return st != null ? st.name : "none"; } catch { return "?"; }
        }

        static void Revive()
        {
            var me = GameAccess.MainCharacter();
            if (me == null) return;
            try
            {
                var d = MobControl.DamageableOf(me);
                if (d != null) d.ReviveAndReset();
                var actor = me.GetComponent<Actor>();
                var active = actor != null ? actor.activeActions : null;
                if (active != null)
                {
                    var dying = new System.Collections.Generic.List<ActionInstance>();
                    for (int i = 0; i < active.Count; i++)
                    {
                        var a = active[i];
                        string n = a != null && a.data != null ? a.data.name : null;
                        if (n != null && n.IndexOf("Death", StringComparison.OrdinalIgnoreCase) >= 0) dying.Add(a);
                    }
                    foreach (var a in dying) actor.StopAction(me, a, null);
                }
                RestoreHud();
                ReleaseLoadingLock();
                Plugin.Logger.LogInfo("[Session] The other player won the boss fight; we get up where we fell.");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not revive after the boss fight: {e.Message}"); }
        }

        internal static void Tick()
        {
            if (held == null) return;
            // The boss is gone while our partner is still here and standing: they won.
            if (WeAreDead() && PartnerFighting() && !BossAliveHere()) { OnVictory(); return; }
            if (ShouldWatch() && Time.unscaledTime - since < 300f) return;
            var go = held;
            held = null;
            if (BossIntro.Banner != null && BossIntro.Banner.StartsWith("You fell")) BossIntro.Banner = null;
            Plugin.Logger.LogInfo($"[Session] Spectating over; '{heldWhat}' goes ahead.");
            try { Replaying = true; go(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not continue after spectating: {e.Message}"); }
            finally { Replaying = false; }
        }
    }

    // Testing aid ([Debug] BossLowHealthKey): F9 drops the living bosses in our room to 1 HP. A boss the other
    // game runs gets the damage through the normal hit path, so both worlds agree.
    internal static class BossCheat
    {
        public static void Tick(bool enabled)
        {
            if (!enabled) return;
            bool f9;
            try { var kb = UnityEngine.InputSystem.Keyboard.current; f9 = kb != null && kb.f9Key.wasPressedThisFrame; }
            catch { return; }
            if (f9) DropBosses();
        }

        // F9: every living boss in our room to 1 HP (in both games: a boss the other game runs gets the hit).
        static void DropBosses()
        {
            string room = GameAccess.CurrentRoomId();
            int n = 0;
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                if (mob == null || !MobRegistry.InRoom(kv.Key, room) || !MobRegistry.IsAlive(mob)) continue;
                try
                {
                    if (mob.GetComponent<BossComponent>() == null) continue;
                    var d = MobControl.DamageableOf(mob);
                    if (d == null || d.currentHealth <= 1) continue;
                    if (MobControl.IsPossessed(mob)) MobControl.OutgoingHits.Add((kv.Key, d.currentHealth - 1));
                    else { d.currentHealth = 1; BossHud.Refresh(mob, d); }
                    n++;
                }
                catch { }
            }
            Plugin.Logger.LogInfo($"[Session] Debug F9: {n} boss(es) dropped to 1 HP.");
        }
    }

    // Shared victory: a boss that dies in the game running it is reported; the other game gets its player
    // back up if they were down, then plays its own copy's death, so walls, defeat and rewards happen there too.
    internal static class Victory
    {
        internal static readonly System.Collections.Generic.List<string> Outgoing = new System.Collections.Generic.List<string>();
        internal static bool Applying;
        static string lastRoom, pendingRoom;
        static float lastAt, pendingAt, roomSince = -1f, nextTry, fightSeen = -1f;

        internal static void OnLocalBossDied(Entity boss)
        {
            if (Applying || boss == null || MobControl.IsPossessed(boss)) return;
            try { if (boss.GetComponent<BossComponent>() == null) return; } catch { return; }
            string room = GameAccess.CurrentRoomId();
            if (room == lastRoom && Time.unscaledTime - lastAt < 30f) return;
            lastRoom = room; lastAt = Time.unscaledTime;
            Outgoing.Add(room);
            Plugin.Logger.LogInfo($"[Session] Boss defeated here ({boss.name}); telling the other player.");
        }

        // Bosses the other player beat this session. Our copy falls whenever we are in that room, however
        // late we get there (we may have died at the same moment and respawned, or been elsewhere): we get
        // the win and the reward too, and the boss never comes back to life here.
        static readonly System.Collections.Generic.HashSet<string> beaten = new System.Collections.Generic.HashSet<string>();

        internal static void ForgetForTest() { beaten.Clear(); MobControl.OwnerKilled.Clear(); }

        internal static void OnRemote(string room)
        {
            Plugin.Logger.LogInfo($"[Session] The other player defeated the boss in {room}.");
            beaten.Add(room);
            pendingAt = Time.unscaledTime;
            Spectate.OnVictory();
        }

        internal static void Tick()
        {
            if (beaten.Count == 0 || GameAccess.IsLoadingRoom) return;
            float now = Time.unscaledTime;
            if (now < Spectate.RespawningUntil && Spectate.WeAreDead()) return;   // respawning: the win waits for our return
            string here = GameAccess.CurrentRoomId();
            if (here == null || !beaten.Contains(here)) { roomSince = -1f; fightSeen = -1f; return; }
            if (roomSince < 0f) roomSince = now;
            if (now - pendingAt < 0.8f || now - roomSince < 0.8f) return;   // let a revive / the room settle first
            // Our fight here must have begun (the boss's intro, then the fight): a boss that falls before it
            // starts leaves the game in a fight that never ends, and the reward can't be picked up.
            if (BossSync.LocalFightRoom != here && now - roomSince < 60f) return;
            if (BossSync.LocalFightRoom == here && fightSeen < 0f) fightSeen = now + 1.5f;
            if (BossSync.LocalFightRoom == here && now < fightSeen) return;
            if (now < nextTry) return;
            nextTry = now + 0.5f;
            pendingRoom = here;
            var me = GameAccess.MainCharacter();
            int n = 0;
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                // Our copy may already show 0 health (our own last hit, or the owner's), with its death held
                // back: it still needs the game's own death here, which ends the fight and drops the rewards.
                if (mob == null || !MobRegistry.InRoom(kv.Key, pendingRoom) || MobControl.OwnerKilled.Contains(mob.Pointer)) continue;
                try
                {
                    if (mob.destroyed || !mob.gameObject.activeInHierarchy || mob.GetComponent<BossComponent>() == null) continue;
                    // Ours already fell for real (we ran it): nothing to do. A copy driven by the other game can
                    // show 0 health with its death held back; that one still needs the game's own death.
                    if (!MobControl.IsPossessed(mob) && !MobRegistry.IsAlive(mob)) continue;
                    var d = MobControl.DamageableOf(mob);
                    if (d == null) continue;
                    Applying = true;
                    MobControl.OwnerKilled.Add(mob.Pointer);
                    MobControl.AllowDeath = true;
                    MobControl.AllowStart = true;
                    MobPatches.ShowAgain?.Invoke(mob);   // back where it is, drawn, so its death and drops happen there
                    MobControl.Possessed.Remove(mob.Pointer);   // its death plays here, with all its moves (the reward)
                    if (d.currentHealth <= 0) d.currentHealth = 1;
                    d.currentHealth = 0;
                    d.Die(mob, me, new DamageData());
                    n++;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not finish our copy of the boss: {e.Message}"); }
                finally { Applying = false; MobControl.AllowDeath = false; MobControl.AllowStart = false; }
            }
            if (n > 0) Plugin.Logger.LogInfo($"[Session] Our copy of the boss falls too ({n}).");
        }
    }

    internal static class SpectatePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Damageable), nameof(Damageable.Die))]
        static void SomethingDied(Entity __0)
        {
            try { Victory.OnLocalBossDied(__0); } catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MotherBase.ApplicationFlowManager.ApplicationFlowManager), nameof(MotherBase.ApplicationFlowManager.ApplicationFlowManager.ReloadCurrentState))]
        static bool Reload(MotherBase.ApplicationFlowManager.ApplicationFlowManager __instance)
        {
            try { var m = __instance; return Spectate.Allow("reload current state", () => m.ReloadCurrentState()); } catch { return true; }
        }

        // Both LoadState overloads end here. (Never patch the ApplicationStateID overload: it is a value type,
        // and patching IL2CPP methods with value-type arguments crashes the game.)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MotherBase.ApplicationFlowManager.ApplicationFlowManager), nameof(MotherBase.ApplicationFlowManager.ApplicationFlowManager.LoadStateInternal))]
        static bool LoadInternal(MotherBase.ApplicationFlowManager.ApplicationFlowManager __instance, MotherBase.ApplicationFlowManager.ApplicationState __0)
        {
            try
            {
                var m = __instance; var st = __0;
                string name = null; try { name = st != null ? st.name : null; } catch { }
                return Spectate.Allow($"load state '{name}'", () => m.LoadStateInternal(st));
            }
            catch { return true; }
        }
    }

    internal static class BossIntroPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.OnCutscenePlayerStarted))]
        static void Started(CutscenePlayer __0)
        {
            try { BossIntro.OnIntroStarted(__0); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.OnCutscenePlayerFinished))]
        static void Finished()
        {
            try { BossIntro.OnIntroFinished(); } catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CutsceneLineReader), nameof(CutsceneLineReader.HandleUserSkipped))]
        static bool Skip()
        {
            try { return BossIntro.AllowLocalSkip(); } catch { return true; }
        }
    }

    // Online, the pause menu must not stop the world: whoever runs a room's monsters (a boss too) would freeze
    // them for both players. The game pauses its entity world (EntityContext.Pause) as well as time.
    internal static class PausePatches
    {
        internal static Func<bool> KeepRunning = () => false;
        static int logs = 3;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EntityContext), nameof(EntityContext.Pause))]
        static bool NoPauseOnline()
        {
            try
            {
                if (!KeepRunning()) return true;
                if (logs-- > 0) Plugin.Logger.LogInfo("[Session] The game wanted to pause its world; online it keeps running.");
                return false;
            }
            catch { return true; }
        }
    }
}
