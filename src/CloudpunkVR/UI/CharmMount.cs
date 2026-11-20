using System;
using System.Collections.Generic;
using CloudpunkVR.VR;
using UnityEngine;
using N = System.Numerics;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Where the charm hangs in the cockpit set: from the ceiling 26 cm right of and 28 cm ahead of the eyes.
    /// Ceiling from colliders, then readable meshes, then the cable anchors, else an estimate. Kept in the set's space.
    /// </summary>
    internal static class CharmMount
    {
        private const float Right = 0.26f;
        private static readonly float[] Ahead = { 0.28f, 0.26f, 0.24f }; // tried in turn until one has a ceiling
        private const float HeadAboveEyes = 0.08f;      // head centre at rest
        private const float MinChain = 0.04f, MaxChain = 0.15f;
        private const float CeilingMin = 0.12f, CeilingMax = 0.6f; // believable ceiling heights above the eyes
        private const float EstimatedCeiling = 0.22f;   // measured in the HOVA
        private const float LookAhead = 0.4f;           // how far ahead to look for obstacles

        public static Transform Parent { get; private set; }
        public static Vector3 PivotLocal { get; private set; }   // chain pivot, in Parent's space
        public static Vector3 UpLocal { get; private set; }      // HOVA up, in Parent's space
        public static Vector3 FaceLocal { get; private set; }    // rest facing: toward the driver
        public static Vector3 AheadLocal { get; private set; }   // HOVA forward, in Parent's space
        public static float ChainLength { get; private set; }
        public static float HeadRoom { get; private set; }       // free space ahead of the head's centre
        public static float RingRoom { get; private set; }       // and of the ring
        public static string Source { get; private set; } = "not placed";
        private static string inFront = "";
        private static Vector3 offset;                           // pivot from the eyes: right, up, ahead
        private static Vector3 eyeLocal;                         // rest eye position, in Parent's space
        private static bool meshesUnreadable;

        /// <summary>"charm at" command: pivot from the eyes (right, up, ahead), skips the search.</summary>
        public static Vector3? Manual;

        public static bool Valid => Parent != null;

        /// <summary>A box the charm bumps into (the rear mirror), in set space. Set each frame before the charm moves.</summary>
        public static bool HasObstacle { get; private set; }
        public static Vector3 ObstacleCenter { get; private set; }
        public static Quaternion ObstacleRotation { get; private set; } = Quaternion.identity;
        public static Vector3 ObstacleHalf { get; private set; }

        public static void SetObstacle(Vector3 center, Quaternion rotation, Vector3 half)
        {
            ObstacleCenter = center;
            ObstacleRotation = rotation;
            ObstacleHalf = half;
            HasObstacle = true;
        }

        public static void ClearObstacle() => HasObstacle = false;

        public static void Forget() => Parent = null;

        /// <summary>True if the game moved the cockpit camera since placing: time to place again.</summary>
        public static bool EyesMoved()
        {
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eye, out _);
            return (Parent.InverseTransformPoint(eye) - eyeLocal).magnitude > 0.15f;
        }

        public static string Describe() => Valid
            ? $"hangs from {Source}, {offset.x:0.00} m right, {offset.y:0.00} m up, {offset.z:0.00} m ahead of the eyes, chain {ChainLength * 100f:0} cm; {inFront}"
            : "not placed yet";

        /// <summary>Finds where to hang. belowChain: head centre below the chain's end at rest (stretch plus ring).</summary>
        public static bool Place(float belowChain)
        {
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            if (vi == null || CameraDriver.CockpitCamera == null) return false;
            var parent = vi.interiorLocal != null ? vi.interiorLocal : vi.transform;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            // in the cockpit the anchor is the game's camera: rest eyes and HOVA heading in the set
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eye, out var eyeRot);
            Vector3 up = parent.up;
            Vector3 fwd = Vector3.ProjectOnPlane(eyeRot * Vector3.forward, up);
            if (fwd.sqrMagnitude < 0.25f) fwd = Vector3.ProjectOnPlane(parent.forward, up);
            fwd.Normalize();
            Vector3 right = Vector3.Cross(up, fwd);
            Vector3 At(float r, float u, float f) => eye + right * r + up * u + fwd * f;

            Vector3 pivot = default;
            Source = null;
            if (Manual is Vector3 m)
            {
                pivot = At(m.x, m.y, m.z);
                Source = "a spot set by hand";
            }
            else
            {
                foreach (float ahead in Ahead)
                {
                    if (!Ceiling(vi, parent, At(Right, 0f, ahead), up, out float height, out string what)) continue;
                    pivot = At(Right, height - CharmModel.MountHeight, ahead);
                    Source = what;
                    break;
                }
                if (Source == null && WireAnchors(vi, eye, up, out float wires))
                {
                    pivot = At(Right, wires - CharmModel.MountHeight, Ahead[0]);
                    Source = "the ceiling where the HOVA's cables hang";
                }
                if (Source == null)
                {
                    pivot = At(Right, EstimatedCeiling, Ahead[0]);
                    Source = "an estimate of the ceiling";
                }
            }

            Vector3 rel = pivot - eye;
            offset = new Vector3(Vector3.Dot(rel, right), Vector3.Dot(rel, up), Vector3.Dot(rel, fwd));
            ChainLength = Mathf.Clamp(offset.y - HeadAboveEyes - belowChain, MinChain, MaxChain);

            // swing room toward the windscreen
            Vector3 ring = pivot - up * ChainLength, head = ring - up * belowChain;
            HeadRoom = InTheWay(vi, parent, head, fwd, out string headWhat);
            RingRoom = InTheWay(vi, parent, ring, fwd, out string ringWhat);
            inFront = HeadRoom < LookAhead || RingRoom < LookAhead
                ? $"ahead of the head {Cm(HeadRoom)} ({headWhat ?? "nothing"}), of the ring {Cm(RingRoom)} ({ringWhat ?? "nothing"})"
                : $"nothing within {LookAhead * 100f:0} cm ahead";

            Vector3 face = Vector3.ProjectOnPlane(eye - pivot, up);
            if (face.sqrMagnitude < 1e-4f) face = -fwd;
            Parent = parent;
            eyeLocal = parent.InverseTransformPoint(eye);
            PivotLocal = parent.InverseTransformPoint(pivot);
            UpLocal = parent.InverseTransformDirection(up);
            FaceLocal = parent.InverseTransformDirection(face.normalized);
            AheadLocal = parent.InverseTransformDirection(fwd);
            Plugin.L.LogInfo($"Charm: {Describe()}; placed in {(System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms{(TransitionGuard.Active ? " under the black" : "")}.");
            return true;
        }

        private static string Cm(float d) => d < LookAhead ? $"{d * 100f:0} cm" : "clear";

        /// <summary>Windscreen and its overlays (rain, cracks): not a ceiling, but an obstacle ahead.</summary>
        private static bool Glass(VehicleInterior vi, Component c)
        {
            var go = c.gameObject;
            return (vi.windshieldFrontMesh != null && go == vi.windshieldFrontMesh.gameObject)
                || (vi.damageIndicatorMesh != null && go == vi.damageIndicatorMesh.gameObject)
                || c.name.IndexOf("wind", StringComparison.OrdinalIgnoreCase) >= 0
                || c.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0
                || c.name.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Nearest thing straight up (colliders, then meshes). Skips the windscreen, which slopes over the spot.</summary>
        private static bool Ceiling(VehicleInterior vi, Transform parent, Vector3 from, Vector3 up, out float height, out string what)
        {
            height = CeilingMax;
            what = null;
            var hits = Physics.RaycastAll(from, up, CeilingMax, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null || !c.transform.IsChildOf(parent) || Glass(vi, c) || hits[i].distance < CeilingMin || hits[i].distance >= height) continue;
                height = hits[i].distance;
                what = $"the ceiling ('{c.name}')";
            }
            if (what != null) return true;
            string name = Nearest(vi, parent, from, up, CeilingMin, ref height, false, true);
            if (name == null) return false;
            what = $"the ceiling ('{name}')";
            return true;
        }

        /// <summary>Distance to the first obstacle ahead (or LookAhead): set meshes plus the world-side windscreens.</summary>
        private static float InTheWay(VehicleInterior vi, Transform parent, Vector3 from, Vector3 fwd, out string what)
        {
            float d = LookAhead;
            what = Nearest(vi, parent, from, fwd, 0.005f, ref d, true, true);
            var glass = vi.windshieldFrontMesh != null ? vi.windshieldFrontMesh.transform.parent : null;
            if (glass != null)
            {
                // undo WorldToCockpit for the ray
                CameraDriver.WorldToCockpit(Vector3.zero, Quaternion.identity, out var origin, out var turn);
                var back = Quaternion.Inverse(turn);
                what = Nearest(vi, glass, back * (from - origin), back * fwd, 0.005f, ref d, true, false) ?? what;
            }
            return d;
        }

        /// <summary>Nearest triangle hit under root between min and max (max gets the hit). Skips unreadable meshes.</summary>
        private static string Nearest(VehicleInterior vi, Transform root, Vector3 from, Vector3 dir, float min, ref float max, bool glass, bool activeOnly)
        {
            if (meshesUnreadable) return null;
            string what = null;
            var seen = new HashSet<int>();
            var renderers = root.GetComponentsInChildren<MeshRenderer>(!activeOnly);
            try
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null || (activeOnly && !r.enabled) || (!glass && Glass(vi, r))) continue;
                    var b = r.bounds;
                    if (BoxEntry(from, dir, b.center, b.extents) >= max) continue;
                    var filter = r.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : null;
                    // a lone quad is a screen or hologram
                    if (mesh == null || !mesh.isReadable || mesh.vertexCount <= 4 || !seen.Add(mesh.GetInstanceID())) continue;
                    // static batches are already in world space
                    bool world = r.isPartOfStaticBatch;
                    Vector3 o = world ? from : r.transform.InverseTransformPoint(from);
                    Vector3 d = world ? dir : r.transform.InverseTransformVector(dir);
                    float t = NearestTriangle(mesh.vertices, mesh.triangles, new N.Vector3(o.x, o.y, o.z), new N.Vector3(d.x, d.y, d.z), min, max);
                    if (t >= max) continue;
                    max = t;
                    what = r.name;
                }
            }
            catch (Exception e)
            {
                // mesh reads may be stripped: fall back to the cables and the estimate
                meshesUnreadable = true;
                Plugin.L.LogInfo("Charm: the cockpit's meshes cannot be read (" + e.Message + ").");
            }
            return what;
        }

        /// <summary>Ray entry distance into an AABB (0 if inside, infinity on a miss).</summary>
        private static float BoxEntry(Vector3 from, Vector3 dir, Vector3 centre, Vector3 extents)
        {
            float[] os = { from.x - centre.x, from.y - centre.y, from.z - centre.z }, ds = { dir.x, dir.y, dir.z }, es = { extents.x, extents.y, extents.z };
            float near = 0f, far = float.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                float o = os[a], d = ds[a], e = es[a];
                if (MathF.Abs(d) < 1e-8f)
                {
                    if (MathF.Abs(o) > e) return float.PositiveInfinity;
                    continue;
                }
                float t1 = (-e - o) / d, t2 = (e - o) / d;
                near = MathF.Max(near, MathF.Min(t1, t2));
                far = MathF.Min(far, MathF.Max(t1, t2));
                if (near > far) return float.PositiveInfinity;
            }
            return near;
        }

        /// <summary>Nearest two-sided triangle hit between min and max, or max.</summary>
        private static float NearestTriangle(Vector3[] v, int[] tris, N.Vector3 o, N.Vector3 d, float min, float max)
        {
            float best = max;
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                Vector3 pa = v[tris[k]], pb = v[tris[k + 1]], pc = v[tris[k + 2]];
                var a = new N.Vector3(pa.x, pa.y, pa.z);
                var e1 = new N.Vector3(pb.x, pb.y, pb.z) - a;
                var e2 = new N.Vector3(pc.x, pc.y, pc.z) - a;
                var p = N.Vector3.Cross(d, e2);
                float det = N.Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var s = o - a;
                float u = N.Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                var q = N.Vector3.Cross(s, e1);
                float w = N.Vector3.Dot(d, q) * inv;
                if (w < 0f || u + w > 1f) continue;
                float t = N.Vector3.Dot(e2, q) * inv;
                if (t >= min && t < best) best = t;
            }
            return best;
        }

        /// <summary>Ceiling height from the highest end of the ceiling cables (VehicleInterior.wires) near the driver.</summary>
        private static bool WireAnchors(VehicleInterior vi, Vector3 eye, Vector3 up, out float height)
        {
            height = 0f;
            var wires = vi.wires;
            if (wires == null) return false;
            for (int i = 0; i < wires.Length; i++)
            {
                var w = wires[i];
                var splits = w != null && w.isActiveAndEnabled ? w.segmentSplits : null;
                if (splits == null) continue;
                for (int j = 0; j < splits.Length; j++)
                {
                    var s = splits[j];
                    if (s == null || (s.position - eye).magnitude > 1.2f) continue;
                    float h = Vector3.Dot(s.position - eye, up);
                    if (h >= CeilingMin && h <= CeilingMax) height = Mathf.Max(height, h);
                }
            }
            return height > 0f;
        }

        // ---------------------------------------------------------------- developer command "charm tree"

        /// <summary>Logs the cockpit set (position from the eyes, layer, renderer), the windscreens and the cable ends.</summary>
        public static void LogTree()
        {
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            if (vi == null || CameraDriver.CockpitCamera == null) { Plugin.L.LogInfo("charm tree: not in the cockpit"); return; }
            var parent = vi.interiorLocal != null ? vi.interiorLocal : vi.transform;
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eye, out var eyeRot);
            Vector3 up = parent.up, fwd = Vector3.ProjectOnPlane(eyeRot * Vector3.forward, up).normalized, right = Vector3.Cross(up, fwd);
            string Where(Vector3 p) { var r = p - eye; return $"({Vector3.Dot(r, right):0.00}, {Vector3.Dot(r, up):0.00}, {Vector3.Dot(r, fwd):0.00})"; }
            var L = Plugin.L;
            int lines = 0;

            L.LogInfo($"charm tree: set '{parent.name}' at {Where(parent.position)} from the eyes (right, up, ahead); VehicleInterior on '{vi.name}'");
            void Walk(Transform t, int depth, bool inWorld)
            {
                if (++lines > 900) return;
                var p = t.position;
                if (inWorld) CameraDriver.WorldToCockpit(p, Quaternion.identity, out p, out _);
                var r = t.GetComponent<Renderer>();
                string draw = r == null ? "" : $" {r.GetIl2CppType().Name}{(r.enabled ? "" : " off")} size {r.bounds.size.x:0.00}x{r.bounds.size.y:0.00}x{r.bounds.size.z:0.00}"
                    + (r.TryCast<MeshRenderer>() != null ? MeshNote(t) : "");
                L.LogInfo($"charm tree: {new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (inactive)")} layer {t.gameObject.layer} at {Where(p)}{draw}");
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, inWorld);
            }
            Walk(parent, 0, false);
            var glass = vi.windshieldFrontMesh != null ? vi.windshieldFrontMesh.transform.parent : null;
            if (glass != null) Walk(glass, 0, true);
            var wires = vi.wires;
            for (int i = 0; wires != null && i < wires.Length; i++)
            {
                var w = wires[i];
                var splits = w != null ? w.segmentSplits : null;
                var ends = new List<string>();
                for (int j = 0; splits != null && j < splits.Length; j++) if (splits[j] != null) ends.Add(Where(splits[j].position));
                L.LogInfo($"charm tree: cable '{(w != null ? w.name : "-")}' {(w != null && w.isActiveAndEnabled ? "on" : "off")}: {string.Join(" ", ends)}");
            }
            L.LogInfo("charm tree: charm " + Describe());
        }

        private static string MeshNote(Transform t)
        {
            var f = t.GetComponent<MeshFilter>();
            var m = f != null ? f.sharedMesh : null;
            return m == null ? "" : $" mesh '{m.name}' {m.vertexCount} vertices{(m.isReadable ? " readable" : "")}";
        }
    }
}
