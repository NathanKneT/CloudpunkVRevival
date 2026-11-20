using System;
using System.Collections.Generic;
using System.Text;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The game's HUD is laid out for a TV, so its corners end up too far out in a headset. Each piece is cut out of
    /// the UI texture and placed in a cone around the gaze (HudAngle wide, 5/8 tall, 3 deg below), keeping its corner.
    /// Small HUD text is 20-30 px, so 0.04 deg/px puts it at 0.8-1.2 deg.
    /// </summary>
    internal static class HudLayout
    {
        // in order of precedence, later pieces make room for earlier ones
        private enum Kind { Dialogue, Call, Jobs, Map, Radar, Unlock, Rest, Rest2, Rest3 }

        private const float TextAngle = 0.04f;     // deg per pixel at HudScale 1
        private const float PictureAngle = 0.035f; // minimap and the rest
        private const float RadarAngle = 11f;      // radar diameter in deg at HudScale 1
        private const float Gap = 1f;              // deg between pieces
        public const float Below = 3f;             // cone centre below the gaze
        public const float WindshieldBelow = 5f;   // cone centre below straight ahead of the seat

        // Windscreen lines and calls, deg from straight ahead of the seat: bottom ~3 above the dashboard top
        // (10-13 below), at most 34 wide so they fit between the dashboard's two screens (~18 each side).
        public const float LineBottom = -7.5f;
        public const float LineRight = 0.5f;
        private const float LineWidest = 34f;

        private sealed class Piece
        {
            public Kind Kind;
            public Transform Quad;
            public Material Material;
            public Rect Px;       // UI pixels, origin bottom-left
            public bool Moved;    // false: only cut out (shown on the wrist or the cockpit screen)
            public bool Shown;
            public float Scale;   // degrees per pixel
            public Rect Target;   // deg around the cone centre, x right, y up
            public Rect Deg;      // eases toward Target
            public bool Placed;   // room was found for it
            public Mesh Mesh;     // Rest pieces only: holed where other pieces are
            public string MeshKey = "";
        }

        private static readonly Piece[] pieces = new Piece[9];
        private static readonly List<Rect> placed = new List<Rect>();
        private static readonly List<Rect> holes = new List<Rect>();
        private static readonly HashSet<IntPtr> skip = new HashSet<IntPtr>();
        private static readonly List<Rect> clusters = new List<Rect>();
        private static float nextScan, halfW, halfH, side;
        private static bool hudShown, lastWindshield;

        public static void Create(Transform parent, int layer)
        {
            if (pieces[0] != null) return;
            for (int i = 0; i < pieces.Length; i++)
            {
                var kind = (Kind)i;
                // fixed order: rest 3001-3003, pieces 3004-3008, radar 3009-3011
                int queue = IsRest(kind) ? 3001 + (kind - Kind.Rest) : 3004 + (kind > Kind.Radar ? i - 1 : i);
                if (kind == Kind.Radar) { CockpitRadar.Create(parent, layer, 3009); pieces[i] = new Piece { Kind = kind }; continue; }
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "CloudpunkVR_HudPiece" + kind;
                UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
                quad.layer = layer;
                quad.transform.SetParent(parent, false);
                var mat = new Material(Shader.Find("UI/Default")) { name = quad.name, mainTexture = VRUI.Texture, hideFlags = HideFlags.DontUnloadUnusedAsset };
                mat.renderQueue = queue;
                var mr = quad.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                Mesh mesh = null;
                if (IsRest(kind))
                {
                    mesh = new Mesh { name = "CloudpunkVR_Hud" + kind, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    quad.GetComponent<MeshFilter>().sharedMesh = mesh;
                }
                quad.SetActive(false);
                pieces[i] = new Piece { Kind = kind, Quad = quad.transform, Material = mat, Mesh = mesh };
            }
        }

        private static bool IsRest(Kind kind) => kind >= Kind.Rest;

        /// <summary>After a settings change.</summary>
        public static void Relayout()
        {
            if (pieces[0] == null) return;
            Scan(lastWindshield);
            Layout();
        }

        /// <summary>Each frame from VRUI. Rescans 4 times a second, pieces ease to new places.</summary>
        public static void Update(bool hud, bool windshield, Vector3 eye, Quaternion centre, float distance)
        {
            if (pieces[0] == null) return;
            if (!hud)
            {
                if (hudShown) foreach (var p in pieces) Show(p, false);
                hudShown = false;
                CockpitRadar.Place(false, eye, centre, distance, 0f);
                nextScan = 0f; // relayout at once when the HUD comes back
                return;
            }
            lastWindshield = windshield;
            // rescan at once when a box starts fading in, so its piece shows the whole fade
            var g = Global.Instance;
            if (g != null && ((!pieces[(int)Kind.Dialogue].Shown && g.dialogUI != null && g.dialogUI.fadingIn) ||
                              (!pieces[(int)Kind.Call].Shown && g.incomingCallUI != null && g.incomingCallUI.fadingIn)))
                nextScan = 0f;
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.25f;
                Scan(windshield);
                Layout();
            }
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f);
            foreach (var p in pieces)
            {
                // clean frame: subtitles only, if it keeps them
                bool show = p.Moved && p.Placed && (p.Kind == Kind.Radar || p.Px.width > 1f) && Film.CleanFrame.ShowsHudPiece(Subtitle(p));
                if (show && !p.Shown) p.Deg = p.Target; // appear in place, no easing
                else if (show) p.Deg = Rect.MinMaxRect(Mathf.Lerp(p.Deg.xMin, p.Target.xMin, k), Mathf.Lerp(p.Deg.yMin, p.Target.yMin, k),
                                                       Mathf.Lerp(p.Deg.xMax, p.Target.xMax, k), Mathf.Lerp(p.Deg.yMax, p.Target.yMax, k));
                Show(p, show);
                if (!show) continue;
                // each piece faces the eyes
                Vector2 c = p.Deg.center;
                Quaternion dir = centre * Quaternion.Euler(-c.y, c.x, 0f);
                if (p.Kind == Kind.Radar) { CockpitRadar.Place(true, eye, dir, distance, p.Deg.width); continue; }
                p.Quad.SetPositionAndRotation(eye + dir * Vector3.forward * distance, dir);
                float span = UiPicture.Span(p.Deg.width, distance);
                p.Quad.localScale = new Vector3(span, span * p.Px.height / p.Px.width, 1f);
            }
            hudShown = true;
        }

        private static void Show(Piece p, bool show)
        {
            p.Shown = show;
            if (p.Kind == Kind.Radar) { if (!show) CockpitRadar.Place(false, default, Quaternion.identity, 1f, 0f); return; }
            if (show != p.Quad.gameObject.activeSelf) p.Quad.gameObject.SetActive(show);
        }

        // ---- Where the game draws each piece (UI pixels, origin bottom-left).

        private static void Scan(bool windshield)
        {
            var cam = CameraDriver.UiCamera;
            var g = Global.Instance;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            UiPicture.StartScan();
            skip.Clear();
            clusters.Clear();
            Rect dialogue = default, call = default, unlock = default;
            Canvas mapCanvas = null;
            if (cam != null && g != null)
            {
                try
                {
                    // dialogue and call boxes, plus the cockpit versions if on this picture
                    var d = g.dialogUI;
                    if (windshield && Patches.CockpitBoxes.KeepOnHud()) Patches.CockpitBoxes.Mirror(d);
                    DialogueReport = d == null ? "no dialogue box" : "";
                    if (d != null)
                    {
                        var box = d.exteriorCanvasGroupGlobal != null ? d.exteriorCanvasGroupGlobal.transform : d.transform;
                        dialogue = Framed(box, cam);
                        if (d.fadingIn || d.fadingOut) dialogue = Fading(dialogue, box, cam);
                        var inside = d.interiorCanvasGroupGlobal;
                        if (inside != null) dialogue = UiPicture.Union(dialogue, Framed(inside.transform, cam));
                        // dev builds only, skips the string work in play
                        if (VRConfig.DevHooks)
                        {
                            string cockpitBox = inside == null ? "none"
                                : "'" + inside.name + "' alpha " + UiPicture.GroupAlpha(inside.transform).ToString("0.00") + ", on this picture " + UiPicture.OnUiCamera(inside, cam);
                            DialogueReport = "box '" + box.name + "' active " + box.gameObject.activeInHierarchy + ", alpha " + UiPicture.GroupAlpha(box).ToString("0.00") +
                                             ", drawn " + UiPicture.Describe(dialogue) + "; cockpit box " + cockpitBox +
                                             "; in a dialogue " + g.IsInDialog(false) + ", showing " + d.IsActiveAndNotFadingOut();
                        }
                    }
                    var c = g.incomingCallUI;
                    if (c != null)
                    {
                        var box = c.exteriorCanvasGroupGlobal != null ? c.exteriorCanvasGroupGlobal.transform : c.transform;
                        call = Framed(box, cam);
                        if (c.fadingIn || c.fadingOut) call = Fading(call, box, cam);
                        if (c.interiorCanvasGroupGlobal != null) call = UiPicture.Union(call, Framed(c.interiorCanvasGroupGlobal.transform, cam));
                    }
                    var parents = g.hudParents;
                    var popup = parents != null && parents.unlockPopup != null ? parents.unlockPopup.transform : g.unlockedItemUI != null ? g.unlockedItemUI.transform : null;
                    unlock = Framed(popup, cam);

                    // everything else on the UI canvases, in clusters (not the minimap or the fade-to-black)
                    if (g.questsParent != null) UiPicture.Collect(g.questsParent.transform, skip);
                    if (g.m_BlackScreenCanvas != null) UiPicture.Collect(g.m_BlackScreenCanvas.transform, skip);
                    var small = Minimap.Instance != null ? Minimap.Instance.mapSmall : null;
                    var smallCanvas = small != null ? small.GetComponentInParent<Canvas>() : null;
                    mapCanvas = smallCanvas != null ? smallCanvas.rootCanvas : null;
                    foreach (var canvas in VRUI.UiCanvases)
                    {
                        if (canvas == null || (mapCanvas != null && canvas.Pointer == mapCanvas.Pointer)) continue;
                        Clusters(canvas.transform, cam, 0);
                    }
                }
                catch (Exception e) { Plugin.L.LogDebug("HUD scan: " + e.Message); /* unreadable through interop */ }
            }

            // map and jobs may be on the wrist. In the cockpit the game has its own screens for them.
            bool wrist = Wrist.TakesHud;
            Set(Kind.Dialogue, UiPicture.Margin(dialogue, 6f), true, TextAngle);
            Set(Kind.Call, UiPicture.Margin(call, 6f), true, TextAngle);
            // only while the game shows them (HUD toggle and fades)
            var mm = Minimap.Instance;
            bool mapShown = mm != null && mm.mapSmall != null && UiPicture.GroupAlpha(mm.mapSmall.transform) > 0.05f;
            bool jobsShown = g != null && g.questsParent != null && UiPicture.GroupAlpha(g.questsParent.transform) > 0.05f;
            // cockpit: the minimap stays on the dashboard (the radar piece is a separate setting)
            bool cockpit = CameraDriver.Mode == ViewMode.Cockpit;
            Set(Kind.Map, mapShown && !cockpit ? Wrist.MapRect : default, !wrist, PictureAngle);
            Set(Kind.Radar, default, CockpitRadar.Wanted(), 0f);
            Set(Kind.Jobs, jobsShown ? Wrist.JobsRect : default, !wrist && !windshield, TextAngle);
            Set(Kind.Unlock, UiPicture.Margin(unlock, 6f), true, TextAngle);

            // holes in the rest where the pieces are, so nothing draws twice
            holes.Clear();
            foreach (var p in pieces) if (!IsRest(p.Kind) && p.Px.width > 1f) holes.Add(p.Px);
            Merge();
            for (int i = 0; i < 3; i++)
            {
                var kind = Kind.Rest + i;
                Set(kind, i < clusters.Count ? clusters[i] : default, true, PictureAngle);
                var piece = pieces[(int)kind];
                if (piece.Px.width <= 1f) continue;
                var key = new StringBuilder(UiPicture.Describe(piece.Px));
                foreach (var h in holes) key.Append(UiPicture.Describe(h));
                if (key.ToString() == piece.MeshKey) continue;
                piece.MeshKey = key.ToString();
                UiPicture.BuildHoled(piece.Mesh, piece.Px, holes);
            }
            VRUI.NoteScan(clock);
        }

        /// <summary>
        /// One cluster per non-fullscreen child, so a tutorial on the left and race placings on the right stay apart.
        /// Full-screen children are containers and get recursed into.
        /// </summary>
        private static void Clusters(Transform node, Camera cam, int depth)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy) continue;
                var rt = child.GetComponent<RectTransform>();
                if (rt != null && depth < 4 && UiPicture.IsBackground(UiPicture.ScreenRect(rt, cam))) { Clusters(child, cam, depth + 1); continue; }
                var r = UiPicture.Content(child, cam, skip, false);
                if (r.width > 1f && r.height > 1f) clusters.Add(r);
            }
        }

        /// <summary>Merge clusters within 48 px, then fold everything past the third into the third.</summary>
        private static void Merge()
        {
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < clusters.Count && !merged; i++)
                    for (int j = i + 1; j < clusters.Count && !merged; j++)
                    {
                        if (UiPicture.Intersect(UiPicture.Margin(clusters[i], 48f), clusters[j]).width <= 0f) continue;
                        clusters[i] = UiPicture.Union(clusters[i], clusters[j]);
                        clusters.RemoveAt(j);
                        merged = true;
                    }
            }
            clusters.Sort((a, b) => (b.width * b.height).CompareTo(a.width * a.height));
            while (clusters.Count > 3)
            {
                clusters[2] = UiPicture.Union(clusters[2], clusters[clusters.Count - 1]);
                clusters.RemoveAt(clusters.Count - 1);
            }
        }

        /// <summary>Content rect, and adds its graphics to the skip set.</summary>
        private static Rect Framed(Transform root, Camera cam)
        {
            if (root == null) return default;
            UiPicture.Collect(root, skip);
            return UiPicture.Content(root, cam, null, false);
        }

        /// <summary>
        /// While a box fades, its text groups are under 5% and the scan finds nothing. Use the box's own rect then.
        /// </summary>
        private static Rect Fading(Rect found, Transform box, Camera cam)
        {
            if (found.width > 1f || box == null || !box.gameObject.activeInHierarchy || !UiPicture.OnUiCamera(box, cam) || UiPicture.GroupAlpha(box) < 0.05f) return found;
            var rt = box.GetComponent<RectTransform>();
            return rt != null ? UiPicture.Intersect(UiPicture.ScreenRect(rt, cam), UiPicture.Full) : found;
        }

        private static void Set(Kind kind, Rect px, bool moved, float scale)
        {
            var p = pieces[(int)kind];
            p.Px = px.width > 1f && px.height > 1f ? px : default;
            p.Moved = moved;
            p.Scale = scale * VRConfig.HudScale.Value;
            if (!IsRest(kind) && kind != Kind.Radar && p.Px.width > 1f)
            {
                p.Material.mainTextureScale = new Vector2(p.Px.width / VRUI.Width, p.Px.height / VRUI.Height);
                p.Material.mainTextureOffset = new Vector2(p.Px.x / VRUI.Width, p.Px.y / VRUI.Height);
            }
        }

        // ---- Layout (degrees around the cone centre).

        private static void Layout()
        {
            halfW = VRConfig.HudAngle.Value * 0.5f;
            halfH = halfW * 0.625f;
            side = 0.2f * VRConfig.HudAngle.Value;
            placed.Clear();
            foreach (var p in pieces)
            {
                p.Placed = false;
                if (!p.Moved || (p.Kind != Kind.Radar && p.Px.width <= 1f)) continue;
                if (!TryPlace(p, out p.Target)) continue; // no room: hidden, not shrunk past legible
                p.Placed = true;
                placed.Add(p.Target);
            }
        }

        private static bool Subtitle(Piece p) => p.Kind == Kind.Dialogue || p.Kind == Kind.Call;

        /// <summary>Size factors from 1 down, floored at 0.03 deg/px for text and 0.025 for pictures.</summary>
        private static IEnumerable<float> Sizes(Piece p, float smallest)
        {
            float floor = p.Kind == Kind.Radar ? 0f : (p.Kind == Kind.Map || IsRest(p.Kind) ? 0.025f : 0.03f) / Mathf.Max(1e-5f, p.Scale);
            for (float f = 1f; f > smallest - 0.001f && f >= floor - 0.001f; f -= 0.05f) yield return f;
        }

        private static bool TryPlace(Piece p, out Rect r)
        {
            // home, down to 70%
            foreach (float f in Sizes(p, 0.7f))
            {
                r = Home(p, f);
                if (Fits(p, r)) return true;
            }
            // beside whatever is in the way, smallest move, down to 50%
            foreach (float f in Sizes(p, 0.5f))
            {
                var home = Home(p, f);
                Rect best = default;
                float bestMove = float.MaxValue;
                foreach (var q in placed)
                {
                    if (!Overlap(home, q)) continue;
                    Consider(p, new Rect(q.xMin - Gap - home.width, home.y, home.width, home.height), home, ref best, ref bestMove);
                    Consider(p, new Rect(q.xMax + Gap, home.y, home.width, home.height), home, ref best, ref bestMove);
                    Consider(p, new Rect(home.x, q.yMin - Gap - home.height, home.width, home.height), home, ref best, ref bestMove);
                    Consider(p, new Rect(home.x, q.yMax + Gap, home.width, home.height), home, ref best, ref bestMove);
                }
                if (bestMove < float.MaxValue) { r = best; return true; }
            }
            // home, down to 50%
            foreach (float f in Sizes(p, 0.5f))
            {
                r = Home(p, f);
                if (Fits(p, r)) return true;
            }
            // subtitles always show, the rest only if it covers nothing
            r = Home(p, 1f);
            if (Subtitle(p)) return true;
            if (!IsRest(p.Kind)) return false;
            foreach (var q in placed) if (Overlap(r, q)) return false;
            return true;
        }

        private static void Consider(Piece p, Rect candidate, Rect home, ref Rect best, ref float bestMove)
        {
            var r = Inside(candidate);
            if (!Fits(p, r)) return;
            float move = (r.center - home.center).magnitude;
            if (move < bestMove) { bestMove = move; best = r; }
        }

        /// <summary>
        /// Home spot at size factor f. Subtitles sit low and centred, at most 3/4 of the cone wide (or LineWidest on
        /// the windscreen). Others keep their screen corner, or their offset from centre if in the middle third.
        /// </summary>
        private static Rect Home(Piece p, float f)
        {
            if (p.Kind == Kind.Radar)
            {
                float d = Mathf.Min(RadarAngle * VRConfig.HudScale.Value * f * 1.06f, 2f * halfH);
                return Inside(new Rect(side, -4f, d, d));
            }
            float k = p.Scale * f;
            Rect px = p.Px;
            float w = px.width * k, h = px.height * k;
            if (Subtitle(p))
            {
                float widest = lastWindshield ? Mathf.Min(1.5f * halfW, LineWidest) : 1.5f * halfW;
                float fit = Mathf.Min(1f, widest / w);
                fit = Mathf.Max(fit, Mathf.Min(1f, 0.03f / k));       // legible before narrow
                fit = Mathf.Min(fit, 2f * halfW / w, 2f * halfH / h);  // never past the cone
                w *= fit;
                h *= fit;
                if (lastWindshield) return Inside(new Rect(LineRight - w * 0.5f, LineBottom + WindshieldBelow, w, h));
                return Inside(new Rect(-w * 0.5f, -halfH + px.yMin * k * fit, w, h));
            }
            float cone = Mathf.Min(1f, 2f * halfW / w, 2f * halfH / h);
            w *= cone;
            h *= cone;
            float x = Along(px.xMin, px.xMax, VRUI.Width, k, halfW, w);
            float y = Along(px.yMin, px.yMax, VRUI.Height, k, halfH, h);
            return Inside(new Rect(x - w * 0.5f, y - h * 0.5f, w, h));
        }

        private static float Along(float min, float max, float screen, float k, float half, float size)
        {
            float middle = (min + max) * 0.5f;
            if (middle < screen / 3f) return -half + min * k + size * 0.5f;
            if (middle > screen * 2f / 3f) return half - (screen - max) * k - size * 0.5f;
            return (middle - screen * 0.5f) * k;
        }

        private static Rect Inside(Rect r) => new Rect(
            Mathf.Clamp(r.x, -halfW, Mathf.Max(-halfW, halfW - r.width)),
            Mathf.Clamp(r.y, -halfH, Mathf.Max(-halfH, halfH - r.height)), r.width, r.height);

        /// <summary>
        /// No overlap. Map, radar and jobs stay out of the middle band (HudAngle/5 each side), and only subtitles go
        /// more than 8 deg below the gaze there, where the hands are.
        /// </summary>
        private static bool Fits(Piece p, Rect r)
        {
            foreach (var q in placed) if (Overlap(r, q)) return false;
            bool middle = r.xMin < side && r.xMax > -side;
            if ((p.Kind == Kind.Map || p.Kind == Kind.Radar || p.Kind == Kind.Jobs) && middle) return false;
            if (!Subtitle(p) && middle && r.yMin < -8f + Below) return false;
            return true;
        }

        private static bool Overlap(Rect a, Rect b) =>
            a.xMin < b.xMax + Gap && a.xMax > b.xMin - Gap && a.yMin < b.yMax + Gap && a.yMax > b.yMin - Gap;

        /// <summary>Dev: how the last scan saw the dialogue box.</summary>
        public static string DialogueReport { get; private set; } = "not scanned";

        /// <summary>kind is a Kind name, any case.</summary>
        public static bool Showing(string kind)
        {
            foreach (var p in pieces)
                if (p != null && p.Shown && string.Equals(p.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Dev `call`: a shown piece's eased place, quad, scale and source rect.</summary>
        public static bool Where(string kind, out Rect deg, out Transform quad, out float perPixel, out Rect px)
        {
            foreach (var p in pieces)
                if (p != null && p.Shown && p.Quad != null && string.Equals(p.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
                {
                    deg = p.Deg;
                    quad = p.Quad;
                    px = p.Px;
                    perPixel = p.Px.width > 1f ? p.Deg.width / p.Px.width : 0f;
                    return true;
                }
            deg = px = default;
            quad = null;
            perPixel = 0f;
            return false;
        }

        public static bool OnWindshield => lastWindshield;

        public static string ShownPieces()
        {
            var sb = new StringBuilder();
            foreach (var p in pieces)
                if (p != null && p.Shown) sb.Append(sb.Length > 0 ? ", " : "").Append(p.Kind);
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        public static string Describe()
        {
            var sb = new StringBuilder($"cone {halfW * 2f:0} x {halfH * 2f:0} deg, centred {(lastWindshield ? $"{WindshieldBelow:0} deg below straight ahead of the seat, lines and calls from {LineBottom:0.0} deg up, {LineRight:0.0} right" : $"{Below:0} deg below the gaze")}");
            foreach (var p in pieces)
            {
                if (p == null || (p.Kind != Kind.Radar && p.Px.width <= 1f) || (p.Kind == Kind.Radar && !p.Moved)) continue;
                sb.Append($"; {p.Kind} {UiPicture.Describe(p.Px)}");
                if (!p.Moved) sb.Append(" cut out (on the wrist or the cockpit's screen)");
                else if (!p.Placed) sb.Append(" hidden: no room left in the cone");
                else
                {
                    sb.Append($" -> x {p.Target.xMin:0.0}..{p.Target.xMax:0.0}, y {p.Target.yMin:0.0}..{p.Target.yMax:0.0} deg from the centre");
                    if (p.Kind != Kind.Radar) sb.Append($" at {p.Target.width / p.Px.width:0.000} deg per pixel");
                }
            }
            sb.Append("; ").Append(CockpitRadar.Describe());
            return sb.ToString();
        }
    }
}
