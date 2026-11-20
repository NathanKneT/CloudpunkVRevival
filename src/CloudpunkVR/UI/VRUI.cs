using System;
using System.Collections.Generic;
using CloudpunkVR.VR;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The game's UI camera renders into a texture, and parts of it are shown on quads drawn by an overlay camera
    /// (so they never sink into walls). Open menus go on the panel, otherwise the HUD is cut into pieces (HudLayout).
    /// Sizes are angles, distance only sets depth.
    /// </summary>
    internal static class VRUI
    {
        public const int Width = 1920, Height = 1080;

        public static int Layer { get; private set; } = 30;
        public static RenderTexture Texture { get; private set; }
        public static Transform Panel { get; private set; }
        public static bool Interactive { get; private set; }

        /// <summary>World point the HUD is laid out from this frame.</summary>
        public static Vector3 HudEye { get; private set; }

        public static bool MenuShown { get; private set; }

        /// <summary>Root canvases on the UI camera, refreshed every second.</summary>
        public static readonly List<Canvas> UiCanvases = new List<Canvas>();

        private static GameObject root;
        private static Camera uiCamera;
        private static Material panelMaterial;
        private static float panelYaw;         // in anchor space
        private static float panelHeight;      // in calibrated head space, meters
        private static float panelPitch;       // degrees, Smooth follow only
        private static bool wasInteractive;
        private static bool placed;
        private static float nextCanvasScan;
        private static bool following;
        private static int panelErrors;

        // panel crop (UI pixels) and scale, also used by the laser
        private static Rect crop = UiPicture.Full;
        private static float pixelAngle = 0.04f;
        private static float below, panelAlpha;
        private static bool panelReady;

        // menu focus
        private static Rect contentRect;
        private static int contentId;
        private static float nextContentScan, openedAt, menuFontPx, smallerSince = -1f;
        private static float scanWindowStart, scanWindowMs, scanMaxMs;
        private static readonly HashSet<IntPtr> skip = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> rootSet = new HashSet<IntPtr>();
        private static readonly List<Transform> roots = new List<Transform>();
        private static Transform backdrop;
        private static Material backdropMaterial;

        public static bool Ready => root != null && Texture != null;

        /// <summary>Parent of everything the overlay camera draws.</summary>
        public static Transform Root => root != null ? root.transform : null;

        public static void Create()
        {
            if (root != null) return;
            Layer = PickFreeLayer();

            Texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "CloudpunkVR_UI",
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            Texture.Create();
            Texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

            root = new GameObject("CloudpunkVR_Overlay");
            UnityEngine.Object.DontDestroyOnLoad(root);

            // overlay camera: only our layer, after everything else
            var camGo = new GameObject("CloudpunkVR_OverlayCamera");
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Depth; // black while no game camera draws (CameraDriver)
            cam.backgroundColor = Color.black;
            cam.cullingMask = 1 << Layer;
            cam.depth = 100;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 50f;
            cam.useOcclusionCulling = false; // occlusion culling hides overlay geometry
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
            XRDevice.DisableAutoXRCameraTracking(cam, true);
            CameraDriver.OverlayCamera = cam;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "CloudpunkVR_UIPanel";
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.layer = Layer;
            quad.transform.SetParent(root.transform, false);
            panelMaterial = new Material(Shader.Find("UI/Default")) { name = "CloudpunkVR_UIPanel", mainTexture = Texture };
            panelMaterial.renderQueue = 3000; // fixed order, see LaserPointer
            panelMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = panelMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            Panel = quad.transform;
            quad.SetActive(false);

            CreateBackdrop();
            HudLayout.Create(root.transform, Layer);
            LaserPointer.Create(root.transform, Layer);
            VoxelHands.Create(root.transform, Layer);
            Wrist.Create(root.transform, Layer);
            Vignette.Create(camGo.transform, Layer);
            Plugin.L.LogInfo($"VR overlay created on layer {Layer}.");
        }

        /// <summary>First of 30/27/26... that no game camera renders, preferring unnamed ones.</summary>
        private static int PickFreeLayer()
        {
            int used = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>()) used |= c.cullingMask;
            foreach (int l in new[] { 30, 27, 26, 7, 6, 3 })
                if ((used & (1 << l)) == 0 && string.IsNullOrEmpty(LayerMask.LayerToName(l))) return l;
            foreach (int l in new[] { 30, 27, 26, 7, 6, 3 })
                if ((used & (1 << l)) == 0) return l;
            return 30;
        }

        /// <summary>Points the UI camera and overlay canvases at our texture and pins canvas scale. Safe every frame.</summary>
        public static void HookGameUI()
        {
            var g = Global.Instance;
            var cam = g != null ? g.uiCamera : null;
            if (cam != null && (uiCamera != cam || cam.targetTexture != Texture))
            {
                uiCamera = cam;
                cam.targetTexture = Texture;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.useOcclusionCulling = false;
                cam.allowMSAA = false;
                XRDevice.DisableAutoXRCameraTracking(cam, true);
                CameraDriver.UiCamera = cam;
                nextCanvasScan = 0f;
                Plugin.L.LogInfo("Game UI camera now renders to the VR panel.");
            }

            if (Time.unscaledTime < nextCanvasScan || uiCamera == null) return;
            nextCanvasScan = Time.unscaledTime + 1f;
            UiCanvases.Clear();
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null || !c.isRootCanvas) continue;
                // overlay canvases (loading screen) never reach the headset
                if (c.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = uiCamera;
                    c.planeDistance = 50f;
                    Plugin.L.LogInfo($"Overlay canvas '{c.name}' moved to the VR panel.");
                }
                if (c.renderMode != RenderMode.ScreenSpaceCamera || c.worldCamera != uiCamera) continue;
                UiPicture.PinScale(c);
                UiCanvases.Add(c);
            }
        }

        /// <summary>Menus, dialogue, inventory, map.</summary>
        private static bool ComputeInteractive()
        {
            var g = Global.Instance;
            if (g == null) return true;
            try
            {
                if (Film.CleanFrame.HidesMenu) return false; // F2 on the main menu hides it
                if (TitleScreen.OnPanel || CameraDriver.Mode == ViewMode.Menu) return true;
                return g.IsInMenu() || g.HasBigWindowOpen() || MapOpen || g.IsInDialog(true);
            }
            catch (Exception) { return false; }
        }

        /// <summary>Recenter (R3).</summary>
        public static void PlaceAgain() => placed = false;

        /// <summary>The static flag throws before Global exists.</summary>
        private static bool MapOpen
        {
            get
            {
                try { return Global.Instance != null && Global.FullMapOpen; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>After the camera driver. Errors are caught here so the laser, wrist and vignette still run.</summary>
        public static void UpdatePanel()
        {
            if (root == null) return;
            try { PlaceAll(); }
            catch (Exception e) { if (panelErrors++ < 5) Plugin.L.LogWarning("VR panel: " + e); }
        }

        private static void PlaceAll()
        {
            Interactive = ComputeInteractive();

            float distance = Interactive ? VRConfig.ScreenDistance.Value : VRConfig.HudDistance.Value;

            Quaternion anchorInv = Quaternion.Inverse(CameraDriver.AnchorRotation);
            float headYaw = CameraDriver.Yaw(anchorInv * CameraDriver.HeadWorldRotation);
            Vector3 headLocal = anchorInv * (CameraDriver.HeadWorldPosition - CameraDriver.AnchorPosition);

            float headPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, (anchorInv * CameraDriver.HeadWorldRotation).eulerAngles.x), -30f, 35f);
            var follow = VRConfig.MenuFollow.Value;
            bool justOpened = Interactive && !wasInteractive;
            if (!placed || justOpened || CameraDriver.Mode == ViewMode.Startup)
            {
                panelYaw = headYaw;
                panelHeight = headLocal.y;
                panelPitch = headPitch;
                placed = true;
            }
            else if (follow == PanelFollow.Smooth)
            {
                // ~0.25 s behind, yaw and pitch
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 5f);
                panelYaw = Mathf.LerpAngle(panelYaw, headYaw, k);
                panelPitch = Mathf.Lerp(panelPitch, headPitch, k);
                panelHeight = headLocal.y;
            }
            else if (follow == PanelFollow.Lazy)
            {
                // stays put for glances, catches up when you turn away
                float delta = Mathf.DeltaAngle(panelYaw, headYaw);
                float limit = Interactive ? 50f : 35f;
                if (Mathf.Abs(delta) > limit) following = true;
                if (following)
                {
                    panelYaw = Mathf.MoveTowardsAngle(panelYaw, headYaw, Time.unscaledDeltaTime * 90f);
                    if (Mathf.Abs(Mathf.DeltaAngle(panelYaw, headYaw)) < 3f) following = false;
                }
                panelHeight = Mathf.Lerp(panelHeight, headLocal.y, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 1.5f));
            }
            wasInteractive = Interactive;

            // in the cockpit the HUD is fixed to the windscreen, straight ahead of the seat
            bool windshield = !Interactive && CameraDriver.Mode == ViewMode.Cockpit && VRConfig.HudOnWindshield.Value;
            Vector3 eyeLocal;
            float pitch, yaw;
            if (windshield) { eyeLocal = anchorInv * Vector3.up * VRConfig.HeightOffset.Value; pitch = HudLayout.WindshieldBelow; yaw = 0f; }
            else if (follow == PanelFollow.Smooth) { eyeLocal = headLocal; pitch = panelPitch; yaw = panelYaw; }
            else { eyeLocal = new Vector3(headLocal.x, panelHeight, headLocal.z); pitch = 0f; yaw = panelYaw; }
            Vector3 eye = CameraDriver.AnchorPosition + CameraDriver.AnchorRotation * eyeLocal;
            HudEye = eye;

            bool menuShown = Interactive && UpdateMenu(justOpened, eye, pitch, yaw, distance);
            if (!Interactive) HideMenu();
            MenuShown = menuShown;
            // no HUD on the title or during startup (the minimap already draws there)
            bool play = CameraDriver.Mode != ViewMode.Startup && CameraDriver.Mode != ViewMode.Menu;
            Quaternion hud = CameraDriver.AnchorRotation * Quaternion.Euler(pitch + (windshield ? 0f : HudLayout.Below), yaw, 0f);
            HudLayout.Update(!menuShown && play, windshield, eye, hud, distance);
        }

        // ---- The open menu on the panel.

        /// <summary>
        /// Crops the panel to the open menu. Hidden (HUD stays up) until something is found, since menus fade in.
        /// Returns whether the panel is shown.
        /// </summary>
        private static bool UpdateMenu(bool justOpened, Vector3 eye, float pitch, float yaw, float distance)
        {
            float now = Time.unscaledTime;
            if (justOpened)
            {
                contentRect = default;
                contentId = 0;
                menuFontPx = 0f;
                openedAt = now;
                nextContentScan = 0f;
                panelReady = false;
                panelAlpha = 0f;
            }
            if (VRConfig.MenuFocus.Value)
            {
                // every frame until found, then ~3 times a second
                if (now >= nextContentScan || (contentRect.width <= 4f && now - openedAt < 1f)) ScanMenu();
            }
            else contentRect = default;
            bool found = contentRect.width > 4f && contentRect.height > 4f;
            // whole picture without MenuFocus, or during startup loading screens
            bool whole = !VRConfig.MenuFocus.Value || (Global.Instance == null && now - openedAt > 0.5f);
            if (!found && !whole) { HidePanel(); return false; }
            Rect target = found ? contentRect : UiPicture.Full;
            float targetAngle = MenuPixelAngle(target, found ? menuFontPx : 0f);

            if (!panelReady)
            {
                panelReady = true;
                crop = target;
                pixelAngle = targetAngle;
            }
            else
            {
                // ease in log space so size x scale never overshoots
                float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 6f);
                Vector2 c = Vector2.Lerp(crop.center, target.center, t);
                float w = Mathf.Exp(Mathf.Lerp(Mathf.Log(crop.width), Mathf.Log(target.width), t));
                float h = Mathf.Exp(Mathf.Lerp(Mathf.Log(crop.height), Mathf.Log(target.height), t));
                crop = new Rect(c.x - w * 0.5f, c.y - h * 0.5f, w, h);
                pixelAngle = Mathf.Exp(Mathf.Lerp(Mathf.Log(pixelAngle), Mathf.Log(targetAngle), t));
            }
            panelAlpha = Mathf.MoveTowards(panelAlpha, 1f, Time.unscaledDeltaTime * 8f);

            // 12 deg below the gaze like a page, less for tall menus so the bottom stays within 22 deg
            float wDeg = crop.width * pixelAngle, hDeg = crop.height * pixelAngle;
            below = Mathf.Clamp(22f - hDeg * 0.5f, 0f, 12f);
            Quaternion rot = CameraDriver.AnchorRotation * Quaternion.Euler(pitch + below, yaw, 0f);
            float pw = UiPicture.Span(wDeg, distance), ph = pw * crop.height / crop.width;
            if (!Panel.gameObject.activeSelf) Panel.gameObject.SetActive(true);
            Panel.SetPositionAndRotation(eye + rot * Vector3.forward * distance, rot);
            Panel.localScale = new Vector3(pw, ph, 1f);
            panelMaterial.mainTextureScale = new Vector2(crop.width / Width, crop.height / Height);
            panelMaterial.mainTextureOffset = new Vector2(crop.x / Width, crop.y / Height);
            panelMaterial.color = new Color(1f, 1f, 1f, panelAlpha);
            UpdateBackdrop(found, rot, pw, ph);
            return true;
        }

        private static void HideMenu()
        {
            contentRect = default;
            panelReady = false;
            HidePanel();
        }

        /// <summary>Instant, so the HUD takes over in the same frame.</summary>
        private static void HidePanel()
        {
            panelAlpha = 0f;
            if (Panel.gameObject.activeSelf) Panel.gameObject.SetActive(false);
            if (backdrop != null && backdrop.gameObject.activeSelf) backdrop.gameObject.SetActive(false);
        }

        /// <summary>
        /// Degrees per pixel: median text height at 0.9 deg (17 deg tall without text), 40 deg wide max except low
        /// strips like the trade window. Clamped to 0.03..0.05 and to MenuAngle (x1.2 for the full map).
        /// </summary>
        private static float MenuPixelAngle(Rect r, float fontPx)
        {
            float angle = VRConfig.MenuAngle.Value * (MapOpen ? 1.2f : 1f);
            float want = fontPx > 2f ? 0.9f / fontPx : 17f / r.height;
            float widest = r.height < r.width * 0.35f ? angle : 40f;
            float k = Mathf.Clamp(Mathf.Min(want, widest / r.width), 0.03f, 0.05f);
            return Mathf.Min(k, angle / r.width, angle * 0.65f / r.height);
        }

        /// <summary>Same menu: grows at once, shrinks only after a second, so hover effects don't make it breathe.</summary>
        private static void ScanMenu()
        {
            nextContentScan = Time.unscaledTime + 0.3f;
            Rect found;
            int id;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { found = UiPicture.Margin(FrameMenu(out id), 40f); }
            catch (Exception e) { Plugin.L.LogDebug("Menu scan: " + e.Message); return; }
            finally { NoteScan(clock); }
            if (found.width <= 4f || found.height <= 4f) return;
            float fonts = Median(UiPicture.FontHeights);
            if (contentRect.width <= 4f || id != contentId)
            {
                if (contentRect.width <= 4f || UiPicture.Differs(found, contentRect, 28f)) { contentRect = found; menuFontPx = fonts; }
                contentId = id;
                smallerSince = -1f;
                return;
            }
            var grown = UiPicture.Union(contentRect, found);
            if (UiPicture.Differs(grown, contentRect, 28f)) { contentRect = grown; smallerSince = -1f; return; }
            if (!UiPicture.Differs(found, contentRect, 28f)) { smallerSince = -1f; return; }
            if (smallerSince < 0f) smallerSince = Time.unscaledTime;
            else if (Time.unscaledTime - smallerSince > 1f) { contentRect = found; menuFontPx = fonts; smallerSince = -1f; }
        }

        private static float Median(List<float> values)
        {
            if (values.Count < 3) return 0f;
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>Longest scan of the last second, for the readout.</summary>
        internal static void NoteScan(System.Diagnostics.Stopwatch clock)
        {
            float ms = (float)clock.Elapsed.TotalMilliseconds;
            if (Time.unscaledTime - scanWindowStart > 1f) { scanMaxMs = scanWindowMs; scanWindowMs = 0f; scanWindowStart = Time.unscaledTime; }
            scanWindowMs = Mathf.Max(scanWindowMs, ms);
        }

        /// <summary>
        /// Union of the open windows: known menus plus each usable button's largest non-fullscreen ancestor.
        /// HUD bits are skipped. identity is an order-independent sum, to tell menus apart.
        /// </summary>
        private static Rect FrameMenu(out int identity)
        {
            identity = 0;
            var cam = uiCamera;
            if (cam == null) return default;
            UiPicture.StartScan();
            var mm = Minimap.Instance;
            if (MapOpen && mm != null && mm.mapFull != null)
            {
                identity = mm.mapFull.Pointer.GetHashCode();
                return UiPicture.Content(mm.mapFull.transform, cam, null, true);
            }

            var g = Global.Instance;
            skip.Clear();
            if (mm != null && mm.mapSmall != null) UiPicture.Collect(mm.mapSmall.transform, skip);
            roots.Clear();
            rootSet.Clear();
            if (g != null)
            {
                if (g.questsParent != null) UiPicture.Collect(g.questsParent.transform, skip);
                if (g.hudParents != null && g.hudParents.racingUI != null) UiPicture.Collect(g.hudParents.racingUI.transform, skip);
                if (g.hudParents != null && g.hudParents.unlockPopup != null) UiPicture.Collect(g.hudParents.unlockPopup.transform, skip);
                if (g.m_BlackScreenCanvas != null) UiPicture.Collect(g.m_BlackScreenCanvas.transform, skip);
                if (g.menuBackground != null) skip.Add(g.menuBackground.Pointer);

                if (TitleScreen.OnPanel) AddRoot(TitleScreen.Prompt);
                AddRoot(g.menuCanvas);
                AddRoot(g.options);
                AddRoot(g.controls);
                AddRoot(g.areYouSureQuit);
                AddRoot(g.areYouSureNew);
                AddRoot(g.listeningScreen);
                var inventory = g.inventoryUI;
                if (inventory != null) { AddRoot(inventory); AddRoot(inventory.window); }
                AddRoot(g.garageUI);
                AddRoot(g.apartmentUI);
                AddRoot(g.holocashUI);
                AddRoot(g.codeCheckUI);
                // trade windows live in the dialogue box
                var d = g.dialogUI;
                if (d != null) AddRoot(d.exteriorCanvasGroupGlobal != null ? (Component)d.exteriorCanvasGroupGlobal : d);
                var call = g.incomingCallUI;
                if (call != null) AddRoot(call.exteriorCanvasGroupGlobal != null ? (Component)call.exteriorCanvasGroupGlobal : call);
            }

            Rect r = default;
            var selectables = Selectable.allSelectables;
            for (int i = 0; selectables != null && i < selectables.Count; i++)
            {
                var s = selectables[i];
                try
                {
                    if (s == null || !s.isActiveAndEnabled || !s.IsInteractable() || !UiPicture.OnUiCamera(s, cam)) continue;
                    var tg = s.targetGraphic;
                    if (tg != null && tg.canvasRenderer != null && tg.canvasRenderer.GetAlpha() < 0.05f) continue;
                    if (UiPicture.GroupAlpha(s.transform) < 0.05f) continue;
                    identity += s.Pointer.GetHashCode();
                    r = UiPicture.Union(r, UiPicture.ScreenRect(s.GetComponent<RectTransform>(), cam));
                    // largest ancestor below the canvas that isn't full-screen
                    var top = s.transform;
                    var canvasRoot = s.GetComponentInParent<Canvas>().rootCanvas.transform;
                    while (top.parent != null && top.parent.Pointer != canvasRoot.Pointer)
                    {
                        var up = top.parent.GetComponent<RectTransform>();
                        if (up == null || UiPicture.IsBackground(UiPicture.ScreenRect(up, cam))) break;
                        top = top.parent;
                    }
                    AddRoot(top);
                }
                catch (Exception) { /* unreadable through interop */ }
            }
            foreach (var t in roots)
            {
                var content = UiPicture.Content(t, cam, skip, false);
                if (content.width <= 0f) continue;
                identity += t.Pointer.GetHashCode();
                r = UiPicture.Union(r, content);
            }
            return r;
        }

        private static void AddRoot(Component c)
        {
            if (c == null || !c.gameObject.activeInHierarchy) return;
            var t = c.transform;
            if (rootSet.Add(t.Pointer)) roots.Add(t);
        }

        /// <summary>Soft navy card with implant-blue corner marks, for contrast against the city.</summary>
        private static void CreateBackdrop()
        {
            const int n = 256;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "CloudpunkVR_MenuBackdrop",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            var navy = new Color(11f / 255f, 18f / 255f, 32f / 255f);
            var implant = new Color(81f / 255f, 168f / 255f, 221f / 255f);
            const int inset = 8, arm = 26, thick = 2;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float ex = Mathf.Min(x, n - 1 - x) / (n * 0.18f), ey = Mathf.Min(y, n - 1 - y) / (n * 0.18f);
                    float a = Mathf.Clamp01(Mathf.Min(ex, ey));
                    var c = new Color(navy.r, navy.g, navy.b, 0.62f * a * a * (3f - 2f * a));
                    int dx = Mathf.Min(x, n - 1 - x) - inset, dy = Mathf.Min(y, n - 1 - y) - inset;
                    bool bracket = dx >= 0 && dy >= 0 && ((dx < thick && dy < arm) || (dy < thick && dx < arm));
                    if (bracket) c = new Color(implant.r, implant.g, implant.b, 0.95f);
                    tex.SetPixel(x, y, c);
                }
            tex.Apply(false);
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "CloudpunkVR_MenuBackdrop";
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.layer = Layer;
            quad.transform.SetParent(root.transform, false);
            backdropMaterial = new Material(Shader.Find("UI/Default")) { name = "CloudpunkVR_MenuBackdrop", mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
            backdropMaterial.renderQueue = 2990; // under the panel
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = backdropMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            backdrop = quad.transform;
            quad.SetActive(false);
        }

        private static void UpdateBackdrop(bool framed, Quaternion rot, float w, float h)
        {
            if (backdrop == null) return;
            if (!framed) { if (backdrop.gameObject.activeSelf) backdrop.gameObject.SetActive(false); return; }
            if (!backdrop.gameObject.activeSelf) backdrop.gameObject.SetActive(true);
            backdrop.SetPositionAndRotation(Panel.position + rot * new Vector3(0f, 0f, 0.004f), rot);
            backdrop.localScale = new Vector3(w * 1.12f, h * 1.18f, 1f);
            backdropMaterial.color = new Color(1f, 1f, 1f, panelAlpha); // colors are in the texture
        }

        public static string DescribeView()
        {
            if (!Interactive || !panelReady) return "HUD: " + HudLayout.Describe() + $"; longest interface scan {scanMaxMs:0.0} ms";
            return $"menu: content {UiPicture.Describe(contentRect)} (menu {contentId:X8}, text {menuFontPx:0} px), shown, panel shows {UiPicture.Describe(crop)}" +
                   $" at {pixelAngle:0.000} deg per pixel = {crop.width * pixelAngle:0.0} x {crop.height * pixelAngle:0.0} deg, centred {below:0.0} deg below the gaze; longest interface scan {scanMaxMs:0.0} ms";
        }

        /// <summary>World ray to UI pixel, through the current crop. False if it misses or the panel is hidden.</summary>
        public static bool RayToPixel(Vector3 origin, Vector3 dir, out Vector2 pixel, out Vector3 hit)
        {
            pixel = default; hit = default;
            if (Panel == null || !Panel.gameObject.activeSelf || panelAlpha < 0.5f) return false;
            var plane = new Plane(-Panel.forward, Panel.position);
            if (!plane.Raycast(new Ray(origin, dir), out float d) || d <= 0f) return false;
            hit = origin + dir * d;
            Vector3 local = Panel.InverseTransformPoint(hit); // quad spans -0.5..0.5
            if (Mathf.Abs(local.x) > 0.5f || Mathf.Abs(local.y) > 0.5f) return false;
            pixel = new Vector2(crop.x + (local.x + 0.5f) * crop.width, crop.y + (local.y + 0.5f) * crop.height);
            return true;
        }
    }
}
