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
        internal static bool IsHost;

        internal static bool SendReady, SendSkip;   // picked up by the session
        internal static string Banner;               // shown on screen

        enum Stage { None, Checking, Asking, Waiting }
        static Stage stage;
        static float stageUntil, savedTimeScale = 1f, sharedUntil;
        static string lastRoom, declinedRoom, askRoom;
        static bool frozen, shared, remoteSkip;

        static bool IsBossRoom(string room) => !string.IsNullOrEmpty(room) && room.IndexOf("_Boss_", StringComparison.OrdinalIgnoreCase) >= 0;

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
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            string room = GameAccess.CurrentRoomId();
            bool loading = GameAccess.IsLoadingRoom;

            // Called to a fight: clear our banner once we are there.
            if (stage == Stage.None && Banner != null && askRoom != null && room == askRoom && !loading) { Banner = null; askRoom = null; }

            // Just walked into a boss room on our own: stop and see whether there is a boss to fight.
            if (!loading && !string.IsNullOrEmpty(room) && room != lastRoom)
            {
                lastRoom = room;
                if (stage != Stage.None) End("left the room");
                if (IsBossRoom(room) && room != declinedRoom && Connected() && !OtherPlayerHere())
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
                    if (Pressed(true))
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
        internal static void OnOtherSkipped()
        {
            if (!shared) return;
            try
            {
                foreach (var r in Object.FindObjectsOfType<CutsceneLineReader>())
                {
                    if (r == null || !r._isActive) continue;
                    remoteSkip = true;
                    r.HandleUserSkipped(default);
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Session] Could not skip the intro line: {e.Message}"); }
            finally { remoteSkip = false; }
        }

        // Our player pressed skip. False = ignore it (the host skips for both).
        internal static bool AllowLocalSkip()
        {
            if (!shared || remoteSkip) return true;
            if (!IsHost) return false;
            SendSkip = true;
            return true;
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
}
