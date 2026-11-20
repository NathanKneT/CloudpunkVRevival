using System;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Runs the pose update once per frame, after LateUpdate and before the first camera renders.
    /// Application.onBeforeRender is stripped from this build, so it uses Camera.onPreCull and a 1x1 offscreen
    /// camera's OnPreCull (offscreen cameras render first). Whichever fires first wins.
    /// </summary>
    public class RenderHook : MonoBehaviour
    {
        public RenderHook(IntPtr ptr) : base(ptr) { }

        public static event Action BeforeRender;

        /// <summary>Each camera before culling, after BeforeRender (global callback only).</summary>
        public static event Action<Camera> PreCull;

        public static event Action<Camera> PostRender;

        /// <summary>Each camera after culling (kept last in Camera.onPreRender).</summary>
        public static event Action<Camera> PreRender;
        private static int lastFrame = -1;
        private static Camera.CameraCallback preCull, postRender, preRender;
        private static float nextCallbackCheck;
        private static int postErrors;
        private static bool loggedFirst;

        public static void Install(GameObject parent)
        {
            var go = new GameObject("CloudpunkVR_RenderHook");
            go.transform.SetParent(parent.transform, false);
            var cam = go.AddComponent<Camera>();
            var rt = new RenderTexture(1, 1, 0) { name = "CloudpunkVR_RenderHook" };
            rt.hideFlags = HideFlags.DontUnloadUnusedAsset;
            cam.targetTexture = rt;
            cam.cullingMask = 0;
            cam.clearFlags = CameraClearFlags.Nothing;
            cam.depth = -1000f;
            cam.useOcclusionCulling = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            go.AddComponent<RenderHook>();
        }

        /// <summary>Re-registers our global callbacks (other code may reassign the delegates).</summary>
        public static void EnsureGlobalCallback()
        {
            try
            {
                if (preCull == null) preCull = (Camera.CameraCallback)new Action<Camera>(OnAnyPreCull);
                var current = Camera.onPreCull;
                Camera.onPreCull = current == null ? preCull : (current - preCull) + preCull;
            }
            catch (Exception e)
            {
                if (preCull != null) Plugin.L.LogWarning("Camera.onPreCull hook unavailable: " + e.Message);
                preCull = null;
            }
            // the other two once a second: each reassignment makes Il2Cpp delegates
            if (Time.unscaledTime < nextCallbackCheck) return;
            nextCallbackCheck = Time.unscaledTime + 1f;
            if (!postRenderFailed)
            {
                try
                {
                    if (postRender == null) postRender = (Camera.CameraCallback)new Action<Camera>(OnAnyPostRender);
                    var current = Camera.onPostRender;
                    Camera.onPostRender = current == null ? postRender : (current - postRender) + postRender;
                }
                catch (Exception e)
                {
                    postRenderFailed = true;
                    Plugin.L.LogWarning("Camera.onPostRender hook unavailable: " + e.Message);
                }
            }
            if (!preRenderFailed)
            {
                try
                {
                    if (preRender == null) preRender = (Camera.CameraCallback)new Action<Camera>(OnAnyPreRender);
                    var current = Camera.onPreRender;
                    Camera.onPreRender = current == null ? preRender : (current - preRender) + preRender;
                }
                catch (Exception e)
                {
                    preRenderFailed = true;
                    Plugin.L.LogWarning("Camera.onPreRender hook unavailable: " + e.Message);
                }
            }
        }

        private static bool preRenderFailed;

        private static void OnAnyPreRender(Camera cam)
        {
            try { PreRender?.Invoke(cam); }
            catch (Exception e) { if (postErrors++ < 10) Plugin.L.LogError("Camera pre-render: " + e); }
        }

        private static bool postRenderFailed;

        private static void OnAnyPostRender(Camera cam)
        {
            try { PostRender?.Invoke(cam); }
            catch (Exception e) { if (postErrors++ < 10) Plugin.L.LogError("Camera post-render: " + e); }
        }

        private void OnPreCull() => Run();

        private static int preCullErrors;

        private static void OnAnyPreCull(Camera cam)
        {
            Run();
            try { PreCull?.Invoke(cam); }
            catch (Exception e) { if (preCullErrors++ < 10) Plugin.L.LogError("Camera pre-cull: " + e); }
        }

        private static void Run()
        {
            int f = Time.frameCount;
            if (f == lastFrame) return;
            lastFrame = f;
            if (!loggedFirst) { loggedFirst = true; Plugin.L.LogInfo("Render hook active."); }
            BeforeRender?.Invoke();
        }
    }
}
