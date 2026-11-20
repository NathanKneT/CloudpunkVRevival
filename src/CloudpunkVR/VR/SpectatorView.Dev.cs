#if DEV
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;

namespace CloudpunkVR.VR
{
    /// <summary>Dev command "spectator": steadiness trace, head shake, pixel check of a held picture, copy readback.</summary>
    internal static partial class SpectatorView
    {
        static partial void DevFrame()
        {
            if (traceUntil > 0f) Trace();
            if (pixelFrames > 0) PixelsFrame();
        }

        static partial void DevOff()
        {
            holding = false;
            if (pixelFrames > 0) StopPixels("the view was switched off");
            if (traceUntil > 0f)
            {
                traceUntil = 0f;
                Plugin.L.LogInfo($"spectator steadiness over {Time.unscaledTime - traceStart:0.0} s: {Compare(headTrace, viewTrace)}. " +
                                 "spectator check FAILED: the trace was cut short by the view switching off.");
            }
        }

        // ---- Probe: the eye's copy read back.

        private static string probeNote = "not read back yet";

        /// <summary>Reads the eye copy back at 8x8. Stalls on the GPU, so only on request.</summary>
        private static void Probe()
        {
            if (copy == null) { probeNote = "no copy to read back"; return; }
            try
            {
                var rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(copy, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                read.Apply(false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                float sum = 0f, max = 0f;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        var c = read.GetPixel(x, y);
                        float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                        sum += l;
                        if (l > max) max = l;
                    }
                UnityEngine.Object.Destroy(read);
                probeNote = $"the eye's copy read back: mean brightness {sum / 64f:0.000}, brightest {max:0.000} ({(max > 0.02f ? "a picture" : "black")}, {copies} copies so far)";
            }
            catch (Exception e) { probeNote = "the eye's copy not read back: " + e.Message; }
            Plugin.L.LogInfo("Spectator view: " + probeNote + ".");
        }

        // ---- Trace: head vs picture.

        private static float traceUntil, traceStart, nextTraceLine;

        private static void Trace()
        {
            float now = Time.unscaledTime;
            if (now >= nextTraceLine)
            {
                nextTraceLine = now + 0.25f;
                Plugin.L.LogInfo($"spectator trace {now - traceStart:0.00} s: {Compare(headWindow, viewWindow)}; lag {lagYaw:0.0} right {lagPitch:0.0} up " +
                                 $"(room {roomYaw:0.0} and {roomPitch:0.0} that way), fade {Math.Max(lastFade, 0f):0.00}.");
                headWindow = default;
                viewWindow = default;
            }
            if (now < traceUntil) return;
            traceUntil = 0f;
            int edges = traceEdges, held = traceHeld;
            string verdict;
            if (headTrace.Count < 10) verdict = "spectator check FAILED: no motion measured (black, or the view off)";
            else if (headTrace.Shake < 0.01f) verdict = "spectator check: nothing to steady (the head held still)";
            else if (viewTrace.Shake * 3f > headTrace.Shake || edges > 0 || viewTrace.RollMax > 0.5f)
                verdict = $"spectator check FAILED: {(viewTrace.Shake * 3f > headTrace.Shake ? "not three times steadier than the head" : edges > 0 ? "edges shown" : "the picture leaned")}";
            else verdict = "spectator check: steadier, level, no edge";
            Plugin.L.LogInfo($"spectator steadiness over {now - traceStart:0.0} s: {Compare(headTrace, viewTrace)}; held at the eye's edge in {held} frame(s), edges shown in {edges}. {verdict}.");
        }

        // ---- Shake: synthetic hand-held head.

        private static float shakeAmp, shakeSweep, shakeUntil, shakeStart;
        private static bool shakeAtEye;
        private static Quaternion lastShake = Quaternion.identity;

        private static void StopShake()
        {
            shakeAmp = 0f;
            VRSession.TestShake = null;
            if (shakeAtEye) VRSession.TestOffset = null;
            shakeAtEye = false;
            lastShake = Quaternion.identity;
        }

        static partial void UpdateShake()
        {
            if (shakeAmp <= 0f) return;
            float now = Time.unscaledTime;
            if (shakeUntil > 0f && now >= shakeUntil)
            {
                StopShake();
                Plugin.L.LogInfo("spectator: shake over.");
                return;
            }
            float t = now - shakeStart, a = shakeAmp, w = 2f * MathF.PI;
            // 5-9 Hz tremor on a slower sway, plus roll
            float yawDeg = a * (0.35f * MathF.Sin(w * 6.3f * t) + 0.2f * MathF.Sin(w * 9.1f * t + 1f) + MathF.Sin(w * 0.9f * t + 0.4f)) + shakeSweep * MathF.Sin(w * 0.12f * t);
            float pitchDeg = a * (0.35f * MathF.Sin(w * 5.1f * t + 2f) + 0.8f * MathF.Sin(w * 1.3f * t + 1.3f));
            float rollDeg = a * (2f * MathF.Sin(w * 0.6f * t + 0.7f) + 0.3f * MathF.Sin(w * 4.7f * t));
            var shake = Quaternion.Euler(-pitchDeg, yawDeg, rollDeg);
            if (shakeAtEye)
            {
                // turn about the copied eye: offset the head so the eye stays put
                var before = VRSession.HeadRotation * Quaternion.Inverse(lastShake);
                var o = new Vector3 { x = eyeOffset.X, y = eyeOffset.Y, z = eyeOffset.Z };
                VRSession.TestOffset = before * (o - shake * o);
            }
            VRSession.TestShake = shake;
            lastShake = shake;
        }

        /// <summary>SteamVR's null driver only: shaking a real headset would shake the wearer's view.</summary>
        private static bool NullHeadset()
        {
            try { return (XRDevice.model ?? "").IndexOf("null", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch (Exception) { return false; }
        }

        // ---- Hold and pixels: picture held still, motion measured in window pixels.

        // held: direction fixed in the anchor's frame (Steer reads it)
        private static bool holding;
        private static float holdYaw, holdPitch;

        // Pixel check: a window square is read back each frame and aligned with the first (SAD, +-4 px, sub-pixel).
        // Head still gives the floor (wipers, HOVA sway). Shaking about the eye must stay at that floor.
        private const int PixelSquare = 256, PixelReach = 4;

        // min detail to align a square (mean neighbour difference, of 255). Flat is ~0.3, the dark dashboard ~1.
        private const float MinDetail = 0.5f;
        private static RenderTexture pixelTarget;
        private static Texture2D pixelRead;
        private static float[] pixelFirst, pixelNow;
        private static int pixelFrames, pixelSeen, pixelSkip, pixelX, pixelY, pixelGaveWay;
        private static double pixelSq;
        private static float pixelMax, pixelDetail, anchorMoved, anchorTurned;
        private static Vector3 anchorAtStart;
        private static float anchorYawAtStart;
        private static bool pixelShaking, haveFloor;
        private static float floorRms, floorMax;

        private static string StartPixels(int frames, int x = -1, int y = -1)
        {
            if (!On || !eyeKnown || cam == null || winW < PixelSquare * 2 || winH < PixelSquare * 2) return "spectator check FAILED: the view is off, not placed yet, or the window too small";
            if (pixelFrames > 0) return "a pixel test is already running";
            pixelTarget = new RenderTexture(winW, winH, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "CloudpunkVR_SpectatorPixels" };
            pixelTarget.Create();
            pixelRead = new Texture2D(PixelSquare, PixelSquare, TextureFormat.RGBA32, false);
            pixelFirst = new float[PixelSquare * PixelSquare];
            pixelNow = new float[PixelSquare * PixelSquare];
            var r = cam.pixelRect;
            if (x >= 0 && y >= 0)
            {
                // lower left corner in window pixels
                pixelX = Math.Clamp(x, 0, winW - PixelSquare);
                pixelY = Math.Clamp(y, 0, winH - PixelSquare);
            }
            else
            {
                // around the gaze: middle, raised by the tilt, shifted back by the bias (Landscape only)
                float perDegree = PixelsPerDegree;
                bool landscape = VRConfig.SpectatorFormat.Value == SpectatorFormat.Landscape;
                float cx = r.x + r.width * 0.5f - (landscape ? restBias * perDegree : 0f);
                float cy = r.y + r.height * 0.5f + (landscape ? VRConfig.SpectatorTilt.Value * perDegree : 0f);
                pixelX = (int)Math.Clamp(cx - PixelSquare * 0.5f, r.x, r.xMax - PixelSquare);
                pixelY = (int)Math.Clamp(cy - PixelSquare * 0.5f, r.y, r.yMax - PixelSquare);
            }
            pixelFrames = Math.Max(3, frames);
            pixelSeen = pixelGaveWay = 0;
            pixelSkip = 1; // texture first drawn this frame, after this command
            pixelSq = 0.0;
            pixelMax = anchorMoved = anchorTurned = 0f;
            anchorAtStart = CameraDriver.AnchorPosition;
            anchorYawAtStart = CameraDriver.Yaw(CameraDriver.AnchorRotation);
            pixelShaking = shakeAmp > 0f;
            cam.targetTexture = pixelTarget;
            return $"measuring the picture in pixels for {pixelFrames} frames, a {PixelSquare}-pixel square at ({pixelX}, {pixelY}) of {winW}x{winH}, " +
                   $"{(holding ? "held still" : "NOT held (spectator hold on first)")}, the head {(pixelShaking ? (shakeAtEye ? "shaking about the copied eye" : "shaking about its centre (the eye moves: parallax)") : "still")}";
        }

        private static void PixelsFrame()
        {
            if (cam.targetTexture == null) { StopPixels("its texture was lost"); return; }
            if (pixelSkip > 0) { pixelSkip--; return; }
            var prev = RenderTexture.active;
            RenderTexture.active = pixelTarget;
            pixelRead.ReadPixels(new Rect(pixelX, pixelY, PixelSquare, PixelSquare), 0, 0);
            RenderTexture.active = prev;
            var px = pixelRead.GetPixels32();
            var into = pixelSeen == 0 ? pixelFirst : pixelNow;
            for (int i = 0; i < into.Length; i++)
            {
                var c = px[i];
                into[i] = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            }
            anchorMoved = Math.Max(anchorMoved, (CameraDriver.AnchorPosition - anchorAtStart).magnitude);
            anchorTurned = Math.Max(anchorTurned, MathF.Abs(DeltaAngle(anchorYawAtStart, CameraDriver.Yaw(CameraDriver.AnchorRotation))));
            if (pixelSeen == 0) pixelDetail = Detail(pixelFirst);
            if (pixelSeen > 0)
            {
                Shift(pixelFirst, pixelNow, out float dx, out float dy);
                float d = MathF.Sqrt(dx * dx + dy * dy);
                pixelSq += d * d;
                if (d > pixelMax) pixelMax = d;
            }
            pixelSeen++;
            if (--pixelFrames == 0) StopPixels(null);
        }

        private static void StopPixels(string why)
        {
            int compared = pixelSeen - 1;
            pixelFrames = 0;
            if (cam != null) cam.targetTexture = null;
            if (pixelTarget != null) { pixelTarget.Release(); UnityEngine.Object.Destroy(pixelTarget); pixelTarget = null; }
            if (pixelRead != null) { UnityEngine.Object.Destroy(pixelRead); pixelRead = null; }
            if (why != null) { Plugin.L.LogInfo("spectator pixels: stopped, " + why + "."); return; }
            float rms = compared > 0 ? (float)Math.Sqrt(pixelSq / compared) : 0f;
            // bar: turning about the eye only turns its picture, noise stays under 0.1 px, so +0.5 px is a drawing error
            string verdict;
            if (compared < 2) verdict = "spectator check FAILED: too few frames compared";
            else if (!holding) verdict = "spectator check FAILED: the picture was not held (spectator hold on first)";
            else if (pixelDetail < MinDetail) verdict = $"spectator check FAILED: the square holds too little detail to line up ({pixelDetail:0.0}, under {MinDetail:0.0})";
            else if (!pixelShaking)
            {
                haveFloor = true;
                floorRms = rms;
                floorMax = pixelMax;
                verdict = rms < 0.5f ? $"spectator check: floor {rms:0.00} px with the head still" : $"spectator check: floor {rms:0.00} px with the head still (things move in the square by themselves)";
            }
            else
            {
                float baseRms = haveFloor ? floorRms : 0f, baseMax = haveFloor ? floorMax : 0f;
                bool ok = pixelGaveWay == 0 && rms <= baseRms + 0.5f && pixelMax <= baseMax + 1.5f;
                verdict = (ok ? "spectator check: the drawing holds the picture" : "spectator check FAILED: the drawing moved the held picture") +
                          $" ({rms:0.00} px RMS shaking against {(haveFloor ? $"{baseRms:0.00} px with the head still" : "no still-head floor measured")}, bar +0.5 px" +
                          $"{(pixelGaveWay > 0 ? $"; the eye's edge moved the picture in {pixelGaveWay} frame(s)" : "")}{(shakeAtEye ? "" : "; the head turned about its centre, so the eye moved too")})";
            }
            Plugin.L.LogInfo($"spectator pixels ({(pixelShaking ? "head shaking" : "head still")}) over {compared} frames against the first, the picture {(holding ? "held still" : "not held")}: " +
                             $"moved {rms:0.00} px RMS, {pixelMax:0.00} px at most ({PixelSquare}-pixel square at ({pixelX}, {pixelY}), detail {pixelDetail:0.0}; " +
                             $"meanwhile the HOVA or Rania moved {anchorMoved * 100f:0.0} cm and turned {anchorTurned:0.00} deg). {verdict}.");
        }

        /// <summary>Mean difference between neighbouring pixels (0-255).</summary>
        private static float Detail(float[] a)
        {
            int n = PixelSquare;
            double sum = 0.0;
            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                    sum += Math.Abs(a[y * n + x + 1] - a[y * n + x]) + Math.Abs(a[(y + 1) * n + x] - a[y * n + x]);
            return (float)(sum / ((n - 1) * (n - 1) * 2));
        }

        /// <summary>Sub-pixel shift of b vs a with the least difference.</summary>
        private static void Shift(float[] a, float[] b, out float dx, out float dy)
        {
            int n = PixelSquare, m = PixelReach, size = 2 * m + 1;
            var sad = new double[size * size];
            int bestX = 0, bestY = 0;
            double best = double.MaxValue;
            for (int sy = -m; sy <= m; sy++)
                for (int sx = -m; sx <= m; sx++)
                {
                    double sum = 0.0;
                    for (int y = m; y < n - m; y++)
                    {
                        int ra = y * n, rb = (y + sy) * n + sx;
                        for (int x = m; x < n - m; x++) sum += Math.Abs(a[ra + x] - b[rb + x]);
                    }
                    sad[(sy + m) * size + sx + m] = sum;
                    if (sum < best) { best = sum; bestX = sx; bestY = sy; }
                }
            dx = bestX + Parabola(sad, size, bestX + m, bestY + m, true);
            dy = bestY + Parabola(sad, size, bestX + m, bestY + m, false);
        }

        private static float Parabola(double[] sad, int size, int x, int y, bool alongX)
        {
            int lo = alongX ? x - 1 : y - 1, hi = alongX ? x + 1 : y + 1;
            if (lo < 0 || hi >= size) return 0f;
            double l = alongX ? sad[y * size + lo] : sad[lo * size + x], c = sad[y * size + x], r = alongX ? sad[y * size + hi] : sad[hi * size + x];
            double den = l - 2.0 * c + r;
            return den > 1e-9 ? (float)(0.5 * (l - r) / den) : 0f;
        }

        // ---- The command.

        /// <summary>
        /// spectator [on|off|key | format landscape|portrait|turned | fov deg | smooth 0-1 | tilt deg | trace s |
        /// shake deg [eye] [sweep deg] [s] | shake off | hold on|off | pixels [frames] [x y] | probe | flip | mirror on|off]
        /// </summary>
        public static string Command(string[] a)
        {
            string what = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            var inv = CultureInfo.InvariantCulture;
            float Arg(int i, float fallback) => a.Length > i ? float.Parse(a[i], inv) : fallback;
            switch (what)
            {
                case "on": Switch(true, "developer command"); break;
                case "off": Switch(false, "developer command"); break;
                case "key": Switch(!On, "key"); break; // like the key: settings file read again
                case "format":
                    var s = a.Length > 2 ? a[2].ToLowerInvariant() : "landscape";
                    VRConfig.SpectatorFormat.Value = s == "turned" || s == "portraitturned" ? SpectatorFormat.PortraitTurned : s == "portrait" ? SpectatorFormat.Portrait : SpectatorFormat.Landscape;
                    break;
                case "fov": VRConfig.SpectatorFov.Value = Arg(2, 75f); break;
                case "smooth": VRConfig.SpectatorSmoothing.Value = Arg(2, 0.5f); break;
                case "tilt": VRConfig.SpectatorTilt.Value = Arg(2, 8f); break;
                case "trace":
                    if (!On) return "spectator check FAILED: trace asked with the view off";
                    traceStart = Time.unscaledTime;
                    traceUntil = traceStart + Arg(2, 6f);
                    nextTraceLine = traceStart + 0.25f;
                    headWindow = viewWindow = headTrace = viewTrace = default;
                    traceHeld = traceEdges = 0;
                    return $"tracing the head and the picture for {traceUntil - traceStart:0.#} s";
                case "shake":
                {
                    if (a.Length > 2 && a[2] == "off") { StopShake(); return "the head's shake off"; }
                    if (!NullHeadset()) return $"refused: the headset is '{XRDevice.model}', not SteamVR's null one, and its wearer's view would shake";
                    // shake <deg> [eye] [sweep <deg>] [seconds]
                    StopShake();
                    shakeAmp = Arg(2, 2f);
                    int i = 3;
                    if (a.Length > i && a[i] == "eye") { shakeAtEye = true; i++; }
                    shakeSweep = 0f;
                    if (a.Length > i + 1 && a[i] == "sweep") { shakeSweep = Arg(i + 1, 0f); i += 2; }
                    float seconds = Arg(i, 0f);
                    shakeStart = Time.unscaledTime;
                    shakeUntil = seconds > 0f ? shakeStart + seconds : 0f;
                    return $"the head shakes on top of its tracking: a {0.55f * shakeAmp:0.0}-degree tremor at 5 to 9 Hz on a {shakeAmp:0.0}-degree sway, rolling up to {2.3f * shakeAmp:0.0} degrees" +
                           $"{(shakeAtEye ? $", turning about the copied eye ({eyeOffset.X * 1000f:0} mm right, {eyeOffset.Y * 1000f:0} up, {eyeOffset.Z * 1000f:0} ahead of the head's centre)" : ", about the head's centre")}" +
                           $"{(shakeSweep > 0f ? $", looking {shakeSweep:0} degrees left and right every 8 s" : "")}{(seconds > 0f ? $", for {seconds:0} s" : "")}";
                }
                case "hold":
                    holding = On && !(a.Length > 2 && a[2] == "off");
                    holdYaw = yaw;
                    holdPitch = pitch;
                    return holding ? $"the picture held still at {holdYaw:0.0} right, {holdPitch:0.0} up in the anchor's frame" : "the picture follows the head again";
                case "pixels": // x y: square's lower left, window px
                    return a.Length > 4 ? StartPixels((int)Arg(2, 20f), (int)Arg(3, 0f), (int)Arg(4, 0f)) : StartPixels((int)Arg(2, 20f));
                case "probe":
                    if (!On) return "off: no copy to read back";
                    Probe();
                    return probeNote;
                case "flip":
                    flip = !flip;
                    if (eyeKnown) Quad();
                    return flip ? "the eye's image now read upside down (test)" : "the eye's image read the right way up";
                case "mirror":
                    keepDeviceView = a.Length > 2 && a[2] == "on";
                    if (On) SetDeviceView(keepDeviceView);
                    return keepDeviceView ? "Unity's mirror kept on under the view (test)" : "Unity's mirror off while the view shows";
            }
            return Describe();
        }

        public static string Describe()
        {
            string keyNote = key.Describe();
            if (!On) return $"off ({keyNote}){(shakeAmp > 0f ? "; the head shaking (test)" : "")}";
            if (!eyeKnown) return "on, waiting for the first eye drawn";
            return $"on ({keyNote}); {Shape()}; {Pixels()}; eye copy {copyW}x{copyH}, {copies} copies so far; lag now {lagYaw:0.0} right {lagPitch:0.0} up; " +
                   $"camera {(cam != null && cam.enabled ? "drawing" : "off")} at {(cam != null ? cam.pixelRect.ToString() : "-")}, bars {(clearCam != null && clearCam.enabled ? "cleared" : "none")}; " +
                   $"Unity's mirror {(keepDeviceView ? "on (test)" : "off")}; fade {Math.Max(lastFade, 0f):0.00}{(flip ? "; image read upside down (test)" : "")}" +
                   $"{(holding ? "; the picture held still (test)" : "")}{(shakeAmp > 0f ? "; the head shaking (test)" : "")}; {probeNote}; " + PeriodSummary();
        }
    }
}
#endif
