#if DEV
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace CloudpunkVR.Patcher
{
    /// <summary>
    /// A test run keeps the player's settings in CloudpunkVR.cfg.before-test (its process in .test-owner) and puts them
    /// back at the end. If a run died first, put them back at the next start, before the settings are read, and keep
    /// the run's copy aside. A damaged copy (empty, NUL bytes, no [General]) is only reported. Never blocks the game.
    /// </summary>
    internal static class TestSettings
    {
        public static void PutBackIfLeft(ManualLogSource log)
        {
            try
            {
                string cfg = Path.Combine(Paths.ConfigPath, "CloudpunkVR.cfg");
                string saved = cfg + ".before-test", owner = cfg + ".test-owner";
                if (!File.Exists(saved)) return;
                string run = RunGoing(owner);
                if (run != null)
                {
                    log.LogMessage($"A test run is going ({run}): this launch uses its settings, and the run puts yours back as it ends.");
                    return;
                }
                string broken = Damaged(saved);
                if (broken != null)
                {
                    // never put a truncated copy back: keep both, tell the player
                    log.LogWarning($"A test run stopped before its cleanup left your settings in {Path.GetFileName(saved)}, but {broken}: not put back. " +
                                   "CloudpunkVR.cfg holds the run's settings; compare the two with the game closed and keep the right one as CloudpunkVR.cfg.");
                    return;
                }
                string kept = cfg + ".replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (File.Exists(cfg)) File.Copy(cfg, kept, true);
                File.Copy(saved, cfg, true);
                File.Delete(saved);
                if (File.Exists(owner)) File.Delete(owner);
                string devcmd = Path.Combine(Paths.ConfigPath, "CloudpunkVR.devcmd");
                if (File.Exists(devcmd)) File.Delete(devcmd);
                log.LogWarning($"A test run stopped before its cleanup had left its own settings in CloudpunkVR.cfg. Yours are back (from " +
                               $"{Path.GetFileName(saved)}); the run's are kept in {Path.GetFileName(kept)}.");
            }
            catch (Exception e)
            {
                log.LogWarning("Settings left by a test run: not checked (" + e.Message + "). If CloudpunkVR.cfg.before-test is in BepInEx\\config, " +
                               "it holds your own settings: copy it over CloudpunkVR.cfg with the game closed.");
            }
        }

        /// <summary>Why a kept copy can't go back (empty, NUL bytes, no [General]), or null.</summary>
        private static string Damaged(string file)
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0) return "it is empty";
            if (Array.IndexOf(bytes, (byte)0) >= 0) return "it holds NUL bytes";
            if (!System.Text.RegularExpressions.Regex.IsMatch(System.Text.Encoding.UTF8.GetString(bytes), @"(?m)^\[General\]")) return "it has no [General] section";
            return null;
        }

        /// <summary>
        /// The run that kept the settings if it's still going, else null: a PowerShell process with that id and start
        /// time, owner file under 6 hours old (a reused pid is not the run).
        /// </summary>
        private static string RunGoing(string owner)
        {
            if (!File.Exists(owner)) return null;
            if ((DateTime.Now - File.GetLastWriteTime(owner)).TotalHours > 6) return null;
            var parts = File.ReadAllText(owner).Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long started)) return null;
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; } // not running
            using (p)
            {
                string name;
                try { name = p.ProcessName; }
                catch (Exception) { return null; } // gone meanwhile
                if (!string.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase)) return null;
                try
                {
                    // another PowerShell with the same id since
                    if (Math.Abs(p.StartTime.ToUniversalTime().Ticks - started) > TimeSpan.TicksPerSecond) return null;
                }
                catch (Exception) { } // start time unreadable: assume it's the run
                return $"process {pid}, {name}";
            }
        }
    }
}
#endif
