using CloudpunkVR.Input;
using HarmonyLib;

namespace CloudpunkVR.Patches
{
    /// <summary>Forwards the game's rumble to the matching controller.</summary>
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.Vibrate), new[] { typeof(Vibration) })]
    internal static class HapticsPatch
    {
        private static void Postfix(Vibration inVibration)
        {
            if (inVibration == null) return;
            float t = inVibration.time > 0f ? inVibration.time : 0.1f;
            Haptics.LastGameRumble = UnityEngine.Time.unscaledTime;
            Haptics.Pulse(true, t, inVibration.leftStrength, 90f);
            Haptics.Pulse(false, t, inVibration.rightStrength, 160f);
        }
    }
}
