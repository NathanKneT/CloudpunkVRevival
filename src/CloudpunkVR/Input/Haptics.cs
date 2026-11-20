using System;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using HarmonyLib;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// Extra controller feedback hooked on the game's own sounds and events (menus, calls, money, lifts, crashes),
    /// plus an engine hum in the HOVA.
    /// </summary>
    internal static class Haptics
    {
        private static float lastUiTick = -1f;
        private static float nextEngineBeat;

        /// <summary>When the game last rumbled, so its collision rumble isn't doubled.</summary>
        public static float LastGameRumble = -1f;

        /// <summary>Last HOVA hit, and the hardest contact (m/s) in the window, so a scrape after a crash doesn't hide it.</summary>
        public static float LastCollision = -10f, LastCollisionSpeed;
        public const float CollisionWindow = 0.2f;
        private static float collisionsFrom = -10f;

        public static void Collided(float speed)
        {
            float now = Time.unscaledTime;
            if (now - collisionsFrom >= CollisionWindow) { collisionsFrom = now; LastCollisionSpeed = 0f; }
            LastCollision = now;
            LastCollisionSpeed = Mathf.Max(LastCollisionSpeed, speed);
        }

        private static bool Enabled => VRConfig.Haptics.Value && VRConfig.HapticsStrength.Value > 0.01f && VRSession.Active;

        /// <summary>One pulse, scaled by the strength setting. delay is scheduled by SteamVR.</summary>
        public static void Pulse(bool left, float seconds, float amplitude, float frequency, float delay = 0f)
        {
            if (!Enabled) return;
            SteamVRInput.HapticAt(left, delay, seconds, Mathf.Clamp01(amplitude * VRConfig.HapticsStrength.Value), frequency);
        }

        public static void Both(float seconds, float amplitude, float frequency, float delay = 0f)
        {
            Pulse(true, seconds, amplitude, frequency, delay);
            Pulse(false, seconds, amplitude, frequency, delay);
        }

        /// <summary>Menu tick in the laser hand. Repeats within 50 ms merge.</summary>
        public static void UiTick(float seconds, float amplitude, float frequency)
        {
            if (Time.unscaledTime - lastUiTick < 0.05f) return;
            lastUiTick = Time.unscaledTime;
            Pulse(LaserPointer.LeftActive, seconds, amplitude, frequency);
        }

        public static void Ring()
        {
            // a phone in the left hand: three short buzzes, a pause, three more
            for (int burst = 0; burst < 2; burst++)
                for (int i = 0; i < 3; i++)
                    Pulse(true, 0.05f, 0.55f, 90f, burst * 0.6f + i * 0.11f);
        }

        public static void DoubleTap(float amplitude) { Both(0.02f, amplitude, 170f); Both(0.025f, amplitude * 1.2f, 190f, 0.08f); }

        public static void Coin() { Pulse(false, 0.012f, 0.3f, 240f); Pulse(false, 0.015f, 0.35f, 260f, 0.06f); }

        public static void Thunk() => Both(0.12f, 0.5f, 45f);

        public static void Impact(float speed)
        {
            if (Time.unscaledTime - LastGameRumble < 0.2f) return; // the game rumbled for it already
            float a = Mathf.InverseLerp(1.5f, 15f, speed);
            if (a <= 0.05f) return;
            Both(0.06f + 0.12f * a, 0.25f + 0.75f * a, 40f);
        }

        /// <summary>Engine hum.</summary>
        public static void Update()
        {
            if (!Enabled || !VRConfig.EngineRumble.Value) return;
            var mode = CameraDriver.Mode;
            if ((mode != ViewMode.Cockpit && mode != ViewMode.Chase) || VRUI.Interactive || Time.timeScale < 0.01f) return;
            if (Time.unscaledTime < nextEngineBeat) return;
            nextEngineBeat = Time.unscaledTime + 0.05f;
            var car = Global.Instance != null ? Global.Instance.playerCar : null;
            if (car == null || car.controlsFrozen || car.currentFuel <= 0f) return;
            float throttle = Mathf.Abs(car.gasInput);
            float speed = car.currentMaxSpeed > 0f ? Mathf.Clamp01(Mathf.Abs(car.currentSpeed) / car.currentMaxSpeed) : 0f;
            float boost = car.speedBoostTimer > 0f ? 0.06f : 0f;
            float amp = 0.025f + 0.07f * throttle + 0.05f * speed + boost;
            if (throttle < 0.05f && speed < 0.05f) amp = 0.02f; // idling: barely there
            Both(0.06f, amp, 55f + 45f * speed);
        }
    }

    // ---- The game's events that drive the feedback ----

    [HarmonyPatch(typeof(Global), nameof(Global.PlayScrollSound))]
    internal static class ScrollSoundHaptics { private static void Postfix() => Haptics.UiTick(0.012f, 0.22f, 210f); }

    [HarmonyPatch(typeof(Global), nameof(Global.PlaySelectSound))]
    internal static class SelectSoundHaptics { private static void Postfix() => Haptics.UiTick(0.02f, 0.4f, 170f); }

    [HarmonyPatch(typeof(Global), nameof(Global.PlayConfirmSound))]
    internal static class ConfirmSoundHaptics { private static void Postfix() => Haptics.UiTick(0.03f, 0.5f, 150f); }

    [HarmonyPatch(typeof(Global), nameof(Global.PlayFailSound))]
    internal static class FailSoundHaptics
    {
        private static void Postfix()
        {
            Haptics.Pulse(LaserPointer.LeftActive, 0.04f, 0.5f, 60f);
            Haptics.Pulse(LaserPointer.LeftActive, 0.04f, 0.5f, 60f, 0.09f);
        }
    }

    [HarmonyPatch(typeof(Global), nameof(Global.PlayIncomingCallSound))]
    internal static class CallSoundHaptics { private static void Postfix() => Haptics.Ring(); }

    [HarmonyPatch(typeof(Global), nameof(Global.PlayNotificationSound))]
    internal static class NotificationSoundHaptics { private static void Postfix() => Haptics.DoubleTap(0.3f); }

    [HarmonyPatch(typeof(Global), nameof(Global.PlaySuccessSound))]
    internal static class SuccessSoundHaptics { private static void Postfix() => Haptics.DoubleTap(0.35f); }

    [HarmonyPatch(typeof(Global), nameof(Global.PlayUnlockSound))]
    internal static class UnlockSoundHaptics { private static void Postfix() => Haptics.DoubleTap(0.4f); }

    [HarmonyPatch(typeof(Global), nameof(Global.GainMoney))]
    internal static class MoneyHaptics { private static void Postfix() => Haptics.Coin(); }

    [HarmonyPatch(typeof(Global), nameof(Global.EnteredLift))]
    internal static class LiftEnterHaptics { private static void Postfix() => Haptics.Thunk(); }

    [HarmonyPatch(typeof(Global), nameof(Global.ExitedLift))]
    internal static class LiftExitHaptics { private static void Postfix() => Haptics.Thunk(); }

    [HarmonyPatch(typeof(PlayerCar), nameof(PlayerCar.OnCollisionEnter))]
    internal static class CollisionHaptics
    {
        private static void Postfix(Collision col)
        {
            try
            {
                // contact first: counts for the coffee even if the speed can't be read
                Haptics.Collided(0f);
                if (col == null) return;
                float speed = col.relativeVelocity.magnitude;
                Haptics.Collided(speed);
                Haptics.Impact(speed);
            }
            catch (Exception) { /* feedback is best effort */ }
        }
    }
}
