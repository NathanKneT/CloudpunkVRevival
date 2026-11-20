#if DEV
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Dev command `glass`: lists and toggles what sits between the eyes and the city in the cockpit, to hunt one-eye bugs.
    ///   glass [list] | off|on | near off|on | blur off|on | layer &lt;n&gt; off|on | reset | game|clear
    /// </summary>
    internal static class GlassProbe
    {
        private const float Near = 4f;
        private static readonly Dictionary<IntPtr, (Renderer r, bool enabled)> switched = new Dictionary<IntPtr, (Renderer, bool)>();
        private static readonly Dictionary<IntPtr, Material> blurOff = new Dictionary<IntPtr, Material>();
        private static (Camera cam, int mask)? maskBefore;

        public static string Dev(string[] a)
        {
            string what = a.Length > 1 ? a[1].ToLowerInvariant() : "list";
            string arg = a.Length > 2 ? a[2].ToLowerInvariant() : "";
            var g = Global.Instance;
            var main = g != null ? g.mainCamera : null;
            var interior = g != null && g.vehicleInterior != null ? g.vehicleInterior.cameraCockpit : null;
            if (main == null) return "no main camera";
            switch (what)
            {
                case "list":
                    return List(main, interior);
                case "off":
                case "on":
                    return $"{Switch(r => IsGlass(r), what == "on")} glass renderer(s) {what}";
                case "near":
                    {
                        Vector3 eye = main.transform.position;
                        int mask = main.cullingMask;
                        return $"{Switch(r => ((mask >> r.gameObject.layer) & 1) != 0 && Vector3.Distance(r.bounds.center, eye) < Near, arg != "off")} renderer(s) within {Near} m of the main camera {(arg == "off" ? "off" : "on")}";
                    }
                case "blur":
                    return Blur(arg != "off");
                case "layer":
                    {
                        int layer = a.Length > 2 ? int.Parse(a[2]) : -1;
                        if (layer < 0 || layer > 31) return "glass layer <0-31> off|on";
                        if (maskBefore == null) maskBefore = (main, Film.CleanFrame.GameMask(main)); // game's mask, minus clean-frame edits
                        bool on = a.Length < 4 || a[3].ToLowerInvariant() != "off";
                        main.cullingMask = on ? main.cullingMask | (1 << layer) : main.cullingMask & ~(1 << layer);
                        return $"main camera layer {layer} ({LayerMask.LayerToName(layer)}) {(on ? "on" : "off")}, mask 0x{main.cullingMask:X8}";
                    }
                case "reset":
                    Reset();
                    return "everything back as it was";
                case "game": // sets CockpitWindowBlur for this run
                case "clear":
                    VRConfig.CockpitWindowBlur.Value = what == "game";
                    CockpitGlass.Refresh();
                    return "CockpitWindowBlur " + VRConfig.CockpitWindowBlur.Value + "; " + CockpitGlass.Describe();
                default:
                    return "unknown: glass [list|off|on|near off|blur off|layer n off|reset|game|clear]";
            }
        }

        private static bool IsGlass(Renderer r)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || m.shader == null) continue;
                string s = m.shader.name.ToLowerInvariant();
                if (s.Contains("windshield") || s.Contains("glass") || s.Contains("blur") || s.Contains("refract") || s.Contains("distort")) return true;
            }
            return false;
        }

        private static int Switch(Func<Renderer, bool> which, bool on)
        {
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Renderer>();
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null || !r.gameObject.scene.IsValid() || r.name.StartsWith("CloudpunkVR")) continue;
                if (!which(r)) continue;
                if (!on)
                {
                    if (!r.enabled) continue;
                    if (!switched.ContainsKey(r.Pointer)) switched[r.Pointer] = (r, true);
                    r.enabled = false;
                    n++;
                }
                else if (switched.TryGetValue(r.Pointer, out var was))
                {
                    r.enabled = was.enabled;
                    switched.Remove(r.Pointer);
                    n++;
                }
            }
            return n;
        }

        private static string Blur(bool on)
        {
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var m = all[i];
                if (m == null || m.shader == null || !m.shader.name.Contains("Windshield")) continue;
                if (!on && m.IsKeywordEnabled("_BLUR_GLASS")) { m.DisableKeyword("_BLUR_GLASS"); blurOff[m.Pointer] = m; n++; }
                else if (on && blurOff.Remove(m.Pointer)) { m.EnableKeyword("_BLUR_GLASS"); n++; }
            }
            return $"windscreen blur {(on ? "back on" : "off")} on {n} material(s)";
        }

        private static void Reset()
        {
            foreach (var kv in switched)
                try { if (kv.Value.r != null) kv.Value.r.enabled = kv.Value.enabled; } catch (Exception) { }
            switched.Clear();
            foreach (var kv in blurOff)
                try { if (kv.Value != null) kv.Value.EnableKeyword("_BLUR_GLASS"); } catch (Exception) { }
            blurOff.Clear();
            if (maskBefore != null && maskBefore.Value.cam != null) maskBefore.Value.cam.cullingMask = maskBefore.Value.mask;
            maskBefore = null;
        }

        private static string List(Camera main, Camera interior)
        {
            var sb = new StringBuilder();
            sb.Append("\n  ").Append(CockpitGlass.Describe());
            Cam(sb, "main", main);
            if (interior != null) Cam(sb, "interior", interior);
            Vector3 eye = main.transform.position;
            Vector3 ieye = interior != null ? interior.transform.position : new Vector3(1e6f, 1e6f, 1e6f);
            int listed = 0;
            var all = Resources.FindObjectsOfTypeAll<Renderer>();
            for (int i = 0; i < all.Length && listed < 120; i++)
            {
                var r = all[i];
                if (r == null || !r.gameObject.scene.IsValid() || r.name.StartsWith("CloudpunkVR")) continue;
                int layer = r.gameObject.layer;
                Vector3 c = r.bounds.center;
                bool byMain = ((main.cullingMask >> layer) & 1) != 0 && Vector3.Distance(c, eye) < Near;
                bool byInterior = interior != null && ((interior.cullingMask >> layer) & 1) != 0 && Vector3.Distance(c, ieye) < Near && IsGlass(r);
                bool glass = IsGlass(r) && r.gameObject.activeInHierarchy;
                if (!byMain && !byInterior && !glass) continue;
                listed++;
                sb.Append($"\n  [{(byMain ? "main" : byInterior ? "interior" : "elsewhere")}] {Diagnostics.Path(r.transform)} ({r.GetIl2CppType().Name}) layer {layer} {LayerMask.LayerToName(layer)}, " +
                          $"{(r.enabled ? "on" : "off")}{(r.gameObject.activeInHierarchy ? "" : " inactive")}{(r.isVisible ? " visible" : "")}, {Vector3.Distance(c, byInterior ? ieye : eye):0.00} m:");
                var mats = r.sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    var m = mats[j];
                    if (m == null) continue;
                    sb.Append($" '{m.name}' {(m.shader != null ? m.shader.name : "-")} q{m.renderQueue} passes {m.passCount}");
                    var kw = m.shaderKeywords;
                    if (kw != null && kw.Length > 0)
                    {
                        sb.Append(" [");
                        for (int k = 0; k < kw.Length; k++) sb.Append(k > 0 ? " " : "").Append(kw[k]);
                        sb.Append(']');
                    }
                    if (m.shader != null && m.shader.name.Contains("Windshield"))
                        sb.Append($" material blur {Num(m, "_BlurIntensity")} x{Num(m, "_BlurSamples")} grain {Num(m, "_NoiseIntensity")} alpha {Num(m, "_GlassAlpha")} rain {Tex(m, "_RainMask")}");
                    sb.Append(';');
                }
            }
            return $"{listed} renderer(s):{sb}";
        }

        private static string Num(Material m, string p) => m.HasProperty(p) ? m.GetFloat(p).ToString("0.###") : "-";

        private static string Tex(Material m, string p)
        {
            if (!m.HasProperty(p)) return "-";
            var t = m.GetTexture(p);
            return t != null ? t.name : "none";
        }

        private static void Cam(StringBuilder sb, string label, Camera cam)
        {
            sb.Append($"\n  {label} camera '{cam.name}': hdr {cam.allowHDR}, path {cam.actualRenderingPath}, stereo {cam.stereoEnabled} ({cam.stereoTargetEye}), " +
                      $"near {cam.nearClipPlane:0.###}, mask 0x{cam.cullingMask:X8}, {cam.commandBufferCount} command buffer(s):");
            if (cam.commandBufferCount == 0) return;
            var events = (CameraEvent[])Enum.GetValues(typeof(CameraEvent));
            for (int e = 0; e < events.Length; e++)
            {
                var bufs = cam.GetCommandBuffers(events[e]);
                for (int i = 0; i < bufs.Length; i++)
                    if (bufs[i] != null) sb.Append($" {events[e]}:'{bufs[i].name}'");
            }
        }
    }
}
#endif
