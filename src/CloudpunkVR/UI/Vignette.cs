using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Comfort vignette for fast turns, climbs, swerves and hard stops. Cruising and gentle throttle leave it off.
    /// Anchor motion is filtered: the game moves the camera at 60 Hz physics, the headset draws at 90/120.
    /// </summary>
    internal static class Vignette
    {
        private static Transform quad;
        private static Material material;
        private static float intensity, target, shownAlpha = -1f;

        public static float Intensity => intensity;
        private static float forcedUntil;
#if DEV
        public static void Force(float seconds) => forcedUntil = Time.unscaledTime + seconds;
#endif

        public static void Create(Transform overlayCamera, int layer)
        {
            var go = new GameObject("CloudpunkVR_Vignette");
            go.layer = layer;
            go.transform.SetParent(overlayCamera, false);
            go.transform.localPosition = new Vector3(0f, 0f, Distance);
            go.AddComponent<MeshFilter>().sharedMesh = MakeRing();
            material = new Material(Shader.Find("UI/Default")) { name = "CloudpunkVR_Vignette", color = Color.black };
            material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            material.renderQueue = 4000; // after the panel and laser
            material.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always); // over them though farther
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            quad = go.transform;
            go.SetActive(false);
        }

        // far, so each eye sees the hole centred (close up, the eye offset shifts it toward the nose)
        private const float Distance = 10f;

        /// <summary>Clear inside radius 1, black from 1.9 to 40. Scaling only changes the hole.</summary>
        private static Mesh MakeRing()
        {
            const int seg = 64;
            float[] radii = { 1f, 1.9f, 40f };
            float[] alpha = { 0f, 1f, 1f };
            var verts = new Vector3[seg * radii.Length];
            var cols = new Color[verts.Length];
            for (int ring = 0; ring < radii.Length; ring++)
                for (int i = 0; i < seg; i++)
                {
                    float a = i * Mathf.PI * 2f / seg;
                    verts[ring * seg + i] = new Vector3(Mathf.Cos(a) * radii[ring], Mathf.Sin(a) * radii[ring], 0f);
                    cols[ring * seg + i] = new Color(1f, 1f, 1f, alpha[ring]);
                }
            var tris = new int[(radii.Length - 1) * seg * 6];
            int k = 0;
            for (int ring = 0; ring < radii.Length - 1; ring++)
                for (int i = 0; i < seg; i++)
                {
                    int a0 = ring * seg + i, a1 = ring * seg + (i + 1) % seg;
                    int b0 = a0 + seg, b1 = a1 + seg;
                    tris[k++] = a0; tris[k++] = b0; tris[k++] = a1;
                    tris[k++] = a1; tris[k++] = b0; tris[k++] = b1;
                }
            var m = new Mesh { name = "CloudpunkVR_VignetteRing" };
            m.vertices = verts;
            m.colors = cols;
            m.triangles = tris;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return m;
        }

        // ---- The view's motion, filtered.

        private const float VelocityTau = 0.08f;  // per stage, two stages
        private const float AccelTau = 0.2f;      // from the smoothed velocity
        private const float YawTau = 0.12f;
        private const float SettleTime = 0.35f;   // after a reset, before accel counts
        private const float AttackTau = 0.3f, ReleaseTau = 1.0f;

        private static int stage;                 // 0 nothing yet, 1 one position, 2 running
        private static float settle;
        private static Vector3 lastPos, vel1, vel2, lastVel, accel, pull;
        private static float lastYaw, yawRate;
        private static ViewMode lastMode = ViewMode.Startup;

        // kept for the readout
        private static Vector3 accelLocal;
        private static float turnTerm, climbTerm, swerveTerm, joltTerm, stopTerm, surgeTerm;

        // dev "vignette trace <s>": a log line every 0.25 s
        private static float traceUntil, nextTrace, traceStart, maxStep, peak;

#if DEV
        public static void Trace(float seconds)
        {
            traceStart = Time.unscaledTime;
            traceUntil = traceStart + seconds;
            nextTrace = 0f;
            maxStep = peak = 0f;
        }
