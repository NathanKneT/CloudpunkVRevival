using System;
using Cinemachine;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>Comfort tweaks to the game's effects: Cinemachine camera wobble and full-screen warps.</summary>
    internal static class ComfortTweaks
    {
        private static bool effectsWarned;

        /// <summary>Scales drink/boost screen effects. Runs just before rendering, after the game set them.</summary>
        public static void ScaleScreenEffects()
        {
            var level = VRConfig.ScreenEffects.Value;
            if (level == ScreenEffectLevel.Full) return;
            float k = level == ScreenEffectLevel.Off ? 0f : 0.25f;
            var g = Global.Instance;
            if (g == null) return;
            try
            {
                if (g.alcoholDoubleVision != null) Scale(g.alcoholDoubleVision.intensity, k);
                if (g.alcoholRipples != null) Scale(g.alcoholRipples.strength, k);
                if (g.speedLines != null) Scale(g.speedLines.intensity, k);
            }
            catch (Exception e)
            {
                if (!effectsWarned) { effectsWarned = true; Plugin.L.LogWarning("Screen effects could not be scaled: " + e.Message); }
            }
        }

        private static readonly System.Collections.Generic.Dictionary<IntPtr, float> lastWritten = new System.Collections.Generic.Dictionary<IntPtr, float>();

        private static void Scale(UnityEngine.Rendering.PostProcessing.FloatParameter p, float k)
        {
            if (p == null) return;
            float v = p.value;
            // still our last value: the game didn't touch it, don't scale twice
            if (lastWritten.TryGetValue(p.Pointer, out float w) && Mathf.Approximately(v, w)) return;
            float scaled = v * k;
            p.value = scaled;
            lastWritten[p.Pointer] = scaled;
        }

        private static float nextCheck;
        private static int seenScenes = -1;
        private static bool cameraListBroken;

        // noise and impulse parts per active virtual camera, cached
        private static readonly System.Collections.Generic.Dictionary<IntPtr, (CinemachineBasicMultiChannelPerlin[] noise, CinemachineImpulseListener impulse)> parts =
            new System.Collections.Generic.Dictionary<IntPtr, (CinemachineBasicMultiChannelPerlin[], CinemachineImpulseListener)>();

        /// <summary>Zeroes wobble on Cinemachine's active cameras. The slow full search only runs on scene change.</summary>
        public static void Update()
        {
            if (VRConfig.CameraShake.Value || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            if (SceneWatch.Changed(ref seenScenes) || cameraListBroken)
            {
                parts.Clear();
                foreach (var n in UnityEngine.Object.FindObjectsOfType<CinemachineBasicMultiChannelPerlin>())
                    if (n != null && n.m_AmplitudeGain != 0f) n.m_AmplitudeGain = 0f;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<CinemachineImpulseListener>())
                    if (l != null && l.m_Gain != 0f) l.m_Gain = 0f;
                if (cameraListBroken) nextCheck = Time.unscaledTime + 2f;
                return;
            }
            try
            {
                var core = CinemachineCore.Instance;
                if (core == null) return;
                int count = core.VirtualCameraCount;
                for (int i = 0; i < count; i++)
                {
                    var vcam = core.GetVirtualCamera(i);
                    if (vcam == null) continue;
                    if (!parts.TryGetValue(vcam.Pointer, out var p))
                    {
                        p = (vcam.GetComponentsInChildren<CinemachineBasicMultiChannelPerlin>(true), vcam.GetComponent<CinemachineImpulseListener>());
                        parts[vcam.Pointer] = p;
                    }
                    for (int j = 0; j < p.noise.Length; j++)
                        if (p.noise[j] != null && p.noise[j].m_AmplitudeGain != 0f) p.noise[j].m_AmplitudeGain = 0f;
                    if (p.impulse != null && p.impulse.m_Gain != 0f) p.impulse.m_Gain = 0f;
                }
            }
            catch (Exception e)
            {
                // fall back to the full search every 2 s
                cameraListBroken = true;
                Plugin.L.LogWarning("Camera shake: Cinemachine's camera list is unreadable, searching instead: " + e.Message);
            }
        }
    }
}
