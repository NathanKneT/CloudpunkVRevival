using System;
using System.Collections.Generic;
using HarmonyLib;
using SCPE;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Screen-only staging kept out of the headset (StereoFixes): scripted third-person cameras, black bars,
    /// tilt-shift, DoF, tube distortion, hue shift, lens raindrops, and lens flares (mirrored per eye).
    /// </summary>
    internal static class SceneComfort
    {
        private static float nextScan, nextSearch;
        private static int seenScenes = -1;
        private static readonly HashSet<IntPtr> logged = new HashSet<IntPtr>();
        private static bool warned;
        // searched on scene change (and every 30 s), checked 4x a second: the search itself is slow
        private static CameraControlVolume[] cameraVolumes = new CameraControlVolume[0];

        public static bool Active => VRSession.Active && VRConfig.StereoFixes.Value;

        public static void Update()
        {
            if (!Active || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.25f;
            try
            {
                if (SceneWatch.Changed(ref seenScenes) || Time.unscaledTime >= nextSearch)
                {
                    nextSearch = Time.unscaledTime + 30f;
                    cameraVolumes = UnityEngine.Object.FindObjectsOfType<CameraControlVolume>();
                    LensEffectsOff();
                }
                for (int i = 0; i < cameraVolumes.Length; i++) KeepFirstPerson(cameraVolumes[i]);
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; Plugin.L.LogWarning("Scripted cameras not checked: " + e.Message); }
            }
        }

        /// <summary>Lens raindrops have no own on/off check to patch, so flip "active" in each profile.</summary>
        private static void LensEffectsOff()
        {
            foreach (var volume in UnityEngine.Object.FindObjectsOfType<PostProcessVolume>())
            {
                var profile = volume != null ? volume.sharedProfile : null;
                var list = profile != null ? profile.settings : null;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    if (s == null || !s.active || s.GetIl2CppType().Name != "SplashesAndDrips") continue;
                    s.active = false;
                    Plugin.L.LogInfo("Raindrops on the lens left out (stuck to the eyes in a headset).");
                }
            }
        }

        /// <summary>Turns a scripted camera back off so the view stays first person.</summary>
        public static void KeepFirstPerson(CameraControlVolume v)
        {
            if (v == null) return;
            var cam = v.overrideCamera;
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy) return;
            cam.enabled = false;
            if (logged.Add(v.Pointer)) Plugin.L.LogInfo($"Scripted camera of '{v.name}' left out: the view stays first person.");
        }
    }

    [HarmonyPatch(typeof(CameraControlVolume), nameof(CameraControlVolume.OnTriggerEnter))]
    internal static class ScriptedCameraOnEnter
    {
        private static void Postfix(CameraControlVolume __instance)
        {
            if (SceneComfort.Active) SceneComfort.KeepFirstPerson(__instance);
        }
    }

    /// <summary>Loading a save restores each volume's camera before our next search.</summary>
    [HarmonyPatch(typeof(CameraControlVolume), nameof(CameraControlVolume.OnAfterLoad))]
    internal static class ScriptedCameraAfterLoad
    {
        private static void Postfix(CameraControlVolume __instance)
        {
            if (SceneComfort.Active) SceneComfort.KeepFirstPerson(__instance);
        }
    }

    [HarmonyPatch(typeof(BlackBars), nameof(BlackBars.IsEnabledAndSupported))]
    internal static class NoBlackBars
    {
        private static void Postfix(ref bool __result) { if (SceneComfort.Active) __result = false; }
    }

    [HarmonyPatch(typeof(TiltShift), nameof(TiltShift.IsEnabledAndSupported))]
    internal static class NoTiltShift
    {
        private static void Postfix(ref bool __result) { if (SceneComfort.Active) __result = false; }
    }

    [HarmonyPatch(typeof(TubeDistortion), nameof(TubeDistortion.IsEnabledAndSupported))]
    internal static class NoTubeDistortion
    {
        private static void Postfix(ref bool __result) { if (SceneComfort.Active) __result = false; }
    }

    [HarmonyPatch(typeof(HueShift3D), nameof(HueShift3D.IsEnabledAndSupported))]
    internal static class NoHueShift
    {
        private static void Postfix(ref bool __result) { if (SceneComfort.Active) __result = false; }
    }

    [HarmonyPatch(typeof(WhiteCat.Rendering.PostProcessing.PostLensFlare), nameof(WhiteCat.Rendering.PostProcessing.PostLensFlare.IsEnabledAndSupported))]
    internal static class NoLensFlare
    {
        private static void Postfix(ref bool __result) { if (SceneComfort.Active) __result = false; }
    }

    [HarmonyPatch(typeof(DepthOfField), nameof(DepthOfField.IsEnabledAndSupported))]
    internal static class NoDepthOfField
    {
        private static void Postfix(ref bool __result) { if (SceneComfort.Active) __result = false; }
    }
}
