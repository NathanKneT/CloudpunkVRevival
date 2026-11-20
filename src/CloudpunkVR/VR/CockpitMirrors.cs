using System;
using System.IO;
using System.Text;
using Bearroll;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// The two dashboard rear-view screens. The game shows one blurry 64x32 left-rear picture on both, banking with
    /// the HOVA. We draw each screen with its own sharp, level camera. Rear-view mirror: CockpitMirrors.Rear.cs.
    /// </summary>
    internal static partial class CockpitMirrors
    {
        private const int LowWidth = 256, HighWidth = 512;
        private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");

        /// <summary>Dev "mirrors game": the game's own mirror for this run.</summary>
        public static bool GameForTest;

        private sealed class Stats
        {
            public long Started;
            public double SumMs;
            public float WorstMs;
            public int Renders;
            // last full second
            public float PerSecond, MsPerRender, Worst, PerFrame;
        }

        private sealed class Side
        {
            public readonly string Name;
            public readonly bool Right;
            public Renderer Screen;
            public Material Original, Own;
            public PostProcessLayer Look;
            public Color Emission = Color.white; // material's own emission colour
            public Camera Cam;
            public IntPtr CamPtr;
            public RenderTexture Rt;
            public Vector3 LocalPos; // in the HOVA world part's frame
            public Quaternion LocalRot = Quaternion.identity;
            public float Aspect = 2f, WidthM, HeightM;
            public string Mapping = "unknown";
            public readonly Stats Stats = new Stats();
            public Side(string name, bool right) { Name = name; Right = right; }
        }

        private static readonly Side left = new Side("left", false), right = new Side("right", true);
        private static readonly Stats gameStats = new Stats();
        private static Camera gameCam;
        private static IntPtr gameCamPtr;
        private static RenderTexture gameRt;
        private static bool gameCamWasEnabled = true;
        private static Transform frame, interior;
        private static GameObject root;
        private static bool found, taken, warned, loggedGame;
        private static float nextSearch;
        private static int searches;

        private static MirrorQuality applied;
        private static bool appliedValid;
        private static float appliedAngle = -1f;

        // Low mirrors: shadow distance 0 for that camera only
        private static bool skipShadows, shadowsChanged;
        private static float savedShadowDistance;

        private static float rollStart, nextRoll;
        private static int rollFrame;

        public static bool Active => taken;

        /// <summary>Once per frame before rendering.</summary>
        public static void Update()
        {
            RestoreShadows();
            Roll();
            var g = Global.Instance;
            var vi = g != null ? g.vehicleInterior : null;
            bool cockpit = vi != null && CameraDriver.Mode == ViewMode.Cockpit && Global.IsInVehicleCockpit;
            skipShadows = VRConfig.Mirrors.Value == MirrorQuality.Low || VRConfig.Mirrors.Value == MirrorQuality.Game;
            if (Time.unscaledTime >= nextLookPass) { nextLookPass = Time.unscaledTime + 1f; FollowMainLook(); }
            wantLeft = wantRight = wantRear = false;
            UpdateScreens(vi, cockpit); // first: the rear camera copies the game's mirror camera
            UpdateRear(vi, cockpit);
            Schedule();
            // keep things behind drawn only while one of our mirrors drew in the last 0.5 s
            KeepBehindDrawn(g, cockpit && Time.unscaledTime - lastDrew < 0.5f);
        }

        /// <summary>MirrorQuality Game or dev "mirrors game".</summary>
        private static bool GameOwn => GameForTest || VRConfig.Mirrors.Value == MirrorQuality.Game;

        private static float lastDrew = -10f, nextLookPass;

        // ---- What's behind the HOVA, kept drawn for the mirrors.

        private static readonly System.Collections.Generic.Dictionary<IntPtr, (GPUInstancer.GPUInstancerManager m, float min)> instancers =
            new System.Collections.Generic.Dictionary<IntPtr, (GPUInstancer.GPUInstancerManager, float)>();
        private static bool instancersRaised, instancersWarned;
        private static float nextInstancerPass;
        private static float instancersRadius = -1f;

        /// <summary>GPU Instancer managers cull for the main camera only: keep CullRadius unculled while the mirrors are on.</summary>
        private static void KeepInstancesDrawn(bool want)
        {
            try
            {
                if (!want)
                {
                    if (!instancersRaised) return;
                    foreach (var kv in instancers)
                        if (kv.Value.m != null) kv.Value.m.minCullingDistance = kv.Value.min;
                    instancers.Clear();
                    instancersRaised = false;
                    return;
                }
                if (instancersRaised && Time.unscaledTime < nextInstancerPass && instancersRadius == CullRadius) return;
                nextInstancerPass = Time.unscaledTime + 1f;
                var list = GPUInstancer.GPUInstancerManager.activeManagerList;
                int n = list != null ? list.Count : 0, raised = 0;
                for (int i = 0; i < n; i++)
                {
                    var m = list[i];
                    if (m == null) continue;
                    if (!instancers.TryGetValue(m.Pointer, out var e)) instancers[m.Pointer] = e = (m, m.minCullingDistance);
                    float r = Mathf.Max(e.min, CullRadius);
                    if (m.minCullingDistance != r) { m.minCullingDistance = r; raised++; }
                }
                if (raised > 0)
                    Plugin.L.LogInfo($"Mirrors: the game's instanced buildings and props ({n} manager(s)) left drawn within {CullRadius:0} m round the main camera while they are on (frustum culling {(n > 0 && list[0] != null && list[0].isFrustumCulling ? "on" : "off")} beyond).");
                instancersRaised = true;
                instancersRadius = CullRadius;
            }
            catch (Exception e)
            {
                if (!instancersWarned) { instancersWarned = true; Plugin.L.LogWarning("Mirrors: the instancing managers' culling not set: " + e.Message); }
            }
        }

        private static string Instancers()
        {
            try
            {
                var list = GPUInstancer.GPUInstancerManager.activeManagerList;
                int n = list != null ? list.Count : 0;
                if (n == 0) return "no instancing manager here";
                var sb = new StringBuilder();
                for (int i = 0; i < n && i < 4; i++)
                {
                    var m = list[i];
                    if (m == null) continue;
                    sb.Append($"{(sb.Length > 0 ? ", " : "")}'{m.name}' never culled within {m.minCullingDistance:0} m, frustum {m.isFrustumCulling}, occlusion {m.isOcclusionCulling}, {(m.prototypeList != null ? m.prototypeList.Count : 0)} prototype(s)");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "instancing managers not readable: " + e.Message; }
        }

        /// <summary>Dev "mirrors cull": radius kept drawn round the HOVA, m (0: game's own).</summary>
        public static float CullRadius = 25f;
        private static GDOC culling, cullingFound;
        private static IntPtr cullingOf;
        private static float cullingBefore;
        private static bool cullingRaised, cullingWarned;

        /// <summary>
        /// The game culls for the main camera only (GDOC beyond 5 m, GPU Instancer by its view), so the mirrors saw fog
        /// behind the HOVA. Both keep CullRadius drawn while the mirrors are on.
        /// </summary>
        private static void KeepBehindDrawn(Global g, bool want)
        {
            KeepInstancesDrawn(want && CullRadius > 0f);
            try
            {
                var main = g != null ? g.mainCamera : null;
                IntPtr mainPtr = main != null ? main.Pointer : IntPtr.Zero;
                if (mainPtr != cullingOf)
                {
                    cullingOf = mainPtr;
                    cullingFound = main != null ? main.GetComponent<GDOC>() : null;
                }
                var gdoc = cullingFound;
                if (gdoc != culling)
                {
                    if (cullingRaised && culling != null) culling.minimumDistance = cullingBefore;
                    cullingRaised = false;
                    culling = gdoc;
                }
                if (culling == null) return;
                if (want && CullRadius > 0f)
                {
                    if (!cullingRaised)
                    {
                        cullingBefore = culling.minimumDistance;
                        cullingRaised = true;
                        Plugin.L.LogInfo($"Mirrors: the game's occlusion culling leaves everything within {CullRadius:0} m drawn while they are on (it was {cullingBefore:0} m): what is behind the HOVA shows in them.");
                    }
                    float radius = Mathf.Max(cullingBefore, CullRadius);
                    if (culling.minimumDistance != radius) culling.minimumDistance = radius;
                }
                else if (cullingRaised)
                {
                    culling.minimumDistance = cullingBefore;
                    cullingRaised = false;
                }
            }
            catch (Exception e)
            {
                if (!cullingWarned) { cullingWarned = true; Plugin.L.LogWarning("Mirrors: the occlusion culling's distance not set: " + e.Message); }
            }
        }

        private static void UpdateScreens(VehicleInterior vi, bool cockpit)
        {
            if (found && (gameCam == null || left.Screen == null || right.Screen == null || interior == null || (vi != null && vi.interiorLocal != interior)))
                Lost();
            if (!found)
            {
                if (!cockpit || Time.unscaledTime < nextSearch) return;
                // a few quick tries, then rarely: a search walks the HOVA's children
                nextSearch = Time.unscaledTime + (searches++ < 5 ? 2f : 30f);
                if (!Find(vi)) return;
            }

            if (GameOwn)
            {
                Release();
                if (!loggedGame) { loggedGame = true; Plugin.L.LogInfo($"Mirrors: the game's own mirror ({(GameForTest ? "mirrors game" : "MirrorQuality Game")}): one 64 x 32 picture of the left rear on both screens."); }
                return;
            }
            loggedGame = false;
            if (!Take()) return;
            if (gameCam.enabled) gameCam.enabled = false; // the game never turns it back on, just in case

            var q = VRConfig.Mirrors.Value;
            float angle = Mathf.Clamp(VRConfig.MirrorAngle.Value, 30f, 110f);
            if (!appliedValid || q != applied || Mathf.Abs(angle - appliedAngle) > 0.01f) Apply(q, angle);

            bool draw = cockpit && q != MirrorQuality.Off && !TransitionGuard.Active;
            wantLeft = draw && InView(left.Screen);
            wantRight = draw && InView(right.Screen);
        }

        // may draw this frame, before Schedule
        private static bool wantLeft, wantRight, wantRear;

        /// <summary>
        /// High: every mirror in view. Low: one mirror camera per frame, about the game's cost (rear every other frame,
        /// screens share the rest). Out-of-view mirrors pass their turn.
        /// </summary>
        private static void Schedule()
        {
            bool l = wantLeft, r = wantRight, b = wantRear;
            if (VRConfig.Mirrors.Value == MirrorQuality.High) { Enable(left, l); Enable(right, r); EnableRear(b); return; }
            int slot = Time.frameCount & 3; // rear, left, rear, right
            bool pickRear = false, pickLeft = false, pickRight = false;
            for (int i = 0; i < 4 && !(pickRear || pickLeft || pickRight); i++, slot = (slot + 1) & 3)
            {
                if ((slot & 1) == 0) pickRear = b;
                else if (slot == 1) pickLeft = l;
                else pickRight = r;
            }
            Enable(left, pickLeft);
            Enable(right, pickRight);
            EnableRear(pickRear);
        }

        /// <summary>Within 80° of the gaze (last frame), else the last picture stays on the glass.</summary>
        private static bool InView(Renderer r)
        {
            var eye = CameraDriver.CockpitCamera;
            if (eye == null || r == null) return true;
            var t = eye.transform;
            return Vector3.Angle(t.forward, r.bounds.center - t.position) < ViewCone;
        }

        private const float ViewCone = 80f;

        private static void Enable(Side s, bool on)
        {
            if (s.Cam != null && s.Cam.enabled != on) s.Cam.enabled = on;
        }

        /// <summary>After the view is placed, before any camera draws.</summary>
        public static void OnBeforeRender()
        {
            // each on its own: a failure must not stop the hands, wheel or UI
            if (!rearBroken)
            {
                try { PlaceRear(); }
                catch (Exception e) { BreakRear(e); }
            }
            if (!taken || frame == null) return;
            try
            {
                Quaternion basis = VRConfig.LevelHorizonInCockpit.Value ? Quaternion.Euler(0f, CameraDriver.Yaw(frame.rotation), 0f) : frame.rotation;
                Place(left, basis);
                Place(right, basis);
            }
            catch (Exception e)
            {
                if (!placeWarned) { placeWarned = true; Plugin.L.LogWarning("Mirrors: the screens' cameras not placed: " + e.Message); }
                Enable(left, false);
                Enable(right, false);
            }
        }

        private static bool placeWarned;

        private static void Place(Side s, Quaternion basis)
        {
            if (s.Cam == null || !s.Cam.enabled) return;
            s.Cam.transform.SetPositionAndRotation(frame.TransformPoint(s.LocalPos), basis * s.LocalRot);
        }

        // ---- Draw timing from Unity's camera callbacks.

        public static void OnPreCull(Camera cam)
        {
            if (!found && rearPtr == IntPtr.Zero) return;
            IntPtr p = cam.Pointer;
            // HOVA body back before any other camera, in case the post-render missed it. Runs before HovaBody's pre-cull.
            if (rearHid && p != rearPtr) { rearHid = false; HovaBody.Restore(); }
            if (fogWritten && p != rearPtr) RestoreFog();
            if (p == rearPtr)
            {
                Begin(rearStats, true);
                rearHid = HovaBody.HideForMirror();
                if (rearProjection.m32 != 0f) cam.projectionMatrix = rearProjection; // again, in case something reset it
            }
            else if (p == left.CamPtr) Begin(left.Stats, true);
            else if (p == right.CamPtr) Begin(right.Stats, true);
            else if (p == gameCamPtr) Begin(gameStats, false);
            else if (shadowsChanged) RestoreShadows();
        }

        // ---- City fog for the rear mirror's frustum.

        private static readonly int FogX = Shader.PropertyToID("_CameraVectorX"), FogY = Shader.PropertyToID("_CameraVectorY"), FogZ = Shader.PropertyToID("_CameraVectorZ");
        private static Vector3 savedFogX, savedFogY, savedFogZ;
        private static bool fogWritten, fogLogged;

        /// <summary>
        /// GlobalFog lays itself along 3 vectors computed for a centred frustum. The rear camera's is off-centre and
        /// narrower, so rewrite them for it and restore after.
        /// </summary>
        public static void OnPreRender(Camera cam)
        {
            if (rearPtr == IntPtr.Zero || cam.Pointer != rearPtr || rearProjection.m32 == 0f) return;
            try
            {
                var t = cam.transform;
                Vector3 r = t.right, u = t.up, f = t.forward;
                Vector3 x = Shader.GetGlobalVector(FogX), y = Shader.GetGlobalVector(FogY), z = Shader.GetGlobalVector(FogZ);
                savedFogX = x; savedFogY = y; savedFogZ = z;
                // game's layout for this camera: signed width, height, corner depth
                float width = Vector3.Dot(x, r), height = Vector3.Dot(y, u), d = Vector3.Dot(z, f);
                if (!(d > 0f) || Mathf.Abs(width) < 1e-4f || Mathf.Abs(height) < 1e-4f)
                {
                    width = height = 1f; // none written for this camera: far plane, game's axes
                    d = cam.farClipPlane;
                }
                var p = rearProjection;
                float left = d * (p.m02 - 1f) / p.m00, right = d * (p.m02 + 1f) / p.m00;
                float bottom = d * (p.m12 - 1f) / p.m11, top = d * (p.m12 + 1f) / p.m11;
                Shader.SetGlobalVector(FogX, r * (width >= 0f ? right - left : left - right));
                Shader.SetGlobalVector(FogY, u * (height >= 0f ? top - bottom : bottom - top));
                Shader.SetGlobalVector(FogZ, f * d + r * (width >= 0f ? left : right) + u * (height >= 0f ? bottom : top));
                fogWritten = true;
                if (!fogLogged)
                {
                    fogLogged = true;
                    Plugin.L.LogInfo($"Mirrors: the city's fog laid along the rear mirror's own frustum ({(right - left) / d:0.00} by {(top - bottom) / d:0.00} at depth 1, the game's centred one {Mathf.Abs(width) / Mathf.Max(d, 1e-4f):0.00} by {Mathf.Abs(height) / Mathf.Max(d, 1e-4f):0.00}).");
                }
            }
            catch (Exception e)
            {
                if (!fogLogged) { fogLogged = true; Plugin.L.LogWarning("Mirrors: the fog not laid along the rear mirror's frustum: " + e.Message); }
            }
        }

        private static void RestoreFog()
        {
            if (!fogWritten) return;
            fogWritten = false;
            Shader.SetGlobalVector(FogX, savedFogX);
            Shader.SetGlobalVector(FogY, savedFogY);
            Shader.SetGlobalVector(FogZ, savedFogZ);
        }

        public static void OnPostRender(Camera cam)
        {
            if (!found && rearPtr == IntPtr.Zero) return;
            IntPtr p = cam.Pointer;
            if (p == rearPtr)
            {
                End(rearStats);
                RestoreFog();
                if (rearHid) { rearHid = false; HovaBody.Restore(); }
            }
            else if (p == left.CamPtr) End(left.Stats);
            else if (p == right.CamPtr) End(right.Stats);
            else if (p == gameCamPtr) End(gameStats);
            else return;
            RestoreShadows();
        }

        private static void Begin(Stats s, bool ours)
        {
            if (ours && !shadowsChanged)
            {
                // Low: no shadows. High: within 30 m only (else every mirror renders the full cascades)
                savedShadowDistance = QualitySettings.shadowDistance;
                QualitySettings.shadowDistance = skipShadows ? 0f : Mathf.Min(savedShadowDistance, MirrorShadowDistance);
                shadowsChanged = true;
            }
            if (ours) lastDrew = Time.unscaledTime;
            s.Started = Stopwatch.GetTimestamp();
        }

        private const float MirrorShadowDistance = 30f;

        private static void End(Stats s)
        {
            if (s.Started == 0) return;
            float ms = (float)((Stopwatch.GetTimestamp() - s.Started) * 1000.0 / Stopwatch.Frequency);
            s.Started = 0;
            s.SumMs += ms;
            s.Renders++;
            if (ms > s.WorstMs) s.WorstMs = ms;
        }

        private static void RestoreShadows()
        {
            if (!shadowsChanged) return;
            shadowsChanged = false;
            QualitySettings.shadowDistance = savedShadowDistance;
        }

        /// <summary>Once a second: draws and time per camera.</summary>
        private static void Roll()
        {
            float now = Time.unscaledTime;
            if (now < nextRoll) return;
            float span = Mathf.Max(0.1f, now - rollStart);
            int frames = Mathf.Max(1, Time.frameCount - rollFrame);
            Publish(left.Stats, span, frames);
            Publish(right.Stats, span, frames);
            Publish(gameStats, span, frames);
            Publish(rearStats, span, frames);
            rollStart = now;
            rollFrame = Time.frameCount;
            nextRoll = now + 1f;
        }

        private static void Publish(Stats s, float span, int frames)
        {
            s.PerSecond = s.Renders / span;
            s.MsPerRender = s.Renders > 0 ? (float)(s.SumMs / s.Renders) : 0f;
            s.PerFrame = (float)(s.SumMs / frames);
            s.Worst = s.WorstMs;
            s.SumMs = 0;
            s.Renders = 0;
            s.WorstMs = 0f;
        }

        // ---- Finding the game's mirror, taking it over, giving it back.

        /// <summary>The two screens (render texture as emission map) and the camera drawing it.</summary>
        private static bool Find(VehicleInterior vi)
        {
            try
            {
                interior = vi.interiorLocal;
                var car = Global.Instance.playerCar != null ? Global.Instance.playerCar.transform : null;
                if (interior == null || car == null) return false;
                var holder = interior.Find("Mirror");
                var renderers = (holder != null ? holder : interior).GetComponentsInChildren<Renderer>(true);
                Renderer a = null, b = null;
                RenderTexture rt = null;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    var m = r != null ? r.sharedMaterial : null;
                    if (m == null || !m.HasProperty(EmissionMapId)) continue;
                    var tex = m.GetTexture(EmissionMapId);
                    var t = tex != null ? tex.TryCast<RenderTexture>() : null;
                    if (t == null || (rt != null && t.Pointer != rt.Pointer)) continue;
                    rt = t;
                    if (a == null) a = r; else if (b == null) b = r;
                }
                if (a == null || b == null) return Fail("the two screens showing the mirror's picture were not found in the cockpit");

                Camera cam = null;
                var cams = car.GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cams.Length && cam == null; i++)
                    if (cams[i] != null && cams[i].targetTexture != null && cams[i].targetTexture.Pointer == rt.Pointer) cam = cams[i];
                if (cam == null) return Fail($"no camera on the HOVA draws '{rt.name}'");

                bool aLeft = interior.InverseTransformPoint(a.bounds.center).x < interior.InverseTransformPoint(b.bounds.center).x;
                left.Screen = aLeft ? a : b;
                right.Screen = aLeft ? b : a;
                gameCam = cam;
                gameCamPtr = cam.Pointer;
                gameRt = rt;
                frame = WorldPart(cam.transform, car);

                // game camera in the world part's frame (centred on x = 0): the right camera mirrors it
                left.LocalPos = frame.InverseTransformPoint(cam.transform.position);
                left.LocalRot = Quaternion.Inverse(frame.rotation) * cam.transform.rotation;
                right.LocalPos = new Vector3(-left.LocalPos.x, left.LocalPos.y, left.LocalPos.z);
                var q = left.LocalRot;
                right.LocalRot = new Quaternion(q.x, -q.y, -q.z, q.w);
                Measure(left);
                Measure(right);
                found = true;
                warned = false;
                searches = 0;
                Plugin.L.LogInfo($"Mirrors: the game's mirror camera '{Path(cam.transform)}' draws {rt.width}x{rt.height} for both screens '{left.Screen.name}' and '{right.Screen.name}' " +
                                 $"({left.WidthM * 100f:0.0} x {left.HeightM * 100f:0.0} cm); screen mapping: {left.Mapping}.");
                return true;
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
        }

        private static bool Fail(string why)
        {
            found = false;
            if (!warned) { warned = true; Plugin.L.LogWarning("Mirrors: " + why + "; the game's own mirror is left as it is."); }
            return false;
        }

        /// <summary>_Vehicle_Interior_Global above the camera, else its grandparent, else the HOVA.</summary>
        private static Transform WorldPart(Transform cam, Transform car)
        {
            for (var t = cam.parent; t != null && t != car; t = t.parent)
                if (t.name == "_Vehicle_Interior_Global") return t;
            var p = cam.parent;
            return p != null && p.parent != null ? p.parent : p != null ? p : car;
        }

        /// <summary>Screen size and aspect from its mesh.</summary>
        private static void Measure(Side s)
        {
            var mf = s.Screen.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            Vector3 size = mesh != null ? mesh.bounds.size : new Vector3(10f, 0f, 5f);
            Vector3 scale = s.Screen.transform.lossyScale;
            float w = Mathf.Abs(size.x * scale.x);
            // a plane is flat in y, a quad in z
            float h = size.y < size.z ? Mathf.Abs(size.z * scale.z) : Mathf.Abs(size.y * scale.y);
            s.WidthM = w;
            s.HeightM = h;
            s.Aspect = h > 1e-4f ? Mathf.Clamp(w / h, 0.5f, 4f) : 2f;
            s.Mapping = Mapping(s.Screen.transform, mesh);
        }

        /// <summary>Which way u and v run as seen from the seat.</summary>
        private static string Mapping(Transform t, Mesh mesh)
        {
            try
            {
                if (mesh == null || interior == null) return "unknown";
                var uv = mesh.uv;
                var v = mesh.vertices;
                if (uv == null || v == null || uv.Length != v.Length || uv.Length < 3) return "unknown";
                int uMin = 0, uMax = 0, vMin = 0, vMax = 0;
                for (int i = 1; i < uv.Length; i++)
                {
                    if (uv[i].x < uv[uMin].x) uMin = i;
                    if (uv[i].x > uv[uMax].x) uMax = i;
                    if (uv[i].y < uv[vMin].y) vMin = i;
                    if (uv[i].y > uv[vMax].y) vMax = i;
                }
                Vector3 du = interior.InverseTransformDirection(t.TransformPoint(v[uMax]) - t.TransformPoint(v[uMin]));
                Vector3 dv = interior.InverseTransformDirection(t.TransformPoint(v[vMax]) - t.TransformPoint(v[vMin]));
                return $"u to the {(du.x >= 0f ? "right" : "left")}, v {(dv.y >= 0f ? "up" : "down")}";
            }
            catch (Exception) { return "unknown (mesh not readable)"; }
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        /// <summary>Game camera off, each screen on its own material copy.</summary>
        private static bool Take()
        {
            if (taken) return true;
            try
            {
                if (root == null)
                {
                    root = new GameObject("CloudpunkVR_Mirrors");
                    UnityEngine.Object.DontDestroyOnLoad(root);
                }
                MakeCamera(left);
                MakeCamera(right);
                Swap(left);
                Swap(right);
                gameCamWasEnabled = gameCam.enabled;
                gameCam.enabled = false;
                taken = true;
                appliedValid = false;
                return true;
            }
            catch (Exception e)
            {
                Release();
                return Fail("not taken over: " + e.Message);
            }
        }

        private static void MakeCamera(Side s)
        {
            if (s.Cam != null) return;
            var go = new GameObject(s.Right ? "CloudpunkVR_MirrorRight" : "CloudpunkVR_MirrorLeft");
            go.SetActive(false); // post-processing gets its resources before it wakes
            go.transform.SetParent(root.transform, false);
            var cam = go.AddComponent<Camera>();
            try { cam.CopyFrom(gameCam); }
            catch (Exception)
            {
                cam.clearFlags = gameCam.clearFlags;
                cam.backgroundColor = Color.black;
                cam.cullingMask = gameCam.cullingMask;
                cam.nearClipPlane = gameCam.nearClipPlane;
                cam.farClipPlane = gameCam.farClipPlane;
                cam.allowHDR = gameCam.allowHDR;
                cam.renderingPath = gameCam.renderingPath;
            }
            cam.targetTexture = null;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.allowMSAA = false;
            cam.depth = gameCam.depth;
            cam.enabled = false;
            CullSmallDetail(cam);
            s.Look = AddGameLook(go);
            go.SetActive(true);
            s.Cam = cam;
            s.CamPtr = cam.Pointer;
        }

        // ---- The game's post-processing on the mirror cameras.

        /// <summary>Dev "mirrors grade game", experimental. Off: raw picture, darkened. The game's stack drew the sky white.</summary>
        public static bool GameLook = false;
        private static bool lookWarned;
        private static readonly Color DarkHaze = new Color(0.035f, 0.035f, 0.045f, 1f);
        private static readonly Color NightSky = new Color(0.022f, 0.02f, 0.03f, 1f);
        private static readonly Color PlainTint = new Color(0.55f, 0.53f, 0.6f, 1f);

        /// <summary>
        /// PostProcessLayer like the main camera's (object still inactive). In VR the cockpit draws after the main
        /// post-processing, so the mirrors get none otherwise.
        /// </summary>
        private static PostProcessLayer AddGameLook(GameObject go)
        {
            try
            {
                var main = Global.Instance != null ? Global.Instance.mainPostProcessLayer : null;
                if (main == null || main.m_Resources == null) { WarnLook("the main camera has no post-processing layer"); return null; }
                var layer = go.AddComponent<PostProcessLayer>();
                layer.m_Resources = main.m_Resources;
                layer.volumeLayer = main.volumeLayer;
                layer.volumeTrigger = go.transform;
                layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
                layer.stopNaNPropagation = true;
                return layer;
            }
            catch (Exception e) { WarnLook(e.Message); return null; }
        }

        private static void WarnLook(string why)
        {
            if (lookWarned) return;
            lookWarned = true;
            Plugin.L.LogWarning("Mirrors: the game's look not on the mirrors (" + why + "): their pictures darkened instead.");
        }

        /// <summary>Game look if on, else raw over dark haze, dimmed through the glass emission.</summary>
        private static void ApplyLook(Camera cam, PostProcessLayer layer, Material glass, Color own)
        {
            bool game = GameLook && layer != null && MainLookOn;
            if (layer != null && layer.enabled != game) layer.enabled = game;
            // the skybox alone is flat grey that the grading blows to white
            if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = game ? NightSky : DarkHaze;
            var tint = game ? GlassTint : PlainTint;
            if (glass != null) glass.SetColor("_EmissionColor", new Color(tint.r * own.r, tint.g * own.g, tint.b * own.b, 1f));
        }

        /// <summary>Main camera post-processing on (checked once a second).</summary>
        private static bool MainLookOn
        {
            get
            {
                if (Time.unscaledTime >= nextMainLookCheck)
                {
                    nextMainLookCheck = Time.unscaledTime + 1f;
                    var main = Global.Instance != null ? Global.Instance.mainPostProcessLayer : null;
                    mainLookOn = main == null || main.enabled;
                }
                return mainLookOn;
            }
        }

        private static bool mainLookOn = true;
        private static float nextMainLookCheck;

        private static void FollowMainLook()
        {
            bool on = MainLookOn;
            if (on == appliedMainLook && !(GameLook && on && taken && (left.Look == null || right.Look == null))) return;
            appliedMainLook = on;
            for (int i = 0; i < 2; i++)
            {
                var s = i == 0 ? left : right;
                if (s.Cam == null) continue;
                if (s.Look == null && GameLook && on) s.Look = AddGameLookLate(s.Cam.gameObject);
                if (taken && s.Own != null && appliedValid && applied != MirrorQuality.Off) ApplyLook(s.Cam, s.Look, s.Own, s.Emission);
            }
        }

        private static bool appliedMainLook = true;

        /// <summary>Adds the layer to an awake camera: asleep while it gets its resources.</summary>
        private static PostProcessLayer AddGameLookLate(GameObject go)
        {
            var main = Global.Instance != null ? Global.Instance.mainPostProcessLayer : null;
            if (main == null || main.m_Resources == null) return null;
            go.SetActive(false);
            var layer = AddGameLook(go);
            go.SetActive(true);
            return layer;
        }

        private static readonly Color GlassTint = new Color(0.92f, 0.92f, 0.94f, 1f);

        /// <summary>Starts black, not with leftover GPU memory.</summary>
        private static void ClearToBlack(RenderTexture rt)
        {
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = prev;
            }
            catch (Exception) { }
        }

        /// <summary>SmallDetail layer culled beyond 30 m: specks at mirror size.</summary>
        private static void CullSmallDetail(Camera cam)
        {
            try
            {
                int layer = LayerMask.NameToLayer("SmallDetail");
                if (layer < 0) return;
                var d = new float[32];
                d[layer] = 30f;
                cam.layerCullDistances = d;
            }
            catch (Exception e) { Plugin.L.LogInfo("Mirrors: small details kept at every distance (" + e.Message + ")."); }
        }

        private static void Swap(Side s)
        {
            if (s.Original == null)
            {
                s.Original = s.Screen.sharedMaterial;
                try { s.Emission = s.Original.HasProperty("_EmissionColor") ? s.Original.GetColor("_EmissionColor") : Color.white; }
                catch (Exception) { s.Emission = Color.white; }
            }
            if (s.Own == null)
                s.Own = new Material(s.Original) { name = "CloudpunkVR_Mirror" + (s.Right ? "Right" : "Left"), hideFlags = HideFlags.DontUnloadUnusedAsset };
            s.Screen.sharedMaterial = s.Own;
        }

        /// <summary>Gives the game back its camera and screen materials.</summary>
        private static void Release()
        {
            Enable(left, false);
            Enable(right, false);
            RestoreShadows();
            if (!taken) return;
            taken = false;
            appliedValid = false;
            Give(left);
            Give(right);
            if (gameCam != null) gameCam.enabled = gameCamWasEnabled;
        }

        private static void Give(Side s)
        {
            if (s.Screen != null && s.Original != null) s.Screen.sharedMaterial = s.Original;
        }

        /// <summary>Screens or camera gone (another HOVA, unload): release and search again.</summary>
        private static void Lost()
        {
            try { Release(); } catch (Exception) { }
            found = false;
            taken = false;
            gameCam = null;
            gameCamPtr = IntPtr.Zero;
            gameRt = null;
            for (int i = 0; i < 2; i++)
            {
                var s = i == 0 ? left : right;
                s.Screen = null;
                s.Original = null;
                if (s.Own != null) UnityEngine.Object.Destroy(s.Own);
                s.Own = null;
            }
            Plugin.L.LogInfo("Mirrors: the HOVA's screens or mirror camera changed; searching them again.");
        }

        private static void Apply(MirrorQuality q, float angle)
        {
            applied = q;
            appliedAngle = angle;
            appliedValid = true;
            int width = q == MirrorQuality.High ? HighWidth : LowWidth;
            Configure(left, q, width, angle);
            Configure(right, q, width, angle);
            if (q == MirrorQuality.Off)
            {
                Plugin.L.LogInfo("Mirrors: off (MirrorQuality Off): both rear-view screens dark, the game's mirror camera off, nothing drawn for them.");
                return;
            }
            Plugin.L.LogInfo($"Mirrors: {q}: each rear-view screen shows its own side, {left.Rt.width}x{left.Rt.height} (mipmapped), {angle:0} degrees across, " +
                             $"level with the horizon {(VRConfig.LevelHorizonInCockpit.Value ? "(as the view)" : "off (LevelHorizonInCockpit off: they bank with the HOVA)")}, " +
                             (q == MirrorQuality.High ? "both every frame, with shadows within 30 m" : "one mirror drawn per frame in all, the rear-view one every second frame, without shadows") +
                             $"; the game's {(gameRt != null ? gameRt.width + "x" + gameRt.height : "")} mirror camera off.");
        }

        private static void Configure(Side s, MirrorQuality q, int width, float angle)
        {
            if (q == MirrorQuality.Off)
            {
                s.Own.SetTexture(EmissionMapId, Texture2D.blackTexture);
                return;
            }
            int height = Mathf.Max(16, Mathf.RoundToInt(width / s.Aspect / 2f) * 2);
            if (s.Rt == null || s.Rt.width != width || s.Rt.height != height)
            {
                var old = s.Rt;
                var format = gameRt != null ? gameRt.format : RenderTextureFormat.ARGB32;
                s.Rt = new RenderTexture(width, height, 24, format, RenderTextureReadWrite.Default)
                {
                    name = "CloudpunkVR_Mirror" + (s.Right ? "Right" : "Left"),
                    useMipMap = true,
                    autoGenerateMips = true,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                s.Rt.Create();
                ClearToBlack(s.Rt);
                s.Cam.targetTexture = s.Rt;
                if (old != null) { old.Release(); UnityEngine.Object.Destroy(old); }
            }
            s.Own.SetTexture(EmissionMapId, s.Rt);
            ApplyLook(s.Cam, s.Look, s.Own, s.Emission);
            s.Cam.aspect = s.Aspect;
            // MirrorAngle is horizontal, the camera takes vertical
            s.Cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(angle * 0.5f * Mathf.Deg2Rad) / s.Aspect) * Mathf.Rad2Deg;
        }

        // ---- Readout and dev helpers.

        /// <summary>For the Performance line, in the cockpit.</summary>
        public static string Short()
        {
            if (CameraDriver.Mode != ViewMode.Cockpit) return null;
            string screens = !found ? null
                : !taken ? $"mirrors as the game ({gameStats.PerSecond:0}/s, {gameStats.PerFrame:0.00} ms per frame)"
                : applied == MirrorQuality.Off ? "mirrors off"
                : $"mirrors {applied} {left.Rt.width}x{left.Rt.height} ({left.Stats.PerSecond:0} + {right.Stats.PerSecond:0} draws/s, {left.Stats.PerFrame + right.Stats.PerFrame:0.00} ms per frame)";
            string rear = rearRt != null && rearStats.PerSecond > 0f
                ? $"rear mirror {rearRt.width}x{rearRt.height} ({rearStats.PerSecond:0} draws/s, {rearStats.PerFrame:0.00} ms per frame)" : null;
            return screens == null ? rear : rear == null ? screens : screens + ", " + rear;
        }

#if DEV
        public static string Dev(string[] a)
        {
            string what = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            switch (what)
            {
                case "":
                case "info":
                    return Describe();
                case "game":
                    GameForTest = true;
                    return "the game's own mirror from the next frame (mirrors low|high|off goes back to the mod's).";
                case "off":
                case "low":
                case "high":
                case "own": // MirrorQuality Game ("game" is per run)
                    GameForTest = false;
                    VRConfig.Mirrors.Value = what == "own" ? MirrorQuality.Game : (MirrorQuality)Enum.Parse(typeof(MirrorQuality), what, true);
                    return $"MirrorQuality {VRConfig.Mirrors.Value} from the next frame.";
                case "angle":
                    if (a.Length > 2) VRConfig.MirrorAngle.Value = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                    return $"MirrorAngle {VRConfig.MirrorAngle.Value:0} degrees.";
                case "look": // look left|right|rear|strip [close] | off
                    return Look(a.Length > 2 ? a[2].ToLowerInvariant() : "off", a.Length > 3 && a[3].ToLowerInvariant() == "close");
                case "snap":
                    return Snap(a.Length > 2 ? a[2] : "now");
                case "rear": // rear on|off: the RearMirror setting
                    VRConfig.RearMirror.Value = a.Length < 3 || a[2] != "off";
                    return $"RearMirror {VRConfig.RearMirror.Value}.";
                case "at": // at <right> <up> <ahead>: from the eyes, m
                    if (a.Length < 5) return "at <right> <up> <ahead>";
                    var c = System.Globalization.CultureInfo.InvariantCulture;
                    RearSpot = new Vector3(float.Parse(a[2], c), float.Parse(a[3], c), float.Parse(a[4], c));
                    rearPlaced = false;
                    return $"the rear-view mirror moves to {RearSpot.Value} from the eyes at the next frame.";
                case "grade": // grade game|plain
                    GameLook = a.Length < 3 || a[2].ToLowerInvariant() != "plain";
                    appliedValid = false;
                    return $"the mirrors' look: {(GameLook ? "the game's (its post-processing on their cameras)" : "plain, darkened")} from the next frame.";
                case "cull": // cull <m> (0: game's)
                    if (a.Length > 2) CullRadius = Mathf.Max(0f, float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture));
                    return $"the occlusion culling and the instancing keep {(CullRadius > 0f ? $"everything within {CullRadius:0} m" : "the game's own distances")} for the mirrors.";
                case "place": // rear mirror back to its spot
                    RearSpot = null;
                    rearPlaced = false;
                    return "the rear-view mirror goes back to its spot at the next frame.";
                default:
                    return "unknown: mirrors [info|game|off|low|high|angle <deg>|rear on|off|cull <m>|grade game|plain|at <right> <up> <ahead>|place|look left|right|rear|strip [close]|off|snap <name>]";
            }
        }
