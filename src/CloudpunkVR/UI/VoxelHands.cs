using System;
using System.Collections.Generic;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Rania's voxel hands, placed by SteamVR's hand skeleton. On PS VR2 Sense the fingers follow the touch sensors
    /// (ring and pinky follow the middle finger), other controllers use the full finger skeleton.
    /// Colors and material come from her own model. Hands close on the wheel, the lever and the coffee cup.
    /// </summary>
    internal static class VoxelHands
    {
        // palette entries
        private const int Skin = 0, Glove = 1, GlovePalm = 2, Strap = 3, Nail = 4, Forearm = 5, ForearmEdge = 6,
                          Metal = 7, Light = 8, Lining = 9;
        private const int PaletteSize = 16, Variants = 4;
        private const float GlowLevel = 0.22f; // faint self-glow so hands read in dark streets

        private static readonly Color DefaultSkin = new Color32(159, 112, 73, 255);
        private static readonly Color DefaultGlove = new Color32(28, 40, 41, 255);
        private static readonly Color DefaultTech = new Color32(81, 168, 221, 255);
        private static readonly Color DefaultMetal = new Color32(170, 170, 170, 255);

        // 0 thumb .. 4 pinky. Meters, for an 8.7 cm palm.
        private static readonly float[] Widths = { 0.0175f, 0.0165f, 0.017f, 0.016f, 0.014f };
        private static readonly float[] Taper = { 1f, 0.93f, 0.86f };
        private static readonly float[][] Lengths =
        {
            new[] { 0.032f, 0.025f, 0.020f },
            new[] { 0.044f, 0.026f, 0.021f },
            new[] { 0.048f, 0.029f, 0.022f },
            new[] { 0.045f, 0.027f, 0.021f },
            new[] { 0.036f, 0.021f, 0.019f },
        };
        private static readonly float[] BendMax = { 72f, 100f, 70f }; // degrees per joint, fully closed

        // Sense curls (0 straight, 1 closed): no touch, touching, fully pressed
        private static readonly float[] OpenCurl = { 0f, 0.07f, 0.10f, 0.13f, 0.16f };
        private static readonly float[] HoldCurl = { 0f, 0.35f, 0.72f, 0.78f, 0.82f };
        private static readonly float[] FullCurl = { 0f, 0.95f, 0.90f, 0.92f, 0.95f };
        private static readonly float[] Spread = { 0f, 2f, 0f, -2.5f, -6f }; // degrees toward the thumb, open hand
        // smoothing time (s), ring and pinky lag a little behind the middle
        private static readonly float[] Settle = { 0.028f, 0.028f, 0.028f, 0.032f, 0.036f };
        private const float NearShare = 0.5f; // how far a finger closes from proximity alone
        // SteamVR skeleton joint chains, base to tip
        private static readonly int[][] Chains =
        {
            new[] { 2, 3, 4, 5 },
            new[] { 7, 8, 9, 10 },
            new[] { 12, 13, 14, 15 },
            new[] { 17, 18, 19, 20 },
            new[] { 22, 23, 24, 25 },
        };

        private sealed class Part
        {
            public GameObject Go;
            public Transform T;
            public MeshRenderer Renderer;
        }

        private sealed class Rig
        {
            public bool Left;
            public GameObject Root;
            public readonly List<Part> All = new List<Part>();
            public Part Palm, Forearm;
            public readonly Part[][] Fingers = new Part[5][];
            public readonly float[] Curl = { 1f, 0.07f, 0.10f, 0.13f, 0.16f }; // thumb: 0 on a button, 1 lifted
            public readonly float[] CurlSpeed = new float[5], Target = new float[5];
            public float SettleStart = -1f; // readout only
            public float Scale = 1f;

            // for the readout
            public SteamVRInput.FingerTouch Touch;
            public float Trigger, Held;
            public string Source = "hidden";
            public readonly NearCheck IndexNear = new NearCheck(), MiddleNear = new NearCheck();
            public bool FlipLogged;

            // palm space, kept so a hand on the wheel still draws when tracking drops
            public bool HasShape;
            public float PalmLength, PalmWidth, PalmThick, ToIndexSign = 1f;
            public readonly Vector3[] Base = new Vector3[5]; // each finger's first joint
            public readonly Vector3[] Dir = new Vector3[15], Up = new Vector3[15]; // finger * 3 + bone
            public readonly float[] Len = new float[15];
            public readonly Vector3[] WrapDir = new Vector3[15], WrapUp = new Vector3[15]; // same, closed on a grip
            public readonly float[] WrapLen = new float[15];
        }

        private static Rig left, right;

        /// <summary>A proximity sensor is trusted once seen low with the finger off and high with it on.</summary>
        private sealed class NearCheck
        {
            public bool SeenOff, SeenOn, Logged;
            public bool Trusted => SeenOff && SeenOn;
        }

        /// <summary>Left wrist in world space, for the wrist display.</summary>
        public struct WristPose
        {
            public bool Valid;
            public Vector3 Position, Forward, Back;
            public float Scale;
        }
        public static WristPose LeftWrist;

        /// <summary>The hand's root while shown, null otherwise.</summary>
        public static Transform Shown(bool isLeft)
        {
            var rig = isLeft ? left : right;
            return rig != null && rig.Root.activeSelf ? rig.Root.transform : null;
        }

        private static Material material;
        private static Texture2D palette, glow;
        private static int overlayLayer, currentLayer = -1;
        private static string paletteSource = "", glowProps = "";
        private static float nextPaletteCheck;
        private static bool loggedNoSkeleton, loggedTouch, loggedMiddle, loggedNoMiddle;
        private static int warnings;

        // dev "hands demo [curl|fingers]": both hands in front of the head
        public static bool Demo;
        public static float DemoCurl = -1f;
        public static string DemoFingers; // "open" or t/i/m touching (null: DemoCurl)
        public static float DemoTrigger, DemoSqueeze;
        public static float ReadoutUntil = -1f; // dev "hands readout [s]"
        private static float nextReadout;
        private static string touchSource = "sensors"; // sensors, demo or simulated
        public static int DemoView; // 0 backs, 1 palms, 2 side, 3 holding a controller
        private static readonly SteamVRInput.HandSkeleton demoLeft = new SteamVRInput.HandSkeleton(), demoRight = new SteamVRInput.HandSkeleton();

        public static void Create(Transform parent, int layer)
        {
            overlayLayer = layer;
            palette = NewPaletteTexture("CloudpunkVR_HandPalette");
            glow = NewPaletteTexture("CloudpunkVR_HandGlow");
            // her default look until her model is found
            SetPalette(DefaultSkin, DefaultGlove, DefaultSkin, DefaultSkin, DefaultMetal, DefaultTech, true);
            material = new Material(Shader.Find("Standard")) { name = "CloudpunkVR_Hands", mainTexture = palette, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.1f);
            SetGlow(material);
            left = Build(parent, true);
            right = Build(parent, false);
        }

        // ---------------------------------------------------------------- model

        private static Rig Build(Transform parent, bool isLeft)
        {
            var rig = new Rig { Left = isLeft, Root = new GameObject(isLeft ? "CloudpunkVR_LeftHand" : "CloudpunkVR_RightHand") };
            rig.Root.transform.SetParent(parent, false);
            int seed = isLeft ? 101 : 7;

            // palm 6x3x6: glove, darker palm, wrist strap
            rig.Palm = NewPart(rig, "Palm", 6, 3, 6, true, (x, y, z, f) => z == 0 ? Strap : y == 0 ? GlovePalm : Glove, ref seed);

            // forearm 5x4x6: skin with the implant plate and light on top, or a sleeve
            rig.Forearm = NewPart(rig, "Forearm", 5, 4, 6, true, (x, y, z, f) =>
            {
                if (f == 4) return Lining;
                if (z == 5) return ForearmEdge;
                if (y == 3 && x >= 1 && x <= 3 && z >= 1 && z <= 3) return x == 2 && f == 2 ? Light : Metal;
                return Forearm;
            }, ref seed);

            for (int fi = 0; fi < 5; fi++)
            {
                rig.Fingers[fi] = new Part[3];
                for (int s = 0; s < 3; s++)
                {
                    int n = s == 0 ? 3 : 2;
                    int seg = s, finger = fi;
                    rig.Fingers[fi][s] = NewPart(rig, $"Finger{fi}_{s}", 2, 2, n, false, (x, y, z, f) =>
                    {
                        if (seg == 0) return finger == 0 || z < 2 ? Glove : Skin; // fingerless gloves end on the first bone
                        if (seg == 2 && z == n - 1 && y == 1 && (f == 2 || f == 4)) return Nail;
                        return Skin;
                    }, ref seed);
                }
            }
            rig.Root.SetActive(false);
            return rig;
        }

        private static Part NewPart(Rig rig, string name, int nx, int ny, int nz, bool centered, Func<int, int, int, int, int> colorAt, ref int seed)
        {
            var go = new GameObject(name);
            go.transform.SetParent(rig.Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = BuildVoxelMesh(nx, ny, nz, centered, colorAt, ref seed);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            var part = new Part { Go = go, T = go.transform, Renderer = mr };
            rig.All.Add(part);
            return part;
        }

        /// <summary>
        /// nx*ny*nz voxels in a unit cube, outer faces only, each voxel a random shade so the grid shows.
        /// colorAt(x, y, z, face) gives the palette entry. Faces: 0 +x, 1 -x, 2 +y, 3 -y, 4 +z, 5 -z.
        /// </summary>
        private static Mesh BuildVoxelMesh(int nx, int ny, int nz, bool centered, Func<int, int, int, int, int> colorAt, ref int seed)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float z0 = centered ? -0.5f : 0f;
            Vector3 size = new Vector3(1f / nx, 1f / ny, 1f / nz);
            for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++)
            {
                seed = seed * 1103515245 + 12345;
                int variant = (seed >> 16) & (Variants - 1);
                Vector3 min = new Vector3(-0.5f + x * size.x, -0.5f + y * size.y, z0 + z * size.z);
                for (int f = 0; f < 6; f++)
                {
                    bool outer = f switch { 0 => x == nx - 1, 1 => x == 0, 2 => y == ny - 1, 3 => y == 0, 4 => z == nz - 1, _ => z == 0 };
                    if (!outer) continue;
                    var uv = new Vector2((colorAt(x, y, z, f) + 0.5f) / PaletteSize, (variant + 0.5f) / Variants);
                    AddFace(verts, normals, uvs, tris, min, size, f, uv);
                }
            }
            var mesh = new Mesh { name = "CloudpunkVR_Voxels" };
            mesh.vertices = verts.ToArray();
            mesh.normals = normals.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static readonly Vector3[] FaceNormals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        private static void AddFace(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, Vector3 min, Vector3 s, int f, Vector2 texel)
        {
            Vector3 max = min + s;
            Vector3 a, b, c, d;
            switch (f)
            {
                case 0: a = new Vector3(max.x, min.y, min.z); b = new Vector3(max.x, max.y, min.z); c = new Vector3(max.x, max.y, max.z); d = new Vector3(max.x, min.y, max.z); break;
                case 1: a = new Vector3(min.x, min.y, max.z); b = new Vector3(min.x, max.y, max.z); c = new Vector3(min.x, max.y, min.z); d = new Vector3(min.x, min.y, min.z); break;
                case 2: a = new Vector3(min.x, max.y, min.z); b = new Vector3(min.x, max.y, max.z); c = new Vector3(max.x, max.y, max.z); d = new Vector3(max.x, max.y, min.z); break;
                case 3: a = new Vector3(min.x, min.y, max.z); b = new Vector3(min.x, min.y, min.z); c = new Vector3(max.x, min.y, min.z); d = new Vector3(max.x, min.y, max.z); break;
                case 4: a = new Vector3(max.x, min.y, max.z); b = new Vector3(max.x, max.y, max.z); c = new Vector3(min.x, max.y, max.z); d = new Vector3(min.x, min.y, max.z); break;
                default: a = new Vector3(min.x, min.y, min.z); b = new Vector3(min.x, max.y, min.z); c = new Vector3(max.x, max.y, min.z); d = new Vector3(max.x, min.y, min.z); break;
            }
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            for (int k = 0; k < 4; k++) { n.Add(FaceNormals[f]); uv.Add(texel); }
            t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        // ---------------------------------------------------------------- colors and material

        private static Texture2D NewPaletteTexture(string name) => new Texture2D(PaletteSize, Variants, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };

        private static void SetPalette(Color skin, Color glove, Color forearm, Color edge, Color metal, Color light, bool lightGlows)
        {
            var colors = new Color[PaletteSize];
            var glowAt = new float[PaletteSize];
            for (int i = 0; i < PaletteSize; i++) { colors[i] = skin; glowAt[i] = GlowLevel; }
            colors[Glove] = glove;
            colors[GlovePalm] = Color.Lerp(glove, Color.black, 0.25f);
            colors[Strap] = Color.Lerp(glove, metal, 0.3f);
            colors[Nail] = Color.Lerp(skin, new Color(1f, 0.86f, 0.84f), 0.4f);
            colors[Forearm] = forearm;
            colors[ForearmEdge] = edge;
            colors[Metal] = metal;
            colors[Light] = light;
            colors[Lining] = forearm * 0.5f;
            if (lightGlows) glowAt[Light] = 0.9f; // implant light
            float[] shades = { 0.93f, 0.98f, 1.02f, 1.07f };
            for (int v = 0; v < Variants; v++)
                for (int i = 0; i < PaletteSize; i++)
                {
                    var c = colors[i] * shades[v];
                    palette.SetPixel(i, v, new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f));
                    float g = glowAt[i];
                    glow.SetPixel(i, v, new Color(Mathf.Clamp01(c.r * g), Mathf.Clamp01(c.g * g), Mathf.Clamp01(c.b * g), 1f));
                }
            palette.Apply(false);
            glow.Apply(false);
        }

        /// <summary>Emission from the dimmed palette. Returns the properties found, for the log.</summary>
        private static string SetGlow(Material m)
        {
            var found = new List<string>();
            bool texture = false;
            foreach (var prop in new[] { "_EmissionMap", "_EmissionTex", "_EmissiveMap", "_EmissiveTex" })
                if (m.HasProperty(prop)) { m.SetTexture(prop, glow); texture = true; found.Add(prop); }
            foreach (var prop in new[] { "_EmissionColor", "_EmissiveColor" })
                if (m.HasProperty(prop)) { m.SetColor(prop, texture ? Color.white : new Color(GlowLevel, GlowLevel, GlowLevel, 1f)); found.Add(prop); }
            foreach (var prop in new[] { "_MetallicGlossMap", "_MetallicMap", "_MetallicTex", "_SpecGlossMap" })
                if (m.HasProperty(prop)) { m.SetTexture(prop, Texture2D.blackTexture); found.Add(prop); }
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return found.Count > 0 ? string.Join(", ", found) : "no emission or metallic maps";
        }

        private static string Hex(Color c) => ((int)(Mathf.Clamp01(c.r) * 255f)).ToString("X2") + ((int)(Mathf.Clamp01(c.g) * 255f)).ToString("X2") + ((int)(Mathf.Clamp01(c.b) * 255f)).ToString("X2");

        /// <summary>Colors and material from Rania's model, rechecked every 3 s for outfit changes.</summary>
        private static void FollowRania()
        {
            if (Time.unscaledTime < nextPaletteCheck) return;
            nextPaletteCheck = Time.unscaledTime + 3f;
            try
            {
                var player = Global.Instance != null ? Global.Instance.player : null;
                if (player == null) return;
                MeshRenderer hand = null, forearm = null, outfit = null;
                foreach (var r in player.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null) continue;
                    string n = r.name;
                    if (hand == null && n == "rania_hand") hand = r;
                    else if (forearm == null && n == "rania_lowerarm_left") forearm = r;
                    else if (outfit == null && !n.StartsWith("rania_") && n.EndsWith("_leftarm", StringComparison.OrdinalIgnoreCase) && r.gameObject.activeSelf) outfit = r;
                }
                if (hand == null) return;
                string source = hand.name + "+" + (outfit != null ? outfit.name : "bare");
                if (source == paletteSource) return;
                paletteSource = source;

                var armColors = Colors(forearm != null ? forearm.sharedMaterial?.mainTexture : null);
                var handColors = Colors(hand.sharedMaterial?.mainTexture);
                Color skin = armColors.Count > 0 ? armColors[0] : DefaultSkin;
                Color glove = DefaultGlove, tech = DefaultTech, metal = DefaultMetal;
                foreach (var c in handColors) if (Distance(c, skin) > 0.3f) { glove = c; break; }
                foreach (var c in armColors) if (c.b - c.r > 0.25f) { tech = c; break; }
                foreach (var c in armColors)
                    if (Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b) < 0.08f && c.grayscale > 0.4f) { metal = c; break; }
                if (outfit != null)
                {
                    var sleeve = Colors(outfit.sharedMaterial?.mainTexture);
                    Color main = sleeve.Count > 0 ? sleeve[0] : DefaultSkin;
                    Color edge = sleeve.Count > 1 ? sleeve[1] : main * 0.75f;
                    SetPalette(skin, glove, main, edge, main, main, false);
                }
                else SetPalette(skin, glove, skin, skin, metal, tech, true);

                // her material (game's character shader) with our palette as the texture
                var src = hand.sharedMaterial;
                if (src != null && src.shader != null && src.shader.name != material.shader.name)
                {
                    var m = new Material(src)
                    {
                        name = "CloudpunkVR_Hands",
                        mainTexture = palette,
                        mainTextureScale = Vector2.one,
                        mainTextureOffset = Vector2.zero,
                        hideFlags = HideFlags.DontUnloadUnusedAsset,
                    };
                    glowProps = SetGlow(m);
                    material = m;
                    foreach (var rig in new[] { left, right })
                        foreach (var p in rig.All) p.Renderer.sharedMaterial = m;
                }
                Plugin.L.LogInfo($"Hands: skin #{Hex(skin)}, gloves #{Hex(glove)}, forearm {(outfit != null ? "sleeve of " + outfit.name : "bare, implant #" + Hex(metal) + " with light #" + Hex(tech))}; shader {material.shader.name} ({glowProps}).");
            }
            catch (Exception e)
            {
                paletteSource = ""; // retry next check
                if (warnings++ < 3) Plugin.L.LogWarning("Hands: Rania's colors not read: " + e);
            }
        }

        private static float Distance(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        /// <summary>Opaque colors, most common first, close shades merged.</summary>
        private static List<Color> Colors(Texture tex)
        {
            var result = new List<Color>();
            if (tex == null) return result;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            read.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var counts = new Dictionary<int, (int n, Color sum)>();
            for (int y = 0; y < tex.height; y++)
                for (int x = 0; x < tex.width; x++)
                {
                    Color c = read.GetPixel(x, y);
                    if (c.a < 0.95f) continue; // atlas filler is semi-transparent gray
                    int key = ((int)(c.r * 15f) << 8) | ((int)(c.g * 15f) << 4) | (int)(c.b * 15f);
                    counts[key] = counts.TryGetValue(key, out var e) ? (e.n + 1, e.sum + c) : (1, c);
                }
            UnityEngine.Object.Destroy(read);
            var sorted = new List<(int n, Color sum)>(counts.Values);
            sorted.Sort((a, b) => b.n.CompareTo(a.n));
            foreach (var e in sorted)
            {
                var c = e.sum / e.n;
                c.a = 1f;
                result.Add(c);
            }
            return result;
        }

        // ---------------------------------------------------------------- every frame

        /// <summary>Right before rendering, after head and anchor are known.</summary>
        public static void Update()
        {
            if (left == null) return;
            bool on = VRConfig.Hands.Value && CameraDriver.Mode != ViewMode.Chase && CameraDriver.Mode != ViewMode.Startup;
            if (on) FollowRania();

            // overlay with a menu open, cockpit set in the HOVA, else the world
            bool cockpit = !VRUI.Interactive && CameraDriver.Mode == ViewMode.Cockpit && CameraDriver.CockpitCamera != null;
            int layer = VRUI.Interactive ? overlayLayer : cockpit ? FirstLayer(CameraDriver.CockpitCamera, 28) : FirstLayer(CameraDriver.ViewCamera, 17);
            if (layer != currentLayer)
            {
                currentLayer = layer;
                foreach (var rig in new[] { left, right })
                    foreach (var p in rig.All) p.Go.layer = layer;
            }

            if (!on) { Hide(left); Hide(right); LeftWrist.Valid = false; return; }
            if (WheelGrab.Simulated || LeverGrab.Simulated || CockpitCoffee.Simulated)
            {
                var closed = new SteamVRInput.FingerTouch { Active = true, Thumb = true, Index = true, Middle = true, MiddleBound = true, IndexNear = -1f, MiddleNear = -1f };
                touchSource = "simulated";
                DriveSimulated(left, demoLeft, closed, cockpit);
                DriveSimulated(right, demoRight, closed, cockpit);
                Readout();
                return;
            }
            if (Demo)
            {
                FillDemo(demoLeft, true);
                FillDemo(demoRight, false);
                var touch = new SteamVRInput.FingerTouch { Active = true, MiddleBound = true, IndexNear = -1f, MiddleNear = -1f };
                float trigger;
                if (DemoFingers != null)
                {
                    touch.Thumb = DemoFingers.IndexOf('t') >= 0;
                    touch.Index = DemoFingers.IndexOf('i') >= 0;
                    touch.Middle = DemoFingers.IndexOf('m') >= 0;
                    touch.Squeeze = DemoSqueeze;
                    trigger = DemoTrigger;
                }
                else
                {
                    // one value open to closed: index, then middle, then thumb
                    trigger = DemoCurl >= 0f ? DemoCurl : 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 1.5f);
                    touch.Thumb = trigger > 0.5f;
                    touch.Index = trigger > 0.2f;
                    touch.Middle = trigger > 0.4f;
                    touch.Squeeze = Mathf.Clamp01((trigger - 0.6f) / 0.4f);
                }
                touchSource = "demo";
                Drive(left, demoLeft, touch, trigger, cockpit);
                Drive(right, demoRight, touch, trigger, cockpit);
                Readout();
                return;
            }

            bool sense = SteamVRInput.ControllerType == "playstation_vr2_sense";
            var lt = SteamVRInput.LeftTouch;
            var rt = SteamVRInput.RightTouch;
            if (!sense) { lt.Active = false; rt.Active = false; }
            touchSource = "sensors";
            Drive(left, SteamVRInput.LeftSkeleton, lt, SteamVRInput.Brake, cockpit);
            Drive(right, SteamVRInput.RightSkeleton, rt, SteamVRInput.Accelerate, cockpit);
            Readout();

            if (!loggedTouch && (lt.Active || rt.Active))
            {
                loggedTouch = true;
                Plugin.L.LogInfo("Hands: fingers follow the Sense controllers' touch sensors (thumb on the buttons, index on the triggers, middle finger on the grips; ring and little fingers go with the middle one); open hand when nothing is touched.");
            }
            if (!loggedMiddle && ((lt.Active && lt.Middle) || (rt.Active && rt.Middle)))
            {
                loggedMiddle = true;
                Plugin.L.LogInfo($"Hands: middle finger on the {(lt.Middle ? "left" : "right")} grip (L1/R1 touch): middle, ring and little fingers close round the handle.");
            }
            if (!loggedNoMiddle && (lt.Active || rt.Active) && !lt.MiddleBound && !rt.MiddleBound)
            {
                loggedNoMiddle = true;
                Plugin.L.LogInfo("Hands: this SteamVR binding gives no grip touch (an older or custom binding?): middle, ring and little fingers hold the grip. The mod's default binding has it.");
            }
            if (!loggedNoSkeleton && (SteamVRInput.LeftHand.Valid || SteamVRInput.RightHand.Valid)
                && !SteamVRInput.LeftSkeleton.Valid && !SteamVRInput.RightSkeleton.Valid)
            {
                loggedNoSkeleton = true;
                Plugin.L.LogInfo("Hands: these controllers give SteamVR no hand skeleton; hands stay hidden.");
            }
        }

        /// <summary>The preferred layer if the camera draws it, else the lowest layer it draws.</summary>
        private static int FirstLayer(Camera cam, int preferred)
        {
            if (cam == null) return preferred;
            int mask = cam.cullingMask;
            if ((mask & (1 << preferred)) != 0) return preferred;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0 && i != overlayLayer) return i;
            return preferred;
        }

        private static void Hide(Rig rig)
        {
            if (rig.Root.activeSelf) rig.Root.SetActive(false);
        }

        private static readonly Vector3[] world = new Vector3[SteamVRInput.HandSkeleton.BoneCount];

        private static void Drive(Rig rig, SteamVRInput.HandSkeleton s, SteamVRInput.FingerTouch touch, float trigger, bool cockpit)
        {
            if (rig.Left) LeftWrist.Valid = false;
            var grip = cockpit ? CockpitGrip(rig.Left) : default;
            // one thing per hand: whichever it's closed on most
            var cup = cockpit ? CockpitCoffee.Grip(rig.Left) : default;
            if (cup.Weight > grip.Weight) grip = default;
            else cup = default;
            HandFrame frame = default;
            rig.Touch = touch;
            rig.Trigger = trigger;
            rig.Held = 0f;
            bool tracked = s != null && s.Valid && Measure(rig, s, touch, trigger, out frame);
            // a hand on the wheel or lever stays drawn there without tracking
            if (!tracked && !(grip.Weight >= 1f && rig.HasShape)) { Hide(rig); rig.Source = "hidden"; return; }
            if (!tracked) rig.Source = "held, not tracked";
            if (!rig.Root.activeSelf) rig.Root.SetActive(true);

            // blend between the tracked pose and the grip pose
            Vector3 wrist = frame.Wrist;
            Quaternion rot = tracked ? frame.Rotation : Quaternion.identity;
            float onRim = 0f;
            if (grip.Weight > 0f)
            {
                WrapAroundRim(rig, grip, out var rimWrist, out var rimRot);
                onRim = tracked ? grip.Weight * grip.Weight * (3f - 2f * grip.Weight) : 1f;
                wrist += (rimWrist - wrist) * onRim;
                rot = Quaternion.Slerp(rot, rimRot, onRim);
            }
            else if (cup.Weight > 0f && tracked)
            {
                WrapAroundCup(rig, cup, out var cupWrist, out var cupRot);
                onRim = cup.Weight * cup.Weight * (3f - 2f * cup.Weight);
                wrist += (cupWrist - wrist) * onRim;
                rot = Quaternion.Slerp(rot, cupRot, onRim);
            }
            rig.Held = onRim;
            Vector3 fwd = rot * Vector3.forward;
            if (rig.Left) LeftWrist = new WristPose { Valid = true, Position = wrist, Forward = fwd, Back = rot * Vector3.up, Scale = rig.Scale };

            Place(rig.Palm, wrist + fwd * (rig.PalmLength * 0.5f), rot, new Vector3(rig.PalmWidth, rig.PalmThick, rig.PalmLength), cockpit);
            // short wrist stub, narrower and thicker than the palm so faces never overlap
            float forearmLength = 0.055f * rig.Scale;
            Place(rig.Forearm, wrist - fwd * (forearmLength * 0.5f - 0.006f), rot,
                new Vector3(rig.PalmWidth * 0.78f, rig.PalmThick * 1.18f, forearmLength), cockpit);

            for (int f = 0; f < 5; f++)
            {
                Vector3 p = wrist + rot * rig.Base[f];
                for (int seg = 0; seg < 3; seg++)
                {
                    int i = f * 3 + seg;
                    Vector3 d = rig.Dir[i], up = rig.Up[i];
                    float len = rig.Len[i];
                    if (onRim > 0f)
                    {
                        d = Vector3.Slerp(d, rig.WrapDir[i], onRim);
                        up += (rig.WrapUp[i] - up) * onRim;
                        len += (rig.WrapLen[i] - len) * onRim;
                    }
                    d = rot * d;
                    PlaceSegment(rig, f, seg, p, d, rot * up, len, cockpit);
                    p += d * len;
                }
            }
        }

        /// <summary>Wheel or lever, whichever the hand is closed on most.</summary>
        private static GripPose CockpitGrip(bool isLeft)
        {
            var wheel = WheelGrab.Grip(isLeft);
            var lever = LeverGrab.Grip(isLeft);
            return lever.Weight > wheel.Weight ? lever : wheel;
        }

        /// <summary>Palm frame in world, shape and finger pose in palm space (touch sensors on Sense, else skeleton).</summary>
        private static bool Measure(Rig rig, SteamVRInput.HandSkeleton s, SteamVRInput.FingerTouch touch, float trigger, out HandFrame frame)
        {
            for (int i = 1; i < world.Length; i++)
                CameraDriver.TrackingToWorld(s.Position + s.Rotation * s.Joints[i], Quaternion.identity, out world[i], out _);
            CameraDriver.TrackingToWorld(Vector3.zero, s.Rotation, out _, out var rootRot);
            if (!HandFrame.From(world, rootRot, rig.Left, out frame)) return false;
            if (frame.Flipped && !rig.FlipLogged)
            {
                rig.FlipLogged = true;
                Plugin.L.LogInfo($"Hands: the {(rig.Left ? "left" : "right")} skeleton's palm faced the wrong way; the controller's axes were used.");
            }

            Quaternion toPalm = Quaternion.Inverse(frame.Rotation);
            rig.Scale = frame.Scale;
            rig.PalmLength = frame.PalmLength;
            rig.PalmWidth = frame.PalmWidth;
            rig.PalmThick = frame.PalmThickness;
            rig.ToIndexSign = Vector3.Dot(frame.ToIndex, frame.Across) >= 0f ? 1f : -1f;
            for (int f = 0; f < 5; f++) rig.Base[f] = toPalm * (world[Chains[f][0]] - frame.Wrist);
            rig.HasShape = true;
            Vector3 side = Vector3.right * rig.ToIndexSign; // thumb side

            if (touch.Active && VRConfig.FingersFromTouch.Value)
            {
                rig.Source = touchSource;
                SenseCurls(rig, touch, trigger);
                PoseThumb(rig, side);
                for (int f = 1; f < 5; f++)
                {
                    // splay when open, then bend each joint
                    Quaternion q = Quaternion.AngleAxis(Spread[f] * (1f - rig.Curl[f]) * rig.ToIndexSign, Vector3.up);
                    for (int seg = 0; seg < 3; seg++)
                    {
                        q *= Quaternion.AngleAxis(BendMax[seg] * rig.Curl[f], Vector3.right);
                        Bone(rig.Dir, rig.Up, rig.Len, f, seg, q * Vector3.forward, q * Vector3.up, Lengths[f][seg] * rig.Scale);
                    }
                }
                return true;
            }

            rig.Source = "skeleton";
            for (int f = 0; f < 5; f++)
            {
                var chain = Chains[f];
                Vector3 d = Vector3.forward;
                for (int seg = 0; seg < 3; seg++)
                {
                    Vector3 bone = toPalm * (world[chain[seg + 1]] - world[chain[seg]]);
                    float len = bone.magnitude;
                    if (len > 1e-4f) d = bone * (1f / len);
                    else len = 1e-4f;
                    Vector3 up = f == 0 ? Vector3.ProjectOnPlane(Vector3.up + side, d) : Vector3.Cross(d, Vector3.right);
                    Bone(rig.Dir, rig.Up, rig.Len, f, seg, d, up, len);
                }
            }
            return true;
        }

        /// <summary>Sense finger curls from touch, proximity, trigger and grip, eased (~80 ms) so nothing snaps.</summary>
        private static void SenseCurls(Rig rig, SteamVRInput.FingerTouch touch, float trigger)
        {
            float index = Reach(rig.IndexNear, touch.Index, touch.IndexNear, rig.Left, "index", "trigger");
            // no grip touch in the binding: assume the hand holds the grip
            float middle = touch.MiddleBound ? Reach(rig.MiddleNear, touch.Middle, touch.MiddleNear, rig.Left, "middle finger", "grip") : 1f;
            // holding the charm: whole hand closed
            bool charm = GrabArbiter.Owns(GrabArbiter.Charm, rig.Left);
            if (charm)
            {
                touch.Thumb = true;
                index = middle = 1f;
                trigger = Mathf.Max(trigger, 0.5f);
                touch.Squeeze = Mathf.Max(touch.Squeeze, 0.5f);
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f), now = Time.unscaledTime;
            bool settled = true;
            for (int f = 0; f < 5; f++)
            {
                float target;
                if (f == 0) target = touch.Thumb ? 0f : 1f;
                else
                {
                    float on = f == 1 ? index : middle, press = Mathf.Clamp01(f == 1 ? trigger : touch.Squeeze);
                    target = OpenCurl[f] + (HoldCurl[f] - OpenCurl[f]) * on + (FullCurl[f] - HoldCurl[f]) * press;
                }
                if (Mathf.Abs(target - rig.Target[f]) > 0.1f && now <= ReadoutUntil) rig.SettleStart = now;
                rig.Target[f] = target;
                rig.Curl[f] = Ease(rig.Curl[f], target, ref rig.CurlSpeed[f], Settle[f], dt);
                if (Mathf.Abs(rig.Curl[f] - target) > 0.04f) settled = false;
            }
            if (settled && rig.SettleStart >= 0f)
            {
                Plugin.L.LogInfo($"hands readout {(rig.Left ? "left" : "right")}: fingers settled in {Mathf.RoundToInt((now - rig.SettleStart) * 1000f)} ms");
                rig.SettleStart = -1f;
            }
        }

        /// <summary>1 touching, else a share of proximity once the sensor is trusted, else 0.</summary>
        private static float Reach(NearCheck check, bool touching, float near, bool isLeft, string finger, string button)
        {
            if (near < 0f) return touching ? 1f : 0f;
            if (touching) { if (near > 0.6f) check.SeenOn = true; }
            else if (near < 0.2f) check.SeenOff = true;
            if (!check.Trusted) return touching ? 1f : 0f;
            if (!check.Logged)
            {
                check.Logged = true;
                Plugin.L.LogInfo($"Hands: the {(isLeft ? "left" : "right")} {button}'s proximity sensor follows the {finger} (low off, high on): it starts closing as it nears the {button}.");
            }
            return touching ? 1f : NearShare * near;
        }

        /// <summary>Critically damped step, settles in about 2.4 x settle.</summary>
        private static float Ease(float current, float target, ref float speed, float settle, float dt)
        {
            float omega = 2f / settle, x = omega * dt;
            float decay = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (speed + omega * change) * dt;
            speed = (speed - omega * temp) * decay;
            return target + (change + temp) * decay;
        }

        private static void Bone(Vector3[] dirs, Vector3[] ups, float[] lens, int finger, int seg, Vector3 d, Vector3 up, float len)
        {
            int i = finger * 3 + seg;
            dirs[i] = d;
            ups[i] = up;
            lens[i] = len;
        }

        // thumb bones resting on the face buttons / lifted, as (to knuckles, to thumb side, to back of hand)
        private static readonly Vector3[] ThumbRest = { new Vector3(0.55f, 0.70f, -0.20f), new Vector3(0.85f, 0.20f, -0.40f), new Vector3(0.75f, -0.15f, -0.55f) };
        private static readonly Vector3[] ThumbLifted = { new Vector3(0.50f, 0.80f, -0.05f), new Vector3(0.60f, 0.75f, -0.05f), new Vector3(0.60f, 0.70f, -0.10f) };

        private static void PoseThumb(Rig rig, Vector3 side)
        {
            float t = rig.Curl[0];
            Vector3 Dir(Vector3 c) => (Vector3.forward * c.x + side * c.y + Vector3.up * c.z).normalized;
            for (int seg = 0; seg < 3; seg++)
            {
                Vector3 d = Vector3.Slerp(Dir(ThumbRest[seg]), Dir(ThumbLifted[seg]), t).normalized;
                Bone(rig.Dir, rig.Up, rig.Len, 0, seg, d, Vector3.ProjectOnPlane(Vector3.up + side, d), Lengths[0][seg] * rig.Scale);
            }
        }

        // angles around the rim tube, from the palm side
        private const float MaxWrap = 290f * Mathf.Deg2Rad;
        private const float ThumbOnRim = 240f * Mathf.Deg2Rad; // inner front, toward driver and hub
        private static readonly Vector3[] thumbOnRim = new Vector3[4];

        /// <summary>
        /// Hand closed on the wheel rim or lever bar: palm pose out, fingers wrapped bone by bone into WrapDir/WrapUp.
        /// Thumb lies along the rim past the index knuckle.
        /// </summary>
        private static void WrapAroundRim(Rig rig, GripPose grip, out Vector3 wrist, out Quaternion rot)
        {
            Vector3 fwd = grip.Forward, toIndex = grip.ThumbSide;
            Vector3 back = rig.Left ? Vector3.Cross(fwd, toIndex) : Vector3.Cross(toIndex, fwd);
            rot = Quaternion.LookRotation(fwd, back);
            float scale = rig.Scale, tube = grip.TubeRadius, rim = Mathf.Max(grip.RimRadius, 0.05f);
            Vector3 bar = HandFrame.GripLocal(rig.PalmLength, rig.PalmThick, tube);
            wrist = grip.Point - rot * bar;

            // mirror if the skeleton's palm came flipped
            float sideSign = rig.Left ? 1f : -1f;
            float mirror = sideSign * rig.ToIndexSign;
            Vector3 inward = Quaternion.Inverse(rot) * -grip.Outward;
            Vector3 RingAt(float x)
            {
                float dx = x - bar.x;
                return bar + inward * (dx * dx / (2f * rim)); // rim curves in toward the hub
            }

            for (int f = 1; f < 5; f++)
            {
                Vector3 joint = rig.Base[f];
                joint.x *= mirror;
                Vector3 c = RingAt(joint.x);
                float radius = tube + 0.5f * Widths[f] * Taper[1] * scale + 0.0005f; // bone centreline on the tube
                float around = -Mathf.PI;
                for (int seg = 0; seg < 3; seg++)
                {
                    // string round a pulley: each bone follows the tangent to the tube
                    float py = joint.y - c.y, pz = joint.z - c.z;
                    float reach = Mathf.Max(Mathf.Sqrt(py * py + pz * pz), radius + 1e-4f);
                    float at = Mathf.Atan2(pz, py);
                    if (at < around - Mathf.PI) at += 2f * Mathf.PI; // keep counting past half a turn
                    around = at;
                    float touch = Mathf.Min(at + Mathf.Acos(radius / reach), MaxWrap);
                    Vector3 d = new Vector3(0f, c.y + radius * Mathf.Cos(touch) - joint.y, c.z + radius * Mathf.Sin(touch) - joint.z).normalized;
                    float len = Lengths[f][seg] * scale;
                    Bone(rig.WrapDir, rig.WrapUp, rig.WrapLen, f, seg, d, Vector3.Cross(d, Vector3.right), len);
                    joint += d * len;
                }
            }

            Vector3 side = Vector3.right * sideSign;
            float thumbRadius = tube + 0.5f * Widths[0] * Taper[1] * scale + 0.0005f;
            Vector3 OnRim(float x)
            {
                Vector3 c = RingAt(x);
                return new Vector3(x, c.y + thumbRadius * Mathf.Cos(ThumbOnRim), c.z + thumbRadius * Mathf.Sin(ThumbOnRim));
            }
            Vector3 thumbBase = rig.Base[0];
            thumbBase.x *= mirror;
            float x0 = rig.Base[1].x * mirror + sideSign * 0.012f * scale;
            float x1 = x0 + sideSign * Lengths[0][1] * scale, x2 = x1 + sideSign * Lengths[0][2] * scale;
            var thumb = thumbOnRim;
            thumb[0] = thumbBase;
            thumb[1] = OnRim(x0);
            thumb[2] = OnRim(x1);
            thumb[3] = OnRim(x2);
            for (int seg = 0; seg < 3; seg++)
            {
                Vector3 bone = thumb[seg + 1] - thumb[seg];
                float len = Mathf.Max(bone.magnitude, 1e-4f);
                Vector3 d = bone * (1f / len);
                Bone(rig.WrapDir, rig.WrapUp, rig.WrapLen, 0, seg, d, Vector3.ProjectOnPlane(Vector3.up + side, d), len);
            }
        }

        /// <summary>
        /// Hand round the coffee cup: palm on its side, fingers round the front, thumb round the back.
        /// The cup tapers, so each finger wraps at its own radius. Fills the same Wrap buffers as the rim.
        /// </summary>
        private static void WrapAroundCup(Rig rig, CockpitCoffee.HandGrip cup, out Vector3 wrist, out Quaternion rot)
        {
            Vector3 up = cup.Up, back = cup.Out;
            Vector3 fwd = rig.Left ? Vector3.Cross(up, back) : Vector3.Cross(back, up);
            rot = Quaternion.LookRotation(fwd, back);
            float scale = rig.Scale, sideSign = rig.Left ? 1f : -1f, mirror = sideSign * rig.ToIndexSign;
            // cup axis toward the knuckles, so the heel of the hand stands off a bit
            var axis = new Vector3(0f, -rig.PalmThick * 0.5f - cup.Radius + 0.0015f, rig.PalmLength * 0.85f);
            wrist = cup.Point - rot * axis;
            float RadiusAt(float x) => cup.Radius + CoffeeModel.Taper * x * sideSign; // toward the index = up the cup

            for (int f = 1; f < 5; f++)
            {
                Vector3 joint = rig.Base[f];
                joint.x *= mirror;
                WrapRound(rig, f, joint, axis, RadiusAt(joint.x) + 0.5f * Widths[f] * Taper[1] * scale + 0.0005f, 1f, joint.x);
            }
            // thumb wraps the other way, just above the index
            Vector3 thumb = rig.Base[0];
            thumb.x *= mirror;
            float height = rig.Base[1].x * mirror + sideSign * 0.012f * scale;
            WrapRound(rig, 0, thumb, axis, RadiusAt(height) + 0.5f * Widths[0] * Taper[1] * scale + 0.0005f, -1f, height);
        }

        /// <summary>One finger round a circle in the plane at x, bone by bone along tangents. way: +1 fingers, -1 thumb.</summary>
        private static void WrapRound(Rig rig, int finger, Vector3 joint, Vector3 centre, float radius, float way, float x)
        {
            Vector3 side = Vector3.right * (rig.Left ? 1f : -1f);
            for (int seg = 0; seg < 3; seg++)
            {
                float py = joint.y - centre.y, pz = joint.z - centre.z;
                float reach = Mathf.Max(Mathf.Sqrt(py * py + pz * pz), radius + 1e-4f);
                float at = Mathf.Atan2(pz, py) + way * Mathf.Acos(radius / reach);
                var target = new Vector3(x, centre.y + radius * Mathf.Cos(at), centre.z + radius * Mathf.Sin(at));
                Vector3 d = (target - joint).normalized;
                float len = Lengths[finger][seg] * rig.Scale;
                Vector3 boneUp = finger == 0 ? Vector3.ProjectOnPlane(Vector3.up + side, d) : Vector3.Cross(d, Vector3.right);
                Bone(rig.WrapDir, rig.WrapUp, rig.WrapLen, finger, seg, d, boneUp, len);
                joint += d * len;
            }
        }

        /// <summary>Tapered, tucked a little into the previous bone.</summary>
        private static void PlaceSegment(Rig rig, int finger, int seg, Vector3 joint, Vector3 d, Vector3 up, float len, bool cockpit)
        {
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            float w = Widths[finger] * Taper[seg] * rig.Scale;
            float tuck = seg == 0 ? w * 0.25f : w * 0.3f;
            Place(rig.Fingers[finger][seg], joint - d * tuck, Quaternion.LookRotation(d, up), new Vector3(w, w, len + tuck), cockpit);
        }

        private static void Place(Part p, Vector3 pos, Quaternion rot, Vector3 scale, bool cockpit)
        {
            if (cockpit) CameraDriver.WorldToCockpit(pos, rot, out pos, out rot);
            p.T.SetPositionAndRotation(pos, rot);
            p.T.localScale = scale;
        }

        // ---------------------------------------------------------------- readout

        private static readonly float[] driverCurls = new float[5];

        /// <summary>Dev "hands readout [s]": a line per hand every 0.25 s.</summary>
        private static void Readout()
        {
            if (Time.unscaledTime > ReadoutUntil || Time.unscaledTime < nextReadout) return;
            nextReadout = Time.unscaledTime + 0.25f;
            LogReadout();
        }

        /// <summary>Per hand: inputs in, curls out, SteamVR's own curls, and how closed on a grip.</summary>
        public static void LogReadout()
        {
            if (left == null) return;
            foreach (var rig in new[] { left, right })
            {
                var t = rig.Touch;
                string N(float v) => v < 0f ? "-" : v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                string C(int f) => rig.Source == "skeleton" ? "skeleton" : rig.Curl[f].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                string driver = SteamVRInput.DriverCurls(rig.Left, driverCurls)
                    ? $"{N(driverCurls[0])} {N(driverCurls[1])} {N(driverCurls[2])} {N(driverCurls[3])} {N(driverCurls[4])}"
                    : "none";
                Plugin.L.LogInfo($"hands readout {(rig.Left ? "left" : "right")} ({rig.Source}, {(SteamVRInput.ControllerType.Length > 0 ? SteamVRInput.ControllerType : "no controller")}): "
                    + $"thumb touch {(t.Thumb ? 1 : 0)} -> {C(0)} | index touch {(t.Index ? 1 : 0)} near {N(t.IndexNear)} pull {N(rig.Trigger)} -> {C(1)} | "
                    + $"middle touch {(t.MiddleBound ? (t.Middle ? "1" : "0") : "unbound")} near {N(t.MiddleNear)} squeeze {N(t.Squeeze)} -> {C(2)} | ring -> {C(3)} | pinky -> {C(4)} | "
                    + $"driver curls {driver} | held {N(rig.Held)}");
            }
        }

        // ---------------------------------------------------------------- demo

        /// <summary>Dev "hands demo": both hands in front of the head.</summary>
        private static void FillDemo(SteamVRInput.HandSkeleton s, bool isLeft)
        {
            float side = isLeft ? -1f : 1f;
            VRSession.SampleHead();
            Quaternion head = Quaternion.Euler(0f, VRSession.HeadRotation.eulerAngles.y, 0f);
            Quaternion view = DemoView switch
            {
                1 => Quaternion.Euler(15f, 0f, -90f * side),  // palms up
                2 => Quaternion.Euler(10f, 0f, 0f),           // side: thumb on top
                3 => Quaternion.Euler(35f, 0f, 0f),           // holding a controller
                _ => Quaternion.Euler(15f, 0f, 90f * side),   // backs up
            };
            DemoSkeleton(s, isLeft, VRSession.HeadPosition + head * new Vector3(0.11f * side, 0.0f, 0.40f), head * view);
        }

        /// <summary>Dev "wheel", "lever", "coffee": demo skeleton placed so its grip sits on the simulated hold.</summary>
        private static void DriveSimulated(Rig rig, SteamVRInput.HandSkeleton s, SteamVRInput.FingerTouch touch, bool cockpit)
        {
            Vector3 grip;
            Quaternion rotation;
            float bar;
            bool onLever = LeverGrab.Grip(rig.Left).Weight > WheelGrab.Grip(rig.Left).Weight;
            bool simulated = onLever
                ? LeverGrab.SimulatedHand(rig.Left, out grip, out rotation, out bar) || WheelGrab.SimulatedHand(rig.Left, out grip, out rotation, out bar)
                : WheelGrab.SimulatedHand(rig.Left, out grip, out rotation, out bar) || LeverGrab.SimulatedHand(rig.Left, out grip, out rotation, out bar);
            if (!simulated && CockpitCoffee.SimulatedHand(rig.Left, out grip, out rotation))
            {
                // open until it closes round the cup
                simulated = true;
                bar = CockpitCoffee.GripRadius;
                touch = new SteamVRInput.FingerTouch { Active = true, MiddleBound = true, IndexNear = -1f, MiddleNear = -1f };
            }
            if (!simulated) { Hide(rig); return; }
            DemoSkeleton(s, rig.Left, Vector3.zero, Quaternion.identity);
            for (int i = 1; i < world.Length; i++) world[i] = s.Joints[i];
            Vector3 offset = HandFrame.From(world, Quaternion.identity, rig.Left, out var f) ? f.GripCentre(bar) : Vector3.zero;
            s.Position = grip - rotation * offset;
            s.Rotation = rotation;
            Drive(rig, s, touch, 0f, cockpit);
        }

        /// <summary>Minimal skeleton in SteamVR's hand frame: fingers forward, index up, right hand's back to the right.</summary>
        private static void DemoSkeleton(SteamVRInput.HandSkeleton s, bool isLeft, Vector3 position, Quaternion rotation)
        {
            s.Position = position;
            s.Rotation = rotation;
            var j = s.Joints;
            j[1] = Vector3.zero;
            j[2] = Mirror(new Vector3(-0.010f, 0.020f, 0.022f), isLeft);
            float[] ys = { 0f, 0.026f, 0.006f, -0.013f, -0.030f };
            float[] zs = { 0f, 0.085f, 0.088f, 0.083f, 0.075f };
            for (int f = 1; f < 5; f++)
            {
                j[Chains[f][0]] = new Vector3(0f, ys[f], zs[f]);
                // placeholder joints, the demo drives fingers like a Sense
                for (int k = 1; k < 4; k++) j[Chains[f][k]] = j[Chains[f][0]] + Vector3.forward * 0.02f * k;
            }
            for (int k = 1; k < 4; k++) j[Chains[0][k]] = j[2] + new Vector3(0f, 0.012f, 0.02f) * k;
            s.Valid = true;
        }

        private static Vector3 Mirror(Vector3 p, bool isLeft) => isLeft ? new Vector3(-p.x, p.y, p.z) : p;
    }
}
