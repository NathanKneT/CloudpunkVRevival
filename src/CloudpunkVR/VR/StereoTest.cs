#if DEV
using System;
using System.Collections.Generic;
using AmplifyBloom;
using Bearroll;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.UI;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// StereoTest = true: cycles through suspects for an eye mismatch, one off every 12 s, with a label in view.
    /// Note which label makes it look right. Each step is logged.
    /// </summary>
    internal static class StereoTest
    {
        private static readonly string[] Steps =
        {
            "all effects on",
            "bloom off",
            "volumetric light off",
            "post-processing off (grading, fog, rain overlay)",
            "sky texture off",
            "occlusion culling off",
            "anti-aliasing off",
            "HOVA windows off (glass, its blur and drops)",
            "fog off (the city's height fog and its glow)",
            "all of the above off",
        };
        private const float StepSeconds = 12f;

        private static int step = -1;
        private static float stepStarted;
        private static readonly List<(Behaviour b, bool was)> changed = new List<(Behaviour, bool)>();
        private static readonly List<Renderer> hidden = new List<Renderer>();
        private static readonly List<PostProcessEffectSettings> fogs = new List<PostProcessEffectSettings>();
        private static bool cullingOff;
        private static GameObject labelRoot;
        private static TextMeshProUGUI label;

        public static void Update(int overlayLayer)
        {
            if (!VRConfig.StereoTest.Value)
            {
                if (step >= 0) { Restore(); step = -1; if (labelRoot != null) labelRoot.SetActive(false); }
                return;
            }
            if (step < 0 || Time.unscaledTime - stepStarted > StepSeconds)
            {
                Restore();
                step = (step + 1) % Steps.Length;
                stepStarted = Time.unscaledTime;
                try { Apply(step); }
                catch (Exception e) { Plugin.L.LogWarning("Stereo test step failed: " + e.Message); }
                Plugin.L.LogInfo($"Stereo test {step + 1}/{Steps.Length}: {Steps[step]}.");
            }
            ShowLabel(overlayLayer, $"STEREO TEST {step + 1}/{Steps.Length}: {Steps[step].ToUpperInvariant()}   ({Mathf.CeilToInt(StepSeconds - (Time.unscaledTime - stepStarted))} s)");
        }

        private static void Apply(int s)
        {
            bool all = s == Steps.Length - 1;
            if (s == 1 || all) foreach (var b in UnityEngine.Object.FindObjectsOfType<AmplifyBloomEffect>()) Off(b);
            if (s == 2 || all)
            {
                foreach (var b in UnityEngine.Object.FindObjectsOfType<HxVolumetricCamera>()) Off(b);
                foreach (var b in UnityEngine.Object.FindObjectsOfType<HxVolumetricImageEffect>()) Off(b);
            }
            if (s == 3 || all) foreach (var b in UnityEngine.Object.FindObjectsOfType<PostProcessLayer>()) Off(b);
            if (s == 4 || all) foreach (var b in UnityEngine.Object.FindObjectsOfType<ViewToGlobalTexture>()) Off(b);
            if (s == 5 || all) { GDOC.Disable(); cullingOff = true; }
            if (s == 6 || all) foreach (var b in UnityEngine.Object.FindObjectsOfType<FXAA>()) Off(b);
            if (s == 7 || all)
            {
                var windows = CockpitGlass.WindowRenderers();
                for (int i = 0; i < windows.Count; i++)
                {
                    if (!windows[i].enabled) continue;
                    hidden.Add(windows[i]);
                    windows[i].enabled = false;
                }
            }
            if (s == 8 || all)
            {
                // GlobalFog and SCPE fog: "active" is their only switch
                var volumes = UnityEngine.Object.FindObjectsOfType<PostProcessVolume>();
                for (int v = 0; v < volumes.Length; v++)
                {
                    var volume = volumes[v];
                    var list = volume != null && volume.sharedProfile != null ? volume.sharedProfile.settings : null;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var fx = list[i];
                        if (fx == null || !fx.active) continue;
                        string type = fx.GetIl2CppType().FullName;
                        if (type != "WhiteCat.Rendering.PostProcessing.GlobalFog" && type != "SCPE.Fog") continue;
                        fx.active = false;
                        fogs.Add(fx);
                    }
                }
            }
        }

        private static void Off(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            changed.Add((b, true));
            b.enabled = false;
        }

        private static void Restore()
        {
            foreach (var (b, was) in changed)
                try { if (b != null) b.enabled = was; } catch (Exception) { }
            changed.Clear();
            for (int i = 0; i < hidden.Count; i++)
                try { if (hidden[i] != null) hidden[i].enabled = true; } catch (Exception) { }
            hidden.Clear();
            for (int i = 0; i < fogs.Count; i++)
                try { if (fogs[i] != null) fogs[i].active = true; } catch (Exception) { }
            fogs.Clear();
            if (cullingOff)
            {
                cullingOff = false;
                try { if (VRConfig.OcclusionCulling.Value) GDOC.Enable(); } catch (Exception) { }
            }
        }

        private static void ShowLabel(int layer, string text)
        {
            if (labelRoot == null)
            {
                TMP_FontAsset font = null;
                foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (f != null && f.name == "RistrettoPro-Regular") font = f;
                labelRoot = new GameObject("CloudpunkVR_StereoTestLabel") { layer = layer };
                UnityEngine.Object.DontDestroyOnLoad(labelRoot);
                var canvas = labelRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.sortingOrder = 32000;
                var rt = labelRoot.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(1600f, 110f);
                labelRoot.transform.localScale = Vector3.one * 0.0011f;
                var back = new GameObject("Back") { layer = layer };
                back.transform.SetParent(labelRoot.transform, false);
                var image = back.AddComponent<Image>();
                image.color = new Color32(11, 18, 32, 220);
                image.rectTransform.sizeDelta = new Vector2(1640f, 120f);
                var go = new GameObject("Text") { layer = layer };
                go.transform.SetParent(labelRoot.transform, false);
                label = go.AddComponent<TextMeshProUGUI>();
                if (font != null) label.font = font;
                label.fontSize = 72f;
                label.color = new Color32(245, 166, 35, 255);
                label.alignment = TextAlignmentOptions.Center;
                label.rectTransform.sizeDelta = new Vector2(1600f, 110f);
            }
            bool show = !Film.CleanFrame.HidesOverlays;
            if (labelRoot.activeSelf != show) labelRoot.SetActive(show);
            if (!show) return;
            label.text = text;
            Quaternion yawPitch = CameraDriver.HeadWorldRotation;
            // above the gaze, menus sit below it
            Vector3 dir = yawPitch * Quaternion.Euler(-16f, 0f, 0f) * Vector3.forward;
            labelRoot.transform.SetPositionAndRotation(CameraDriver.HeadWorldPosition + dir * 1.4f, Quaternion.LookRotation(dir, yawPitch * Vector3.up));
        }
    }
}
#endif
