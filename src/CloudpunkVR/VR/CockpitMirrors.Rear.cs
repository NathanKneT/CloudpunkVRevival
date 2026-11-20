using System;
using System.Text;
using CloudpunkVR.UI;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Rear-view mirror hanging left of Camus. A real flat mirror: one camera at the head's reflection, frustum cut
    /// to the glass, pushed back past the HOVA's tail (no rear window). Drawn every frame, once for both eyes.
    /// </summary>
    internal static partial class CockpitMirrors
    {
        // glass and frame, m. Left of Camus, a bit above his head, 2 cm ahead of his chain, nudged left to clear the sign.
        private const float GlassW = 0.16f, GlassH = 0.045f, Bezel = 0.008f, FrameFront = 0.003f, FrameBack = 0.006f;
        private const float CharmHalf = 0.03f, Gap = 0.06f, MirrorUp = 0.115f, AheadOfChain = 0.02f;
        private static readonly Vector3 UsualPivot = new Vector3(0.26f, 0.21f, 0.28f); // Camus's usual pivot, from the eyes
        private const float CharmWait = 4f; // s to wait for Camus before using the usual spot
        private const int RearLow = 512, RearHigh = 768;

        /// <summary>Dev "mirrors at": mirror centre from the resting eyes.</summary>
        public static Vector3? RearSpot;

        private static GameObject rearRoot;
        private static Transform rearT, stemT, rearFrame;
        private static MeshRenderer glassR, frameR, stemR, lightR;
        private static Material frameMat, glassMat, lightMat;
        private static string frameShader = "?";
        private static bool frameIsRania;
        private static float nextRaniaCheck;
        private static UnityEngine.Rendering.PostProcessing.PostProcessLayer rearLook;
        private static bool rearLookApplied;
        private static IntPtr lookShown = (IntPtr)(-1);
        private static bool rearBroken, rearLookOn = true;
        private static float nextRearLookCheck;
        // palette entries (CharmModel's layout, recoloured)
        private const int Body = CharmModel.Gunmetal, Edge = CharmModel.Steel, Shell = CharmModel.Fur, Lamp = CharmModel.White;
        private static Texture2D palette;
        private static Mesh stemMesh;
        private static Camera rearCam;
        private static IntPtr rearPtr;
        private static RenderTexture rearRt;
        private static readonly Stats rearStats = new Stats();
        private static Vector3 rearLocalPos, rearSpotUsed;
        private static Quaternion rearLocalRot = Quaternion.identity, stemLocalRot = Quaternion.identity;
        private static bool rearPlaced, rearHid, rearWarned, projectionBroken, glassSet;
        private static IntPtr glassShows;
        private static readonly Vector3 JointTop = new Vector3(0f, GlassH * 0.5f + Bezel + 0.012f, -0.008f); // where the stem meets the frame
        private static int rearAimedSpace = -1, rearLayer = -1;
        private static string rearPlace = "not placed";
        private static bool placedWithCharm;
        private static Transform tailOf;
        private static float tailZ, rearShift;
        private static int tailChildren = -1;
        private static Vector3 lastViewPos;
        private static Quaternion lastViewRot = Quaternion.identity;
        private static bool haveLastView;
        private static Transform carT;
        private static IntPtr carOf;
        private static Matrix4x4 rearProjection;
        private const float RearFar = 120f;
        private static Vector3 placedPivot;
        private static float cockpitSince = -1f;

        /// <summary>Each frame before rendering.</summary>
        private static void UpdateRear(VehicleInterior vi, bool cockpit)
        {
            try
            {
                var set = cockpit ? vi.interiorLocal : null;
                bool want = VRConfig.RearMirror.Value && !rearBroken && set != null && CameraDriver.CockpitCamera != null;
                if (!want)
                {
                    HideRear();
                    if (!cockpit) cockpitSince = -1f;
                    return;
                }
                if (cockpitSince < 0f) cockpitSince = Time.unscaledTime;
                // placed once Camus is (or soon without him), again when his place changes
                bool charm = CharmPlaced(set);
                bool waiting = !charm && VRConfig.CockpitCharm.Value && Time.unscaledTime - cockpitSince < CharmWait;
                if (!rearPlaced && waiting) { HideRear(); return; }
                if (!rearPlaced || rearFrame != set
                    || (charm && (!placedWithCharm || (CharmMount.PivotLocal - placedPivot).sqrMagnitude > 1e-6f)))
                {
                    if (rearRoot == null && !CreateRear(vi)) return;
                    PlaceRearAt(set, charm);
                }
                if (CameraDriver.SpaceVersion != rearAimedSpace) AimRear(); // re-aim at the head on recenter
                FollowRania();
                KeepRearLayer();
                var q = VRConfig.Mirrors.Value;
                if (q != MirrorQuality.Off) SizeRear(q == MirrorQuality.High ? RearHigh : RearLow);
                if (Time.unscaledTime >= nextRearLookCheck)
                {
                    nextRearLookCheck = Time.unscaledTime + 1f;
                    if (rearLook == null && GameLook && MainLookOn) rearLook = AddGameLookLate(rearCam.gameObject);
                    var car = Car();
                    if (car != null && tailOf == car && car.childCount != tailChildren) MeasureTail(car); // an upgrade fitted
                    bool lookOn = GameLook && MainLookOn;
                    if (lookOn != rearLookOn) { rearLookOn = lookOn; lookShown = (IntPtr)(-1); }
                }
                IntPtr shown = q == MirrorQuality.Off ? IntPtr.Zero : rearRt.Pointer;
                if (shown != glassShows || !glassSet)
                {
                    glassMat.SetTexture(EmissionMapId, q == MirrorQuality.Off ? (Texture)Texture2D.blackTexture : rearRt);
                    glassShows = shown;
                    glassSet = true;
                }
                if (!rearRoot.activeSelf) rearRoot.SetActive(true);
                if (rearLookApplied != GameLook || shown != lookShown)
                {
                    ApplyLook(rearCam, rearLook, q == MirrorQuality.Off ? null : glassMat, Color.white);
                    rearLookApplied = GameLook;
                    lookShown = shown;
                }
                wantRear = q != MirrorQuality.Off && !TransitionGuard.Active && !projectionBroken && InView(glassR);
            }
            catch (Exception e)
            {
                if (!rearWarned) { rearWarned = true; Plugin.L.LogWarning("Rear-view mirror: " + e.Message); }
                if (rearCam != null) rearCam.enabled = false;
            }
        }

        /// <summary>Off for the session on failure, the rest carries on.</summary>
        private static void BreakRear(Exception e)
        {
            rearBroken = true;
            Plugin.L.LogWarning("Rear-view mirror off for this session: " + e.Message);
            try { HideRear(); } catch (Exception) { }
        }

        private static bool CreateRear(VehicleInterior vi)
        {
            try
            {
                BuildRear(vi);
                return true;
            }
            catch (Exception e)
            {
                if (rearRoot != null) UnityEngine.Object.Destroy(rearRoot);
                rearRoot = null;
                rearCam = null;
                rearPtr = IntPtr.Zero;
                BreakRear(e);
                return false;
            }
        }

        private static void BuildRear(VehicleInterior vi)
        {
            rearRoot = new GameObject("CloudpunkVR_RearMirror");
            rearRoot.SetActive(false); // shown once placed
            UnityEngine.Object.DontDestroyOnLoad(rearRoot);
            MakeRearMaterials(vi);

            rearT = Part("Mirror", BuildFrame(), frameMat, out frameR);
            glassR = Child(rearT, "Glass", BuildGlass(), glassMat);
            lightR = Child(rearT, "Light", BuildLightStrip(), lightMat);
            stemMesh = new Mesh { name = "CloudpunkVR_MirrorStem" };
            stemT = Part("Stem", stemMesh, frameMat, out stemR);

            var go = new GameObject("CloudpunkVR_RearMirrorCamera");
            go.SetActive(false); // post-processing gets its resources before it wakes
            go.transform.SetParent(rearRoot.transform, false);
            rearCam = go.AddComponent<Camera>();
            var from = gameCam != null ? gameCam : Global.Instance != null ? Global.Instance.mainCamera : null;
            if (from != null)
            {
                try { rearCam.CopyFrom(from); } catch (Exception) { }
            }
            // not the cockpit set (28, 31), our overlay (30), UI (5) or the HOVA's world-side interior (16, game's mirror skips it too)
            int mask = gameCam != null ? gameCam.cullingMask : from != null ? from.cullingMask : ~0;
            rearCam.cullingMask = mask & ~((1 << 28) | (1 << 31) | (1 << 30) | (1 << 5) | (1 << 16));
            rearCam.clearFlags = CameraClearFlags.Skybox;
            rearCam.targetTexture = null;
            rearCam.stereoTargetEye = StereoTargetEyeMask.None;
            rearCam.allowMSAA = false;
            rearCam.depth = 0f;
            rearCam.farClipPlane = RearFar;
            rearCam.enabled = false;
            CullSmallDetail(rearCam);
            // PostProcessLayer resets the projection before culling unless physical properties are on: keeps our off-centre one
            try { rearCam.usePhysicalProperties = true; } catch (Exception) { }
            rearLook = AddGameLook(go);
            go.SetActive(true);
            rearPtr = rearCam.Pointer;
            rearLookApplied = !GameLook; // applied on the first frame
        }

        /// <summary>Cockpit colours, drawn with Rania's material: the interior's lightmapped one comes out white on a runtime mesh.</summary>
        private static void MakeRearMaterials(VehicleInterior vi)
        {
            Color body = CoffeeMount.HaveColor ? CoffeeMount.ConsoleColor : (Color)new Color32(40, 44, 50, 255);
            Color edge = Color.Lerp(body, new Color(0.62f, 0.66f, 0.72f, 1f), 0.28f);
            Color shell = body * 0.7f;
            Color light = new Color32(214, 230, 242, 255);
            palette = new Texture2D(CharmModel.PaletteSize, CharmModel.Variants, TextureFormat.RGBA32, false)
            {
                name = "CloudpunkVR_MirrorPalette",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            for (int v = 0; v < CharmModel.Variants; v++)
            {
                float shade = 0.94f + 0.04f * v; // slight per-voxel variation
                for (int i = 0; i < CharmModel.PaletteSize; i++)
                {
                    Color c = i == Body ? body : i == Edge ? edge : i == Shell ? shell : i == Lamp ? light : CharmModel.Texel(i, v, false);
                    if (i == Body || i == Edge || i == Shell) c = new Color(c.r * shade, c.g * shade, c.b * shade, 1f);
                    palette.SetPixel(i, v, c);
                }
            }
            palette.Apply(false);

            var standard = Shader.Find("Standard");
            frameMat = FrameMaterial(standard);

            glassMat = new Material(standard) { name = "CloudpunkVR_MirrorGlass", hideFlags = HideFlags.DontUnloadUnusedAsset };
            glassMat.SetColor("_Color", Color.black);
            if (glassMat.HasProperty("_Glossiness")) glassMat.SetFloat("_Glossiness", 0.88f);
            glassMat.EnableKeyword("_EMISSION");
            glassMat.SetColor("_EmissionColor", GlassTint);
            glassMat.SetTexture(EmissionMapId, Texture2D.blackTexture); // dark until its first picture
            glassMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;

            lightMat = new Material(standard) { name = "CloudpunkVR_MirrorLight", hideFlags = HideFlags.DontUnloadUnusedAsset };
            lightMat.SetColor("_Color", Color.black);
            lightMat.EnableKeyword("_EMISSION");
            lightMat.SetColor("_EmissionColor", light * 0.8f);
            lightMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        /// <summary>Rania's material with the palette, else Standard until hers exists.</summary>
        private static Material FrameMaterial(Shader standard)
        {
            var rania = RaniaMaterial();
            frameIsRania = rania != null;
            var m = rania != null
                ? new Material(rania) { name = "CloudpunkVR_MirrorFrame", mainTexture = palette, mainTextureScale = Vector2.one, mainTextureOffset = Vector2.zero, hideFlags = HideFlags.DontUnloadUnusedAsset }
                : new Material(standard) { name = "CloudpunkVR_MirrorFrame", mainTexture = palette, hideFlags = HideFlags.DontUnloadUnusedAsset };
            NoGlow(m);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.3f);
            frameShader = m.shader != null ? m.shader.name : "?";
            return m;
        }

        private static void FollowRania()
        {
            if (frameIsRania || Time.unscaledTime < nextRaniaCheck || frameR == null) return;
            nextRaniaCheck = Time.unscaledTime + 3f;
            if (RaniaMaterial() == null) return;
            var old = frameMat;
            frameMat = FrameMaterial(old.shader);
            frameR.sharedMaterial = frameMat;
            stemR.sharedMaterial = frameMat;
            UnityEngine.Object.Destroy(old);
            Plugin.L.LogInfo($"Mirrors: the rear-view mirror's frame drawn with Rania's material ({frameShader}).");
        }

        /// <summary>Rania's hand material. Null before her model loads.</summary>
        private static Material RaniaMaterial()
        {
            var player = Global.Instance != null ? Global.Instance.player : null;
            if (player == null) return null;
            var all = player.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == "rania_hand" && all[i].sharedMaterial != null) return all[i].sharedMaterial;
            return null;
        }

        /// <summary>No glow or metal maps from the copied material.</summary>
        private static void NoGlow(Material m)
        {
            foreach (var prop in new[] { "_EmissionMap", "_EmissionTex", "_EmissiveMap", "_EmissiveTex", "_MetallicGlossMap", "_MetallicMap", "_MetallicTex", "_SpecGlossMap" })
                if (m.HasProperty(prop)) m.SetTexture(prop, Texture2D.blackTexture);
            foreach (var prop in new[] { "_EmissionColor", "_EmissiveColor" })
                if (m.HasProperty(prop)) m.SetColor(prop, Color.black);
            m.DisableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        private static Transform Part(string name, Mesh mesh, Material m, out MeshRenderer r)
        {
            var go = new GameObject("CloudpunkVR_RearMirror" + name);
            go.transform.SetParent(rearRoot.transform, false);
            r = Renderer(go, mesh, m);
            return go.transform;
        }

        private static MeshRenderer Child(Transform parent, string name, Mesh mesh, Material m)
        {
            var go = new GameObject("CloudpunkVR_RearMirror" + name);
            go.transform.SetParent(parent, false);
            return Renderer(go, mesh, m);
        }

        private static MeshRenderer Renderer(GameObject go, Mesh mesh, Material m)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.allowOcclusionWhenDynamic = false; // baked occlusion would hide unknown objects
            return mr;
        }

        /// <summary>4 mm voxels, 2 cm deep: stepped bezel, lighter inner edge, smaller back shell, stem joint.</summary>
        private static Mesh BuildFrame()
        {
            const float v = 0.004f;
            float hw = GlassW * 0.5f, hh = GlassH * 0.5f, ow = hw + Bezel, oh = hh + Bezel;
            float back = -FrameBack, front = FrameFront;
            var b = new CharmModel.Builder();
            // bezel: stepped corners
            b.Box(new Vector3(-ow + v, hh, back), new Vector3(ow - v, oh, front), Body, 0);
            b.Box(new Vector3(-ow + v, -oh, back), new Vector3(ow - v, -hh, front), Body, 1);
            b.Box(new Vector3(-ow, -oh + v, back), new Vector3(-hw, oh - v, front), Body, 2);
            b.Box(new Vector3(hw, -oh + v, back), new Vector3(ow, oh - v, front), Body, 3);
            // lighter edge, a hair proud
            b.Box(new Vector3(-hw - 0.002f, hh, front), new Vector3(hw + 0.002f, hh + 0.002f, front + 0.001f), Edge, 0);
            b.Box(new Vector3(-hw - 0.002f, -hh - 0.002f, front), new Vector3(hw + 0.002f, -hh, front + 0.001f), Edge, 1);
            b.Box(new Vector3(-hw - 0.002f, -hh, front), new Vector3(-hw, hh, front + 0.001f), Edge, 2);
            b.Box(new Vector3(hw, -hh, front), new Vector3(hw + 0.002f, hh, front + 0.001f), Edge, 3);
            // back, then the shell a voxel smaller, stepped again
            b.Box(new Vector3(-hw, -hh, back), new Vector3(hw, hh, -0.0005f), Shell, 2);
            b.Box(new Vector3(-ow + 2f * v, -oh + v, back - 2f * v), new Vector3(ow - 2f * v, oh - v, back), Shell, 1);
            b.Box(new Vector3(-ow + 3f * v, -oh + 2f * v, back - 3f * v), new Vector3(ow - 3f * v, oh - 2f * v, back - 2f * v), Shell, 3);
            b.Box(new Vector3(-1.5f * v, oh, -0.012f), new Vector3(1.5f * v, JointTop.y, -0.004f), Edge, 1);
            return b.ToMesh("CloudpunkVR_MirrorFrame");
        }

        private static Mesh BuildLightStrip()
        {
            float half = GlassW * 0.36f, y = -(GlassH * 0.5f + Bezel);
            var b = new CharmModel.Builder();
            b.Box(new Vector3(-half, y - 0.0015f, -0.008f), new Vector3(half, y, 0.0f), Lamp, 0);
            return b.ToMesh("CloudpunkVR_MirrorLight");
        }

        /// <summary>One quad facing +z (the driver), u along +x, v along +y.</summary>
        private static Mesh BuildGlass()
        {
            float hw = GlassW * 0.5f, hh = GlassH * 0.5f;
            var mesh = new Mesh { name = "CloudpunkVR_MirrorGlass" };
            mesh.vertices = new[] { new Vector3(hw, -hh, 0f), new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f), new Vector3(-hw, -hh, 0f) };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.uv = new[] { new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Vertical stem up to the ceiling, with a ceiling plate.</summary>
        private static void BuildStem(float length)
        {
            const float v = 0.01f;
            var b = new CharmModel.Builder();
            b.Box(new Vector3(-0.6f * v, 0f, -0.6f * v), new Vector3(0.6f * v, length, 0.6f * v), CharmModel.Steel, 1);
            b.Box(new Vector3(-1.5f * v, length - 0.5f * v, -1.5f * v), new Vector3(1.5f * v, length, 1.5f * v), CharmModel.Gunmetal, 2);
            var built = b.ToMesh("CloudpunkVR_MirrorStem");
            stemMesh.Clear();
            stemMesh.vertices = built.vertices;
            stemMesh.normals = built.normals;
            stemMesh.uv = built.uv;
            stemMesh.triangles = built.triangles;
            stemMesh.RecalculateBounds();
            UnityEngine.Object.Destroy(built);
        }

        private static bool CharmPlaced(Transform frame) => VRConfig.CockpitCharm.Value && CharmMount.Valid && CharmMount.Parent == frame;

        private static void EnableRear(bool on)
        {
            if (rearCam != null && rearRoot != null && rearRoot.activeSelf && rearCam.enabled != on) rearCam.enabled = on;
        }

        private static void HideRear()
        {
            if (rearCam != null && rearCam.enabled) rearCam.enabled = false;
            if (rearRoot != null && rearRoot.activeSelf) rearRoot.SetActive(false);
            CharmMount.ClearObstacle();
        }

        /// <summary>From the resting eyes: left of Camus, nudged left until the sign shows whole, aimed at the head.</summary>
        private static void PlaceRearAt(Transform frame, bool charm)
        {
            rearFrame = frame;
            placedWithCharm = charm;
            placedPivot = charm ? CharmMount.PivotLocal : Vector3.zero;
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eye, out var eyeRot);
            Quaternion toEye = Quaternion.Inverse(eyeRot);
            Vector3 pivot = charm ? toEye * (frame.TransformPoint(CharmMount.PivotLocal) - eye) : UsualPivot;
            Vector3 spot;
            int shifted = 0;
            bool signClear = true;
            if (RearSpot.HasValue) spot = RearSpot.Value;
            else
            {
                spot = new Vector3(pivot.x - CharmHalf - Gap - (GlassW * 0.5f + Bezel), MirrorUp, pivot.z + AheadOfChain);
                bool sign = SignEdge(eye, eyeRot, out var a, out var b);
                while (sign && shifted < 8 && Covers(eye, eyeRot, spot, a, b))
                {
                    spot.x -= 0.01f;
                    shifted++;
                }
                signClear = !sign || !Covers(eye, eyeRot, spot, a, b);
            }
            rearSpotUsed = spot;
            Vector3 m = eye + eyeRot * spot;
            rearLocalPos = frame.InverseTransformPoint(m);
            stemLocalRot = Quaternion.Inverse(frame.rotation) * eyeRot;
            AimRear();

            // stem reaches the ceiling where Camus hangs, else 6 cm
            Vector3 up = eyeRot * Vector3.up;
            Vector3 joint = m + frame.rotation * rearLocalRot * JointTop;
            float stem = 0.06f;
            string ceiling = "the ceiling estimated";
            if (charm)
            {
                float h = Vector3.Dot(frame.TransformPoint(CharmMount.PivotLocal) - joint, up) + CharmModel.MountHeight;
                if (h > 0.01f && h < 0.25f) { stem = h; ceiling = "the ceiling where Camus hangs"; }
            }
            BuildStem(stem);
            rearPlaced = true;
            var car = Car();
            if (car != null && (car != tailOf || car.childCount != tailChildren)) MeasureTail(car);

            float az = Mathf.Atan2(spot.x, spot.z) * Mathf.Rad2Deg;
            float el = Mathf.Atan2(spot.y, new Vector2(spot.x, spot.z).magnitude) * Mathf.Rad2Deg;
            string where = RearSpot.HasValue ? "where mirrors at put it"
                : (charm ? "beside Camus on his left" : "where Camus usually hangs, on his left (Camus not placed)") +
                  (shifted > 0 ? $", {shifted} cm further left to keep the district sign whole" : "") + (signClear ? "" : " (the sign still partly hidden)");
            rearPlace = $"{spot.x:0.00} m right, {spot.y:0.00} up, {spot.z:0.00} ahead of the eyes ({az:0} deg right, {el:0} up), {where}, " +
                        $"glass {GlassW * 100f:0} x {GlassH * 100f:0.#} cm, stem {stem * 100f:0} cm to {ceiling}";
            Plugin.L.LogInfo($"Mirrors: the rear-view mirror hangs {rearPlace}; {Clearances(m)}; a real mirror drawn from your head, looking out of the HOVA's back, every frame.");
        }

        /// <summary>The sign strip's two lowest corners, 2 cm lower for its frame.</summary>
        private static bool SignEdge(Vector3 eye, Quaternion eyeRot, out Vector3 a, out Vector3 b)
        {
            a = b = Vector3.zero;
            var strip = Strip();
            if (strip == null) return false;
            var t = strip.transform;
            Vector3 up = eyeRot * Vector3.up;
            var c = new[] { t.TransformPoint(new Vector3(-0.5f, -0.5f, 0f)), t.TransformPoint(new Vector3(0.5f, -0.5f, 0f)),
                            t.TransformPoint(new Vector3(0.5f, 0.5f, 0f)), t.TransformPoint(new Vector3(-0.5f, 0.5f, 0f)) };
            System.Array.Sort(c, (p, q) => Vector3.Dot(p, up).CompareTo(Vector3.Dot(q, up)));
            a = c[0] - up * 0.02f;
            b = c[1] - up * 0.02f;
            return true;
        }

        /// <summary>Mirror top less than 1° below the sign's lower edge somewhere along it.</summary>
        private static bool Covers(Vector3 eye, Quaternion eyeRot, Vector3 spot, Vector3 a, Vector3 b)
        {
            Vector3 m = eye + eyeRot * spot;
            Vector3 n = ((eye - m).normalized + eyeRot * Vector3.back).normalized;
            var rot = Quaternion.LookRotation(n, eyeRot * Vector3.up);
            float hw = GlassW * 0.5f + Bezel, hh = GlassH * 0.5f + Bezel;
            Angles(eye, eyeRot, m + rot * new Vector3(-hw, hh, 0f), out float az1, out float el1);
            Angles(eye, eyeRot, m + rot * new Vector3(hw, hh, 0f), out float az2, out float el2);
            Angles(eye, eyeRot, a, out float azA, out float elA);
            Angles(eye, eyeRot, b, out float azB, out float elB);
            float lo = Mathf.Min(azA, azB), hi = Mathf.Max(azA, azB);
            float mLo = Mathf.Min(az1, az2), mHi = Mathf.Max(az1, az2);
            if (mHi < lo || mLo > hi) return false;
            for (int i = 0; i <= 4; i++)
            {
                float az = Mathf.Lerp(Mathf.Max(lo, mLo), Mathf.Min(hi, mHi), i / 4f);
                float signEl = Mathf.Lerp(elA, elB, Mathf.Abs(azB - azA) > 1e-3f ? (az - azA) / (azB - azA) : 0f);
                float mirrorEl = Mathf.Lerp(el1, el2, Mathf.Abs(az2 - az1) > 1e-3f ? (az - az1) / (az2 - az1) : 0f);
                if (mirrorEl > signEl - 1f) return true;
            }
            return false;
        }

        private static void Angles(Vector3 eye, Quaternion eyeRot, Vector3 p, out float az, out float el)
        {
            Vector3 d = Quaternion.Inverse(eyeRot) * (p - eye);
            az = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            el = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        }

        /// <summary>Glass faces halfway between the head and straight back. Re-aimed on recenter, like a car mirror.</summary>
        private static void AimRear()
        {
            rearAimedSpace = CameraDriver.SpaceVersion;
            if (rearFrame == null || CameraDriver.CockpitCamera == null) return;
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out _, out var eyeRot);
            Vector3 m = rearFrame.TransformPoint(rearLocalPos);
            Vector3 head = CameraDriver.CockpitCamera.transform.position;
            Vector3 toHead = (head - m).normalized, back = eyeRot * Vector3.back, up = eyeRot * Vector3.up;
            Vector3 n = (toHead + back).normalized;
            if (n.sqrMagnitude < 0.5f) n = toHead;
            rearLocalRot = Quaternion.Inverse(rearFrame.rotation) * Quaternion.LookRotation(n, up);
        }

        private static void KeepRearLayer()
        {
            var cam = CameraDriver.CockpitCamera;
            int mask = cam.cullingMask, want = 28;
            if ((mask & (1 << want)) == 0)
                for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { want = i; break; }
            if (want == rearLayer) return;
            rearLayer = want;
            rearT.gameObject.layer = glassR.gameObject.layer = lightR.gameObject.layer = stemT.gameObject.layer = want;
        }

        private static void SizeRear(int width)
        {
            int height = Mathf.Max(16, Mathf.RoundToInt(width * GlassH / GlassW / 2f) * 2);
            if (rearRt != null && rearRt.width == width && rearRt.height == height) return;
            var old = rearRt;
            var format = gameRt != null ? gameRt.format : RenderTextureFormat.ARGB32;
            rearRt = new RenderTexture(width, height, 24, format, RenderTextureReadWrite.Default)
            {
                name = "CloudpunkVR_RearMirror",
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            rearRt.Create();
            ClearToBlack(rearRt);
            rearCam.targetTexture = rearRt;
            if (old != null) { old.Release(); UnityEngine.Object.Destroy(old); }
        }

        /// <summary>After the view is placed, before any camera draws: mirror with the set, camera at the head's reflection.</summary>
        private static void PlaceRear()
        {
            if (rearRoot == null || !rearRoot.activeSelf || rearFrame == null) return;
            Vector3 mc = rearFrame.TransformPoint(rearLocalPos);
            Quaternion rc = rearFrame.rotation * rearLocalRot;
            rearT.SetPositionAndRotation(mc, rc);
            Quaternion stemRot = rearFrame.rotation * stemLocalRot;
            stemT.SetPositionAndRotation(mc + rc * JointTop, stemRot);
            // Camus bumps into the frame
            CharmMount.SetObstacle(mc + rc * new Vector3(0f, 0f, (FrameFront - FrameBack - 0.012f) * 0.5f), rc,
                new Vector3(GlassW * 0.5f + Bezel, GlassH * 0.5f + Bezel, (FrameFront + FrameBack + 0.012f) * 0.5f));
            if (!rearCam.enabled) return;

            CameraDriver.CockpitToWorld(mc, rc, out var mw, out var rw);
            Vector3 n = rw * Vector3.forward, right = rw * Vector3.right, up = rw * Vector3.up;
            Vector3 e = CameraDriver.HeadWorldPosition;
            float dist = Vector3.Dot(e - mw, n);
            var car = Car();
            if (!(dist >= 0.05f)) // head at or behind the glass: keep the last view, carried with the HOVA
            {
                if (car != null && haveLastView) rearCam.transform.SetPositionAndRotation(car.TransformPoint(lastViewPos), car.rotation * lastViewRot);
                return;
            }
            Vector3 image = e - 2f * dist * n;
            Vector3 d = mw - image;
            float mx = Vector3.Dot(d, right), my = Vector3.Dot(d, up), near = Vector3.Dot(d, n);
            // pushed back along the line of sight to just behind the tail: nothing of the HOVA (windows, drops) in view
            rearShift = TailShift(car, image);
            Vector3 eyePos = image + CameraDriver.AnchorRotation * Vector3.back * rearShift;
            rearCam.transform.SetPositionAndRotation(eyePos, rw);
            if (car != null)
            {
                lastViewPos = car.InverseTransformPoint(eyePos);
                lastViewRot = Quaternion.Inverse(car.rotation) * rw;
                haveLastView = true;
            }
            float far = Mathf.Max(near + 1f, RearFar);
            rearCam.nearClipPlane = near;
            rearCam.farClipPlane = far;
            try
            {
                rearProjection = OffCenter(mx - GlassW * 0.5f, mx + GlassW * 0.5f, my - GlassH * 0.5f, my + GlassH * 0.5f, near, far);
                rearCam.projectionMatrix = rearProjection;
            }
            catch (Exception ex)
            {
                projectionBroken = true;
                rearCam.enabled = false;
                Plugin.L.LogWarning("Rear-view mirror off: its camera's projection cannot be set (" + ex.Message + ").");
            }
        }

        /// <summary>Cached until the car changes.</summary>
        private static Transform Car()
        {
            var g = Global.Instance;
            var pc = g != null ? g.playerCar : null;
            IntPtr p = pc != null ? pc.Pointer : IntPtr.Zero;
            if (p != carOf) { carOf = p; carT = pc != null ? pc.transform : null; }
            return carT;
        }

        /// <summary>Head reflection to the HOVA's tail along its length, plus 10 cm.</summary>
        private static float TailShift(Transform car, Vector3 image)
        {
            if (car == null) return 0f;
            if (car != tailOf) MeasureTail(car); // normally done at placement, under the boarding black
            return Mathf.Max(0f, car.InverseTransformPoint(image).z - tailZ + 0.1f);
        }

        /// <summary>The HOVA's rearmost opaque point (own frame). Once per HOVA and on part changes.</summary>
        private static void MeasureTail(Transform car)
        {
            tailOf = car;
            tailChildren = car.childCount;
            tailZ = -2.5f;
            bool any = false;
            int used = 0;
            var renderers = car.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.name.StartsWith("CloudpunkVR")) continue;
                var mat = r.sharedMaterial;
                if (mat == null || mat.renderQueue >= 2500) continue; // transparent: glows, light cones, glass
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var b = mesh.bounds;
                var t = r.transform;
                used++;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    float z = car.InverseTransformPoint(t.TransformPoint(corner)).z;
                    if (!any || z < tailZ) tailZ = z;
                    any = true;
                }
            }
            bool odd = !any || tailZ > -1f; // a HOVA is longer: something wasn't readable
            if (odd) tailZ = -2.2f;
            Plugin.L.LogInfo($"Mirrors: the rear-view mirror looks out from {-tailZ:0.00} m behind the HOVA's centre, its tail ({used} opaque mesh(es) measured{(odd ? ", too few: estimated" : "")}).");
        }

        /// <summary>Off-centre projection (OpenGL convention, as Camera.projectionMatrix takes it).</summary>
        private static Matrix4x4 OffCenter(float l, float r, float b, float t, float n, float f)
        {
            var m = new Matrix4x4();
            m.m00 = 2f * n / (r - l);
            m.m02 = (r + l) / (r - l);
            m.m11 = 2f * n / (t - b);
            m.m12 = (t + b) / (t - b);
            m.m22 = -(f + n) / (f - n);
            m.m23 = -2f * f * n / (f - n);
            m.m32 = -1f;
            return m;
        }

        /// <summary>Log readout: clearance to Camus at rest and swinging, gap seen from the eyes, sign visible.</summary>
        private static string Clearances(Vector3 m)
        {
            var sb = new StringBuilder();
            Quaternion rot = rearFrame.rotation * rearLocalRot;
            Vector3 half = new Vector3(GlassW * 0.5f + Bezel, GlassH * 0.5f + Bezel, 0.012f);
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eye, out var eyeRot);
            Vector3 up = eyeRot * Vector3.up;
            if (CharmPlaced(rearFrame))
            {
                Vector3 pivot = rearFrame.TransformPoint(CharmMount.PivotLocal);
                float reach = CharmMount.ChainLength + CharmHalf;
                Vector3 head = pivot - up * reach;
                sb.Append($"{(BoxDistance(head, m, rot, half) - CharmHalf) * 100f:0} cm from Camus's head at rest, {BoxDistance(pivot, m, rot, half) * 100f:0} cm from his chain's anchor; ");
                // swing toward the mirror until his head touches
                Vector3 toward = m - pivot;
                toward -= up * Vector3.Dot(toward, up);
                int touch = -1;
                if (toward.sqrMagnitude > 1e-6f)
                {
                    toward.Normalize();
                    for (int deg = 0; deg <= 90 && touch < 0; deg += 2)
                    {
                        float a = deg * Mathf.Deg2Rad;
                        Vector3 p = pivot + (-up * Mathf.Cos(a) + toward * Mathf.Sin(a)) * reach;
                        if (BoxDistance(p, m, rot, half) < CharmHalf) touch = deg;
                    }
                }
                sb.Append(touch < 0 ? "his swing never reaches it" : $"a swing of {touch} deg toward it touches it, and the frame stops him there");
                Angles(eye, eyeRot, head, out float azHead, out _);
                Angles(eye, eyeRot, m + rot * new Vector3(half.x, 0f, 0f), out float azEdge, out _);
                Angles(eye, eyeRot, m + rot * new Vector3(-half.x, 0f, 0f), out float azOther, out _);
                float gapDeg = azHead - Mathf.Max(azEdge, azOther) - Mathf.Atan2(CharmHalf, Vector3.Distance(head, eye)) * Mathf.Rad2Deg;
                sb.Append($"; seen from the eyes, {gapDeg:0.0} deg between its edge and Camus's head ({(gapDeg > 0f ? "side by side" : "OVERLAPPING")})");
            }
            else sb.Append("Camus not placed");
            if (SignEdge(eye, eyeRot, out var a1, out var b1))
                sb.Append(Covers(eye, eyeRot, rearSpotUsed, a1, b1) ? "; it hides part of the district sign" : "; the district sign shows whole above it");
            return sb.ToString();
        }

        private static float BoxDistance(Vector3 p, Vector3 center, Quaternion rot, Vector3 half)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - center);
            Vector3 c = new Vector3(Mathf.Clamp(l.x, -half.x, half.x), Mathf.Clamp(l.y, -half.y, half.y), Mathf.Clamp(l.z, -0.03f, half.z));
            return (l - c).magnitude;
        }

        private static void DescribeRear(StringBuilder sb, float eyePxPerDeg)
        {
            if (!VRConfig.RearMirror.Value) { sb.Append("\n   rear mirror: off (RearMirror false)"); return; }
            if (rearRoot == null || !rearPlaced) { sb.Append("\n   rear mirror: not placed yet (cockpit view needed)"); return; }
            var st = rearStats;
            sb.Append($"\n   rear mirror: {rearPlace}; camera {(rearCam.enabled ? "on" : "off")}{(projectionBroken ? " (projection refused)" : "")}");
            if (rearRt != null)
                sb.Append($", {rearRt.width}x{rearRt.height} {rearRt.format} mipmapped, {st.PerSecond:0.0} draws/s, {st.MsPerRender:0.00} ms per draw (worst {st.Worst:0.00}), {st.PerFrame:0.00} ms per frame, " +
                          $"shadows {(skipShadows ? "off" : "on")}, look {LookName(rearLook)}, frame drawn with '{frameShader}', mask 0x{rearCam.cullingMask:X8}, seen from {rearShift:0.00} m further back, behind the HOVA's tail, the HOVA's body left out while it draws");
            var eye = CameraDriver.CockpitCamera;
            if (eye != null && glassR != null && rearCam.enabled)
            {
                Vector3 c = glassR.bounds.center, half = glassR.transform.right * (GlassW * 0.5f), e = eye.transform.position;
                float deg = Vector3.Angle(c - half - e, c + half - e);
                sb.Append($"; {Vector3.Distance(c, e):0.00} m from the eye, {deg:0.0} deg wide in view ({(rearRt != null && deg > 0.01f && eyePxPerDeg > 0f ? rearRt.width / deg / eyePxPerDeg : 0f):0.00} of the eye's pixels per degree); " +
                          $"reflects about {FrustumWidth():0} deg across behind; {Clearances(c)}");
            }
        }

        /// <summary>Horizontal angle the mirror reflects now.</summary>
        private static float FrustumWidth()
        {
            var p = rearCam.projectionMatrix;
            if (p.m00 <= 0f) return 0f;
            float right = (1f + p.m02) / p.m00, left = (1f - p.m02) / p.m00;
            return (Mathf.Atan(right) + Mathf.Atan(left)) * Mathf.Rad2Deg;
        }
    }
}
