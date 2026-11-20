using System;
using CloudpunkVR.VR;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// In VR the window's focus means nothing. InControl keeps reading in the background, and while unfocused the
    /// game reads only the virtual pad, so a pad on the desk can't open the pause menu. VR only.
    /// </summary>
    internal static class WindowFocus
    {
        private static bool backgroundSet, restricted;
        private static IntPtr restrictedSet;
        private static InputDevice deviceBefore;
        private static int focusLogs, menuLogs;
        private static bool warned;

        public static void Update()
        {
            if (!VirtualGamepad.Attached) return;
            if (!backgroundSet) { backgroundSet = true; KeepReadingInBackground(); }
            var pa = Actions();
            if (pa == null) return;
            if (restricted && pa.Pointer != restrictedSet) restricted = false; // a new set of actions: not restricted yet
            bool want = !Application.isFocused;
            if (want == restricted) return;
            try
            {
                if (want) { deviceBefore = pa.Device; pa.Device = VirtualGamepad.Device; restrictedSet = pa.Pointer; }
                else pa.Device = deviceBefore;
                restricted = want;
                if (focusLogs++ < 6)
                    Plugin.L.LogInfo(want ? "Window focus: the game's window lost the focus; only the VR controllers drive the game until it is back."
                                          : "Window focus: the game's window has the focus again; the game reads every controller as before.");
            }
            catch (Exception e)
            {
                restricted = want;
                if (!warned) { warned = true; Plugin.L.LogWarning("Window focus: the game's input could not be kept to the VR controllers: " + e.Message); }
            }
        }

        private static PlayerActions Actions()
        {
            var gi = GameInput.Instance;
            return gi != null ? gi.playerActions : null;
        }

        /// <summary>Turns off SuspendInBackground, in the InControlManager too so a later setup keeps it off.</summary>
        private static void KeepReadingInBackground()
        {
            try
            {
                bool was = InputManager.SuspendInBackground;
                InputManager.SuspendInBackground = false;
                var manager = UnityEngine.Object.FindObjectOfType<InControlManager>();
                if (manager != null && manager.suspendInBackground) { manager.suspendInBackground = false; was = true; }
                if (was) Plugin.L.LogInfo("Controllers: InControl paused them while the game's window was not focused; they are now read in the background too.");
            }
            catch (Exception e) { Plugin.L.LogWarning("Controllers in the background: " + e.Message); }
        }

        /// <summary>Logs what opened the pause menu. The title menu uses the same call with fromGame false.</summary>
        internal static void MenuOpening(bool fromGame)
        {
            if (!fromGame || !VRSession.Active || menuLogs >= 10) return;
            menuLogs++;
            string menu = "?", cancel = "?";
            var pa = Actions();
            if (pa != null)
            {
                menu = Describe(pa.Menu);
                cancel = Describe(pa.Cancel);
            }
            Plugin.L.LogInfo($"Game menu opens (the pause menu): window focused {Application.isFocused}, its menu action {menu}, its cancel action {cancel}; " +
                             $"the VR controllers' menu button {(SteamVRInput.Ready && SteamVRInput.Menu.Down ? "held" : "not held")}; the game reads {(restricted ? "the VR controllers only" : "every device")}.");
        }

        private static string Describe(PlayerAction action)
        {
            if (action == null) return "none";
            var device = action.ActiveDevice;
            return $"{(action.WasPressed ? "pressed" : action.IsPressed ? "held" : "not pressed")} (last from {action.LastInputType}, device '{(device != null ? device.Name : "none")}')";
        }

        public static string Describe()
        {
            try
            {
                var active = InputManager.ActiveDevice;
                return $"window focused {Application.isFocused}, InControl suspends in background {InputManager.SuspendInBackground}, " +
                       $"active device '{(active != null ? active.Name : "none")}', virtual pad attached {VirtualGamepad.Attached}, " +
                       $"the game reads {(restricted ? "the VR controllers only" : "every device")}";
            }
            catch (Exception e) { return "input state unreadable (" + e.Message + ")"; }
        }
    }

    /// <summary>Logs pause menu opens (see WindowFocus).</summary>
    [HarmonyPatch(typeof(Global), nameof(Global.DisplayMenu))]
    internal static class MenuOpensPatch
    {
        private static void Prefix(bool fromGame)
        {
            try { WindowFocus.MenuOpening(fromGame); }
            catch (Exception) { /* logging only, the menu must open */ }
        }
    }
}
