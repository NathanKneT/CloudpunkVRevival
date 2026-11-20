using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>One heavy one-time setup per frame (lever, charm, coffee). Others retry next frame.</summary>
    internal static class SetupSlot
    {
        private static int taken = -1;

        /// <summary>True for the first caller this frame.</summary>
        public static bool Take()
        {
            int now = Time.frameCount;
            if (taken == now) return false;
            taken = now;
            return true;
        }
    }
}
