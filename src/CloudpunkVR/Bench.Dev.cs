#if DEV
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace CloudpunkVR
{
    public partial class Plugin
    {
        static partial void SetUpBench() => Bench.Install();
    }

    /// <summary>
    /// Test runs unlock no Steam achievements (`goto` enters districts ahead of the story). A bench run has a
    /// BepInEx\config\CloudpunkVR.bench marker under 5 minutes old (a launch through Steam passes no environment), or
    /// CLOUDPUNKVR_BENCH=1. Then, at startup only, prefixes skip SteamManager.UnlockAchievement and
    /// UnlockAchievementDelayed (every unlock in the game goes through them) and SteamUserStats.SetAchievement.
    /// No marker, nothing patched.
    /// </summary>
    internal static class Bench
    {
        public const string MarkerName = "CloudpunkVR.bench";
        public const string EnvironmentVariable = "CLOUDPUNKVR_BENCH";
        // a leftover marker from a crashed run must not block a player's achievements (story ones are once per playthrough)
        private const double MarkerLifetimeMinutes = 5;
        private const int HookCount = 3;

        public static bool Active { get; private set; }
        /// <summary>All three hooks on: only then `goto` runs without `force`.</summary>
        public static bool Complete { get; private set; }
        private static string how = "";
        private static readonly List<string> hooked = new List<string>();
        private static readonly List<string> blocked = new List<string>();
        private static int blockedCount;

        public static void Install()
        {
            how = Reason();
            if (how == null) return;
            try { HookAll(); }
            catch (Exception e) { Plugin.L.LogError($"Bench run ({how}): achievement guard NOT on ({e.GetBaseException().Message}): an achievement can still unlock."); }
        }

        // separate method so the Steam types only get loaded on a bench run
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HookAll()
        {
            var harmony = new Harmony(Plugin.Guid + ".bench");
            Hook(harmony, AccessTools.Method(typeof(SteamManager), nameof(SteamManager.UnlockAchievement), new[] { typeof(int) }), nameof(SkipUnlock), "SteamManager.UnlockAchievement");
            Hook(harmony, AccessTools.Method(typeof(SteamManager), nameof(SteamManager.UnlockAchievementDelayed), new[] { typeof(int), typeof(float) }), nameof(SkipDelayed), "SteamManager.UnlockAchievementDelayed");
            Hook(harmony, AccessTools.Method(typeof(Steamworks.SteamUserStats), nameof(Steamworks.SteamUserStats.SetAchievement), new[] { typeof(string) }), nameof(SkipSet), "SteamUserStats.SetAchievement");
            Active = hooked.Count > 0;
            Complete = hooked.Count == HookCount;
            if (hooked.Count == HookCount) Plugin.L.LogMessage($"Bench run ({how}): achievements blocked, {HookCount} of {HookCount} hooks on ({string.Join(", ", hooked)}).");
            else Plugin.L.LogError($"Bench run ({how}): achievement guard INCOMPLETE, {hooked.Count} of {HookCount} hooks on ({string.Join(", ", hooked)}): an achievement can still unlock.");
        }

        /// <summary>`bench`: bench run or not, and what got blocked.</summary>
        public static string Describe()
        {
            if (how == null || how.Length == 0) return $"not a bench run (no {MarkerName} marker, no {EnvironmentVariable}=1): achievements are not blocked.";
            return $"achievement guard {(Active ? "on" : "OFF")} ({how}), {hooked.Count} of {HookCount} hooks; {blockedCount} achievement call(s) blocked" +
                   (blocked.Count > 0 ? ": " + string.Join("; ", blocked) : "") + ".";
        }

        /// <summary>Why this run is a bench run, or null.</summary>
        private static string Reason()
        {
            if (Environment.GetEnvironmentVariable(EnvironmentVariable) == "1") return EnvironmentVariable + "=1";
            string marker = Path.Combine(BepInEx.Paths.ConfigPath, MarkerName);
            try
            {
                if (!File.Exists(marker)) return null;
                double minutes = (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker)).TotalMinutes;
                if (minutes <= MarkerLifetimeMinutes) return $"{MarkerName} written {minutes:0.0} min ago";
                Plugin.L.LogWarning($"A bench marker {minutes:0} min old was left behind ({marker}): ignored, achievements not blocked. Delete it.");
            }
            catch (Exception e) { Plugin.L.LogWarning($"Bench marker unreadable ({e.Message}): achievements not blocked."); }
            return null;
        }

        private static void Hook(Harmony harmony, System.Reflection.MethodInfo original, string prefix, string label)
        {
            try
            {
                if (original == null) throw new MissingMethodException(label);
                harmony.Patch(original, prefix: new HarmonyMethod(AccessTools.Method(typeof(Bench), prefix)));
                hooked.Add(label);
            }
            catch (Exception e) { Plugin.L.LogWarning($"Bench: {label} not hooked: {e.GetBaseException().Message}"); }
        }

        // Prefixes replace the game's method and never throw (an exception through the native call would leave the
        // caller half done). SetAchievement returns false.

        private static bool SkipUnlock(int __0)
        {
            Blocked(Name(__0), "SteamManager.UnlockAchievement");
            return false;
        }

        private static bool SkipDelayed(int __0, float __1)
        {
            Blocked(Name(__0), $"SteamManager.UnlockAchievementDelayed, due in {__1.ToString("0.#", CultureInfo.InvariantCulture)} s");
            return false;
        }

        private static bool SkipSet(string __0, ref bool __result)
        {
            __result = false;
            Blocked("'" + __0 + "'", "SteamUserStats.SetAchievement");
            return false;
        }

        private static void Blocked(string what, string where)
        {
            try
            {
                blockedCount++;
                if (blocked.Count < 40) blocked.Add($"{what} ({where})");
                Plugin.L.LogInfo($"achievement {what} blocked (bench): {where}.");
            }
            catch (Exception) { /* never into the game's call */ }
        }

        /// <summary>The game's number and Steam's name, from SteamManager's list like UnlockAchievement (ids from 1).</summary>
        private static string Name(int id)
        {
            string n = id.ToString(CultureInfo.InvariantCulture);
            try
            {
                var sm = SteamManager.s_instance;
                var list = sm != null ? sm.m_Achievements : null;
                if (list != null && id >= 1 && id <= list.Length && list[id - 1] != null) n += $" ({list[id - 1].apiName})";
            }
            catch (Exception) { /* the number alone */ }
            return n;
        }
    }
}
#endif