#endif

        private static float Blend(float dt, float tau) => 1f - Mathf.Exp(-dt / tau);

        /// <summary>0 below start, 1 from full, smoothstep between.</summary>
        private static float Knee(float x, float start, float full)
        {
            float t = Mathf.Clamp01((x - start) / (full - start));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Smoothed turn rate, velocity and acceleration. After a reset, accel waits for velocity to settle.</summary>
        private static void Measure(float dt, bool reset)
        {
            Vector3 pos = CameraDriver.AnchorPosition;
            float yaw = CameraDriver.Yaw(CameraDriver.AnchorRotation);
            if (reset) stage = 0;
            if (stage == 0)
            {
                stage = 1;
                lastPos = pos;
                lastYaw = yaw;
                yawRate = 0f;
                vel1 = vel2 = lastVel = accel = pull = accelLocal = Vector3.zero;
                turnTerm = climbTerm = swerveTerm = joltTerm = stopTerm = surgeTerm = 0f;
                return;
            }
            Vector3 v = (pos - lastPos) / dt;
            float w = Mathf.DeltaAngle(lastYaw, yaw) / dt;
            lastPos = pos;
            lastYaw = yaw;
            if (v.sqrMagnitude > 200f * 200f) { stage = 1; accel = pull = Vector3.zero; return; } // teleport the cut check missed
            if (stage == 1)
            {
                stage = 2;
                vel1 = vel2 = lastVel = v;
                settle = SettleTime;
            }
            float kv = Blend(dt, VelocityTau);
            vel1 += (v - vel1) * kv;
            vel2 += (vel1 - vel2) * kv;
            Vector3 a = (vel2 - lastVel) / dt;
            lastVel = vel2;
            yawRate += (w - yawRate) * Blend(dt, YawTau);
            // centripetal pull of the turn, subtracted later so sideways accel only counts swerves
            Quaternion heading = Quaternion.Euler(0f, yaw, 0f);
            Vector3 vLocal = Quaternion.Inverse(heading) * vel2;
            float wr = yawRate * Mathf.Deg2Rad;
            Vector3 p = heading * new Vector3(wr * vLocal.z, 0f, -wr * vLocal.x);
            if (settle > 0f) settle -= dt;
            else
            {
                float ka = Blend(dt, AccelTau);
                accel += (a - accel) * ka;
                pull += (p - pull) * ka;
            }
        }

        /// <summary>0..1, the max of turn, climb, swerve, jolt, stop and (a little) surge.</summary>
        private static float Need()
        {
            accelLocal = Quaternion.Inverse(Quaternion.Euler(0f, lastYaw, 0f)) * (accel - pull); // x: sideways, y: up, z: forward
            turnTerm = Knee(Mathf.Abs(yawRate), 25f, 90f);
            // the game's own level changes (up to ~8 m/s, ~20 m/s2 stop) feel fine
            climbTerm = Knee(Mathf.Abs(vel2.y), 8f, 20f);
            swerveTerm = Knee(Mathf.Abs(accelLocal.x), 4f, 14f);
            joltTerm = Knee(Mathf.Abs(accelLocal.y), 20f, 45f);
            stopTerm = Knee(-accelLocal.z, 14f, 35f);
            surgeTerm = 0.35f * Knee(accelLocal.z, 15f, 45f);
            return Mathf.Max(Mathf.Max(turnTerm, climbTerm), Mathf.Max(Mathf.Max(swerveTerm, joltTerm), Mathf.Max(stopTerm, surgeTerm)));
        }

        public static void Update()
        {
            if (quad == null) return;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            var mode = CameraDriver.Mode;
            bool inVehicle = mode == ViewMode.Cockpit || mode == ViewMode.Chase;
            // reset on hitches, menus, pauses, cuts and view changes, or they read as bursts of speed
            bool reset = !inVehicle || mode != lastMode || CameraDriver.CutThisFrame || TransitionGuard.Active
                         || dt > 0.1f || VRUI.Interactive || Time.timeScale < 0.01f;
            lastMode = mode;
            Measure(dt, reset);

            float need = inVehicle && stage == 2 ? Need() : 0f;
            target = VRConfig.FlightVignette.Value && inVehicle && !VRUI.Interactive ? need * VRConfig.VignetteStrength.Value : 0f;
            if (Time.unscaledTime < forcedUntil) target = VRConfig.VignetteStrength.Value;
            float before = intensity;
            intensity += (target - intensity) * Blend(dt, target > intensity ? AttackTau : ReleaseTau);
            if (target <= 0f && intensity < 0.002f) intensity = 0f;
            if (Film.CleanFrame.HidesOverlays) intensity = target = 0f; // clean frame: off at once
            maxStep = Mathf.Max(maxStep, Mathf.Abs(intensity - before));
            peak = Mathf.Max(peak, intensity);
            LogTrace();

            bool show = intensity > 0.002f;
            if (quad.gameObject.activeSelf != show) quad.gameObject.SetActive(show);
            if (!show) return;
            // hole half-angle 60 deg barely on, 25 at full. Alpha fades in first so no edge pops.
            float halfAngle = Mathf.Lerp(60f, 25f, intensity);
            float r = Distance * Mathf.Tan(halfAngle * Mathf.Deg2Rad);
            quad.localScale = new Vector3(r, r, 1f);
            float alpha = Mathf.SmoothStep(0f, 1f, intensity / 0.15f);
            if (Mathf.Abs(alpha - shownAlpha) > 0.004f)
            {
                shownAlpha = alpha;
                material.color = new Color(0f, 0f, 0f, alpha);
            }
        }

        private static void LogTrace()
        {
            float now = Time.unscaledTime;
            if (now >= traceUntil || now < nextTrace) return;
            nextTrace = now + 0.25f;
            Plugin.L.LogInfo($"vignette trace +{now - traceStart:0.00} s: {Describe()}");
            maxStep = peak = 0f;
        }

        public static string Describe()
        {
            return $"{(VRConfig.FlightVignette.Value ? "on" : "off")}, strength {VRConfig.VignetteStrength.Value:0.00}, view {CameraDriver.Mode}" +
                   $"{(stage < 2 ? " (measure starting)" : settle > 0f ? " (settling)" : "")}; turn {yawRate:0.0} deg/s, climb {vel2.y:0.0} m/s, " +
                   $"speed {new Vector2(vel2.x, vel2.z).magnitude:0.0} m/s, accel forward {accelLocal.z:0.0} sideways {accelLocal.x:0.0} up {accelLocal.y:0.0} m/s2 (the turn's own pull {pull.magnitude:0.0} taken out); " +
                   $"needs turn {turnTerm:0.00} climb {climbTerm:0.00} swerve {swerveTerm:0.00} jolt {joltTerm:0.00} stop {stopTerm:0.00} surge {surgeTerm:0.00}; " +
                   $"target {target:0.00}, intensity {intensity:0.000} (peak {peak:0.000}, largest step in a frame {maxStep:0.0000})";
        }
    }
}
