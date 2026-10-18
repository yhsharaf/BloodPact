using System;
using HarmonyLib;
using MotherBase.ActionFramework;
using MotherBase.EntityFramework;
using UnityEngine;

namespace RoseNet.Game
{
    // Thin, failure-tolerant access to the bits of the game the session needs.
    internal static class GameAccess
    {
        internal static RoomManager.RoomManagerSystem RoomSystem;

        public static Entity MainCharacter()
        {
            try
            {
                var e = MainCharacterSingleton.GetEntity();
                return e != null && e.gameObject.activeInHierarchy ? e : null;
            }
            catch { return null; }
        }

        // Unique room id from the world data (e.g. the room behind "StreetsPoorV2_Room43_uid136").
        // Chunk coordinates are not unique, so they are not used.
        public static string CurrentRoomId()
        {
            try
            {
                var room = RoomSystem?.currentRoomVariable?.CurrentValue;
                return room?.worldRoomData?.id;
            }
            catch { return null; }
        }

        public static bool IsLoadingRoom
        {
            get { try { return RoomManager.RoomManagerSystem.IsLoadingNextRoom; } catch { return false; } }
        }

        // The animated model under the main character; its subtree holds the skinned meshes and bones.
        public static Transform VisualRoot(Entity character)
        {
            var actor = character.GetComponentInChildren<AnimatedActor>(true);
            if (actor != null && actor.animator != null) return actor.animator.transform;
            var animator = character.GetComponentInChildren<Animator>(true);
            return animator != null ? animator.transform : character.transform;
        }

        // Moves the character the way the game's own teleporters do (GPE_Teleporter.TeleportOnPoint also
        // updates the character's movement state; setting the transform alone gets snapped back), then lets
        // the room manager load whatever room that lands in.
        public static void Teleport(Entity character, Vector3 position)
        {
            var go = new GameObject("Blood Pact teleport point");
            try
            {
                go.transform.position = position;
                var tp = go.AddComponent<GPE_Teleporter>();
                tp.TeleportOnPoint(character);
            }
            catch
            {
                character.transform.position = position;
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
            RoomManager.RoomManagerSystem.ForceTriggerRoomChange = true;
        }
    }

    internal static class GamePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoomManager.RoomManagerSystem), "UpdateEntities")]
        static void CaptureRoomSystem(RoomManager.RoomManagerSystem __instance)
        {
            GameAccess.RoomSystem = __instance;
        }

        // Each player keeps their own world and save. The one exception: two copies of the game on one PC (how
        // we test) share one save folder, so there the guest's copy doesn't write, or it would overwrite the
        // host's file. Only the file write is skipped; the game's save logic still runs.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MotherBase.GameServices.StandaloneAccount), nameof(MotherBase.GameServices.StandaloneAccount.WriteSave))]
        static bool BlockGuestSaveFile(string __0, ref bool __result)
        {
            if (!Session.CoopSession.IsGuest || (__0 != null && __0.IndexOf("PLAYERPREFS", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            if (!AnotherCopyRunning()) return true;
            Plugin.Logger.LogInfo($"[Session] Guest: did not write '{__0}' (another copy of the game on this PC uses the same save folder).");
            __result = true;
            return false;
        }

        static float nextCopyCheck = -1f;
        static bool anotherCopy;

        static bool AnotherCopyRunning()
        {
            if (Time.unscaledTime < nextCopyCheck) return anotherCopy;
            nextCopyCheck = Time.unscaledTime + 10f;
            try
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                anotherCopy = System.Diagnostics.Process.GetProcessesByName(me.ProcessName).Length > 1;
            }
            catch { anotherCopy = true; }   // unsure: keep the host's file safe
            return anotherCopy;
        }
    }
}
