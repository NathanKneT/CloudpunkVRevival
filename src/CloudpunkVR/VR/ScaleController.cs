using System;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// AutoRenderScale logic, Unity-free. Each resolution change is a hitch, so: drop at once in play when over budget,
    /// step back up only after calm play and only while the view is hidden. Floor 80%, or 70% at 90 Hz and below.
    /// </summary>
    internal sealed class ScaleController
    {
        public enum Verdict { None, Down, FloorReached }

        /// <summary>Per-side scale steps, each about a fifth fewer pixels.</summary>
        public static readonly float[] Levels = { 1f, 0.9f, 0.8f, 0.7f };

        // GPU time vs per-side scale, measured around 1.5 (part of the frame doesn't scale with pixels)
        public const float CostExponent = 1.5f;

        public const float MinSecondsBetweenUps = 120f;
        public const float FailedUpWindow = 150f; // a drop this soon after a step up doubles UpWait
        private const float FirstUpWait = 15f, MaxUpWait = 240f, ForgetFailuresAfter = 600f;

        public int Level { get; private set; }
        public bool UpPending { get; private set; }
        /// <summary>Seconds of play with headroom before a step up is pending.</summary>
        public float UpWait { get; private set; } = FirstUpWait;
        public string Why { get; private set; } = "";

        private int overRun, roomRun;
        private float lastUpAt = -1e6f, lastFailAt = -1e6f;
        private bool floorReported;

        public static int Floor(float hz) => hz > 100f ? 2 : 3;

        public static float Predict(float gpuMs, float fromScale, float toScale) =>
            gpuMs * (float)Math.Pow(toScale / fromScale, CostExponent);

        public void Reset()
        {
            Level = 0;
            UpPending = false;
            overRun = roomRun = 0;
        }

        /// <summary>A second outside play (menu, load): overload must be seen again in play.</summary>
        public void Pause() => overRun = 0;

        /// <summary>Called once per second of play.</summary>
        public Verdict Judge(float now, float gpuMs, float hz)
        {
            int floor = Floor(hz);
            if (Level > floor) Level = floor;
            if (now - lastFailAt > ForgetFailuresAfter) UpWait = FirstUpWait;
            float budget = 1000f / hz;

            overRun = gpuMs > budget * 0.92f ? overRun + 1 : 0;
            if (overRun >= 2 || (overRun >= 1 && gpuMs > budget * 1.3f))
            {
                overRun = 0;
                roomRun = 0;
                UpPending = false;
                if (Level < floor)
                {
                    int from = Level;
                    Level = Fitting(gpuMs, from, floor, budget * 0.85f);
                    if (now - lastUpAt < FailedUpWindow)
                    {
                        UpWait = Math.Min(UpWait * 2f, MaxUpWait);
                        lastFailAt = now;
                    }
                    Why = $"the graphics card took {gpuMs:0.0} ms per frame at {Levels[from] * 100f:0}%, {budget:0.0} ms per refresh";
                    return Verdict.Down;
                }
                if (floorReported) return Verdict.None;
                floorReported = true;
                Why = $"{gpuMs:0.0} ms per frame at {Levels[Level] * 100f:0}% per side, {budget:0.0} ms per refresh at {hz:0} Hz";
                return Verdict.FloorReached;
            }

            bool room = Level > 0 && Predict(gpuMs, Levels[Level], Levels[Level - 1]) < budget * 0.78f;
            roomRun = room ? roomRun + 1 : 0;
            if (!room) UpPending = false;
            else if (roomRun >= UpWait && now - lastUpAt >= MinSecondsBetweenUps)
            {
                UpPending = true;
                Why = $"{gpuMs:0.0} ms per frame at {Levels[Level] * 100f:0}% left room for {budget:0.0} ms";
            }
            return Verdict.None;
        }

        /// <summary>Call while the view is hidden (black, menu) so the hitch isn't seen.</summary>
        public bool TakeUp(float now)
        {
            if (!UpPending || Level == 0 || now - lastUpAt < MinSecondsBetweenUps) return false;
            Level--;
            lastUpAt = now;
            UpPending = false;
            roomRun = 0;
            return true;
        }

        /// <summary>Sharpest step below <paramref name="from"/> predicted to fit, else the floor.</summary>
        private static int Fitting(float gpuMs, int from, int floor, float limitMs)
        {
            for (int i = from + 1; i <= floor; i++)
                if (Predict(gpuMs, Levels[from], Levels[i]) <= limitMs) return i;
            return floor;
        }
    }
}
