using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using TMPro;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Look at the back of the left wrist for district, jobs and the last dialogue lines (the game keeps no log).
    /// On foot it also takes the minimap off the HUD. Text is TMP meshes, not a world-space canvas: that draws
    /// before the wrist is placed and lags behind in flight.
    /// </summary>
    internal static class Wrist
    {
        private const float CardW = 0.22f, CardH = 0.12f;   // meters
        private const float Unit = 0.0001f;                  // text unit: 0.1 mm
        private const float TmpScale = 10f;                  // TMP outside a canvas draws 10x smaller
        private const float MapLift = 0.0005f, TextLift = 0.001f; // toward the eyes
        private const int MaxLines = 12, ShownLines = 4;
        private static readonly Color Amber = new Color32(245, 166, 35, 255);
        private static readonly Color Implant = new Color32(81, 168, 221, 255);
        private static readonly Color Band = new Color32(11, 18, 32, 255);

        private static GameObject root;
        private static Transform card, mapQuad, textT;
        private static Material cardMat, mapMat, textMat;
        private static TextMeshPro header, jobs, log;
        private static string headerText = "", jobsText = "", logText = "";
        private static bool headerDirty, jobsDirty, logDirty; // mesh needs a rebuild
        private static float alpha, textAlpha = -1f, nextText, nextScan, nextFontSearch, validAt = -10f;
        private static bool fontsFound;
        private static bool shown, mapShown;
        private static bool laidOut;
        private static float laidOutMapW; // 0 without the minimap

        // anchor space, held while fading out
        private static Vector3 heldPos;
        private static Quaternion heldRot = Quaternion.identity;

        // for the dev readout
        private static Vector3 lastPos;
        private static Quaternion lastRot = Quaternion.identity;
        private static int placedFrame = -10;
        private static float frameMoveMm, peakMoveMm;

        private static readonly List<(string who, string line)> lines = new List<(string, string)>();
        private static DialogHUD dialog;
        private static float nextDialogSearch, nextDialogRead;
        private static string lastLine = "";
        private static readonly Regex Tags = new Regex("<[^>]*>");

        // dev "wrist show": in front of the head, no controllers needed
        public static bool ForceShow;

        /// <summary>Minimap and jobs blocks in UI pixels, empty when not drawn.</summary>
        public static Rect MapRect { get; private set; }
        public static Rect JobsRect { get; private set; }

        public static bool Shown => root != null && root.activeSelf;

        /// <summary>On foot the minimap and jobs move to the wrist.</summary>
        public static bool TakesHud => root != null && VRConfig.WristDisplay.Value && CameraDriver.Mode == ViewMode.OnFoot
                                       && !VRUI.Interactive && Time.unscaledTime - validAt < 2f;

        public static void Create(Transform parent, int layer)
        {
            if (root != null) return;
            root = new GameObject("CloudpunkVR_Wrist") { layer = layer };
            root.transform.SetParent(parent, false);

            card = MakeQuad("Card", layer, out cardMat, MakeCardTexture(), 1);
            mapQuad = MakeQuad("Minimap", layer, out mapMat, VRUI.Texture, 2);
            // off until it has a crop, or it shows the whole UI texture metre-wide
            mapQuad.gameObject.SetActive(false);

            var textGo = new GameObject("CloudpunkVR_WristText") { layer = layer };
            textT = textGo.transform;
            textT.SetParent(root.transform, false);
            textT.localPosition = new Vector3(0f, 0f, -TextLift);
            textT.localScale = Vector3.one * Unit;

            header = MakeText("Header", layer, 100f, Amber);
            jobs = MakeText("Jobs", layer, 72f, Color.white);
            log = MakeText("Lines", layer, 60f, new Color(0.86f, 0.9f, 0.95f, 1f));
            LayOut(false);
            root.SetActive(false);
        }

        /// <summary>The game's HUD font, once loaded (not there yet at mod start).</summary>
        private static void FindFonts()
        {
            if (fontsFound || Time.unscaledTime < nextFontSearch) return;
            nextFontSearch = Time.unscaledTime + 2f;
            TMP_FontAsset hud = null, body = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f == null) continue;
                if (f.name == "Aquawax-Pro-DemiBold") hud = f;
                else if (f.name == "RistrettoPro-Regular") body = f;
            }
            var font = hud ?? body;
            if (font == null) return;
            // copy of the font material, same queue as the card so sortingOrder 1/2/3 decides
            if (font.material != null)
                textMat = new Material(font.material) { name = "CloudpunkVR_WristText", renderQueue = cardMat.renderQueue, hideFlags = HideFlags.DontUnloadUnusedAsset };
            foreach (var t in new[] { header, jobs, log })
            {
                t.font = font;
                if (textMat != null) t.fontSharedMaterial = textMat;
            }
            fontsFound = true;
            textAlpha = -1f;
            Plugin.L.LogInfo($"Wrist display: font {font.name}, text queue {(textMat != null ? textMat.renderQueue : -1)} (card {cardMat.renderQueue}).");
        }

        private static Transform MakeQuad(string name, int layer, out Material mat, Texture tex, int order)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "CloudpunkVR_Wrist" + name;
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.layer = layer;
            quad.transform.SetParent(root.transform, false);
            mat = new Material(Shader.Find("UI/Default")) { name = quad.name, mainTexture = tex, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.sortingOrder = order;
            Quiet(mr);
            return quad.transform;
        }

        private static void Quiet(MeshRenderer mr)
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.allowOcclusionWhenDynamic = false;
        }

        private static TextMeshPro MakeText(string name, int layer, float size, Color color)
        {
            var go = new GameObject("CloudpunkVR_Wrist" + name) { layer = layer };
            go.transform.SetParent(textT, false);
            var t = go.AddComponent<TextMeshPro>();
            t.fontSize = size * TmpScale;
            t.color = color;
            t.richText = true;
            t.enableWordWrapping = true;
            t.overflowMode = TextOverflowModes.Truncate;
            t.alignment = TextAlignmentOptions.TopLeft;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sortingOrder = 3;
            Quiet(mr);
            return t;
        }

        /// <summary>Dialogue-box navy, implant-blue top edge, soft sides.</summary>
        private static Texture2D MakeCardTexture()
        {
            const int w = 128, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "CloudpunkVR_WristCard", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float side = Mathf.Clamp01(Mathf.Min(x, w - 1 - x) / 6f);
                    Color c = new Color(Band.r, Band.g, Band.b, 0.93f * side);
                    if (y >= h - 2) c = new Color(Implant.r, Implant.g, Implant.b, side);              // top edge
                    else if (y == h - 3) c = new Color(Implant.r * 0.5f, Implant.g * 0.5f, Implant.b * 0.6f, 0.9f * side);
                    if ((x < 10 || x > w - 11) && y < 2) c = new Color(Amber.r, Amber.g, Amber.b, 0.9f); // corner ticks
                    tex.SetPixel(x, y, c);
                }
            tex.Apply(false);
            return tex;
        }

        /// <summary>After the hands and the panel are placed.</summary>
        public static void Update()
        {
            if (root == null) return;
            try
            {
                ReadDialogue();
                if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.5f; ScanHud(); }
                Place();
                if (traceUntil > 0f) TraceTick();
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Wrist display: " + e.Message);
                root.SetActive(false);
                alpha = 0f;
            }
        }

        private static void Place()
        {
            var w = VoxelHands.LeftWrist;
            if (w.Valid) validAt = Time.unscaledTime;
            var mode = CameraDriver.Mode;
            // clean frame: off at once, even with `wrist show`
            if (Film.CleanFrame.HidesOverlays)
            {
                shown = false;
                alpha = 0f;
                if (root.activeSelf) root.SetActive(false);
                return;
            }
            bool want = VRConfig.WristDisplay.Value && w.Valid && !VRUI.Interactive && (mode == ViewMode.OnFoot || mode == ViewMode.Cockpit);
            Vector3 head = CameraDriver.HeadWorldPosition;
            Vector3 pos = default;
            if (ForceShow && !VRUI.Interactive)
            {
                validAt = Time.unscaledTime;
                pos = head + CameraDriver.HeadWorldRotation * new Vector3(-0.06f, -0.10f, 0.42f);
            }
            else if (want)
            {
                pos = w.Position - w.Forward * (0.06f * w.Scale) + w.Back * 0.04f;
                Vector3 toHead = head - pos;
                float dist = toHead.magnitude;
                Vector3 dir = toHead / Mathf.Max(dist, 1e-4f);
                float facing = Vector3.Dot(w.Back, dir);
                float inView = Vector3.Dot(CameraDriver.HeadWorldRotation * Vector3.forward, -dir);
                // hysteresis so it doesn't flicker at the edge
                want = dist < 0.8f && facing > (shown ? 0.4f : 0.6f) && inView > (shown ? 0.55f : 0.72f);
            }
            if (ForceShow && !VRUI.Interactive) want = true;
            if (want && !shown) Haptics.Pulse(true, 0.015f, 0.25f, 210f);
            shown = want;
            alpha = Mathf.MoveTowards(alpha, want ? 1f : 0f, Time.unscaledDeltaTime * 7f);
            if (alpha <= 0.001f) { if (root.activeSelf) root.SetActive(false); return; }
            if (!root.activeSelf) root.SetActive(true);
            Quaternion rot, toAnchor = Quaternion.Inverse(CameraDriver.AnchorRotation);
            if (want)
            {
                // face the eyes, upright to the head
                rot = Quaternion.LookRotation(pos - head, CameraDriver.HeadWorldRotation * Vector3.up);
                heldPos = toAnchor * (pos - CameraDriver.AnchorPosition);
                heldRot = toAnchor * rot;
            }
            else
            {
                // fading out: hold it in anchor space so it isn't left behind in flight
                pos = CameraDriver.AnchorPosition + CameraDriver.AnchorRotation * heldPos;
                rot = CameraDriver.AnchorRotation * heldRot;
            }
            root.transform.SetPositionAndRotation(pos, rot);
            NoteMove(pos, rot);
            FindFonts();
            card.localScale = new Vector3(CardW, CardH, 1f);
            cardMat.color = new Color(1f, 1f, 1f, alpha);
            mapMat.color = new Color(1f, 1f, 1f, alpha);
            if (Time.unscaledTime >= nextText) { nextText = Time.unscaledTime + 0.25f; Fill(); }
            bool fade = alpha != textAlpha;
            if (fade)
            {
                // <alpha> tags in the lines keep the lower of the two
                textAlpha = alpha;
                header.alpha = jobs.alpha = log.alpha = alpha;
            }
            // rebuild now so the text matches this frame's placement, only what changed
            if ((fade || headerDirty) && header.gameObject.activeSelf) header.ForceMeshUpdate();
            if (fade || jobsDirty) jobs.ForceMeshUpdate();
            if (fade || logDirty) log.ForceMeshUpdate();
            headerDirty = jobsDirty = logDirty = false;
        }

        /// <summary>Dev readout: largest corner movement since last frame, in mm.</summary>
        private static void NoteMove(Vector3 pos, Quaternion rot)
        {
            int frame = Time.frameCount;
            frameMoveMm = 0f;
            if (placedFrame == frame - 1)
                for (int i = 0; i < 4; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? -CardW * 0.5f : CardW * 0.5f, (i & 2) == 0 ? -CardH * 0.5f : CardH * 0.5f, 0f);
                    frameMoveMm = Mathf.Max(frameMoveMm, ((pos + rot * c) - (lastPos + lastRot * c)).magnitude * 1000f);
                }
            peakMoveMm = Mathf.Max(peakMoveMm, frameMoveMm);
            traceLinePeak = Mathf.Max(traceLinePeak, frameMoveMm);
            traceRunPeak = Mathf.Max(traceRunPeak, frameMoveMm);
            lastPos = pos;
            lastRot = rot;
            placedFrame = frame;
        }

        private static void Fill()
        {
            var g = Global.Instance;
            bool map = TakesHud && MapRect.width > 8f && VRUI.Texture != null;
            if (map)
            {
                // minimap crop on the left of the card
                float h = CardH * 0.9f, wq = MapWidth();
                mapQuad.localPosition = new Vector3(-CardW * 0.5f + 0.006f + wq * 0.5f, 0f, -MapLift);
                mapQuad.localScale = new Vector3(wq, h, 1f);
                mapMat.mainTextureScale = new Vector2(MapRect.width / VRUI.Width, MapRect.height / VRUI.Height);
                mapMat.mainTextureOffset = new Vector2(MapRect.x / VRUI.Width, MapRect.y / VRUI.Height);
            }
            if (map != mapShown) { mapShown = map; mapQuad.gameObject.SetActive(map); }
            if (LayOut(map)) headerDirty = jobsDirty = logDirty = true;

            string sector = "";
            try { sector = g?.textSectorNameInterior?.text ?? ""; } catch (Exception) { }
            SetText(header, ref headerText, string.IsNullOrEmpty(sector) ? "NIVALIS" : sector.ToUpperInvariant(), ref headerDirty);

            var sb = new StringBuilder();
            int shownJobs = 0;
            var quests = g?.quests;
            if (quests != null)
                for (int i = 0; i < quests.Length && shownJobs < 2; i++)
                {
                    var q = quests[i];
                    if (q == null || !q.isActiveAndEnabled) continue;
                    var qt = q.questText;
                    if (qt == null || string.IsNullOrEmpty(qt.text)) continue;
                    float d = Vector3.Distance(CameraDriver.HeadWorldPosition, qt.markerPos);
                    string dist = d < 1000f ? $"{d:0} m" : d < 9000f ? $"{d / 1000f:0.0} km" : "";
                    sb.Append($"<color=#F5A623>{Clean(qt.characterName)}</color>  <color=#7FC8F0>{dist}</color>\n{Clean(qt.text)}\n");
                    shownJobs++;
                }
            SetText(jobs, ref jobsText, shownJobs == 0 ? "<color=#7FC8F0>No job right now</color>" : sb.ToString().TrimEnd(), ref jobsDirty);

            sb.Clear();
            int first = Math.Max(0, lines.Count - ShownLines);
            for (int i = first; i < lines.Count; i++)
            {
                int age = lines.Count - 1 - i; // 0 = latest
                string a = age == 0 ? "FF" : age == 1 ? "CC" : "99";
                var (who, line) = lines[i];
                sb.Append($"<alpha=#{a}>{(string.IsNullOrEmpty(who) ? "" : $"<color=#F5A623>{who}</color>  ")}{line}\n");
            }
            SetText(log, ref logText, sb.ToString().TrimEnd(), ref logDirty);
        }

        private static float MapWidth() => CardH * 0.9f * MapRect.width / Mathf.Max(1f, MapRect.height);

        private static void SetText(TextMeshPro t, ref string last, string text, ref bool dirty)
        {
            if (text == last) return;
            last = text;
            t.text = text;
            dirty = true;
        }

        /// <summary>0.1 mm units from the card centre, no header beside the minimap. True if the layout changed.</summary>
        private static bool LayOut(bool map)
        {
            float mapW = map ? MapWidth() : 0f;
            if (laidOut && Mathf.Abs(mapW - laidOutMapW) < 0.0005f) return false;
            laidOut = true;
            laidOutMapW = mapW;
            float textLeft = map ? 0.012f + mapW : 0.012f;
            float tw = (CardW - textLeft - 0.01f) / Unit;
            float left = textLeft / Unit - CardW / Unit * 0.5f;
            float top = CardH / Unit * 0.5f - 70f;
            if (!map) { SetRect(header, left, top, tw, 130f); top -= 140f; }
            header.gameObject.SetActive(!map);
            SetRect(jobs, left, top, tw, 420f);
            float logTop = top - 440f, bottom = -CardH / Unit * 0.5f + 40f;
            SetRect(log, left, logTop, tw, Mathf.Max(100f, logTop - bottom));
            return true;
        }

        private static void SetRect(TextMeshPro t, float left, float top, float width, float height)
        {
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.localPosition = new Vector3(left, top, 0f); // pivot is top left
        }

        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : Tags.Replace(s, "").Trim();

        // ---- Dialogue lines, read from the game's dialogue box.

        /// <summary>A mod line in the log. replaceOwn overwrites the same speaker's note if still last.</summary>
        public static void Note(string who, string line, bool replaceOwn = false)
        {
            int last = lines.Count - 1;
            if (replaceOwn && lastIsNote && last >= 0 && lines[last].who == who) lines[last] = (who, line);
            else lines.Add((who, line));
            if (lines.Count > MaxLines) lines.RemoveAt(0);
            nextText = 0f;
            noteAfter = lastIsNote = true;
        }

        private static bool noteAfter; // a note came after the last game line
        private static bool lastIsNote; // last entry is a mod note
        private static float grewAt = -10f;

        /// <summary>A dialogue line is still typing out.</summary>
        public static bool Typing => Time.unscaledTime - grewAt < 0.5f;

        // dev "wrist type <who> <words>"
        private const float TestLetters = 20f; // per second
        private static string testWho, testLine;
        private static float testFrom;
        private static int grewTimes;

#if DEV
        /// <summary>Fakes a line typing out, read through the same path as the game's.</summary>
        public static void TypeTest(string who, string line)
        {
            testWho = who;
            testLine = line;
            testFrom = Time.unscaledTime;
        }
#endif

        private static void ReadDialogue()
        {
            if (Time.unscaledTime < nextDialogRead) return;
            nextDialogRead = Time.unscaledTime + 0.2f;
            string who, line;
            if (testLine != null)
            {
                int shown = Math.Min(testLine.Length, 1 + (int)((Time.unscaledTime - testFrom) * TestLetters));
                who = testWho;
                line = testLine.Substring(0, shown);
                if (shown == testLine.Length) testLine = null;
            }
            else if (!ReadBox(out who, out line)) return;
            if (string.IsNullOrEmpty(line) || line == lastLine) return;
            if (line.StartsWith("This is text", StringComparison.Ordinal)) return; // the box's placeholder text
            // a line typing out replaces its shorter self, even if a note came after it
            int growing = -1;
            if (lastLine.Length > 0 && line.StartsWith(lastLine, StringComparison.Ordinal))
                for (int i = lines.Count - 1; i >= 0 && growing < 0; i--)
                    if (lines[i].line == lastLine) growing = i;
            if (growing >= 0)
            {
                grewAt = Time.unscaledTime;
                grewTimes++;
                if (noteAfter && growing < lines.Count - 1)
                    Plugin.L.LogInfo($"Wrist display: a line typing out ({(string.IsNullOrEmpty(who) ? "no name" : who)}) grew past a note after it ('{lines[lines.Count - 1].who}: {lines[lines.Count - 1].line}'); the note stays.");
                lines[growing] = (who, line);
            }
            else
            {
                lines.Add((who, line));
                if (lines.Count > MaxLines) lines.RemoveAt(0);
            }
            if (growing < 0 || growing == lines.Count - 1) lastIsNote = false;
            if (growing < lines.Count - 1) noteAfter = false;
            lastLine = line;
        }

        /// <summary>Speaker and visible text, from the exterior box or else the cockpit one.</summary>
        private static bool ReadBox(out string who, out string line)
        {
            who = line = null;
            if (dialog == null)
            {
                if (Time.unscaledTime < nextDialogSearch) return false;
                nextDialogSearch = Time.unscaledTime + 3f;
                foreach (var d in Resources.FindObjectsOfTypeAll<DialogHUD>())
                    if (d != null && d.gameObject.scene.IsValid()) { dialog = d; break; }
                if (dialog == null) return false;
            }
            if (!dialog.gameObject.activeInHierarchy) return false;
            who = Clean(Read(dialog.nameObj));
            line = Clean(Read(dialog.textField));
            if (string.IsNullOrEmpty(line)) { who = Clean(Read(dialog.nameObjInterior)); line = Clean(Read(dialog.textFieldInterior)); }
            return true;
        }

        private static string Read(UnityEngine.Object o)
        {
            if (o == null) return null;
            var tmp = o.TryCast<TMP_Text>();
            if (tmp != null) return tmp.text;
            var go = o.TryCast<GameObject>();
            if (go != null) { var t = go.GetComponentInChildren<TMP_Text>(true); return t != null ? t.text : null; }
            var c = o.TryCast<Component>();
            if (c != null) { var t = c.GetComponentInChildren<TMP_Text>(true); return t != null ? t.text : null; }
            return null;
        }

        // ---- Minimap and jobs rects (UI pixels, origin bottom-left).

        private static void ScanHud()
        {
            var cam = CameraDriver.UiCamera;
            if (cam == null) { MapRect = JobsRect = default; return; }
            Rect map = default;
            var small = Minimap.Instance != null ? Minimap.Instance.mapSmall : null;
            if (small != null && small.isActiveAndEnabled && small.alpha > 0.05f && UiPicture.OnUiCamera(small, cam))
            {
                var srt = small.GetComponent<RectTransform>();
                map = UiPicture.ScreenRect(srt, cam);
                float xMin = map.xMin, xMax = map.xMax;
                // include money/fuel/district lines just outside it, not the far-reaching markers
                for (int i = 0; i < srt.childCount; i++)
                {
                    var c = srt.GetChild(i).GetComponent<RectTransform>();
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    var r = UiPicture.ScreenRect(c, cam);
                    if (r.xMin >= xMin - 24f && r.xMax <= xMax + 24f && r.yMin > -24f) map = UiPicture.Union(map, r);
                }
            }
            MapRect = UiPicture.Margin(map, 6f); // so no sliver stays in the HUD

            Rect jobsRect = default;
            var parent = Global.Instance != null ? Global.Instance.questsParent : null;
            if (parent != null && parent.activeInHierarchy)
            {
                var t = parent.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i).GetComponent<RectTransform>();
                    if (c == null || !c.gameObject.activeInHierarchy || c.GetComponent<Quest>() == null) continue;
                    jobsRect = UiPicture.Union(jobsRect, UiPicture.ScreenRect(c, cam));
                }
            }
            JobsRect = UiPicture.Margin(jobsRect, 6f);
        }

        public static string Describe()
        {
            string box = "no dialogue box found";
            if (dialog != null)
            {
                try
                {
                    box = $"dialogue box '{dialog.name}' active {dialog.gameObject.activeInHierarchy}, name '{Clean(Read(dialog.nameObj))}', text '{Clean(Read(dialog.textField))}'," +
                          $" cockpit name '{Clean(Read(dialog.nameObjInterior))}', cockpit text '{Clean(Read(dialog.textFieldInterior))}'";
                }
                catch (Exception e) { box = "dialogue box unreadable: " + e.Message; }
            }
            return $"wrist shown {shown} (alpha {alpha:0.00}), wrist tracked {VoxelHands.LeftWrist.Valid}, takes HUD {TakesHud}, map {MapRect}, jobs {JobsRect}, {lines.Count} lines, " +
                   $"a line grew as it typed out {grewTimes} time(s){(testLine != null ? " (a test line typing)" : "")}" +
                   (lines.Count > 0 ? $", last: {lines[lines.Count - 1].who}: {lines[lines.Count - 1].line}" : "") + "; " + box;
        }

        // ---- Dev readout.

        private const float PlaneTolMm = 2f, EdgeTolMm = 1f;
        private static float traceStart, traceUntil, nextTraceLine, traceLinePeak, traceRunPeak;
        private static int traceLines, traceFailedLines;

