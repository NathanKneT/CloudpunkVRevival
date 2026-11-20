#if DEV
using System;
using System.Globalization;
using System.IO;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Free cam dev tools for render tests:
    /// `freecam keys [png|jpg]` one still at each keyframe (held half a second on fixed steps so the effects settle),
    /// key1.png, key2.png... in BepInEx\CloudpunkVR-takes\time-shot-keys with the path;
    /// `freecam relight <s>|off` when a take relights the menu sign (relightAt);
    /// `freecam aim sign` or `aim <x> <y> <z>` points the camera at the sign or a point.
    /// </summary>
    public partial class FreeCam
    {
        private sealed class StillRun
        {
            public CameraPath Path;
            public int Index, Settle, SettleFrames, Fps, SavedCapture, Written;
            public FrameFormat Format;
            public string Folder;
            public ShotPose Before;
            public bool Grab;
            public Texture2D Tex;
            public long Bytes;
            public double ReadMs, EncodeMs;
            public readonly System.Diagnostics.Stopwatch Wall = System.Diagnostics.Stopwatch.StartNew();
        }

        private static StillRun still;

        private static string Stills(string format)
        {
            if (take != null) return "a take is running.";
            if (path.Keys.Count == 0) return "no keyframes.";
            if (path.Space == ShotSpace.Hova && !following && !StartFollow())
                return menuMode ? "this path moves around the HOVA: not on the main menu." : "this path moves around the HOVA, and there is nothing to follow.";
            if (path.Space == ShotSpace.World) StopFollow();
            int fps = VRConfig.FreeCamFramerate.Value > 0 ? VRConfig.FreeCamFramerate.Value : 60;
            var s = new StillRun
            {
                Path = path.Copy(),
                Fps = fps,
                Format = format == "jpg" ? FrameFormat.Jpg : FrameFormat.Png,
                SettleFrames = Math.Max(1, (int)MathF.Round(Take.PreRollSeconds * fps)),
                Before = pose,
                SavedCapture = Time.captureFramerate,
            };
            s.Settle = s.SettleFrames;
            try { s.Folder = FrameFile.Folder(s.Path, "keys"); }
            catch (Exception e) { return "no folder for the stills: " + e.Message; }
            still = s;
            stills = true;
            flyVelocity = Vector3.zero;
            Time.captureFramerate = fps; // fixed steps: same settle time however slow the frames
            instance.StartCoroutine(StillGrabber(s).WrapToIl2Cpp());
            Plugin.L.LogInfo($"freecam keys: {s.Path.Keys.Count} still(s) of {DescribePath()}, each posed at its keyframe and held {s.SettleFrames} frames " +
                             $"({fps} fps fixed steps) before it is saved, {s.Format} at {Screen.width}x{Screen.height}, into {s.Folder}.");
            return "rendering the keyframe stills";
        }

        /// <summary>Each frame (Update): the camera at the current keyframe.</summary>
        static partial void StillsFrame(ref ShotPose shown)
        {
            var s = still;
            if (s == null) return;
            shown = pose = s.Path.Keys[s.Index];
            if (!s.Grab && --s.Settle <= 0) s.Grab = true; // saved at the end of this frame
        }

        private static System.Collections.IEnumerator StillGrabber(StillRun s)
        {
            var end = new WaitForEndOfFrame();
            while (still == s)
            {
                yield return end;
                if (still != s) yield break;
                if (s.Grab) SaveStill(s);
            }
        }

        private static void SaveStill(StillRun s)
        {
            int i = s.Index;
            var key = s.Path.Keys[i];
            string file = Path.Combine(s.Folder, $"key{i + 1}.{(s.Format == FrameFormat.Jpg ? "jpg" : "png")}");
            try
            {
                s.Bytes += FrameFile.Write(ref s.Tex, file, s.Format, out double read, out double encode);
                s.ReadMs += read;
                s.EncodeMs += encode;
                s.Written++;
                // the pose as drawn, read back from the transform
                var drawn = ShotPose.From(camT.position, camT.rotation, cam.fieldOfView);
                string from = (s.Path.Space == ShotSpace.Hova ? $" from {targetName}'s anchor" : "") +
                              (key.Timed && !s.Path.Timed ? " (its time not used: " + s.Path.TimingNote + ")" : "");
                Plugin.L.LogInfo($"freecam keys: keyframe {i + 1} of {s.Path.Keys.Count} (keys[{i}] in the file) at {key}{from}, drawn at {drawn} -> {file} " +
                                 $"({s.Tex.width}x{s.Tex.height}, {(read + encode).ToString("0", CultureInfo.InvariantCulture)} ms)");
            }
            catch (Exception e) { Plugin.L.LogWarning($"freecam keys: keyframe {i + 1} not saved to {file}: {e.Message}"); }
            s.Grab = false;
            s.Index++;
            s.Settle = s.SettleFrames;
            if (s.Index >= s.Path.Keys.Count) FinishStills(s, null);
        }

        private static void FinishStills(StillRun s, string stopped)
        {
            if (still != s) return;
            still = null;
            stills = false;
            if (Time.captureFramerate == s.Fps) Time.captureFramerate = s.SavedCapture;
            int w = s.Tex != null ? s.Tex.width : Screen.width, h = s.Tex != null ? s.Tex.height : Screen.height;
            if (s.Tex != null) { UnityEngine.Object.Destroy(s.Tex); s.Tex = null; }
            pose = s.Before;
            var c = CultureInfo.InvariantCulture;
            double secs = s.Wall.Elapsed.TotalSeconds;
            if (stopped != null)
            {
                Plugin.L.LogInfo($"freecam keys stopped after {s.Written} of {s.Path.Keys.Count} still(s): {stopped}.");
                return;
            }
            int n = Math.Max(1, s.Written);
            Plugin.L.LogInfo(string.Format(c, "freecam keys done: {0} still(s) of {1}x{2} in {3}, {4:0.0} s ({5:0.0} s a still, {6} frames held each; reading back {7:0} ms and encoding and writing {8:0} ms a still, {9:0.0} MB); the camera back where it was.",
                                           s.Written, w, h, s.Folder, secs, secs / n, s.SettleFrames, s.ReadMs / n, s.EncodeMs / n, s.Bytes / 1048576.0));
        }

        static partial void StopStills(string why)
        {
            if (still != null) FinishStills(still, why);
        }

        /// <summary>`freecam relight <s>|off`: takes relight the menu sign s seconds in.</summary>
        private static string Relight(string arg)
        {
            if (take != null) return "a take is running.";
            var c = CultureInfo.InvariantCulture;
            if (arg == "off")
            {
                path.RelightAt = -1f;
                Touch();
                Plugin.L.LogInfo($"freecam: the path relights nothing; path {DescribePath()}.");
                return "no relight";
            }
            if (!float.TryParse(arg, NumberStyles.Float, c, out float s) || s < 0f) return "relight <seconds into the take>|off";
            path.RelightAt = MathF.Round(Math.Min(s, 600f), 3);
            Touch();
            var cues = UI.MenuSign.CueTimes();
            string when = cues.Count > 0
                ? string.Format(c, "its first crackle at {0:0.00} s, all lit at {1:0.00} s", path.RelightAt + cues[0].At, path.RelightAt + UI.MenuSign.LitBy)
                : "the sign not built here, so its timing is read at the take";
            Plugin.L.LogInfo($"freecam: the path relights the menu sign {path.RelightAt.ToString("0.###", c)} s into its takes ({when}); path {DescribePath()}.");
            return "relight set";
        }

        /// <summary>`freecam aim sign` or `aim x y z`: point the camera at the sign's centre or a point.</summary>
        private static string Aim(Vector3? at)
        {
            if (take != null) return "a take is running.";
            if (following) return "not while following: 'freecam follow off' first.";
            Vector3 target;
            string what;
            if (at.HasValue) { target = at.Value; what = "the point"; }
            else
            {
                if (!UI.MenuSign.SignBounds(out var b)) return "no menu sign here.";
                target = b.center;
                what = "the sign's centre";
            }
            Toward(pose.Position, target, out float yaw, out float pitch);
            pose.Yaw = MathF.Round(yaw, 2);
            pose.Pitch = Math.Clamp(MathF.Round(pitch, 2), -89f, 89f);
            pose.Roll = 0f;
            flyVelocity = Vector3.zero;
            Plugin.L.LogInfo(string.Format(CultureInfo.InvariantCulture, "freecam: aimed at {0} ({1:0.0}, {2:0.0}, {3:0.0}), {4:0.0} away: yaw {5:0.00} pitch {6:0.00}.",
                                           what, target.x, target.y, target.z, (target - pose.Position).magnitude, pose.Yaw, pose.Pitch));
            return Describe();
        }
    }
}
#endif
