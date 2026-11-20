using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// 1€ filter (Casiez, Roussel and Vogel, CHI 2012): low-pass with a cutoff that rises with speed.
    /// Removes jitter at rest without adding lag in motion.
    /// </summary>
    internal sealed class OneEuroFilter
    {
        private readonly float minCutoff, beta, speedCutoff;
        private float value, speed;
        private bool primed;

        /// <param name="minCutoff">Cutoff at rest, Hz: lower removes more jitter.</param>
        /// <param name="beta">Cutoff added per unit of speed: higher follows fast motion more closely.</param>
        /// <param name="speedCutoff">Cutoff of the speed estimate, Hz.</param>
        public OneEuroFilter(float minCutoff, float beta, float speedCutoff)
        {
            this.minCutoff = minCutoff;
            this.beta = beta;
            this.speedCutoff = speedCutoff;
        }

        public void Reset(float x)
        {
            value = x;
            speed = 0f;
            primed = true;
        }

        public float Filter(float x, float dt)
        {
            if (!primed || dt <= 0f)
            {
                Reset(x);
                return x;
            }
            speed += Alpha(speedCutoff, dt) * ((x - value) / dt - speed);
            float cutoff = minCutoff + beta * Mathf.Abs(speed);
            value += Alpha(cutoff, dt) * (x - value);
            return value;
        }

        private static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }
}
