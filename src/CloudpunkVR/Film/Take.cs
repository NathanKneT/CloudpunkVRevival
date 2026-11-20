using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CloudpunkVR.Film
{
    public enum FrameFormat { Off, Png, Jpg }

    /// <summary>
    /// One play of a path, after a short still pre-roll so bloom and volumetric light settle.
    /// With a Framerate it runs on fixed steps (Time.captureFramerate), so the same path gives the same frames.
    /// </summary>
    internal sealed class Take
    {
        public const float PreRollSeconds = 0.5f;

        public readonly int Number;
        public readonly CameraPath Path;
        public readonly uint Signature;
        public readonly int Fps; // 0: real time
        public readonly string FollowName; // null if not following
        private readonly string where;     // "in the city", "on the main menu"
        public bool Rolling { get; private set; }
        public bool Done { get; private set; }
        /// <summary>The path relights the menu sign but there's no sign here (warned once).</summary>
        public bool SignMissing;

        private readonly int lastFrame; // fixed steps: the final frame's index
        private int preRollLeft;
        private readonly float preRollUntil;
        private int frame;
        private float t;
        private float realStart;
        private readonly List<Pose> poses = new List<Pose>();

        private int savedCapture, savedVSync, savedTarget;
        private bool paced;

        // frames to disk
        private const int MaxGrabFailures = 5;
        private FrameFormat format;
        /// <summary>Set when there's no room for the frames.</summary>
        public string FramesRefused { get; private set; }
        private readonly float[] grabTimes; // dev "frames at" times, else every frame
        private string folder;
        private int grabFrame = -1, grabbed, grabFailures;
        private Texture2D grabTex;

        private int frameW, frameH;
#if DEV
        // render cost: wall clock, pre-roll, readback and encode per frame
        private readonly System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        private double preRollWall, readMs, encodeMs;
        private long bytes;
#endif

        // follow statistics
        private Vector3 targetStart, targetLast;
        private float nearest = float.MaxValue, farthest;

#if DEV
        private readonly VisibilityProbe.Run probe;
        private float aspect = 16f / 9f, near = 0.05f, far = 600f; // for the probe's frustum test
        private readonly uint probeKey; // path + occlusion setting, so only like is compared
#endif

        private struct Pose
        {
            public Vector3 P;
            public Quaternion R;
            public float Fov;
            public float T;
        }

        public Take(int number, CameraPath path, int fps, string followName, string where, FrameFormat format, float[] grabTimes, bool probe, bool gameOcclusion)
        {
            Number = number;
            Path = path;
            Signature = path.Signature();
            Fps = fps;
            FollowName = followName;
            this.where = where;
            this.format = format;
            this.grabTimes = grabTimes;
            lastFrame = fps > 0 ? Math.Max(1, (int)MathF.Round(path.Duration * fps)) : 0;
            preRollLeft = fps > 0 ? Math.Max(1, (int)MathF.Round(PreRollSeconds * fps)) : 0;
            preRollUntil = Time.realtimeSinceStartup + PreRollSeconds;
#if DEV
            probeKey = gameOcclusion ? Signature ^ 0x5bd1e995u : Signature;
            if (probe) this.probe = VisibilityProbe.Begin(fps > 0 ? lastFrame + 1 : (int)(path.Duration * 240f) + 2);
#endif
            if (format != FrameFormat.Off && grabTimes == null) CheckRoom();
            Pace();
        }

        public bool WantsGrab => format != FrameFormat.Off;

        /// <summary>A 10 s Png take at 4K is about 6 GB. No frames if the disk can't hold them.</summary>
        private void CheckRoom()
        {
            try
            {
                long pixels = (long)Screen.width * Screen.height;
                long perFrame = format == FrameFormat.Png ? pixels * 3 : pixels / 2; // Png at most raw, Jpg at quality 95 well under
                long frames = Fps > 0 ? lastFrame + 1 : (long)(Path.Duration * 60f);
                long need = perFrame * frames + (1L << 30);
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(BepInEx.Paths.BepInExRootPath));
                long free = drive.AvailableFreeSpace;
                if (free >= need) return;
                FramesRefused = $"{format} frames need up to {need >> 30} GB, {free >> 30} GB free on {drive.Name}";
                format = FrameFormat.Off;
            }
            catch (Exception e) { Plugin.L.LogWarning($"freecam take {Number}: free disk space not read ({e.Message}); frames saved anyway."); }
        }

        public void Lens(float aspect, float near, float far)
        {
#if DEV
            this.aspect = aspect;
            this.near = near;
            this.far = far;
#endif
        }

        private void SampleProbe()
        {
#if DEV
            if (probe == null || frame >= poses.Count) return;
            var p = poses[frame];
            probe.Sample(frame, p.P, p.R, p.Fov, aspect, near, far);
#endif
        }

        /// <summary>Per frame, before the pose: the share of path time to show.</summary>
        public float Step()
        {
            if (!Rolling)
            {
                bool settled = Fps > 0 ? --preRollLeft <= 0 : Time.realtimeSinceStartup >= preRollUntil;
                if (!settled) return 0f;
                Rolling = true;
                realStart = Time.realtimeSinceStartup;
#if DEV
                preRollWall = wall.Elapsed.TotalSeconds;
#endif
                frame = 0;
                t = 0f;
                return 0f;
            }
            // sample the previous frame, now that it's drawn
            SampleProbe();
            frame++;
            if (Fps > 0) t = TimeOfFrame(frame);
            else t = Math.Min(t + Time.unscaledDeltaTime, Path.Duration);
            return t / Path.Duration;
        }

        /// <summary>Fixed steps: path time at frame f.</summary>
        private float TimeOfFrame(int f) => Math.Min((float)f / Fps, Path.Duration);

        /// <summary>
        /// Sign relight clock at frame f (path time minus relightAt), 0.1 ms ahead so a cue exactly on a frame's time
        /// lands on that frame despite float rounding. Same sum drives the sign and numbers the cues.
        /// </summary>
        private float RelightClock(int f) => TimeOfFrame(f) - Path.RelightAt + 1e-4f;

        /// <summary>Relight clock for this frame, negative (dark) during the pre-roll.</summary>
        public float RelightClockNow => !Rolling ? -1f : Fps > 0 ? RelightClock(frame) : t - Path.RelightAt;

        /// <summary>This frame's pose, as written to the camera.</summary>
        public void Record(Vector3 p, Quaternion r, float fov, Transform target)
        {
            if (!Rolling) return;
            poses.Add(new Pose { P = p, R = r, Fov = fov, T = t });
            if (target != null)
            {
                var tp = target.position;
                if (poses.Count == 1) targetStart = tp;
                targetLast = tp;
                float d = (p - tp).magnitude;
                nearest = MathF.Min(nearest, d);
                farthest = MathF.Max(farthest, d);
            }
            if (WantsGrab && Wanted(frame)) grabFrame = frame;
            bool last = Fps > 0 ? frame >= lastFrame : t >= Path.Duration;
            if (last) Done = true;
        }

        private bool Wanted(int f)
        {
            if (grabTimes == null) return true;
            float ft = Fps > 0 ? (float)f / Fps : t;
            float step = Fps > 0 ? 0.5f / Fps : 1f / 120f;
            for (int i = 0; i < grabTimes.Length; i++)
                if (MathF.Abs(grabTimes[i] - ft) < step) return true;
            return false;
        }

        // ---- Pacing: fixed steps at Fps, presented at Fps.

        private void Pace()
        {
            if (Fps <= 0) return;
            savedCapture = Time.captureFramerate;
            savedVSync = QualitySettings.vSyncCount;
            savedTarget = Application.targetFrameRate;
            Time.captureFramerate = Fps;
            // refresh a multiple of the rate: vsync to it, else cap the frame rate
            int hz = Screen.currentResolution.refreshRate;
            if (hz > 0 && hz % Fps == 0 && hz / Fps <= 4) { QualitySettings.vSyncCount = hz / Fps; Application.targetFrameRate = -1; }
            else { QualitySettings.vSyncCount = 0; Application.targetFrameRate = Fps; }
            paced = true;
        }

        private void Unpace()
        {
            if (!paced) return;
            paced = false;
            Time.captureFramerate = savedCapture;
            QualitySettings.vSyncCount = savedVSync;
            Application.targetFrameRate = savedTarget;
            Plugin.L.LogInfo($"freecam take {Number}: pacing back (capture rate {Time.captureFramerate}, vsync {QualitySettings.vSyncCount}, target frame rate {Application.targetFrameRate}).");
        }

        // ---- Frames to disk, at end of frame (FreeCam's coroutine).

        public void GrabAtEndOfFrame()
        {
            int f = grabFrame;
            if (f < 0 || grabFailures >= MaxGrabFailures) return;
            grabFrame = -1;
            try
            {
                if (folder == null) folder = FrameFile.Folder(Path, $"take{Number}");
                string file = System.IO.Path.Combine(folder, f.ToString("00000", CultureInfo.InvariantCulture) + (format == FrameFormat.Jpg ? ".jpg" : ".png"));
#if DEV
                bytes += FrameFile.Write(ref grabTex, file, format, out double read, out double encode);
                readMs += read;
                encodeMs += encode;
#else
                FrameFile.Write(ref grabTex, file, format, out _, out _);
#endif
                grabbed++;
                if (grabbed == 1)
                {
                    frameW = grabTex.width;
                    frameH = grabTex.height;
                    Plugin.L.LogInfo($"freecam take {Number}: frames of {frameW}x{frameH}, read whole from the game's own picture ({WindowSize.Short()}), in {folder}.");
                }
                if (grabTimes != null)
                    Plugin.L.LogInfo($"freecam take {Number} frame {f} ({(Fps > 0 ? (float)f / Fps : t).ToString("0.00", CultureInfo.InvariantCulture)} s) saved: {file}");
            }
            catch (Exception e)
            {
                grabFailures++;
                if (grabFailures == 1) Plugin.L.LogWarning($"freecam take {Number}: frame {f} not saved: {e.Message}");
                if (grabFailures == MaxGrabFailures) Plugin.L.LogWarning($"freecam take {Number}: {MaxGrabFailures} frames failed to save, no more frames saved for this take.");
            }
        }

        // ---- The end.

        /// <summary>Restores pacing and logs the report.</summary>
        public void Finish(string stoppedBecause)
        {
            Unpace();
            if (grabTex != null) { UnityEngine.Object.Destroy(grabTex); grabTex = null; }
            if (Rolling && stoppedBecause == null) SampleProbe();
            var c = CultureInfo.InvariantCulture;
            float real = Time.realtimeSinceStartup - realStart;
            string name = string.IsNullOrEmpty(Path.Name) ? "unsaved path" : $"path '{Path.Name}'";
            if (stoppedBecause != null)
            {
                Plugin.L.LogInfo($"freecam take {Number} stopped after {poses.Count} frames: {stoppedBecause}.");
#if DEV
                Render();
#endif
                return;
            }
            var sb = new StringBuilder();
            sb.Append($"freecam take {Number} done: {name} (sig {Signature:x8}, {Path.Keys.Count} keyframe(s), {(Path.Space == ShotSpace.Hova ? "around " + FollowName : FollowName != null ? "following " + FollowName : where)}), ");
            sb.Append(Fps > 0 ? $"{poses.Count} frames at {Fps} fps fixed, " : $"{poses.Count} frames in real time, ");
            sb.Append(string.Format(c, "{0:0.00} s of video in {1:0.00} s ({2:0.0} fps drawn), world at x{3:0.##}, ease {4:0.##}",
                                    Path.Duration, real, real > 0f ? poses.Count / real : 0f, Path.Slow, Path.Ease));
            if (grabbed > 0) sb.Append($"; {grabbed} frame(s) saved in {folder}" + (grabTimes == null && Fps > 0 ? $" (ffmpeg -framerate {Fps} -i %05d.{(format == FrameFormat.Jpg ? "jpg" : "png")} ...)" : ""));
            if (grabFailures > 0) sb.Append($"; {grabFailures} frame(s) failed to save{(grabFailures >= MaxGrabFailures ? ", saving stopped" : "")}");
            if (FramesRefused != null) sb.Append($"; no frames saved: {FramesRefused}");
            Plugin.L.LogInfo(sb.ToString());

            // poses at five times, plus a checksum of all
            uint sum = 2166136261u;
            void Mix(float f)
            {
                uint b = (uint)BitConverter.SingleToInt32Bits(f);
                for (int i = 0; i < 4; i++) { sum ^= (b >> (8 * i)) & 0xFF; sum *= 16777619u; }
            }
            for (int i = 0; i < poses.Count; i++)
            {
                var p = poses[i];
                Mix(p.P.x); Mix(p.P.y); Mix(p.P.z); Mix(p.R.x); Mix(p.R.y); Mix(p.R.z); Mix(p.R.w); Mix(p.Fov);
            }
            var at = new StringBuilder();
            for (int q = 0; q <= 4; q++)
            {
                float want = Path.Duration * q / 4f;
                int idx = poses.FindIndex(p => p.T >= want - 1e-4f);
                if (idx < 0) idx = poses.Count - 1;
                if (idx < 0) break;
                var p = poses[idx];
                var e = ShotPose.From(p.P, p.R, p.Fov);
                at.Append(string.Format(c, "\n  {0:0.00} s (frame {1}): {2}", p.T, idx, e));
            }
            Plugin.L.LogInfo($"freecam take {Number} poses, checksum {sum:x8}:{at}");

            if (FollowName != null && poses.Count > 1)
                Plugin.L.LogInfo(string.Format(c, "freecam take {0} follow: {1} moved {2:0.0} m during the take; the camera stayed {3:0.0} to {4:0.0} m from it",
                                                Number, FollowName, (targetLast - targetStart).magnitude, nearest, farthest));

            Compare(sum);
#if DEV
            probe?.Report(Number, probeKey);
            Render();
#endif
            if (Path.RelightAt >= 0f && !SignMissing) RelightCues();
        }

#if DEV
        /// <summary>What the take cost to draw and save.</summary>
        private void Render()
        {
            var c = CultureInfo.InvariantCulture;
            double total = wall.Elapsed.TotalSeconds, rolling = Math.Max(0.0, total - preRollWall);
            if (grabbed == 0)
            {
                Plugin.L.LogInfo(string.Format(c, "freecam take {0} render: {1} frames drawn in {2:0.0} s ({3:0.0} s of it the still pre-roll), none saved.",
                                               Number, poses.Count, total, preRollWall));
                return;
            }
            Plugin.L.LogInfo(string.Format(c, "freecam take {0} render: {1} {2} frames of {3}x{4} in {5:0.0} s ({6:0.0} s of it the still pre-roll), {7:0.000} s a frame, " +
                                              "of which reading back {8:0.0} ms and encoding and writing {9:0.0} ms; {10:0} MB in {11}",
                                           Number, grabbed, format, frameW, frameH, total, preRollWall, rolling / Math.Max(1, poses.Count),
                                           readMs / grabbed, encodeMs / grabbed, bytes / 1048576.0, folder));
        }
#endif

        /// <summary>
        /// The sign's crackle times in the take, to lay the sound in the edit: time, first frame, length.
        /// Logged and written as relight-cues.txt beside the frames.
        /// </summary>
        private void RelightCues()
        {
            var cues = UI.MenuSign.CueTimes();
            if (cues.Count == 0) return;
            var c = CultureInfo.InvariantCulture;
            var line = new StringBuilder();
            var file = new StringBuilder();
            string which = string.IsNullOrEmpty(Path.Name) ? "an unsaved path" : $"path '{Path.Name}'";
            file.AppendLine($"Relight cues of take {Number}, {which}" + (Fps > 0 ? $", {Fps} fps: frame 0 (00000.png) is the take's start." : ", in real time (no frame numbers)."));
            file.AppendLine(string.Format(c, "The relight starts {0:0.00} s into the take (relightAt). Each cue is a crackle of the game's Sparks_Slow_Audio at {1:0}% volume,",
                                          Path.RelightAt, UI.MenuSign.CueVolume * 100f));
            file.AppendLine("fading out over its last 0.1 s. The sound is not in the frames: lay it in at these times.");
            file.AppendLine();
            file.AppendLine("  time (s)   frame   length (s)   letter          Sparks_Slow_Audio from (s)");
            foreach (var cue in cues)
            {
                float at = Path.RelightAt + cue.At;
                int f = FirstFrameAt(cue.At);
                string frameText = Fps <= 0 ? "-" : f < 0 ? "after the end" : f.ToString(c);
                line.Append(line.Length > 0 ? ", " : "").Append(string.Format(c, "{0:0.00} ({1}{2}, {3:0.00} s, {4})", at, Fps > 0 && f >= 0 ? "frame " : "", frameText, cue.Seconds, cue.Label));
                file.AppendLine(string.Format(c, "  {0,8:0.000}   {1,5}   {2,10:0.00}   {3,-15} {4:0.00}", at, frameText, cue.Seconds, cue.Label, cue.Clip));
            }
            float litAt = Path.RelightAt + UI.MenuSign.LitBy;
            int litFrame = FirstFrameAt(UI.MenuSign.LitBy);
            string lit = string.Format(c, "all lit at {0:0.00} s{1}", litAt, Fps > 0 ? (litFrame >= 0 ? $" (frame {litFrame})" : " (after the take's end)") : "");
            file.AppendLine();
            file.AppendLine(char.ToUpperInvariant(lit[0]) + lit.Substring(1) + ".");
            string written = "";
            if (folder != null)
            {
                string path = System.IO.Path.Combine(folder, "relight-cues.txt");
                try { File.WriteAllText(path, file.ToString()); written = "; written to " + path; }
                catch (Exception e) { written = "; relight-cues.txt not written: " + e.Message; }
            }
            Plugin.L.LogInfo(string.Format(c, "freecam take {0} relight cues (s from take start, the relight from {1:0.00} s): {2}; {3}{4}",
                                           Number, Path.RelightAt, line, lit, written));
        }

        /// <summary>First frame whose relight clock reaches s, -1 past the end.</summary>
        private int FirstFrameAt(float s)
        {
            if (Fps <= 0) return -1;
            int f = Math.Max(0, (int)((Path.RelightAt + s) * Fps) - 2);
            for (; f <= lastFrame; f++)
                if (RelightClock(f) >= s) return f;
            return -1;
        }

        // ---- Same path played again should give the same frames.

        private sealed class Kept
        {
            public int Number, Fps;
            public Pose[] Poses;
        }

        private static readonly Dictionary<uint, Kept> lastOfPath = new Dictionary<uint, Kept>();

        private void Compare(uint checksum)
        {
            if (Fps <= 0) { Plugin.L.LogInfo($"freecam take {Number}: real time (Framerate = 0), so no frame-for-frame check."); return; }
            if (FollowName != null) { Plugin.L.LogInfo($"freecam take {Number}: the camera follows {FollowName}, who moves differently each time, so no frame-for-frame check."); return; }
            var mine = poses.ToArray();
            if (!lastOfPath.TryGetValue(Signature, out var before) || before.Fps != Fps)
            {
                lastOfPath[Signature] = new Kept { Number = Number, Fps = Fps, Poses = mine };
                Plugin.L.LogInfo($"freecam take {Number}: first take of this path at {Fps} fps, kept to check the next one against.");
                return;
            }
            int n = Math.Min(mine.Length, before.Poses.Length), same = 0, first = -1;
            float worstMm = 0f, worstDeg = 0f;
            for (int i = 0; i < n; i++)
            {
                var a = mine[i];
                var b = before.Poses[i];
                // bitwise: Unity's == on vectors has a tolerance
                bool identical = a.P.x == b.P.x && a.P.y == b.P.y && a.P.z == b.P.z
                                 && a.R.x == b.R.x && a.R.y == b.R.y && a.R.z == b.R.z && a.R.w == b.R.w && a.Fov == b.Fov;
                if (identical) { same++; continue; }
                if (first < 0) first = i;
                worstMm = MathF.Max(worstMm, (a.P - b.P).magnitude * 1000f);
                float dot = MathF.Min(1f, MathF.Abs(a.R.x * b.R.x + a.R.y * b.R.y + a.R.z * b.R.z + a.R.w * b.R.w));
                worstDeg = MathF.Max(worstDeg, 2f * MathF.Acos(dot) * (180f / MathF.PI));
            }
            var c = CultureInfo.InvariantCulture;
            if (same == n && mine.Length == before.Poses.Length)
                Plugin.L.LogInfo($"freecam take check: take {Number} = take {before.Number} frame for frame ({same}/{n} poses identical, path {Signature:x8}, checksum {checksum:x8}).");
            else
                Plugin.L.LogWarning(string.Format(c, "freecam take check FAILED: take {0} against take {1}: {2}/{3} poses identical ({4} and {5} frames), first difference at frame {6}, up to {7:0.000} mm and {8:0.0000} degrees.",
                                                  Number, before.Number, same, n, mine.Length, before.Poses.Length, first, worstMm, worstDeg));
            lastOfPath[Signature] = new Kept { Number = Number, Fps = Fps, Poses = mine };
        }
    }

    /// <summary>The game's final picture to disk, for takes and keyframe stills.</summary>
    internal static class FrameFile
    {
        /// <summary>New folder in BepInEx\CloudpunkVR-takes for the frames, with path.json beside them.</summary>
        public static string Folder(CameraPath path, string what)
        {
            string name = CameraPath.ValidName(path.Name) ? path.Name : "path";
            string folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "CloudpunkVR-takes", $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}-{what}");
            Directory.CreateDirectory(folder);
            try { File.WriteAllText(Path.Combine(folder, "path.json"), path.ToJson(path.District)); }
            catch (Exception e) { Plugin.L.LogWarning($"freecam: path.json not written beside the frames: {e.Message}"); }
            return folder;
        }

        /// <summary>Reads back the finished frame at window size and writes it. Returns bytes; ms for readback and encode.</summary>
        public static long Write(ref Texture2D tex, string file, FrameFormat format, out double readMs, out double encodeMs)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int w = Screen.width, h = Screen.height;
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            }
            var prev = RenderTexture.active;
            RenderTexture.active = null;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            // straight from the Il2Cpp array, no 10 MB managed copy per frame
            var bytes = format == FrameFormat.Jpg ? ImageConversion.EncodeToJPG(tex, 95) : ImageConversion.EncodeToPNG(tex);
            using (var stream = File.Create(file)) stream.Write(bytes.AsSpan());
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            readMs = (t1 - t0) * tick;
            encodeMs = (t2 - t1) * tick;
            return bytes.Length;
        }
    }
}
