using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Preloader.Core.Patching;

namespace CloudpunkVR.Patcher
{
    public enum StartMode { Auto, Always, Never }

    /// <summary>
    /// Preloader: picks VR or flat before Unity reads its VR settings. VR enables Unity's OpenVR support and deploys
    /// openvr_api.dll, flat restores the originals. Passes the choice to the plugin via CLOUDPUNKVR_START.
    /// </summary>
    [PatcherPluginInfo("com.cloudpunkvr.patcher", "Cloudpunk VR enabler", "0.3.0")]
    public class VrEnablerPatcher : BasePatcher
    {
        public const string BackupSuffix = ".cloudpunkvr-original";
        public const string DecisionVariable = "CLOUDPUNKVR_START";

        public override void Initialize()
        {
#if DEV
            // before the settings are read: undo what a crashed test run left in the cfg
            TestSettings.PutBackIfLeft(Log);
#endif
            try
            {
                var cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, "CloudpunkVR.cfg"), true);
                var mode = cfg.Bind("General", "StartInVR", StartMode.Auto,
                    "Auto: VR when SteamVR is already running, otherwise the game starts on the monitor. Always: VR at every launch (starts SteamVR). Never: flat, without uninstalling. --vr or --novr in Steam's launch options override this for one launch.").Value;

                string reason;
                bool vr = Decide(mode, out reason);
                Environment.SetEnvironmentVariable(DecisionVariable, (vr ? "vr:" : "flat:") + reason);

                string dataDir = Path.Combine(Paths.GameRootPath, Paths.ProcessName + "_Data");
                string ggm = Path.Combine(dataDir, "globalgamemanagers");
                string bootConfig = Path.Combine(dataDir, "boot.config");

                if (vr)
                {
                    DeployOpenVrRuntime(dataDir);
                    PatchGlobalGameManagers(ggm);
                    PatchBootConfig(bootConfig, true);
                    Log.LogMessage($"Starting in VR ({reason}).");
                }
                else
                {
                    Restore(ggm);
                    PatchBootConfig(bootConfig, false);
                    Log.LogMessage($"Starting flat ({reason}): original game files in place.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Could not prepare the game for VR: " + e);
            }
        }

        private static bool Decide(StartMode mode, out string reason)
        {
            // the hosted .NET runtime's args don't carry the game's command line
            string line = " " + Marshal.PtrToStringUni(GetCommandLineW()) + " ";
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\s--novr\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { reason = "--novr"; return false; }
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\s--vr\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { reason = "--vr"; return true; }
            switch (mode)
            {
                case StartMode.Always: reason = "StartInVR = Always"; return true;
                case StartMode.Never: reason = "StartInVR = Never"; return false;
                default:
                    bool running = IsRunning("vrserver") || IsRunning("vrmonitor");
                    reason = running ? "SteamVR is running" : "SteamVR is not running";
                    return running;
            }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetCommandLineW();

        private static bool IsRunning(string process)
        {
            try { return System.Diagnostics.Process.GetProcessesByName(process).Length > 0; }
            catch (Exception) { return false; }
        }

        private void PatchGlobalGameManagers(string path)
        {
            byte[] current = File.ReadAllBytes(path);
            var result = GlobalGameManagersPatch.AddDevice(current, "OpenVR");
            if (result.AlreadyPatched)
            {
                Log.LogInfo("globalgamemanagers already lists OpenVR.");
                return;
            }

            // unpatched means it's the game's own file (fresh install or Steam update), back it up
            string backup = path + BackupSuffix;
            File.WriteAllBytes(backup, current);
            WriteAtomically(path, result.Patched);
            Log.LogMessage($"Enabled Unity's OpenVR support in globalgamemanagers (was: [{string.Join(", ", result.DevicesBefore)}]). Original saved as {Path.GetFileName(backup)}.");
        }

        private void Restore(string path)
        {
            string backup = path + BackupSuffix;
            if (!File.Exists(backup)) return;
            var devices = GlobalGameManagersPatch.ReadDevices(File.ReadAllBytes(path));
            if (!devices.Contains("OpenVR")) return; // Steam already restored the original
            WriteAtomically(path, File.ReadAllBytes(backup));
        }

        /// <summary>Unity reads "vr-enabled" from boot.config before the graphics device exists.</summary>
        private void PatchBootConfig(string path, bool enable)
        {
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            string want = enable ? "vr-enabled=1" : "vr-enabled=0";
            string other = enable ? "vr-enabled=0" : "vr-enabled=1";
            if (text.Contains(want)) return;
            // not enabled yet: the game's own file (a flat start only turns it back to 0), so the backup follows game updates
            if (enable) File.Copy(path, path + BackupSuffix, true);
            string patched = text.Contains(other) ? text.Replace(other, want) : text.TrimEnd('\r', '\n') + "\n" + want + "\n";
            WriteAtomically(path, System.Text.Encoding.UTF8.GetBytes(patched));
            Log.LogInfo("boot.config: " + want);
        }

        private void DeployOpenVrRuntime(string dataDir)
        {
            string source = Path.Combine(Paths.PluginPath, "CloudpunkVR", "openvr_api.dll");
            string target = Path.Combine(dataDir, "Plugins", "openvr_api.dll");
            if (!File.Exists(source))
            {
                Log.LogWarning("openvr_api.dll missing from BepInEx/plugins/CloudpunkVR; Unity cannot start OpenVR.");
                return;
            }
            if (File.Exists(target) && Hash(target) == Hash(source)) return;
            File.Copy(source, target, true);
            Log.LogInfo("Copied openvr_api.dll to " + target);
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            string tmp = path + ".cloudpunkvr-tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            using var s = File.OpenRead(path);
            return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
        }
    }
}
