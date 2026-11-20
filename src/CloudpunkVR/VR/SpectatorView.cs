using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Valve.VR;
using N = System.Numerics;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Steady crop of the left eye in the game window, for recording (Unity's mirror shakes with the head).
    /// The eye image is copied once a frame and filmed by our own camera, smoothed and level.
    /// </summary>
    internal static partial class SpectatorView
    {
        public static bool On { get; private set; }

        // far below the city, the studio camera sees only its quad. At 100 m, float rounding this far out (~2 mm)
        // moves the picture 0.01 px (6 px at 1 m).
        private static readonly Vector3 StudioPosition = new Vector3(0f, -20000f, 0f);
        private const float QuadDistance = 100f;

        private static GameObject root;
        private static Camera cam, clearCam;
        private static Transform camTransform;
        private static Mesh quadMesh;
        private static Material material;
        private static int layer = -1;
        private static bool hooked, started, callbackFailed;
        private static int errors, callbackErrors;

        // left eye copy: command buffer on the overlay camera, left pass only
        private static RenderTexture copy;
        private static int copyW, copyH;
        private static bool copyMips;
        private static CommandBuffer copyBuffer;
        private static Camera bufferOn;
        private static IntPtr overlayPtr, cameraPtr;
        private static RenderTargetIdentifier eyeTarget, copyTarget;
        private static bool recordedCopy, preRenderSeen, copyEveryPass;
        private static int copies, copiesAtReport;
        private static float onSince;

        // copied eye: frustum tangents (l, r, b, t) and its turn vs the head
        private static bool eyeKnown;
        private static float eL, eR, eB, eT;
        private static N.Quaternion eyeInHead = N.Quaternion.Identity;
        private static N.Vector3 eyeOffset; // eye position in the head's frame, m
        private static int nextEyeSample;

        // SteamVR visible area (eye UV line loop) in tangents, read both ways up, and the largest box inside it
        private static N.Vector2[] loopUv, loopA, loopB;
        private static bool loopTried;
        private static float safeX0, safeX1, safeY0, safeY1;

        // picture half sizes (tangents), fov, window pixels
        private static float hx, hy, fovUsed, fovMax;
        private static float restLeft, restRight, restDown, restUp, restBias;
        private static int picW, picH, winW, winH;
        private static SpectatorFormat fitFormat;
        private static float fitFov = -1f, fitTilt;
        private static float fitL, fitR, fitB, fitT;
        private static N.Quaternion fitEye;
        private static bool turned;

        // smoothed direction in the anchor's frame (deg) and speed
        private static float yaw, pitch, yawSpeed, pitchSpeed, lastAimYaw, lastAimPitch, rateYaw, ratePitch, rateYaw2, ratePitch2;
        private static bool settled, refitted;
        private static ViewMode lastMode;
        private static int lastSpace;
        private static N.Quaternion viewRotation = N.Quaternion.Identity;
        private static float lastFade = -1f, lagYaw, lagPitch, roomYaw, roomPitch;

        private static bool black;
        private static float blackSince;
        private static int blackLogs, blackWhy;
        private static bool fadeBroken;

        // dev switches
        private static bool flip, keepDeviceView, announce;

        // Test hooks: filled in by SpectatorView.Dev.cs in DEV builds, stand-in fields otherwise.
        static partial void DevFrame();
        static partial void DevOff();
        static partial void UpdateShake();
#if !DEV
        private static readonly bool holding;
        private static readonly float holdYaw, holdPitch, traceUntil;
        private static readonly int pixelFrames;
        private static int pixelGaveWay;
#endif

        // ---- Each frame.

        /// <summary>Each frame in VR, after CameraDriver and the headset fade.</summary>
        public static void Update()
        {
            PollKey();
            UpdateShake();
            if (!started)
            {
                started = true;
                if (VRConfig.SpectatorEnabled.Value) Switch(true, "Enabled = true");
            }
            if (!On) return;
            if (callbackFailed) { Switch(false, "errors in its render callbacks"); return; }
            long t0 = Stopwatch.GetTimestamp();
            try { Frame(); }
            catch (Exception e)
            {
                if (errors++ < 10) Plugin.L.LogError("Spectator view: " + e);
                if (errors == 10) Switch(false, "too many errors");
            }
            cpuTicks += Stopwatch.GetTimestamp() - t0;
            cpuFrames++;
            DevFrame();
            if (Time.unscaledTime >= nextReport) Report();
        }

        /// <summary>Render callback error: log a few, switch off next frame.</summary>
        private static void CallbackError(string where, Exception e)
        {
            if (callbackErrors++ < 5) Plugin.L.LogError($"Spectator view, {where}: {e}");
            callbackFailed = true;
        }

        private static void Frame()
        {
            if (!Build()) return;
            if (!shown) Show(true);
            Attach();
            if (!preRenderSeen && !copyEveryPass && Time.unscaledTime - onSince > 2f) EveryPassFallback();
            if (!eyeKnown) return; // first frame drawn brings the left pass's matrices
            Fit();
            PlaceInWindow();
            EnsureCopy();
            float fade = Fade();
            Steer(fade);
            Shade(fade);
            Measure(fade);
            if (announce && copies > 0) Announce();
        }

        // ---- Switching on and off.

        public static void Switch(bool on, string why)
        {
            if (on == On) return;
            if (on)
            {
                var mode = XRSettings.StereoRenderingMode.MultiPass;
                try { mode = XRSettings.stereoRenderingMode; }
                catch (Exception e) { Plugin.L.LogWarning("Spectator view: the stereo rendering mode is unknown, taken as MultiPass: " + e.Message); }
                if (mode != XRSettings.StereoRenderingMode.MultiPass)
                {
                    Plugin.L.LogWarning($"Spectator view not switched on: it copies the left eye's own pass, and the stereo rendering is {mode}, not MultiPass.");
                    return;
                }
                if (why == "key") ReadSettingsAgain();
                On = true;
                settled = false;
                announce = true;
                copies = copiesAtReport = 0;
                copyFrame = -1;
                onSince = Time.unscaledTime;
                errors = callbackErrors = 0;
                callbackFailed = false;
                preRenderSeen = false;
                eyeKnown = false; // re-read from the next frame drawn
                if (copyEveryPass)
                {
                    copyEveryPass = false;
                    copiedEye = EVREye.Eye_Left;
                    loopTried = false;
                    loopUv = null;
                }
                cpuTicks = 0; cpuFrames = 0;
                nextReport = Time.unscaledTime + 30f;
                ResetMotion();
                ResetPeriod();
                fitFov = -1f;
                if (Build()) Show(true); // else once the overlay camera exists
                Plugin.L.LogInfo($"Spectator view: switching on ({why}).");
            }
            else
            {
                On = false;
                black = false;
                DevOff();
                if (root != null) Show(false);
                Detach();
                if (copy != null)
                {
                    material.mainTexture = null;
                    copy.Release();
                    UnityEngine.Object.Destroy(copy);
                    copy = null;
                    copyW = copyH = 0;
                }
                Plugin.L.LogInfo($"Spectator view off ({why}): {PeriodSummary()}; the headset's mirror is back in the window.");
            }
        }

        private static bool shown;

        private static void Show(bool show)
        {
            shown = show;
            if (cam != null) cam.enabled = show;
            if (clearCam != null) clearCam.enabled = false; // PlaceInWindow enables it when there are bars
            winW = winH = 0;
            SetDeviceView(!show || keepDeviceView);
        }

        private static void SetDeviceView(bool show)
        {
            try { XRSettings.showDeviceView = show; }
            catch (Exception e) { if (!deviceViewWarned) { deviceViewWarned = true; Plugin.L.LogWarning("Spectator view: Unity's mirror could not be switched: " + e.Message); } }
        }

        private static bool deviceViewWarned;

        // ---- The objects.

        private static bool Build()
        {
            if (root != null) return true;
            if (CameraDriver.OverlayCamera == null) return false; // VRUI first: its layer is taken then
            layer = PickLayer();
            root = new GameObject("CloudpunkVR_Spectator");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.transform.SetPositionAndRotation(StudioPosition, Quaternion.identity);

            quadMesh = new Mesh { name = "CloudpunkVR_SpectatorEye", hideFlags = HideFlags.DontUnloadUnusedAsset };
            var quad = new GameObject("CloudpunkVR_SpectatorEye") { layer = layer };
            quad.transform.SetParent(root.transform, false);
            quad.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            var mr = quad.AddComponent<MeshRenderer>();
            // UI/Default blends by alpha and the eye's alpha is junk: push sampled alpha to 1+
            material = new Material(Shader.Find("UI/Default")) { name = "CloudpunkVR_Spectator", hideFlags = HideFlags.DontUnloadUnusedAsset };
            material.SetVector("_TextureSampleAdd", new Vector4(0f, 0f, 0f, 1f));
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.allowOcclusionWhenDynamic = false;

            cam = MakeCamera("CloudpunkVR_SpectatorCamera", 200f, 1 << layer);
            camTransform = cam.transform;
            cameraPtr = cam.Pointer;
            clearCam = MakeCamera("CloudpunkVR_SpectatorBars", 199f, 0);

            copyBuffer = new CommandBuffer { name = "CloudpunkVR spectator copy" };
            eyeTarget = BuiltinRenderTextureType.CameraTarget;
            if (!hooked)
            {
                hooked = true;
                RenderHook.PreRender += OnPreRender;
                RenderHook.PreCull += OnPreCull;
            }
            Plugin.L.LogInfo($"Spectator view: its camera and the eye's quad made on layer {layer}, {StudioPosition.y:0} m below the city.");
            return true;
        }

        /// <summary>Window camera (not the headset), drawn after the headset's.</summary>
        private static Camera MakeCamera(string name, float depth, int mask)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var c = go.AddComponent<Camera>();
            c.stereoTargetEye = StereoTargetEyeMask.None;
            c.targetTexture = null;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = Color.black;
            c.cullingMask = mask;
            c.depth = depth;
            c.nearClipPlane = QuadDistance * 0.1f;
            c.farClipPlane = QuadDistance * 5f;
            c.renderingPath = RenderingPath.Forward;
            c.allowHDR = false;
            c.allowMSAA = false;
            c.useOcclusionCulling = false;
            XRDevice.DisableAutoXRCameraTracking(c, true);
            c.enabled = false;
            return c;
        }

        /// <summary>A layer no other camera draws, else the overlay's (the studio is far from its view).</summary>
        private static int PickLayer()
        {
            int used = 0;
            var cameras = Resources.FindObjectsOfTypeAll<Camera>();
            for (int i = 0; i < cameras.Length; i++) used |= cameras[i].cullingMask;
            foreach (int l in new[] { 29, 28, 25, 24, 23, 22 })
                if (l != UI.VRUI.Layer && (used & (1 << l)) == 0 && string.IsNullOrEmpty(LayerMask.LayerToName(l))) return l;
            return UI.VRUI.Layer;
        }

        private static void Attach()
        {
            var overlay = CameraDriver.OverlayCamera;
            if (overlay == null || (bufferOn != null && overlayPtr == overlay.Pointer)) return;
            Detach();
            overlay.AddCommandBuffer(CameraEvent.AfterEverything, copyBuffer);
            bufferOn = overlay;
            overlayPtr = overlay.Pointer;
            recordedCopy = false;
            copyBuffer.Clear();
            if (copyEveryPass) Record(true);
        }

        private static void Detach()
        {
            if (bufferOn != null)
            {
                try { bufferOn.RemoveCommandBuffer(CameraEvent.AfterEverything, copyBuffer); }
                catch (Exception e) { Plugin.L.LogWarning("Spectator view: the copy could not be taken off the overlay camera: " + e.Message); }
            }
            bufferOn = null;
            overlayPtr = IntPtr.Zero;
            recordedCopy = false;
            copyBuffer?.Clear();
        }

        /// <summary>Eye-sized copy, mipmapped when shown smaller than 1:1.</summary>
        private static void EnsureCopy()
        {
            int w = XRSettings.eyeTextureWidth, h = XRSettings.eyeTextureHeight;
            if (w <= 0 || h <= 0) return;
            bool mips = picW > 0 && hx > 0f && w / (eR - eL) > 1.4f * picW / (2f * hx);
            if (copy != null && w == copyW && h == copyH && mips == copyMips) return;
            if (copy != null)
            {
                copy.Release();
                UnityEngine.Object.Destroy(copy);
            }
            copy = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "CloudpunkVR_SpectatorEye",
                useMipMap = mips,
                autoGenerateMips = mips,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
            copy.Create();
            copyW = w;
            copyH = h;
            copyMips = mips;
            copyTarget = (Texture)copy;
            material.mainTexture = copy;
            recordedCopy = false;
            copyBuffer.Clear();
            if (copyEveryPass) Record(true);
            if (copyLogs++ < 8) Plugin.L.LogInfo($"Spectator view: the left eye's image copied at {w}x{h}{(mips ? ", mipmapped (the picture shows it smaller)" : "")}.");
        }

        private static int copyLogs;

        // ---- The copy, per eye.

        /// <summary>Overlay's left pass: copy the eye and sample its frustum.</summary>
        private static void OnPreRender(Camera c)
        {
            if (!On || copyEveryPass || callbackFailed || c == null) return;
            try
            {
                IntPtr p = c.Pointer;
                if (p == overlayPtr)
                {
                    preRenderSeen = true;
                    bool left = c.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left;
                    Record(left);
                    if (!left) return;
                    if (copy != null)
                    {
                        copies++;
                        copyFrame = Time.frameCount;
                        copiedHead = Q(CameraDriver.HeadWorldRotation); // overlay camera pose for this image
                        copiedAnchor = Q(CameraDriver.AnchorRotation);
                    }
                    if (Time.frameCount >= nextEyeSample) SampleEye(c);
                }
            }
            catch (Exception e) { CallbackError("before an eye is drawn", e); }
        }

        /// <summary>Copy on the left pass only: one copy per frame.</summary>
        private static void Record(bool copyIt)
        {
            copyIt &= copy != null;
            if (copyIt == recordedCopy) return;
            copyBuffer.Clear();
            if (copyIt)
            {
                copyBuffer.Blit(eyeTarget, copyTarget);
                copyBuffer.SetRenderTarget(eyeTarget);
            }
            recordedCopy = copyIt;
        }

        /// <summary>No pre-render callback: copy at both passes (the right one stays) and use the right eye's frustum.</summary>
        private static void EveryPassFallback()
        {
            copyEveryPass = true;
            recordedCopy = false;
            if (copy != null) Record(true);
            Plugin.L.LogWarning("Spectator view: no pre-render callback from Unity; the eye is copied at both passes and the last one shown.");
            copiedEye = EVREye.Eye_Right;
            loopTried = false;
            loopUv = null;
            var overlay = CameraDriver.OverlayCamera;
            if (overlay != null) SampleEye(overlay, Camera.StereoscopicEye.Right, false);
        }

        private static EVREye copiedEye = EVREye.Eye_Left;

        /// <summary>Eye frustum and turn vs the head. Outside a render the view matrix may be stale: eye looks where the head does.</summary>
        private static void SampleEye(Camera c, Camera.StereoscopicEye which = Camera.StereoscopicEye.Left, bool drawing = true)
        {
            var p = c.GetStereoNonJitteredProjectionMatrix(which);
            if (p.m00 == 0f || p.m11 == 0f) return;
            float l = (p.m02 - 1f) / p.m00, r = (p.m02 + 1f) / p.m00, b = (p.m12 - 1f) / p.m11, t = (p.m12 + 1f) / p.m11;
            var inHead = N.Quaternion.Identity;
            if (drawing)
            {
                // rows are the eye's axes: no inverse, it rounds badly at the city's large coordinates
                var v = c.GetStereoViewMatrix(which);
                var right = new N.Vector3(v.m00, v.m01, v.m02);
                var up = new N.Vector3(v.m10, v.m11, v.m12);
                var back = new N.Vector3(v.m20, v.m21, v.m22);
                if (up.LengthSquared() < 1e-8f || back.LengthSquared() < 1e-8f) return;
                var head = c.transform;
                var headInv = N.Quaternion.Conjugate(Q(head.rotation));
                inHead = headInv * Look(-back, up);
                var eye = -(right * v.m03 + up * v.m13 + back * v.m23); // eye position
                eyeOffset = N.Vector3.Transform(eye - V(head.position), headInv);
            }
            // refit only on a real eye change (lens settings), not rounding
            bool changed = !eyeKnown || Mathf.Abs(l - eL) > 1e-3f || Mathf.Abs(r - eR) > 1e-3f || Mathf.Abs(b - eB) > 1e-3f
                           || Mathf.Abs(t - eT) > 1e-3f || Angle(inHead, eyeInHead) > 0.5f;
            nextEyeSample = Time.frameCount + 45;
            if (!changed) return;
            eL = l; eR = r; eB = b; eT = t;
            eyeInHead = inHead;
            eyeKnown = true;
            Quad();
            Loop();
        }

        /// <summary>Eye image on its frustum, QuadDistance ahead.</summary>
        private static void Quad()
        {
            float v0 = flip ? 1f : 0f, v1 = flip ? 0f : 1f, d = QuadDistance;
            quadMesh.vertices = new[] { new Vector3(eL * d, eB * d, d), new Vector3(eR * d, eB * d, d), new Vector3(eR * d, eT * d, d), new Vector3(eL * d, eT * d, d) };
            quadMesh.uv = new[] { new Vector2(0f, v0), new Vector2(1f, v0), new Vector2(1f, v1), new Vector2(0f, v1) };
            quadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            quadMesh.RecalculateBounds();
        }

        /// <summary>SteamVR hidden-area line loop, read once. Unsure which way up, so a point must be inside both.</summary>
        private static void Loop()
        {
            if (!loopTried)
            {
                var sys = OpenVR.System;
                if (sys == null) return;
                loopTried = true;
                try
                {
                    var m = sys.GetHiddenAreaMesh(copiedEye, EHiddenAreaMeshType.k_eHiddenAreaMesh_LineLoop);
                    int n = (int)m.unTriangleCount;
                    if (m.pVertexData != IntPtr.Zero && n >= 3 && n <= 4096)
                    {
                        loopUv = new N.Vector2[n];
                        int size = Marshal.SizeOf<HmdVector2_t>();
                        for (int i = 0; i < n; i++)
                        {
                            var v = Marshal.PtrToStructure<HmdVector2_t>(m.pVertexData + i * size);
                            loopUv[i] = new N.Vector2(v.v0, v.v1);
                        }
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("Spectator view: SteamVR's visible area unavailable, the eye's whole image counts: " + e.Message); }
            }
            loopA = loopB = null;
            safeX0 = eL; safeX1 = eR; safeY0 = eB; safeY1 = eT;
            if (loopUv == null) return;
            loopA = new N.Vector2[loopUv.Length];
            loopB = new N.Vector2[loopUv.Length];
            for (int i = 0; i < loopUv.Length; i++)
            {
                float x = eL + loopUv[i].X * (eR - eL);
                loopA[i] = new N.Vector2(x, eT - loopUv[i].Y * (eT - eB));
                loopB[i] = new N.Vector2(x, eB + loopUv[i].Y * (eT - eB));
            }
            // box: frustum shrunk to its centre until the edges are inside both loops
            float cx = (eL + eR) * 0.5f, cy = (eB + eT) * 0.5f;
            safeX0 = safeX1 = cx;
            safeY0 = safeY1 = cy;
            for (float s = 1f; s > 0.2f; s -= 0.01f)
            {
                float x0 = cx + (eL - cx) * s, x1 = cx + (eR - cx) * s, y0 = cy + (eB - cy) * s, y1 = cy + (eT - cy) * s;
                bool inside = true;
                for (int k = 0; k <= 20 && inside; k++)
                {
                    float a = k / 20f, x = x0 + (x1 - x0) * a, y = y0 + (y1 - y0) * a;
                    inside = Visible(x, y0) && Visible(x, y1) && Visible(x0, y) && Visible(x1, y);
                }
                if (!inside) continue;
                safeX0 = x0; safeX1 = x1; safeY0 = y0; safeY1 = y1;
                break;
            }
        }

        private static bool Visible(float x, float y) => loopA == null || (Inside(loopA, x, y) && Inside(loopB, x, y));

        private static bool Inside(N.Vector2[] poly, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                N.Vector2 a = poly[i], b = poly[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        // ---- What fits in the eye's image.

        // edge points in half sizes: -1, -0.5, 0, 0.5 along each side
        private static readonly N.Vector2[] Edge = MakeEdge();

        private static N.Vector2[] MakeEdge()
        {
            var s = new N.Vector2[16];
            int i = 0;
            for (int k = 0; k < 4; k++)
            {
                float a = -1f + k * 0.5f;
                s[i++] = new N.Vector2(a, -1f);
                s[i++] = new N.Vector2(1f, a);
                s[i++] = new N.Vector2(-a, 1f);
                s[i++] = new N.Vector2(-1f, -a);
            }
            return s;
        }

        /// <summary>Picture fits in the visible eye image, 2 px in.</summary>
        private static bool Fits(N.Quaternion eyeFromPicture, float sx, float sy)
        {
            float padX = 2f * (eR - eL) / Math.Max(copyW, 1000), padY = 2f * (eT - eB) / Math.Max(copyH, 1000);
            for (int i = 0; i < Edge.Length; i++)
            {
                N.Vector3 d = N.Vector3.Transform(new N.Vector3(Edge[i].X * sx, Edge[i].Y * sy, 1f), eyeFromPicture);
                if (d.Z < 0.05f) return false;
                float x = d.X / d.Z, y = d.Y / d.Z;
                if (x < eL + padX || x > eR - padX || y < eB + padY || y > eT - padY) return false;
                if (loopA != null && (x < safeX0 || x > safeX1 || y < safeY0 || y > safeY1) && !Visible(x, y)) return false;
            }
            return true;
        }

        /// <summary>Fits with the head straight ahead at this fov, turned by these offsets (deg).</summary>
        private static bool FitsAtRest(float fov, float yawOff, float pitchOff)
        {
            Half(fov, out float sx, out float sy);
            var pic = Euler(VRConfig.SpectatorTilt.Value - pitchOff, yawOff);
            return Fits(N.Quaternion.Conjugate(eyeInHead) * pic, sx * 1.01f, sy * 1.01f);
        }

        private static void Half(float fov, out float sx, out float sy)
        {
            float l = MathF.Tan(fov * (MathF.PI / 360f)), s = l * 9f / 16f;
            bool landscape = VRConfig.SpectatorFormat.Value == SpectatorFormat.Landscape;
            sx = landscape ? l : s;
            sy = landscape ? s : l;
        }

        /// <summary>Fov and room at rest, on settings or eye change.</summary>
        private static void Fit()
        {
            var format = VRConfig.SpectatorFormat.Value;
            float want = VRConfig.SpectatorFov.Value, tilt = VRConfig.SpectatorTilt.Value;
            if (format == fitFormat && want == fitFov && tilt == fitTilt && eL == fitL && eR == fitR && eB == fitB && eT == fitT && eyeInHead == fitEye) return;
            bool first = fitFov < 0f;
            fitFormat = format; fitFov = want; fitTilt = tilt; fitL = eL; fitR = eR; fitB = eB; fitT = eT; fitEye = eyeInHead;
            turned = format == SpectatorFormat.PortraitTurned;
            float lo = 5f, hi = 170f;
            if (!FitsAtRest(lo, 0f, 0f)) hi = lo;
            for (int i = 0; i < 14; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (FitsAtRest(mid, 0f, 0f)) lo = mid; else hi = mid;
            }
            fovMax = lo;
            fovUsed = Math.Min(want, fovMax);
            Half(fovUsed, out hx, out hy);
            // eyes reach further out than in: rest a few degrees toward the wider side so smoothing has room both ways
            restBias = 0f;
            restBias = Math.Clamp((RestRoom(1f, 0f) - RestRoom(-1f, 0f)) * 0.5f, -MaxBias, MaxBias);
            restLeft = RestRoom(-1f, 0f);
            restRight = RestRoom(1f, 0f);
            restDown = RestRoom(0f, -1f);
            restUp = RestRoom(0f, 1f);
            winW = winH = 0;
            refitted = true; // aim may move with the bias
            if (!first && !announce)
                Plugin.L.LogInfo($"Spectator view: now {Shape()}.");
        }

        private const float MaxBias = 4f;

        /// <summary>How far the resting picture can lag one way before leaving the eye image.</summary>
        private static float RestRoom(float yawSign, float pitchSign)
        {
            float lo = 0f, hi = 60f;
            if (!FitsAtRest(fovUsed, restBias, 0f)) return 0f;
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (FitsAtRest(fovUsed, restBias + yawSign * mid, pitchSign * mid)) lo = mid; else hi = mid;
            }
            return lo;
        }

        // ---- The window.

        private static void PlaceInWindow()
        {
            int w = Screen.width, h = Screen.height;
            if (w == winW && h == winH) return;
            winW = w;
            winH = h;
            if (w < 16 || h < 16) return;
            // a turned picture's width runs along the window's height
            float halfW = turned ? hy : hx, halfH = turned ? hx : hy, aspect = halfW / halfH;
            Rect r = (float)w / h > aspect ? new Rect((1f - h * aspect / w) * 0.5f, 0f, h * aspect / w, 1f) : new Rect(0f, (1f - w / aspect / h) * 0.5f, 1f, w / aspect / h);
            cam.rect = r;
            cam.aspect = aspect;
            cam.fieldOfView = 2f * Mathf.Atan(halfH) * Mathf.Rad2Deg;
            int rw = Mathf.RoundToInt(r.width * w), rh = Mathf.RoundToInt(r.height * h);
            picW = turned ? rh : rw;
            picH = turned ? rw : rh;
            bool bars = r.width < 0.999f || r.height < 0.999f;
            clearCam.enabled = bars;
            if (!announce && (w != loggedW || h != loggedH) && windowLogs++ < 10) Plugin.L.LogInfo($"Spectator view: the window is now {w}x{h}: {Pixels()}.");
            loggedW = w;
            loggedH = h;
        }

        private static int windowLogs, loggedW, loggedH;

        private static string Pixels()
        {
            var format = VRConfig.SpectatorFormat.Value;
            bool landscape = format == SpectatorFormat.Landscape;
            int wantW = landscape ? 1920 : 1080, wantH = landscape ? 1080 : 1920;
            string s = $"picture {picW}x{picH} ({format}{(turned ? ", on its side in the window: turn it back in OBS" : "")}) in the {winW}x{winH} window";
            if (picW < wantW || picH < wantH)
                s += format == SpectatorFormat.Portrait
                    ? $", under {wantW}x{wantH}: PortraitTurned gives 1080x1920 from a 1920x1080 window, or make the window at least 1920 tall"
                    : $", under {wantW}x{wantH}: make the game's window {wantW}x{wantH} or larger";
            return s;
        }

        // ---- Steering.

        private static void Steer(float fade)
        {
            float dt = Math.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f);
            anchorNow = Q(CameraDriver.AnchorRotation);
            headNow = Q(CameraDriver.HeadWorldRotation);
            N.Vector3 f = N.Vector3.Transform(N.Vector3.Transform(N.Vector3.UnitZ, headNow), N.Quaternion.Conjugate(anchorNow));
            float aimYaw = MathF.Atan2(f.X, f.Z) * Rad + restBias;
            float aimPitch = Math.Clamp(MathF.Asin(Math.Clamp(f.Y, -1f, 1f)) * Rad - VRConfig.SpectatorTilt.Value, -85f, 85f);

            // cut, black, new view or recenter: restart at the gaze
            bool restart = !settled || fade > 0.99f || CameraDriver.CutThisFrame || CameraDriver.Mode != lastMode || CameraDriver.SpaceVersion != lastSpace;
            lastMode = CameraDriver.Mode;
            lastSpace = CameraDriver.SpaceVersion;
            if (refitted)
            {
                refitted = false;
                lastAimYaw = aimYaw;
                lastAimPitch = aimPitch;
            }
            if (restart)
            {
                yaw = aimYaw;
                pitch = aimPitch;
                yawSpeed = pitchSpeed = 0f;
                rateYaw = ratePitch = rateYaw2 = ratePitch2 = 0f;
                settled = true;
            }
            else if (holding)
            {
                yaw = holdYaw;
                pitch = holdPitch;
            }
            else
            {
                // turn rate low-passed twice at ~1 Hz, fed forward: no lag on steady turns, tremor filtered
                float k = 1f - MathF.Exp(-dt * 2f * MathF.PI);
                rateYaw += (DeltaAngle(lastAimYaw, aimYaw) / dt - rateYaw) * k;
                ratePitch += ((aimPitch - lastAimPitch) / dt - ratePitch) * k;
                rateYaw2 += (rateYaw - rateYaw2) * k;
                ratePitch2 += (ratePitch - ratePitch2) * k;
                float smooth = 0.5f * VRConfig.SpectatorSmoothing.Value;
                Spring(ref yaw, ref yawSpeed, yaw + DeltaAngle(yaw, aimYaw + rateYaw2 * smooth), smooth, dt);
                Spring(ref pitch, ref pitchSpeed, aimPitch + ratePitch2 * smooth, smooth, dt);
            }
            lastAimYaw = aimYaw;
            lastAimPitch = aimPitch;

            // lag, eased into the room left on that side
            N.Quaternion eyeInv = N.Quaternion.Conjugate(headNow * eyeInHead);
            float offYaw = DeltaAngle(aimYaw, yaw), offPitch = pitch - aimPitch;
            roomYaw = Room(aimYaw, aimPitch, eyeInv, true, offYaw >= 0f ? 1f : -1f);
            roomPitch = Room(aimYaw, aimPitch, eyeInv, false, offPitch >= 0f ? 1f : -1f);
            float y = holding ? offYaw : Soft(offYaw, roomYaw), p = holding ? offPitch : Soft(offPitch, roomPitch);
            bool held = AtEdge(offYaw, y, roomYaw) || AtEdge(offPitch, p, roomPitch);
            float sx = hx * 1.01f, sy = hy * 1.01f;
            var pic = Picture(aimYaw + y, aimPitch + p);
            if (!Fits(eyeInv * pic, sx, sy))
            {
                // pull both axes toward the head until it fits
                float lo = 0f, hi = 1f;
                for (int i = 0; i < 8; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Fits(eyeInv * Picture(aimYaw + y * mid, aimPitch + p * mid), sx, sy)) lo = mid; else hi = mid;
                }
                y *= lo;
                p *= lo;
                held = true;
                pic = Picture(aimYaw + y, aimPitch + p);
                if (!Fits(eyeInv * pic, sx, sy)) pic = Rolled(pic, eyeInv, sx, sy);
            }
            if (held) Held();
            yaw = aimYaw + y;
            pitch = aimPitch + p;
            yawSpeed = Math.Clamp(yawSpeed, -720f, 720f);
            pitchSpeed = Math.Clamp(pitchSpeed, -720f, 720f);
            lagYaw = y;
            lagPitch = p;
            viewRotation = pic;

            // the quad is the eye: camera turned against it as the picture is against the eye
            pictureCamera = turned ? Look(N.Vector3.Transform(N.Vector3.UnitZ, pic), N.Vector3.Transform(N.Vector3.UnitX, pic)) : pic;
            pictureInAnchor = N.Quaternion.Conjugate(anchorNow) * pictureCamera;
            camTransform.localRotation = U(eyeInv * pictureCamera);
        }

        private static N.Quaternion pictureCamera = N.Quaternion.Identity, pictureInAnchor = N.Quaternion.Identity, copiedHead = N.Quaternion.Identity,
            copiedAnchor = N.Quaternion.Identity, anchorNow = N.Quaternion.Identity, headNow = N.Quaternion.Identity;
        private static int copyFrame = -1, freshFrames, staleFrames;

        /// <summary>Studio camera pre-cull: posed as at copy time (headset cameras draw first).</summary>
        private static void OnPreCull(Camera c)
        {
            if (!On || callbackFailed || c == null) return;
            try
            {
                if (c.Pointer != cameraPtr || copyFrame < 0) return;
                if (copyFrame == Time.frameCount) freshFrames++; else staleFrames++;
                camTransform.localRotation = U(N.Quaternion.Conjugate(copiedHead * eyeInHead) * (copiedAnchor * pictureInAnchor));
            }
            catch (Exception e) { CallbackError("before the window's picture is drawn", e); }
        }

        /// <summary>Level picture for a direction in the anchor's frame (deg, pitch up).</summary>
        private static N.Quaternion Picture(float yawDeg, float pitchDeg)
        {
            N.Vector3 dir = N.Vector3.Transform(N.Vector3.Transform(N.Vector3.UnitZ, Euler(-pitchDeg, yawDeg)), anchorNow);
            return Look(dir, N.Vector3.UnitY);
        }

        /// <summary>Extreme head tilt: take as little head roll as fits.</summary>
        private static N.Quaternion Rolled(N.Quaternion level, N.Quaternion eyeInv, float sx, float sy)
        {
            var rolled = Look(N.Vector3.Transform(N.Vector3.UnitZ, level), N.Vector3.Transform(N.Vector3.UnitY, headNow));
            if (!Fits(eyeInv * rolled, sx, sy)) { EdgeShown(); return level; }
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 8; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Fits(eyeInv * N.Quaternion.Slerp(level, rolled, mid), sx, sy)) hi = mid; else lo = mid;
            }
            rolledFrames++;
            return N.Quaternion.Slerp(level, rolled, hi);
        }

        /// <summary>Largest lag one way on one axis (deg) that stays in the eye image.</summary>
        private static float Room(float aimYaw, float aimPitch, N.Quaternion eyeInv, bool yawAxis, float sign)
        {
            float sx = hx * 1.01f, sy = hy * 1.01f;
            if (!Fits(eyeInv * Picture(aimYaw, aimPitch), sx, sy)) return 0f;
            float lo = 0f, hi = 40f;
            for (int i = 0; i < 10; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var pic = yawAxis ? Picture(aimYaw + sign * mid, aimPitch) : Picture(aimYaw, Math.Clamp(aimPitch + sign * mid, -85f, 85f));
                if (Fits(eyeInv * pic, sx, sy)) lo = mid; else hi = mid;
            }
            return lo;
        }

        private const float Knee = 0.35f;

        /// <summary>Lag unchanged up to a third of its room (keeps tremor out), then eased short of the edge.</summary>
        private static float Soft(float lag, float room)
        {
            if (room < 0.01f) return 0f;
            float a = MathF.Abs(lag), knee = Knee * room;
            if (a <= knee) return lag;
            return MathF.CopySign(knee + (room - knee) * MathF.Tanh((a - knee) / (room - knee)), lag);
        }

        /// <summary>Held at the edge: past 3/4 of its room, or cut where there was none.</summary>
        private static bool AtEdge(float lag, float eased, float room) => room < 0.01f ? MathF.Abs(lag) > 0.05f : MathF.Abs(eased) > 0.75f * room;

        private static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            else if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>Critically damped spring, like Unity's SmoothDamp.</summary>
        private static void Spring(ref float x, ref float v, float target, float time, float dt)
        {
            if (time < 0.005f) { x = target; v = 0f; return; }
            float omega = 2f / time, e = omega * dt;
            float exp = 1f / (1f + e + 0.48f * e * e + 0.235f * e * e * e);
            float change = x - target, temp = (v + omega * change) * dt;
            v = (v - omega * temp) * exp;
            x = target + (change + temp) * exp;
        }

        // ---- Black with the headset.

        /// <summary>SteamVR headset fade (0 clear, 1 black).</summary>
        private static float Fade()
        {
            if (fadeBroken) return 0f;
            try
            {
                var compositor = OpenVR.Compositor;
                return compositor != null ? Mathf.Clamp01(compositor.GetCurrentFadeColor(false).a) : 0f;
            }
            catch (Exception e)
            {
                fadeBroken = true;
                Plugin.L.LogWarning("Spectator view: SteamVR's fade unreadable, the picture will not go black with the headset: " + e.Message);
                return 0f;
            }
        }

        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static void Shade(float fade)
        {
            float now = Time.unscaledTime;
            if (fade >= 0.99f)
            {
                if (!black) { black = true; blackSince = now; blackWhy = 0; }
                if (TransitionGuard.Active) blackWhy |= 1;
                if (CameraDriver.CutThisFrame) blackWhy |= 2;
                if (CameraDriver.MenuSettling) blackWhy |= 4;
            }
            else if (black)
            {
                black = false;
                periodBlack += now - blackSince;
                string why = (blackWhy & 1) != 0 ? "a passage, a load or getting in or out" : (blackWhy & 2) != 0 ? "a cut of the game's camera"
                           : (blackWhy & 4) != 0 ? "the main menu coming up" : "the game's black screen";
                if (blackLogs++ < 40)
                    Plugin.L.LogInfo($"Spectator view: black with the headset for {now - blackSince:0.00} s ({why}; {CameraDriver.Mode} now), the picture back with it.");
            }
            if (Mathf.Abs(fade - lastFade) < 0.002f) return;
            lastFade = fade;
            float k = 1f - fade;
            material.SetColor(ColorId, new Color(k, k, k, 1f));
        }

        private static readonly Input.FilmKey key = new Input.FilmKey("Spectator view", "[Spectator] Key", "the view");

        private static void PollKey()
        {
            if (key.Pressed(VRConfig.SpectatorKey.Value)) Switch(!On, "key");
        }

        /// <summary>Re-reads [Spectator] (all but Enabled) to tune while filming.</summary>
        private static void ReadSettingsAgain()
        {
            string changes = VRConfig.ReadSectionAgain("Spectator", name => name switch
            {
                "Format" => VRConfig.SpectatorFormat,
                "FieldOfView" => VRConfig.SpectatorFov,
                "Smoothing" => VRConfig.SpectatorSmoothing,
                "Tilt" => VRConfig.SpectatorTilt,
                "Key" => VRConfig.SpectatorKey,
                _ => null,
            });
            if (changes != null) Plugin.L.LogInfo("Spectator view: [Spectator] read again from the settings file: " + changes + ".");
        }

        // ---- Logs and measures.

        private static long cpuTicks;
        private static int cpuFrames;
        private static float nextReport;

        private static void Announce()
        {
            announce = false;
            string loop = loopUv != null ? $"visible area from SteamVR ({loopUv.Length} points)" : "no visible area from SteamVR (its whole image counts)";
            Plugin.L.LogInfo($"Spectator view on: the left eye's finished image, cropped and steadied, in the game's window " +
                             $"(a copy of the eye per frame and one quad drawn, nothing rendered twice); {Shape()}; {Pixels()}; eye image " +
                             $"{copyW}x{copyH} covering {Deg(-eL)}/{Deg(eR)}/{Deg(-eB)}/{Deg(eT)} degrees left/right/down/up" +
                             $"{(Angle(eyeInHead, N.Quaternion.Identity) > 0.05f ? $", turned {Angle(eyeInHead, N.Quaternion.Identity):0.0} degrees from the head's direction" : "")}, {loop}; " +
                             $"Unity's mirror {(keepDeviceView ? "kept on under it (test)" : "off in the window")}" +
                             $"{(copyEveryPass ? "; copied at both eyes' passes (no pre-render callback)" : "")}.");
        }

        private static string Shape()
        {
            float smooth = VRConfig.SpectatorSmoothing.Value;
            return $"field of view {fovUsed:0.#} degrees across its long side (asked {VRConfig.SpectatorFov.Value:0.#}" +
                   $"{(fovUsed < VRConfig.SpectatorFov.Value - 0.05f ? ", more than the eye's image holds" : "")}; the eye's image allows up to {fovMax:0.#}), " +
                   $"aimed {VRConfig.SpectatorTilt.Value:0.#} degrees below the gaze{(MathF.Abs(restBias) > 0.05f ? $" and {MathF.Abs(restBias):0.#} {(restBias < 0f ? "left" : "right")} of it (toward the eye's wider side)" : "")}, horizon level, smoothing {smooth:0.00} ({0.5f * smooth:0.00} s); " +
                   $"room to lag behind the head {restLeft:0.0} left, {restRight:0.0} right, {restDown:0.0} down, {restUp:0.0} up (degrees)";
        }

        private static string Deg(float tangent) => (Mathf.Atan(tangent) * Mathf.Rad2Deg).ToString("0.0", CultureInfo.InvariantCulture);

        private static void Report()
        {
            nextReport = Time.unscaledTime + 30f;
            Plugin.L.LogInfo("Spectator view: " + PeriodSummary() + ".");
            ResetPeriod();
        }

        // head vs picture turns in the anchor's frame. Shake: frame-to-frame change in turn speed, deg per frame.
        private struct Motion
        {
            public N.Quaternion Last;
            public N.Vector3 LastSpeed;
            public bool Has, HasSpeed;
            public double Speed, ShakeSq;
            public float RollMax;
            public int Count;

            public void Add(N.Quaternion q, float dt)
            {
                float roll = MathF.Abs(Roll(q));
                if (roll > RollMax) RollMax = roll;
                if (Has)
                {
                    N.Vector3 speed = Turn(q * N.Quaternion.Conjugate(Last)) / dt;
                    if (HasSpeed)
                    {
                        float s = (speed - LastSpeed).Length() * dt;
                        ShakeSq += s * s;
                        Speed += speed.Length();
                        Count++;
                    }
                    LastSpeed = speed;
                    HasSpeed = true;
                }
                Last = q;
                Has = true;
            }

            public void Break() { Has = HasSpeed = false; }

            public float Shake => Count > 0 ? (float)Math.Sqrt(ShakeSq / Count) : 0f;
            public float MeanSpeed => Count > 0 ? (float)(Speed / Count) : 0f;
        }

        private static Motion headPeriod, viewPeriod, headWindow, viewWindow, headTrace, viewTrace;
        private static int periodFrames, heldFrames, edgeFrames, rolledFrames, traceHeld, traceEdges;
        private static float periodBlack;

        private static void Held() { heldFrames++; if (traceUntil > 0f) traceHeld++; if (pixelFrames > 0) pixelGaveWay++; }

        private static void EdgeShown() { edgeFrames++; if (traceUntil > 0f) traceEdges++; }

        private static void ResetMotion()
        {
            headPeriod.Break(); viewPeriod.Break();
            headWindow.Break(); viewWindow.Break();
            headTrace.Break(); viewTrace.Break();
        }

        private static void ResetPeriod()
        {
            headPeriod = default;
            viewPeriod = default;
            periodFrames = heldFrames = edgeFrames = rolledFrames = freshFrames = staleFrames = 0;
            periodBlack = 0f;
            copiesAtReport = copies;
            cpuTicks = 0;
            cpuFrames = 0;
            periodStart = Time.unscaledTime;
        }

        private static float periodStart;

        private static void Measure(float fade)
        {
            float dt = Math.Max(Time.unscaledDeltaTime, 1e-4f);
            if (fade > 0.01f || !settled || CameraDriver.CutThisFrame)
            {
                headPeriod.Break(); viewPeriod.Break(); headWindow.Break(); viewWindow.Break(); headTrace.Break(); viewTrace.Break();
                return;
            }
            N.Quaternion inv = N.Quaternion.Conjugate(anchorNow);
            N.Quaternion head = inv * headNow, view = inv * viewRotation;
            periodFrames++;
            headPeriod.Add(head, dt); viewPeriod.Add(view, dt);
            if (traceUntil <= 0f) return;
            headWindow.Add(head, dt); viewWindow.Add(view, dt);
            headTrace.Add(head, dt); viewTrace.Add(view, dt);
        }

        private static string PeriodSummary()
        {
            float seconds = Mathf.Max(Time.unscaledTime - periodStart, 0.001f);
            double cpu = cpuFrames > 0 ? cpuTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / cpuFrames : 0.0;
            int drawn = freshFrames + staleFrames;
            string order = drawn == 0 ? "the picture not drawn yet" : staleFrames == 0 ? "each picture from the same frame's copy"
                         : $"{100f * freshFrames / drawn:0}% of pictures from the same frame's copy, the others a frame old (placed for it)";
            return $"{seconds:0} s, {periodFrames} frame(s) shown, the eye copied {copies - copiesAtReport} time(s) ({(copies - copiesAtReport) / seconds:0}/s), {order}; " +
                   Compare(headPeriod, viewPeriod) + $"; held at the eye's edge in {Share(heldFrames)}, the head's roll taken in {rolledFrames}, edges shown in {edgeFrames}; " +
                   $"black with the headset {periodBlack:0.0} s; {cpu:0.000} ms of processor per frame";
        }

        private static string Share(int frames) => periodFrames > 0 ? $"{100f * frames / periodFrames:0}% of frames" : "no frame";

        private static float PixelsPerDegree => fovUsed > 0f ? Mathf.Max(picW, picH) / fovUsed : 0f;

        private static string Compare(Motion head, Motion view)
        {
            if (head.Count == 0) return "no motion measured";
            float px = PixelsPerDegree;
            string ratio = view.Shake > 1e-5f ? $"{head.Shake / view.Shake:0.#}x steadier" : "steady";
            return $"head {head.MeanSpeed:0.0} deg/s, shake {head.Shake:0.000} deg per frame ({head.Shake * px:0.0} px), roll up to {head.RollMax:0.0} deg; " +
                   $"picture {view.MeanSpeed:0.0} deg/s, shake {view.Shake:0.000} deg ({view.Shake * px:0.0} px), roll up to {view.RollMax:0.0} deg: {ratio}";
        }

        /// <summary>Turn as axis * angle (deg).</summary>
        private static N.Vector3 Turn(N.Quaternion d)
        {
            if (d.W < 0f) d = N.Quaternion.Negate(d);
            float s = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (s < 1e-7f) return N.Vector3.Zero;
            return new N.Vector3(d.X, d.Y, d.Z) * (2f * MathF.Atan2(s, d.W) * Rad / s);
        }

        /// <summary>Picture roll from vertical, degrees.</summary>
        private static float Roll(N.Quaternion q)
        {
            N.Vector3 f = N.Vector3.Transform(N.Vector3.UnitZ, q), u = N.Vector3.Transform(N.Vector3.UnitY, q);
            N.Vector3 level = N.Vector3.UnitY - f * f.Y;
            if (level.LengthSquared() < 1e-6f) return 0f;
            return MathF.Atan2(N.Vector3.Dot(N.Vector3.Cross(level, u), f), N.Vector3.Dot(level, u)) * Rad;
        }

        // ---- Rotations in System.Numerics (Unity's go through IL2CPP interop), Unity's axes.

        private const float Rad = 180f / MathF.PI;

        private static N.Quaternion Q(Quaternion q) => new N.Quaternion(q.x, q.y, q.z, q.w);

        private static Quaternion U(N.Quaternion q) => new Quaternion { x = q.X, y = q.Y, z = q.Z, w = q.W };

        private static N.Vector3 V(Vector3 v) => new N.Vector3(v.x, v.y, v.z);

        /// <summary>Like Quaternion.Euler(down, yaw, 0).</summary>
        private static N.Quaternion Euler(float downDeg, float yawDeg)
        {
            float y = yawDeg * (MathF.PI / 360f), x = downDeg * (MathF.PI / 360f);
            return new N.Quaternion(0f, MathF.Sin(y), 0f, MathF.Cos(y)) * new N.Quaternion(MathF.Sin(x), 0f, 0f, MathF.Cos(x));
        }

        /// <summary>Like Quaternion.LookRotation.</summary>
        private static N.Quaternion Look(N.Vector3 forward, N.Vector3 up)
        {
            var z = N.Vector3.Normalize(forward);
            var x = N.Vector3.Cross(up, z);
            if (x.LengthSquared() < 1e-10f) x = N.Vector3.Cross(N.Vector3.UnitZ, z); // straight up or down
            x = N.Vector3.Normalize(x);
            var y = N.Vector3.Cross(z, x);
            float t = x.X + y.Y + z.Z, s;
            if (t > 0f)
            {
                s = MathF.Sqrt(t + 1f) * 2f;
                return new N.Quaternion((y.Z - z.Y) / s, (z.X - x.Z) / s, (x.Y - y.X) / s, 0.25f * s);
            }
            if (x.X > y.Y && x.X > z.Z)
            {
                s = MathF.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
                return new N.Quaternion(0.25f * s, (y.X + x.Y) / s, (z.X + x.Z) / s, (y.Z - z.Y) / s);
            }
            if (y.Y > z.Z)
            {
                s = MathF.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
                return new N.Quaternion((y.X + x.Y) / s, 0.25f * s, (z.Y + y.Z) / s, (z.X - x.Z) / s);
            }
            s = MathF.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
            return new N.Quaternion((z.X + x.Z) / s, (z.Y + y.Z) / s, 0.25f * s, (x.Y - y.X) / s);
        }

        private static float Angle(N.Quaternion a, N.Quaternion b) => 2f * MathF.Acos(Math.Min(1f, MathF.Abs(N.Quaternion.Dot(a, b)))) * Rad;
    }
}
