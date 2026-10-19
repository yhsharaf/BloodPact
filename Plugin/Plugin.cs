using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using RoseNet.Session;

namespace RoseNet
{
    // Per-launch overrides such as --rosenet-role guest, so two game copies on one PC can share one config.
    internal static class CommandLine
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr GetCommandLineW();

        static readonly string Text = System.Runtime.InteropServices.Marshal.PtrToStringUni(GetCommandLineW()) ?? "";

        public static string Value(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Text, "--" + name + "[ =]+\"?([^\" ]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static T Enum<T>(string name, T fallback) where T : struct =>
            System.Enum.TryParse<T>(Value(name), true, out var v) ? v : fallback;
    }

    [BepInPlugin(Guid, "Blood Pact", Version)]
    [BepInDependency("com.ristiisa.rosecoop", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.rosenet.tailscale";
        public const string Version = "0.2.3";

        internal static ManualLogSource Logger;
        internal static UdpHost Host;
        internal static ConfigEntry<float> NeutralAfterSeconds;
        internal static ConfigEntry<bool> LogInput;

        public override void Load()
        {
            Logger = Log;

            var enabled = Config.Bind("Network", "Enabled", true,
                "Listen for guest controllers. Turn off to play purely locally.");
            var port = Config.Bind("Network", "Port", Protocol.DefaultPort,
                "UDP port guests send to. Both sides must use the same port.");
            var allowAny = Config.Bind("Network", "AllowNonTailscale", false,
                "Accept packets from any address, not only Tailscale (100.64.0.0/10, fd7a:115c:a1e0::/48) and this PC. Leave off unless you know you need it.");
            var maxPads = Config.Bind("Remote", "MaxRemotePads", 3,
                new ConfigDescription("How many guests can connect (RoseCoop supports four players in total).",
                    new AcceptableValueRange<int>(1, 3)));
            NeutralAfterSeconds = Config.Bind("Remote", "NeutralAfterSeconds", 0.5f,
                "Release all buttons and sticks of a guest pad when no packet arrived for this long.");
            var takeover = Config.Bind("Remote", "ReconnectTakeoverSeconds", 3f,
                "A reconnecting guest takes over a pad that has been silent for this long, so they keep their Rose.");

            LogInput = Config.Bind("Debug", "LogInput", false,
                "Log every guest button change and what the virtual gamepad reports back. For bug reports.");
            var bossCheat = Config.Bind("Debug", "BossLowHealthKey", false,
                "Testing only: F9 drops every living boss in your room to 1 HP (in both games). Leave off for normal play.");
            var scale = Config.Bind("Difficulty", "Enabled", true,
                "Souls-style co-op difficulty: monsters and bosses get more health while both players are in the room.");
            float[] Factors(string what, float p2, float p3, float p4)
            {
                return new[] { 1f, 1f,
                    Config.Bind("Difficulty", $"{what}Health2Players", p2, $"{what} health multiplier with 2 players (Elden Ring uses about this).").Value,
                    Config.Bind("Difficulty", $"{what}Health3Players", p3, $"{what} health multiplier with 3 players (for later; Blood Pact is 2 players today).").Value,
                    Config.Bind("Difficulty", $"{what}Health4Players", p4, $"{what} health multiplier with 4 players (for later).").Value };
            }
            var monsterHealth = Factors("Monster", 1.3f, 1.5f, 1.6f);
            var bossHealth = Factors("Boss", 1.6f, 2.3f, 2.7f);
            var probe = Config.Bind("Probe", "Enabled", false,
                "Write every room entered and every enemy spawned/killed to BepInEx\\RoseNetProbe\\. Used to compare two PCs before online sync.");

            var role = Config.Bind("Session", "Role", Role.Off,
                "Online co-op where each player runs their own game. Host = your world, Guest = join a host. " +
                "Can be overridden per launch with --rosenet-role host|guest|off.");
            var transport = Config.Bind("Session", "Transport", TransportKind.Steam,
                "Steam = connect through Steam (friends, no extra software). Udp = direct address (two copies on one PC, or Tailscale). " +
                "Override: --rosenet-transport steam|udp.");
            var udpHost = Config.Bind("Session", "UdpHostAddress", "127.0.0.1",
                "Udp only: the host's address (127.0.0.1 on the same PC, 100.x.y.z over Tailscale). Override: --rosenet-host <address>.");
            var udpPort = Config.Bind("Session", "UdpPort", 27852, "Udp only: session port. Override: --rosenet-port <port>.");
            var steamHost = Config.Bind("Session", "SteamHostId", 0UL,
                "Steam guest only: SteamID64 of the host. 0 = join the first Steam friend who is hosting in this game.");
            var sendRate = Config.Bind("Session", "SendRate", 30f,
                new ConfigDescription("Rose updates sent per second.", new AcceptableValueRange<float>(5f, 60f)));
            var tint = Config.Bind("Display", "OtherRoseTint", "#B8CCFF",
                "Color multiplied onto the other player's Rose so you can tell her apart (hex, e.g. #B8CCFF light blue, #FFB8B8 light red). #FFFFFF = no tint.");
            var showNames = Config.Bind("Display", "ShowNameTag", true, "Show the other player's name above their Rose.");
            var showStatus = Config.Bind("Display", "ShowStatus", true, "Show a small connection status line in the top-left corner.");
            var followHost = Config.Bind("Session", "FollowHost", false,
                "Guest only: when the host goes to another room, move the guest there too. Off = both explore freely; " +
                "you see each other whenever you are in the same room.");

            if (CommandLine.Value("rosenet-dump-methods") != null || System.Text.RegularExpressions.Regex.IsMatch(Environment.CommandLine, "--rosenet-dump-methods"))
                MethodDump.Write();

            var harmony = new Harmony(Guid);
            if (probe.Value)
            {
                try { Probe.Start(harmony); }
                catch (Exception e) { Log.LogError($"Probe mode failed to start: {e}"); }
            }

            var settings = new SessionSettings
            {
                Role = CommandLine.Enum("rosenet-role", role.Value),
                Transport = CommandLine.Enum("rosenet-transport", transport.Value),
                UdpHostAddress = CommandLine.Value("rosenet-host") ?? udpHost.Value,
                UdpPort = int.TryParse(CommandLine.Value("rosenet-port"), out var p) ? p : udpPort.Value,
                SteamHostId = steamHost.Value,
                AllowNonTailscale = allowAny.Value,
                SendRate = sendRate.Value,
                FollowHost = followHost.Value,
                RemoteTint = ParseColor(tint.Value),
                ShowNames = showNames.Value,
                ShowStatus = showStatus.Value,
                BossLowHealthKey = bossCheat.Value,
                ScaleDifficulty = scale.Value,
                MonsterHealth = monsterHealth,
                BossHealth = bossHealth,
            };

            if (settings.Role != Role.Off)
            {
                try
                {
                    CoopSession.Settings = settings;
                    harmony.PatchAll(typeof(Game.GamePatches));
                    harmony.PatchAll(typeof(Game.EffectPatches));
                    harmony.PatchAll(typeof(Game.ProjectilePatches));
                    harmony.PatchAll(typeof(Game.MobPatches));
                    harmony.PatchAll(typeof(Game.MobCulling));
                    harmony.PatchAll(typeof(Game.BossPatches));
                    harmony.PatchAll(typeof(Game.BossIntroPatches));
                    harmony.PatchAll(typeof(Game.SpectatePatches));
                    harmony.PatchAll(typeof(Game.PartnerMapPatches));
                    harmony.PatchAll(typeof(Game.RestPatches));
                    harmony.PatchAll(typeof(Game.RestDiag));
                    try { harmony.PatchAll(typeof(Game.PausePatches)); }
                    catch (Exception e) { Log.LogWarning($"Online pause patch failed (the pause menu will stop the world): {e.Message}"); }
                    AddComponent<CoopSession>();
                    Log.LogInfo($"Online session: {settings.Role} over {settings.Transport}.");
                }
                catch (Exception e) { Log.LogError($"Online session failed to start: {e}"); }
            }

            // Milestone-1 controller listener: only on the PC that runs the world.
            if (!enabled.Value || settings.Role == Role.Guest)
            {
                Log.LogInfo(settings.Role == Role.Guest ? "Guest: remote controller listener not needed." : "Remote controller listener disabled in config.");
                return;
            }

            Host = new UdpHost(port.Value, maxPads.Value, allowAny.Value, takeover.Value, Log);
            Host.Start();
            AddComponent<RemotePadDriver>();
        }

        static UnityEngine.Color ParseColor(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                return UnityEngine.Color.white;
            return new UnityEngine.Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
        }

        public override bool Unload()
        {
            Host?.Stop();
            return true;
        }
    }
}
