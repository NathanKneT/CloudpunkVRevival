using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// Wheel angle to steering. The game's spin brake makes a dead zone (about 0.17 at rest, 4x at top speed), so an
    /// offset keeps the turn starting at the same hand angle at any speed: steer = o + (1 - o) u.
    /// Presets are the VREVIVAL menu's Wheel sensitivity.
    /// </summary>
    internal static class WheelSteering
    {
        public const float MinLock = 20f, MaxLock = 90f;
        public static readonly float[] Presets = { 25f, 35f, 50f, 65f };
        public static readonly string[] PresetNames = { "sharp", "quick", "relaxed", "slow" };
        private const float GameAtTopSpeed = 0.25f; // steering multiplier at top speed (PlayerCar.Engine)
        private const float GameWheelLock = 65f;    // game wheel at full steer (steeringWheelData.maxAngle)
        private const float GameBrake = 1f;         // rad/s off the spin each second (PlayerCar.Engine)
        private const float RestDeadZone = 0.17f;   // measured fallback
        private const float RampDegrees = 3f;       // offset fade-in, deg of wheel

        private static float speedShare, restDeadZone = RestDeadZone, offset;
        private static IntPtr carSeen;
        private static float inertiaUp, boost = 1f, nextRead, loggedDeadZone;
        private static int deadZoneNotes;

        /// <summary>Degrees of hand turn for full steer, either way.</summary>
        public static float Lock => Mathf.Clamp(VRConfig.WheelLock.Value, MinLock, MaxLock);

        /// <summary>Every frame, before the hands steer.</summary>
        public static void Update(PlayerCar car)
        {
            speedShare = 0f;
            if (car != null)
            {
                float top = car.maximumSpeed;
                if (top > 1f) speedShare = Mathf.Clamp01(car.currentSpeed / top);
                restDeadZone = DeadZoneAtRest(car);
            }
            offset = OffsetAt(speedShare);
            ProbeUpdate(car);
        }

        /// <summary>
        /// Dead zone at rest = brake x inertia / steer torque. Re-read every 2 s, since a loading HOVA has a default inertia.
        /// </summary>
        private static float DeadZoneAtRest(PlayerCar car)
        {
            if (car.Pointer != carSeen)
            {
                carSeen = car.Pointer;
                nextRead = 0f;
            }
            if (Time.unscaledTime >= nextRead)
            {
                nextRead = Time.unscaledTime + 2f;
                inertiaUp = InertiaUp(car);
                boost = Boost(car);
            }
            float torque = car.currentSteerTorque * boost;
            float zone = inertiaUp > 0f && torque >= 1f ? GameBrake * inertiaUp / torque : 0f;
            bool usable = zone >= 0.05f && zone <= 0.6f;
            if (Mathf.Abs((usable ? zone : RestDeadZone) - loggedDeadZone) > 0.01f && deadZoneNotes++ < 10)
            {
                loggedDeadZone = usable ? zone : RestDeadZone;
                Plugin.L.LogInfo(usable
                    ? $"Wheel: the HOVA starts to turn past steer {zone:0.00} at rest (inertia {inertiaUp:0} about its up, steer torque {torque:0}): {zone * Lock:0.0} degrees of wheel at any speed."
                    : $"Wheel: the HOVA's steering read {zone:0.000} at rest (inertia {inertiaUp:0} about its up, steer torque {torque:0}), out of reason; {RestDeadZone:0.00} as measured until it reads right.");
            }
            return usable ? zone : RestDeadZone;
        }

        /// <summary>Inertia about the HOVA's up, kg m2 (0 if unreadable).</summary>
        private static float InertiaUp(PlayerCar car)
        {
            try
            {
                var rb = car.rBody;
                if (rb == null) return 0f;
                Vector3 inertia = rb.inertiaTensor;
                Vector3 e = Quaternion.Inverse(rb.inertiaTensorRotation) * Vector3.up; // its up in the inertia's own axes
                float inverse = e.x * e.x / inertia.x + e.y * e.y / inertia.y + e.z * e.z / inertia.z;
                return inverse > 1e-9f && !float.IsInfinity(inverse) && !float.IsNaN(inverse) ? 1f / inverse : 0f;
            }
            catch (Exception) { return 0f; }
        }

        /// <summary>Lateral boosters give 1.2x, except during the game's vehicle damage event.</summary>
        private static float Boost(PlayerCar car)
        {
            try
            {
                var lateral = car.LateralBoosters;
                var g = Global.Instance;
                return lateral != null && lateral.amount > 0 && g != null && !g.globalVehicleDamage ? 1.2f : 1f;
            }
            catch (Exception) { return 1f; }
        }

        /// <summary>o = (b - b0) / (1 - b0), b the dead zone now, b0 at rest.</summary>
        private static float OffsetAt(float x)
        {
            float now = Mathf.Min(0.95f, restDeadZone / Mathf.Lerp(1f, GameAtTopSpeed, x));
            return Mathf.Clamp((now - restDeadZone) / (1f - restDeadZone), 0f, 0.9f);
        }

        /// <summary>Offset fade-in, kept short of where the turn starts.</summary>
        private static float Ramp => Mathf.Min(RampDegrees / Lock, 0.6f * restDeadZone);

        /// <summary>Game steering for this hand angle (deg, positive right).</summary>
        public static float Steer(float handsAngle) => Law(handsAngle / Lock, offset, Ramp);

        /// <summary>Inverse of Steer, for drawing the wheel under stick steering.</summary>
        public static float HandsAngle(float steer) => steer == 0f ? 0f : Mathf.Sign(steer) * Unlaw(Mathf.Abs(steer), offset) * Lock;

        /// <summary>Share of the lock (0-1) that gives this steering with this offset.</summary>
        private static float Unlaw(float steer, float o)
        {
            float target = Mathf.Clamp01(steer), ramp = Ramp, low = 0f, high = 1f;
            if (target <= 0f) return 0f;
            for (int i = 0; i < 24; i++) // the law only grows: halve the interval
            {
                float mid = 0.5f * (low + high);
                if (Law(mid, o, ramp) < target) low = mid;
                else high = mid;
            }
            return high;
        }

        private static float Law(float u, float o, float ramp)
        {
            float a = Mathf.Clamp01(Mathf.Abs(u));
            float fade = ramp > 1e-4f ? Mathf.SmoothStep(0f, 1f, a / ramp) : 1f;
            return Mathf.Sign(u) * (o * fade + (1f - o) * a);
        }

        /// <summary>Hand angle where the HOVA starts to turn (same at any speed).</summary>
        private static float StartAngle => restDeadZone * Lock;

        public static string Describe() =>
            $"lock {Lock:0} deg, turns from {StartAngle:0.0} deg, offset {offset:0.00} at {speedShare * 100f:0}% of top speed";

        // ---------------------------------------------------------------- measuring (developer command "wheel")

        // turn rate from the heading, in game time
        private const float Window = 1f;
        private static bool measuring;
        private static float measureLeft, measureTime, measureTurn, lastHeading;
        private static Action<float> measured;

        private struct Sample
        {
            public float Angle, Wheel, Steer, Law, Game, Rate, Spin, Speed, Offset, Expected;
        }

        private sealed class Sweep
        {
            public float[] Angles;
            public float Hold, Wait, Lock, StartWheel; // the wheel goes back to where it was
            public int Index, Number;
            public bool Fast; // swept at speed: the offset must be up
            public readonly List<Sample> Samples = new List<Sample>();
        }

        private const float FastSpeed = 50f;    // km/h a full-lock circle reaches under throttle
        private const float FastOffset = 0.1f;  // offset clearly up (0.24 at 50 km/h)
        private static Sweep sweep;
        private static int sweepCount;
        private static readonly SortedDictionary<int, List<Sample>> sweeps = new SortedDictionary<int, List<Sample>>(); // by lock

        private static PlayerCar Car()
        {
            var g = Global.Instance;
            return g != null ? g.playerCar : null;
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

#if DEV
        /// <summary>
        /// wheel physics, rate [s], sweep &lt;deg&gt;... [hold &lt;s&gt;] [fast], sweep compare, lock &lt;deg&gt;.
        /// False for the other "wheel" commands.
        /// </summary>
        public static bool Probe(string[] a)
        {
            switch (a[1].ToLowerInvariant())
            {
                case "physics":
                    Plugin.L.LogInfo("wheel physics: " + Physics(Car()));
                    return true;
                case "rate":
                    StartMeasure(a.Length > 2 ? F(a[2]) : Window, rate =>
                        Plugin.L.LogInfo($"wheel rate: the HOVA turns {rate:0.0} deg/s ({Spin(Car()):0.0} by its rigidbody now); {State(Car())}"));
                    return true;
                case "sweep":
                    if (a.Length > 2 && a[2].Equals("compare", StringComparison.OrdinalIgnoreCase)) Compare();
                    else StartSweep(a);
                    return true;
                case "lock":
                    if (a.Length > 2) VRConfig.WheelLock.Value = Mathf.Clamp(F(a[2]), MinLock, MaxLock);
                    Plugin.L.LogInfo($"wheel lock: WheelLock = {VRConfig.WheelLock.Value:0}; {Describe()}");
                    return true;
            }
            return false;
        }
#endif

        private static string State(PlayerCar car)
        {
            string game = car != null ? $"game steer {car.steerInput:0.00}, {car.currentSpeed:0} km/h" : "no HOVA";
            return $"hands {WheelGrab.HeldAngle:0.0} deg, steer {WheelGrab.HeldSteer:0.00}, {game}; {Describe()}";
        }

        /// <summary>Turn rate from the rigidbody, deg/s, positive right.</summary>
        private static float Spin(PlayerCar car)
        {
            if (car == null) return 0f;
            var rb = car.rBody;
            if (rb == null) return 0f;
            return Vector3.Dot(rb.angularVelocity, car.transform.up) * Mathf.Rad2Deg;
        }

        private static float Heading(PlayerCar car)
        {
            Vector3 f = car.transform.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        private static void StartMeasure(float seconds, Action<float> done)
        {
            var car = Car();
            if (car == null)
            {
                Plugin.L.LogWarning("wheel: no HOVA to measure.");
                sweep = null;
                return;
            }
            measuring = true;
            measureLeft = Mathf.Max(0.1f, seconds);
            measureTime = measureTurn = 0f;
            lastHeading = Heading(car);
            measured = done;
        }

        private static void ProbeUpdate(PlayerCar car)
        {
            if (!measuring && sweep == null) return;
            if (car == null)
            {
                if (measuring || sweep != null) Plugin.L.LogWarning("wheel: the HOVA is gone; the measure stops.");
                measuring = false;
                sweep = null;
                return;
            }
            float dt = Time.deltaTime;
            if (measuring)
            {
                float h = Heading(car);
                measureTurn += Mathf.DeltaAngle(lastHeading, h);
                lastHeading = h;
                measureTime += dt;
                measureLeft -= dt;
                if (measureLeft > 0f) return;
                measuring = false;
                measured?.Invoke(measureTime > 1e-4f ? measureTurn / measureTime : 0f);
                return;
            }
            sweep.Wait -= dt;
            if (sweep.Wait <= 0f) StartMeasure(Window, Record);
        }

        private static void StartSweep(string[] a)
        {
            var angles = new List<float>();
            float hold = 3f;
            bool fast = false;
            for (int i = 2; i < a.Length; i++)
            {
                if (a[i].Equals("hold", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length) { hold = F(a[++i]); continue; }
                if (a[i].Equals("fast", StringComparison.OrdinalIgnoreCase)) { fast = true; continue; }
                angles.Add(F(a[i]));
            }
            float startWheel = WheelGrab.HeldAngle;
            if (angles.Count == 0 || !WheelGrab.SimTurnTo(HandsFor(angles[0])))
            {
                Plugin.L.LogWarning("wheel sweep: needs angles and the simulated hands on the wheel (wheel grab both).");
                return;
            }
            if (sweep != null) Plugin.L.LogWarning($"wheel sweep #{sweep.Number}: cut short by a new sweep.");
            sweep = new Sweep { Angles = angles.ToArray(), Hold = Mathf.Max(Window + 0.5f, hold), Lock = Lock, Fast = fast, Number = ++sweepCount, StartWheel = startWheel };
            sweep.Wait = sweep.Hold - Window;
            measuring = false; // drop any measure left running
            Plugin.L.LogInfo($"wheel sweep #{sweep.Number}: lock {sweep.Lock:0}{(fast ? ", at speed" : "")}, the wheel to {string.Join(", ", angles)} deg from {startWheel:0.0}, {sweep.Hold:0.0} s each, the last {Window:0} s measured; {Physics(Car())}");
        }

        /// <summary>Offset from fresh readings, to catch a stale one.</summary>
        private static float ExpectedOffset(PlayerCar car)
        {
            float torque = car.currentSteerTorque * Boost(car), inertia = InertiaUp(car), top = car.maximumSpeed;
            if (inertia <= 0f || torque < 1f || top <= 1f) return float.NaN;
            float rest = GameBrake * inertia / torque, x = Mathf.Clamp01(car.currentSpeed / top);
            float now = Mathf.Min(0.95f, rest / Mathf.Lerp(1f, GameAtTopSpeed, x));
            return Mathf.Clamp((now - rest) / (1f - rest), 0f, 0.9f);
        }

        /// <summary>Simulated hands' target for this wheel angle, counting any slide past a stop.</summary>
        private static float HandsFor(float wheelAngle) => WheelGrab.SimTurnNow + (wheelAngle - WheelGrab.HeldAngle);

        private static void Record(float rate)
        {
            var car = Car();
            if (sweep == null || car == null) return;
            float angle = sweep.Angles[sweep.Index];
            var s = new Sample
            {
                Angle = angle,
                Wheel = WheelGrab.HeldAngle,
                Steer = WheelGrab.HeldSteer,
                Law = Steer(Mathf.Clamp(angle, -sweep.Lock, sweep.Lock)), // past the stop, the wheel is at the stop
                Game = car.steerInput,
                Rate = rate,
                Spin = Spin(car),
                Speed = car.currentSpeed,
                Offset = offset,
                Expected = ExpectedOffset(car),
            };
            sweep.Samples.Add(s);
            Plugin.L.LogInfo($"wheel sweep #{sweep.Number}: lock {sweep.Lock:0}, wheel {angle:0} deg (at {s.Wheel:0.0}) -> steer {s.Steer:0.00} (law {s.Law:0.00}, game {s.Game:0.00}) -> " +
                             $"the HOVA turns {rate:0.0} deg/s ({s.Spin:0.0} by its rigidbody now), {s.Speed:0} km/h, offset {s.Offset:0.00} (its physics: {s.Expected:0.00})");
            if (++sweep.Index < sweep.Angles.Length)
            {
                WheelGrab.SimTurnTo(HandsFor(sweep.Angles[sweep.Index]));
                sweep.Wait = sweep.Hold - Window;
                return;
            }
            Finish();
        }

        /// <summary>Checks: wheel where asked, steer matches the law, rate never drops further out, fast offset right.</summary>
        private static void Finish()
        {
            var done = sweep;
            sweep = null;
            WheelGrab.SimTurnTo(HandsFor(done.StartWheel));
            sweeps[Mathf.RoundToInt(done.Lock)] = done.Samples;
            var list = new List<Sample>(done.Samples);
            list.Sort((p, q) => Mathf.Abs(p.Angle).CompareTo(Mathf.Abs(q.Angle)));
            var sb = new StringBuilder();
            string steerFail = "", rateFail = "";
            float before = float.NegativeInfinity, most = 0f, slowest = float.PositiveInfinity, fastest = 0f, lowOffset = float.PositiveInfinity, highOffset = 0f;
            foreach (var s in list)
            {
                sb.Append($"{(sb.Length > 0 ? ", " : "")}{s.Angle:0} -> {s.Rate:0.0}");
                float asked = Mathf.Clamp(s.Angle, -done.Lock, done.Lock);
                if (Mathf.Abs(s.Wheel - asked) > 1f) steerFail += $" {s.Angle:0} deg: the wheel at {s.Wheel:0.0};";
                else if (Mathf.Abs(s.Game - s.Law) > 0.03f) steerFail += $" {s.Angle:0} deg: game {s.Game:0.00}, law {s.Law:0.00};";
                float toward = s.Rate * Mathf.Sign(s.Angle); // turning the way the hands went
                if (toward < before - 3f) rateFail += $" {s.Angle:0} deg turns slower ({toward:0.0}) than less ({before:0.0});";
                before = Mathf.Max(before, toward);
                most = Mathf.Max(most, toward);
                slowest = Mathf.Min(slowest, s.Speed);
                fastest = Mathf.Max(fastest, s.Speed);
                lowOffset = Mathf.Min(lowOffset, s.Offset);
                highOffset = Mathf.Max(highOffset, s.Offset);
            }
            if (most < 5f) rateFail += $" the furthest turns it only {most:0.0} deg/s;";
            var car = Car();
            float top = car != null ? car.maximumSpeed : 0f;
            string rate = rateFail.Length == 0 ? " rate ok" : " RATE FAILED:" + rateFail;
            if (fastest - slowest > Mathf.Max(5f, 0.15f * top)) rate = $" rate not checked (the speed went from {slowest:0} to {fastest:0} km/h)";
            string checks = (steerFail.Length == 0 ? " steer ok" : " STEER FAILED:" + steerFail) + rate;
            if (done.Fast)
            {
                string fastFail = "";
                if (slowest < FastSpeed) fastFail += $" down to {slowest:0} km/h, under {FastSpeed:0} (not fast enough, or it hit something);";
                else if (lowOffset < FastOffset) fastFail += $" the offset down to {lowOffset:0.00} at {slowest:0} km/h;";
                foreach (var s in list)
                    if (!(Mathf.Abs(s.Offset - s.Expected) <= 0.03f)) fastFail += $" {s.Angle:0} deg: offset {s.Offset:0.00} where its physics give {s.Expected:0.00};";
                checks += fastFail.Length == 0 ? $" fast ok (offset {lowOffset:0.00} to {highOffset:0.00} at {slowest:0} to {fastest:0} km/h, as its physics give)"
                    : " FAST FAILED:" + fastFail;
            }
            string line = $"wheel sweep #{done.Number} over, lock {done.Lock:0}, {slowest:0} to {fastest:0} km/h: {sb} (hands deg -> deg/s); check:{checks}";
            if (checks.Contains("FAILED")) Plugin.L.LogWarning(line);
            else Plugin.L.LogInfo(line);
        }

        /// <summary>Each angle across locks: a longer lock never turns the HOVA faster.</summary>
        private static void Compare()
        {
            if (sweeps.Count < 2)
            {
                Plugin.L.LogWarning($"wheel sweep compare: {sweeps.Count} lock(s) swept; needs two or more.");
                return;
            }
            var angles = new SortedSet<int>();
            foreach (var kv in sweeps)
                foreach (var s in kv.Value) angles.Add(Mathf.RoundToInt(s.Angle));
            var sb = new StringBuilder();
            string fail = "";
            foreach (int angle in angles)
            {
                sb.Append($"; hands {angle} deg:");
                float faster = float.PositiveInfinity;
                int fasterLock = 0;
                foreach (var kv in sweeps) // shortest lock first
                {
                    foreach (var s in kv.Value)
                    {
                        if (Mathf.RoundToInt(s.Angle) != angle) continue;
                        float toward = s.Rate * Mathf.Sign(s.Angle);
                        sb.Append($" lock {kv.Key} {toward:0.0}");
                        if (toward > faster + 3f) fail += $" {angle} deg: lock {kv.Key} {toward:0.0} faster than lock {fasterLock} {faster:0.0};";
                        faster = Mathf.Min(faster, toward);
                        fasterLock = kv.Key;
                        break;
                    }
                }
            }
            string line = $"wheel sweep compare (deg/s){sb}; check: {(fail.Length == 0 ? "follow ok" : "FOLLOW FAILED:" + fail)}";
            if (fail.Length > 0) Plugin.L.LogWarning(line);
            else Plugin.L.LogInfo(line);
        }

        /// <summary>Steering setup (torques, rigidbody) and the dead zone at rest, now and at top speed.</summary>
        private static string Physics(PlayerCar car)
        {
            if (car == null) return "no HOVA";
            var sb = new StringBuilder();
            float torque = car.currentSteerTorque;
            bool boosters = false;
            try
            {
                var lateral = car.LateralBoosters;
                var g = Global.Instance;
                boosters = lateral != null && lateral.amount > 0 && g != null && !g.globalVehicleDamage;
            }
            catch (Exception) { /* left at none */ }
            float boost = boosters ? 1.2f : 1f;
            sb.Append($"steer torque {torque:0} (the game's: low {car.minSteerTorque:0}, mid {car.midSteerTorque:0}, high {car.steerTorque:0}, speedway {car.speedwaySteerTorque:0})");
            if (boosters) sb.Append(", x1.2 with the lateral boosters");
            sb.Append($"; {car.currentSpeed:0} km/h of a top speed of {car.maximumSpeed:0}: the game's push x{Mathf.Lerp(1f, GameAtTopSpeed, speedShare):0.00}, the wheel's offset {offset:0.00}");
            var rb = car.rBody;
            if (rb == null) return sb.Append("; no rigidbody").ToString();
            try
            {
                float cap = rb.maxAngularVelocity;
                sb.Append($"; mass {rb.mass:0} kg, angular drag {rb.angularDrag:0.###}, spin cap {cap:0.00} rad/s ({cap * Mathf.Rad2Deg:0} deg/s), the game's brake {GameBrake:0} rad/s each second");
                Vector3 inertia = rb.inertiaTensor;
                Vector3 e = Quaternion.Inverse(rb.inertiaTensorRotation) * Vector3.up; // its up in the inertia's own axes
                float inverse = e.x * e.x / inertia.x + e.y * e.y / inertia.y + e.z * e.z / inertia.z;
                float push = torque * boost * inverse; // rad/s each second for a steer of 1, at rest
                sb.Append($"; inertia ({inertia.x:0}, {inertia.y:0}, {inertia.z:0}), {1f / inverse:0} about its up: a steer of 1 pushes {push:0.00} rad/s each second at rest");
                if (push > 1e-4f)
                {
                    float rest = GameBrake / push, now = rest / Mathf.Lerp(1f, GameAtTopSpeed, speedShare), top = rest / GameAtTopSpeed;
                    sb.Append($"; nothing turns below steer {rest:0.00} at rest, {now:0.00} now, {top:0.00} at top speed: the wheel at {Hands(rest, 0f)} / {Hands(now, speedShare)} / {Hands(top, 1f)} " +
                              $"with lock {Lock:0}, {Old(rest)} / {Old(now)} / {Old(top)} with the game's 65 as it was; the dead zone at rest the wheel works from: {restDeadZone:0.00}");
                }
            }
            catch (Exception ex) { sb.Append("; the rigidbody not read (" + ex.Message + ")"); }
            return sb.ToString();
        }

        private static string Hands(float steer, float x) => steer >= 1f ? "never" : $"{Unlaw(steer, OffsetAt(x)) * Lock:0.0} deg";
        private static string Old(float steer) => steer >= 1f ? "never" : $"{steer * GameWheelLock:0.0} deg";
    }
}
