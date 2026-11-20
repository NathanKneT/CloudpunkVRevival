using System;
using N = System.Numerics;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The dropped cup as a rigid body: foot, waist and rim points kept out of CabinShape, the holder ring and the
    /// wheel and lever bars. Steps of 1/240 s or 8 mm, so nothing tunnels. Holder frame.
    /// </summary>
    internal static class CoffeeBody
    {
        private const float StepTime = 1f / 240f, StepMove = 0.008f;
        private const int MaxSteps = 48;
        public const float Middle = 0.036f;             // centre of mass, from the foot
        private const float Inertia = 0.0009f;          // scalar, per unit mass (m2)
        public const float BounceFrom = 0.6f, Friction = 0.5f;
        private const float Bounce = 0.3f, Climb = 0.004f, MaxPush = 0.02f;
        private const float SpinDrag = 0.6f, RollDrag = 6f, MaxFelt = 40f, FeltTime = 0.02f;
        private const float RingInner = CoffeeModel.HolderInner * CoffeeModel.Voxel, RingOuter = CoffeeModel.HolderOuter * CoffeeModel.Voxel;
        private const float FootRadius = CoffeeModel.FootRadius * CoffeeModel.Voxel, RimRadius = CoffeeModel.Lip * CoffeeModel.Voxel;
        private static readonly N.Vector3 MiddleUp = new N.Vector3(0f, Middle, 0f);
        private static readonly N.Vector3[] Corners = MakeCorners();

        /// <summary>Contact tags besides CabinShape.Set and pane indices.</summary>
        public const int Wheel = -2, Lever = -3, Ring = -4;

        /// <summary>Foot position, velocity and spin of the middle, smoothed acceleration.</summary>
        public static N.Vector3 Pos, Vel, Spin, Accel;
        public static N.Quaternion Rot = N.Quaternion.Identity;

        /// <summary>Holder foot in CabinShape's frame (from the resting eyes).</summary>
        public static N.Vector3 Eyes;

        public static N.Vector3 Up => N.Vector3.Transform(N.Vector3.UnitY, Rot);
        public static N.Vector3 MiddlePoint => Pos + N.Vector3.Transform(MiddleUp, Rot);

        /// <summary>Cup-local point (from the foot) to holder frame.</summary>
        public static N.Vector3 Point(N.Vector3 local) => Pos + N.Vector3.Transform(local, Rot);

        /// <summary>A capsule obstacle (holder frame), tagged Wheel or Lever.</summary>
        public struct Bar
        {
            public N.Vector3 A, B;
            public float Radius;
            public int Tag;
        }

        public const int MaxBars = 72;
        /// <summary>Wheel and lever bars as posed this frame (set by CockpitCoffee).</summary>
        public static readonly Bar[] Bars = new Bar[MaxBars];
        public static int BarCount;
        private static readonly int[] near = new int[MaxBars];
        private static int nearCount;

        /// <summary>Released here with this velocity and spin. Accel starts at gravity (free fall feels nothing).</summary>
        public static void Start(N.Vector3 pos, N.Quaternion rot, N.Vector3 vel, N.Vector3 spin, N.Vector3 gravity)
        {
            Pos = pos;
            Rot = N.Quaternion.Normalize(rot);
            Vel = vel;
            Spin = spin;
            Accel = gravity;
        }

        public static void Stop() => Vel = Spin = Accel = N.Vector3.Zero;

        /// <summary>One frame of flight. hit: hardest impact (m/s) and its tag. False (cup reset) on NaN.</summary>
        public static bool Advance(float frame, N.Vector3 gravity, out bool touching, out float hit, out int hitTag)
        {
            touching = false;
            hit = 0f;
            hitTag = CabinShape.Set;
            float fast = Vel.Length() + Spin.Length() * 0.06f;
            int n = Math.Clamp((int)MathF.Ceiling(MathF.Max(frame / StepTime, fast * frame / StepMove)), 1, MaxSteps);
            float h = frame / n;
            Near(0.12f + (fast + 9.81f * frame) * frame);
            var before = Vel;
            for (int i = 0; i < n; i++) touching |= Step(h, gravity, ref hit, ref hitTag);
            if (!Finite(Pos) || !Finite(Vel) || !Finite(Spin) || !float.IsFinite(Rot.W) || !float.IsFinite(Rot.X) || !float.IsFinite(Rot.Y) || !float.IsFinite(Rot.Z))
            {
                Pos = Vel = Spin = Accel = N.Vector3.Zero;
                Rot = N.Quaternion.Identity;
                return false;
            }
            var a = (Vel - before) / frame;
            Accel += (a - Accel) * (1f - MathF.Exp(-frame / FeltTime));
            if (Accel.Length() > MaxFelt) Accel *= MaxFelt / Accel.Length();
            return true;
        }

        /// <summary>Bars within reach this frame; the steps only test these.</summary>
        private static void Near(float reach)
        {
            nearCount = 0;
            var middle = MiddlePoint;
            for (int b = 0; b < BarCount; b++)
                if (ToSegment(middle, Bars[b].A, Bars[b].B) <= reach + Bars[b].Radius + RimRadius + Middle) near[nearCount++] = b;
        }

        // R from the middle, normal and tangents, target bounce speed, accumulated impulses
        private struct Contact
        {
            public N.Vector3 R, N, T1, T2;
            public float Target, Jn, J1, J2;
        }
        private const int MaxContacts = 32, Passes = 8;
        private static readonly Contact[] contact = new Contact[MaxContacts];
        private static int contacts;

        /// <summary>Sequential impulses over all contacts, so a flat landing isn't tipped by the first corner.</summary>
        private static void Solve()
        {
            for (int pass = 0; pass < Passes; pass++)
                for (int i = 0; i < contacts; i++)
                {
                    ref var c = ref contact[i];
                    var vp = Vel + N.Vector3.Cross(Spin, c.R);
                    var rn = N.Vector3.Cross(c.R, c.N);
                    float jn = MathF.Max(c.Jn + (c.Target - N.Vector3.Dot(vp, c.N)) / (1f + rn.LengthSquared() / Inertia), 0f);
                    Apply(c.N, rn, jn - c.Jn);
                    c.Jn = jn;
                    float most = Friction * c.Jn;
                    Along(ref c.J1, c.T1, c.R, most);
                    Along(ref c.J2, c.T2, c.R, most);
                }
            contacts = 0;
        }

        private static void Along(ref float total, N.Vector3 t, N.Vector3 r, float most)
        {
            var rt = N.Vector3.Cross(r, t);
            float vt = N.Vector3.Dot(Vel + N.Vector3.Cross(Spin, r), t);
            float j = Math.Clamp(total - vt / (1f + rt.LengthSquared() / Inertia), -most, most);
            Apply(t, rt, j - total);
            total = j;
        }

        private static void Apply(N.Vector3 dir, N.Vector3 r_x_dir, float j)
        {
            Vel += dir * j;
            Spin += r_x_dir * (j / Inertia);
        }

        /// <summary>Adds a contact at r (from the middle) with an outward normal.</summary>
        private static void Touch(N.Vector3 r, N.Vector3 normal, int tag, ref float hit, ref int hitTag)
        {
            float vn = N.Vector3.Dot(Vel + N.Vector3.Cross(Spin, r), normal);
            if (-vn > hit) { hit = -vn; hitTag = tag; }
            if (contacts >= MaxContacts) return;
            ref var c = ref contact[contacts++];
            c.R = r;
            c.N = normal;
            c.T1 = MathF.Abs(normal.Y) > 0.5f ? N.Vector3.UnitX : N.Vector3.UnitY;
            c.T2 = N.Vector3.Normalize(N.Vector3.Cross(normal, c.T1));
            c.T1 = N.Vector3.Cross(c.T2, normal);
            c.Target = vn < -BounceFrom ? -Bounce * vn : 0f;
            c.Jn = c.J1 = c.J2 = 0f;
        }

        private static bool Step(float h, N.Vector3 g, ref float hit, ref int hitTag)
        {
            contacts = 0;
            var rot = Rot;
            var middle = Pos + N.Vector3.Transform(MiddleUp, rot);
            var oldMiddle = middle;
            var oldRot = rot;
            Vel += g * h;
            middle += Vel * h;
            float w = Spin.Length();
            if (w > 1e-5f) rot = N.Quaternion.Normalize(N.Quaternion.CreateFromAxisAngle(Spin / w, w * h) * rot);
            Spin *= MathF.Exp(-SpinDrag * h);

            bool touching = false;
            N.Vector3 pushUp = N.Vector3.Zero, pushDown = N.Vector3.Zero; // max push out, + and - per axis
            for (int k = 0; k < Corners.Length; k++)
            {
                var r = N.Vector3.Transform(Corners[k], rot);
                var p = middle + r;
                var e = p + Eyes;
                int col = CabinShape.Column(e.X, e.Z);
                bool ring = InRing(p, out float lo, out float hi);
                int tag = Ring;
                if (ring) { lo += Eyes.Y; hi += Eyes.Y; }
                else if (!CabinShape.Solid(col, e.Y, out lo, out hi, out tag)) continue;
                // exit through the face it entered, judged from last step's position
                var was = oldMiddle + N.Vector3.Transform(Corners[k], oldRot) + Eyes;
                N.Vector3 normal;
                float depth;
                if (tag >= 0 && CabinShape.GlassExit(tag, e, out normal, out depth)) { }          // glass: out square to the pane
                else if (was.Y >= hi - Climb) { normal = N.Vector3.UnitY; depth = hi - e.Y; }        // top (steps under 4 mm are climbed)
                else if (was.Y <= lo + Climb) { normal = -N.Vector3.UnitY; depth = e.Y - lo; }  // underside
                else if (!ring && !CabinShape.Solid(CabinShape.Column(was.X, e.Z), e.Y))
                {
                    bool fromLess = was.X < e.X;
                    normal = fromLess ? -N.Vector3.UnitX : N.Vector3.UnitX;
                    depth = fromLess ? e.X - CabinShape.EdgeX(col, false) : CabinShape.EdgeX(col, true) - e.X;
                }
                else if (!ring && !CabinShape.Solid(CabinShape.Column(e.X, was.Z), e.Y))
                {
                    bool fromLess = was.Z < e.Z;
                    normal = fromLess ? -N.Vector3.UnitZ : N.Vector3.UnitZ;
                    depth = fromLess ? e.Z - CabinShape.EdgeZ(col, false) : CabinShape.EdgeZ(col, true) - e.Z;
                }
                else if (!ring) NearestWayOut(e, middle + Eyes, col, lo, hi, out normal, out depth); // inside corner, or released inside something
                else
                {
                    // ring: back the way it came, else out the top
                    var back = new N.Vector3(was.X - e.X, 0f, was.Z - e.Z);
                    float len = back.Length();
                    if (len > 1e-4f) { normal = back / len; depth = len; }
                    else { normal = N.Vector3.UnitY; depth = hi - e.Y; }
                }
                depth = Math.Clamp(depth, 0f, MaxPush);
                touching = true;
                pushUp = N.Vector3.Max(pushUp, normal * depth);
                pushDown = N.Vector3.Min(pushDown, normal * depth);
                Touch(r, normal, tag, ref hit, ref hitTag);
            }

            // wheel and lever bars against the cup's real shape
            var up = N.Vector3.Transform(N.Vector3.UnitY, rot);
            var foot = middle - up * Middle;
            for (int i = 0; i < nearCount; i++)
            {
                ref var bar = ref Bars[near[i]];
                if (!BarContact(bar, foot, up, middle, out var normal, out float depth, out var point)) continue;
                depth = MathF.Min(depth, MaxPush);
                touching = true;
                pushUp = N.Vector3.Max(pushUp, normal * depth);
                pushDown = N.Vector3.Min(pushDown, normal * depth);
                Touch(point - middle, normal, bar.Tag, ref hit, ref hitTag);
            }

            Solve();
            if (touching) Spin *= MathF.Exp(-RollDrag * h);
            middle += pushUp + pushDown;
            Rot = rot;
            Pos = middle - N.Vector3.Transform(MiddleUp, rot);
            return touching;
        }

        /// <summary>
        /// Shortest exit from a solid span: top, bottom, or an open side facing the cup's middle
        /// (a side facing away would push through a thin wall at inside corners).
        /// </summary>
        private static void NearestWayOut(N.Vector3 e, N.Vector3 middle, int col, float lo, float hi, out N.Vector3 normal, out float depth)
        {
            normal = N.Vector3.UnitY;
            depth = hi - e.Y;
            if (e.Y - lo < depth) { normal = -N.Vector3.UnitY; depth = e.Y - lo; }
            var toMiddle = middle - e;
            float d = e.X - CabinShape.EdgeX(col, false);
            if (d < depth && toMiddle.X < 0f && !CabinShape.Solid(CabinShape.Column(e.X - CabinShape.Cell, e.Z), e.Y)) { normal = -N.Vector3.UnitX; depth = d; }
            d = CabinShape.EdgeX(col, true) - e.X;
            if (d < depth && toMiddle.X > 0f && !CabinShape.Solid(CabinShape.Column(e.X + CabinShape.Cell, e.Z), e.Y)) { normal = N.Vector3.UnitX; depth = d; }
            d = e.Z - CabinShape.EdgeZ(col, false);
            if (d < depth && toMiddle.Z < 0f && !CabinShape.Solid(CabinShape.Column(e.X, e.Z - CabinShape.Cell), e.Y)) { normal = -N.Vector3.UnitZ; depth = d; }
            d = CabinShape.EdgeZ(col, true) - e.Z;
            if (d < depth && toMiddle.Z > 0f && !CabinShape.Solid(CabinShape.Column(e.X, e.Z + CabinShape.Cell), e.Y)) { normal = N.Vector3.UnitZ; depth = d; }
        }

        /// <summary>Tapered cup vs a bar: deepest of the closest point and both ends (negative depth: a gap).</summary>
        private static bool BarContact(in Bar bar, N.Vector3 foot, N.Vector3 up, N.Vector3 middle, out N.Vector3 normal, out float depth, out N.Vector3 point)
        {
            var axis = up * CoffeeModel.Height;
            Closest(foot, foot + axis, bar.A, bar.B, out float s, out var a, out var q);
            BarAt(bar, s, a, q, up, middle, out normal, out depth, out point);
            for (int end = 0; end < 2; end++)
            {
                if (MathF.Abs(s - end) < 0.05f) continue;
                var e = foot + axis * end;
                var closest = OnSegment(e, bar.A, bar.B);
                // skip bars past the other end
                float off = N.Vector3.Dot(closest - e, up);
                if (MathF.Abs(off) > bar.Radius && (end == 1) == (off < 0f)) continue;
                BarAt(bar, end, e, closest, up, middle, out var n2, out float d2, out var p2);
                if (d2 > depth) { normal = n2; depth = d2; point = p2; }
            }
            return depth > 0f;
        }

        /// <summary>Cup vs bar at axis point a (s: 0 foot, 1 rim) and bar point q.</summary>
        private static void BarAt(in Bar bar, float s, N.Vector3 a, N.Vector3 q, N.Vector3 up, N.Vector3 middle, out N.Vector3 normal, out float depth, out N.Vector3 point)
        {
            float rc = FootRadius + (RimRadius - FootRadius) * s;
            var v = q - a; // axis to bar
            float along = N.Vector3.Dot(v, up);
            var radial = v - up * along;
            float rho = radial.Length();
            bool end = (s <= 0f && along < 0f) || (s >= 1f && along > 0f);
            if (end && rho <= rc)
            {
                // flat end
                depth = bar.Radius - MathF.Abs(along);
                normal = along < 0f ? up : -up;
                point = a + radial;
            }
            else if (end)
            {
                // end edge
                var edge = a + radial * (rc / rho);
                var w = q - edge;
                float d = w.Length();
                depth = bar.Radius - d;
                normal = d > 1e-6f ? -w / d : (along < 0f ? up : -up);
                point = edge;
            }
            else
            {
                // side
                depth = rc + bar.Radius - rho;
                if (rho > 1e-6f) normal = -radial / rho;
                else
                {
                    // bar through the axis: out toward the cup's middle
                    var off = middle - q;
                    off -= up * N.Vector3.Dot(off, up);
                    normal = off.LengthSquared() > 1e-10f ? N.Vector3.Normalize(off) : N.Vector3.Normalize(N.Vector3.Cross(up, bar.B - bar.A + new N.Vector3(1e-4f, 0f, 0f)));
                }
                point = a - normal * rc;
            }
        }

        private static N.Vector3 OnSegment(N.Vector3 p, N.Vector3 a, N.Vector3 b)
        {
            var ab = b - a;
            float t = Math.Clamp(N.Vector3.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-8f), 0f, 1f);
            return a + ab * t;
        }

        /// <summary>Closest points of segments p1-q1 (at s) and p2-q2.</summary>
        private static void Closest(N.Vector3 p1, N.Vector3 q1, N.Vector3 p2, N.Vector3 q2, out float s, out N.Vector3 c1, out N.Vector3 c2)
        {
            N.Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = d1.LengthSquared(), e = d2.LengthSquared(), f = N.Vector3.Dot(d2, r), t;
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
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        private static float ToSegment(N.Vector3 p, N.Vector3 a, N.Vector3 b) => (p - OnSegment(p, a, b)).Length();

        /// <summary>Smallest gap to the bars with this tag (negative: overlapping), or infinity.</summary>
        public static float Gap(int tag)
        {
            float best = float.PositiveInfinity;
            var up = Up;
            var middle = MiddlePoint;
            var foot = middle - up * Middle;
            for (int b = 0; b < BarCount; b++)
            {
                if (Bars[b].Tag != tag) continue;
                BarContact(Bars[b], foot, up, middle, out _, out float depth, out _);
                best = MathF.Min(best, -depth);
            }
            return best;
        }

        /// <summary>Holder ring as a solid span, from just under the console to its top.</summary>
        private static bool InRing(N.Vector3 p, out float lo, out float hi)
        {
            lo = -0.01f;
            hi = CoffeeModel.HolderHeight;
            if (p.Y < lo || p.Y > hi) return false;
            float r = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
            return r >= RingInner && r <= RingOuter;
        }

        /// <summary>8 points each on foot, waist and rim, plus the centre.</summary>
        private static N.Vector3[] MakeCorners()
        {
            var c = new N.Vector3[25];
            float foot = FootRadius, rim = RimRadius;
            float waist = CoffeeModel.OuterRadius(0.5f * CoffeeModel.Height);
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f, x = MathF.Cos(a), z = MathF.Sin(a);
                c[k] = new N.Vector3(x * foot, -Middle, z * foot);
                c[k + 8] = new N.Vector3(x * waist, 0.5f * CoffeeModel.Height - Middle, z * waist);
                c[k + 16] = new N.Vector3(x * rim, CoffeeModel.Height - Middle, z * rim);
            }
            c[24] = N.Vector3.Zero;
            return c;
        }

        private static bool Finite(N.Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
