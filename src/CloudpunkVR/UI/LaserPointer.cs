using System;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CloudpunkVR.UI
{
    /// <summary>Menu laser: selects like the gamepad, trigger presses Cross. Mouse-only widgets are out of reach.</summary>
    internal static class LaserPointer
    {
        public static bool PointingAtPanel { get; private set; }
        public static bool LeftActive { get; private set; }

        private static LineRenderer line;
        private static Material dotMaterial;
        private static Transform dot;
        private static GameObject lastHover;
        private static Il2CppSystem.Collections.Generic.List<RaycastResult> results;
        private static readonly Color Neon = new Color(0.15f, 0.95f, 1f, 1f);

        public static void Create(Transform parent, int layer)
        {
            var go = new GameObject("CloudpunkVR_Laser");
            go.layer = layer;
            go.transform.SetParent(parent, false);
            line = go.AddComponent<LineRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            var mat = new Material(shader) { name = "CloudpunkVR_Laser" };
            // Fixed overlay order: backdrop 2990, panel 3000, HUD 3001-3011, laser 3100, dot 3110, vignette 4000.
            // Per-eye distance sorting could put the panel over the dot in one eye only.
            mat.renderQueue = 3100;
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            line.sharedMaterial = mat;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.startWidth = 0.004f;
            line.endWidth = 0.0015f;
            line.startColor = new Color(Neon.r, Neon.g, Neon.b, 0.9f);
            line.endColor = new Color(Neon.r, Neon.g, Neon.b, 0.25f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;

            var d = GameObject.CreatePrimitive(PrimitiveType.Quad);
            d.name = "CloudpunkVR_LaserDot";
            UnityEngine.Object.Destroy(d.GetComponent<Collider>());
            d.layer = layer;
            d.transform.SetParent(parent, false);
            var dm = new Material(Shader.Find("UI/Default")) { name = "CloudpunkVR_LaserDot", mainTexture = MakeDot() };
            dm.renderQueue = 3110;
            dm.hideFlags = HideFlags.DontUnloadUnusedAsset;
            dm.color = Neon;
            dotMaterial = dm;
            d.GetComponent<MeshRenderer>().sharedMaterial = dm;
            dot = d.transform;
            dot.gameObject.SetActive(false);

            results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            LeftActive = VRConfig.PointerHand.Value == Hand.Left;
        }

        /// <summary>Pixel reticle: square core and four corner brackets, like the HUD frames.</summary>
        private static Texture2D MakeDot()
        {
            const int n = 16;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "CloudpunkVR_Reticle",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool core = x >= 6 && x <= 9 && y >= 6 && y <= 9;
                    bool edgeX = x == 0 || x == n - 1, edgeY = y == 0 || y == n - 1;
                    bool nearCornerX = x <= 3 || x >= n - 4, nearCornerY = y <= 3 || y >= n - 4;
                    bool bracket = (edgeX && nearCornerY) || (edgeY && nearCornerX);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, core ? 1f : bracket ? 0.85f : 0f));
                }
            t.Apply(false);
            return t;
        }

        public static void Update()
        {
            PointingAtPanel = false;
            if (line == null) return;
            if (!VRUI.Interactive)
            {
                Hide();
                gazeCandidate = null;
                return;
            }

            if (SteamVRInput.Ready)
            {
                // trigger on the other hand switches the pointer
                if (SteamVRInput.ClickLeft.Pressed && !LeftActive) { LeftActive = true; return; }
                if (SteamVRInput.ClickRight.Pressed && LeftActive) { LeftActive = false; return; }
                UpdateLaser();
            }
            else Hide();

            if (!PointingAtPanel) UpdateGaze();
        }

        private static void UpdateLaser()
        {
            var hand = LeftActive ? SteamVRInput.LeftHand : SteamVRInput.RightHand;
            if (!hand.Valid) { Hide(); return; }
            CameraDriver.TrackingToWorld(hand.Position, hand.Rotation, out var origin, out var rot);
            Vector3 dir = rot * Vector3.forward;

            line.enabled = true;
            line.SetPosition(0, origin);

            if (!VRUI.RayToPixel(origin, dir, out var pixel, out var hit))
            {
                // clean frame: laser only while it's on the panel
                if (Film.CleanFrame.HidesOverlays) line.enabled = false;
                else line.SetPosition(1, origin + dir * 1.5f);
                dot.gameObject.SetActive(false);
                lastHover = null;
                return;
            }

            PointingAtPanel = true;
            line.SetPosition(1, hit);
            ShowDot(hit, Vector3.Distance(origin, hit), Neon);

            var target = RaycastSelectable(pixel);
            if (target != null && target != lastHover)
            {
                lastHover = target;
                SelectLikeGamepad(target);
                Haptics.UiTick(0.012f, 0.25f, 180f);
            }
            else if (target == null)
            {
                lastHover = null;
            }

            bool click = LeftActive ? SteamVRInput.ClickLeft.Pressed : SteamVRInput.ClickRight.Pressed;
            if (click && target != null && MenuValues.Click(target, pixel))
            {
                // option row, value already changed
            }
            else if (click && target != null)
            {
                // Cross, the game's own submit path
                VirtualGamepad.Hold(InputControlType.Action1, 0.1f);
                Haptics.UiTick(0.02f, 0.5f, 160f);
            }
        }

        private static GameObject gazeCandidate, gazeSelected;
        private static float gazeSince, gazeQuietUntil;

        /// <summary>Stick moved the selection: gaze stays quiet for a moment.</summary>
        public static void NoteStickNavigation() => gazeQuietUntil = Time.unscaledTime + 1.5f;
        private static readonly Color GazeColor = new Color(0.96f, 0.65f, 0.14f, 0.55f); // the game's title amber

        /// <summary>
        /// PS VR2 eye select while the laser is off the panel: dwell on a button to select it.
        /// Only reselects when the gaze moves to another button, so it doesn't fight the stick.
        /// </summary>
        private static void UpdateGaze()
        {
            if (!VRConfig.EyeSelect.Value || !Psvr2Toolkit.TryGetGaze(0.15f, out var g))
            {
                if (!line.enabled) dot.gameObject.SetActive(false);
                gazeCandidate = null;
                return;
            }
            Vector3 origin = CameraDriver.HeadWorldPosition;
            Vector3 dir = CameraDriver.HeadWorldRotation * g;
            if (!VRUI.RayToPixel(origin, dir, out var pixel, out var hit))
            {
                if (!line.enabled) dot.gameObject.SetActive(false);
                gazeCandidate = null;
                return;
            }
            ShowDot(hit, Vector3.Distance(origin, hit) * 0.7f, GazeColor);

            var target = RaycastSelectable(pixel);
            if (target != gazeCandidate) { gazeCandidate = target; gazeSince = Time.unscaledTime; return; }
            if (target == null) { gazeSelected = null; return; }
            if (target != gazeSelected && Time.unscaledTime >= gazeQuietUntil && Time.unscaledTime - gazeSince > 0.35f)
            {
                gazeSelected = target;
                lastHover = target;
                SelectLikeGamepad(target);
                Haptics.UiTick(0.01f, 0.15f, 200f);
            }
        }

        private static void ShowDot(Vector3 hit, float distance, Color color)
        {
            dot.gameObject.SetActive(true);
            dot.position = hit - VRUI.Panel.forward * 0.003f; // just in front of the panel
            dot.rotation = VRUI.Panel.rotation;
            float size = 0.016f * distance;
            dot.localScale = new Vector3(size, size, 1f);
            if (dotMaterial != null && dotMaterial.color != color) dotMaterial.color = color;
        }

