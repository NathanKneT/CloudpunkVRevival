using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// SteamVR's per-app frame counters, read once a second for the resolution controller.
    /// Logged every 30 s and at quit, with long frames and the mod's own cost.
    /// </summary>
    internal static class FrameTiming
    {
        /// <summary>SteamVR counters at one moment.</summary>
        public struct Snapshot
        {
            public float At;
            public int Frame;
            public uint Submits, Presents, Reprojected, Dropped, TimedOut;
            public double GpuMs, CpuMs;
            public bool Valid;
        }

        public struct Span
        {
            public float Seconds;
            /// <summary>Frames Unity drew, and frames SteamVR counted as submitted.</summary>
            public int Frames, Submits;
            public int Presents, Reprojected, Dropped, TimedOut;
            /// <summary>Per frame.</summary>
            public float GpuMs, CpuMs;
            /// <summary>Frames SteamVR timed: its count, or Unity's when SteamVR counts each eye.</summary>
            public int Counted => Submits > Frames * 1.5f ? Frames : Submits;
            public bool Valid => Seconds > 0.2f && Frames > 0 && Submits > 0;
            public float Fps => Seconds > 0f ? Counted / Seconds : 0f;
            public float Share(int refreshes) => Presents > 0 ? (float)refreshes / Presents : 0f;
        }

        public static float Hz => XRDevice.refreshRate > 1f ? XRDevice.refreshRate : 90f;
        public static float BudgetMs => 1000f / Hz;

        /// <summary>SteamVR dashboard open (checked once a second). Frames then don't count as play.</summary>
        public static bool DashboardVisible { get; private set; }
        /// <summary>When it last opened or closed (realtime seconds).</summary>
        public static float DashboardChangedAt { get; private set; } = -100f;

        /// <summary>The last full second, and whether it was all play.</summary>
        public static Span LastSecond { get; private set; }
        public static bool LastSecondWasPlay { get; private set; }

        private static readonly uint StatsSize = (uint)Marshal.SizeOf<Compositor_CumulativeStats>();
        private static readonly int Pid = Environment.ProcessId;
        private static bool statsWork = true, foreignNoted, ratioNoted;

        private static Snapshot session, secondStart, logStart;
        private static float nextSecond, nextLog;
        private static bool started, secondAllPlay = true, wasDashboard;

        // since the last log line
        private static int frames, missedFrames, longFrames, playSeconds, dashboardSeconds, otherSeconds;
        private static float worstFrameMs, modMsSum, frameModMs, worstModFrameMs;
        private static int sessionLongFrames;

        private sealed class Cost { public double SumMs; public float WorstMs; }
        private static readonly Dictionary<string, Cost> costs = new Dictionary<string, Cost>();

        /// <summary>A game view, nothing loading, no menu, map or dashboard.</summary>
        public static bool Playing
        {
            get
            {
                var m = CameraDriver.Mode;
                if (!(m == ViewMode.OnFoot || m == ViewMode.Cockpit || m == ViewMode.Chase) || TransitionGuard.Active || DashboardVisible) return false;
                try
                {
                    var g = Global.Instance;
                    return g == null || !(g.IsInMenu() || g.HasBigWindowOpen() || Global.FullMapOpen);
                }
                catch (Exception) { return true; }
            }
        }

        public static Snapshot Read()
        {
            var s = new Snapshot { At = Time.realtimeSinceStartup, Frame = Time.frameCount };
            if (!statsWork) return s;
            try
            {
                var c = OpenVR.Compositor;
                if (c == null) return s;
                var st = new Compositor_CumulativeStats();
                c.GetCumulativeStats(ref st, StatsSize);
                // counters are the shown app's: just after launch maybe the previous one's, pid 0 is nobody's
                if (st.m_nPid != (uint)Pid)
                {
                    if (!foreignNoted)
                    {
                        foreignNoted = true;
                        Plugin.L.LogInfo(st.m_nPid == 0 ? "SteamVR has no frame counters for the game yet." : $"SteamVR's frame counters are another process's ({st.m_nPid}) for now.");
                    }
                    return s;
                }
                s.Submits = st.m_nNumFrameSubmits;
                s.Presents = st.m_nNumFramePresents;
                s.Reprojected = st.m_nNumReprojectedFrames;
                s.Dropped = st.m_nNumDroppedFrames;
                s.TimedOut = st.m_nNumFramePresentsTimedOut;
                s.GpuMs = st.m_flSumApplicationGPUTimeMS;
                s.CpuMs = st.m_flSumApplicationCPUTimeMS;
                s.Valid = true;
            }
            catch (Exception e)
            {
                statsWork = false;
                Plugin.L.LogWarning("SteamVR's frame counters are unavailable: " + e.Message);
            }
            return s;
        }

        public static Span Between(Snapshot a, Snapshot b)
        {
            var d = new Span { Seconds = b.At - a.At, Frames = b.Frame - a.Frame };
            if (!a.Valid || !b.Valid || b.Submits < a.Submits || b.Presents < a.Presents) return d;
            d.Submits = (int)(b.Submits - a.Submits);
            d.Presents = (int)(b.Presents - a.Presents);
            d.Reprojected = (int)(b.Reprojected - a.Reprojected);
            d.Dropped = (int)(b.Dropped - a.Dropped);
            d.TimedOut = (int)(b.TimedOut - a.TimedOut);
            // running sums over the frames SteamVR timed
            int n = d.Counted;
            if (n > 0)
            {
                d.GpuMs = (float)((b.GpuMs - a.GpuMs) / n);
                d.CpuMs = (float)((b.CpuMs - a.CpuMs) / n);
            }
            return d;
        }

        public static bool QueryDashboard()
        {
            try { return OpenVR.Overlay != null && OpenVR.Overlay.IsDashboardVisible(); }
            catch (Exception) { return false; }
        }

        /// <summary>Every frame. True once a second, with that second in <see cref="LastSecond"/>.</summary>
        public static bool Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (!started)
            {
                started = true;
                session = secondStart = logStart = Read();
                nextSecond = now + 1f;
                nextLog = now + 30f;
            }

            bool play = Playing;
            secondAllPlay &= play;
            frames++;
            if (frameModMs > worstModFrameMs) worstModFrameMs = frameModMs;
            frameModMs = 0f;
            // over 1.5 refreshes the headset reused a frame, over 3 it's a visible stutter
            float ms = Time.unscaledDeltaTime * 1000f;
            if (play && ms > 1.5f * BudgetMs) missedFrames++;
            if (play && ms > 3f * BudgetMs)
            {
                longFrames++;
                sessionLongFrames++;
                if (ms > worstFrameMs) worstFrameMs = ms;
            }

            if (now < nextSecond) return false;
            nextSecond = now + 1f;
            var snap = Read();
            // counters became ours after launch: start from here
            if (!session.Valid) session = snap;
            if (!logStart.Valid) logStart = snap;
            LastSecond = Between(secondStart, snap);
            LastSecondWasPlay = secondAllPlay;
            secondStart = snap;
            if (DashboardVisible) dashboardSeconds++;
            else if (secondAllPlay) playSeconds++;
            else otherSeconds++;
            secondAllPlay = true;
            DashboardVisible = QueryDashboard();
            if (DashboardVisible != wasDashboard)
            {
                wasDashboard = DashboardVisible;
                DashboardChangedAt = now;
                Plugin.L.LogInfo(DashboardVisible
                    ? "SteamVR's dashboard is open over the game: its frames are not judged until it closes."
                    : "SteamVR's dashboard closed.");
            }

            if (now >= nextLog)
            {
                nextLog = now + 30f;
                var span = Between(logStart, snap);
                NoteSubmitRatio(span);
                Plugin.L.LogInfo("Performance: " + Describe(span, true));
                logStart = snap;
                frames = missedFrames = longFrames = playSeconds = dashboardSeconds = otherSeconds = 0;
                worstFrameMs = modMsSum = worstModFrameMs = 0f;
                costs.Clear();
            }
            return true;
        }

        /// <summary>Once: SteamVR's submit count against Unity's frames.</summary>
        private static void NoteSubmitRatio(Span s)
        {
            if (ratioNoted || !s.Valid) return;
            ratioNoted = true;
            Plugin.L.LogInfo($"SteamVR counted {s.Submits} frames handed over while Unity drew {s.Frames} ({(float)s.Submits / s.Frames:0.00} per frame); " +
                             $"times per frame are divided by {(s.Counted == s.Submits ? "SteamVR's count" : "Unity's count")}.");
        }

        /// <summary>Times a mod system since the previous lap, returns the new timestamp.</summary>
        public static long Lap(long startTimestamp, string system)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float ms = (float)((now - startTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            modMsSum += ms;
            frameModMs += ms;
            if (!costs.TryGetValue(system, out var c)) costs[system] = c = new Cost();
            c.SumMs += ms;
            if (ms > c.WorstMs) c.WorstMs = ms;
            return now;
        }

        /// <summary>Slowest mod systems since the last log line.</summary>
        private static string Slowest()
        {
            var list = new List<KeyValuePair<string, Cost>>(costs);
            list.Sort((a, b) => b.Value.WorstMs.CompareTo(a.Value.WorstMs));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{list[i].Key} {list[i].Value.WorstMs:0.0} ms");
                if (frames > 0) sb.Append($" ({list[i].Value.SumMs / frames:0.00} per frame)");
            }
            return sb.ToString();
        }

        /// <summary>One readable line: fps, frame times, reprojection, eye resolution, mod cost.</summary>
        public static string Describe(Span s, bool withPeriodDetails)
        {
            var sb = new System.Text.StringBuilder();
            float budget = BudgetMs;
            if (s.Valid)
            {
                sb.Append($"{s.Fps:0} fps at {Hz:0} Hz, GPU {s.GpuMs:0.0} ms, CPU {s.CpuMs:0.0} ms per frame (one refresh: {budget:0.0} ms), limit {Limit(s)}; ");
                sb.Append($"refreshes reprojected {s.Share(s.Reprojected):P0}, dropped {s.Share(s.Dropped):P0}, timed out {s.Share(s.TimedOut):P0}");
            }
            else sb.Append("no frame counters from SteamVR");
            if (withPeriodDetails)
            {
                sb.Append($"; in play {missedFrames} frame(s) over 1.5 refreshes, {longFrames} over 3");
                if (longFrames > 0) sb.Append($" (worst {worstFrameMs:0} ms)");
                sb.Append($"; {playSeconds} s play");
                if (dashboardSeconds > 0) sb.Append($", {dashboardSeconds} s dashboard");
                if (otherSeconds > 0) sb.Append($", {otherSeconds} s menu or loading");
                if (frames > 0) sb.Append($"; mod {modMsSum / frames:0.00} ms per frame, worst frame {worstModFrameMs:0.0} ms, slowest: {Slowest()}");
            }
            string mirrors = CockpitMirrors.Short();
            sb.Append($"; {RenderResolution.Describe()}; view {CameraDriver.Mode}{(mirrors != null ? "; " + mirrors : "")}.");
            return sb.ToString();
        }

        /// <summary>What holds the frame rate back. CPU time includes waiting on a late GPU, so the GPU is checked first.</summary>
        public static string Limit(Span s)
        {
            if (!s.Valid) return "unknown";
            float frameMs = 1000f / Mathf.Max(1f, s.Fps);
            if (s.Fps >= Hz * 0.95f) return "none (every refresh)";
            if (s.GpuMs >= frameMs * 0.8f || s.GpuMs > BudgetMs) return "graphics card";
            if (s.CpuMs >= frameMs * 0.8f || s.CpuMs > BudgetMs) return "processor";
            return "pacing (SteamVR holds the game below the refresh rate)";
        }

        /// <summary>Short form for the dev status line.</summary>
        public static string Short()
        {
            var s = LastSecond;
            if (!s.Valid) return "fps=? hz=" + Hz.ToString("0");
            return $"fps={s.Fps:0} gpu={s.GpuMs:0.0}ms cpu={s.CpuMs:0.0}ms reprojected={s.Share(s.Reprojected):P0} timedout={s.Share(s.TimedOut):P0} hz={Hz:0}";
        }

        /// <summary>At quit: the whole session in one line.</summary>
        public static void LogSession()
        {
            if (!session.Valid) return;
            var s = Between(session, Read());
            if (!s.Valid) return;
            Plugin.L.LogInfo($"Performance over the session ({s.Seconds / 60f:0} min): {s.Fps:0} fps at {Hz:0} Hz on average, GPU {s.GpuMs:0.0} ms and CPU {s.CpuMs:0.0} ms per frame, " +
                             $"refreshes reprojected {s.Share(s.Reprojected):P0}, timed out {s.Share(s.TimedOut):P0}; {sessionLongFrames} long frame(s) in play; " +
                             $"{RenderResolution.Changes} eye resolution change(s); {RenderResolution.Describe()}.");
        }
    }
}
