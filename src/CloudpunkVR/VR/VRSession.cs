using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace CloudpunkVR.VR
{
    /// <summary>Unity's OpenVR state plus direct SteamVR calls for what Unity 2018 doesn't expose.</summary>
    internal static class VRSession
    {
        public static bool Active =>
            XRSettings.enabled && XRSettings.isDeviceActive && XRSettings.loadedDeviceName == "OpenVR";

        /// <summary>Head pose in tracking space, sampled once per frame before rendering.</summary>
        public static Vector3 HeadPosition;
        public static Quaternion HeadRotation = Quaternion.identity;
        public static bool HeadValid;

        // dev commands only (headlook, mirrors look close, spectator shake), null in play
        public static Quaternion? TestLook;
        public static Vector3? TestOffset;
        public static Quaternion? TestShake;

        private static bool configured;
        private static float lastFade = -1f;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        /// <summary>Binds our OpenVR calls to the openvr_api.dll Unity already loaded. A second copy is "not initialized".</summary>
        public static void InstallNativeResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(VRSession).Assembly, (name, asm, path) =>
            {
                if (name != "openvr_api") return IntPtr.Zero;
                IntPtr h = GetModuleHandle("openvr_api.dll");
                return h; // zero falls back to the default search
            });
        }

        public static void ConfigureOnce()
        {
            if (configured || !Active) return;
            configured = true;
            QualitySettings.vSyncCount = 0; // the headset paces frames, not the monitor
            // hidden-area mesh: skip the corners the lenses never show
            try
            {
                if (!XRSettings.useOcclusionMesh)
                {
                    XRSettings.useOcclusionMesh = true;
                    Plugin.L.LogInfo("Hidden-area mesh turned on: the corners the lenses never show are not drawn.");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Hidden-area mesh state unknown: " + e.Message); }
            // Unity warns every frame when Cinemachine sets the FOV in VR. Stack traces for those cost frame time.
            try
            {
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            }
            catch (System.Exception e) { Plugin.L.LogWarning("Stack traces of warnings stay on: " + e.Message); }
            Plugin.L.LogMessage($"VR ready: {XRDevice.model} at {XRDevice.refreshRate:F0} Hz, eye texture " +
                                $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} (scale {XRSettings.eyeTextureResolutionScale}), " +
                                $"tracking {XRDevice.GetTrackingSpaceType()}.");
        }

        private static float nextPacingCheck;

        /// <summary>Also undoes the game's V-Sync and frame cap, which would hold the headset to the monitor's 60 Hz.</summary>
        public static void KeepFramePacing()
        {
            if (FrameTiming.Tick()) RenderResolution.Judge(FrameTiming.LastSecond, FrameTiming.LastSecondWasPlay);
            RenderResolution.Update();
            if (Time.unscaledTime < nextPacingCheck) return;
            nextPacingCheck = Time.unscaledTime + 1f;
            if (QualitySettings.vSyncCount != 0)
            {
                Plugin.L.LogInfo($"V-Sync was {QualitySettings.vSyncCount}; turned off for VR.");
                QualitySettings.vSyncCount = 0;
            }
            if (Application.targetFrameRate > 0 && Application.targetFrameRate < Mathf.RoundToInt(XRDevice.refreshRate))
            {
                Plugin.L.LogInfo($"Frame cap was {Application.targetFrameRate}; removed for VR.");
                Application.targetFrameRate = -1;
            }
        }

        public static void SampleHead()
        {
            HeadPosition = InputTracking.GetLocalPosition(XRNode.Head);
            HeadRotation = InputTracking.GetLocalRotation(XRNode.Head);
            HeadValid = HeadRotation != new Quaternion(0, 0, 0, 0) && (HeadRotation.w != 0 || HeadRotation.x != 0 || HeadRotation.y != 0 || HeadRotation.z != 0);
            if (!HeadValid) HeadRotation = Quaternion.identity;
            else
            {
                if (TestLook.HasValue) HeadRotation *= TestLook.Value;
                if (TestShake.HasValue) HeadRotation *= TestShake.Value;
                if (TestOffset.HasValue) HeadPosition += TestOffset.Value;
            }
        }

        /// <summary>Compositor fade, mirrors the game's black-screen transitions.</summary>
        public static void SetFade(float alpha, float seconds = 0.15f)
        {
            alpha = Mathf.Clamp01(alpha);
            if (Mathf.Abs(alpha - lastFade) < 0.02f) return;
            lastFade = alpha;
            try { OpenVR.Compositor?.FadeToColor(seconds, 0f, 0f, 0f, alpha, false); }
            catch (Exception e) { Plugin.L.LogWarning("Compositor fade failed: " + e.Message); }
        }

        public static string FrameStats() => FrameTiming.Short();

        public static ETrackingUniverseOrigin TrackingUniverse
        {
            get
            {
                try { return OpenVR.Compositor != null ? OpenVR.Compositor.GetTrackingSpace() : ETrackingUniverseOrigin.TrackingUniverseSeated; }
                catch { return ETrackingUniverseOrigin.TrackingUniverseSeated; }
            }
        }

        /// <summary>OpenVR (right-handed) pose to Unity (left-handed).</summary>
        public static void ToUnity(HmdMatrix34_t p, out Vector3 pos, out Quaternion rot)
        {
            var m = Matrix4x4.identity;
            m.m00 = p.m0; m.m01 = p.m1; m.m02 = -p.m2; m.m03 = p.m3;
            m.m10 = p.m4; m.m11 = p.m5; m.m12 = -p.m6; m.m13 = p.m7;
            m.m20 = -p.m8; m.m21 = -p.m9; m.m22 = p.m10; m.m23 = -p.m11;
            pos = new Vector3(m.m03, m.m13, m.m23);
            Vector3 fwd = new Vector3(m.m02, m.m12, m.m22);
            Vector3 up = new Vector3(m.m01, m.m11, m.m21);
            rot = fwd.sqrMagnitude > 0 && up.sqrMagnitude > 0 ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
        }
    }
}
