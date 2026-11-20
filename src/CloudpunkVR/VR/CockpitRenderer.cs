using RedAlliance.Util;
using UnityEngine;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// The cockpit is a separate set with its own camera. The game renders it by hand after the main camera, which
    /// only reaches one eye in VR. Here it's a normal stereo camera drawn right after the main one instead.
    /// </summary>
    internal static class CockpitRenderer
    {
        private static bool loggedOnce;

        public static void Update()
        {
            var g = Global.Instance;
            if (g == null || g.mainCamera == null) return;

            var manual = g.mainCamera.GetComponent<MainCameraVehicleRenderer>();
            if (manual != null && manual.enabled)
            {
                manual.enabled = false;
                Plugin.L.LogInfo("Cockpit interior: the game's one-eye manual draw is off; Unity draws it in stereo.");
            }

            var vi = g.vehicleInterior;
            var interior = vi != null ? vi.cameraCockpit : null;
            if (interior == null || !interior.gameObject.activeInHierarchy) return;
            if (!interior.enabled)
            {
                interior.enabled = true;
                interior.stereoTargetEye = StereoTargetEyeMask.Both;
                if (!loggedOnce) { loggedOnce = true; Plugin.L.LogInfo($"Cockpit interior camera '{interior.name}' now renders in both eyes."); }
            }
            float depth = g.mainCamera.depth + 0.5f;
            if (interior.depth != depth) interior.depth = depth;
        }
    }
}
