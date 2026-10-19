using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace RoseNet.Session
{
    // Faster testing together:
    //  - Build check: each game tells the other which exact Blood Pact build it runs; a different build is shown
    //    on screen (most "it got worse" reports were one player on an older DLL).
    //  - F10 bug report: saves our log and pulls the other player's log over the connection, both into
    //    BepInEx\BloodPact-reports\<time>\ on this PC. One key, no files to send around.
    internal static class DebugReport
    {
        // Short id of this exact DLL (changes with every build).
        internal static readonly string BuildId = typeof(DebugReport).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8);
        internal static string PartnerBuild;
        internal static string Notice;
        static float noticeUntil;

        internal static Action<byte[], int> Send;   // reliable send of a finished message (set by the session)
        static readonly MessageWriter writer = new MessageWriter();

        const int ChunkSize = 1000;          // fits one UDP packet on any network
        const int ChunksPerFrame = 24;
        const int MaxLogBytes = 3 * 1024 * 1024;

        // ---- build check ----

        internal static void SendBuild()
        {
            var w = writer.Begin(MsgType.BuildInfo);
            w.Write(BuildId);
            w.Write(Plugin.Version);
            Send?.Invoke(writer.Buffer, writer.Length);
        }

        internal static void OnBuild(BinaryReader r, string partnerName)
        {
            string build = r.ReadString();
            string version = r.ReadString();
            if (build == PartnerBuild) return;
            PartnerBuild = build;
            if (build == BuildId) Plugin.Logger.LogInfo($"[Session] {partnerName} runs the same Blood Pact build ({version}, {build}).");
            else Plugin.Logger.LogWarning($"[Session] {partnerName} runs a DIFFERENT Blood Pact build ({version}, {build}); ours is {Plugin.Version}, {BuildId}. Use the same RoseNet.dll on both PCs.");
        }

        internal static string BuildWarning(string partnerName) =>
            PartnerBuild != null && PartnerBuild != BuildId
                ? $"{(string.IsNullOrEmpty(partnerName) ? "Your partner" : partnerName)} has a different Blood Pact build ({PartnerBuild}, yours {BuildId}): copy the same RoseNet.dll to both PCs."
                : null;

        // ---- F10 bug report ----

        static string reportDir;
        static uint wantedId;
        static byte[][] parts;
        static int partsGot;
        static float askedAt;

        static readonly Queue<(uint id, ushort index, ushort count, byte[] data)> outgoing = new Queue<(uint, ushort, ushort, byte[])>();

        static void Show(string text, float seconds = 8f) { Notice = text; noticeUntil = Time.unscaledTime + seconds; }

        internal static void Tick(bool connected, string partnerName)
        {
            float now = Time.unscaledTime;
            if (Notice != null && now > noticeUntil) Notice = null;

            bool f10 = false;
            try { var kb = UnityEngine.InputSystem.Keyboard.current; f10 = kb != null && kb.f10Key.wasPressedThisFrame; } catch { }
            if (f10) StartReport(connected, partnerName);

            // Waiting for the other log.
            if (parts != null && now - askedAt > 45f)
            {
                SavePartner(partnerName, complete: false);
                parts = null;
            }

            // Sending ours, a few chunks per frame.
            for (int i = 0; i < ChunksPerFrame && outgoing.Count > 0; i++)
            {
                var c = outgoing.Dequeue();
                var w = writer.Begin(MsgType.LogChunk);
                w.Write(c.id);
                w.Write(c.index);
                w.Write(c.count);
                w.Write((ushort)c.data.Length);
                w.Write(c.data);
                Send?.Invoke(writer.Buffer, writer.Length);
            }
        }

        internal static string ReportDir => reportDir;

        internal static void StartReport(bool connected, string partnerName)
        {
            try
            {
                string root = Path.Combine(BepInEx.Paths.BepInExRootPath, "BloodPact-reports");
                reportDir = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(reportDir);
                Plugin.Logger.LogInfo($"[Report] Bug report requested (F10). Build {Plugin.Version} {BuildId}; partner build {PartnerBuild ?? "unknown"}.");
                File.WriteAllBytes(Path.Combine(reportDir, $"mine_{BuildId}.log"), ReadOwnLog(int.MaxValue));
                if (!connected)
                {
                    Show($"Bug report saved (only your log, not connected): {reportDir}");
                    return;
                }
                wantedId = (uint)Environment.TickCount;
                partsGot = 0;
                askedAt = Time.unscaledTime;
                parts = new byte[0][];
                var w = writer.Begin(MsgType.LogRequest);
                w.Write(wantedId);
                Send?.Invoke(writer.Buffer, writer.Length);
                Show($"Bug report: your log saved, getting {(string.IsNullOrEmpty(partnerName) ? "your partner" : partnerName)}'s log...", 45f);
            }
            catch (Exception e)
            {
                Show($"Bug report failed: {e.Message}");
                Plugin.Logger.LogWarning($"[Report] {e}");
            }
        }

        // The other player asked for our log: send its newest part, compressed, in chunks.
        internal static void OnRequest(BinaryReader r, string partnerName)
        {
            uint id = r.ReadUInt32();
            try
            {
                Plugin.Logger.LogInfo($"[Report] {partnerName} asked for our log (bug report). Build {Plugin.Version} {BuildId}.");
                byte[] raw = ReadOwnLog(MaxLogBytes);
                byte[] packed;
                using (var ms = new MemoryStream())
                {
                    using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
                    packed = ms.ToArray();
                }
                int count = Math.Max(1, (packed.Length + ChunkSize - 1) / ChunkSize);
                if (count > ushort.MaxValue) return;
                outgoing.Clear();
                for (int i = 0; i < count; i++)
                {
                    int len = Math.Min(ChunkSize, packed.Length - i * ChunkSize);
                    var part = new byte[len];
                    Buffer.BlockCopy(packed, i * ChunkSize, part, 0, len);
                    outgoing.Enqueue((id, (ushort)i, (ushort)count, part));
                }
                Show($"Sending your log to {partnerName} for a bug report...", 5f);
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Report] Could not send our log: {e.Message}"); }
        }

        internal static void OnChunk(BinaryReader r, string partnerName)
        {
            uint id = r.ReadUInt32();
            ushort index = r.ReadUInt16();
            ushort count = r.ReadUInt16();
            ushort len = r.ReadUInt16();
            byte[] data = r.ReadBytes(len);
            if (parts == null || id != wantedId) return;
            if (parts.Length != count) { parts = new byte[count][]; partsGot = 0; }
            if (index >= count || parts[index] != null) return;
            parts[index] = data;
            partsGot++;
            Show($"Bug report: getting {partnerName}'s log... {partsGot * 100 / count}%", 45f);
            if (partsGot == count) { SavePartner(partnerName, complete: true); parts = null; }
        }

        static void SavePartner(string partnerName, bool complete)
        {
            try
            {
                string who = string.IsNullOrEmpty(partnerName) ? "partner" : string.Join("_", partnerName.Split(Path.GetInvalidFileNameChars()));
                string file = Path.Combine(reportDir, $"partner_{who}_{PartnerBuild ?? "unknown"}.log");
                if (parts == null || parts.Length == 0 || !complete)
                {
                    File.WriteAllText(file + ".missing.txt", $"Did not receive the full log ({partsGot}/{parts?.Length ?? 0} parts). Is the other player on a build with F10 support?");
                    Show($"Bug report: {partnerName}'s log didn't arrive (both need this build). Yours is in {reportDir}");
                    return;
                }
                using var packed = new MemoryStream();
                foreach (var p in parts) packed.Write(p, 0, p.Length);
                packed.Position = 0;
                using (var gz = new GZipStream(packed, CompressionMode.Decompress))
                using (var outFile = File.Create(file)) gz.CopyTo(outFile);
                Plugin.Logger.LogInfo($"[Report] Bug report saved: {reportDir}");
                Show($"Bug report saved (both logs): {reportDir}", 12f);
            }
            catch (Exception e)
            {
                Show($"Bug report: could not save {partnerName}'s log: {e.Message}");
                Plugin.Logger.LogWarning($"[Report] {e}");
            }
        }

        // The file BepInEx writes this game's log to (a second copy on the same PC writes LogOutput.1.log).
        static string OwnLogPath()
        {
            try
            {
                foreach (var l in BepInEx.Logging.Logger.Listeners)
                    if (l is BepInEx.Logging.DiskLogListener d && d.LogWriter is StreamWriter sw && sw.BaseStream is FileStream fs)
                        return fs.Name;
            }
            catch { }
            return Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
        }

        // Our BepInEx log (the game keeps it open, so share it), its newest maxBytes.
        static byte[] ReadOwnLog(int maxBytes)
        {
            string path = OwnLogPath();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Position = start;
            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < buf.Length) Array.Resize(ref buf, read);
            return buf;
        }
    }
}