#endif

        /// <summary>Dev: turns the head toward a screen or the windscreen strip. "off" resets.</summary>
        private static string Look(string which, bool close)
        {
            if (which == "off") { VRSession.TestLook = null; VRSession.TestOffset = null; return "look: straight ahead again."; }
            Renderer r = which == "left" ? left.Screen : which == "right" ? right.Screen : which == "strip" ? Strip() : which == "rear" ? glassR : null;
            Transform target = r != null && r.gameObject.activeInHierarchy ? r.transform : null;
            if (target == null || CameraDriver.CockpitCamera == null) return $"look: no {which} screen here (cockpit view needed).";
            CameraDriver.CockpitToTracking(target.position, Quaternion.identity, out var p, out _);
            var raw = VRSession.TestLook.HasValue ? VRSession.HeadRotation * Quaternion.Inverse(VRSession.TestLook.Value) : VRSession.HeadRotation;
            Vector3 head = VRSession.HeadPosition - (VRSession.TestOffset ?? Vector3.zero);
            Vector3 toward = p - head;
            if (toward.sqrMagnitude < 1e-6f) return "look: the screen is at the eye?";
            // close: head moved to 25 cm along the gaze, to judge sharpness
            float dist = toward.magnitude;
            VRSession.TestOffset = close && dist > CloseLook ? toward * ((dist - CloseLook) / dist) : (Vector3?)null;
            Vector3 d = (Quaternion.Inverse(raw) * toward).normalized;
            float down = -Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg, rightDeg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            VRSession.TestLook = Quaternion.Euler(down, rightDeg, 0f);
            return $"look: the head turned {down:0.0} degrees down, {rightDeg:0.0} right, at the {which} {(which == "strip" ? "strip" : which == "rear" ? "mirror" : "screen")}, " +
                   (VRSession.TestOffset.HasValue ? $"moved {VRSession.TestOffset.Value.magnitude * 100f:0} cm toward it ({CloseLook * 100f:0} cm away)." : $"{dist:0.00} m away.");
        }

        private const float CloseLook = 0.25f;

        /// <summary>The glossy windscreen strip (Fake_Text_Mirror_Interior), the cockpit's other "mirror".</summary>
        private static Renderer Strip() => Strip(rearFrame != null ? rearFrame : interior);

        private static Renderer Strip(Transform set)
        {
            if (set == null) return null;
            var renderers = set.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var m = renderers[i] != null ? renderers[i].sharedMaterial : null;
                if (m != null && m.name.StartsWith("Fake_Text_Mirror")) return renderers[i];
            }
            return null;
        }

        /// <summary>Saves each mirror texture to BepInEx (upside down, the screens flip it).</summary>
        private static string Snap(string name)
        {
            // the rear camera films upright, the glass flips it
            string rear = rearRt != null && rearCam != null && rearCam.enabled ? ", " + Save(rearRt, $"mirrors-{name}-rear.png") + " (seen from behind the glass: flipped left to right on it)" : "";
            if (!found) return "snap: no screen found" + rear;
            if (!taken) return "snap: " + Save(gameRt, $"mirrors-{name}-game.png") + rear;
            if (applied == MirrorQuality.Off) return "snap: the mirrors are off.";
            return "snap: " + Save(left.Rt, $"mirrors-{name}-left.png") + ", " + Save(right.Rt, $"mirrors-{name}-right.png") + rear;
        }

        private static string Save(RenderTexture rt, string file)
        {
            if (rt == null) return file + " (no texture)";
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var bytes = ImageConversion.EncodeToPNG(tex);
            UnityEngine.Object.Destroy(tex);
            string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, file);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>Dev command "mirrors": per-mirror cost and sharpness, other cockpit displays, culling.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            float eyePxPerDeg = EyePixelsPerDegree();
            sb.Append($"view {CameraDriver.Mode}; MirrorQuality {VRConfig.Mirrors.Value}, MirrorAngle {VRConfig.MirrorAngle.Value:0}, RearMirror {VRConfig.RearMirror.Value}; eye about {eyePxPerDeg:0.0} px per degree");
            if (!found) sb.Append("\n   screens: the game's mirror not found yet (cockpit view needed)");
            else sb.Append(taken ? $"\n   screens drawn by the mod (the game's camera '{gameCam.name}' {(gameCam.enabled ? "ON (unexpected)" : "off")})"
                                 : $"\n   screens drawn by the game's camera '{gameCam.name}' ({(gameCam.enabled ? "on" : "off")})");
            if (found && taken && applied != MirrorQuality.Off)
            {
                DescribeSide(sb, left, eyePxPerDeg);
                DescribeSide(sb, right, eyePxPerDeg);
            }
            else if (found && !taken)
            {
                sb.Append($"\n   game: camera '{Path(gameCam.transform)}' {gameRt.width}x{gameRt.height} {gameRt.format}, {gameStats.PerSecond:0.0} draws/s, " +
                          $"{gameStats.MsPerRender:0.00} ms per draw (worst {gameStats.Worst:0.00}), {gameStats.PerFrame:0.00} ms per frame; sees {Sees(gameCam)}; " +
                          $"both screens show it: left {Sharpness(left, gameRt.width, eyePxPerDeg)}, right {Sharpness(right, gameRt.width, eyePxPerDeg)}");
            }
            DescribeRear(sb, eyePxPerDeg);
            DescribeOthers(sb, vi);
            return sb.ToString();
        }

        private static void DescribeSide(StringBuilder sb, Side s, float eyePxPerDeg)
        {
            var c = s.Cam;
            var st = s.Stats;
            sb.Append($"\n   {s.Name}: camera {c.name} {s.Rt.width}x{s.Rt.height} {s.Rt.format} mipmapped trilinear aniso {s.Rt.anisoLevel}, " +
                      $"{st.PerSecond:0.0} draws/s ({(applied == MirrorQuality.High ? "every frame" : "taking turns")}), {st.MsPerRender:0.00} ms per draw (worst {st.Worst:0.00}), {st.PerFrame:0.00} ms per frame; " +
                      $"sees {Sees(c)}, shadows {(skipShadows ? "off" : "on")}, look {LookName(s.Look)}; " +
                      $"at {s.LocalPos} in the HOVA's world part, {Aim(s)}; screen {Sharpness(s, s.Rt.width, eyePxPerDeg)}, mapping {s.Mapping}");
        }

        private static string LookName(PostProcessLayer layer) =>
            layer != null && layer.enabled ? "the game's (its post-processing)" : layer == null ? "plain, darkened (no post-processing layer)" : "plain, darkened";

        private static string Sees(Camera c)
        {
            float v = c.fieldOfView, h = 2f * Mathf.Atan(Mathf.Tan(v * 0.5f * Mathf.Deg2Rad) * c.aspect) * Mathf.Rad2Deg;
            int mask = c.cullingMask, layers = 0;
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) layers++;
            string path;
            try { path = c.actualRenderingPath.ToString(); } catch (Exception) { path = "?"; }
            return $"{h:0} x {v:0} deg, {c.nearClipPlane:0.##} to {c.farClipPlane:0} m, mask 0x{mask:X8} ({layers} layers, interior set {((mask & (1 << 28)) != 0 ? "IN" : "out")}), {path}";
        }

        private static string Aim(Side s)
        {
            Vector3 f = s.LocalRot * Vector3.forward, up = s.LocalRot * Vector3.up;
            float outward = Mathf.Atan2(s.Right ? f.x : -f.x, -f.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            return $"looking back {outward:0} deg out, {pitch:0} up, {(up.y < 0f ? "rolled over (the screen turns it back)" : "upright")}";
        }

        /// <summary>Screen width in view, and texels per degree vs the eye's pixels.</summary>
        private static string Sharpness(Side s, int texels, float eyePxPerDeg)
        {
            var eye = CameraDriver.CockpitCamera;
            if (s.Screen == null) return "-";
            string size = $"'{s.Screen.name}' {s.WidthM * 100f:0.0} x {s.HeightM * 100f:0.0} cm";
            if (eye == null) return size;
            Vector3 c = s.Screen.bounds.center, half = s.Screen.transform.right * (s.WidthM * 0.5f), e = eye.transform.position;
            float deg = Vector3.Angle(c - half - e, c + half - e);
            float dist = Vector3.Distance(c, e);
            float density = deg > 0.01f ? texels / deg : 0f;
            return $"{size}, {dist:0.00} m away, {deg:0.0} deg wide in view: {density:0.0} texels per degree, {(eyePxPerDeg > 0f ? density / eyePxPerDeg : 0f):0.00} of the eye's";
        }

        private static float EyePixelsPerDegree()
        {
            var cam = CameraDriver.ViewCamera;
            if (cam == null || XRSettings.eyeTextureWidth <= 0) return 0f;
            float h = 2f * Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect) * Mathf.Rad2Deg;
            return h > 1f ? XRSettings.eyeTextureWidth / h : 0f;
        }

        private static void DescribeOthers(StringBuilder sb, VehicleInterior vi)
        {
            if (vi == null) return;
            try
            {
                var pv = vi.parkingVariables;
                var pc = pv != null ? pv.parkingCamera : null;
                var pt = pc != null ? pc.targetTexture : null;
                sb.Append($"\n   parking display: camera {(pc != null ? $"'{pc.name}' {(pc.enabled && pc.gameObject.activeInHierarchy ? "on" : "off")}" : "none")}" +
                          $"{(pt != null ? $" {pt.width}x{pt.height}" : "")}, screen {(pv != null && pv.parkingDisplay != null ? (pv.parkingDisplay.enabled ? "shown" : "hidden") : "none")} (the game's, unchanged)");
            }
            catch (Exception e) { sb.Append("\n   parking display: " + e.Message); }
            try
            {
                var strip = Strip();
                if (strip != null)
                {
                    var eye = CameraDriver.CockpitCamera;
                    var b = strip.bounds;
                    sb.Append($"\n   strip: '{strip.name}' under '{(strip.transform.parent != null ? strip.transform.parent.name : "-")}' ({strip.sharedMaterial.name}), " +
                              $"{b.size.x * 100f:0.0} x {b.size.y * 100f:0.0} cm{(eye != null ? $", {Vector3.Distance(b.center, eye.transform.position):0.00} m from the eye" : "")}: the district sign's glossy strip, a 'mirror' only by its material (it reflects the interior's probe), left as it is");
                }
                var probe = vi.reflectionProbe;
                if (probe != null)
                {
                    var tex = probe.texture;
                    sb.Append($"\n   interior reflection probe '{probe.name}': {probe.mode}, refresh {probe.refreshMode}, texture {(tex != null ? $"'{tex.name}' {tex.width}x{tex.height}" : "none")}");
                }
                var pr = vi.reflectionProbeRenderer;
                if (pr != null) sb.Append($", the game's probe renderer '{pr.name}'");
            }
            catch (Exception e) { sb.Append("\n   reflections: " + e.Message); }
            try
            {
                var main = Global.Instance.mainCamera;
                var gdoc = main != null ? main.GetComponent<GDOC>() : null;
                if (gdoc != null)
                    sb.Append($"\n   occlusion culling (GDOC): {(gdoc.enabled ? "on" : "off")}, everything within {gdoc.minimumDistance:0} m kept drawn " +
                              $"({(cullingRaised ? $"raised from the game's {cullingBefore:0} m for the mirrors" : "the game's own")}), keep shadows {gdoc.keepShadows}: " +
                              "what it hides from the main camera behind the HOVA beyond that distance is missing from the mirrors (compare with perf gdoc off)");
                sb.Append("\n   instanced buildings and props (GPU Instancer): " + Instancers());
            }
            catch (Exception e) { sb.Append("\n   occlusion culling: " + e.Message); }
        }
    }
}