#if DEV
        /// <summary>Dev `wrist parts`: checks every part is on the card, inside it, overlay camera only, no canvas.</summary>
        public static string DescribeParts()
        {
            var details = new StringBuilder();
            var problems = new StringBuilder();
            int on = CheckParts(details, problems);
            string head = $"wrist parts check {(problems.Length == 0 ? "ok" : "FAILED:" + problems)}: {on} part(s) drawn, card {CardW * 1000f:0} x {CardH * 1000f:0} mm" +
                          $", placed in the render hook {Time.frameCount - placedFrame} frame(s) ago, the card moved {frameMoveMm:0} mm in the world in its last frame" +
                          $" (peak {peakMoveMm:0} mm since the last look: what words on a canvas would have trailed it by)";
            peakMoveMm = 0f;
            return head + details;
        }
#endif

        /// <summary>Returns how many parts are drawn. Problems are appended as " ...;".</summary>
        private static int CheckParts(StringBuilder details, StringBuilder problems)
        {
            if (root == null) { problems.Append(" no wrist display;"); return 0; }
            if (!root.activeInHierarchy) { problems.Append(" the wrist is hidden (raise it, or `wrist show`);"); return 0; }
            var overlay = CameraDriver.OverlayCamera;
            var cams = Camera.allCameras;
            var rs = root.GetComponentsInChildren<Renderer>(true);
            var rootT = root.transform;
            int on = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null) continue;
                string name = r.gameObject.name.Replace("CloudpunkVR_Wrist", "");
                if (!r.enabled || !r.gameObject.activeInHierarchy) { details.Append("; ").Append(name).Append(" off"); continue; }
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || mesh.vertexCount == 0) { details.Append("; ").Append(name).Append(" empty"); continue; }
                on++;
                // mesh bounds into card space, mm, z toward the eyes
                Bounds b = mesh.bounds;
                Vector3 min = Vector3.one * float.MaxValue, max = Vector3.one * float.MinValue;
                var t = r.transform;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    var p = rootT.InverseTransformPoint(t.TransformPoint(corner)) * 1000f;
                    p.z = -p.z;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                // cameras that draw its layer, plus the manually rendered cockpit camera
                int layerBit = 1 << r.gameObject.layer;
                var by = new StringBuilder();
                int count = 0;
                bool byOverlay = false;
                for (int k = 0; k < cams.Length; k++)
                {
                    var cam = cams[k];
                    if (cam == null || (cam.cullingMask & layerBit) == 0) continue;
                    if (count++ > 0) by.Append(", ");
                    by.Append(cam.name);
                    if (cam == overlay) byOverlay = true;
                }
                var cockpit = CameraDriver.CockpitCamera;
                if (cockpit != null && !cockpit.enabled && (cockpit.cullingMask & layerBit) != 0) { if (count++ > 0) by.Append(", "); by.Append(cockpit.name); }
                bool text = r.GetComponent<TMP_Text>() != null;
                var mat = r.sharedMaterial;
                details.Append($"; {name} ({(text ? "text mesh" : "quad")}, sorting {r.sortingOrder}, queue {(mat != null ? mat.renderQueue : -1)}, layer {r.gameObject.layer}):" +
                               $" {min.z:0.0}..{max.z:0.0} mm off the card's plane, x {min.x:0}..{max.x:0} y {min.y:0}..{max.y:0} mm on it, drawn by {(count == 0 ? "no camera" : by.ToString())}");
                if (Mathf.Max(Mathf.Abs(min.z), Mathf.Abs(max.z)) > PlaneTolMm) problems.Append($" {name} {Mathf.Max(Mathf.Abs(min.z), Mathf.Abs(max.z)):0.0} mm off the card's plane;");
                if (min.x < -CardW * 500f - EdgeTolMm || max.x > CardW * 500f + EdgeTolMm || min.y < -CardH * 500f - EdgeTolMm || max.y > CardH * 500f + EdgeTolMm)
                    problems.Append($" {name} past the card's edges;");
                if (!byOverlay || count != 1) problems.Append($" {name} not drawn by the overlay camera alone;");
            }
            var canvases = root.GetComponentsInChildren<Canvas>(true);
            for (int i = 0; i < canvases.Length; i++)
                if (canvases[i] != null) problems.Append($" canvas '{canvases[i].name}' under the wrist (drawn a frame behind it);");
            if (Time.frameCount - placedFrame > 1 && shown) problems.Append(" not placed in the render hook this frame;");
            if (on == 0) problems.Append(" nothing drawn;");
            return on;
        }

