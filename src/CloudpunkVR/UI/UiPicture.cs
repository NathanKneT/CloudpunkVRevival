using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.UI
{
    /// <summary>Helpers for the 1920x1080 UI texture. Rects are UI pixels, origin bottom-left.</summary>
    internal static class UiPicture
    {
        public static readonly Rect Full = new Rect(0f, 0f, VRUI.Width, VRUI.Height);

        // ---- The scale of the game's canvases.

        /// <summary>Canvas scalers follow the window size even into our texture (4K doubled everything): pin to 1080p.</summary>
        public static void PinScale(Canvas canvas)
        {
            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null || scaler.m_UiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return;
            Vector2 reference = scaler.m_ReferenceResolution;
            if (reference.x <= 0f || reference.y <= 0f) return;
            float wide = VRUI.Width / reference.x, high = VRUI.Height / reference.y;
            // Unity's formula, for a 1920x1080 screen
            float scale = scaler.m_ScreenMatchMode switch
            {
                CanvasScaler.ScreenMatchMode.Expand => Mathf.Min(wide, high),
                CanvasScaler.ScreenMatchMode.Shrink => Mathf.Max(wide, high),
                _ => Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(wide, 2f), Mathf.Log(high, 2f), scaler.m_MatchWidthOrHeight)),
            };
            scaler.m_ScaleFactor = scale;
            scaler.m_UiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            Plugin.L.LogInfo($"Canvas '{canvas.name}' pinned to the 1080p layout: scale {scale:0.00} (reference {reference.x:0}x{reference.y:0}, window {Screen.width}x{Screen.height}).");
        }

        // ---- Where things land on the picture.

        public static bool OnUiCamera(Component c, Camera cam)
        {
            var canvas = c.GetComponentInParent<Canvas>();
            if (canvas == null) return false;
            var root = canvas.rootCanvas;
            return root.renderMode == RenderMode.ScreenSpaceCamera && root.worldCamera == cam;
        }

        public static Rect ScreenRect(RectTransform rt, Camera cam)
        {
            var r = rt.rect;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>Empty rects are ignored.</summary>
        public static Rect Union(Rect a, Rect b)
        {
            if (a.width <= 0f || a.height <= 0f) return b;
            if (b.width <= 0f || b.height <= 0f) return a;
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        }

        public static Rect Intersect(Rect a, Rect b)
        {
            float x0 = Mathf.Max(a.xMin, b.xMin), y0 = Mathf.Max(a.yMin, b.yMin), x1 = Mathf.Min(a.xMax, b.xMax), y1 = Mathf.Min(a.yMax, b.yMax);
            return x1 > x0 && y1 > y0 ? Rect.MinMaxRect(x0, y0, x1, y1) : default;
        }

        /// <summary>Grown by m, clipped to the picture. Empty stays empty.</summary>
        public static Rect Margin(Rect r, float m) => r.width > 0f && r.height > 0f ? Intersect(Rect.MinMaxRect(r.xMin - m, r.yMin - m, r.xMax + m, r.yMax + m), Full) : default;

        public static bool Differs(Rect a, Rect b, float pixels) =>
            Mathf.Abs(a.xMin - b.xMin) > pixels || Mathf.Abs(a.xMax - b.xMax) > pixels || Mathf.Abs(a.yMin - b.yMin) > pixels || Mathf.Abs(a.yMax - b.yMax) > pixels;

        public static string Describe(Rect r) => r.width > 0f ? $"({r.xMin:0},{r.yMin:0})-({r.xMax:0},{r.yMax:0})" : "none";

        /// <summary>Width in m of something this many degrees wide at this distance.</summary>
        public static float Span(float degrees, float distance) => 2f * distance * Mathf.Tan(Mathf.Clamp(degrees, 0f, 170f) * 0.5f * Mathf.Deg2Rad);

        /// <summary>Product of the canvas group alphas up the hierarchy.</summary>
        public static float GroupAlpha(Transform t)
        {
            float a = 1f;
            for (var p = t; p != null; p = p.parent)
            {
                var group = p.GetComponent<CanvasGroup>();
                if (group == null || !group.enabled) continue;
                a *= group.alpha;
                if (group.ignoreParentGroups) break;
            }
            return a;
        }

        // ---- What a part of the interface shows.

        private static readonly HashSet<IntPtr> hidden = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> visited = new HashSet<IntPtr>();
        private static readonly List<Transform> maskTransforms = new List<Transform>();
        private static readonly List<Rect> maskRects = new List<Rect>();
        private static int budget;
        private static bool budgetWarned;

        /// <summary>Text heights in px from the last scan. Menus are sized from these.</summary>
        public static readonly List<float> FontHeights = new List<float>();

        /// <summary>Each graphic once per scan, capped so a crowded screen stays cheap.</summary>
        public static void StartScan()
        {
            budget = 1500;
            visited.Clear();
            FontHeights.Clear();
        }

        /// <summary>
        /// Union of visible graphics under root, clipped to their masks. Full-screen ones only count with
        /// keepBackgrounds or when nothing else is drawn.
        /// </summary>
        public static Rect Content(Transform root, Camera cam, HashSet<IntPtr> skip, bool keepBackgrounds)
        {
            if (root == null || cam == null || !root.gameObject.activeInHierarchy || !OnUiCamera(root, cam) || GroupAlpha(root) < 0.05f) return default;

            // faded-out groups inside the root
            hidden.Clear();
            var groups = root.GetComponentsInChildren<CanvasGroup>(false);
            for (int i = 0; i < groups.Length; i++)
            {
                var g = groups[i];
                if (g == null || !g.enabled || g.alpha >= 0.05f) continue;
                var inside = g.GetComponentsInChildren<Graphic>(false);
                for (int j = 0; j < inside.Length; j++) if (inside[j] != null) hidden.Add(inside[j].Pointer);
            }

            maskTransforms.Clear();
            maskRects.Clear();
            AddMasks(root.GetComponentsInChildren<RectMask2D>(false), cam);
            AddMasks(root.GetComponentsInParent<RectMask2D>(false), cam);
            AddMasks(root.GetComponentsInChildren<Mask>(false), cam);
            AddMasks(root.GetComponentsInParent<Mask>(false), cam);

            Rect content = default, backgrounds = default;
            var graphics = root.GetComponentsInChildren<Graphic>(false);
            for (int i = 0; i < graphics.Length; i++)
            {
                var gr = graphics[i];
                if (gr == null) continue;
                IntPtr p = gr.Pointer;
                if (hidden.Contains(p) || (skip != null && skip.Contains(p)) || !visited.Add(p)) continue;
                if (budget-- <= 0)
                {
                    if (!budgetWarned) { budgetWarned = true; Plugin.L.LogWarning($"Interface scan: more graphics than it looks at under '{root.name}'; the rest is left out."); }
                    break;
                }
                if (!gr.enabled) continue;
                var cr = gr.canvasRenderer;
                if (cr != null && cr.GetAlpha() < 0.05f) continue;
                // fields, not getters: the color getters are stripped in this build
                if (gr.m_Color.a < 0.05f) continue;
                var tmp = gr.TryCast<TMP_Text>();
                if (tmp != null && (string.IsNullOrWhiteSpace(tmp.text) || tmp.m_fontColor.a < 0.05f)) continue;
                var legacy = gr.TryCast<Text>();
                if (legacy != null && string.IsNullOrWhiteSpace(legacy.text)) continue;
                var rt = gr.rectTransform;
                var r = Intersect(ScreenRect(rt, cam), Full);
                var t = gr.transform;
                for (int m = 0; m < maskTransforms.Count && r.width > 0f; m++)
                    if (t.IsChildOf(maskTransforms[m])) r = Intersect(r, maskRects[m]);
                if (r.width <= 0f || r.height <= 0f) continue;
                if (IsBackground(r)) { backgrounds = Union(backgrounds, r); continue; }
                content = Union(content, r);
                if (tmp != null)
                {
                    float a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(Vector3.zero)).y;
                    float b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(0f, tmp.m_fontSize, 0f))).y;
                    if (Mathf.Abs(b - a) > 2f) FontHeights.Add(Mathf.Abs(b - a));
                }
            }
            return keepBackgrounds || content.width <= 0f ? Union(content, backgrounds) : content;
        }

        /// <summary>Covers most of the picture (background, veil, full-screen container).</summary>
        public static bool IsBackground(Rect r) => r.width >= VRUI.Width * 0.85f && r.height >= VRUI.Height * 0.85f;

        private static void AddMasks<T>(Il2CppArrayBase<T> masks, Camera cam) where T : Behaviour
        {
            for (int i = 0; i < masks.Length; i++)
            {
                var m = masks[i];
                if (m == null || !m.isActiveAndEnabled) continue;
                var rt = m.GetComponent<RectTransform>();
                if (rt == null) continue;
                maskTransforms.Add(m.transform);
                maskRects.Add(ScreenRect(rt, cam));
            }
        }

        /// <summary>For the skip set of Content.</summary>
        public static void Collect(Transform root, HashSet<IntPtr> into)
        {
            if (root == null) return;
            var graphics = root.GetComponentsInChildren<Graphic>(false);
            for (int i = 0; i < graphics.Length; i++) if (graphics[i] != null) into.Add(graphics[i].Pointer);
        }

        // ---- A quad showing part of the picture with holes in it.

        /// <summary>Quad of the crop minus the holes. UVs span the whole picture, so no material offset.</summary>
        public static void BuildHoled(Mesh mesh, Rect crop, List<Rect> holes)
        {
            var xs = new SortedSet<float> { crop.xMin, crop.xMax };
            var ys = new SortedSet<float> { crop.yMin, crop.yMax };
            var cut = new List<Rect>();
            foreach (var h in holes)
            {
                var c = Intersect(h, crop);
                if (c.width <= 0f) continue;
                cut.Add(c);
                xs.Add(c.xMin); xs.Add(c.xMax); ys.Add(c.yMin); ys.Add(c.yMax);
            }
            var xl = new List<float>(xs);
            var yl = new List<float>(ys);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            for (int i = 0; i + 1 < xl.Count; i++)
                for (int j = 0; j + 1 < yl.Count; j++)
                {
                    float x0 = xl[i], x1 = xl[i + 1], y0 = yl[j], y1 = yl[j + 1];
                    if (x1 - x0 < 0.01f || y1 - y0 < 0.01f) continue;
                    var centre = new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
                    bool inHole = false;
                    foreach (var h in cut) if (h.Contains(centre)) { inHole = true; break; }
                    if (inHole) continue;
                    int v = verts.Count;
                    foreach (var (x, y) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
                    {
                        verts.Add(new Vector3((x - crop.xMin) / crop.width - 0.5f, (y - crop.yMin) / crop.height - 0.5f, 0f));
                        uvs.Add(new Vector2(x / VRUI.Width, y / VRUI.Height));
                    }
                    tris.Add(v); tris.Add(v + 3); tris.Add(v + 1);
                    tris.Add(v + 3); tris.Add(v); tris.Add(v + 2);
                }
            mesh.Clear();
            mesh.vertices = verts.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
        }
    }
}
