#if DEV
using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// For test renders: with --keep-screen-size the window stays at the size the command line asked, even when the
    /// game applies its saved resolution after Unity's (set back a few times at most). Dev command `screen`.
    /// </summary>
    internal static partial class WindowSize
    {
        private const int MaxCorrections = 10;
        private static bool keep;
        private static int corrections;

        static partial void ReadKeep(string commandLine)
        {
            keep = Regex.IsMatch(commandLine, @"\s--keep-screen-size\s", RegexOptions.IgnoreCase) && AskedWidth > 0 && AskedHeight > 0;
        }

        static partial void Keep(int w, int h, bool full)
        {
            if (!keep) return;
            bool wantFull = AskedFullScreen ?? full;
            if (w == AskedWidth && h == AskedHeight && full == wantFull) return;
            if (corrections >= MaxCorrections)
            {
                if (corrections++ == MaxCorrections)
                    Plugin.L.LogWarning($"Window: still {w}x{h} {(full ? "full screen" : "windowed")} after {MaxCorrections} tries at {AskedWidth}x{AskedHeight}: left as it is; frames are saved at {w}x{h}.");
                return;
            }
            corrections++;
            Plugin.L.LogInfo($"Window: {w}x{h} {(full ? "full screen" : "windowed")}, not the {AskedWidth}x{AskedHeight} {(wantFull ? "full screen" : "windowed")} the command line asks " +
                             $"(--keep-screen-size): set back, try {corrections} of {MaxCorrections}.");
            Screen.SetResolution(AskedWidth, AskedHeight, wantFull);
        }

        static partial void KeepDescribe(ref string note)
        {
            if (keep) note = $" (kept: --keep-screen-size, {corrections} correction(s))";
        }

        /// <summary>Developer command `screen`: the window now.</summary>
        public static string Command(string[] a) => Describe();
    }
}
#endif
