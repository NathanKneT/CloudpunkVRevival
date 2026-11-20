using System;
using System.Collections.Generic;
using System.Globalization;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using UnityEngine;
using N = System.Numerics;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Where the cup holder goes: the HOVA has none, so we search the console right of the lever for a flat, clear,
    /// reachable spot away from the lever, wheel and resting hand (trays win). Also feeds CabinShape for a thrown cup.
    /// </summary>
    internal static class CoffeeMount
    {
        // spots tried, m from the resting eyes (right, up, ahead); past 0.47 m right only a tray counts (passenger seat)
        private static readonly float[] Rights = { 0.32f, 0.35f, 0.38f, 0.41f, 0.44f, 0.47f, 0.50f, 0.53f, 0.56f };
        private const float SeatSide = 0.465f;
        private const float AheadFrom = -0.06f, AheadTo = 0.38f, AheadStep = 0.04f;
        private const float RayTop = -0.24f, Lowest = -0.68f, Highest = -0.36f;
        private const float Flat = 0.005f, Probe = 0.025f;     // 8 probes around must agree within 5 mm
        private const float Clearance = 0.14f;                 // free height over the spot, cup plus hand
        private const float LeverMin = 0.15f, LeverWanted = 0.18f, WheelMin = 0.12f, RestMin = 0.10f, Reach = 0.62f;
        private const float Recess = 0.07f, WallMin = 0.012f, WallMax = 0.14f; // tray wall distance and height
        /// <summary>Right shoulder from the resting eyes.</summary>
        public static readonly N.Vector3 Shoulder = new N.Vector3(0.17f, -0.20f, -0.10f);
        // resting right hand: on the rim at 3 o'clock, or low in front
        private static readonly N.Vector3[] RestingHand = { new N.Vector3(0.19f, -0.31f, 0.46f), new N.Vector3(0.16f, -0.48f, 0.28f) };
        private static readonly N.Vector2 BesideLever = new N.Vector2(0.13f, -0.09f); // right of the lever's foot, a bit back
        private static readonly N.Vector2 NoLever = new N.Vector2(0.41f, 0.21f);
        private const float NoLeverHeight = -0.50f;

        public static Transform Parent { get; private set; }
        public static Vector3 SeatLocal { get; private set; }   // holder foot, in Parent's space
        public static Vector3 UpLocal { get; private set; }     // HOVA up, in Parent's space
        public static Vector3 AheadLocal { get; private set; }  // HOVA forward, in Parent's space
        public static Vector3 EyeLocal { get; private set; }    // resting eyes, in Parent's space
        public static string Source { get; private set; } = "not placed";
        public static Color ConsoleColor { get; private set; }
        public static bool HaveColor { get; private set; }
        public static float LeverGap { get; private set; } = float.PositiveInfinity;   // cup axis to lever handle
        public static float WheelGap { get; private set; } = float.PositiveInfinity;   // cup axis to wheel rim
        public static float ShoulderGap { get; private set; }
        private static Vector3 offset;                          // seat from the eyes: right, up, ahead

        /// <summary>Holder foot from the resting eyes: holder frame to CabinShape's.</summary>
        public static N.Vector3 SeatFromEyes => new N.Vector3(offset.x, offset.y, offset.z);
        private static string colorNote = "", leverNote = "", wheelNote = "", tried = "", summary = "";
        private static bool meshesUnreadable;

        /// <summary>"coffee at" command: holder foot from the eyes, skips the search.</summary>
        public static Vector3? Manual;

        public static bool Valid => Parent != null;
        /// <summary>A spot was found or set. No spot, no cup (better than one inside the console).</summary>
        public static bool HasSpot { get; private set; }
        public static void Forget() => Parent = null;

        /// <summary>True if the game moved the cockpit camera since placing.</summary>
        public static bool EyesMoved()
        {
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eyeNow, out _);
            return (Parent.InverseTransformPoint(eyeNow) - EyeLocal).magnitude > 0.15f;
        }

        public static string Describe() => !Valid ? "not placed yet" : !HasSpot ? "no cup: " + Source : $"holder on {Source}, {offset.x:0.00} m right, {offset.y:0.00} m up, {offset.z:0.00} m ahead of the eyes; " +
              $"{Gap("lever's handle", LeverGap, leverNote)}, {Gap("wheel's rim", WheelGap, wheelNote)}, {ShoulderGap * 100f:0} cm from the right shoulder; {colorNote}";

        private static string Gap(string what, float d, string note) =>
            float.IsPositiveInfinity(d) ? $"no {what} found{note}" : $"{d * 100f:0} cm from the {what}{note}";

        /// <summary>Spots tried in the last search, for the dev command.</summary>
        public static string Tried => tried;

        // ---------------------------------------------------------------- the search

        // search frame: eyes and HOVA axes in the set
        private static N.Vector3 eye, right, up, fwd;
        private static N.Vector3 E(Vector3 p) { var d = V(p) - eye; return new N.Vector3(N.Vector3.Dot(d, right), N.Vector3.Dot(d, up), N.Vector3.Dot(d, fwd)); }
        private static Vector3 W(N.Vector3 e) => U(eye + right * e.X + up * e.Y + fwd * e.Z);

        public static bool Place()
        {
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            if (vi == null || CameraDriver.CockpitCamera == null) return false;
            var parent = vi.interiorLocal != null ? vi.interiorLocal : vi.transform;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            // in the cockpit the anchor is the game's camera: rest eyes and HOVA heading in the set
            CameraDriver.WorldToCockpit(CameraDriver.AnchorPosition, CameraDriver.AnchorRotation, out var eyeU, out var eyeRot);
            Vector3 upU = parent.up;
            Vector3 fwdU = Vector3.ProjectOnPlane(eyeRot * Vector3.forward, upU);
            if (fwdU.sqrMagnitude < 0.25f) fwdU = Vector3.ProjectOnPlane(parent.forward, upU);
            fwdU.Normalize();
            eye = V(eyeU); up = V(upU); fwd = V(fwdU); right = N.Vector3.Cross(up, fwd);

            bool haveLever = Lever(vi, out var handle, out var foot);
            ReadWheel(vi);
            var target = haveLever ? new N.Vector2(foot.X, foot.Z) + BesideLever : NoLever;
            float height = haveLever ? foot.Y : NoLeverHeight;

            N.Vector3 seat = default;
            HasSpot = true;
            bool read = Collect(vi, parent); // console surfaces, plus CabinShape
            if (Manual is Vector3 m)
            {
                seat = new N.Vector3(m.x, m.y, m.z);
                Source = "a spot set by hand";
                tried = summary = "no search (set by hand)";
            }
            else if (Search(read, haveLever, handle, target, height, out seat, out var hit, out int walls))
            {
                Source = walls >= 2 ? $"a tray of the console ('{hit.Name}', walls on {walls} sides)" : $"the console ('{hit.Name}')";
                SampleColor(hit);
            }
            else
            {
                HasSpot = false;
                Source = "no flat spot of the console clear of the lever, the wheel and the hands (\"coffee tried\" lists them; \"coffee at\" places it by hand)";
            }

            var bottom = seat + new N.Vector3(0f, 0.005f, 0f);
            var top = seat + new N.Vector3(0f, CoffeeModel.Height, 0f);
            LeverGap = haveLever ? LeverDistance(handle, bottom, top) : float.PositiveInfinity;
            leverNote = !haveLever ? "" : LeverMeasured
                ? $" over its whole travel, as the lever's model has it (its middle at rest {Fmt(handle)})"
                : $" (the top of its box {Fmt(handle)}: the lever was not measured)";
            WheelGap = WheelDistance(bottom, top);
            ShoulderGap = (seat + new N.Vector3(0f, CoffeeModel.GripHeight, 0f) - Shoulder).Length();

            if (!HasSpot) LeverGap = WheelGap = float.PositiveInfinity;
            else if (read)
            {
                CabinShape.Finish(Inside(seat, haveLever, handle));
                CabinShape.Check(seat, Manual != null);
            }
            WheelT = vi.steeringWheel;
            LeverT = vi.ascensionLever;
            offset = new Vector3(seat.X, seat.Y, seat.Z);
            Parent = parent;
            EyeLocal = parent.InverseTransformPoint(eyeU);
            SeatLocal = parent.InverseTransformPoint(W(seat));
            UpLocal = parent.InverseTransformDirection(upU);
            AheadLocal = parent.InverseTransformDirection(fwdU);
            float took = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Plugin.L.LogInfo($"Coffee: {Describe()}; search: {summary}, {took:0.0} ms{(TransitionGuard.Active ? " under the black" : "")}; cabin for a thrown cup: {CabinShape.Describe()}.");
            if (!HasSpot) Plugin.L.LogWarning("Coffee: no cup this flight.");
            else if (haveLever && LeverGap < LeverMin) Plugin.L.LogWarning($"Coffee: the cup is only {LeverGap * 100f:0} cm from the lever's handle (wanted {LeverMin * 100f:0}).");
            return true;
        }

        private static string Fmt(N.Vector3 e) => string.Format(CultureInfo.InvariantCulture, "({0:0.00}, {1:0.00}, {2:0.00})", e.X, e.Y, e.Z);

        // LeverModel's handle at 5 points of its travel (end pairs, eye frame); empty if not measured
        private static readonly List<N.Vector3> bars = new List<N.Vector3>();

        /// <summary>Placed against LeverModel's handle, not just the lever's box.</summary>
        public static bool LeverMeasured => bars.Count > 0;

        /// <summary>Lever handle and foot, eye frame. Uses LeverModel's swept bar if measured, else the box top.</summary>
        private static bool Lever(VehicleInterior vi, out N.Vector3 handle, out N.Vector3 foot)
        {
            handle = foot = default;
            bars.Clear();
            LeverBoxed = false;
            var lever = vi.ascensionLever;
            if (lever == null) return false;
            if (LeverModel.Ready)
            {
                var frame = LeverModel.SetFrame(lever, lever.parent);
                float travel = LeverModel.Travel(vi.ascensionLeverData.maxAngle);
                for (int k = 0; k <= 4; k++)
                {
                    LeverModel.Handle(frame, -travel + k * travel * 0.5f, out var middle, out var bar, out _, out _);
                    bars.Add(E(middle - bar * LeverModel.BarHalfLength));
                    bars.Add(E(middle + bar * LeverModel.BarHalfLength));
                    if (k == 2) handle = E(middle);
                }
                foot = E(lever.position);
                return true;
            }
            var parts = lever.GetComponentsInChildren<Renderer>(false);
            float minY = float.MaxValue, maxY = float.MinValue;
            N.Vector3 sum = N.Vector3.Zero;
            int n = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var r = parts[i];
                if (r == null || !r.enabled) continue;
                var b = r.bounds;
                Vector3 c = b.center, x = b.extents;
                for (int k = 0; k < 8; k++)
                {
                    var p = E(new Vector3(c.x + ((k & 1) != 0 ? x.x : -x.x), c.y + ((k & 2) != 0 ? x.y : -x.y), c.z + ((k & 4) != 0 ? x.z : -x.z)));
                    minY = MathF.Min(minY, p.Y);
                    maxY = MathF.Max(maxY, p.Y);
                }
                sum += E(c);
                n++;
            }
            if (n == 0)
            {
                foot = E(lever.position);
                handle = foot + new N.Vector3(0f, 0.1f, 0f);
            }
            else
            {
                var mid = sum / n;
                handle = new N.Vector3(mid.X, maxY, mid.Z);
                foot = new N.Vector3(mid.X, minY, mid.Z);
            }
            LeverBoxed = true;
            LeverFoot = foot;
            LeverTop = handle;
            return true;
        }

        /// <summary>Wheel and lever the thrown cup bumps off.</summary>
        public static Transform WheelT { get; private set; }
        public static Transform LeverT { get; private set; }

        /// <summary>Points surely inside the cabin. No pane may have one behind it.</summary>
        private static List<CabinShape.Inside> Inside(N.Vector3 seat, bool haveLever, N.Vector3 handle)
        {
            var list = new List<CabinShape.Inside>
            {
                new CabinShape.Inside { Name = "eyes", At = N.Vector3.Zero },
                new CabinShape.Inside { Name = "mouth", At = new N.Vector3(0f, -0.075f, 0.075f) },
                new CabinShape.Inside { Name = "right shoulder", At = Shoulder },
                new CabinShape.Inside { Name = "left shoulder", At = new N.Vector3(-Shoulder.X, Shoulder.Y, Shoulder.Z) },
                new CabinShape.Inside { Name = "cup over its holder", At = seat + new N.Vector3(0f, 0.15f, 0f) },
            };
            foreach (var rest in RestingHand) list.Add(new CabinShape.Inside { Name = "resting right hand", At = rest });
            if (haveLever) list.Add(new CabinShape.Inside { Name = "lever's handle", At = handle });
            foreach (var p in rim) list.Add(new CabinShape.Inside { Name = "wheel", At = p });
            return list;
        }

        /// <summary>Lever not measured (LeverGrab off): its box foot and top, eye frame.</summary>
        public static bool LeverBoxed { get; private set; }
        public static N.Vector3 LeverFoot { get; private set; }
        public static N.Vector3 LeverTop { get; private set; }

        // wheel rim points every 10 degrees (eye frame); until WheelModel is ready, its box as a ball
        private static readonly List<N.Vector3> rim = new List<N.Vector3>();
        private static float rimMargin;

        private static void ReadWheel(VehicleInterior vi)
        {
            rim.Clear();
            wheelNote = "";
            var wheel = vi.steeringWheel;
            if (wheel == null) return;
            if (WheelModel.Ready)
            {
                for (int a = 0; a < 360; a += 10)
                {
                    WheelModel.RimPoint(wheel, a, out var p, out _, out _);
                    rim.Add(E(p));
                }
                rimMargin = WheelModel.TubeRadius;
                return;
            }
            var parts = wheel.GetComponentsInChildren<Renderer>(false);
            float reach = 0f;
            N.Vector3 sum = N.Vector3.Zero;
            int n = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                var b = parts[i].bounds;
                sum += E(b.center);
                reach = MathF.Max(reach, b.extents.magnitude);
                n++;
            }
            if (n == 0) return;
            rim.Add(sum / n);
            rimMargin = reach;
            wheelNote = " (its box: the wheel is not measured yet)";
        }

        /// <summary>Closest the lever handle gets to the cup's axis over its travel.</summary>
        private static float LeverDistance(N.Vector3 handle, N.Vector3 bottom, N.Vector3 top)
        {
            if (bars.Count == 0) return ToSegment(handle, bottom, top);
            float best = float.PositiveInfinity;
            for (int i = 0; i + 1 < bars.Count; i += 2) best = MathF.Min(best, SegmentToSegment(bottom, top, bars[i], bars[i + 1]));
            return best;
        }

        private static float SegmentToSegment(N.Vector3 p1, N.Vector3 q1, N.Vector3 p2, N.Vector3 q2)
        {
            N.Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = d1.LengthSquared(), e = d2.LengthSquared(), f = N.Vector3.Dot(d2, r), s, t;
            if (a <= 1e-9f && e <= 1e-9f) return r.Length();
            if (a <= 1e-9f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
            else
            {
                float c = N.Vector3.Dot(d1, r);
                if (e <= 1e-9f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else
                {
                    float b = N.Vector3.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-12f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                    else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
                }
            }
            return (p1 + d1 * s - (p2 + d2 * t)).Length();
        }

        /// <summary>Wheel rim to cup axis, minus the rim thickness.</summary>
        private static float WheelDistance(N.Vector3 bottom, N.Vector3 top)
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < rim.Count; i++) best = MathF.Min(best, ToSegment(rim[i], bottom, top) - rimMargin);
            return best;
        }

        private static float ToSegment(N.Vector3 p, N.Vector3 a, N.Vector3 b)
        {
            var ab = b - a;
            float t = Math.Clamp(N.Vector3.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-8f), 0f, 1f);
            return (p - (a + ab * t)).Length();
        }

        // ---------------------------------------------------------------- the console's surfaces

        /// <summary>A level triangle in the eye frame, with its source mesh.</summary>
        private struct Tri
        {
            public float Ax, Az, Bx, Bz, Cx, Cz, Ay, By, Cy;
            public bool Up;
            public int Source, Index;
        }

        private sealed class MeshData
        {
            public string Name;
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public int[] Triangles;
        }

        public struct Hit
        {
            public string Name;
            public int Source, Index;
            public float U, V; // barycentric weights of corners 2 and 3
        }

        private const float GridX0 = 0.20f, GridZ0 = -0.16f, Cell = 0.02f;
        private const int GridW = 25, GridH = 34;
        private static readonly List<Tri> tris = new List<Tri>();
        private static readonly List<MeshData> sources = new List<MeshData>();
        private static List<int>[] cells;

        /// <summary>Reads the set's triangles: level ones near the console into the search grid, all of them into CabinShape.</summary>
        private static bool Collect(VehicleInterior vi, Transform parent)
        {
            tris.Clear();
            sources.Clear();
            pieces = 0;
            CabinShape.Begin();
            if (cells == null) { cells = new List<int>[GridW * GridH]; for (int i = 0; i < cells.Length; i++) cells[i] = new List<int>(); }
            else foreach (var c in cells) c.Clear();
            if (meshesUnreadable) return false;
            var seen = new HashSet<int>();
            var renderers = parent.GetComponentsInChildren<MeshRenderer>(false);
            try
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null || !r.enabled || Moving(vi, r.transform)) continue;
                    Box(r.bounds, out var lo, out var hi);
                    bool near = Near(lo, hi), cabin = InCabin(lo, hi);
                    if (!near && !cabin) continue;
                    bool glassy = Glass(vi, r.transform);
                    if (glassy && !cabin) continue;
                    var filter = r.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : null;
                    if (cabin) Thing(r, mesh, lo, hi);
                    if (mesh == null || !mesh.isReadable || (!glassy && mesh.vertexCount <= 4)) continue;
                    bool world = r.isPartOfStaticBatch; // static batches are already in world space
                    if (!seen.Add(world ? mesh.GetInstanceID() : r.GetInstanceID())) continue;
                    Vector3[] v = mesh.vertices;
                    int[] t = mesh.triangles;
                    var m = world ? Matrix4x4.identity : r.transform.localToWorldMatrix;
                    // negative scale flips the winding: tops would read as bottoms
                    bool mirrored = m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20) < 0f;
                    var e = new N.Vector3[v.Length];
                    for (int k = 0; k < v.Length; k++)
                    {
                        var p = v[k];
                        var w = new N.Vector3(
                            m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
                            m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
                            m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23) - eye;
                        e[k] = new N.Vector3(N.Vector3.Dot(w, right), N.Vector3.Dot(w, up), N.Vector3.Dot(w, fwd));
                    }
                    int source = sources.Count;
                    bool added = false;
                    pieces++;
                    for (int k = 0; k + 2 < t.Length; k += 3)
                    {
                        var a = e[t[k]]; var b = e[t[k + 1]]; var c = e[t[k + 2]];
                        var n = N.Vector3.Cross(b - a, c - a);
                        float len = n.Length();
                        if (len < 1e-10f) continue;
                        if (glassy)
                        {
                            // glass or screen: thin wall on the far side, never a floor (not closed)
                            CabinShape.AddWall(a, b, c, (a + b + c) / 3f);
                            continue;
                        }
                        if (MathF.Abs(n.Y) < 0.7f * len)
                        {
                            // upright: wall, solid behind the face
                            if (cabin) CabinShape.AddWall(a, b, c, mirrored ? n : -n);
                            continue;
                        }
                        bool faceUp = (n.Y > 0f) != mirrored;
                        if (cabin) CabinShape.Add(a, b, c, faceUp, pieces);
                        if (!near) continue;
                        float x0 = MathF.Min(a.X, MathF.Min(b.X, c.X)), x1 = MathF.Max(a.X, MathF.Max(b.X, c.X));
                        float z0 = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), z1 = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                        int i0 = CellX(x0), i1 = CellX(x1), j0 = CellZ(z0), j1 = CellZ(z1);
                        if (i1 < 0 || j1 < 0 || i0 >= GridW || j0 >= GridH) continue;
                        int index = tris.Count;
                        tris.Add(new Tri { Ax = a.X, Az = a.Z, Bx = b.X, Bz = b.Z, Cx = c.X, Cz = c.Z, Ay = a.Y, By = b.Y, Cy = c.Y, Up = faceUp, Source = source, Index = k });
                        for (int gi = Math.Max(i0, 0); gi <= Math.Min(i1, GridW - 1); gi++)
                            for (int gj = Math.Max(j0, 0); gj <= Math.Min(j1, GridH - 1); gj++)
                                cells[gi * GridH + gj].Add(index);
                        added = true;
                    }
                    if (!added) continue;
                    sources.Add(new MeshData { Name = r.name, Renderer = r, Mesh = mesh, Triangles = t });
                }
            }
            catch (Exception ex)
            {
                // mesh reads may be stripped from the game
                meshesUnreadable = true;
                Plugin.L.LogInfo("Coffee: the cockpit's meshes cannot be read (" + ex.Message + ").");
                return false;
            }
            WorldGlass(vi, parent);
            return tris.Count > 0;
        }

        private static int pieces; // mesh id per piece, for CabinShape seams
        private static readonly N.Vector3[] thing = new N.Vector3[256];

        /// <summary>Small set object (under 40 cm) for CabinShape: its vertices if readable, else its box corners.</summary>
        private static void Thing(MeshRenderer r, Mesh mesh, N.Vector3 lo, N.Vector3 hi)
        {
            var size = hi - lo;
            if (MathF.Max(size.X, MathF.Max(size.Y, size.Z)) > 0.4f) return;
            int n = 0;
            if (mesh != null && mesh.isReadable && !r.isPartOfStaticBatch && mesh.vertexCount <= 600)
            {
                Vector3[] v = mesh.vertices;
                var m = r.transform.localToWorldMatrix;
                int step = Math.Max(1, (v.Length + thing.Length - 1) / thing.Length);
                for (int k = 0; k < v.Length && n < thing.Length; k += step)
                {
                    var q = v[k];
                    var w = new N.Vector3(m.m00 * q.x + m.m01 * q.y + m.m02 * q.z + m.m03, m.m10 * q.x + m.m11 * q.y + m.m12 * q.z + m.m13,
                                          m.m20 * q.x + m.m21 * q.y + m.m22 * q.z + m.m23) - eye;
                    thing[n++] = new N.Vector3(N.Vector3.Dot(w, right), N.Vector3.Dot(w, up), N.Vector3.Dot(w, fwd));
                }
            }
            else
                for (int k = 0; k < 8; k++) thing[n++] = new N.Vector3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z);
            CabinShape.AddThing(r.name, thing, n);
        }

        private static int CellX(float x) => (int)MathF.Floor((x - GridX0) / Cell);
        private static int CellZ(float z) => (int)MathF.Floor((z - GridZ0) / Cell);

        /// <summary>Wheel and lever (CoffeeBody handles them as bars) and rain: skipped.</summary>
        private static bool Moving(VehicleInterior vi, Transform t)
        {
            if (vi.ascensionLever != null && t.IsChildOf(vi.ascensionLever)) return true;
            if (vi.steeringWheel != null && t.IsChildOf(vi.steeringWheel)) return true;
            return t.name.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Glass and screens: thin walls, never a surface for the holder.</summary>
        private static bool Glass(VehicleInterior vi, Transform t)
        {
            if (vi.windshieldFrontMesh != null && t == vi.windshieldFrontMesh.transform) return true;
            string n = t.name;
            return n.IndexOf("wind", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------- the windows' glass

        /// <summary>The windows live on the HOVA's world side ("_Windshields"), not in the set: bring them into the eye frame.</summary>
        private static void WorldGlass(VehicleInterior vi, Transform parent)
        {
            var root = vi.windshieldFrontMesh != null ? vi.windshieldFrontMesh.transform.parent : null;
            if (root == null || root.IsChildOf(parent)) return; // none, or already read with the set
            try
            {
                VR.HovaBody.WindowPose(root, out var pos, out var rot);
                var panes = root.GetComponentsInChildren<MeshRenderer>(false);
                for (int i = 0; i < panes.Length; i++)
                {
                    var r = panes[i];
                    if (r == null || !r.enabled || !r.name.StartsWith("Windshield", StringComparison.OrdinalIgnoreCase)) continue;
                    ReadPane(r, root, pos, rot);
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Coffee: the windows' glass could not be read (" + e.Message + "); a thrown cup may leave through them.");
            }
        }

        /// <summary>One window as a pane: its box plane, plus triangles if readable. A non-flat box is bent glass.</summary>
        private static void ReadPane(MeshRenderer r, Transform root, Vector3 pos, Quaternion rot)
        {
            var filter = r.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return;
            bool world = r.isPartOfStaticBatch; // static batch: world space, use the renderer's box
            var b = world ? r.bounds : mesh.bounds;
            Vector3 c = b.center, x = b.extents;
            var corner = new N.Vector3[8];
            for (int k = 0; k < 8; k++)
            {
                var p = new Vector3(c.x + ((k & 1) != 0 ? x.x : -x.x), c.y + ((k & 2) != 0 ? x.y : -x.y), c.z + ((k & 4) != 0 ? x.z : -x.z));
                corner[k] = GlassPoint(world ? p : r.transform.TransformPoint(p), root, pos, rot);
            }
            var centre = (corner[0] + corner[7]) * 0.5f;
            N.Vector3 boxLo = corner[0], boxHi = corner[0];
            for (int k = 1; k < 8; k++) { boxLo = N.Vector3.Min(boxLo, corner[k]); boxHi = N.Vector3.Max(boxHi, corner[k]); }
            N.Vector3 hx = (corner[1] - corner[0]) * 0.5f, hy = (corner[2] - corner[0]) * 0.5f, hz = (corner[4] - corner[0]) * 0.5f;
            // sort axes by length
            N.Vector3 l = hx, p1 = hy, p2 = hz;
            if (p1.LengthSquared() > l.LengthSquared()) (l, p1) = (p1, l);
            if (p2.LengthSquared() > l.LengthSquared()) (l, p2) = (p2, l);
            if (p2.LengthSquared() > p1.LengthSquared()) (p1, p2) = (p2, p1);
            N.Vector3 along = l, across;
            bool bent = p2.Length() >= 0.01f;
            // bent: diagonal of the box with the lower edge farther out
            if (!bent) across = p1;
            else across = Lean(centre, p1 + p2) >= Lean(centre, p1 - p2) ? p1 + p2 : p1 - p2;
            if (along.LengthSquared() < 1e-6f || across.LengthSquared() < 1e-6f) return;
            var normal = N.Vector3.Normalize(N.Vector3.Cross(along, across));
            if (N.Vector3.Dot(normal, centre) < 0f) normal = -normal; // away from the eyes
            bool readable = mesh.isReadable && !world && mesh.vertexCount <= 2000;
            int tag = CabinShape.AddPane(new CabinShape.Pane
            {
                Name = r.name, Where = Where(normal), How = (readable ? "read" : "its box: not readable") + (bent ? ", bent" : ""),
                Centre = centre, Normal = normal, U = N.Vector3.Normalize(along), HalfU = along.Length(), V = N.Vector3.Normalize(across), HalfV = across.Length(),
                Read = readable, Bent = bent && !readable, BoxLo = boxLo, BoxHi = boxHi,
            });
            if (!readable) return;
            Vector3[] v = mesh.vertices;
            int[] t = mesh.triangles;
            var e = new N.Vector3[v.Length];
            for (int k = 0; k < v.Length; k++) e[k] = GlassPoint(r.transform.TransformPoint(v[k]), root, pos, rot);
            for (int k = 0; k + 2 < t.Length; k += 3) CabinShape.AddGlass(e[t[k]], e[t[k + 1]], e[t[k + 2]], tag);
        }

        /// <summary>How much farther out the lower edge (centre +- d) is than the upper.</summary>
        private static float Lean(N.Vector3 centre, N.Vector3 d)
        {
            N.Vector3 a = centre + d, b = centre - d;
            if (a.Y > b.Y) (a, b) = (b, a);
            return new N.Vector2(a.X, a.Z).Length() - new N.Vector2(b.X, b.Z).Length();
        }

        private static string Where(N.Vector3 n) =>
            MathF.Abs(n.Y) > 0.85f ? (n.Y < 0f ? "the floor window" : "the roof window")
            : MathF.Abs(n.X) > MathF.Abs(n.Z) ? (n.X < 0f ? "the left window" : "the right window")
            : n.Z > 0f ? "the windscreen" : "the rear window";

        /// <summary>World-side glass point to the eye frame, where the world camera draws it.</summary>
        private static N.Vector3 GlassPoint(Vector3 p, Transform root, Vector3 pos, Quaternion rot)
        {
            var local = root.InverseTransformPoint(p);
            var s = root.lossyScale;
            var drawn = pos + rot * new Vector3(local.x * s.x, local.y * s.y, local.z * s.z);
            CameraDriver.WorldToCockpit(drawn, Quaternion.identity, out var inSet, out _);
            return E(inSet);
        }

        private static void Box(Bounds b, out N.Vector3 lo, out N.Vector3 hi)
        {
            Vector3 c = b.center, x = b.extents;
            lo = new N.Vector3(float.MaxValue);
            hi = new N.Vector3(float.MinValue);
            for (int k = 0; k < 8; k++)
            {
                var p = E(new Vector3(c.x + ((k & 1) != 0 ? x.x : -x.x), c.y + ((k & 2) != 0 ? x.y : -x.y), c.z + ((k & 4) != 0 ? x.z : -x.z)));
                lo = N.Vector3.Min(lo, p);
                hi = N.Vector3.Max(hi, p);
            }
        }

        private static bool Near(N.Vector3 lo, N.Vector3 hi) =>
            hi.X > GridX0 && lo.X < GridX0 + GridW * Cell && hi.Z > GridZ0 && lo.Z < GridZ0 + GridH * Cell && hi.Y > Lowest - 0.05f && lo.Y < RayTop + Clearance;

        private static bool InCabin(N.Vector3 lo, N.Vector3 hi) =>
            hi.X > CabinShape.X0 && lo.X < CabinShape.X0 + CabinShape.Wide * CabinShape.Cell && hi.Z > CabinShape.Z0 && lo.Z < CabinShape.Z0 + CabinShape.Deep * CabinShape.Cell
            && hi.Y > CabinShape.Low && lo.Y < CabinShape.High;

        /// <summary>Vertical ray at (x, z): first top going down (or underside going up) within limit; height or NaN.</summary>
        private static float Cast(float x, float z, bool down, float fromY, float limit, out Hit hit)
        {
            hit = default;
            int i = CellX(x), j = CellZ(z);
            if (i < 0 || j < 0 || i >= GridW || j >= GridH) return float.NaN;
            float best = float.NaN;
            var list = cells[i * GridH + j];
            for (int k = 0; k < list.Count; k++)
            {
                var t = tris[list[k]];
                if (t.Up != down) continue; // down meets tops, up meets undersides
                float d = (t.Bz - t.Cz) * (t.Ax - t.Cx) + (t.Cx - t.Bx) * (t.Az - t.Cz);
                if (MathF.Abs(d) < 1e-12f) continue;
                float wa = ((t.Bz - t.Cz) * (x - t.Cx) + (t.Cx - t.Bx) * (z - t.Cz)) / d;
                float wb = ((t.Cz - t.Az) * (x - t.Cx) + (t.Ax - t.Cx) * (z - t.Cz)) / d;
                float wc = 1f - wa - wb;
                if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f) continue;
                float y = wa * t.Ay + wb * t.By + wc * t.Cy;
                if (down ? (y > fromY || y < limit || (!float.IsNaN(best) && y <= best)) : (y < fromY || y > limit || (!float.IsNaN(best) && y >= best))) continue;
                best = y;
                hit = new Hit { Name = sources[t.Source].Name, Source = t.Source, Index = t.Index, U = wb, V = wc };
            }
            return best;
        }

        private static readonly float[] ProbeX = { 1f, -1f, 0f, 0f, 1.4f, -1.4f, 1.4f, -1.4f };
        private static readonly float[] ProbeZ = { 0f, 0f, 1f, -1f, 1.4f, 1.4f, -1.4f, -1.4f };

        private static readonly float[] WallX = { 1f, -1f, 0f, 0f }, WallZ = { 0f, 0f, 1f, -1f };

        /// <summary>Walls around a spot, out of 4. A tray has 2 or more.</summary>
        private static int Walls(float x, float z, float y)
        {
            int n = 0;
            for (int k = 0; k < 4; k++)
            {
                float h = Cast(x + WallX[k] * Recess, z + WallZ[k] * Recess, true, RayTop, Lowest, out _);
                if (!float.IsNaN(h) && h - y >= WallMin && h - y <= WallMax) n++;
            }
            return n;
        }

        private static bool Search(bool read, bool haveLever, N.Vector3 handle, N.Vector2 target, float height, out N.Vector3 seat, out Hit hit, out int walls)
        {
            seat = default;
            hit = default;
            walls = 0;
            var log = new System.Text.StringBuilder();
            if (!read) { tried = summary = "no readable surface near the console"; return false; }
            float best = float.MaxValue;
            int flat = 0, passed = 0, trays = 0;
            for (int ri = 0; ri < Rights.Length; ri++)
                for (float a = AheadFrom; a <= AheadTo + 1e-4f; a += AheadStep)
                {
                    float x = Rights[ri];
                    float y = Cast(x, a, true, RayTop, Lowest, out var centre);
                    string why = null;
                    if (float.IsNaN(y)) why = "nothing";
                    else if (y > Highest) why = $"{y:0.00} high";
                    else
                    {
                        for (int p = 0; p < ProbeX.Length && why == null; p++)
                        {
                            float py = Cast(x + ProbeX[p] * Probe, a + ProbeZ[p] * Probe, true, RayTop, Lowest, out _);
                            if (float.IsNaN(py) || MathF.Abs(py - y) > Flat) why = "not flat";
                        }
                        if (why == null) flat++;
                        for (int p = -1; p < 4 && why == null; p++)
                        {
                            float cx = p < 0 ? x : x + ProbeX[p] * 0.03f, cz = p < 0 ? a : a + ProbeZ[p] * 0.03f;
                            if (!float.IsNaN(Cast(cx, cz, false, y + 0.01f, y + Clearance, out _))) why = "covered";
                        }
                    }
                    var bottom = new N.Vector3(x, float.IsNaN(y) ? 0f : y, a);
                    var top = bottom + new N.Vector3(0f, CoffeeModel.Height, 0f);
                    float lever = haveLever ? LeverDistance(handle, bottom, top) : float.PositiveInfinity;
                    if (why == null && lever < LeverMin) why = $"lever {lever * 100f:0} cm";
                    if (why == null)
                        foreach (var rest in RestingHand)
                            if (ToSegment(rest, bottom, top) < RestMin) why = "where the hand rests";
                    if (why == null && WheelDistance(bottom, top) < WheelMin) why = "by the wheel";
                    if (why == null && (bottom + new N.Vector3(0f, CoffeeModel.GripHeight, 0f) - Shoulder).Length() > Reach) why = "out of reach";
                    int sides = why == null ? Walls(x, a, y) : 0;
                    if (why == null && x > SeatSide && sides < 2) why = "past the console, not a tray";
                    if (why != null)
                    {
                        if (why != "nothing") log.Append(string.Format(CultureInfo.InvariantCulture, " ({0:0.00},{1:0.00}) {2};", x, a, why));
                        continue;
                    }
                    passed++;
                    if (sides >= 2) trays++;
                    float dx = x - target.X, dz = a - target.Y, dy = y - height;
                    float score = dx * dx + dz * dz + 0.5f * dy * dy;
                    if (lever < LeverWanted) score += 4f * (LeverWanted - lever) * (LeverWanted - lever);
                    if (sides >= 2) score -= 1f; // a tray beats any open spot
                    log.Append(string.Format(CultureInfo.InvariantCulture, " ({0:0.00},{1:0.00}) ok at {2:0.00} on '{3}', lever {4:0} cm{5};", x, a, y, centre.Name, lever * 100f, sides >= 2 ? $", a tray ({sides} walls)" : ""));
                    if (score >= best) continue;
                    best = score;
                    seat = new N.Vector3(x, y + 0.001f, a);
                    hit = centre;
                    walls = sides;
                }
            summary = $"{passed} of {Rights.Length * (int)MathF.Round((AheadTo - AheadFrom) / AheadStep + 1f)} spots fit ({flat} flat, {trays} in a tray), {tris.Count} surfaces near the console";
            tried = summary + ":" + log;
            return passed > 0;
        }

        // ---------------------------------------------------------------- the console's colour

        /// <summary>Console colour under the holder: texture at the hit UV times the tint. Vertex colours have no AOT code here.</summary>
        private static void SampleColor(Hit hit)
        {
            try
            {
                var src = sources[hit.Source];
                var mat = src.Renderer.sharedMaterial;
                if (mat == null) { colorNote = "the console's colour unknown (no material)"; return; }
                int i0 = src.Triangles[hit.Index], i1 = src.Triangles[hit.Index + 1], i2 = src.Triangles[hit.Index + 2];
                float wa = 1f - hit.U - hit.V;
                Color c = Color.white;
                string from = "";
                var tex = mat.mainTexture;
                Vector2[] uv = null;
                try { if (tex != null) uv = src.Mesh.uv; }
                catch (Exception e) { from = "no uv (" + e.GetType().Name + ")"; }
                if (tex != null && uv != null && uv.Length > Math.Max(i0, Math.Max(i1, i2)))
                {
                    Vector2 a = uv[i0], b = uv[i1], d = uv[i2];
                    float u = wa * a.x + hit.U * b.x + hit.V * d.x, v = wa * a.y + hit.U * b.y + hit.V * d.y;
                    Vector2 scale = mat.mainTextureScale, shift = mat.mainTextureOffset;
                    u = u * scale.x + shift.x;
                    v = v * scale.y + shift.y;
                    c = Pixel(tex, u, v);
                    from = $"texture '{tex.name}' at uv ({u:0.000}, {v:0.000})";
                }
                if (mat.HasProperty("_Color"))
                {
                    var tint = mat.GetColor("_Color");
                    c = new Color(c.r * tint.r, c.g * tint.g, c.b * tint.b, 1f);
                }
                float lum = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
                if (lum > 0.85f || !from.StartsWith("texture", StringComparison.Ordinal))
                {
                    colorNote = $"the console's colour not read ({(from.Length == 0 ? "no texture" : from.StartsWith("texture", StringComparison.Ordinal) ? "too light: " + Hex(c) : from)}), a dark grey instead";
                    return;
                }
                ConsoleColor = c;
                HaveColor = true;
                colorNote = $"the holder in the console's colour #{Hex(c)} ({from}, material '{mat.name}')";
            }
            catch (Exception e)
            {
                colorNote = "the console's colour not read (" + e.Message + ")";
            }
        }

        /// <summary>One texel via a 1x1 blit: the game's textures aren't CPU-readable.</summary>
        private static Color Pixel(Texture tex, float u, float v)
        {
            // scale zero, offset to the point
            var rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt, Vector2.zero, new Vector2(u - MathF.Floor(u), v - MathF.Floor(v)));
            RenderTexture.active = rt;
            var read = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            read.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var c = read.GetPixel(0, 0);
            UnityEngine.Object.Destroy(read);
            return new Color(c.r, c.g, c.b, 1f);
        }

        private static string Hex(Color c) => ((int)(Mathf.Clamp01(c.r) * 255f)).ToString("X2") + ((int)(Mathf.Clamp01(c.g) * 255f)).ToString("X2") + ((int)(Mathf.Clamp01(c.b) * 255f)).ToString("X2");

        private static N.Vector3 V(Vector3 v) => new N.Vector3(v.x, v.y, v.z);
        private static Vector3 U(N.Vector3 v) => new Vector3 { x = v.X, y = v.Y, z = v.Z };
    }
}
