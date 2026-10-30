using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx.Logging;

namespace RoseNet.Session
{
    // BepInEx overwrites LogOutput.log every time the game starts, so a player who restarts after a problem loses
    // the log that shows it. Each run also gets its own file in BepInEx\BloodPact-logs; the newest few are kept.
    // Every line is flushed, so the file survives a crash.
    internal sealed class SessionLog : ILogListener
    {
        const int Keep = 5;
        const long MaxBytes = 20 * 1024 * 1024;

        readonly StreamWriter writer;
        long written;

        public LogLevel LogLevelFilter => LogLevel.All;

        SessionLog(StreamWriter w) { writer = w; }

        internal static void Start()
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "BloodPact-logs");
                Directory.CreateDirectory(dir);
                foreach (var old in new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep - 1))
                {
                    try { old.Delete(); } catch { }
                }
                string name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Process.GetCurrentProcess().Id}.log";
                var file = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                Logger.Listeners.Add(new SessionLog(new StreamWriter(file) { AutoFlush = true }));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Could not start the session log: {e.Message}"); }
        }

        public void LogEvent(object sender, LogEventArgs e)
        {
            if (written > MaxBytes) return;
            try
            {
                string line = e.ToStringLine();
                writer.Write(line);
                written += line.Length;
            }
            catch { }
        }

        public void Dispose()
        {
            try { writer.Dispose(); } catch { }
        }
    }
}
