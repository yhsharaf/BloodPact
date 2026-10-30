using System;
using System.Collections.Generic;
using MotherBase.EntityFramework;
using UnityEngine;

namespace RoseNet.Game
{
    // Locked-room fights (the gates close and waves of monsters come). Each game has its own gate, which launches
    // its own spawners and opens when they are finished, so a fight only one game runs breaks: the other game's
    // waiting monsters never wake up, and if the first player falls nobody can finish it. A game whose gate is
    // running says so once a second; the other game, when it is in that room, starts its own. Saying it again and
    // again also covers a player walking in late or coming back during the fight. Both games then spawn the same
    // waves, the room owner's monsters are mirrored as usual, and a takeover has a running fight to carry on with.
    internal static class EncounterSync
    {
        internal static readonly List<(string room, string gate)> Outgoing = new List<(string, string)>();

        static float nextTick;
        static string remoteRoom, remoteGate;
        static float remoteAt = -100f;
        static int logs = 20;

        static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static bool InRoom(string path, string room)
        {
            int slash = path.IndexOf('/');
            return !string.IsNullOrEmpty(room) && slash > 0 && room.EndsWith(path.Substring(0, slash), StringComparison.Ordinal);
        }

        // A fight is going on in our room here (we are down and may watch it).
        internal static bool ActiveHere()
        {
            string room = GameAccess.CurrentRoomId();
            try
            {
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GPE_EncounterGateComponent>())
                    if (g != null && g.isEncounterActive && InRoom(PathOf(g.transform), room)) return true;
            }
            catch { }
            return false;
        }

        public static void OnRemote(string room, string gate)
        {
            remoteRoom = room;
            remoteGate = gate;
            remoteAt = Time.unscaledTime;
        }

        // Every frame while connected.
        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < nextTick || GameAccess.IsLoadingRoom) return;
            nextTick = now + 1f;
            string room = GameAccess.CurrentRoomId();
            if (string.IsNullOrEmpty(room)) return;
            var me = GameAccess.MainCharacter();
            bool theirsHere = now - remoteAt < 3f && remoteRoom == room;
            foreach (var g in UnityEngine.Object.FindObjectsOfType<GPE_EncounterGateComponent>())
            {
                try
                {
                    if (g == null) continue;
                    string path = PathOf(g.transform);
                    if (!InRoom(path, room)) continue;
                    if (g.isEncounterActive) { Outgoing.Add((room, path)); continue; }
                    if (!theirsHere || path != remoteGate || g.IsDone || me == null) continue;
                    if (Spectate.WeAreDead()) continue;
                    g.LaunchStartEncounterAction(me);
                    if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player's locked-room fight is on; started ours ('{g.name}').");
                }
                catch (Exception e) { if (logs-- > 0) Plugin.Logger.LogWarning($"[Session] Locked-room fight: {e.Message}"); }
            }
        }

        // The other player finished a fight (their gate's saved state says complete): ours ends too.
        internal static void OnFinished(SavableEntityBase savable, Entity entity)
        {
            var g = savable.GetComponentInParent<GPE_EncounterGateComponent>() ?? savable.GetComponentInChildren<GPE_EncounterGateComponent>(true);
            if (g == null) return;
            var me = GameAccess.MainCharacter();
            g.IsDone = true;
            if (g.isEncounterActive && me != null) g.LaunchEndEncounterAction(me);
            else g.InstantDisableGate();
            if (logs-- > 0) Plugin.Logger.LogInfo($"[Session] The other player finished the locked-room fight '{g.name}'; finished here too.");
        }

        public static void Forget()
        {
            Outgoing.Clear();
            remoteAt = -100f;
        }
    }
}
