using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Valve.VR;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// "Next district" card shown during the black of a district passage. A SteamVR overlay, since the compositor
    /// fade covers everything the game submits. Rendered once to a texture, placed where the head looked.
    /// </summary>
    internal static class PassageCard
    {
        private const string Key = "cloudpunkvr.passage";
        private const int Width = 1024, Height = 256, RenderFrames = 5;
        private const float MaxAlpha = 0.8f, WidthMeters = 0.75f, Distance = 2f, BelowEyes = 0.3f;
        private static readonly Color Amber = new Color32(245, 166, 35, 255);
        private static readonly Color Implant = new Color32(81, 168, 221, 255);
        private static readonly Color Band = new Color32(11, 18, 32, 255);

        private static bool failed, placed, shown;
        private static ulong overlay;
        private static GameObject rig;
        private static Camera cam;
        private static Canvas canvas;
        private static RenderTexture texture;
        private static IntPtr nativeTexture;
        private static int framesToRender;
        private static bool sendOnceMore;
        private static float alpha, setAlpha = -1f;
        private static HmdMatrix34_t pose;

        public static bool Showing => shown;

        public static void Begin()
        {
            if (failed) return;
            try
            {
                if (!Create()) return;
                placed = PlaceInFrontOfHead();
                alpha = 0f;
                framesToRender = RenderFrames;
            }
            catch (Exception e) { Fail(e.Message); }
        }

        /// <summary>Fades in after 0.5 s of black while the game is busy.</summary>
        public static void Update(bool gameBusy, float blackFor)
        {
            if (failed || !placed || rig == null) return;
            try
            {
                float target = gameBusy && blackFor > 0.5f ? MaxAlpha : 0f;
                float rate = target > alpha ? MaxAlpha / 0.3f : MaxAlpha / 0.2f;
                alpha = Mathf.MoveTowards(alpha, target, rate * Time.unscaledDeltaTime);
                if (Film.CleanFrame.HidesOverlays) alpha = 0f;
                Apply();
            }
            catch (Exception e) { Fail(e.Message); }
        }

        /// <summary>Hides at once, no fade.</summary>
        public static void End()
        {
            placed = false;
            alpha = 0f;
            if (failed || rig == null) return;
            try { Apply(); }
            catch (Exception e) { Fail(e.Message); }
        }

        public static void DestroyAtExit()
        {
            try { if (overlay != 0) OpenVR.Overlay?.DestroyOverlay(overlay); }
            catch (Exception) { /* SteamVR drops it with the process anyway */ }
            overlay = 0;
        }

        private static void Apply()
        {
            bool visible = alpha > 0.01f;
            // drawn in the first visible frames only, again after a device reset
            if (visible && !texture.IsCreated()) { texture.Create(); nativeTexture = IntPtr.Zero; framesToRender = RenderFrames; }
            bool render = visible && framesToRender > 0;
            if (render) framesToRender--;
            cam.enabled = render;
            canvas.enabled = render;
            bool send = visible && (render || sendOnceMore);
            sendOnceMore = render;
            var ov = OpenVR.Overlay;
            if (ov == null) return;
            if (visible)
            {
                if (send)
                {
                    if (nativeTexture == IntPtr.Zero) nativeTexture = texture.GetNativeTexturePtr();
                    var tex = new Texture_t { handle = nativeTexture, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
                    var sent = ov.SetOverlayTexture(overlay, ref tex);
                    if (sent != EVROverlayError.None) { Fail("SteamVR refused the card's texture: " + sent); return; }
                }
                if (Mathf.Abs(alpha - setAlpha) > 0.01f) { ov.SetOverlayAlpha(overlay, alpha); setAlpha = alpha; }
                if (!shown)
                {
                    ov.SetOverlayTransformAbsolute(overlay, VRSession.TrackingUniverse, ref pose);
                    var err = ov.ShowOverlay(overlay);
                    if (err != EVROverlayError.None) { Fail("SteamVR refused to show the card: " + err); return; }
                    shown = true;
                    TransitionGuard.Signal("card shown (SteamVR overlay None)");
                }
            }
            else if (shown)
            {
                var err = ov.HideOverlay(overlay);
                shown = false;
                setAlpha = -1f;
                TransitionGuard.Signal($"card hidden (SteamVR overlay {err})");
            }
        }

        /// <summary>2 m ahead, level, a bit below the eyes, in SteamVR room space.</summary>
        private static bool PlaceInFrontOfHead()
        {
            var sys = OpenVR.System;
            if (sys == null) return false;
            var poses = new TrackedDevicePose_t[1];
            sys.GetDeviceToAbsoluteTrackingPose(VRSession.TrackingUniverse, 0f, poses);
            if (!poses[0].bPoseIsValid) return false;
            var m = poses[0].mDeviceToAbsoluteTracking;
            // OpenVR: right-handed, +Y up, head looks down -Z
            var head = new Vector3(m.m3, m.m7, m.m11);
            var forward = new Vector3(-m.m2, 0f, -m.m10);
            if (forward.sqrMagnitude < 1e-6f) forward = new Vector3(0f, 0f, -1f);
            forward.Normalize();
            var at = head + forward * Distance + Vector3.down * BelowEyes;
            // overlay faces +Z, so point it back at the eyes
            var z = (head - at).normalized;
            var x = Vector3.Cross(Vector3.up, z).normalized;
            var y = Vector3.Cross(z, x);
            pose = new HmdMatrix34_t
            {
                m0 = x.x, m1 = y.x, m2 = z.x, m3 = at.x,
                m4 = x.y, m5 = y.y, m6 = z.y, m7 = at.y,
                m8 = x.z, m9 = y.z, m10 = z.z, m11 = at.z,
            };
            shown = false; // re-placed on next show
            return true;
        }

        private static bool Create()
        {
            if (rig != null) return true;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new NotSupportedException("the card is handed to SteamVR as a Direct3D 11 texture, the game runs " + SystemInfo.graphicsDeviceType);
            var ov = OpenVR.Overlay;
            if (ov == null) throw new InvalidOperationException("SteamVR's overlay interface is not available");
            // may still exist from an earlier game start in this SteamVR session
            var err = ov.FindOverlay(Key, ref overlay);
            if (err != EVROverlayError.None || overlay == 0) err = ov.CreateOverlay(Key, "Cloudpunk VR passage", ref overlay);
            if (err != EVROverlayError.None) throw new InvalidOperationException("SteamVR refused the overlay: " + err);
            ov.SetOverlayWidthInMeters(overlay, WidthMeters);
            // D3D11 render textures come out upside down, flip V
            var bounds = new VRTextureBounds_t { uMin = 0f, vMin = 1f, uMax = 1f, vMax = 0f };
            ov.SetOverlayTextureBounds(overlay, ref bounds);

            texture = new RenderTexture(Width, Height, 16, RenderTextureFormat.ARGB32) { name = "CloudpunkVR_PassageCard", antiAliasing = 1 };
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            texture.Create();
            framesToRender = RenderFrames;

            int layer = FreeLayer();
            rig = new GameObject("CloudpunkVR_PassageCard") { layer = layer };
            UnityEngine.Object.DontDestroyOnLoad(rig);
            // far below the city, on a layer no game camera draws
            rig.transform.position = new Vector3(0f, -5000f, 0f);

            var camGo = new GameObject("Camera") { layer = layer };
            camGo.transform.SetParent(rig.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0f, -10f);
            cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = Height / 2f;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 20f;
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.targetTexture = texture;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.useOcclusionCulling = false;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.depth = -100f;
            cam.enabled = false;

            var canvasGo = new GameObject("Canvas") { layer = layer };
            canvasGo.transform.SetParent(rig.transform, false);
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.enabled = false;
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(Width, Height);

            Box(canvasGo.transform, layer, "Band", new Color(Band.r, Band.g, Band.b, 0.92f), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Box(canvasGo.transform, layer, "Line", Implant, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -5f), Vector2.zero);
            var font = HudFont();
            Text(canvasGo.transform, layer, font, 40f, new Color(Implant.r, Implant.g, Implant.b, 0.9f), new Vector2(0f, 0.62f), new Vector2(1f, 0.95f)).text = "DISTRICT PASSAGE";
            Text(canvasGo.transform, layer, font, 92f, Amber, new Vector2(0f, 0.08f), new Vector2(1f, 0.66f)).text = "Next district";
            Plugin.L.LogInfo($"Passage card ready (SteamVR overlay, font {(font != null ? font.name : "TextMeshPro's default")}, layer {layer}).");
            return true;
        }

        private static void Box(Transform parent, int layer, string name, Color color, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
        }

        private static TextMeshProUGUI Text(Transform parent, int layer, TMP_FontAsset font, float size, Color color, Vector2 min, Vector2 max)
        {
            var go = new GameObject("Text") { layer = layer };
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = new Vector2(40f, 0f);
            rt.offsetMax = new Vector2(-40f, 0f);
            return t;
        }

        private static TMP_FontAsset HudFont()
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && f.name == "Aquawax-Pro-DemiBold") return f;
            return null;
        }

        /// <summary>A layer no camera draws and VRUI doesn't use.</summary>
        private static int FreeLayer()
        {
            int used = 1 << UI.VRUI.Layer;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>()) used |= c.cullingMask;
            foreach (int l in new[] { 29, 27, 26, 7, 6, 3 })
                if ((used & (1 << l)) == 0) return l;
            return 29;
        }

        private static void Fail(string why)
        {
            failed = true;
            Plugin.L.LogWarning("Passage card off for this session: " + why);
            TransitionGuard.Signal("card off: " + why);
            try
            {
                if (overlay != 0) OpenVR.Overlay?.HideOverlay(overlay);
                if (cam != null) cam.enabled = false;
                if (canvas != null) canvas.enabled = false;
            }
            catch (Exception) { /* nothing more to undo */ }
        }
    }
}
