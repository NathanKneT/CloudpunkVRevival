using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Adds "VREVIVAL" under the main menu's CLOUDPUNK sign, cut from the game's own letter textures and materials
    /// so its lights, fog and bloom match. It lights up letter by letter with the game's spark sound (F11 replays),
    /// and one V flickers like the game's second U. The game's letters are 30-unit quads at z 5.2, sloping down.
    /// </summary>
    internal static class MenuSign
    {
        private const string Word = "VREVIVAL";
        private const int Nod = 5;                 // the flickering V
        private const float LineZ = -8f;           // sign space, clear of the cloud in front of the letters
        private const float Size = 15f;            // the game's letter quads are 30
        private const float StartFromLeft = 2.4f;  // from the game's first letter's centre
        private const float Drop = -21.93f;        // below the line through the game's letter centres
        private const float Gap = 96f;             // texture px between glyphs, as in CLOUDPUNK
        private const float OffLevel = 0.1f;       // unlit brightness
        private const KeyCode ReplayKey = KeyCode.F11; // F5-F8 are the free camera, F10 opens the window menu

        // relight timeline, seconds from the menu coming into view
        private const float FirstAt = 0.8f, Step = 0.28f, CatchDelay = 0.3f;
        private const float Volume = 0.35f, CueSeconds = 0.35f;
        // crackle start times in Sparks_Slow_Audio, one per letter, then the V's catch
        private static readonly float[] CueClip = { 0.43f, 2.06f, 0.89f, 6.18f, 2.92f, 5.59f, 7.06f, 8.65f };
        private const float CatchClip = 1.39f;

        private sealed class Letter
        {
            public int Index;
            public SignLetters.Glyph Glyph;
            public GameObject Go;
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public Material[] Lit, Unlit; // Unlit: the V with U-off, under the Flicker layer
            public Color[] Tints;
            public MeshRenderer Flicker;
            public GameObject Light;
            public float[] KeyTimes, KeyLevels;
            public float Level = -1f;
            public float CatchAt; // flickering V: when its second try starts
            public bool FlickerOn, UnlitOn;
            public float Left, Right, X, Y;
            public string Sources;
        }

        private struct Cue { public float At, Clip, Seconds; public string Label; }

        /// <summary>One relight crackle, for the edit: time from the relight's start, length, letter.</summary>
        public readonly struct CueTime
        {
            public readonly float At, Seconds, Clip;
            public readonly string Label;
            public CueTime(float at, float seconds, float clip, string label) { At = at; Seconds = seconds; Clip = clip; Label = label; }
        }

        private static Transform logo;
        private static GameObject root;
        private static Letter[] letters;
        private static Camera menuCam;
        private static AudioSource audio;
        private static AudioClip sparks;
        private static string soundVia = "none";
        private static readonly List<Cue> cues = new List<Cue>();
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly List<Material> made = new List<Material>();

        private static float nextFind, slope, intercept, end, playStart = -1f, holdAt = float.NaN, lastT, soundStop = -1f, checkUntil = -1f, checkStart;
        private static bool armed, wasActive, nodFlicker = true, autoChecked, failedLogged;
        // a free cam take drives the relight from its own clock (Drive)
        private static bool driven;
        private static float drivenT;
        private static int drivenCues;
        private static int relights, checks, gameLetters, errors;
        private static IntPtr failedFor;
        private static string timelineSignature = "";
        // check counts per eye (left, right, mono)
        private static readonly int[] drawn = new int[3], passes = new int[3];
        private static int[] culledAt;
        private static int culledMask, culledFrames, lastCulledFrame = -1, passErrors;
        private static bool fullCheck;
        private static Camera.CameraCallback eyePass;
        private static string otherCameras = "";

        /// <summary>Each frame, flat and in VR.</summary>
        public static void Update()
        {
            try { Tick(); }
            catch (Exception e) { if (errors++ < 10) Plugin.L.LogError("Menu sign: " + e); }
        }

        private static void Tick()
        {
            float now = Time.unscaledTime;
            bool want = VRConfig.MenuSign.Value;
            if (logo == null)
            {
                if (letters != null) Forget("the menu's scene is gone; the line goes with it (built again when it is back)");
                if (!want || now < nextFind) return;
                nextFind = now + 1f;
                logo = FindLogo();
                if (logo == null) return;
            }
            if (!want)
            {
                if (root != null && root.activeSelf)
                {
                    root.SetActive(false);
                    StopSound();
                    playStart = -1f;
                    Plugin.L.LogInfo("Menu sign: off (MenuSign = false); the game's sign as it is.");
                }
                return;
            }
            if (root == null)
            {
                if (letters != null) Forget("the line was taken down; building it again");
                if (failedFor == logo.Pointer) return;
                bool built;
                try { built = Build(); }
                catch (Exception e) { Plugin.L.LogError("Menu sign: building the line failed: " + e); built = false; }
                if (!built)
                {
                    failedFor = logo.Pointer; // don't retry for this scene
                    if (root != null || letters != null) Forget("what was built of the line is taken down");
                    return;
                }
            }
            if (!root.activeSelf)
            {
                root.SetActive(true);
                armed = true;
                Plugin.L.LogInfo("Menu sign: on again (MenuSign = true).");
            }

            bool active = root.activeInHierarchy;
            if (active != wasActive)
            {
                wasActive = active;
                if (active) { armed = true; holdAt = float.NaN; } // relight once in view
                else { playStart = -1f; StopSound(); Apply(-1f); } // dark for next time
            }
            if (!active) return;

            if (UnityEngine.Input.GetKeyDown(ReplayKey)) Relight("F11");
            UpdateSequence(now);
            UpdateSound(now);
            if (checkUntil > 0f && now >= checkUntil) EndCheck(now);
        }

        // ---------------------------------------------------------------- building

        private static Transform FindLogo()
        {
            var g = Global.Instance;
            var cam = g != null ? g.menuCamera : null;
            var menu = cam != null ? cam.parent : null;
            if (menu == null) return null;
            var t = menu.Find("Objects/Logo v2");
            if (t == null && !failedLogged)
            {
                failedLogged = true;
                Plugin.L.LogWarning("Menu sign: the game's sign (Objects/Logo v2 beside the menu camera) is not there; nothing added.");
            }
            return t;
        }

        private static Material Template(string path, string texture, out MeshRenderer renderer, StringBuilder missing)
        {
            var t = logo.Find(path);
            renderer = t != null ? t.GetComponent<MeshRenderer>() : null;
            var m = renderer != null ? renderer.sharedMaterial : null;
            string name = "-";
            try { var tex = m != null ? m.mainTexture : null; if (tex != null) name = tex.name; } catch (Exception) { }
            if (m == null || name != texture) { missing.Append($" {path} ({texture}, found {name})"); return null; }
            return m;
        }

        private static bool Build()
        {
            var missing = new StringBuilder();
            var tpl = new Dictionary<SignLetters.Source, Material>();
            tpl[SignLetters.Source.L] = Template("L", "L_brighter", out var lRenderer, missing);
            tpl[SignLetters.Source.P] = Template("P", "P_brighter", out _, missing);
            tpl[SignLetters.Source.K] = Template("K", "K_brighter", out _, missing);
            tpl[SignLetters.Source.U] = Template("U", "U_brighter", out _, missing);
            if (missing.Length > 0)
            {
                Plugin.L.LogWarning("Menu sign: the game's letters are not as expected:" + missing + "; nothing added.");
                return false;
            }
            var nodMissing = new StringBuilder();
            var unlitU = Template("U2 with flicker/U2", "U-off", out var unlitRenderer, nodMissing);
            var gameU2 = logo.Find("U2 with flicker");
            var flickerLayer = logo.Find("U2 with flicker/U2-flicker");
            var flickerRenderer = flickerLayer != null ? flickerLayer.GetComponent<MeshRenderer>() : null;
            var flickerMat = flickerRenderer != null ? flickerRenderer.sharedMaterial : null;
            var lightTemplate = flickerLayer != null ? flickerLayer.Find("Point light") : null;
            if (flickerMat == null) nodMissing.Append(" U2 with flicker/U2-flicker");
            if (lightTemplate == null) nodMissing.Append(" its Point light");

            // least-squares slope through the game's letters, leftmost x, and probe anchors
            var anchors = new List<(float X, Transform T)>();
            float sx = 0f, sy = 0f, sxx = 0f, sxy = 0f, leftX = float.MaxValue;
            gameLetters = 0;
            for (int i = 0; i < logo.childCount; i++)
            {
                var c = logo.GetChild(i);
                if (!c.gameObject.activeSelf || c.name.StartsWith("CloudpunkVR")) continue;
                var p = c.localPosition;
                gameLetters++; sx += p.x; sy += p.y; sxx += p.x * p.x; sxy += p.x * p.y;
                leftX = Mathf.Min(leftX, p.x);
                if (c.GetComponent<MeshRenderer>() != null) anchors.Add((p.x, c));
            }
            if (gameLetters < 3) { Plugin.L.LogWarning($"Menu sign: only {gameLetters} letters in the game's sign; nothing added."); return false; }
            slope = (gameLetters * sxy - sx * sy) / (gameLetters * sxx - sx * sx);
            intercept = (sy - slope * sx) / gameLetters;

            root = new GameObject("CloudpunkVR_MenuSign");
            int layer = lRenderer.gameObject.layer; // the sign's layer, lit only by its own light
            root.layer = layer;
            root.transform.SetParent(logo, false);
            root.SetActive(false); // on once complete

            letters = new Letter[Word.Length];
            float left = leftX + StartFromLeft;
            var subs = new List<(SignLetters.Source Source, Color Tint)>();
            int pieces = 0;
            for (int i = 0; i < Word.Length; i++)
            {
                var glyph = SignLetters.For(Word[i]);
                var L = new Letter { Index = i, Glyph = glyph };
                L.X = left - (glyph.Left / 2048f - 0.5f) * Size;
                L.Y = intercept + slope * L.X + Drop;
                L.Left = left;
                L.Right = L.X + (glyph.Right / 2048f - 0.5f) * Size;
                left = L.Right + Gap / 2048f * Size;
                pieces += glyph.Pieces.Length;

                L.Mesh = SignLetters.Build(glyph, Size, subs);
                L.Go = new GameObject($"CloudpunkVR_Sign{i}{glyph.Letter}") { layer = layer };
                L.Go.transform.SetParent(root.transform, false);
                L.Go.transform.localPosition = new Vector3(L.X, L.Y, LineZ);
                L.Go.AddComponent<MeshFilter>().sharedMesh = L.Mesh;
                L.Renderer = L.Go.AddComponent<MeshRenderer>();
                L.Tints = new Color[subs.Count];
                L.Lit = new Material[subs.Count];
                for (int m = 0; m < subs.Count; m++)
                {
                    L.Tints[m] = subs[m].Tint;
                    L.Lit[m] = Instance(tpl[subs[m].Source], $"CloudpunkVR_Sign{i}{glyph.Letter}_{subs[m].Source}");
                }
                L.Renderer.sharedMaterials = L.Lit;
                Transform anchor = null;
                float best = float.MaxValue;
                foreach (var a in anchors) if (Mathf.Abs(a.X - L.X) < best) { best = Mathf.Abs(a.X - L.X); anchor = a.T; }
                Dress(L.Renderer, lRenderer, anchor);

                if (i == Nod && nodMissing.Length == 0)
                {
                    L.Unlit = new Material[subs.Count];
                    for (int m = 0; m < subs.Count; m++)
                        L.Unlit[m] = subs[m].Source == SignLetters.Source.U ? Instance(unlitU, $"CloudpunkVR_Sign{i}{glyph.Letter}_UOff") : L.Lit[m];
                    // map the game's U2 layer offsets onto our letter size
                    float scale = Size / gameU2.lossyScale.y;
                    Vector3 OnOurs(Transform t) => (gameU2.InverseTransformPoint(t.position) - gameU2.InverseTransformPoint(unlitRenderer.transform.position)) * Size;
                    var fl = new GameObject("CloudpunkVR_SignFlicker") { layer = layer };
                    fl.transform.SetParent(L.Go.transform, false);
                    // a hair nearer to avoid z-fighting
                    fl.transform.localPosition = OnOurs(flickerLayer) + new Vector3(0f, 0f, -0.02f);
                    fl.AddComponent<MeshFilter>().sharedMesh = L.Mesh;
                    L.Flicker = fl.AddComponent<MeshRenderer>();
                    L.Flicker.sharedMaterial = flickerMat; // the game's, shared, never modified
                    Dress(L.Flicker, lRenderer, anchor);
                    L.Flicker.enabled = false;
                    L.Light = UnityEngine.Object.Instantiate(lightTemplate.gameObject);
                    L.Light.name = "CloudpunkVR_SignLight";
                    L.Light.SetActive(false);
                    L.Light.transform.SetParent(L.Go.transform, false);
                    L.Light.transform.localPosition = OnOurs(lightTemplate);
                    var light = L.Light.GetComponent<Light>();
                    if (light != null) light.range *= scale;
                }
                L.Sources = Describe(L);
                letters[i] = L;
                Set(L, OffLevel, false);
            }

            FindSound();
            Plan();
            root.SetActive(true);
            wasActive = false;
            armed = true;
            var first = letters[0]; var last = letters[letters.Length - 1];
            Plugin.L.LogInfo($"Menu sign: VREVIVAL built under the game's CLOUDPUNK from its own letter pictures (L_brighter, P_brighter, " +
                             $"K_brighter, U_brighter{(unlitU != null ? ", U-off" : "")}; no image of the mod): {letters.Length} letters, {pieces} pieces, " +
                             $"{Size:0} units tall (the game's 30), at z {LineZ:0.0} in its space ({5.2f - LineZ:0.0} in front of its letters), " +
                             $"slope {slope:0.0000} from its {gameLetters} letters, x {first.Left:0.0} to {last.Right:0.0}, centres y {first.Y:0.0} to {last.Y:0.0}; " +
                             $"layer {LayerMask.LayerToName(layer)} ({layer}); " +
                             (nodMissing.Length == 0 ? $"letter {Nod + 1} ({Word[Nod]}) flickers like the game's second U" : "no flickering letter, missing:" + nodMissing) +
                             $"; sound {soundVia}.");
            for (int i = 0; i < letters.Length; i++) Plugin.L.LogInfo($"Menu sign letter {i + 1} {letters[i].Glyph.Letter}: {letters[i].Sources}");
            return true;
        }

        private static Material Instance(Material template, string name)
        {
            var m = new Material(template) { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            made.Add(m);
            return m;
        }

        /// <summary>Same renderer settings as the game's letters, and immune to baked occlusion.</summary>
        private static void Dress(MeshRenderer r, MeshRenderer like, Transform anchor)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.allowOcclusionWhenDynamic = false;
            r.lightProbeUsage = like.lightProbeUsage;
            r.reflectionProbeUsage = like.reflectionProbeUsage;
            if (anchor != null) r.probeAnchor = anchor;
        }

        /// <summary>For the log.</summary>
        private static string Describe(Letter L)
        {
            var sb = new StringBuilder();
            foreach (var p in L.Glyph.Pieces)
            {
                Material m = null;
                for (int k = 0; k < L.Lit.Length && m == null; k++)
                    if (L.Lit[k].name.EndsWith("_" + p.Source)) m = L.Lit[k];
                string tex = "?";
                try { if (m != null && m.mainTexture != null) tex = m.mainTexture.name; } catch (Exception) { }
                if (sb.Length > 0) sb.Append(" + ");
                sb.Append($"{tex} {SignLetters.Uv(p)}");
                if (p.Note != null) sb.Append($" ({p.Note})");
            }
            sb.Append($"; centre ({L.X:0.0}, {L.Y:0.0}), {L.Mesh.vertexCount} vertices");
            if (L.Unlit != null) sb.Append("; flickers: U-off under the game's Flicker layer, with a copy of its light");
            return sb.ToString();
        }

        private static void FindSound()
        {
            sparks = null;
            UnityEngine.Audio.AudioMixerGroup group = null;
            var sources = Resources.FindObjectsOfTypeAll<AudioSource>();
            for (int i = 0; i < sources.Length && sparks == null; i++)
            {
                var s = sources[i];
                var c = s != null ? s.clip : null;
                if (c != null && c.name == "Sparks_Slow_Audio") { sparks = c; group = s.outputAudioMixerGroup; }
            }
            if (sparks == null)
            {
                soundVia = "none (the game's Sparks_Slow_Audio is not loaded): a silent relight";
                return;
            }
            if (group == null)
            {
                // without the mixer group it would ignore the volume settings
                soundVia = $"none ('{sparks.name}' plays outside the game's mixer here): a silent relight";
                sparks = null;
                return;
            }
            audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = false;
            audio.spatialBlend = 0f;
            audio.priority = 64;
            audio.clip = sparks;
            audio.outputAudioMixerGroup = group; // so the game's volume settings apply
            soundVia = $"'{sparks.name}' ({sparks.length:0.0} s) through the mixer group '{group.name}'";
        }

        /// <summary>Tears down the line with our meshes and materials.</summary>
        private static void Forget(string why)
        {
            if (letters != null)
                foreach (var L in letters)
                    if (L != null && L.Mesh != null) UnityEngine.Object.Destroy(L.Mesh);
            foreach (var m in made) if (m != null) UnityEngine.Object.Destroy(m);
            made.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
            letters = null; root = null; audio = null; menuCam = null;
            playStart = -1f; soundStop = -1f; checkUntil = -1f; wasActive = false; driven = false;
            try { if (eyePass != null && Camera.onPreRender != null) Camera.onPreRender = Camera.onPreRender - eyePass; } catch (Exception) { }
            Plugin.L.LogInfo("Menu sign: " + why + ".");
        }

        // ---------------------------------------------------------------- the relight

        private static float Hash(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(seed * 73856093) ^ (uint)(n * 19349663) ^ 0x9E3779B9u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        /// <summary>Seeded flashes and drops starting at 'at'. Returns the end time.</summary>
        private static float Crackle(List<(float T, float Level)> keys, float at, int flashes, int seed, float strength)
        {
            float t = at;
            for (int k = 0; k < flashes; k++)
            {
                keys.Add((t, strength * (0.75f + 0.5f * Hash(seed, 4 * k))));
                t += 0.03f + 0.05f * Hash(seed, 4 * k + 1);
                keys.Add((t, OffLevel + 0.18f * Hash(seed, 4 * k + 2)));
                t += 0.025f + 0.07f * Hash(seed, 4 * k + 3);
            }
            return t;
        }

        /// <summary>Fixed timeline: left to right, the flickering V fails once and catches last.</summary>
        private static void Plan()
        {
            cues.Clear();
            end = 0f;
            int sig = 17;
            var keys = new List<(float T, float Level)>();
            for (int i = 0; i < letters.Length; i++)
            {
                keys.Clear();
                float at = FirstAt + Step * i;
                string label = $"{Word[i]} {i + 1}";
                cues.Add(new Cue { At = at, Clip = CueClip[i], Seconds = CueSeconds, Label = label + (i == Nod ? " fails" : "") });
                if (i == Nod)
                {
                    float t0 = Crackle(keys, at, 3, 11 * i + 1, 0.6f);
                    keys.Add((t0, OffLevel));
                    at = FirstAt + Step * letters.Length + CatchDelay;
                    letters[i].CatchAt = at;
                    cues.Add(new Cue { At = at, Clip = CatchClip, Seconds = CueSeconds + 0.1f, Label = label + " catches" });
                }
                float t = Crackle(keys, at, 4 + i % 3, 11 * i + 3, 1f);
                keys.Add((t, 1f));
                var L = letters[i];
                L.KeyTimes = new float[keys.Count];
                L.KeyLevels = new float[keys.Count];
                for (int k = 0; k < keys.Count; k++)
                {
                    L.KeyTimes[k] = keys[k].T; L.KeyLevels[k] = keys[k].Level;
                    sig = unchecked(sig * 31 + Mathf.RoundToInt(keys[k].T * 1000f)); sig = unchecked(sig * 31 + Mathf.RoundToInt(keys[k].Level * 1000f));
                }
                end = Mathf.Max(end, t + 0.05f);
            }
            cues.Sort((a, b) => a.At.CompareTo(b.At));
            foreach (var c in cues) { sig = unchecked(sig * 31 + Mathf.RoundToInt(c.At * 1000f)); sig = unchecked(sig * 31 + Mathf.RoundToInt(c.Clip * 1000f)); }
            timelineSignature = $"{sig:X8}";
        }

        private static float LevelAt(Letter L, float t)
        {
            if (t < L.KeyTimes[0]) return OffLevel;
            int k = L.KeyTimes.Length - 1;
            while (k > 0 && L.KeyTimes[k] > t) k--;
            return L.KeyLevels[k];
        }

        private static void Apply(float t)
        {
            if (letters == null) return;
            foreach (var L in letters)
            {
                float level = t < 0f ? OffLevel : LevelAt(L, t);
                // flickering V: lit picture until its catch, then U-off under the Flicker layer like the game's U2
                bool asGameU = L.Flicker != null && nodFlicker && t >= L.CatchAt;
                bool on = asGameU && level >= 0.5f;
                Set(L, on ? 1f : level, on, asGameU);
            }
        }

        private static void Set(Letter L, float level, bool flicker, bool unlitPicture = false)
        {
            bool unlit = L.Unlit != null && unlitPicture;
            if (unlit != L.UnlitOn)
            {
                L.UnlitOn = unlit;
                L.Renderer.sharedMaterials = unlit ? L.Unlit : L.Lit;
                L.Level = -1f;
            }
            if (L.Level != level)
            {
                L.Level = level;
                var mats = unlit ? L.Unlit : L.Lit;
                for (int m = 0; m < mats.Length; m++)
                {
                    var c = L.Tints[m];
                    mats[m].SetColor(ColorId, new Color(c.r * level, c.g * level, c.b * level, 1f));
                }
            }
            if (L.Flicker != null && L.FlickerOn != flicker)
            {
                L.FlickerOn = flicker;
                L.Flicker.enabled = flicker;
                L.Light.SetActive(flicker);
            }
        }

        /// <summary>Menu camera on, and no black screen or VR settle in front of it.</summary>
        private static bool InView()
        {
            var g = Global.Instance;
            if (g == null || g.menuCamera == null) return false;
            if (menuCam == null) menuCam = g.menuCamera.GetComponent<Camera>();
            if (menuCam == null || !menuCam.isActiveAndEnabled) return false;
            if (VR.CameraDriver.MenuSettling) return false;
            var black = g.m_BlackScreenCanvas;
            return black == null || !black.gameObject.activeInHierarchy || black.alpha < 0.05f;
        }

        private static void UpdateSequence(float now)
        {
            if (driven || !float.IsNaN(holdAt)) return;
            if (armed && InView())
            {
                armed = false;
                Start(now, "the menu in view");
            }
            if (playStart < 0f) return;
            float t = now - playStart;
            for (int i = 0; i < cues.Count; i++)
                if (cues[i].At > lastT && cues[i].At <= t) PlayCue(cues[i], now);
            lastT = t;
            Apply(t);
            if (t < end) return;
            playStart = -1f;
            Plugin.L.LogInfo($"Menu sign: relight {relights} done at {now:0.00} s, after {t:0.00} s: all {letters.Length} letters lit" +
                             (letters[Nod].Flicker != null && nodFlicker ? $", letter {Nod + 1} ({Word[Nod]}) flickering like the game's second U." : "."));
            // once: full check in dev builds, else a cheap per-eye count in VR
            if (!autoChecked) { autoChecked = true; if (VRConfig.DevHooks) StartCheck(now, 2f, true); else if (VR.VRSession.Active) StartCheck(now, 2f, false); }
        }

        private static void Start(float now, string why)
        {
            playStart = now;
            lastT = -1f;
            relights++;
            Apply(0f);
            Plugin.L.LogInfo($"Menu sign: relight {relights} started at {now:0.00} s ({why}); timeline {timelineSignature}: {letters.Length} letters from " +
                             $"{FirstAt:0.00} s, one every {Step:0.00} s, letter {Nod + 1} catching last, lit by {end:0.00} s; {cues.Count} crackles of sound.");
        }

        /// <summary>F11 or `sign relight`.</summary>
        private static string Relight(string why)
        {
            if (letters == null || root == null || !root.activeInHierarchy || !InView()) return "the sign is not in view";
            if (driven) return "the free camera's take plays the relight now";
            holdAt = float.NaN;
            StopSound();
            Start(Time.unscaledTime, why);
            return $"relight {relights} from the start ({why})";
        }

        // ---------------------------------------------------------------- relight in a free cam take

        /// <summary>The line is built and visible (menu scene up, MenuSign on).</summary>
        public static bool Showing => letters != null && root != null && root.activeInHierarchy;

        /// <summary>
        /// Relight at <paramref name="t"/> s from its start (negative: dark), set from the take's frame so the letters
        /// land on the same frames every take. Sound only for real-time takes. False when there's no sign.
        /// </summary>
        public static bool Drive(float t, bool sound)
        {
            if (!Showing) return false;
            if (!driven)
            {
                driven = true;
                holdAt = float.NaN;
                playStart = -1f;
                armed = false;
                StopSound();
                drivenT = float.NegativeInfinity;
                drivenCues = 0;
                Plugin.L.LogInfo($"Menu sign: the relight played by a free camera take, frame by frame (timeline {timelineSignature}, lit by {end:0.00} s, " +
                                 $"{cues.Count} crackles{(sound ? "" : ", silent: the frames go to disk slower than the sound would play")}).");
            }
            for (int i = 0; i < cues.Count; i++)
            {
                if (cues[i].At <= drivenT || cues[i].At > t) continue;
                drivenCues++;
                if (sound) PlayCue(cues[i], Time.unscaledTime);
            }
            drivenT = t;
            Apply(t < 0f ? -1f : t);
            return true;
        }

        /// <summary>Take over: leave the line lit.</summary>
        public static void EndDrive(string why)
        {
            if (!driven) return;
            driven = false;
            StopSound();
            if (letters != null) Apply(end + 1f);
            Plugin.L.LogInfo($"Menu sign: the take's relight over ({why}) at {drivenT:0.00} s of it, {drivenCues} of {cues.Count} crackles reached; the line lit.");
        }

        /// <summary>The relight's crackles in order (empty until the line is built).</summary>
        public static List<CueTime> CueTimes()
        {
            var list = new List<CueTime>();
            if (letters == null) return list;
            foreach (var c in cues) list.Add(new CueTime(c.At, c.Seconds, c.Clip, c.Label));
            return list;
        }

        /// <summary>Seconds until every letter is lit.</summary>
        public static float LitBy => end;

        /// <summary>Crackle volume, share of the game's sound (0.1 s fade-out each).</summary>
        public static float CueVolume => Volume;

        /// <summary>World bounds of the whole sign (game letters and our line), to aim a camera at.</summary>
        public static bool SignBounds(out Bounds b)
        {
            b = default;
            if (logo == null) return false;
            bool any = false;
            var renderers = logo.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled || r.name.StartsWith("CloudpunkVR_SignFlicker")) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any;
        }

        private static void PlayCue(Cue c, float now)
        {
            if (audio == null || sparks == null || c.Clip + 0.05f >= sparks.length) return; // clip shorter than expected
            audio.Stop();
            audio.volume = Volume;
            audio.Play();
            audio.time = c.Clip;
            soundStop = now + c.Seconds;
        }

        private static void UpdateSound(float now)
        {
            if (soundStop < 0f || audio == null) return;
            float left = soundStop - now;
            if (left <= 0f) { StopSound(); return; }
            if (left < 0.1f) audio.volume = Volume * left / 0.1f;
        }

        private static void StopSound()
        {
            soundStop = -1f;
            if (audio != null) audio.Stop();
        }

        // ---------------------------------------------------------------- checks

        /// <summary>
        /// Which eyes draw the letters. Stereo culls once a frame, in the left eye's pass, so each eye's onPreRender
        /// checks that frame's culling. full adds probes and an occluder report, else Renderer.isVisible is used.
        /// </summary>
        private static string StartCheck(float now, float seconds, bool full)
        {
            if (letters == null) return "no sign built";
            fullCheck = full;
            Array.Clear(drawn, 0, 3); Array.Clear(passes, 0, 3);
            culledMask = 0; culledFrames = 0; lastCulledFrame = -1; passErrors = 0;
            if (culledAt == null || culledAt.Length != letters.Length) culledAt = new int[letters.Length];
            for (int i = 0; i < culledAt.Length; i++) culledAt[i] = -1;
            otherCameras = "";
            if (full)
            {
                if (!ClassInjector.IsTypeRegisteredInIl2Cpp<MenuSignProbe>()) ClassInjector.RegisterTypeInIl2Cpp<MenuSignProbe>();
                foreach (var L in letters)
                    if (L.Go.GetComponent<MenuSignProbe>() == null) L.Go.AddComponent<MenuSignProbe>();
            }
            try
            {
                if (eyePass == null) eyePass = (Camera.CameraCallback)new Action<Camera>(EyePass);
                var current = Camera.onPreRender;
                Camera.onPreRender = current == null ? eyePass : (current - eyePass) + eyePass;
            }
            catch (Exception e) { Plugin.L.LogWarning("Menu sign: the eye passes cannot be watched: " + e.Message); }
            checkStart = now;
            checkUntil = now + seconds;
            if (!full) return "eye passes for " + seconds.ToString("0.0") + " s";
            checks++;
            Plugin.L.LogInfo($"Menu sign check {checks}: {Placement()}");
            return $"check {checks} for {seconds:0.0} s";
        }

        /// <summary>From MenuSignProbe: the culling kept this letter.</summary>
        public static void Culled(GameObject go)
        {
            if (letters == null || culledAt == null) return;
            var cam = Camera.current;
            if (cam == null) return;
            if (menuCam == null || cam.Pointer != menuCam.Pointer)
            {
                if (!otherCameras.Contains(cam.name)) otherCameras += " '" + cam.name + "'";
                return;
            }
            int f = Time.frameCount;
            if (f != lastCulledFrame) { lastCulledFrame = f; culledFrames++; }
            for (int i = 0; i < letters.Length; i++)
                if (letters[i].Go.Pointer == go.Pointer) { culledAt[i] = f; culledMask |= 1 << i; return; }
        }

        /// <summary>Start of one eye's pass: count culled-in letters inside that eye's view.</summary>
        private static void EyePass(Camera cam)
        {
            try
            {
                if (letters == null || culledAt == null || checkUntil < 0f || menuCam == null || cam == null || cam.Pointer != menuCam.Pointer) return;
                var eye = cam.stereoActiveEye;
                int e = eye == Camera.MonoOrStereoscopicEye.Left ? 0 : eye == Camera.MonoOrStereoscopicEye.Right ? 1 : 2;
                passes[e]++;
                int f = Time.frameCount;
                for (int i = 0; i < letters.Length; i++)
                {
                    var r = letters[i].Renderer;
                    if (fullCheck ? culledAt[i] != f : !r.isVisible) continue;
                    if (!r.enabled) continue;
                    var v = cam.WorldToViewportPoint(r.bounds.center, eye);
                    if (v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f) drawn[e] |= 1 << i;
                }
            }
            catch (Exception ex) { if (passErrors++ < 3) Plugin.L.LogWarning("Menu sign: eye pass: " + ex.Message); }
        }

        private static int Bits(int mask) { int n = 0; for (; mask != 0; mask &= mask - 1) n++; return n; }

        private static void EndCheck(float now)
        {
            checkUntil = -1f;
            try { if (eyePass != null && Camera.onPreRender != null) Camera.onPreRender = Camera.onPreRender - eyePass; } catch (Exception) { }
            if (fullCheck) // probes only exist in a full check
                foreach (var L in letters)
                {
                    var p = L.Go.GetComponent<MenuSignProbe>();
                    if (p != null) UnityEngine.Object.Destroy(p);
                }
            int all = (1 << letters.Length) - 1, n = letters.Length;
            bool vr = VR.VRSession.Active;
            bool ok = vr ? drawn[0] == all && drawn[1] == all : drawn[2] == all;
            string cam = menuCam != null ? menuCam.name : "the menu camera";
            if (!fullCheck)
            {
                string line = $"Menu sign: in the headset, drawn in the left eye {Bits(drawn[0])}/{n} letters ({passes[0]} passes), the right eye {Bits(drawn[1])}/{n} ({passes[1]} passes) over {now - checkStart:0.0} s";
                if (ok) Plugin.L.LogInfo(line + ".");
                else Plugin.L.LogWarning(line + ": not every letter in both eyes.");
                return;
            }
            string eyes = vr
                ? $"left eye {Bits(drawn[0])}/{n} letters ({passes[0]} passes), right eye {Bits(drawn[1])}/{n} ({passes[1]} passes)" + (passes[2] > 0 ? $", mono {Bits(drawn[2])}/{n} ({passes[2]} passes)" : "")
                : $"its single view {Bits(drawn[2])}/{n} letters ({passes[2]} passes)";
            Plugin.L.LogInfo($"Menu sign check {checks}: drawn by '{cam}' in {(ok && vr ? "both eyes: " : "")}{eyes} over {now - checkStart:0.0} s; " +
                             $"kept by its culling (once a frame for both eyes) {Bits(culledMask)}/{n} letters ({culledFrames} frames)" +
                             (otherCameras.Length > 0 ? $"; also culled in by{otherCameras}" : "") +
                             (ok ? "." : $"; FAILED: not every letter drawn in {(vr ? "both eyes" : "the view")}."));
        }

        /// <summary>Viewport rect per eye, and any cloud or cab geometry in front.</summary>
        private static string Placement()
        {
            var sb = new StringBuilder();
            var b = LineBounds();
            if (menuCam != null)
            {
                float d = Vector3.Distance(menuCam.transform.position, b.center);
                sb.Append($"'{menuCam.name}' {d:0} units from the line; ");
                if (VR.VRSession.Active)
                    sb.Append($"left eye {ViewRect(b, Camera.MonoOrStereoscopicEye.Left)}, right eye {ViewRect(b, Camera.MonoOrStereoscopicEye.Right)}");
                else sb.Append($"view {ViewRect(b, Camera.MonoOrStereoscopicEye.Mono)}");
            }
            sb.Append("; ").Append(Occluders());
            return sb.ToString();
        }

        private static Bounds LineBounds()
        {
            var b = letters[0].Renderer.bounds;
            for (int i = 1; i < letters.Length; i++) b.Encapsulate(letters[i].Renderer.bounds);
            return b;
        }

        private static string ViewRect(Bounds b, Camera.MonoOrStereoscopicEye eye)
        {
            float x0 = 9f, y0 = 9f, x1 = -9f, y1 = -9f;
            bool behind = false;
            for (int k = 0; k < 8; k++)
            {
                var c = new Vector3((k & 1) == 0 ? b.min.x : b.max.x, (k & 2) == 0 ? b.min.y : b.max.y, (k & 4) == 0 ? b.min.z : b.max.z);
                var v = menuCam.WorldToViewportPoint(c, eye);
                if (v.z <= 0f) behind = true;
                x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x); y0 = Mathf.Min(y0, v.y); y1 = Mathf.Max(y1, v.y);
            }
            bool inside = !behind && x0 >= 0f && x1 <= 1f && y0 >= 0f && y1 <= 1f;
            return $"x {x0:0.00}-{x1:0.00} y {y0:0.00}-{y1:0.00} ({(inside ? "whole in view" : "NOT whole in view")})";
        }

        /// <summary>Cloud and cab vertices in sign space that sit in front of a letter's rect.</summary>
        private static string Occluders()
        {
            var objects = logo.parent;
            if (objects == null) return "no Objects";
            int inFront = 0, unreadable = 0, total = 0;
            float nearest = float.MaxValue, deepest = 0f;
            string nearestName = "-", frontNames = "";
            int frontLetters = 0;
            var toSign = logo.worldToLocalMatrix;
            string notFound = "";
            foreach (var part in new[] { "Clouds", "Cab" })
            {
                var t = objects.Find(part);
                if (t == null) { notFound += " " + part; continue; }
                var filters = t.GetComponentsInChildren<MeshFilter>(true);
                for (int f = 0; f < filters.Length; f++)
                {
                    var mf = filters[f];
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    if (!mesh.isReadable) { unreadable++; continue; }
                    var m = toSign * mf.transform.localToWorldMatrix;
                    var v = mesh.vertices;
                    total += v.Length;
                    for (int i = 0; i < v.Length; i++)
                    {
                        var p = v[i];
                        float x = m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03;
                        float y = m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13;
                        float z = m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23;
                        for (int k = 0; k < letters.Length; k++)
                        {
                            var L = letters[k];
                            if (x < L.Left || x > L.Right || y < L.Y - 0.45f * Size || y > L.Y + 0.45f * Size) continue;
                            if (z < LineZ)
                            {
                                inFront++;
                                frontLetters |= 1 << k;
                                deepest = Mathf.Max(deepest, LineZ - z);
                                if (!frontNames.Contains(mf.name)) frontNames += " " + mf.name;
                            }
                            else if (z - LineZ < nearest) { nearest = z - LineZ; nearestName = mf.name; }
                        }
                    }
                }
            }
            string letterList = "";
            for (int k = 0; k < letters.Length; k++) if ((frontLetters & (1 << k)) != 0) letterList += Word[k];
            if (notFound.Length > 0 || total == 0) // nothing read proves nothing
                return $"clouds and cab NOT checked: {(notFound.Length > 0 ? "not found:" + notFound : "no readable mesh")}; {total} vertices read";
            return (inFront == 0
                       ? $"clouds and cab in front of the letters: none (nearest surface behind them {nearest:0.0} units back, {nearestName})"
                       : $"clouds and cab in front of the letters: {inFront} points IN FRONT of {letterList}, up to {deepest:0.0} units ({frontNames.Trim()})") +
                   $"; {total} vertices read" + (unreadable > 0 ? $", {unreadable} meshes unreadable" : "");
        }

        // ---------------------------------------------------------------- close-ups

        /// <summary>BepInEx\sign-name-line.png and -sign.png from the menu camera's eye, no image effects.</summary>
        private static string Snap(string name)
        {
            if (letters == null || menuCam == null || !root.activeInHierarchy) return "snap: the sign is not in view";
            var line = LineBounds();
            var sign = line;
            for (int i = 0; i < logo.childCount; i++)
            {
                var r = logo.GetChild(i).GetComponentInChildren<MeshRenderer>();
                if (r != null && r.gameObject.activeInHierarchy) sign.Encapsulate(r.bounds);
            }
            return "snap: " + Shoot(line, $"sign-{name}-line.png", 2400, 640) + ", " + Shoot(sign, $"sign-{name}-sign.png", 2400, 1200);
        }

        private static string Shoot(Bounds b, string file, int w, int h)
        {
            var go = new GameObject("CloudpunkVR_SignSnap");
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "CloudpunkVR_SignSnap" };
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.CopyFrom(menuCam);
                cam.enabled = false;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.targetTexture = rt;
                Vector3 eye = menuCam.transform.position;
                float d = Vector3.Distance(eye, b.center);
                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation(b.center - eye, Vector3.up);
                cam.aspect = (float)w / h;
                float half = Mathf.Max(b.extents.y * 1.12f, b.extents.x * 1.06f / cam.aspect);
                cam.fieldOfView = 2f * Mathf.Atan(half / d) * Mathf.Rad2Deg;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var bytes = ImageConversion.EncodeToPNG(tex);
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, file);
                System.IO.File.WriteAllBytes(path, bytes);
                return path;
            }
            finally
            {
                RenderTexture.active = prev;
                if (tex != null) UnityEngine.Object.Destroy(tex);
                UnityEngine.Object.Destroy(go);
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        // ---------------------------------------------------------------- developer command

#if DEV
        /// <summary>
        /// `sign`, `sign relight`, `sign at s` (hold), `sign lit`, `sign check [s]`, `sign snap name`,
        /// `sign on|off`, `sign flicker on|off`.
        /// </summary>
        public static string Dev(string[] a)
        {
            string sub = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            float now = Time.unscaledTime;
            switch (sub)
            {
                case "on":
                case "off":
                    VRConfig.MenuSign.Value = sub == "on";
                    return $"MenuSign = {VRConfig.MenuSign.Value}";
                case "relight":
                    return Relight("sign relight");
                case "at":
                    if (letters == null || a.Length < 3) return "sign at <seconds>: no sign built, or no time given";
                    holdAt = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    playStart = -1f;
                    armed = false;
                    StopSound();
                    Apply(holdAt);
                    return $"held at {holdAt:0.00} s of the relight: {Levels()}";
                case "lit":
                    holdAt = float.NaN;
                    playStart = -1f;
                    armed = false;
                    StopSound();
                    Apply(end + 1f);
                    return "all lit: " + Levels();
                case "check":
                    return StartCheck(now, a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 2f, true);
                case "snap":
                    return Snap(a.Length > 2 ? a[2] : "close");
                case "flicker":
                    nodFlicker = a.Length < 3 || a[2] != "off";
                    if (letters != null) Apply(float.IsNaN(holdAt) ? (playStart >= 0f ? now - playStart : end + 1f) : holdAt);
                    return $"letter {Nod + 1} ({Word[Nod]}) flickering like the game's second U: {(nodFlicker ? "on" : "off (lit like the others)")}";
                default:
                    return Describe();
            }
        }
#endif

        private static string Levels()
        {
            var sb = new StringBuilder();
            foreach (var L in letters)
                sb.Append($"{L.Glyph.Letter} {L.Level:0.00}{(L.FlickerOn ? "+flicker" : "")} ");
            return sb.ToString().TrimEnd();
        }

        private static string Describe()
        {
            if (!VRConfig.MenuSign.Value) return "off (MenuSign = false)";
            if (letters == null) return logo == null ? "no menu sign found yet" : "not built (see the log)";
            string state = driven ? $"played by a free camera take, at {drivenT:0.00} s of the relight" : !float.IsNaN(holdAt) ? $"held at {holdAt:0.00} s" : playStart >= 0f ? $"relight {relights} playing, at {Time.unscaledTime - playStart:0.00} s" : armed ? "dark, waiting for the menu to be in view" : $"lit after relight {relights}";
            return $"on, {letters.Length} letters '{Word}' under '{logo.name}', in the menu's view: {(root.activeInHierarchy && InView() ? "yes" : "no")}; {state} " +
                   $"({Levels()}); timeline {timelineSignature}, lit by {end:0.00} s; letter {Nod + 1} flickering: {(letters[Nod].Flicker != null ? (nodFlicker ? "on" : "off") : "unavailable")}; " +
                   $"sound {soundVia}; {ReplayKey} or `sign relight` replays it; {relights} relight(s), {checks} check(s).";
        }
    }

    /// <summary>Reports culling to MenuSign during a check.</summary>
    public class MenuSignProbe : MonoBehaviour
    {
        public MenuSignProbe(IntPtr ptr) : base(ptr) { }

        private void OnWillRenderObject() => MenuSign.Culled(gameObject);
    }
}
