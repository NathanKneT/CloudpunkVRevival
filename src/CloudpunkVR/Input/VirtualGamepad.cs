using System;
using System.Collections.Generic;
using InControl;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// A gamepad that only exists inside InControl, so the game keeps its own bindings and menus.
    /// Values are written in InControl's device update, right before it commits them.
    /// </summary>
    internal static class VirtualGamepad
    {
        public static InputDevice Device { get; private set; }

        private static readonly Dictionary<InputControlType, float> pending = new Dictionary<InputControlType, float>();
        private static readonly Dictionary<InputControlType, float> forced = new Dictionary<InputControlType, float>();
        private static Vector2 leftStick, rightStick;
        private static Vector2 forcedLeft, forcedRight;
        private static float forcedUntil;
        private static bool hooked;

        public static readonly InputControlType[] Buttons =
        {
            InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
            InputControlType.LeftBumper, InputControlType.RightBumper,
            InputControlType.LeftTrigger, InputControlType.RightTrigger,
            InputControlType.LeftStickButton, InputControlType.RightStickButton,
            InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
            InputControlType.Options, InputControlType.Share,
        };

        public static bool Attached => Device != null && Device.IsAttached;

        public static void Attach()
        {
            if (Attached) return;
            try
            {
                if (!InputManager.IsSetup) return;
                var d = new InputDevice("PS VR2 Sense (Cloudpunk VR)");
                d.DeviceClass = InputDeviceClass.Controller;
                d.DeviceStyle = InputDeviceStyle.XboxOne; // ButtonPrompts switches it to PlayStation for PS VR2 Sense
                d.AddControl(InputControlType.LeftStickLeft, "Left Stick Left");
                d.AddControl(InputControlType.LeftStickRight, "Left Stick Right");
                d.AddControl(InputControlType.LeftStickUp, "Left Stick Up");
                d.AddControl(InputControlType.LeftStickDown, "Left Stick Down");
                d.AddControl(InputControlType.RightStickLeft, "Right Stick Left");
                d.AddControl(InputControlType.RightStickRight, "Right Stick Right");
                d.AddControl(InputControlType.RightStickUp, "Right Stick Up");
                d.AddControl(InputControlType.RightStickDown, "Right Stick Down");
                foreach (var b in Buttons) d.AddControl(b, b.ToString());
                InputManager.AttachDevice(d);
                Device = d;

                if (!hooked)
                {
                    InputManager.add_OnUpdateDevices((Il2CppSystem.Action<ulong, float>)new Action<ulong, float>(OnUpdateDevices));
                    hooked = true;
                }
                Plugin.L.LogInfo("Virtual PS VR2 gamepad attached to InControl.");
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Could not attach the virtual gamepad: " + e);
            }
        }

        /// <summary>Announces the pad as PlayStation or Xbox style, to match the prompts shown.</summary>
        public static void SetStyle(bool playStation)
        {
            if (Device == null) return;
            try { Device.DeviceStyle = playStation ? InputDeviceStyle.PlayStation4 : InputDeviceStyle.XboxOne; }
            catch (Exception) { /* cosmetic */ }
        }

        public static void Set(InputControlType control, float value) => pending[control] = value;
        public static void SetSticks(Vector2 left, Vector2 right) { leftStick = left; rightStick = right; }

        /// <summary>Dev helper: hold a control for a while.</summary>
        public static void Hold(InputControlType control, float seconds, float value = 1f)
        {
            forced[control] = value;
            forcedUntil = Mathf.Max(forcedUntil, Time.unscaledTime + seconds);
        }

        public static void HoldSticks(Vector2 left, Vector2 right, float seconds)
        {
            forcedLeft = left; forcedRight = right;
            forcedUntil = Mathf.Max(forcedUntil, Time.unscaledTime + seconds);
        }

        private static void OnUpdateDevices(ulong tick, float dt)
        {
            var d = Device;
            if (d == null) return;
            try
            {
                bool forcing = Time.unscaledTime < forcedUntil;
                if (!forcing && forced.Count > 0) { forced.Clear(); forcedLeft = forcedRight = Vector2.zero; }

                Vector2 l = leftStick + (forcing ? forcedLeft : Vector2.zero);
                Vector2 r = rightStick + (forcing ? forcedRight : Vector2.zero);
                d.UpdateLeftStickWithValue(Vector2.ClampMagnitude(l, 1f), tick, dt);
                d.UpdateRightStickWithValue(Vector2.ClampMagnitude(r, 1f), tick, dt);

                foreach (var b in Buttons)
                {
                    pending.TryGetValue(b, out float v);
                    if (forcing && forced.TryGetValue(b, out float f)) v = Mathf.Max(v, f);
                    d.UpdateWithValue(b, v, tick, dt);
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Virtual gamepad update failed: " + e.Message);
                Device = null;
            }
        }
    }
}
