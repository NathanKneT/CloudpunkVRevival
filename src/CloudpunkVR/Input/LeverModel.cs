using System;
using System.Collections.Generic;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// The HOVA's lift lever. The game sets localRotation = Euler(currentAngle, 0, 0). If an update changes that, we
    /// leave the lever alone. Handle measured from the mesh: crossbar if wide along the hinge, else an upright stick.
    /// </summary>
    internal static class LeverModel
    {
        /// <summary>Lever at rest: hinge pivot, axis, and where the handle points.</summary>
        public struct Frame
        {
            public Vector3 Pivot, Axis, Up;
        }

        public static bool Ready { get; private set; }
        /// <summary>Ready, and the game's poses follow the rule (CheckGamePose).</summary>
        public static bool Usable => Ready && !broken;
        /// <summary>Handle bar: half length, radius, grip distance from the hinge, m.</summary>
        public static float BarHalfLength { get; private set; } = 0.03f;
        public static float BarRadius { get; private set; } = 0.012f;
        public static float GripRadius { get; private set; } = 0.09f;
        /// <summary>Crossbar along the hinge, else an upright grip.</summary>
        public static bool Crossbar { get; private set; } = true;

        private const int MaxVertices = 4000;
        private const float MismatchTime = 1f; // s of off-rule game poses before giving up the lever

        private static int leverId;
        private static bool broken;
        private static Vector3 upLocal = Vector3.up, tanLocal = Vector3.forward, gripLocal; // lever's own frame, m
        private static Vector3 handleSize;  // along the hinge, across the travel, along the handle
        private static float reach;         // m from the hinge to the farthest point
        private static string source = "";
        private static readonly List<Vector3> vertices = new List<Vector3>();
        private static bool haveWritten;
        private static Quaternion written;
        private static float mismatchFor, matchFor, worstMismatch;

        /// <summary>Measures a new lever. False if there's nothing to hold or the game poses it off-rule.</summary>
        public static bool Prepare(Transform lever)
        {
            int id = lever.GetInstanceID();
            if (id == leverId) return Ready && !broken;
            leverId = id;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Ready = broken = haveWritten = false;
            mismatchFor = matchFor = worstMismatch = 0f;
            if (!CollectShape(lever))
            {
                Plugin.L.LogWarning($"Lever: '{lever.name}' has no mesh to measure; it cannot be grabbed.");
                return false;
            }
            if (!Measure())
            {
                Plugin.L.LogWarning($"Lever: '{lever.name}' measured {reach:0.000} m from its hinge to its top, not a lever a hand holds; it cannot be grabbed.");
                return false;
            }
            Ready = true;
            Plugin.L.LogInfo($"Lever: '{lever.name}' measured from {source}: {reach:0.000} m from the hinge to the top; the handle's top is " +
                             $"{handleSize.x:0.000} m along the hinge, {handleSize.y:0.000} m across its travel and {handleSize.z:0.000} m high, " +
                             $"its middle {GripRadius:0.000} m from the hinge: held {(Crossbar ? "as a crossbar, knuckles along it" : "upright, like a stick")}, " +
                             $"a bar {BarRadius * 2f:0.000} m thick and {BarHalfLength * 2f:0.000} m long; hinge local x, as the game turns it (Euler(angle, 0, 0)); " +
                             $"measured in {(System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms{(TransitionGuard.Active ? " under the black" : "")}.");
            return true;
        }

        /// <summary>Already measured, so Prepare is cheap.</summary>
        public static bool Measured(Transform lever) => lever.GetInstanceID() == leverId;

        /// <summary>Degrees either side, as far as the game's lever goes.</summary>
        public static float Travel(float maxAngle) => Mathf.Clamp(maxAngle > 1f ? maxAngle : 30f, 10f, 60f);

        public static Frame TrackingFrame(Transform lever, Transform parent)
        {
            CameraDriver.CockpitToTracking(lever.position, parent != null ? parent.rotation : Quaternion.identity, out var p, out var r);
            return new Frame { Pivot = p, Axis = r * Vector3.right, Up = r * upLocal };
        }

        /// <summary>In the cockpit set, for things placed beside it (the coffee holder).</summary>
        public static Frame SetFrame(Transform lever, Transform parent)
        {
            Quaternion r = parent != null ? parent.rotation : Quaternion.identity;
            return new Frame { Pivot = lever.position, Axis = r * Vector3.right, Up = r * upLocal };
        }

        /// <summary>A point's angle about the hinge (deg), its distance from the axis and along it.</summary>
        public static float Around(Frame f, Vector3 p, out float rho, out float along)
        {
            Vector3 v = p - f.Pivot;
            along = Vector3.Dot(v, f.Axis);
            Vector3 q = v - f.Axis * along;
            rho = q.magnitude;
            Vector3 tan = Vector3.Cross(f.Axis, f.Up);
            return Mathf.Atan2(Vector3.Dot(q, tan), Vector3.Dot(q, f.Up)) * Mathf.Rad2Deg;
        }

        /// <summary>Handle at this angle: middle, bar direction, two directions across. No engine calls.</summary>
        public static void Handle(Frame f, float angle, out Vector3 middle, out Vector3 bar, out Vector3 across1, out Vector3 across2)
        {
            float a = angle * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            Vector3 tan = Vector3.Cross(f.Axis, f.Up);
            Vector3 up = f.Up * c + tan * s, travel = tan * c - f.Up * s; // turned about the hinge
            middle = f.Pivot + f.Axis * gripLocal.x + up * Vector3.Dot(gripLocal, upLocal) + travel * Vector3.Dot(gripLocal, tanLocal);
            if (Crossbar) { bar = f.Axis; across1 = up; across2 = travel; }
            else { bar = up; across1 = travel; across2 = f.Axis; }
        }

        public static float Distance(Frame f, float angle, Vector3 p)
        {
            Handle(f, angle, out var middle, out var bar, out _, out _);
            Vector3 d = p - middle;
            return (d - bar * OnBar(d, bar)).magnitude;
        }

        /// <summary>m along the bar from its middle, clamped.</summary>
        public static float Along(Frame f, float angle, Vector3 p)
        {
            Handle(f, angle, out var middle, out var bar, out _, out _);
            return OnBar(p - middle, bar);
        }

        private static float OnBar(Vector3 fromMiddle, Vector3 bar) => Mathf.Clamp(Vector3.Dot(fromMiddle, bar), -BarHalfLength, BarHalfLength);

        /// <summary>Drawn grip in the cockpit set: onBar m from the middle, palm deg about the bar.</summary>
        public static void GripInSet(Transform lever, float onBar, float palm, float thumbSign, out Vector3 point, out Vector3 forward, out Vector3 thumb, out Vector3 across)
        {
            Quaternion r = lever.rotation;
            Vector3 bar = Crossbar ? Vector3.right : upLocal;
            Vector3 p1 = Crossbar ? upLocal : tanLocal, p2 = Crossbar ? tanLocal : Vector3.right;
            float a = palm * Mathf.Deg2Rad;
            point = lever.position + r * (gripLocal + bar * Mathf.Clamp(onBar, -BarHalfLength, BarHalfLength));
            forward = r * (p1 * Mathf.Cos(a) + p2 * Mathf.Sin(a));
            thumb = r * (bar * thumbSign);
            across = r * p1;
        }

        /// <summary>Lever as posed now (cockpit set), for a thrown cup to hit.</summary>
        public static void Shape(Transform lever, out Vector3 pivot, out Vector3 handle, out Vector3 bar)
        {
            // one engine call (runs every frame a cup flies). A mirrored lever uses its rotation instead.
            var m = lever.localToWorldMatrix;
            if (m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20) < 0f)
            {
                Quaternion r = lever.rotation;
                pivot = lever.position;
                handle = pivot + r * gripLocal;
                bar = r * (Crossbar ? Vector3.right : upLocal);
                return;
            }
            float sx = Len(m.m00, m.m10, m.m20), sy = Len(m.m01, m.m11, m.m21), sz = Len(m.m02, m.m12, m.m22);
            Vector3 Turn(Vector3 v) => new Vector3
            {
                x = m.m00 / sx * v.x + m.m01 / sy * v.y + m.m02 / sz * v.z,
                y = m.m10 / sx * v.x + m.m11 / sy * v.y + m.m12 / sz * v.z,
                z = m.m20 / sx * v.x + m.m21 / sy * v.y + m.m22 / sz * v.z,
            };
            pivot = new Vector3 { x = m.m03, y = m.m13, z = m.m23 };
            var g = Turn(gripLocal);
            handle = new Vector3 { x = pivot.x + g.x, y = pivot.y + g.y, z = pivot.z + g.z };
            bar = Turn(Crossbar ? new Vector3 { x = 1f } : upLocal);
        }

        private static float Len(float x, float y, float z) => Math.Max(1e-9f, MathF.Sqrt(x * x + y * y + z * z));

        /// <summary>After the game's update, before rendering.</summary>
        public static void Pose(Transform lever, float angle)
        {
            written = AboutX(angle);
            lever.localRotation = written;
            haveWritten = true;
        }

        /// <summary>Call before Pose. 1 s off-rule leaves the lever to the game, 2 s on-rule takes it back.</summary>
        public static void CheckGamePose(Transform lever, float currentAngle, float dt)
        {
            Quaternion pose = lever.localRotation;
            if (haveWritten && Quaternion.Angle(pose, written) < 0.01f) return;
            float off = Quaternion.Angle(pose, AboutX(currentAngle));
            worstMismatch = Mathf.Max(worstMismatch, off);
            bool follows = off <= 1f;
            if (!broken)
            {
                mismatchFor = follows ? 0f : mismatchFor + dt;
                if (mismatchFor < MismatchTime) return;
                broken = true;
                matchFor = 0f;
                Plugin.L.LogWarning($"Lever: the game poses its lever otherwise than Euler(currentAngle, 0, 0) ({off:0.0} degrees off at angle {currentAngle:0.0}); it is left to the game.");
                return;
            }
            matchFor = follows ? matchFor + dt : 0f;
            if (matchFor < 2f * MismatchTime) return;
            broken = false;
            mismatchFor = 0f;
            Plugin.L.LogInfo("Lever: the game's poses follow Euler(currentAngle, 0, 0) again; the lever can be grabbed.");
        }

        /// <summary>Which way the handle's top goes for this sign, as the driver sees it. For the log.</summary>
        public static string Way(Transform lever, float sign)
        {
            var cam = CameraDriver.CockpitCamera;
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            Transform parent = lever.parent, wheel = vi != null ? vi.steeringWheel : null;
            if (cam == null || parent == null || wheel == null) return sign > 0f ? "toward its local +z" : "toward its local -z";
            Vector3 tan = parent.rotation * tanLocal * Mathf.Sign(sign);
            Vector3 up = parent.up, ahead = Vector3.ProjectOnPlane(wheel.position - cam.transform.position, up).normalized, side = Vector3.Cross(up, ahead);
            float f = Vector3.Dot(tan, ahead), u = Vector3.Dot(tan, up), r = Vector3.Dot(tan, side);
            if (Mathf.Abs(f) >= Mathf.Abs(u) && Mathf.Abs(f) >= Mathf.Abs(r)) return f > 0f ? "forward, toward the windscreen" : "back, toward you";
            if (Mathf.Abs(u) >= Mathf.Abs(r)) return u > 0f ? "up" : "down";
            return r > 0f ? "to the right" : "to the left";
        }

        public static string Describe() =>
            Ready ? $"handle {(Crossbar ? "crossbar" : "upright")} {BarHalfLength * 200f:0.0} cm long, {BarRadius * 200f:0.0} cm thick, {GripRadius * 100f:0.0} cm from the hinge" +
                    $" (from {source}){(broken ? "; LEFT TO THE GAME: its poses do not follow Euler(angle, 0, 0)" : "")}; game poses checked, worst {worstMismatch:0.0} deg off"
                  : "lever not measured";

        /// <summary>Quaternion.Euler(angle, 0, 0) by hand.</summary>
        private static Quaternion AboutX(float angle)
        {
            float h = angle * 0.5f * Mathf.Deg2Rad;
            return new Quaternion(Mathf.Sin(h), 0f, 0f, Mathf.Cos(h));
        }

        // ---------------------------------------------------------------- measuring

        /// <summary>Vertices in the lever's own frame, m from the pivot.</summary>
        private static bool CollectShape(Transform lever)
        {
            vertices.Clear();
            bool readable = false;
            var filters = lever.GetComponentsInChildren<MeshFilter>(true);
            Quaternion toLever = Quaternion.Inverse(lever.rotation);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                // part pose relative to the lever, plain math: one engine call per part
                Transform t = mf.transform;
                Quaternion q = toLever * t.rotation;
                Vector3 origin = toLever * (t.position - lever.position);
                Vector3 s = t.lossyScale;
                if (mesh.isReadable)
                {
                    var v = mesh.vertices;
                    int step = Math.Max(1, v.Length / MaxVertices);
                    for (int k = 0; k < v.Length; k += step) vertices.Add(origin + q * new Vector3(v[k].x * s.x, v[k].y * s.y, v[k].z * s.z));
                    readable = true;
                    continue;
                }
                var b = mesh.bounds;
                Vector3 c = b.center, e = b.extents;
                for (int k = 0; k < 8; k++)
                    vertices.Add(origin + q * new Vector3((c.x + ((k & 1) == 0 ? -e.x : e.x)) * s.x, (c.y + ((k & 2) == 0 ? -e.y : e.y)) * s.y, (c.z + ((k & 4) == 0 ? -e.z : e.z)) * s.z));
            }
            source = readable ? $"{vertices.Count} vertices" : "the mesh's bounds";
            return vertices.Count > 0;
        }

        /// <summary>Handle direction from the farthest vertices, then the size of the top part.</summary>
        private static bool Measure()
        {
            reach = 0f;
            for (int i = 0; i < vertices.Count; i++) reach = Mathf.Max(reach, Radial(vertices[i]).magnitude);
            if (reach < 0.03f || reach > 0.4f) return false;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 r = Radial(vertices[i]);
                if (r.magnitude > 0.7f * reach) sum += r;
            }
            upLocal = sum.sqrMagnitude > 1e-8f ? sum.normalized : Vector3.up;
            tanLocal = Vector3.Cross(Vector3.right, upLocal);
            // top part extent: along hinge (x), across travel (y), along handle (z)
            float x0 = float.MaxValue, x1 = float.MinValue, t0 = float.MaxValue, t1 = float.MinValue, h0 = float.MaxValue, h1 = float.MinValue;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 p = vertices[i];
                float h = Vector3.Dot(p, upLocal);
                if (h < 0.6f * reach) continue;
                float t = Vector3.Dot(p, tanLocal);
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                t0 = Mathf.Min(t0, t); t1 = Mathf.Max(t1, t);
                h0 = Mathf.Min(h0, h); h1 = Mathf.Max(h1, h);
            }
            if (x0 > x1) return false;
            handleSize = new Vector3(x1 - x0, t1 - t0, h1 - h0);
            float mx = 0.5f * (x0 + x1), mt = 0.5f * (t0 + t1), mh = 0.5f * (h0 + h1);
            gripLocal = Vector3.right * mx + tanLocal * mt + upLocal * mh;
            GripRadius = Mathf.Sqrt(mt * mt + mh * mh);
            // a hand closes around the narrow side
            Crossbar = handleSize.x >= 1.4f * handleSize.y && handleSize.x >= 0.03f;
            float length = Crossbar ? handleSize.x : handleSize.z;
            float thick = Crossbar ? Mathf.Min(handleSize.y, handleSize.z) : 0.5f * (handleSize.x + handleSize.y);
            BarHalfLength = Mathf.Clamp(0.5f * length, 0.01f, 0.08f);
            BarRadius = Mathf.Clamp(0.5f * thick, 0.008f, 0.03f);
            return GripRadius >= 0.03f;
        }

        private static Vector3 Radial(Vector3 p) => new Vector3(0f, p.y, p.z);
    }
}
