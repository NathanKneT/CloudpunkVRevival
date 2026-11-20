using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Bumps a version when scenes load or unload, so scene searches run then, not on a timer (they stall frames).
    /// Checked twice a second.
    /// </summary>
    internal static class SceneWatch
    {
        private static int version, lastFrame = -1;
        private static int lastSignature;
        private static float nextCheck;

        public static int Version
        {
            get
            {
                int f = Time.frameCount;
                if (f != lastFrame && Time.unscaledTime >= nextCheck)
                {
                    lastFrame = f;
                    nextCheck = Time.unscaledTime + 0.5f;
                    int sig = 17;
                    int n = SceneManager.sceneCount;
                    for (int i = 0; i < n; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        sig = sig * 31 + s.handle;
                        sig = sig * 31 + (s.isLoaded ? 1 : 0);
                    }
                    if (sig != lastSignature)
                    {
                        lastSignature = sig;
                        version++;
                    }
                }
                return version;
            }
        }

        /// <summary>True once per caller after a change. <paramref name="seen"/> is the caller's last version.</summary>
        public static bool Changed(ref int seen)
        {
            int v = Version;
            if (v == seen) return false;
            seen = v;
            return true;
        }
    }
}