#if DEV
        /// <summary>Dev command: point at a panel pixel like the laser, optionally click like the trigger.</summary>
        public static string DebugPoint(Vector2 pixel, bool click)
        {
            var target = RaycastSelectable(pixel, true);
            if (target != null)
            {
                SelectLikeGamepad(target);
                // option rows step their value, anything else is confirmed
                if (click && !MenuValues.Click(target, pixel)) VirtualGamepad.Hold(InputControlType.Action1, 0.1f);
            }
            return target != null ? Diagnostics.Path(target.transform) : "nothing selectable";
        }
#endif

        private static readonly System.Collections.Generic.List<GraphicRaycaster> raycasters = new System.Collections.Generic.List<GraphicRaycaster>();
        private static float nextRaycasterScan;

        /// <summary>The game disables its UI raycaster while a gamepad is in use (ours counts), so ask the canvases directly.</summary>
        private static GameObject RaycastSelectable(Vector2 pixel, bool log = false)
        {
            var es = EventSystem.current;
            var uiCam = CameraDriver.UiCamera;
            if (es == null || uiCam == null) return null;
            if (Time.unscaledTime >= nextRaycasterScan || log)
            {
                nextRaycasterScan = Time.unscaledTime + 1f;
                raycasters.Clear();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceCamera || c.worldCamera != uiCam) continue;
                    var gr = c.GetComponent<GraphicRaycaster>();
                    if (gr != null) raycasters.Add(gr);
                }
            }

            var ped = new PointerEventData(es) { position = pixel };
            results.Clear();
            foreach (var gr in raycasters)
                if (gr != null && gr.gameObject.activeInHierarchy) gr.Raycast(ped, results);

            // topmost: sorting order, then depth
            RaycastResult best = null;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r.gameObject == null) continue;
                if (best == null || r.sortingOrder > best.sortingOrder || (r.sortingOrder == best.sortingOrder && r.depth > best.depth)) best = r;
            }
            if (log)
            {
                var sb = new System.Text.StringBuilder($"raycast at {pixel}: {results.Count} hits on {raycasters.Count} canvases");
                for (int i = 0; i < results.Count && i < 8; i++)
                    sb.AppendLine().Append($"   {Diagnostics.Path(results[i].gameObject.transform)} order={results[i].sortingOrder} depth={results[i].depth}");
                Plugin.L.LogInfo(sb.ToString());
            }
            if (best == null) return null;
            // a button is several graphics, any of them counts. Option rows take the laser whatever their Selectable
            // says: the game highlights and steps them itself (OptionsButton).
            var sel = best.gameObject.GetComponentInParent<Selectable>();
            if (sel != null && sel.isActiveAndEnabled && sel.IsInteractable()) return sel.gameObject;
            var row = OptionRow(best.gameObject);
            if (row != null) return row;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r.gameObject == null || r.sortingOrder != best.sortingOrder) continue;
                var s2 = r.gameObject.GetComponentInParent<Selectable>();
                if (s2 != null && s2.isActiveAndEnabled && s2.IsInteractable()) return s2.gameObject;
                row = OptionRow(r.gameObject);
                if (row != null) return row;
            }
            if (log)
                Plugin.L.LogInfo($"raycast: nothing to select under {Diagnostics.Path(best.gameObject.transform)}: " +
                                 (sel == null ? "no Selectable above it" : $"'{sel.name}' {sel.GetIl2CppType().Name}, enabled {sel.enabled}, interactable {sel.interactable}, allowed by its groups {sel.IsInteractable()}"));
            return null;
        }

        private static GameObject OptionRow(GameObject hit)
        {
            var row = hit.GetComponentInParent<OptionsButton>();
            return row != null && row.isActiveAndEnabled ? row.gameObject : null;
        }

        private static void SelectLikeGamepad(GameObject go)
        {
            try
            {
                var g = Global.Instance;
                if (g != null) g.SelectUIObject(go);
                else EventSystem.current?.SetSelectedGameObject(go);
            }
            catch (Exception)
            {
                EventSystem.current?.SetSelectedGameObject(go);
            }
        }

        private static void Hide()
        {
            line.enabled = false;
            if (dot != null) dot.gameObject.SetActive(false);
            lastHover = null;
        }
    }
}
