using System;
using System.Collections.Generic;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// The HOVA's wheel model, measured since IL2CPP bodies can't be read: rim from the mesh, axis and gain from the
    /// game's own poses. Angles are clockwise for the driver, positive steers right.
    /// </summary>
    internal static class WheelModel
    {
        /// <summary>Tracking space: hub centre, axis toward the driver, 12 o'clock at rest.</summary>
        public struct Frame
        {
            public Vector3 Centre, Axis, Ref;
        }

        public static bool Ready { get; private set; }
        /// <summary>The game's steering has shown the axis and gain.</summary>
        public static bool Learned => learnedApplied;
        /// <summary>Rim and ring radius, m (the cockpit set is at world scale).</summary>
        public static float RimRadius { get; private set; }
        public static float TubeRadius { get; private set; }

        // a right turn gives a negative currentAngle with the wheel going clockwise: -1 until learned
        private const float DefaultGain = -1f;
        private const int LearnSamples = 15;
        private const int MaxVertices = 8000;

        private static int wheelId;
        private static Quaternion rest = Quaternion.identity;
        private static Vector3 axisLocal = Vector3.forward, refLocal = Vector3.up;
        private static float gain = DefaultGain, hubAlong, rimLocal, tubeLocal, metersPerUnit = 1f;
        private static readonly List<Vector3> vertices = new List<Vector3>();
        private static readonly bool[] spokeBins = new bool[180]; // 2-deg bins from 6 o'clock where a spoke meets the rim
        private static readonly List<int> edges = new List<int>();     // index pairs into vertices
        private const float HandHalfWidth = 10f; // deg of rim either side of a grip
        private static Vector3 boundsMin, boundsMax;
        private static string axisSource = "", rimSource = "", rimProfile = "";

        // learned from the game's poses
        private static Vector3 learnedAxisSum;
        private static float learnedRatioSum, steerVotes;
        private static int learnedCount;
        private static bool learned, learnedApplied;
        private static bool haveWritten;
        private static Quaternion written;

        /// <summary>Measures a new wheel. False if there's nothing to measure.</summary>
        public static bool Prepare(Transform wheel, float currentAngle)
        {
            int id = wheel.GetInstanceID();
            if (id == wheelId) return Ready;
            wheelId = id;
            learnedAxisSum = Vector3.zero;
            learnedRatioSum = steerVotes = 0f;
            learnedCount = 0;
            learned = learnedApplied = haveWritten = false;
            gain = DefaultGain;
            Ready = false;
            if (!CollectShape(wheel))
            {
                Plugin.L.LogWarning("Wheel: the HOVA's wheel has no mesh to measure; it cannot be grabbed.");
                return false;
            }
            Vector3 size = boundsMax - boundsMin;
            axisLocal = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
            axisSource = "the mesh's thinnest side";
            if (!AxisFacesDriver(wheel)) axisLocal = -axisLocal;
            // current pose minus the game's angle
            rest = wheel.localRotation * Quaternion.Inverse(Quaternion.AngleAxis(gain * currentAngle, axisLocal));
            Vector3 s = wheel.lossyScale;
            metersPerUnit = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            SetReference(wheel);
            MeasureRim();
            Ready = true;
            Plugin.L.LogInfo($"Wheel: '{wheel.name}' measured from {rimSource}: rim radius {RimRadius:0.000} m, ring {TubeRadius * 2f:0.000} m thick, " +
                             $"{hubAlong * metersPerUnit:0.000} m along the axis; axis local {axisLocal} from {axisSource}; {SpokeDegrees()} degrees of rim met by spokes; {rimProfile}.");
            return true;
        }

        /// <summary>Until learned, the axis faces the cockpit camera. True if it had to be flipped.</summary>
        public static bool FaceDriver(Transform wheel)
        {
            if (learnedApplied || AxisFacesDriver(wheel)) return false;
            axisLocal = -axisLocal;
            SetReference(wheel);
            MeasureRim();
            Plugin.L.LogWarning("Wheel: the axis pointed away from the cockpit camera; turned toward the driver.");
            return true;
        }

        private static bool AxisFacesDriver(Transform wheel)
        {
            var cam = CameraDriver.CockpitCamera;
            return cam == null || Vector3.Dot(wheel.TransformDirection(axisLocal), cam.transform.position - wheel.position) >= 0f;
        }

        /// <summary>Moves a drawn grip toward 12 o'clock to clear the spokes (the HOVA's bar sits at quarter to three).</summary>
        public static float SpokeClearance(float angle)
        {
            float moved = 0f, toward = angle > 0f ? -1f : 1f;
            for (int step = 0; step < 60 && OnSpoke(angle + moved); step++) moved += toward;
            return moved;
        }

        private static int SpokeDegrees()
        {
            int n = 0;
            for (int b = 0; b < spokeBins.Length; b++) if (spokeBins[b]) n += 2;
            return n;
        }

        private static bool OnSpoke(float angle)
        {
            for (int b = 0; b < spokeBins.Length; b++)
                if (spokeBins[b] && Mathf.Abs(Mathf.DeltaAngle(b * 2f - 179f, angle)) < HandHalfWidth) return true;
            return false;
        }

        /// <summary>The game's currentAngle in this class's convention.</summary>
        public static float GameAngle(float currentAngle) => gain * currentAngle;

        public static Frame TrackingFrame(Transform wheel)
        {
            Transform parent = wheel.parent;
            Quaternion restRot = (parent != null ? parent.rotation : Quaternion.identity) * rest;
            Vector3 centre = wheel.position + restRot * axisLocal * (hubAlong * metersPerUnit);
            CameraDriver.CockpitToTracking(centre, restRot, out var p, out var r);
            return new Frame { Centre = p, Axis = r * axisLocal, Ref = r * refLocal };
        }

        /// <summary>Rim point at this angle from 12 o'clock, as posed now (cockpit set).</summary>
        public static void RimPoint(Transform wheel, float angle, out Vector3 point, out Vector3 outward, out Vector3 axis)
        {
            Vector3 radial = Quaternion.AngleAxis(angle, axisLocal) * refLocal;
            point = wheel.TransformPoint(axisLocal * hubAlong + radial * rimLocal);
            outward = wheel.TransformDirection(radial);
            axis = wheel.TransformDirection(axisLocal);
        }

        /// <summary>Wheel as posed now (cockpit set), for a thrown cup to hit.</summary>
        public static void Shape(Transform wheel, out Vector3 hub, out Vector3 axis, out Vector3 twelve, out Vector3 three)
        {
            // one engine call (runs every frame a cup flies). A mirrored wheel goes through the engine.
            var m = wheel.localToWorldMatrix;
            if (Det(m) < 0f)
            {
                hub = wheel.TransformPoint(axisLocal * hubAlong);
                axis = wheel.TransformDirection(axisLocal);
                twelve = wheel.TransformDirection(refLocal);
                three = wheel.TransformDirection(Vector3.Cross(axisLocal, refLocal));
                return;
            }
            float hx = axisLocal.x * hubAlong, hy = axisLocal.y * hubAlong, hz = axisLocal.z * hubAlong;
            hub = new Vector3 { x = m.m00 * hx + m.m01 * hy + m.m02 * hz + m.m03, y = m.m10 * hx + m.m11 * hy + m.m12 * hz + m.m13, z = m.m20 * hx + m.m21 * hy + m.m22 * hz + m.m23 };
            axis = Direction(m, axisLocal);
            twelve = Direction(m, refLocal);
            Vector3 a = axisLocal, r = refLocal;
            three = Direction(m, new Vector3 { x = a.y * r.z - a.z * r.y, y = a.z * r.x - a.x * r.z, z = a.x * r.y - a.y * r.x });
        }

        private static float Det(Matrix4x4 m) =>
            m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

        private static Vector3 Direction(Matrix4x4 m, Vector3 d)
        {
            float x = m.m00 * d.x + m.m01 * d.y + m.m02 * d.z, y = m.m10 * d.x + m.m11 * d.y + m.m12 * d.z, z = m.m20 * d.x + m.m21 * d.y + m.m22 * d.z;
            float len = MathF.Sqrt(x * x + y * y + z * z);
            return len > 1e-9f ? new Vector3 { x = x / len, y = y / len, z = z / len } : d;
        }

        public static int SpokeCount => spokeCount;
        public static void Spoke(int i, out float middle, out float halfWidth) { middle = spokeMiddle[i]; halfWidth = spokeHalf[i]; }

        private const int MaxSpokes = 8;
        private static readonly float[] spokeMiddle = new float[MaxSpokes], spokeHalf = new float[MaxSpokes];
        private static int spokeCount;

        /// <summary>Runs of spoke bins around the circle become spokes.</summary>
        private static void FindSpokes()
        {
            spokeCount = 0;
            int bins = spokeBins.Length, start = -1;
            for (int b = 0; b < bins; b++) if (!spokeBins[b]) { start = b; break; }
            if (start < 0) return; // all of the rim met: no gaps to tell spokes apart
            for (int k = 1; k <= bins && spokeCount < MaxSpokes; k++)
            {
                int b = (start + k) % bins;
                if (!spokeBins[b]) continue;
                int length = 0;
                while (length < bins && spokeBins[(b + length) % bins]) length++;
                spokeMiddle[spokeCount] = Mathf.DeltaAngle(0f, (b + 0.5f * (length - 1)) * 2f - 179f);
                spokeHalf[spokeCount] = length; // 2-degree bins: as many degrees either side as bins
                spokeCount++;
                k += length - 1;
            }
        }

        /// <summary>After the game's update, before rendering.</summary>
        public static void Pose(Transform wheel, float angle)
        {
            written = rest * Quaternion.AngleAxis(angle, axisLocal);
            wheel.localRotation = written;
            haveWritten = true;
        }

        /// <summary>
        /// Call before Pose: rest pose at 0, axis and gain at an angle. Skips our own last write (the game may only
        /// write when its angle changes).
        /// </summary>
        public static void Learn(Transform wheel, float currentAngle, float steerInput, bool idle)
        {
            Quaternion pose = wheel.localRotation;
            if (haveWritten && Quaternion.Angle(pose, written) < 0.01f) return;
            if (Mathf.Abs(currentAngle) < 0.25f)
            {
                if (idle) rest = pose;
                return;
            }
            if (learned || Mathf.Abs(currentAngle) < 4f) return;
            AxisAngle(Quaternion.Inverse(rest) * pose, out float angle, out Vector3 axis);
            if (angle < 1f) return;
            Vector3 sample = axis * Mathf.Sign(currentAngle); // the axis turned about for a positive currentAngle
            if (learnedCount > 0 && Vector3.Dot(sample, learnedAxisSum.normalized) < 0.95f)
            {
                learnedAxisSum = Vector3.zero; // poses that disagree: the rest pose was not one, start over
                learnedRatioSum = 0f;
                learnedCount = 0;
                return;
            }
            learnedAxisSum += sample;
            learnedRatioSum += angle / Mathf.Abs(currentAngle);
            // sign of the game's angle for a right turn
            if (Mathf.Abs(steerInput) > 0.2f) steerVotes += Mathf.Sign(currentAngle * steerInput);
            if (++learnedCount >= LearnSamples && Mathf.Abs(steerVotes) >= LearnSamples / 2) learned = true;
        }

        /// <summary>Only while nobody holds the wheel, or the frame moves under the hands.</summary>
        public static void ApplyLearned(Transform wheel)
        {
            if (!learned || learnedApplied) return;
            learnedApplied = true;
            // a right turn is clockwise for the driver, so gameAxis * rightSign points at the driver
            Vector3 gameAxis = learnedAxisSum.normalized;
            float rightSign = Mathf.Sign(steerVotes);
            Vector3 towardDriver = gameAxis * rightSign;
            float ratio = learnedRatioSum / learnedCount;
            float offBy = Vector3.Angle(towardDriver, axisLocal);
            gain = ratio * rightSign;
            if (offBy > 90f)
                Plugin.L.LogWarning($"Wheel: the game's steering turns the wheel the other way round than the geometric guess ({offBy:0} degrees); the game's steering wins.");
            if (Mathf.Sign(gain) != Mathf.Sign(DefaultGain))
                Plugin.L.LogWarning($"Wheel: the game's angle goes the other way than seen in testing ({gain:0.00} degree per degree, {DefaultGain:0} expected).");
            if (offBy > 2f)
            {
                axisLocal = towardDriver;
                SetReference(wheel);
                MeasureRim();
            }
            axisSource = "the game's own steering";
            Plugin.L.LogInfo($"Wheel: a right turn turns the game's wheel about local {towardDriver} ({offBy:0.0} degrees from the guess), " +
                             $"{gain:0.00} degree per degree of its angle; rim radius {RimRadius:0.000} m.");
        }

        public static string Describe() =>
            Ready ? $"axis local {axisLocal} from {axisSource}, {gain:0.00} deg per game deg, rim {RimRadius:0.000} m from {rimSource}, ring {TubeRadius * 2f:0.000} m, {SpokeDegrees()} degrees of rim met by spokes"
                  : "wheel not measured";

        // ---------------------------------------------------------------- measuring

        /// <summary>12 o'clock at rest: toward the top of the cockpit set.</summary>
        private static void SetReference(Transform wheel)
        {
            Transform parent = wheel.parent;
            Quaternion toRest = Quaternion.Inverse((parent != null ? parent.rotation : Quaternion.identity) * rest);
            Vector3 up = Vector3.ProjectOnPlane(toRest * Vector3.up, axisLocal);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.ProjectOnPlane(toRest * Vector3.forward, axisLocal);
            refLocal = up.normalized;
        }

        /// <summary>Vertices and bounds in the wheel's own space.</summary>
        private static bool CollectShape(Transform wheel)
        {
            vertices.Clear();
            edges.Clear();
            boundsMin = Vector3.one * float.MaxValue;
            boundsMax = Vector3.one * float.MinValue;
            bool any = false;
            var filters = wheel.GetComponentsInChildren<MeshFilter>(true);
            Quaternion toWheel = Quaternion.Inverse(wheel.rotation);
            Vector3 wheelScale = wheel.lossyScale;
            float unit = Mathf.Max(1e-6f, (Mathf.Abs(wheelScale.x) + Mathf.Abs(wheelScale.y) + Mathf.Abs(wheelScale.z)) / 3f);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                // part pose relative to the wheel, plain math: avoids thousands of engine calls
                Transform t = mf.transform;
                Quaternion q = toWheel * t.rotation;
                Vector3 origin = wheel.InverseTransformPoint(t.position);
                Vector3 s = t.lossyScale * (1f / unit);
                var b = mesh.bounds;
                Vector3 c = b.center, e = b.extents;
                for (int k = 0; k < 8; k++)
                {
                    var p = Local(q, origin, s, c.x + ((k & 1) == 0 ? -e.x : e.x), c.y + ((k & 2) == 0 ? -e.y : e.y), c.z + ((k & 4) == 0 ? -e.z : e.z));
                    boundsMin = new Vector3(Mathf.Min(boundsMin.x, p.x), Mathf.Min(boundsMin.y, p.y), Mathf.Min(boundsMin.z, p.z));
                    boundsMax = new Vector3(Mathf.Max(boundsMax.x, p.x), Mathf.Max(boundsMax.y, p.y), Mathf.Max(boundsMax.z, p.z));
                }
                any = true;
                if (!mesh.isReadable) continue;
                var v = mesh.vertices;
                int step = Math.Max(1, v.Length / MaxVertices);
                int first = vertices.Count;
                for (int k = 0; k < v.Length; k += step) vertices.Add(Local(q, origin, s, v[k].x, v[k].y, v[k].z));
                if (step != 1) continue;
                // edges too: a greedy-meshed bar only has vertices at its ends
                var tris = mesh.triangles;
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    edges.Add(first + tris[k]); edges.Add(first + tris[k + 1]);
                    edges.Add(first + tris[k + 1]); edges.Add(first + tris[k + 2]);
                    edges.Add(first + tris[k + 2]); edges.Add(first + tris[k]);
                }
            }
            return any;
        }

        private static Vector3 Local(Quaternion q, Vector3 origin, Vector3 s, float x, float y, float z)
        {
            x *= s.x; y *= s.y; z *= s.z;
            float tx = 2f * (q.y * z - q.z * y), ty = 2f * (q.z * x - q.x * z), tz = 2f * (q.x * y - q.y * x);
            return new Vector3(origin.x + x + q.w * tx + (q.y * tz - q.z * ty),
                               origin.y + y + q.w * ty + (q.z * tx - q.x * tz),
                               origin.z + z + q.w * tz + (q.x * ty - q.y * tx));
        }

        /// <summary>Outer edge = farthest vertex, inner edge = median per sector (skips spokes). Falls back to bounds.</summary>
        private static void MeasureRim()
        {
            Vector3 a = axisLocal, u = refLocal, w = Vector3.Cross(axisLocal, refLocal);
            if (vertices.Count >= 64)
            {
                float outer = 0f;
                for (int i = 0; i < vertices.Count; i++) outer = Mathf.Max(outer, Radial(vertices[i], a).magnitude);
                const int Sectors = 24;
                var inner = new float[Sectors];
                for (int k = 0; k < Sectors; k++) inner[k] = outer;
                float zMin = float.MaxValue, zMax = float.MinValue;
                for (int i = 0; i < vertices.Count; i++)
                {
                    Vector3 p = vertices[i];
                    Vector3 q = Radial(p, a);
                    float rho = q.magnitude;
                    if (rho > 0.9f * outer)
                    {
                        float z = Vector3.Dot(p, a);
                        zMin = Mathf.Min(zMin, z);
                        zMax = Mathf.Max(zMax, z);
                    }
                    if (rho < 0.55f * outer) continue;
                    float turn = Mathf.Atan2(Vector3.Dot(q, w), Vector3.Dot(q, u));
                    int sector = Mathf.Clamp((int)((turn + Mathf.PI) / (2f * Mathf.PI) * Sectors), 0, Sectors - 1);
                    inner[sector] = Mathf.Min(inner[sector], rho);
                }
                var sorted = (float[])inner.Clone();
                Array.Sort(sorted);
                float innerEdge = sorted[Sectors / 2];
                // spokes: mesh just inside the ring, sampled every 1 cm along edges
                Array.Clear(spokeBins, 0, spokeBins.Length);
                float sampleStep = 0.01f / Mathf.Max(metersPerUnit, 1e-6f), bandTop = innerEdge - sampleStep;
                for (int e = 0; e + 1 < edges.Count; e += 2)
                {
                    Vector3 a0 = vertices[edges[e]], b0 = vertices[edges[e + 1]];
                    int n = Mathf.Clamp(Mathf.CeilToInt((b0 - a0).magnitude / sampleStep), 1, 100);
                    for (int k = 0; k <= n; k++)
                    {
                        Vector3 q = Radial(a0 + (b0 - a0) * ((float)k / n), a);
                        float rho = q.magnitude;
                        if (rho < 0.6f * outer || rho > bandTop) continue;
                        float turn = Mathf.Atan2(Vector3.Dot(q, w), Vector3.Dot(q, u)) * Mathf.Rad2Deg;
                        spokeBins[Mathf.Clamp((int)((turn + 180f) * 0.5f), 0, spokeBins.Length - 1)] = true;
                    }
                }
                var profile = new System.Text.StringBuilder();
                for (int k = 0; k < Sectors; k++) profile.Append(' ').Append((inner[k] / outer).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                rimProfile = "inner edge by 15-degree sector from 6 o'clock, over the outer radius:" + profile;
                rimLocal = 0.5f * (outer + innerEdge);
                tubeLocal = 0.25f * ((outer - innerEdge) + (zMax - zMin));
                hubAlong = 0.5f * (zMin + zMax);
                rimSource = $"{vertices.Count} vertices";
            }
            else
            {
                Vector3 half = (boundsMax - boundsMin) * 0.5f;
                float ax = Mathf.Abs(a.x), ay = Mathf.Abs(a.y), az = Mathf.Abs(a.z);
                float outer = ax >= ay && ax >= az ? Mathf.Max(half.y, half.z) : ay >= az ? Mathf.Max(half.x, half.z) : Mathf.Max(half.x, half.y);
                tubeLocal = 0.085f * outer;
                rimLocal = outer - tubeLocal;
                hubAlong = Vector3.Dot((boundsMin + boundsMax) * 0.5f, a);
                rimSource = "the mesh's bounds";
                Array.Clear(spokeBins, 0, spokeBins.Length);
            }
            // clamp to a human-sized wheel
            RimRadius = Mathf.Clamp(rimLocal * metersPerUnit, 0.1f, 0.35f);
            TubeRadius = Mathf.Clamp(tubeLocal * metersPerUnit, 0.008f, 0.035f);
            rimLocal = RimRadius / metersPerUnit;
            tubeLocal = TubeRadius / metersPerUnit;
            FindSpokes();
        }

        private static Vector3 Radial(Vector3 p, Vector3 axis) => p - axis * Vector3.Dot(p, axis);

        /// <summary>Angle (0-180) and axis by hand, since Quaternion.ToAngleAxis calls into the engine.</summary>
        private static void AxisAngle(Quaternion q, out float angle, out Vector3 axis)
        {
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            float w = Mathf.Clamp(q.w, -1f, 1f);
            angle = 2f * Mathf.Acos(w) * Mathf.Rad2Deg;
            float s = Mathf.Sqrt(Mathf.Max(0f, 1f - w * w));
            axis = s < 1e-5f ? Vector3.forward : new Vector3(q.x / s, q.y / s, q.z / s);
        }
    }
}
