using System;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// PS VR2 Sense adaptive triggers via the PSVR2 Toolkit: R2 throttle (wall when empty or locked), L2 brake.
    /// Freed in menus and at quit, since the Toolkit keeps the last effect. Positions 0-9, strengths 1-8.
    /// </summary>
    internal static class AdaptiveTriggers
    {
        private static readonly string[] sent = new string[2];
        private static bool freedAtExit;

        public static void Update()
        {
            bool onPsvr2 = VRSession.Active && SteamVRInput.ControllerType == "playstation_vr2_sense";
            Psvr2Toolkit.Tick(onPsvr2);
            if (!Psvr2Toolkit.WantTriggers || !Psvr2Toolkit.Connected)
            {
                // forget what was sent so effects get re-sent when it comes back
                sent[0] = sent[1] = null;
                return;
            }

            var (rightKey, rightMsg) = ThrottleEffect();
            var (leftKey, leftMsg) = BrakeEffect();
            Apply(Psvr2Toolkit.Right, rightKey, rightMsg);
            Apply(Psvr2Toolkit.Left, leftKey, leftMsg);
        }

        private static bool Free => VRUI.Interactive || CameraDriver.Mode == ViewMode.Startup || CameraDriver.Mode == ViewMode.Menu || Time.timeScale < 0.01f;

        private static (string, byte[]) ThrottleEffect()
        {
            byte hand = Psvr2Toolkit.Right;
            if (Free) return ("off", Off(hand));
            var mode = CameraDriver.Mode;
            if (mode != ViewMode.Cockpit && mode != ViewMode.Chase) return ("off", Off(hand)); // R2 does nothing on foot
            var car = Global.Instance != null ? Global.Instance.playerCar : null;
            if (car == null) return ("off", Off(hand));
            if (car.controlsFrozen || car.isInLift || car.currentFuel <= 0f) return ("locked", Feedback(hand, 1, 8));
            if (car.speedBoostTimer > 0f) return ("boost", Vibration(hand, 2, 5, 35));
            return ("throttle", Feedback(hand, 2, 3));
        }

        private static (string, byte[]) BrakeEffect()
        {
            byte hand = Psvr2Toolkit.Left;
            if (Free) return ("off", Off(hand));
            var mode = CameraDriver.Mode;
            if (mode == ViewMode.Cockpit || mode == ViewMode.Chase) return ("brake", Feedback(hand, 1, 5));
            if (mode == ViewMode.OnFoot) return ("sneak", Feedback(hand, 3, 2));
            return ("off", Off(hand));
        }

        private static void Apply(byte hand, string key, byte[] message)
        {
            if (sent[hand] == key) return;
            sent[hand] = key;
            Psvr2Toolkit.Send(message);
        }

        private static byte[] Off(byte hand) => Psvr2Toolkit.Message(Psvr2Toolkit.CmdTriggerOff, hand);
        private static byte[] Feedback(byte hand, byte position, byte strength) => Psvr2Toolkit.Message(Psvr2Toolkit.CmdTriggerFeedback, hand, position, strength);
        private static byte[] Vibration(byte hand, byte position, byte amplitude, byte frequency) => Psvr2Toolkit.Message(Psvr2Toolkit.CmdTriggerVibration, hand, position, amplitude, frequency);

        /// <summary>Frees both triggers right away and stops the connection. Only the first call does anything.</summary>
        public static void FreeAtExit()
        {
            if (freedAtExit) return;
            freedAtExit = true;
            try
            {
                if (sent[0] != null || sent[1] != null)
                    Psvr2Toolkit.SendNow(Off(Psvr2Toolkit.Left), Off(Psvr2Toolkit.Right));
            }
            catch (Exception) { /* nothing more can be done at exit */ }
            Psvr2Toolkit.Stop();
        }
    }
}
