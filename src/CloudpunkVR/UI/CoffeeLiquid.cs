using System;
using N = System.Numerics;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Coffee physics: the level, and a planar surface sloshing in the cylinder's first mode
    /// (w^2 = g k tanh(k h), k = 1.841 / R) around level against the felt gravity. Meters, radians, cup frame (+y up).
    /// </summary>
    internal static class CoffeeLiquid
    {
        private const float Gravity = 9.81f;
        private const float ModeRoot = 1.8412f;     // first zero of J1'
        private const float Damping = 0.05f;        // of critical: rings for about a second
        private const float MaxLean = 0.48f * MathF.PI; // ~86 degrees
        private const float SpillRate = 6f;         // 1/s, of the height over the rim
        private const float MaxPour = 0.06f;        // m/s of level, cup upside down
        private const float CrestLoss = 9f;         // 1/s, slosh damping while spilling
        private const float RippleLife = 0.35f, RippleSpeed = 30f, RippleWave = 300f, MaxRipple = 0.003f;

        // The HOVA accelerates like an arcade car (15 m/s2 throttle, 25 braking, 60 in a crash), so a real cup would
        // spill at every start. The push is smoothed and scaled down, and only a crash counts past Jolt.
        public const float Jolt = 30f;
        private const float PushTime = 0.3f, PushScale = 0.37f, LiftScale = 0.25f, Lightest = 0.8f, Heaviest = 1.3f;
        private static N.Vector3 pushSlow;

        /// <summary>Depth from the floor, cup upright and still.</summary>
        public static float Level;
        /// <summary>Surface normal's lean from the cup axis (direction times radians), and its rate.</summary>
        public static N.Vector2 Tilt, Spin;
        private static N.Vector2 rest;              // level against the felt gravity
        public static float Ripple, RipplePhase;
        private static float lastAlong = float.NaN;
        private static bool pouring; // past 84 degrees: the rest runs out

        /// <summary>Felt gravity strength this step, m/s2.</summary>
        public static float Felt { get; private set; } = Gravity;

        public static void Reset(float level)
        {
            Level = level;
            Tilt = Spin = rest = N.Vector2.Zero;
            Ripple = 0f;
            lastAlong = float.NaN;
            pouring = false;
            pushSlow = N.Vector3.Zero;
        }

        /// <summary>Settles the surface (after a cut or a new place).</summary>
        public static void Still()
        {
            Tilt = rest;
            Spin = N.Vector2.Zero;
            Ripple = 0f;
            lastAlong = float.NaN;
            pushSlow = N.Vector3.Zero;
        }

        /// <summary>The share of the HOVA's acceleration the cup feels. knock: just hit something. Once a frame.</summary>
        public static N.Vector3 HovaPush(N.Vector3 accel, N.Vector3 up, float dt, bool knock)
        {
            if (!Finite(accel)) accel = N.Vector3.Zero;
            if (!knock)
            {
                float along = N.Vector3.Dot(accel, up);
                var level = accel - up * along;
                float m = level.Length();
                if (m > Jolt) accel = up * along + level * (Jolt / m);
            }
            pushSlow += (accel - pushSlow) * (1f - MathF.Exp(-dt / PushTime));
            float lift = N.Vector3.Dot(pushSlow, up);
            var across = (pushSlow - up * lift) * PushScale;
            lift = Math.Clamp(lift * LiftScale, (Lightest - 1f) * Gravity, (Heaviest - 1f) * Gravity);
            var flat = accel - up * N.Vector3.Dot(accel, up);
            float hard = flat.Length();
            if (knock && hard > Jolt) across += flat * ((hard - Jolt) / hard * PushScale);
            return across + up * lift;
        }

        public static bool Finite(N.Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        private static bool Finite(N.Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

        public static bool Healthy => float.IsFinite(Level) && Finite(Tilt) && Finite(Spin) && Finite(rest) && float.IsFinite(Ripple)
                                      && float.IsFinite(RipplePhase) && Finite(pushSlow);

        /// <summary>Still surface, keeping the level if it's finite.</summary>
        public static void Heal()
        {
            Reset(float.IsFinite(Level) ? Math.Clamp(Level, 0f, CoffeeModel.Inside) : 0f);
        }

        /// <summary>Cup turned about its axis (radians): the coffee doesn't turn, so the slosh turns back.</summary>
        public static void Twist(float angle)
        {
            if (MathF.Abs(angle) < 1e-6f) return;
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            N.Vector2 Back(N.Vector2 v) => new N.Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
            Tilt = Back(Tilt);
            Spin = Back(Spin);
        }

        /// <summary>Inner radius at the surface, m.</summary>
        public static float Radius => MathF.Max(CoffeeModel.InnerRadius(CoffeeModel.Floor + Level), 0.02f);

        /// <summary>One slosh step under felt gravity (cup frame). Returns the level lost over the rim.</summary>
        public static float Step(float h, N.Vector3 felt, bool spill)
        {
            float g = felt.Length();
            if (g > 1e-3f)
            {
                var up = -felt / g;
                float lean = MathF.Acos(Math.Clamp(up.Y, -1f, 1f));
                float side = MathF.Sqrt(up.X * up.X + up.Z * up.Z);
                rest = side > 1e-6f ? new N.Vector2(up.X, up.Z) * (MathF.Min(lean, MaxLean) / side) : N.Vector2.Zero;
                pouring = up.Y < 0.1f;
                // a jolt along the cup ripples the surface; hand tremor doesn't
                if (!float.IsNaN(lastAlong)) Ripple = MathF.Min(Ripple + MathF.Max(MathF.Abs(felt.Y - lastAlong) - 0.01f, 0f) * 0.00045f, MaxRipple);
                lastAlong = felt.Y;
            }
            Felt = Math.Clamp(g, 0.25f * Gravity, 3f * Gravity);

            float w = Omega();
            var accel = -(w * w) * (Tilt - rest) - (2f * Damping * w) * Spin;
            Spin += accel * h;
            Tilt += Spin * h;
            if (Tilt.Length() > MaxLean) Tilt *= MaxLean / Tilt.Length();

            Ripple *= MathF.Exp(-h / RippleLife);
            RipplePhase += RippleSpeed * h;
            if (RipplePhase > 1000f) RipplePhase -= 2f * MathF.PI * 150f;

            float over = Over();
            if (over <= 0f || Level <= 0f) return 0f;
            Spin *= MathF.Exp(-CrestLoss * h);
            if (!spill) return 0f;
            float lost = MathF.Min(MathF.Min(SpillRate * over, MaxPour) * h, Level);
            Level -= lost;
            return lost;
        }

        private static float Omega()
        {
            float r = Radius, depth = MathF.Max(Level, 0.003f);
            return MathF.Sqrt(Felt * ModeRoot / r * MathF.Tanh(ModeRoot * depth / r));
        }

        /// <summary>Slosh period, s.</summary>
        public static float Period => 2f * MathF.PI / Omega();

        /// <summary>Surface slope (rise over run), capped short of vertical.</summary>
        public static float Slope => MathF.Tan(MathF.Min(Tilt.Length(), 1.48f));

        /// <summary>Highest point of the surface on the wall, from the floor.</summary>
        public static float WallHeight()
        {
            if (Level <= 0f) return 0f;
            float r = Radius, t = Slope;
            // tilted far enough, the coffee is a wedge against the low wall
            return Level >= r * t ? Level + r * t : MathF.Sqrt(4f * Level * r * t);
        }

        /// <summary>
        /// Height over the rim (negative: room left). Past 84 degrees it always pours,
        /// or the plane model keeps a last wedge in a cup on its side.
        /// </summary>
        public static float Over()
        {
            float over = WallHeight() + Ripple - CoffeeModel.Inside;
            return pouring && Level > 0f ? MathF.Max(over, 0.01f) : over;
        }

        /// <summary>Unit direction the surface rises toward (cup x-z), zero when level.</summary>
        public static N.Vector2 HighSide
        {
            get
            {
                float l = Tilt.Length();
                return l > 1e-5f ? -Tilt / l : N.Vector2.Zero;
            }
        }

        /// <summary>Surface height over the floor at (x, z), with ripples.</summary>
        public static float SurfaceAt(float x, float z)
        {
            if (Level <= 0f) return 0f;
            float r = Radius, t = Slope;
            var high = HighSide;
            float s = WallHeight() - t * (r - (x * high.X + z * high.Y));
            if (Ripple > 1e-5f)
            {
                float d = MathF.Sqrt(x * x + z * z);
                s += Ripple * MathF.Cos(RippleWave * d - RipplePhase) * (1f - 0.4f * d / r);
            }
            return Math.Clamp(s, 0f, CoffeeModel.Inside);
        }

        /// <summary>Adds a ripple (a sip, a drop into the holder).</summary>
        public static void Knock(float amount) => Ripple = MathF.Min(Ripple + amount, MaxRipple);
    }
}
