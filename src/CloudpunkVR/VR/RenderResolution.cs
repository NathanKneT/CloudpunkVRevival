using System;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Eye resolution. SteamVR's Auto size can be over 4x the panel's pixels on a fast card, far too heavy for this game.
    /// renderViewportScale is refused on the deferred path, so eyeTextureResolutionScale is used. It re-creates the
    /// eye images (a hitch), so it changes rarely, and a watchdog reverts if frames stop reaching the headset.
    /// </summary>
    internal static class RenderResolution
    {
        public static int Changes { get; private set; }

        /// <summary>Dev override: supersample per side, above 1 to load the GPU.</summary>
        public static float Supersample = 1f;

        private static readonly ScaleController auto = new ScaleController();

        private static uint recW, recH;          // what SteamVR asks for in play
        private static uint candW, candH;        // new size seen once, needs a second look
        private static float candSince, nextQuery, dashChangedAt = -100f;
        private static bool dashOpen;

        private static float applied = -1f;      // the eyeTextureResolutionScale this mod set
        private static float lastGood = 1f;      // last scale that kept frames flowing
        private static float appliedAt = -100f;
        private static string reason;
        private static bool locked;

        private static float settleAt = -1f;
        private static string settleFrom, settleWhy;

        private static bool watching;
        private static FrameTiming.Snapshot watchStart;
        private static float watchSince, watchChangedAt, watchSeconds, watchBefore, nextWatchCheck;
        // end of the last load or passage: the seconds after aren't judged
        private static float loadedAt = -100f;

        private static int MaxEye
        {
            get
            {
                int v = VRConfig.MaxEyeResolution.Value;
                return v <= 0 ? 0 : Math.Max(v, 800);
            }
        }

        private static float UserScale => Mathf.Clamp(VRConfig.RenderScale.Value, 0.5f, 1f);
        private static bool Auto => VRConfig.AutoRenderScale.Value;

        /// <summary>Share of SteamVR's size that MaxEyeResolution allows (times Supersample).</summary>
        private static float CapScale
        {
            get
            {
                float ss = Mathf.Max(1f, Supersample);
                uint big = Math.Max(recW, recH);
                int max = MaxEye;
                return big > 0 && max > 0 && big * ss > max ? max / (float)big : ss;
            }
        }

        private static float AutoScale => Auto ? ScaleController.Levels[auto.Level] : 1f;
        private static float Target => Mathf.Clamp(CapScale * UserScale * AutoScale, 0.2f, Mathf.Max(1f, Supersample));

        /// <summary>Every frame, from the VR update.</summary>
        public static void Update()
        {
            if (!VRSession.Active) return;
            float now = Time.unscaledTime;
            if (now >= nextQuery)
            {
                nextQuery = now + 0.5f;
                QueryRecommended(now);
            }
            if (!Auto && auto.Level != 0)
            {
                auto.Reset();
                reason = "AutoRenderScale off";
            }
            if (Loading) loadedAt = now;
            Watch();
            if (settleAt > 0f && now >= settleAt) LogSettled();
            if (locked || recW == 0) return;

            // step up only while the view is hidden, so the hitch isn't seen
            if (auto.UpPending && Auto && ViewHidden() && auto.TakeUp(now))
                reason = $"AutoRenderScale {ScaleController.Levels[auto.Level] * 100f:0}% per side while the view was hidden: {auto.Why}";

            float target = Target;
            if (applied < 0f)
            {
                if (UserScale < 0.999f)
                    Plugin.L.LogInfo($"RenderScale {UserScale:0.00} applies on top of MaxEyeResolution: each eye is drawn at {UserScale * 100f:0}% of that size per side. " +
                                     "It had no effect in earlier builds; set it back to 1 for full sharpness.");
                Apply(target, "at start");
                return;
            }
            if (Mathf.Abs(target - applied) < 0.005f || now - appliedAt < 2f) return;
            Apply(target, reason ?? "settings changed");
        }

        /// <summary>Black screen, a load, a menu, the map or the dashboard in front of the game.</summary>
        private static bool ViewHidden()
        {
            if (TransitionGuard.Active || CameraDriver.Mode == ViewMode.Menu || FrameTiming.DashboardVisible) return true;
            try
            {
                var g = Global.Instance;
                return Global.BlackScreenIsOn || Global.FullMapOpen || (g != null && (g.IsInMenu() || g.HasBigWindowOpen()));
            }
            catch (Exception) { return false; }
        }

        /// <summary>A new size counts when seen twice, 1 s apart, not just after a dashboard toggle (it halves the size).</summary>
        private static void QueryRecommended(float now)
        {
            bool dash = FrameTiming.QueryDashboard();
            if (dash != dashOpen)
            {
                dashOpen = dash;
                dashChangedAt = now;
            }
            uint w = 0, h = 0;
            try { OpenVR.System?.GetRecommendedRenderTargetSize(ref w, ref h); }
            catch (Exception) { }
            if (w == 0 || h == 0)
            {
                if (recW != 0) return;
                // no answer from SteamVR: work back from Unity's eye image
                float s = Mathf.Max(0.05f, XRSettings.eyeTextureResolutionScale);
                w = (uint)Mathf.RoundToInt(XRSettings.eyeTextureWidth / s);
                h = (uint)Mathf.RoundToInt(XRSettings.eyeTextureHeight / s);
                if (w == 0 || h == 0) return;
            }
            if (recW == 0)
            {
                recW = w;
                recH = h;
                Plugin.L.LogInfo($"SteamVR asks for {w}x{h} per eye (MaxEyeResolution {(MaxEye > 0 ? MaxEye.ToString() : "off")}, RenderScale {UserScale:0.00}" +
                                 (dash ? ", its dashboard is open" : "") + ").");
                return;
            }
            if (dash || (w == recW && h == recH))
            {
                candW = candH = 0;
                return;
            }
            if (now - dashChangedAt < 3f) return;
            if (w != candW || h != candH)
            {
                candW = w;
                candH = h;
                candSince = now;
                return;
            }
            if (now - candSince < 1f) return;
            recW = w;
            recH = h;
            candW = candH = 0;
            Plugin.L.LogInfo($"SteamVR now asks for {w}x{h} per eye.");
            reason = "SteamVR's size changed";
        }

        private static void Apply(float scale, string why)
        {
            string before = EyeSize();
            try
            {
                float current = XRSettings.eyeTextureResolutionScale;
                if (applied < 0f) lastGood = current;
                // Unity may re-create the eye images even for the same value
                if (Mathf.Abs(current - scale) < 0.001f)
                {
                    applied = scale;
                    appliedAt = Time.unscaledTime;
                    reason = null;
                    return;
                }
                XRSettings.eyeTextureResolutionScale = scale;
            }
            catch (Exception e)
            {
                locked = true;
                Plugin.L.LogWarning("The eye resolution cannot be changed in this game build: " + e.Message);
                return;
            }
            applied = scale;
            appliedAt = Time.unscaledTime;
            reason = null;
            Changes++;
            settleFrom = before;
            settleWhy = why;
            settleAt = Time.unscaledTime + 1.5f;
            StartWatch();
        }

        /// <summary>Logs the size Unity actually made, a moment after a change.</summary>
        private static void LogSettled()
        {
            settleAt = -1f;
            if (EyeSize() == settleFrom)
                Plugin.L.LogWarning($"Render resolution: Unity kept the eye images at {settleFrom} after scale {applied:0.00} ({settleWhy}).");
            Plugin.L.LogInfo($"Render resolution: eye {EyeSize()} (was {settleFrom}), {settleWhy}; scale {applied:0.00} = " +
                             $"MaxEyeResolution {CapScale:0.00} x RenderScale {UserScale:0.00} x auto {AutoScale:0.00} of SteamVR's {recW}x{recH}.");
        }

        /// <summary>A load frame (0.5 s or more) or a district passage.</summary>
        private static bool Loading => Time.unscaledDeltaTime >= 0.5f || TransitionGuard.Active;

        private static void StartWatch()
        {
            watching = true;
            watchSince = watchChangedAt = Time.realtimeSinceStartup;
            watchStart = default;
            watchSeconds = 0f;
            nextWatchCheck = 0f;
            var before = FrameTiming.LastSecond;
            watchBefore = before.Valid ? StaleShare(before) : -1f;
        }

        /// <summary>Share of refreshes that reused an old frame.</summary>
        private static float StaleShare(FrameTiming.Span s) => Mathf.Min(1f, s.Share(s.Reprojected + s.Dropped));

        /// <summary>
        /// Watchdog after a change: frames must keep reaching SteamVR, and stale refreshes must not jump to nearly all.
        /// 1 s of normal frames passes, 4 s failing reverts. Loads restart it.
        /// No counters for 10 s, or no verdict after 2 min: give up and keep the change.
        /// </summary>
        private static void Watch()
        {
            if (!watching) return;
            float real = Time.realtimeSinceStartup;
            if (Loading)
            {
                watchStart = default;
                watchSince = real;
            }
            else if (watchStart.Valid) watchSeconds += Time.unscaledDeltaTime;
            if (real < nextWatchCheck) return;
            nextWatchCheck = real + 0.25f;
            if (real - watchChangedAt > 120f)
            {
                GiveUp("loads for two minutes after it");
                return;
            }
            float elapsed = real - watchSince;
            var now = FrameTiming.Read();
            if (!now.Valid || !watchStart.Valid)
            {
                if (now.Valid)
                {
                    watchStart = now;
                    watchSeconds = 0f;
                }
                else if (elapsed > 10f) GiveUp("SteamVR gave no frame counters to check it with");
                return;
            }
            if (watchSeconds < 1f)
            {
                if (elapsed > 10f) GiveUp("the game was loading the whole time");
                return;
            }
            var d = FrameTiming.Between(watchStart, now);
            float stale = StaleShare(d);
            bool flowing = d.Submits >= 5;
            bool shown = watchBefore >= 0.9f || stale < 0.97f;
            if (flowing && shown)
            {
                watching = false;
                lastGood = applied;
                return;
            }
            if (watchSeconds < 4f)
            {
                if (elapsed > 10f) GiveUp("the game was loading most of the time");
                return;
            }
            watching = false;
            locked = true;
            try { XRSettings.eyeTextureResolutionScale = lastGood; } catch (Exception) { }
            applied = lastGood;
            Plugin.L.LogWarning($"After the eye resolution changed, {(flowing ? $"{stale:P0} of the headset's refreshes showed an old frame again (before: {(watchBefore < 0f ? "unknown" : watchBefore.ToString("P0"))})" : "no frame reached SteamVR")} " +
                                $"for 4 s: back to scale {lastGood:0.00}, and no more changes until the next launch. Please report it with this log; " +
                                "MaxEyeResolution = 0, AutoRenderScale = false and RenderScale = 1 leave the eye resolution alone.");
        }

        private static void GiveUp(string why)
        {
            watching = false;
            lastGood = applied;
            Plugin.L.LogInfo($"Render resolution: the change could not be checked ({why}); it stays.");
        }

        /// <summary>AutoRenderScale's decision, once a second.</summary>
        public static void Judge(FrameTiming.Span s, bool play)
        {
            float now = Time.unscaledTime;
            if (!Auto || locked || applied < 0f || watching) return;
            // steady play only: skip a few seconds after a resize, load, passage or dashboard toggle (hitches)
            if (!play || !s.Valid || s.GpuMs <= 0f || now - Mathf.Max(appliedAt, loadedAt) < 3f
                || Time.realtimeSinceStartup - FrameTiming.DashboardChangedAt < 3f)
            {
                auto.Pause();
                return;
            }
            float hz = FrameTiming.Hz;
            switch (auto.Judge(now, s.GpuMs, hz))
            {
                case ScaleController.Verdict.Down:
                    reason = $"AutoRenderScale {ScaleController.Levels[auto.Level] * 100f:0}% per side: {auto.Why}";
                    break;
                case ScaleController.Verdict.FloorReached:
                    Plugin.L.LogInfo($"Render resolution: AutoRenderScale is at its lowest step and the graphics card still misses the refresh ({auto.Why}). " +
                                     (hz > 100f
                                         ? "Set the headset to 90 Hz in SteamVR: a steady 90 is smoother than frames missed at 120. Or lower MaxEyeResolution."
                                         : "Lower MaxEyeResolution or the game's quality options."));
                    break;
            }
        }

        private static string EyeSize()
        {
            try { return $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}"; }
            catch (Exception) { return "?"; }
        }

        /// <summary>For the log and the dev command.</summary>
        public static string Describe()
        {
            string s = $"eye {EyeSize()} of SteamVR's {recW}x{recH} (scale {(applied < 0f ? 1f : applied):0.00}";
            if (Auto) s += $", auto {AutoScale * 100f:0}%" + (auto.UpPending ? " (step up waiting for a menu or a black screen)" : "");
            if (Supersample > 1f) s += $", supersample x{Supersample:0.##}";
            if (locked) s += ", changes stopped";
            return s + ")";
        }
    }
}