#if DEV
        /// <summary>Dev `wrist trace [s]`: card movement and parts check every 0.25 s.</summary>
        public static void StartTrace(float seconds)
        {
            traceStart = Time.unscaledTime;
            traceUntil = traceStart + Mathf.Max(0.5f, seconds);
            nextTraceLine = 0f;
            traceLinePeak = traceRunPeak = 0f;
            traceLines = traceFailedLines = 0;
        }
#endif

        private static void TraceTick()
        {
            float now = Time.unscaledTime;
            bool over = now >= traceUntil;
            if (!over && now < nextTraceLine) return;
            nextTraceLine = now + 0.25f;
            var problems = new StringBuilder();
            int on = CheckParts(new StringBuilder(), problems);
            traceLines++;
            if (problems.Length > 0) traceFailedLines++;
            float toEyes = root.activeInHierarchy ? (root.transform.position - CameraDriver.HeadWorldPosition).magnitude : 0f;
            Plugin.L.LogInfo($"wrist trace +{now - traceStart:0.00} s: the card moved up to {traceLinePeak:0} mm in a frame (the view at {CameraDriver.AnchorVelocity.magnitude:0.0} m/s," +
                             $" turning {CameraDriver.AnchorYawRate:0} deg/s, {CameraDriver.Mode}), {toEyes:0.00} m from the eyes; {on} part(s) {(problems.Length == 0 ? "ok" : "FAILED:" + problems)}");
            traceLinePeak = 0f;
            if (!over) return;
            traceUntil = 0f;
            Plugin.L.LogInfo($"wrist trace over after {now - traceStart:0.0} s: the card moved up to {traceRunPeak:0} mm in a frame; " +
                             (traceFailedLines == 0 ? $"every part stayed on the card in all {traceLines} lines" : $"parts FAILED in {traceFailedLines} of {traceLines} lines"));
        }
    }
}
