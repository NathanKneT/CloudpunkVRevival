using System;
using System.Collections.Generic;
using AmplifyBloom;
using Bearroll;
using Bearroll.GDOC_Internal;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Image effects made for one screen that differ between the eyes.
    /// Lens flares, bloom temporal/flares/dirt, volumetric temporal and god rays off, transparents sorted by view depth.
    /// The sky is drawn per eye. StereoFixes = false restores the game's settings next launch.
    /// </summary>
    internal static class StereoFixes
    {
        private static float nextPass;
        private static bool logged, warned, cullingRestarted, cullingOff, cullingWarned, cameraCountBroken;
        private static int seenScenes = -1, lastCameraCount = -1;

        /// <summary>GDOC already running when the mod loaded: set its VR mode and restart it once.</summary>
        private static void Culling(GDOC g)
        {
            try
            {
                if (!VRConfig.OcclusionCulling.Value)
                {
                    if (!cullingOff) { cullingOff = true; GDOC.Disable(); Plugin.L.LogInfo("Occlusion culling (GDOC) off (OcclusionCulling = false)."); }
                    return;
                }
                if (g == null || g.vrMode == GDOC_VRMode.MultiPass) return;
                g.vrMode = GDOC_VRMode.MultiPass;
                if (!cullingRestarted)
                {
                    cullingRestarted = true;
                    GDOC.Restart();
                    Plugin.L.LogInfo("Occlusion culling (GDOC) restarted in its two-eye mode.");
                }
                else Plugin.L.LogInfo("Occlusion culling (GDOC) set to its two-eye mode.");
            }
            catch (Exception e)
            {
                if (!cullingWarned) { cullingWarned = true; Plugin.L.LogWarning("Occlusion culling VR mode not set: " + e.Message); }
            }
        }

        /// <summary>On a camera count or scene change (new cameras fixed next frame), plus a slow pass.</summary>
        private static bool Due()
        {
            int count = -1;
            if (!cameraCountBroken)
            {
                try { count = Camera.allCamerasCount; }
                catch (Exception) { cameraCountBroken = true; }
            }
            bool due = Time.unscaledTime >= nextPass || SceneWatch.Changed(ref seenScenes) || (count >= 0 && count != lastCameraCount);
            if (count >= 0) lastCameraCount = count;
            return due;
        }

        private static float nextCallbackCheck;
        private static int callbackScenes = -1;

        public static void Update()
        {
            if (!VRConfig.StereoFixes.Value) return;
            // keep the per-eye callback last: on scene change, else once a second
            if (SceneWatch.Changed(ref callbackScenes))
            {
                FogRaysPerEye.ClearCaches();
                VolumetricPerEye.ClearCaches();
                FogRaysPerEye.EnsureCallback();
                nextCallbackCheck = Time.unscaledTime + 1f;
            }
            else if (Time.unscaledTime >= nextCallbackCheck)
            {
                nextCallbackCheck = Time.unscaledTime + 1f;
                FogRaysPerEye.EnsureCallback();
            }
#if DEV
            EyeProbe.AutoAtTitle();
#endif
            if (!Due()) return;
            nextPass = Time.unscaledTime + (cameraCountBroken ? 2f : 10f);
            int bloom = 0, volumetric = 0, rays = 0, flares = 0, sorted = 0;
            try
            {
                // look on the cameras: FindObjectsOfType over the city stalled a frame
                var cams = Camera.allCameras;
                for (int i = 0; i < cams.Length; i++)
                {
                    var cam = cams[i];
                    if (cam == null || !cam.stereoEnabled) continue;
                    // lens flares fade from a per-eye occlusion test: flicker in one eye
                    var flare = cam.GetComponent<FlareLayer>();
                    if (flare != null && flare.enabled) { flare.enabled = false; flares++; }
                    // sorted by eye distance, transparents can swap order in one eye. View depth is shared.
                    if (cam.transparencySortMode != TransparencySortMode.Orthographic)
                    {
                        cam.transparencySortMode = TransparencySortMode.Orthographic;
                        sorted++;
                    }
                    Culling(cam.GetComponent<GDOC>());
                    var b = cam.GetComponent<AmplifyBloomEffect>();
                    if (b != null)
                    {
                        if (b.TemporalFilteringActive) b.TemporalFilteringActive = false;
                        if (b.ApplyLensFlare) b.ApplyLensFlare = false;
                        if (b.ApplyLensDirt) b.ApplyLensDirt = false;
                        if (b.ApplyLensStardurst) b.ApplyLensStardurst = false;
                        bloom++;
                    }
                    var v = cam.GetComponent<HxVolumetricCamera>();
                    if (v != null)
                    {
                        if (v.TemporalSampling) v.TemporalSampling = false;
                        volumetric++;
                    }
                    var p = cam.GetComponent<PostProcess>();
                    if (p != null)
                    {
                        if (p.GodRayAmount != 0f) p.GodRayAmount = 0f;
                        rays++;
                    }
                }
                if (sorted > 0) Plugin.L.LogInfo($"Stereo fixes: transparent objects sorted by view depth on {sorted} camera(s), the same in both eyes.");
                if (flares > 0) Plugin.L.LogInfo($"Stereo fixes: lens flares off on {flares} camera(s) (they flickered differently in each eye).");
                if (!logged && bloom + volumetric + rays > 0)
                {
                    logged = true;
                    Plugin.L.LogInfo($"Stereo fixes: {bloom} bloom, {volumetric} volumetric light, {rays} light-ray effect(s) set for two eyes.");
                }
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; Plugin.L.LogWarning("Stereo fixes not applied: " + e.Message); }
            }
        }
    }

    /// <summary>GDOC without its VR mode can toggle lights between the eyes: multi-pass set before it starts.</summary>
    [HarmonyPatch(typeof(GDOC), nameof(GDOC.Awake))]
    internal static class CullingInVRMode
    {
        private static void Prefix(GDOC __instance)
        {
            if (VRConfig.StereoFixes.Value && __instance != null) __instance.vrMode = GDOC_VRMode.MultiPass;
        }
    }

    /// <summary>The sky texture's camera copies the main one: give it each eye's off-centre projection.</summary>
    [HarmonyPatch(typeof(ViewToGlobalTexture), nameof(ViewToGlobalTexture.OnPreRender))]
    internal static class SkyTexturePerEye
    {
        private static bool warned;

        private static void Prefix(ViewToGlobalTexture __instance)
        {
            if (!VRConfig.StereoFixes.Value || !VRSession.Active) return;
            try
            {
                var main = __instance.mainCam;
                var sky = __instance.skycam;
                if (main == null || sky == null || !main.stereoEnabled) return;
                var eye = main.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                sky.projectionMatrix = main.GetStereoProjectionMatrix(eye);
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; Plugin.L.LogWarning("Sky texture per eye: " + e.Message); }
            }
        }
    }

    /// <summary>
    /// The city's GlobalFog rebuilds pixel rays from _CameraVectorX/Y/Z, computed once per frame for the centred
    /// frustum. Each eye has its own, so the vectors are rewritten before each eye.
    /// </summary>
    internal static class FogRaysPerEye
    {
        /// <summary>Dev switch (`fx eyes off`) to compare with the game's vectors.</summary>
        public static bool Enabled = true;
        /// <summary>Dev readout (`fx eyes dump`): logs the game's and each eye's vectors for one frame.</summary>
        public static bool DumpNext;
        private static readonly int XId = Shader.PropertyToID("_CameraVectorX");
        private static readonly int YId = Shader.PropertyToID("_CameraVectorY");
        private static readonly int ZId = Shader.PropertyToID("_CameraVectorZ");
        private static readonly Dictionary<IntPtr, bool> fogCameras = new Dictionary<IntPtr, bool>();
        private static Camera.CameraCallback callback;
        private static bool broken, logged, checkedLayout, layoutOk = true;
        private static int frame = -1;
        private static IntPtr frameCamera;
        // game's layout this frame in head axes: signed width, height, corner depth
        private static float width, height, cornerDepth;
        private static Vector3 gameX, gameY, gameZ;
        public static int Left, Right;

        public static bool Active => Enabled && VRConfig.StereoFixes.Value && VRSession.Active && layoutOk && !broken;

        /// <summary>Keeps our pre-render callback after the game's. Not every frame: it allocates.</summary>
        public static void EnsureCallback()
        {
            if (broken) return;
            try
            {
                if (callback == null) callback = (Camera.CameraCallback)new Action<Camera>(OnPreRender);
                var current = Camera.onPreRender;
                Camera.onPreRender = current == null ? callback : (current - callback) + callback;
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning("Fog per eye unavailable (no pre-render callback): " + e.Message);
            }
        }

        /// <summary>New scene: forget cameras (a pointer can come back as another camera).</summary>
        public static void ClearCaches() => fogCameras.Clear();

        private static void OnPreRender(Camera cam)
        {
            if (cam == null || broken) return;
            VolumetricPerEye.OnPreRender(cam);
            bool fix = Active;
#if DEV
            if (!fix && FakeEyes.Shift == 0f && !DumpNext) return;
#else
            if (!fix && !DumpNext) return;
#endif
            try
            {
                var eye = cam.stereoActiveEye;
                if (eye == Camera.MonoOrStereoscopicEye.Mono || !cam.stereoEnabled || !DrawsFog(cam)) return;
                Transform t = cam.transform;
                Vector3 r = t.right, u = t.up, f = t.forward;
                // first eye of the frame reads the game's centred vectors for this camera
                if (frame != Time.frameCount || frameCamera != cam.Pointer)
                {
                    Vector3 x = Shader.GetGlobalVector(XId), y = Shader.GetGlobalVector(YId), z = Shader.GetGlobalVector(ZId);
                    if (x.sqrMagnitude < 1e-6f || y.sqrMagnitude < 1e-6f || Vector3.Dot(z, f) <= 0f) return; // not this camera's
                    frame = Time.frameCount;
                    frameCamera = cam.Pointer;
                    gameX = x; gameY = y; gameZ = z;
                    width = Vector3.Dot(x, r);
                    height = Vector3.Dot(y, u);
                    cornerDepth = Vector3.Dot(z, f);
                    if (!checkedLayout) Check(cam, x, y, z, r, u, f);
                    if (!layoutOk) return;
                }
                bool right = eye == Camera.MonoOrStereoscopicEye.Right;
                var which = right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                var projection = cam.GetStereoProjectionMatrix(which);
#if DEV
                FakeEyes.CheckEye(cam, right, projection);
#endif
                // eye's own axes: canted displays turn each eye's frustum
                EyeAxes(cam, which, out Vector3 er, out Vector3 eu, out Vector3 ef);
                Write(projection, er, eu, ef, out Vector3 vx, out Vector3 vy, out Vector3 vz);
                if (DumpNext)
                {
                    Plugin.L.LogInfo($"fx eyes: frame {Time.frameCount} on '{cam.name}': the game's centred fog vectors X {gameX} Y {gameY} Z {gameZ}; " +
                                     $"the {(right ? "right" : "left")} eye's own X {vx} Y {vy} Z {vz} ({(fix ? "written" : "not written, fix off")}).");
                    if (right) DumpNext = false;
                }
                if (!fix) return;
                Shader.SetGlobalVector(XId, vx);
                Shader.SetGlobalVector(YId, vy);
                Shader.SetGlobalVector(ZId, vz);
                if (right) Right++; else Left++;
                if (!logged && Left > 0 && Right > 0)
                {
                    logged = true;
                    Plugin.L.LogInfo($"Stereo fixes: the city's fog (GlobalFog) laid along each eye's own frustum on '{cam.name}' (left and right eye passes seen).");
                }
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning("Fog per eye stopped: " + e.Message);
            }
        }

        /// <summary>Eye axes in world space from its view matrix (looks down -z).</summary>
        private static void EyeAxes(Camera cam, Camera.StereoscopicEye eye, out Vector3 r, out Vector3 u, out Vector3 f)
        {
            var toWorld = cam.GetStereoViewMatrix(eye).inverse;
            r = ((Vector3)toWorld.GetColumn(0)).normalized;
            u = ((Vector3)toWorld.GetColumn(1)).normalized;
            f = -((Vector3)toWorld.GetColumn(2)).normalized;
        }

        /// <summary>Only cameras with a post-processing stack. Cached per camera.</summary>
        private static bool DrawsFog(Camera cam)
        {
            if (fogCameras.TryGetValue(cam.Pointer, out bool has)) return has;
            if (fogCameras.Count > 64) fogCameras.Clear();
            has = cam.GetComponent<PostProcessLayer>() != null;
            fogCameras[cam.Pointer] = has;
            return has;
        }

        /// <summary>Far-plane corner, width and height for a projection, in the game's layout.</summary>
        private static void Write(Matrix4x4 p, Vector3 r, Vector3 u, Vector3 f, out Vector3 vx, out Vector3 vy, out Vector3 vz)
        {
            float d = cornerDepth;
            float left = d * (p.m02 - 1f) / p.m00, right = d * (p.m02 + 1f) / p.m00;
            float bottom = d * (p.m12 - 1f) / p.m11, top = d * (p.m12 + 1f) / p.m11;
            vx = r * (width >= 0f ? right - left : left - right);
            vy = u * (height >= 0f ? top - bottom : bottom - top);
            vz = f * d + r * (width >= 0f ? left : right) + u * (height >= 0f ? bottom : top);
        }

        /// <summary>Once: checks the game's vectors match the layout this rewrites, logs the frustums.</summary>
        private static void Check(Camera cam, Vector3 x, Vector3 y, Vector3 z, Vector3 r, Vector3 u, Vector3 f)
        {
            checkedLayout = true;
            float zr = Vector3.Dot(z, r), zu = Vector3.Dot(z, u);
            float err = Mathf.Max(Mathf.Max((x - r * width).magnitude / x.magnitude, (y - u * height).magnitude / y.magnitude),
                                  Mathf.Max(Mathf.Abs(zr + width / 2f) / Mathf.Abs(width), Mathf.Abs(zu + height / 2f) / Mathf.Abs(height)));
            layoutOk = err < 0.05f;
            float halfX = Mathf.Atan(Mathf.Abs(width) / 2f / cornerDepth) * Mathf.Rad2Deg, halfY = Mathf.Atan(Mathf.Abs(height) / 2f / cornerDepth) * Mathf.Rad2Deg;
            var left = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
            var right = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
            EyeAxes(cam, Camera.StereoscopicEye.Left, out _, out _, out Vector3 lf);
            EyeAxes(cam, Camera.StereoscopicEye.Right, out _, out _, out Vector3 rf);
            string line = $"Fog per eye on '{cam.name}': the game's fog frustum is centred, {halfX:0.#}/{halfX:0.#}/{halfY:0.#}/{halfY:0.#} degrees left/right/down/up (camera field of view {cam.fieldOfView:0.#}, aspect {cam.aspect:0.###}); " +
                          $"the eyes' frustums are left eye {Angles(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left))}, right eye {Angles(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right))}, " +
                          $"{((Vector3)(right - left)).magnitude * 1000f:0.#} mm apart, looking {Vector3.Angle(lf, f):0.#} and {Vector3.Angle(rf, f):0.#} degrees away from the head's direction. ";
            if (layoutOk) Plugin.L.LogInfo(line + $"The game's vectors fit that layout within {err:P1}: each eye now gets its own.");
            else Plugin.L.LogWarning(line + $"The game's vectors are off that layout by {err:P0}: left as the game made them.");
        }

        /// <summary>Half-angles left/right/down/up, in degrees.</summary>
        private static string Angles(Matrix4x4 p)
        {
            float l = Mathf.Atan((1f - p.m02) / p.m00) * Mathf.Rad2Deg, r = Mathf.Atan((1f + p.m02) / p.m00) * Mathf.Rad2Deg;
            float d = Mathf.Atan((1f - p.m12) / p.m11) * Mathf.Rad2Deg, u = Mathf.Atan((1f + p.m12) / p.m11) * Mathf.Rad2Deg;
            return $"{l:0.#}/{r:0.#}/{d:0.#}/{u:0.#}";
        }

        public static string Describe() => $"fog rays per eye {(Active ? "on" : broken ? "broken" : !layoutOk ? "off (the game's layout not recognised)" : "off")}: {Left} left and {Right} right eye pass(es)";
    }

    /// <summary>
    /// Hx Volumetric records its commands once per frame and both eyes replay them, so beams sat off their lamps.
    /// Multi-pass only: re-recorded per eye. Light shadow buffers are released first or they pile up.
    /// </summary>
    internal static class VolumetricPerEye
    {
        /// <summary>Dev switch (`fx hx off`) to compare with the game's single recording.</summary>
        public static bool Enabled = true;
        /// <summary>Dev check (`fx hx lights`): logs shadowed lights' command buffers.</summary>
        public static bool CountLightsNext;
        public static int Left, Right;
        private static readonly Dictionary<IntPtr, HxVolumetricCamera> volumetric = new Dictionary<IntPtr, HxVolumetricCamera>();
        private static readonly Dictionary<string, (long ticks, int frames, int lastFrame, bool logged)> cost = new Dictionary<string, (long, int, int, bool)>();
        private static bool broken, checkedRestore;

        public static void ClearCaches() => volumetric.Clear();

        public static void OnPreRender(Camera cam)
        {
            if (!Enabled || broken || !VRConfig.StereoFixes.Value || !VRSession.Active || !cam.stereoEnabled) return;
            if (UnityEngine.XR.XRSettings.stereoRenderingMode != UnityEngine.XR.XRSettings.StereoRenderingMode.MultiPass) return;
            var eye = cam.stereoActiveEye;
            if (eye == Camera.MonoOrStereoscopicEye.Mono) return;
            if (!volumetric.TryGetValue(cam.Pointer, out var hx))
            {
                if (volumetric.Count > 64) volumetric.Clear();
                hx = cam.GetComponent<HxVolumetricCamera>();
                volumetric[cam.Pointer] = hx;
            }
            if (hx == null || !hx.enabled) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool right = eye == Camera.MonoOrStereoscopicEye.Right;
            var which = right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            Matrix4x4 before = default;
            bool lent = false;
            try
            {
                if (right && !checkedRestore) before = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                Matrix4x4 p = cam.GetStereoProjectionMatrix(which), v = cam.GetStereoViewMatrix(which);
                ReleaseLightBuffers();
                // both slots get this eye, whichever one the recording reads
                lent = true;
                cam.SetStereoProjectionMatrix(Camera.StereoscopicEye.Left, p);
                cam.SetStereoProjectionMatrix(Camera.StereoscopicEye.Right, p);
                cam.SetStereoViewMatrix(Camera.StereoscopicEye.Left, v);
                cam.SetStereoViewMatrix(Camera.StereoscopicEye.Right, v);
                hx.BuildBuffer();
            }
            catch (Exception e)
            {
                Stop("recording: " + e.Message);
            }
            if (lent) Restore(cam);
            if (broken) return;
            if (right && !checkedRestore) CheckRestore(cam, before);
            if (right && CountLightsNext) { CountLightsNext = false; LogLightBuffers(); }
            Count(cam, right, start);
        }

        /// <summary>Puts back the headset's matrices, or the dev fake eyes (a reset would override them).</summary>
        private static void Restore(Camera cam)
        {
#if DEV
            bool fake;
            try { fake = FakeEyes.Reapply(cam); }
            catch (Exception e) { Stop("fake eyes back: " + e.Message); fake = false; }
            if (fake) return;
#endif
            try { cam.ResetStereoProjectionMatrices(); }
            catch (Exception e) { Stop("projections back: " + e.Message); }
            try { cam.ResetStereoViewMatrices(); }
            catch (Exception e) { Stop("eye positions back: " + e.Message); }
        }

        /// <summary>Once: checks the right eye's projection survives the lend and restore.</summary>
        private static void CheckRestore(Camera cam, Matrix4x4 before)
        {
            checkedRestore = true;
            var after = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            float diff = 0f;
            for (int i = 0; i < 16; i++) diff = Mathf.Max(diff, Mathf.Abs(after[i] - before[i]));
            if (diff > 1e-4f) Plugin.L.LogWarning($"Volumetric light per eye: the right eye's projection differs by {diff:0.####} after it was lent and put back.");
            else Plugin.L.LogInfo("Volumetric light per eye: the right eye's projection is the same after it was lent and put back.");
        }

        /// <summary>Drops each light's shadow commands before the next recording adds them again.</summary>
        private static void ReleaseLightBuffers()
        {
            var lights = HxVolumetricCamera.ActiveLights;
            if (lights == null) return;
            for (int i = 0; i < lights.Count; i++)
            {
                var l = lights[i];
                if (l != null) l.ReleaseBuffer();
            }
        }

        /// <summary>Command buffers per shadowed light (expect 1, 2 for a sun).</summary>
        private static void LogLightBuffers()
        {
            var lights = HxVolumetricCamera.ActiveLights;
            var sb = new System.Text.StringBuilder();
            int shadowed = 0, extra = 0;
            for (int i = 0; lights != null && i < lights.Count; i++)
            {
                var hl = lights[i];
                var l = hl != null ? hl.myLight : null;
                if (l == null || l.shadows == LightShadows.None || !hl.Shadows) continue;
                shadowed++;
                int n = l.commandBufferCount, want = l.type == LightType.Directional ? 2 : 1;
                if (n > want) extra++;
                if (shadowed <= 20) sb.Append($" '{l.name}' ({l.type}) {n}{(n > want ? $" (expected {want})" : "")};");
            }
            Plugin.L.LogInfo(shadowed == 0
                ? $"fx hx lights: {(lights != null ? lights.Count : 0)} volumetric light(s) active, none casting shadows here."
                : $"fx hx lights: {shadowed} shadowed volumetric light(s), command buffers read in the right eye:{sb} {(extra == 0 ? "none doubled" : $"{extra} with too many")}.");
        }

        private static void Stop(string why)
        {
            if (broken) return;
            broken = true;
            Plugin.L.LogWarning("Volumetric light per eye stopped (" + why + "); the game's single recording is used from now on.");
        }

        /// <summary>Cost, logged once per camera after 300 frames.</summary>
        private static void Count(Camera cam, bool right, long start)
        {
            if (right) Right++; else Left++;
            string name = cam.name;
            cost.TryGetValue(name, out var c);
            c.ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (Time.frameCount != c.lastFrame) { c.lastFrame = Time.frameCount; c.frames++; }
            if (!c.logged && c.frames >= 300)
            {
                c.logged = true;
                double ms = c.ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / c.frames;
                Plugin.L.LogInfo($"Stereo fixes: the volumetric light recorded for each eye on '{name}' (left and right eye passes seen), {ms:0.00} ms per frame.");
            }
            cost[name] = c;
        }

        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in cost)
                if (kv.Value.frames > 0) sb.Append($" '{kv.Key}' {kv.Value.ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / kv.Value.frames:0.00} ms per frame;");
            return $"volumetric light per eye {(broken ? "broken" : !Enabled ? "off (one recording for both eyes, as the game)" : "on (recorded again for each eye)")}: {Left} left and {Right} right eye pass(es);{sb}";
        }
    }
}
