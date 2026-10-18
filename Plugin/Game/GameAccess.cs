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

        public static void Teleport(Entity character, Vector3 position)
        {
            character.transform.position = position;
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

        // The guest plays inside the host's world: its own save file must never be overwritten by that.
        // Only the file write is skipped; the game's save logic still runs (resting resets monsters through it).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MotherBase.GameServices.StandaloneAccount), nameof(MotherBase.GameServices.StandaloneAccount.WriteSave))]
        static bool BlockGuestSaveFile(string __0, ref bool __result)
        {
            if (!Session.CoopSession.IsGuest || (__0 != null && __0.IndexOf("PLAYERPREFS", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            Plugin.Logger.LogInfo($"[Session] Guest: did not write '{__0}' (only the host's world is saved).");
            __result = true;
            return false;
        }
    }
}
