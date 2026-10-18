using System;
using HarmonyLib;
using UnityEngine;

namespace RoseNet.Game
{
    // Where is my partner? On the game's map (which uses world coordinates: Rose's own map position is her
    // world position) the other player shows as a marker with their name. Outside the map, a short line says
    // whether they are in this room, elsewhere in this area, or in another area.
    internal static class PartnerMap
    {
        static LevelMap map;
        static bool mapOpen, logged;
        static GUIStyle markerStyle, markerShadow, hintStyle;

        internal static void OnMapEnabled(LevelMap m) { map = m; mapOpen = true; logged = false; }
        internal static void OnMapDisabled() { mapOpen = false; }

        static string Area(string room)
        {
            // "World_Biome_01_Streets_Poor_..." -> "World_Biome_01"
            if (string.IsNullOrEmpty(room)) return null;
            int n = 0;
            for (int i = 0; i < room.Length; i++)
                if (room[i] == '_' && ++n == 3) return room.Substring(0, i);
            return room;
        }

        static GUIStyle Style(int size, FontStyle fs, Color c)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.fontStyle = fs;
            s.alignment = TextAnchor.MiddleCenter;
            s.normal.textColor = c;
            return s;
        }

        static void Label(Rect r, string text, GUIStyle style, GUIStyle shadow)
        {
            if (shadow != null) GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, shadow);
            GUI.Label(r, text, style);
        }

        // Called from the session's OnGUI with the other player's latest position and room.
        internal static void Draw(bool haveRemote, Vector3 partnerPos, string partnerRoom, string name, Color tint, Vector3? partnerHead)
        {
            if (!haveRemote) return;
            if (markerStyle == null)
            {
                markerStyle = Style(20, FontStyle.Bold, tint);
                markerShadow = Style(20, FontStyle.Bold, new Color(0, 0, 0, 0.85f));
                hintStyle = Style(13, FontStyle.Normal, new Color(1, 1, 1, 0.75f));
                hintStyle.alignment = TextAnchor.UpperLeft;
            }
            string label = string.IsNullOrEmpty(name) ? "Player 2" : name;
            string myRoom = GameAccess.CurrentRoomId();

            if (mapOpen && map != null)
            {
                DrawOnMap(partnerPos, partnerRoom, myRoom, label);
                return;
            }

            // Outside the map: a one-line hint.
            bool sameRoom = !string.IsNullOrEmpty(partnerRoom) && partnerRoom == myRoom;
            string where = sameRoom ? "in this room" : (Area(partnerRoom) == Area(myRoom) ? "elsewhere in this area (see map)" : "in another area");
            GUI.Label(new Rect(10, 26, 600, 20), $"{label}: {where}", hintStyle);
        }

        static void DrawOnMap(Vector3 partnerPos, string partnerRoom, string myRoom, string label)
        {
            if (Area(partnerRoom) != Area(myRoom)) return;   // the map shows our area only
            Camera cam = null;
            try { var mc = map.mapCamera; if (mc != null) cam = mc.GetComponent<Camera>() ?? mc.GetComponentInChildren<Camera>(); } catch { }
            if (cam == null) return;
            var p = cam.WorldToScreenPoint(partnerPos);
            if (!logged)
            {
                logged = true;
                Plugin.Logger.LogInfo($"[Session] Map open: camera '{cam.name}' ortho {cam.orthographic} size {cam.orthographicSize:0.0}, renders to {(cam.targetTexture != null ? "a texture " + cam.targetTexture.width + "x" + cam.targetTexture.height : "the screen")}; partner at screen {p.x:0},{p.y:0} (world {partnerPos.x:0.0},{partnerPos.y:0.0}).");
            }
            if (p.z < 0) return;
            float x = p.x, y = p.y;
            var rt = cam.targetTexture;
            if (rt != null) { x = x / rt.width * Screen.width; y = y / rt.height * Screen.height; }
            var r = new Rect(x - 80, Screen.height - y - 34, 160, 26);
            Label(new Rect(x - 12, Screen.height - y - 12, 24, 24), "◆", markerStyle, markerShadow);   // shows fine on the map
            Label(r, label, markerStyle, markerShadow);
        }
    }

    internal static class PartnerMapPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LevelMap), nameof(LevelMap.EnableMap))]
        static void Enabled(LevelMap __instance) { try { PartnerMap.OnMapEnabled(__instance); } catch { } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LevelMap), nameof(LevelMap.DisableMap))]
        static void Disabled() { try { PartnerMap.OnMapDisabled(); } catch { } }
    }
}
