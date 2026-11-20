#if DEV
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Dev commands to find what differs between the eyes:
    ///   fx pp [&lt;Type&gt; off|on | reset]        post-processing volumes and their effects
    ///   fx cam [&lt;Component&gt; off|on | reset]  the view camera's own effects
    ///   fx eyes off|on | dump | fake ...     per-eye fog fix, its vectors, fake eyes (see FakeEyes)
    ///   eyes [frames]                        renderers and lights that change between the eyes
    /// </summary>
    internal static class PostFxProbe
    {
        private static readonly Dictionary<IntPtr, PostProcessEffectSettings> switched = new Dictionary<IntPtr, PostProcessEffectSettings>();

        public static string Pp(string[] a)
        {
            string what = a.Length > 2 ? a[2] : "list";
            if (what == "list") return List();
            if (what == "reset")
            {
                int n = switched.Count;
                foreach (var kv in switched) try { if (kv.Value != null) kv.Value.active = true; } catch (Exception) { }
                switched.Clear();
                return $"{n} effect(s) back on";
            }
            bool on = a.Length < 4 || a[3].ToLowerInvariant() != "off";
            int count = 0;
            var volumes = UnityEngine.Object.FindObjectsOfType<PostProcessVolume>();
            for (int v = 0; v < volumes.Length; v++)
            {
                var list = Settings(volumes[v]);
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    if (s == null || !string.Equals(s.GetIl2CppType().Name, what, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!on && s.active) { s.active = false; switched[s.Pointer] = s; count++; }
                    else if (on && switched.Remove(s.Pointer)) { s.active = true; count++; }
                }
            }
            return $"{what} {(on ? "back on" : "off")} in {count} place(s)";
        }

        private static readonly List<Behaviour> camOff = new List<Behaviour>();

        /// <summary>fx cam [list | &lt;Component&gt; off|on | reset]: toggles the view camera's effects one type at a time.</summary>
        public static string Cam(string[] a)
        {
            var cam = CameraDriver.ViewCamera;
            if (cam == null) return "no view camera";
            string what = a.Length > 2 ? a[2] : "list";
            if (what == "reset")
            {
                int n = camOff.Count;
                for (int i = 0; i < camOff.Count; i++) try { if (camOff[i] != null) camOff[i].enabled = true; } catch (Exception) { }
                camOff.Clear();
                return $"{n} component(s) of the view camera back on";
            }
            var all = cam.GetComponents<Behaviour>();
            var sb = new StringBuilder($"'{cam.name}':");
            bool on = a.Length < 4 || a[3].ToLowerInvariant() != "off";
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                string type = b.GetIl2CppType().Name;
                if (what != "list" && string.Equals(type, what, StringComparison.OrdinalIgnoreCase))
                {
                    if (!on && b.enabled) { b.enabled = false; camOff.Add(b); count++; }
                    else if (on && !b.enabled && camOff.Remove(b)) { b.enabled = true; count++; }
                }
                sb.Append($" {type}{(b.enabled ? "" : "(off)")}");
            }
            return what == "list" ? sb.ToString() : $"{what} {(on ? "back on" : "off")} on {count} component(s); {sb}";
        }

        private static List<PostProcessEffectSettings> Settings(PostProcessVolume volume)
        {
            var profile = volume != null ? volume.sharedProfile : null;
            var il = profile != null ? profile.settings : null;
            if (il == null) return null;
            var list = new List<PostProcessEffectSettings>();
            for (int i = 0; i < il.Count; i++) list.Add(il[i]);
            return list;
        }

        private static string List()
        {
            var sb = new StringBuilder();
            var volumes = UnityEngine.Object.FindObjectsOfType<PostProcessVolume>();
            for (int v = 0; v < volumes.Length; v++)
            {
                var vol = volumes[v];
                var list = Settings(vol);
                sb.Append($"\n  '{Diagnostics.Path(vol.transform)}' {(vol.isGlobal ? "global" : "local")} weight {vol.weight:0.##} priority {vol.priority:0.##} layer {LayerMask.LayerToName(vol.gameObject.layer)}, profile '{(vol.sharedProfile != null ? vol.sharedProfile.name : "-")}':");
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) sb.Append($" {list[i].GetIl2CppType().Name}{(list[i].active ? "" : "(off)")}");
            }
            var view = CameraDriver.ViewCamera;
            var layer = view != null ? view.GetComponent<PostProcessLayer>() : null;
            if (layer != null) sb.Append($"\n  view camera '{view.name}': layer mask 0x{layer.volumeLayer.value:X8}, {(layer.enabled ? "on" : "off")}");
            return $"{volumes.Length} volume(s):{sb}";
        }
    }

    /// <summary>
    /// `fx eyes fake [turn [ipd [tilt]]|off]`: fakes off-centre eyes (like PS VR2 or Index) on post-processing cameras.
    /// Frustums turned out by `turn` degrees (6), eyes `ipd` mm apart (63), reaching `tilt` degrees further down (0).
    /// </summary>
    internal static class FakeEyes
    {
        public static float Shift { get; private set; }
        public static float Ipd { get; private set; }
        public static float Tilt { get; private set; }
        private static readonly Dictionary<IntPtr, (Matrix4x4 left, Matrix4x4 right)> real = new Dictionary<IntPtr, (Matrix4x4, Matrix4x4)>();
        private static readonly Dictionary<IntPtr, Camera> cameras = new Dictionary<IntPtr, Camera>();
        private static bool hooked, checkedLeft, checkedRight;

        /// <summary>Sets the fake eyes, or `off` gives SteamVR's back.</summary>
        public static string Set(string[] a)
        {
            string arg = a.Length > 3 ? a[3].ToLowerInvariant() : "";
            if (arg == "off")
            {
                Shift = Ipd = Tilt = 0f;
                foreach (var cam in cameras.Values)
                    try { if (cam != null) { cam.ResetStereoProjectionMatrices(); cam.ResetStereoViewMatrices(); } } catch (Exception) { }
                cameras.Clear();
                real.Clear();
                return "fake eyes off: SteamVR's frustums and eye positions back";
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Shift = arg == "" || arg == "on" ? 6f : float.Parse(arg, inv);
            Ipd = a.Length > 4 ? float.Parse(a[4], inv) / 1000f : 0.063f;
            Tilt = a.Length > 5 ? float.Parse(a[5], inv) : 0f;
            checkedLeft = checkedRight = false;
            if (!hooked) { hooked = true; RenderHook.PreCull += OnPreCull; }
            FogRaysPerEye.DumpNext = true;
            return $"fake eyes on: each eye's frustum turned {Shift:0.#} degrees outward, the eyes {Ipd * 1000f:0.#} mm apart, both frustums {Tilt:0.#} degrees further down than up";
        }

        private static void OnPreCull(Camera cam)
        {
            if (Shift == 0f && Ipd == 0f && Tilt == 0f) return;
            if (cam == null || !cam.stereoEnabled || cam.GetComponent<PostProcessLayer>() == null) return;
            if (!real.TryGetValue(cam.Pointer, out var r))
            {
                cam.ResetStereoProjectionMatrices();
                cam.ResetStereoViewMatrices();
                r = (cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right));
                real[cam.Pointer] = r;
                cameras[cam.Pointer] = cam;
                var sl = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
                var sr = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
                Plugin.L.LogInfo($"fx eyes fake: SteamVR put the eyes of '{cam.name}' {((Vector3)(sr - sl)).magnitude * 1000f:0.#} mm apart; now {Ipd * 1000f:0.#} mm.");
            }
            ApplyTo(cam, r);
        }

        private static void ApplyTo(Camera cam, (Matrix4x4 left, Matrix4x4 right) r)
        {
            cam.SetStereoProjectionMatrix(Camera.StereoscopicEye.Left, Turned(r.left, -Shift, Tilt));
            cam.SetStereoProjectionMatrix(Camera.StereoscopicEye.Right, Turned(r.right, Shift, Tilt));
            // eyes half the IPD to each side of the head, looking where it looks
            var view = cam.worldToCameraMatrix;
            cam.SetStereoViewMatrix(Camera.StereoscopicEye.Left, Matrix4x4.Translate(new Vector3(Ipd / 2f, 0f, 0f)) * view);
            cam.SetStereoViewMatrix(Camera.StereoscopicEye.Right, Matrix4x4.Translate(new Vector3(-Ipd / 2f, 0f, 0f)) * view);
        }

        /// <summary>Frustum turned by deg (positive: right) and tilted down, same width and height.</summary>
        private static Matrix4x4 Turned(Matrix4x4 p, float deg, float down)
        {
            float l = Mathf.Atan((1f - p.m02) / p.m00) * Mathf.Rad2Deg - deg, r = Mathf.Atan((1f + p.m02) / p.m00) * Mathf.Rad2Deg + deg;
            float b = Mathf.Atan((1f - p.m12) / p.m11) * Mathf.Rad2Deg + down, t = Mathf.Atan((1f + p.m12) / p.m11) * Mathf.Rad2Deg - down;
            float tl = Mathf.Tan(l * Mathf.Deg2Rad), tr = Mathf.Tan(r * Mathf.Deg2Rad);
            float tb = Mathf.Tan(b * Mathf.Deg2Rad), tt = Mathf.Tan(t * Mathf.Deg2Rad);
            var q = p;
            q.m00 = 2f / (tl + tr);
            q.m02 = (tr - tl) / (tl + tr);
            q.m11 = 2f / (tb + tt);
            q.m12 = (tt - tb) / (tb + tt);
            return q;
        }

        /// <summary>Re-applies the fake eyes after the stereo matrices were reset.</summary>
        public static bool Reapply(Camera cam)
        {
            if ((Shift == 0f && Ipd == 0f && Tilt == 0f) || !real.ContainsKey(cam.Pointer)) return false;
            ApplyTo(cam, real[cam.Pointer]);
            return true;
        }

        /// <summary>From the fog pass: logs once per eye whether the drawn frustum is the faked one.</summary>
        public static void CheckEye(Camera cam, bool right, Matrix4x4 drawn)
        {
            if ((Shift == 0f && Ipd == 0f && Tilt == 0f) || (right ? checkedRight : checkedLeft) || !real.TryGetValue(cam.Pointer, out var r)) return;
            if (right) checkedRight = true; else checkedLeft = true;
            var want = Turned(right ? r.right : r.left, right ? Shift : -Shift, Tilt);
            bool ok = Mathf.Abs(drawn.m02 - want.m02) < 1e-3f && Mathf.Abs(drawn.m00 - want.m00) < 1e-3f && Mathf.Abs(drawn.m12 - want.m12) < 1e-3f;
            var eyePos = cam.GetStereoViewMatrix(right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left).inverse.GetColumn(3);
            float side = Vector3.Dot((Vector3)eyePos - cam.transform.position, cam.transform.right) * 1000f;
            Plugin.L.LogInfo($"fx eyes fake: the {(right ? "right" : "left")} eye of '{cam.name}' is drawn {(ok ? "with the turned frustum" : "with SteamVR's frustum (the fake did not hold)")}: m00 {drawn.m00:0.####} m02 {drawn.m02:0.####} m12 {drawn.m12:0.####} (wanted {want.m00:0.####} {want.m02:0.####} {want.m12:0.####}), {side:0.#} mm to the head's right.");
        }
    }

    /// <summary>`eyes`: logs renderers and lights that change between the left and right eye draws.</summary>
    internal static class EyeProbe
    {
        private static Renderer[] renderers;
        private static Light[] lights;
        private static byte[] leftR, rightR;
        private static float[] leftL, rightL;
        private static int framesLeft, leftFrame = -1, lefts, rights, monos, compared, differing;
        private static Camera.CameraCallback callback;
        private static float until, menuSince = -1f;
        private static bool autoDone;

        /// <summary>Once per session, 5 s into the title menu: the same check over two frames.</summary>
        public static void AutoAtTitle()
        {
            if (autoDone || !VRSession.Active) return;
            if (CameraDriver.Mode != ViewMode.Menu || CameraDriver.ViewCamera == null) { menuSince = -1f; return; }
            if (menuSince < 0f) { menuSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - menuSince < 5f) return;
            autoDone = true;
            Plugin.L.LogInfo("eyes (once per session, on the title): " + Start(2));
        }

        public static string Start(int frames)
        {
            var allR = Resources.FindObjectsOfTypeAll<Renderer>();
            var r = new List<Renderer>();
            for (int i = 0; i < allR.Length; i++) if (allR[i] != null && allR[i].gameObject.scene.IsValid()) r.Add(allR[i]);
            renderers = r.ToArray();
            var allL = Resources.FindObjectsOfTypeAll<Light>();
            var l = new List<Light>();
            for (int i = 0; i < allL.Length; i++) if (allL[i] != null && allL[i].gameObject.scene.IsValid()) l.Add(allL[i]);
            lights = l.ToArray();
            leftR = new byte[renderers.Length]; rightR = new byte[renderers.Length];
            leftL = new float[lights.Length]; rightL = new float[lights.Length];
            framesLeft = Math.Max(1, frames);
            leftFrame = -1; lefts = rights = monos = compared = differing = 0;
            until = Time.unscaledTime + 10f;
            if (callback == null) callback = (Camera.CameraCallback)new Action<Camera>(OnPreRender);
            var current = Camera.onPreRender;
            Camera.onPreRender = current == null ? callback : (current - callback) + callback;
            return $"watching {renderers.Length} renderers and {lights.Length} lights between the eyes of '{(CameraDriver.ViewCamera != null ? CameraDriver.ViewCamera.name : "-")}' for {framesLeft} frame(s)";
        }

        private static void Stop(string why)
        {
            try { if (callback != null && Camera.onPreRender != null) Camera.onPreRender = Camera.onPreRender - callback; } catch (Exception) { }
            Plugin.L.LogInfo($"eyes: done ({why}): {compared} frame(s) compared, {differing} with a difference; eye passes seen: left {lefts}, right {rights}, mono {monos}.");
            renderers = null; lights = null;
        }

        private static void OnPreRender(Camera cam)
        {
            if (renderers == null) return;
            try
            {
                if (Time.unscaledTime > until) { Stop("time out"); return; }
                var view = CameraDriver.ViewCamera;
                if (cam == null || view == null || cam.Pointer != view.Pointer) return;
                var eye = cam.stereoActiveEye;
                if (eye == Camera.MonoOrStereoscopicEye.Mono) { monos++; return; }
                bool right = eye == Camera.MonoOrStereoscopicEye.Right;
                if (right) rights++; else lefts++;
                Snapshot(right ? rightR : leftR, right ? rightL : leftL);
                if (!right) { leftFrame = Time.frameCount; return; }
                if (leftFrame != Time.frameCount) return;
                Compare();
                if (--framesLeft <= 0) Stop("all frames read");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("eyes: " + e.Message);
                Stop("error");
            }
        }

        private static void Snapshot(byte[] r, float[] l)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                var x = renderers[i];
                if (x == null) { r[i] = 255; continue; }
                r[i] = (byte)((x.enabled ? 1 : 0) | (x.gameObject.activeInHierarchy ? 2 : 0) | ((int)x.shadowCastingMode << 2));
            }
            for (int i = 0; i < lights.Length; i++)
            {
                var x = lights[i];
                l[i] = x == null ? -2f : !x.enabled || !x.gameObject.activeInHierarchy ? -1f : x.intensity;
            }
        }

        private static void Compare()
        {
            compared++;
            var sb = new StringBuilder();
            int nr = 0, nl = 0;
            for (int i = 0; i < renderers.Length; i++)
                if (leftR[i] != rightR[i] && nr++ < 30)
                    sb.Append($" renderer '{Name(renderers[i])}' {State(leftR[i])} -> {State(rightR[i])};");
            for (int i = 0; i < lights.Length; i++)
                if (Mathf.Abs(leftL[i] - rightL[i]) > 0.001f && nl++ < 20)
                    sb.Append($" light '{Name(lights[i])}' {LightState(leftL[i])} -> {LightState(rightL[i])};");
            int n = nr + nl;
            if (n > 0) differing++;
            Plugin.L.LogInfo($"eyes: frame {Time.frameCount}: {(n == 0 ? "the same renderers and lights in both eyes" : $"{n} change(s) between the left and the right eye:{sb}")}");
        }

        /// <summary>A renderer's or light's path, or "(destroyed)" when it went away between the eyes.</summary>
        private static string Name(Component c)
        {
            try { return c != null ? Diagnostics.Path(c.transform) : "(destroyed)"; }
            catch (Exception) { return "(destroyed)"; }
        }

        private static string State(byte b) => b == 255 ? "gone" : $"{((b & 1) != 0 ? "on" : "off")}{((b & 2) != 0 ? "" : " inactive")} {(ShadowCastingMode)(b >> 2)}";
        private static string LightState(float f) => f == -2f ? "gone" : f < 0f ? "off" : $"intensity {f:0.###}";
    }
}
#endif
