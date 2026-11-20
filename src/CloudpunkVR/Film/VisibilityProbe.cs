#if DEV
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using BitOperations = System.Numerics.BitOperations;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Dev "freecam probe on": checks nothing pops, from Renderer.isVisible of mesh renderers in view
    /// (isVisible alone also counts shadows and mirrors). Flags blinks and diffs with the last take. Costs a few ms.
    /// </summary>
    internal static class VisibilityProbe
    {
        private static MeshRenderer[] renderers;
        private static Vector3[] startPos;
        private static Vector3[] centre, extents; // world bounds when listed (still renderers keep them)
        private static string scenes;
        private static int words;

        private static readonly Dictionary<uint, Run> lastOfPath = new Dictionary<uint, Run>();
        private const double Tolerance = 0.001; // share of drawn renderer-frames allowed to differ

        public static Run Begin(int maxFrames)
        {
            string now = Scenes();
            if (renderers == null || now != scenes)
            {
                var found = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
                renderers = new MeshRenderer[found.Length];
                startPos = new Vector3[found.Length];
                centre = new Vector3[found.Length];
                extents = new Vector3[found.Length];
                for (int i = 0; i < found.Length; i++)
                {
                    var r = found[i];
                    renderers[i] = r;
                    startPos[i] = r.transform.position;
                    var b = r.bounds;
                    centre[i] = b.center;
                    extents[i] = b.extents;
                }
                words = (renderers.Length + 63) / 64;
                scenes = now;
                lastOfPath.Clear();
                Plugin.L.LogInfo($"freecam probe: {renderers.Length} mesh renderers in {now}, read every frame of the takes.");
            }
            return new Run(maxFrames);
        }

        /// <summary>The view's six planes, normals inward (xyz) and offset (w).</summary>
        private static void Frustum(Vector3 p, Quaternion r, float fov, float aspect, float near, float far, Span<Vector4> planes)
        {
            Vector3 f = r * Vector3.forward, u = r * Vector3.up, s = r * Vector3.right;
            float tv = MathF.Tan(fov * 0.5f * MathF.PI / 180f), th = tv * aspect;
            planes[0] = Plane(f, p + f * near);
            planes[1] = Plane(-f, p + f * far);
            planes[2] = Plane(s + th * f, p);
            planes[3] = Plane(-s + th * f, p);
            planes[4] = Plane(u + tv * f, p);
            planes[5] = Plane(-u + tv * f, p);
        }

        private static Vector4 Plane(Vector3 n, Vector3 through)
        {
            float len = MathF.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
            n = new Vector3(n.x / len, n.y / len, n.z / len);
            return new Vector4(n.x, n.y, n.z, -(n.x * through.x + n.y * through.y + n.z * through.z));
        }

        /// <summary>Box inside all six planes (conservative).</summary>
        private static bool InView(Span<Vector4> planes, Vector3 c, Vector3 e)
        {
            for (int i = 0; i < 6; i++)
            {
                var q = planes[i];
                float reach = MathF.Abs(q.x) * e.x + MathF.Abs(q.y) * e.y + MathF.Abs(q.z) * e.z;
                if (q.x * c.x + q.y * c.y + q.z * c.z + q.w + reach < 0f) return false;
            }
            return true;
        }

        private static string Scenes()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++) sb.Append(SceneManager.GetSceneAt(i).name).Append(' ');
            return sb.ToString().Trim();
        }

        internal sealed class Run
        {
            private readonly ulong[][] frames;  // drawn and in view
            private readonly ulong[][] hidden;  // in view but not drawn
            private int count;
            private ulong[] still; // present and unmoved for the whole take

            public Run(int maxFrames)
            {
                frames = new ulong[Math.Max(1, maxFrames)][];
                hidden = new ulong[frames.Length][];
            }

            /// <summary>What Unity drew in that frame, read during the next one.</summary>
            public void Sample(int frame, Vector3 p, Quaternion r, float fov, float aspect, float near, float far)
            {
                if (frame < 0 || frame >= frames.Length || renderers == null) return;
                Span<Vector4> planes = stackalloc Vector4[6];
                Frustum(p, r, fov, aspect, near, far, planes);
                var bits = new ulong[words];
                var notDrawn = new ulong[words];
                var list = renderers;
                for (int i = 0; i < list.Length; i++)
                {
                    if (!InView(planes, centre[i], extents[i])) continue;
                    var x = list[i];
                    if (x.m_CachedPtr == IntPtr.Zero) continue;
                    if (x.isVisible) bits[i >> 6] |= 1UL << (i & 63);
                    else notDrawn[i >> 6] |= 1UL << (i & 63);
                }
                frames[frame] = bits;
                hidden[frame] = notDrawn;
                if (frame + 1 > count) count = frame + 1;
            }

            public void Report(int take, uint signature)
            {
                if (count == 0) return;
                still = new ulong[words];
                int moved = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    bool keep = r.m_CachedPtr != IntPtr.Zero && (r.transform.position - startPos[i]).sqrMagnitude < 1e-4f;
                    if (keep) still[i >> 6] |= 1UL << (i & 63);
                    else moved++;
                }

                // drawn per frame, and blinks
                int fewest = int.MaxValue, most = 0, blinks = 0;
                for (int f = 0; f < count; f++)
                {
                    var bits = frames[f];
                    if (bits == null) continue;
                    int n = 0;
                    for (int w = 0; w < words; w++) n += BitOperations.PopCount(bits[w] & still[w]);
                    fewest = Math.Min(fewest, n);
                    most = Math.Max(most, n);
                }
                var blinkNames = new List<string>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    ulong mask = 1UL << (i & 63);
                    int w = i >> 6;
                    if ((still[w] & mask) == 0) continue;
                    int lastSeen = -1;
                    for (int f = 0; f < count; f++)
                    {
                        var bits = frames[f];
                        if (bits == null) continue;
                        if ((bits[w] & mask) == 0) continue;
                        int gap = f - lastSeen - 1;
                        if (lastSeen >= 0 && gap >= 1 && gap <= 3 && HiddenThrough(w, mask, lastSeen + 1, f))
                        {
                            blinks++;
                            if (blinkNames.Count < 5) blinkNames.Add($"{renderers[i].name} (frame {lastSeen + 1})");
                        }
                        lastSeen = f;
                    }
                }
                Plugin.L.LogInfo($"freecam take {take} probe: {renderers.Length - moved} still mesh renderers ({moved} moved or gone, left out), " +
                                 $"{(fewest == int.MaxValue ? 0 : fewest)} to {most} drawn in view per frame over {count} frames, " +
                                 $"{blinks} blink(s) (drawn, gone for 1 to 3 frames while in view, drawn again){(blinkNames.Count > 0 ? ": " + string.Join(", ", blinkNames) : "")}.");

                if (lastOfPath.TryGetValue(signature, out var before))
                {
                    int n = Math.Min(count, before.count), differingFrames = 0, differing = 0;
                    long drawn = 0;
                    var byRenderer = new Dictionary<int, int>();
                    for (int f = 0; f < n; f++)
                    {
                        var a = frames[f];
                        var b = before.frames[f];
                        if (a == null || b == null) continue;
                        bool any = false;
                        for (int w = 0; w < words; w++)
                        {
                            drawn += BitOperations.PopCount((a[w] | b[w]) & still[w] & before.still[w]);
                            ulong diff = (a[w] ^ b[w]) & still[w] & before.still[w];
                            if (diff == 0) continue;
                            any = true;
                            differing += BitOperations.PopCount(diff);
                            while (diff != 0)
                            {
                                int bit = BitOperations.TrailingZeroCount(diff);
                                diff &= diff - 1;
                                int idx = (w << 6) + bit;
                                byRenderer.TryGetValue(idx, out int k);
                                byRenderer[idx] = k + 1;
                            }
                        }
                        if (any) differingFrames++;
                    }
                    var top = new List<KeyValuePair<int, int>>(byRenderer);
                    top.Sort((x, y) => y.Value.CompareTo(x.Value));
                    var names = new StringBuilder();
                    for (int i = 0; i < top.Count && i < 6; i++)
                        names.Append(i == 0 ? "" : ", ").Append(renderers[top[i].Key].name).Append(" (").Append(top[i].Value).Append(')');
                    // the game toggles some signs and props itself: allow 0.1%
                    double share = drawn > 0 ? (double)differing / drawn : 0.0;
                    string pct = (share * 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    if (differingFrames == 0 && count == before.count)
                        Plugin.L.LogInfo($"freecam probe check: take {take} drew the same still renderers as take {before.Number} in all {n} frames.");
                    else if (share <= Tolerance && count == before.count)
                        Plugin.L.LogInfo($"freecam probe check: take {take} drew the same still renderers as take {before.Number} but for {differing} of {drawn} renderer-frames " +
                                         $"({pct}%, within 0.1%; in {differingFrames} frames: {names}).");
                    else
                        Plugin.L.LogWarning($"freecam probe check FAILED: take {take} against take {before.Number}: {differingFrames} of {n} frames drew other still renderers " +
                                            $"({differing} of {drawn} renderer-frames, {pct}%; most: {names}){(count != before.count ? $"; {count} and {before.count} frames" : "")}.");
                }
                else Plugin.L.LogInfo($"freecam take {take} probe: the first probed take of this path with this culling, kept to check the next one against.");
                Number = take;
                lastOfPath[signature] = this;
            }

            public int Number { get; private set; }

            // in view but not drawn for the whole gap: a blink, not the camera turning away
            private bool HiddenThrough(int w, ulong mask, int first, int last)
            {
                for (int f = first; f < last; f++)
                    if (hidden[f] == null || (hidden[f][w] & mask) == 0) return false;
                return true;
            }
        }
    }
}
#endif
