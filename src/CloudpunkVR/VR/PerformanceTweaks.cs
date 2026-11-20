using System;
using System.Collections.Generic;
using AmplifyBloom;
using Bearroll;
using HarmonyLib;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>TrafficDensity: scales the game's own car and crowd limits down.</summary>
    internal static class PerformanceTweaks
    {
        private static AIManager ai;
        private static float nextCrowdPass, nextAiSearch, aiFoundAt;
        // per cluster: game's limit and ours, so a new game value gets scaled once
        private static readonly Dictionary<IntPtr, (int game, int ours)> people = new Dictionary<IntPtr, (int, int)>();
        private static readonly Dictionary<IntPtr, (int game, int ours)> robots = new Dictionary<IntPtr, (int, int)>();
        private static bool warned;

        public static float Density => Mathf.Clamp(VRConfig.TrafficDensity.Value, 0.25f, 1f);

        public static void Update()
        {
            PerfProbe.Update();
            if (Time.unscaledTime < nextCrowdPass) return;
            nextCrowdPass = Time.unscaledTime + 1f;
            try { ScaleCrowds(); }
            catch (Exception e) { Warn("Crowd density not applied: " + e.Message); }
        }

        private static void ScaleCrowds()
        {
            if (ai == null)
            {
                if (Time.unscaledTime < nextAiSearch) return;
                nextAiSearch = Time.unscaledTime + 3f;
                ai = UnityEngine.Object.FindObjectOfType<AIManager>();
                people.Clear();
                robots.Clear();
                if (ai == null) return;
                aiFoundAt = Time.unscaledTime;
            }
            var clusters = ai.clusters;
            if (clusters == null) return;
            float k = Density;
            foreach (var c in clusters)
            {
                if (c == null) continue;
                int p = Scaled(people, c.Pointer, c.maxCharacters, k);
                if (p != c.maxCharacters) c.maxCharacters = p;
                int r = Scaled(robots, c.Pointer, c.maxRobots, k);
                if (r != c.maxRobots) c.maxRobots = r;
                // the game keeps extras over the limit: remove a few per second, once the district has settled
                if (Time.unscaledTime - aiFoundAt < 30f) continue;
                int extraPeople = (c.liveCharacters?.Count ?? 0) - p;
                if (extraPeople > 0) c.RemoveCrowdCharacters(Math.Min(extraPeople, 4), false);
                int extraRobots = (c.liveRobots?.Count ?? 0) - r;
                if (extraRobots > 0) c.RemoveCrowdCharacters(Math.Min(extraRobots, 2), true);
            }
        }

        private static int Scaled(Dictionary<IntPtr, (int game, int ours)> seen, IntPtr key, int current, float k)
        {
            int game = seen.TryGetValue(key, out var e) && e.ours == current ? e.game : current;
            int ours = game <= 0 ? game : Math.Max(1, Mathf.RoundToInt(game * k));
            seen[key] = (game, ours);
            return ours;
        }

        public static string Describe()
        {
            var g = Global.Instance;
            string cars = g == null ? "no game" :
                $"cars in use {g.vehiclesInUse?.Count ?? -1} (game limit {TrafficPatch.GameLimit}, used {TrafficPatch.UsedLimit})";
            int maxP = 0, liveP = 0, maxR = 0, liveR = 0, n = 0;
            if (ai != null && ai.clusters != null)
                foreach (var c in ai.clusters)
                {
                    if (c == null) continue;
                    n++;
                    maxP += c.maxCharacters; maxR += c.maxRobots;
                    liveP += c.liveCharacters?.Count ?? 0; liveR += c.liveRobots?.Count ?? 0;
                }
            string rain = WorldRain.Describe();
            return $"density {Density:0.00}; {cars}; {n} crowd clusters: people {liveP}/{maxP}, robots {liveR}/{maxR}; {rain}";
        }

        private static void Warn(string message)
        {
            if (warned) return;
            warned = true;
            Plugin.L.LogWarning(message);
        }
    }

    /// <summary>
    /// Logs the game's image effects when they may have changed. Dev command "perf" toggles each one and times it
    /// with SteamVR's counters. "perf reset" undoes the toggles, "perf reset all" also the resolution settings.
    /// </summary>
    internal static class PerfProbe
    {
        private static readonly Dictionary<IntPtr, (Behaviour b, bool enabled)> switched = new Dictionary<IntPtr, (Behaviour, bool)>();
        private static ShadowQuality? shadowsBefore;
        private static float? lodBefore;
        private static (HxVolumetricCamera cam, HxVolumetricCamera.Resolution res)? volumetricBefore;
        private static (AmplifyBloomEffect bloom, MainThresholdSizeEnum size)? bloomBefore;
        private static (Camera cam, int mask, bool hdr)? cockpitBefore;
        private static bool gdocOff;
        private static (int max, float scale, bool auto)? settingsBefore;

        private static string measureLabel;
        private static FrameTiming.Snapshot measureStart;
        private static float measureEnd;
        private static int measureMissed, measureLong;
        private static float measureWorst;

        private static float summaryAt = 2f, nextMenuCheck;
        private static int seenScenes = -1;
        private static bool wasInMenu;
        private static string lastSummary;

        public static void Update()
        {
            if (measureLabel != null)
            {
                float ms = Time.unscaledDeltaTime * 1000f;
                if (ms > 1.5f * FrameTiming.BudgetMs) measureMissed++;
                if (ms > 3f * FrameTiming.BudgetMs) { measureLong++; measureWorst = Mathf.Max(measureWorst, ms); }
                if (Time.realtimeSinceStartup >= measureEnd) FinishMeasure();
            }
            float now = Time.unscaledTime;
            if (now >= nextMenuCheck)
            {
                nextMenuCheck = now + 1f;
                // scene load or a closed menu (quality options) may change them
                if (SceneWatch.Changed(ref seenScenes)) SummaryIn(2f);
                bool inMenu = false;
                try { inMenu = Global.Instance != null && Global.Instance.IsInMenu(); } catch (Exception) { }
                if (wasInMenu && !inMenu) SummaryIn(0.5f);
                wasInMenu = inMenu;
            }
            if (summaryAt < 0f || now < summaryAt) return;
            summaryAt = -1f;
            try
            {
                string s = EffectsSummary();
                if (s == null) SummaryIn(2f); // no main camera yet
                else if (s != lastSummary)
                {
                    lastSummary = s;
                    Plugin.L.LogInfo("Image effects: " + s);
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Image effects not readable: " + e.Message);
            }
        }

        private static void SummaryIn(float seconds)
        {
            float at = Time.unscaledTime + seconds;
            if (summaryAt < 0f || at < summaryAt) summaryAt = at;
        }

        /// <summary>Main camera effects and costly quality settings, one line.</summary>
        private static string EffectsSummary()
        {
            var g = Global.Instance;
            var cam = g != null ? g.mainCamera : null;
            if (cam == null) return null;
            var sb = new System.Text.StringBuilder();
            var v = cam.GetComponent<HxVolumetricCamera>();
            sb.Append(v == null ? "no volumetric light" : v.enabled ? $"volumetric light at {v.resolution} resolution, {v.SampleCount} samples" : "volumetric light off");
            var b = cam.GetComponent<AmplifyBloomEffect>();
            if (b != null) sb.Append(b.enabled ? $"; bloom (threshold at {b.MainThresholdSize} size, {b.BloomDownsampleCount} downsamples)" : "; bloom off");
            var extra = cam.GetComponent<PostProcess>();
            if (extra != null) sb.Append(extra.enabled ? "; extra bloom on" : "; extra bloom off");
            if (g.mainPostProcessLayer != null) sb.Append(g.mainPostProcessLayer.enabled ? "; post-processing stack on" : "; post-processing stack off");
            var fxaa = cam.GetComponent<FXAA>();
            if (fxaa != null) sb.Append(fxaa.enabled ? "; FXAA on" : "; FXAA off");
            if (g.qualitySkyboxCamera != null) sb.Append(g.qualitySkyboxCamera.enabled ? "; sky camera on" : "; sky camera off");
            var gdoc = cam.GetComponent<GDOC>();
            if (gdoc != null) sb.Append(gdoc.enabled ? $"; occlusion culling ({gdoc.vrMode})" : "; occlusion culling off");
            int q = QualitySettings.GetQualityLevel();
            var names = QualitySettings.names;
            sb.Append($"; quality '{(q >= 0 && q < names.Length ? names[q] : q.ToString())}', shadows {QualitySettings.shadows} to {QualitySettings.shadowDistance:0} m in {QualitySettings.shadowCascades} cascade(s), " +
                      $"LOD bias {QualitySettings.lodBias:0.##}, {QualitySettings.pixelLightCount} per-pixel lights");
            return sb.ToString();
        }

#if DEV
        private static float Number(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        public static void Run(string[] a)
        {
            string what = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            string arg = a.Length > 2 ? a[2].ToLowerInvariant() : "";
            bool on = arg != "off";
            var g = Global.Instance;
            var cam = g != null ? g.mainCamera : null;
            if (what != "" && what != "measure") SummaryIn(1f);
            switch (what)
            {
                case "":
                    Plugin.L.LogInfo($"perf: {FrameTiming.Describe(FrameTiming.LastSecond, false)} Effects: {EffectsSummary() ?? "no main camera"}.");
                    return;
                case "measure": // perf measure <label> [seconds]
                    measureLabel = a.Length > 2 ? a[2] : "measure";
                    measureEnd = Time.realtimeSinceStartup + (a.Length > 3 ? Number(a[3]) : 8f);
                    measureStart = FrameTiming.Read();
                    measureMissed = measureLong = 0;
                    measureWorst = 0f;
                    return;
                case "reset": // [all]
                    Reset(arg == "all");
                    return;
                case "volumetric": // off|on|full|half|quarter
                    var v = cam != null ? cam.GetComponent<HxVolumetricCamera>() : null;
                    if (v != null && (arg == "full" || arg == "half" || arg == "quarter"))
                    {
                        if (volumetricBefore == null) volumetricBefore = (v, v.resolution);
                        v.resolution = arg == "full" ? HxVolumetricCamera.Resolution.full : arg == "half" ? HxVolumetricCamera.Resolution.half : HxVolumetricCamera.Resolution.quarter;
                        Plugin.L.LogInfo($"perf: volumetric light at {v.resolution} resolution.");
                        return;
                    }
                    Switch(g?.qualityVolumetricCamera, on, "volumetric light");
                    return;
                case "bloom": // off|on|full|half|quarter (threshold size)
                    var b = cam != null ? cam.GetComponent<AmplifyBloomEffect>() : null;
                    if (b != null && (arg == "full" || arg == "half" || arg == "quarter"))
                    {
                        if (bloomBefore == null) bloomBefore = (b, b.MainThresholdSize);
                        b.MainThresholdSize = arg == "full" ? MainThresholdSizeEnum.Full : arg == "half" ? MainThresholdSizeEnum.Half : MainThresholdSizeEnum.Quarter;
                        Plugin.L.LogInfo($"perf: bloom threshold at {b.MainThresholdSize} size.");
                        return;
                    }
                    Switch(g?.qualityBloom, on, "bloom");
                    return;
                case "extrapost": Switch(g?.qualityExtraPostProcess, on, "extra bloom"); return;
                case "fxaa": Switch(g?.qualityFXAA, on, "FXAA"); return;
                case "sky": Switch(g?.qualitySkyboxCamera, on, "sky camera"); return;
                case "post": Switch(g?.mainPostProcessLayer, on, "post-processing stack"); return;
                case "gdoc":
                    gdocOff = !on;
                    if (on && VRConfig.OcclusionCulling.Value) GDOC.Enable(); else GDOC.Disable();
                    Plugin.L.LogInfo($"perf: occlusion culling {(on ? "as set" : "off")}.");
                    return;
                case "overlay": Switch(CameraDriver.OverlayCamera, on, "overlay camera"); return;
                case "ui": Switch(CameraDriver.UiCamera, on, "UI camera"); return;
                case "cockpit": // nohdr|empty|on
                    Cockpit(arg);
                    return;
                case "shadows":
                    if (shadowsBefore == null) shadowsBefore = QualitySettings.shadows;
                    QualitySettings.shadows = on ? shadowsBefore.Value : ShadowQuality.Disable;
                    Plugin.L.LogInfo($"perf: shadows {QualitySettings.shadows}.");
                    return;
                case "lod": // lod <bias>|on
                    if (lodBefore == null) lodBefore = QualitySettings.lodBias;
                    QualitySettings.lodBias = arg == "on" || arg == "" ? lodBefore.Value : Number(arg);
                    Plugin.L.LogInfo($"perf: LOD bias {QualitySettings.lodBias:0.##}.");
                    return;
                case "fovwarnings": // on: let Unity's FOV warnings through, to measure them
                    NoFovWarnings.Enabled = !on;
                    Plugin.L.LogInfo($"perf: field-of-view warnings {(on ? "on, as in the game" : "off (the refused call is skipped)")}.");
                    return;
                case "res": // MaxEyeResolution until perf reset all
                    KeepSettings();
                    VRConfig.MaxEyeResolution.Value = (int)Number(arg);
                    return;
                case "scale":
                    KeepSettings();
                    VRConfig.RenderScale.Value = Number(arg);
                    return;
                case "auto":
                    KeepSettings();
                    VRConfig.AutoRenderScale.Value = on;
                    return;
                case "supersample": // x SteamVR's size per side, to load the GPU
                    RenderResolution.Supersample = Mathf.Clamp(Number(arg), 1f, 3f);
                    Plugin.L.LogInfo($"perf: eye images at {RenderResolution.Supersample:0.##} times SteamVR's size per side (then MaxEyeResolution, RenderScale, auto).");
                    return;
                default:
                    Plugin.L.LogWarning("perf: unknown switch " + what);
                    return;
            }
        }

        private static void Switch(Behaviour b, bool on, string name)
        {
            if (b == null) { Plugin.L.LogWarning($"perf: no {name} here."); return; }
            if (!switched.ContainsKey(b.Pointer)) switched[b.Pointer] = (b, b.enabled);
            b.enabled = on && switched[b.Pointer].enabled;
            Plugin.L.LogInfo($"perf: {name} {(b.enabled ? "on" : "off")}.");
        }

        private static void Cockpit(string how)
        {
            var c = Global.Instance?.vehicleInterior?.cameraCockpit;
            if (c == null) { Plugin.L.LogWarning("perf: no cockpit camera here."); return; }
            if (cockpitBefore == null) cockpitBefore = (c, Film.CleanFrame.GameMask(c), c.allowHDR); // game's mask, minus clean-frame edits
            var before = cockpitBefore.Value;
            c.allowHDR = how != "nohdr" && before.hdr;
            c.cullingMask = how == "empty" ? 0 : before.mask;
            Plugin.L.LogInfo($"perf: cockpit camera HDR {c.allowHDR}, mask 0x{c.cullingMask:X8}.");
        }

        /// <summary>Saves the resolution settings before the first res/scale/auto, for "reset all".</summary>
        private static void KeepSettings()
        {
            if (settingsBefore == null)
                settingsBefore = (VRConfig.MaxEyeResolution.Value, VRConfig.RenderScale.Value, VRConfig.AutoRenderScale.Value);
        }

        private static void Reset(bool settingsToo)
        {
            if (settingsToo)
            {
                if (settingsBefore != null)
                {
                    VRConfig.MaxEyeResolution.Value = settingsBefore.Value.max;
                    VRConfig.RenderScale.Value = settingsBefore.Value.scale;
                    VRConfig.AutoRenderScale.Value = settingsBefore.Value.auto;
                    settingsBefore = null;
                }
                RenderResolution.Supersample = 1f;
            }
            foreach (var kv in switched)
                if (kv.Value.b != null) kv.Value.b.enabled = kv.Value.enabled;
            switched.Clear();
            if (shadowsBefore != null) QualitySettings.shadows = shadowsBefore.Value;
            if (lodBefore != null) QualitySettings.lodBias = lodBefore.Value;
            if (volumetricBefore != null && volumetricBefore.Value.cam != null) volumetricBefore.Value.cam.resolution = volumetricBefore.Value.res;
            if (bloomBefore != null && bloomBefore.Value.bloom != null) bloomBefore.Value.bloom.MainThresholdSize = bloomBefore.Value.size;
            if (cockpitBefore != null && cockpitBefore.Value.cam != null)
            {
                cockpitBefore.Value.cam.cullingMask = cockpitBefore.Value.mask;
                cockpitBefore.Value.cam.allowHDR = cockpitBefore.Value.hdr;
            }
            shadowsBefore = null;
            lodBefore = null;
            volumetricBefore = null;
            bloomBefore = null;
            cockpitBefore = null;
            NoFovWarnings.Enabled = true;
            if (gdocOff && VRConfig.OcclusionCulling.Value) GDOC.Enable();
            gdocOff = false;
            Plugin.L.LogInfo(settingsToo ? "perf: the game's image and the resolution settings back as they were." : "perf: the game's image back as it was.");
        }
#endif

        private static void FinishMeasure()
        {
            var s = FrameTiming.Between(measureStart, FrameTiming.Read());
            Plugin.L.LogInfo($"perf {measureLabel}: " + (s.Valid
                ? $"{s.Fps:0.0} fps, GPU {s.GpuMs:0.00} ms, CPU {s.CpuMs:0.00} ms per frame, reprojected {s.Share(s.Reprojected):P0}, timed out {s.Share(s.TimedOut):P0}, " +
                  $"{measureMissed} frame(s) over 1.5 refreshes, {measureLong} over 3{(measureLong > 0 ? $" (worst {measureWorst:0} ms)" : "")} over {s.Seconds:0.0} s; {RenderResolution.Describe()}."
                : "no frame counters from SteamVR."));
            measureLabel = null;
        }
    }

    /// <summary>
    /// Unity refuses FOV changes on stereo cameras and logs a warning each time. Cinemachine does it every frame
    /// on two cameras, so skip the call: no effect, just no log spam.
    /// </summary>
    [HarmonyPatch(typeof(Camera), nameof(Camera.fieldOfView), MethodType.Setter)]
    internal static class NoFovWarnings
    {
        internal static bool Enabled = true;

        private static bool Prefix(Camera __instance) => !(Enabled && __instance != null && __instance.stereoEnabled);
    }

    /// <summary>Scales the car limit the game starts traffic with.</summary>
    [HarmonyPatch(typeof(Global), nameof(Global.StartUpdatingVehicles))]
    internal static class TrafficPatch
    {
        internal static int GameLimit = -1, UsedLimit = -1;

        private static void Prefix(ref int maxVehicles)
        {
            GameLimit = maxVehicles;
            float k = PerformanceTweaks.Density;
            if (k < 0.999f && maxVehicles > 0) maxVehicles = Math.Max(1, Mathf.RoundToInt(maxVehicles * k));
            UsedLimit = maxVehicles;
            if (UsedLimit != GameLimit) Plugin.L.LogInfo($"Traffic: {UsedLimit} cars instead of {GameLimit} (TrafficDensity {k:0.00}).");
        }
    }
}
