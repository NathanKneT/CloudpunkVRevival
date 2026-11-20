using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>Camera pose: position, yaw/pitch/roll in degrees, FOV. A keyframe can carry its time in the take (T, when Timed).</summary>
    internal struct ShotPose
    {
        public Vector3 Position;
        public float Yaw, Pitch, Roll, Fov;
        public bool Timed;
        public float T;

        public Quaternion Rotation => Quaternion.Euler(-Pitch, Yaw, Roll);

        public static ShotPose From(Vector3 position, Quaternion rotation, float fov)
        {
            var e = rotation.eulerAngles;
            return new ShotPose { Position = position, Yaw = e.y, Pitch = -Angle(e.x), Roll = Angle(e.z), Fov = fov };
        }

        /// <summary>Rounded to mm and 0.01 degree, so the saved file plays exactly the same. No time (set in the file).</summary>
        public ShotPose Rounded() => new ShotPose
        {
            Position = new Vector3(R(Position.x, 1000f), R(Position.y, 1000f), R(Position.z, 1000f)),
            Yaw = R(Angle(Yaw), 100f), Pitch = R(Pitch, 100f), Roll = R(Angle(Roll), 100f), Fov = R(Fov, 100f),
        };

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.000}, {1:0.000}, {2:0.000}) yaw {3:0.00} pitch {4:0.00} roll {5:0.00} fov {6:0.00}{7}",
                          Position.x, Position.y, Position.z, Yaw, Pitch, Roll, Fov, Timed ? string.Format(CultureInfo.InvariantCulture, " t {0:0.###} s", T) : "");

        private static float R(float v, float scale) => MathF.Round(v * scale) / scale;

        /// <summary>Wraps to -180..180.</summary>
        public static float Angle(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            else if (a < -180f) a += 360f;
            return a;
        }
    }

    /// <summary>World, or relative to the followed HOVA (or Rania).</summary>
    internal enum ShotSpace { World, Hova }

    /// <summary>
    /// 1 to 8 keyframes over a duration. Centripetal Catmull-Rom for positions (no loops or overshoot between close
    /// keys), uniform Catmull-Rom for angles and FOV. Even speed along the path with turns and zoom counted as
    /// distance, eased at both ends.
    ///
    /// Timed keys ("t" in the file) are hit exactly on time: distance is a monotone cubic of time (Fritsch-Carlson,
    /// Fritsch-Butland slopes), so it never runs backwards or past a key. Untimed keys get a time by distance. Ease
    /// then just means starting and ending at rest. Bad times fall back to even speed (reason in TimingNote).
    /// </summary>
    internal sealed class CameraPath
    {
        public const int MaxKeys = 8;
        public const int FileVersion = 1;

        public readonly List<ShotPose> Keys = new List<ShotPose>();
        public ShotSpace Space;
        public float Duration = 10f;
        public float Ease = 0.3f;
        public float Slow = 1f;
        /// <summary>Seconds into a take when the main menu's sign relights (as F11 would), or -1 for none.</summary>
        public float RelightAt = -1f;
        public string Name = "";
        public string District = ""; // where it was made, for the note
        public bool Unsaved;            // changed since last save or load

        private const float MinKeyGap = 0.001f;       // s
        private const float MetresPerDegree = 0.05f;  // a degree of turn or zoom counts as this much travel
        private const int Samples = 64; // per segment, for the even speed

        private bool built;
        private Vector3[] pos;
        private float[] yaw, pitch, roll, fov;
        private float[] along;
        private float length;

        // timed keys: checked in CheckTiming, times, distances and slopes built in Build
        private bool timed;
        private float[] keyT, keyS, slope;

        /// <summary>Key times are valid and actually change something.</summary>
        public bool Timed => timed;

        /// <summary>For the log: the key times in use, or why not (empty when no key has a time).</summary>
        public string TimingNote { get; private set; } = "";

        /// <summary>Keys or duration changed: rebuild and recheck the times.</summary>
        public void Changed()
        {
            built = false;
            CheckTiming();
        }

        public CameraPath Copy()
        {
            var c = new CameraPath { Space = Space, Duration = Duration, Ease = Ease, Slow = Slow, RelightAt = RelightAt, Name = Name, District = District, Unsaved = Unsaved };
            c.Keys.AddRange(Keys);
            c.CheckTiming();
            return c;
        }

        /// <summary>Validates key times (a later last key stretches the duration). Never throws: bad times mean even speed.</summary>
        public void CheckTiming()
        {
            timed = false;
            TimingNote = "";
            var c = CultureInfo.InvariantCulture;
            int n = Keys.Count, count = 0;
            for (int i = 0; i < n; i++) if (Keys[i].Timed) count++;
            if (count == 0) return;
            string Not(string why) => $"the keyframes' times NOT used ({why}): even speed instead";
            if (n < 2) { TimingNote = Not("a single keyframe"); return; }
            if (count == 1 && Keys[0].Timed && Keys[0].T == 0f) { TimingNote = "only keyframe 1 has a time, 0 s, which changes nothing: even speed"; return; }
            // a later last key stretches the take, but only if every time checks out
            var last = Keys[n - 1];
            float end = last.Timed && float.IsFinite(last.T) && last.T > Duration && last.T <= 600f ? last.T : Duration;
            float prev = 0f;
            int prevKey = -1;
            for (int i = 0; i < n; i++)
            {
                var k = Keys[i];
                if (!k.Timed && i != 0 && i != n - 1) continue; // gets a time by distance later
                float t = k.Timed ? k.T : i == 0 ? 0f : end;
                if (!float.IsFinite(t)) { TimingNote = Not($"keyframe {i + 1}'s t is not a number"); return; }
                if (t < 0f || t > end + 1e-4f) { TimingNote = Not(string.Format(c, "keyframe {0} at {1:0.###} s is outside the take, 0 to {2:0.###} s", i + 1, t, end)); return; }
                // at least 1 ms between keys, closer gives a speed no take can play
                if (prevKey >= 0 && t - prev < MinKeyGap)
                {
                    TimingNote = Not(string.Format(c, "keyframe {0} at {1:0.###} s{2} does not come at least 1 ms after keyframe {3} at {4:0.###} s",
                                                   i + 1, t, k.Timed ? "" : " (its default)", prevKey + 1, prev));
                    return;
                }
                prev = t;
                prevKey = i;
            }
            Duration = end;
            timed = true;
            var sb = new StringBuilder("timed keyframes:");
            for (int i = 0; i < n; i++)
            {
                var k = Keys[i];
                sb.Append(i == 0 ? " " : ", ").Append(i + 1).Append(' ');
                if (k.Timed) sb.Append("at ").Append(k.T.ToString("0.###", c)).Append(" s");
                else if (i == 0) sb.Append("at 0 s");
                else if (i == n - 1) sb.Append("at ").Append(Duration.ToString("0.###", c)).Append(" s (the end)");
                else sb.Append("(by distance)");
            }
            sb.Append(Ease > 1e-4f ? "; at rest at both ends (Ease)" : "; at speed at both ends (Ease 0)");
            TimingNote = sb.ToString();
        }

        /// <summary>Share of the move done at share x of the time: smooth accel over Ease, even speed, same braking (C2).</summary>
        public static float Eased(float x, float ease)
        {
            x = Math.Clamp(x, 0f, 1f);
            float r = Math.Clamp(ease, 0f, 0.5f);
            if (r < 1e-4f) return x;
            if (x > 0.5f) return 1f - Eased(1f - x, r);
            float vmax = 1f / (1f - r);
            if (x >= r) return vmax * (r * 0.5f + (x - r));
            float y = x / r;
            return vmax * r * (y * y * y - 0.5f * y * y * y * y);
        }

        /// <summary>x: share of the take's time, 0 to 1.</summary>
        public ShotPose Evaluate(float x)
        {
            int n = Keys.Count;
            if (n == 0) return default;
            if (n == 1) return Keys[0];
            if (!built) Build();
            int segments = n - 1;
            if (timed && length >= 1e-5f) return AtDistance(DistanceAt(Math.Clamp(x, 0f, 1f) * Duration));
            if (timed)
            {
                // nothing moves: the times only pick which (identical) key shows
                float t = Math.Clamp(x, 0f, 1f) * Duration;
                int k = 0;
                while (k < segments - 1 && keyT[k + 1] <= t) k++;
                float h = keyT[k + 1] - keyT[k];
                return At(k, h > 0f ? Math.Clamp((t - keyT[k]) / h, 0f, 1f) : 1f);
            }
            if (length < 1e-5f)
            {
                float s = Eased(x, Ease) * segments;
                int seg = Math.Min((int)s, segments - 1);
                return At(seg, Math.Clamp(s - seg, 0f, 1f));
            }
            return AtDistance(Eased(x, Ease) * length);
        }

        /// <summary>Pose at a distance along the path (turns and zoom included), 0 to length.</summary>
        private ShotPose AtDistance(float target)
        {
            int segments = Keys.Count - 1;
            // binary search in the per-sample distances
            int lo = 0, hi = along.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (along[mid] <= target) lo = mid; else hi = mid;
            }
            float span = along[hi] - along[lo];
            float f = span > 1e-7f ? (target - along[lo]) / span : 0f;
            float sample = lo + f;
            int seg = Math.Min((int)(sample / Samples), segments - 1);
            float u = sample / Samples - seg;
            return At(seg, Math.Clamp(u, 0f, 1f));
        }

        /// <summary>Timed keys: distance at time t on the monotone cubic (flat before the first key and after the last).</summary>
        private float DistanceAt(float t)
        {
            int n = keyT.Length;
            if (t <= keyT[0]) return keyS[0];
            if (t >= keyT[n - 1]) return keyS[n - 1];
            int k = 0;
            while (k < n - 2 && keyT[k + 1] <= t) k++;
            float h = keyT[k + 1] - keyT[k];
            if (h <= 0f) return keyS[k + 1];
            float u = (t - keyT[k]) / h, u2 = u * u, u3 = u2 * u;
            float s = (2f * u3 - 3f * u2 + 1f) * keyS[k] + (u3 - 2f * u2 + u) * h * slope[k]
                      + (-2f * u3 + 3f * u2) * keyS[k + 1] + (u3 - u2) * h * slope[k + 1];
            if (!float.IsFinite(s)) return keyS[k];
            return Math.Clamp(s, keyS[k], keyS[k + 1]); // monotone already, this only catches rounding
        }

        /// <summary>
        /// Timed keys: times (untimed keys placed by distance) and distances, then slopes: Fritsch-Butland weighted
        /// harmonic mean, zero at the ends with Ease, clamped to the Fritsch-Carlson bound so nothing overshoots a key.
        /// </summary>
        private void BuildTiming()
        {
            int n = Keys.Count;
            keyT = new float[n];
            keyS = new float[n];
            slope = new float[n];
            for (int i = 0; i < n; i++) keyS[i] = i == n - 1 ? length : along[i * Samples];
            int a = 0; // last anchor: a timed key, or the first/last one
            keyT[0] = Keys[0].Timed ? Keys[0].T : 0f;
            for (int i = 1; i < n; i++)
            {
                if (!Keys[i].Timed && i != n - 1) continue;
                keyT[i] = Keys[i].Timed ? Keys[i].T : Duration;
                float ds = keyS[i] - keyS[a];
                for (int j = a + 1; j < i; j++)
                {
                    float share = ds > 1e-6f ? (keyS[j] - keyS[a]) / ds : (float)(j - a) / (i - a);
                    keyT[j] = keyT[a] + (keyT[i] - keyT[a]) * share;
                }
                a = i;
            }

            var h = new float[n - 1];
            var d = new float[n - 1];
            for (int k = 0; k < n - 1; k++)
            {
                h[k] = keyT[k + 1] - keyT[k];
                d[k] = h[k] > 0f ? (keyS[k + 1] - keyS[k]) / h[k] : 0f;
                if (!float.IsFinite(d[k])) d[k] = 0f;
            }
            // at rest at the ends with Ease, and always where the camera holds before the first or after the last key
            bool rest = Ease > 1e-4f;
            slope[0] = rest || keyT[0] > 0f ? 0f : d[0];
            slope[n - 1] = rest || keyT[n - 1] < Duration ? 0f : d[n - 2];
            for (int k = 1; k < n - 1; k++)
            {
                if (d[k - 1] <= 0f || d[k] <= 0f) { slope[k] = 0f; continue; }
                float w1 = 2f * h[k] + h[k - 1], w2 = h[k] + 2f * h[k - 1];
                slope[k] = (w1 + w2) / (w1 / d[k - 1] + w2 / d[k]);
            }
            for (int k = 0; k < n - 1; k++)
            {
                if (d[k] <= 0f) { slope[k] = 0f; slope[k + 1] = 0f; continue; }
                float al = slope[k] / d[k], be = slope[k + 1] / d[k], r = al * al + be * be;
                if (r <= 9f) continue;
                float tau = 3f / MathF.Sqrt(r);
                slope[k] = tau * al * d[k];
                slope[k + 1] = tau * be * d[k];
            }
            for (int k = 0; k < n; k++) if (!float.IsFinite(slope[k])) slope[k] = 0f; // no NaN in the poses
        }

        private void Build()
        {
            int n = Keys.Count;
            pos = new Vector3[n + 2];
            yaw = new float[n + 2];
            pitch = new float[n + 2];
            roll = new float[n + 2];
            fov = new float[n + 2];
            for (int i = 0; i < n; i++)
            {
                var k = Keys[i];
                pos[i + 1] = k.Position;
                // unwrap yaw and roll so each key turns the short way
                yaw[i + 1] = i == 0 ? k.Yaw : yaw[i] + ShotPose.Angle(k.Yaw - yaw[i]);
                roll[i + 1] = i == 0 ? k.Roll : roll[i] + ShotPose.Angle(k.Roll - roll[i]);
                pitch[i + 1] = k.Pitch;
                fov[i + 1] = k.Fov;
            }
            // ends extended in a straight line so the end segments don't bend
            pos[0] = 2f * pos[1] - pos[2];
            pos[n + 1] = 2f * pos[n] - pos[n - 1];
            Extend(yaw, n); Extend(pitch, n); Extend(roll, n); Extend(fov, n);

            int segments = n - 1;
            along = new float[segments * Samples + 1];
            var prev = At(0, 0f);
            float d = 0f;
            for (int s = 0; s < segments; s++)
            {
                for (int j = 1; j <= Samples; j++)
                {
                    var p = At(s, (float)j / Samples);
                    d += (p.Position - prev.Position).magnitude
                         + MetresPerDegree * (MathF.Abs(p.Yaw - prev.Yaw) + MathF.Abs(p.Pitch - prev.Pitch) + MathF.Abs(p.Roll - prev.Roll) + MathF.Abs(p.Fov - prev.Fov));
                    along[s * Samples + j] = d;
                    prev = p;
                }
            }
            length = d;
            if (timed) BuildTiming();
            built = true;
        }

        private static void Extend(float[] v, int n)
        {
            v[0] = 2f * v[1] - v[2];
            v[n + 1] = 2f * v[n] - v[n - 1];
        }

        private ShotPose At(int seg, float u)
        {
            int i = seg + 1; // keyframe i to i+1 in the padded arrays
            return new ShotPose
            {
                Position = Centripetal(pos[i - 1], pos[i], pos[i + 1], pos[i + 2], u),
                Yaw = Uniform(yaw[i - 1], yaw[i], yaw[i + 1], yaw[i + 2], u),
                Pitch = Math.Clamp(Uniform(pitch[i - 1], pitch[i], pitch[i + 1], pitch[i + 2], u), -89.9f, 89.9f),
                Roll = Uniform(roll[i - 1], roll[i], roll[i + 1], roll[i + 2], u),
                Fov = Math.Clamp(Uniform(fov[i - 1], fov[i], fov[i + 1], fov[i + 2], u), 5f, 120f),
            };
        }

        private static float Uniform(float p0, float p1, float p2, float p3, float u) =>
            0.5f * (2f * p1 + (p2 - p0) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u + (3f * p1 - p0 - 3f * p2 + p3) * u * u * u);

        /// <summary>Centripetal Catmull-Rom (Barry and Goldman's form) between p1 and p2.</summary>
        private static Vector3 Centripetal(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float d12 = (p2 - p1).magnitude;
            if (d12 < 1e-4f) return p1; // a keyframe that only turns or zooms
            const float eps = 1e-3f;
            float t0 = 0f;
            float t1 = t0 + MathF.Max(MathF.Sqrt((p1 - p0).magnitude), eps);
            float t2 = t1 + MathF.Sqrt(d12);
            float t3 = t2 + MathF.Max(MathF.Sqrt((p3 - p2).magnitude), eps);
            float t = t1 + (t2 - t1) * u;
            Vector3 a1 = (t1 - t) / (t1 - t0) * p0 + (t - t0) / (t1 - t0) * p1;
            Vector3 a2 = (t2 - t) / (t2 - t1) * p1 + (t - t1) / (t2 - t1) * p2;
            Vector3 a3 = (t3 - t) / (t3 - t2) * p2 + (t - t2) / (t3 - t2) * p3;
            Vector3 b1 = (t2 - t) / (t2 - t0) * a1 + (t - t0) / (t2 - t0) * a2;
            Vector3 b2 = (t3 - t) / (t3 - t1) * a2 + (t - t1) / (t3 - t1) * a3;
            return (t2 - t) / (t2 - t1) * b1 + (t - t1) / (t2 - t1) * b2;
        }

        /// <summary>Hash of everything that shapes a take.</summary>
        public uint Signature()
        {
            uint h = 2166136261u;
            void Mix(float f)
            {
                uint b = (uint)BitConverter.SingleToInt32Bits(f);
                for (int i = 0; i < 4; i++) { h ^= (b >> (8 * i)) & 0xFF; h *= 16777619u; }
            }
            Mix((float)Space);
            Mix(Duration); Mix(Ease); Mix(Slow);
            if (RelightAt >= 0f) Mix(RelightAt); // only when set, so older paths keep their signature
            for (int i = 0; i < Keys.Count; i++)
            {
                var k = Keys[i];
                Mix(k.Position.x); Mix(k.Position.y); Mix(k.Position.z);
                Mix(k.Yaw); Mix(k.Pitch); Mix(k.Roll); Mix(k.Fov);
                if (k.Timed) Mix(k.T); // same
            }
            return h;
        }

        // ---- The file: BepInEx\config\CloudpunkVR-shots\<name>.json, one keyframe per line.

        public string ToJson(string district)
        {
            var c = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString(c);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"version\": {FileVersion},\n");
            sb.Append($"  \"name\": \"{Escape(Name)}\",\n");
            sb.Append($"  \"space\": \"{(Space == ShotSpace.Hova ? "hova" : "world")}\",\n");
            sb.Append($"  \"duration\": {F(Duration)},\n");
            sb.Append($"  \"ease\": {F(Ease)},\n");
            sb.Append($"  \"slow\": {F(Slow)},\n");
            if (RelightAt >= 0f) sb.Append($"  \"relightAt\": {F(RelightAt)},\n");
            sb.Append($"  \"district\": \"{Escape(district)}\",\n");
            if (Unsaved) sb.Append("  \"unsaved\": true,\n");
            sb.Append("  \"keys\": [");
            for (int i = 0; i < Keys.Count; i++)
            {
                var k = Keys[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"x\": {F(k.Position.x)}, \"y\": {F(k.Position.y)}, \"z\": {F(k.Position.z)}, ");
                sb.Append($"\"yaw\": {F(k.Yaw)}, \"pitch\": {F(k.Pitch)}, \"roll\": {F(k.Roll)}, \"fov\": {F(k.Fov)}");
                if (k.Timed) sb.Append($", \"t\": {(float.IsFinite(k.T) ? F(k.T) : "null")}");
                sb.Append(" }");
            }
            sb.Append(Keys.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Null plus error when the file can't be used.</summary>
        public static CameraPath FromJson(string text, out string error)
        {
            error = null;
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                var p = new CameraPath();
                // must be a valid file name (it names the take folders too)
                if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && ValidName(name.GetString())) p.Name = name.GetString();
                if (root.TryGetProperty("unsaved", out var unsaved) && unsaved.ValueKind == JsonValueKind.True) p.Unsaved = true;
                if (root.TryGetProperty("district", out var district) && district.ValueKind == JsonValueKind.String) p.District = district.GetString() ?? "";
                if (root.TryGetProperty("space", out var space)) p.Space = string.Equals(space.GetString(), "hova", StringComparison.OrdinalIgnoreCase) ? ShotSpace.Hova : ShotSpace.World;
                p.Duration = Math.Clamp(Number(root, "duration", 10f), 1f, 600f);
                p.Ease = Math.Clamp(Number(root, "ease", 0.3f), 0f, 0.5f);
                p.Slow = Math.Clamp(Number(root, "slow", 1f), FreeCam.SlowestSlow, 1f);
                float relight = Number(root, "relightAt", -1f);
                p.RelightAt = relight >= 0f ? Math.Min(relight, 600f) : -1f;
                if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in keys.EnumerateArray())
                    {
                        if (p.Keys.Count == MaxKeys) { error = $"more than {MaxKeys} keyframes; the first {MaxKeys} kept"; break; }
                        const float far = 100000f; // sanity clamp for hand-edited files
                        var key = new ShotPose
                        {
                            Position = new Vector3(Math.Clamp(Number(k, "x", 0f), -far, far), Math.Clamp(Number(k, "y", 0f), -far, far), Math.Clamp(Number(k, "z", 0f), -far, far)),
                            Yaw = ShotPose.Angle(Number(k, "yaw", 0f)),
                            Pitch = Math.Clamp(Number(k, "pitch", 0f), -89.9f, 89.9f),
                            Roll = ShotPose.Angle(Number(k, "roll", 0f)),
                            Fov = Math.Clamp(Number(k, "fov", 60f), 5f, 120f),
                        };
                        // a non-number t is kept as NaN and CheckTiming reports it
                        if (k.ValueKind == JsonValueKind.Object && k.TryGetProperty("t", out _))
                        {
                            key.Timed = true;
                            key.T = Number(k, "t", float.NaN);
                        }
                        p.Keys.Add(key);
                    }
                }
                p.CheckTiming();
                return p;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static float Number(JsonElement e, string name, float fallback)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out float f)) return fallback;
            return float.IsFinite(f) ? f : fallback;
        }

        /// <summary>Plain file name, no path, no spaces.</summary>
        public static bool ValidName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= 40 && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
            && name.IndexOf(' ') < 0 && name != "." && name != "..";
    }
}
