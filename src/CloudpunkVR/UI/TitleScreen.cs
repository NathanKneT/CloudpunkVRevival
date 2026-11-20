using System;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using InControl;
using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// The game's "press any key" after loading. With SkipTitleScreen, one Square press on the virtual pad passes it
    /// (not Cross: a late Cross would hit Continue in the menu). Otherwise it shows on the panel like a menu.
    /// </summary>
    internal static class TitleScreen
    {
        private const float Delay = 0.25f;        // the game's fade in
        private const float PressSeconds = 0.1f;
        private const float Patience = 2f;        // wait for the fade out after a press
        private const int Tries = 3;
        private const InputControlType Button = InputControlType.Action3; // Square

        private enum State { None, Showing, Pressed, Passed, GaveUp }

        private static State state;
        private static CanvasGroup prompt;
        private static float lastAlpha, shownAt, firstPressAt, pressedAt, passedAt, reportAt = -1f, skippedAt = -1f;
        private static int presses;
        private static bool byUs, keptForRun;

        private static bool Skip => VRConfig.SkipTitleScreen.Value && !keptForRun;

        /// <summary>SceneLoader's confirmation group.</summary>
        public static CanvasGroup Prompt => prompt;

        /// <summary>Skip off, or the skip failed.</summary>
        public static bool OnPanel => prompt != null && (state == State.GaveUp || (state == State.Showing && !Skip));

        /// <summary>Each frame, after the controllers are read.</summary>
        public static void Update()
        {
            float now = Time.unscaledTime;
            if (CameraDriver.Mode != ViewMode.Startup)
            {
                // the prompt only exists while no game camera is on
                if (byUs && reportAt < 0f && CameraDriver.Mode == ViewMode.Menu) reportAt = now + 1f;
                if (reportAt >= 0f && now >= reportAt) Report(now);
                state = State.None;
                prompt = null;
                return;
            }

            float alpha = Alpha();
            bool shown = alpha > 0.01f;
            bool falling = alpha < lastAlpha - 0.0001f;
            lastAlpha = alpha;
            switch (state)
            {
                case State.None:
                    if (!shown) break;
                    state = State.Showing;
                    shownAt = now;
                    presses = 0;
                    byUs = false;
                    reportAt = -1f;
                    if (!Skip) Plugin.L.LogInfo("Title screen: the game waits on its \"press any key\"; shown on the panel (SkipTitleScreen off), any button passes it.");
                    break;
                case State.Showing:
                    if (!shown || falling) { Passed(now, false); break; }
                    // after the fade in, so the game's wait loop is already running
                    bool ready = now - shownAt >= Delay && (alpha >= 0.99f || now - shownAt >= 1f);
                    if (Skip && ready && VirtualGamepad.Attached)
                    {
                        Press(now);
                        Plugin.L.LogInfo($"Title screen: the game waits on its \"press any key\" ({now - shownAt:0.00} s shown); one press of Square passes it (SkipTitleScreen).");
                    }
                    break;
                case State.Pressed:
                    if (!shown || falling) { Passed(now, true); break; }
                    if (now - pressedAt < Patience) break;
                    if (presses < Tries) { Press(now); break; }
                    state = State.GaveUp;
                    Plugin.L.LogWarning($"Title screen: skip FAILED: {presses} presses of Square did not pass the game's \"press any key\" " +
                                       $"(the game reads '{ActiveDevice()}'); it now shows on the panel, press a button.");
                    break;
                case State.GaveUp:
                    if (!shown || falling) Passed(now, false);
                    break;
            }
        }

        private static void Press(float now)
        {
            VirtualGamepad.Hold(Button, PressSeconds);
            if (presses++ == 0) firstPressAt = now;
            pressedAt = now;
            state = State.Pressed;
        }

        private static void Passed(float now, bool ours)
        {
            state = State.Passed;
            passedAt = now;
            byUs = ours;
            if (ours) skippedAt = now;
        }

        /// <summary>Logs the skip result once the menu has been up a second.</summary>
        private static void Report(float now)
        {
            reportAt = -1f;
            byUs = false;
            if (CameraDriver.Mode != ViewMode.Menu)
            {
                Plugin.L.LogInfo($"Title screen: skipped at {skippedAt:0.00} s, but the view is {CameraDriver.Mode} a second after the menu came up.");
                return;
            }
            Plugin.L.LogInfo($"Title screen: skipped at {skippedAt:0.00} s: the game's \"press any key\" showed {firstPressAt - shownAt:0.00} s, " +
                             $"{presses} press{(presses > 1 ? "es" : "")} of Square, fading out {passedAt - pressedAt:0.00} s after; main menu up (view {CameraDriver.Mode}, " +
                             $"panel {(VRUI.MenuShown ? "framed on the menu" : "not shown yet")}, HUD pieces in view: {HudLayout.ShownPieces()}).");
        }

        /// <summary>0 when the prompt isn't there.</summary>
        private static float Alpha()
        {
            try
            {
                if (prompt == null)
                {
                    var loader = SceneLoader.Instance;
                    if (loader == null) return 0f;
                    prompt = loader.confirmationCanvasGroup;
                    if (prompt == null) return 0f;
                }
                return prompt.isActiveAndEnabled ? prompt.alpha : 0f;
            }
            catch (Exception) { prompt = null; return 0f; }
        }

        private static string ActiveDevice()
        {
            try { var d = InputManager.ActiveDevice; return d != null ? d.Name : "none"; }
            catch (Exception) { return "?"; }
        }

#if DEV
        /// <summary>
        /// Dev command `title`: one Square press if the prompt shows.
        /// `title keep`, sent while loading, keeps the prompt for this run.
        /// </summary>
        public static void PassNow(bool keep)
        {
            if (keep)
            {
                keptForRun = true;
                Plugin.L.LogInfo("title: the game's \"press any key\" is kept for this run (as SkipTitleScreen = false).");
                return;
            }
            float alpha;
            try
            {
                var loader = SceneLoader.Instance;
                var group = loader != null ? loader.confirmationCanvasGroup : null;
                alpha = group != null && group.isActiveAndEnabled ? group.alpha : 0f;
            }
            catch (Exception e) { Plugin.L.LogWarning("title: the game's loader is unreadable: " + e.Message); return; }
            var g = Global.Instance;
            bool menu = g != null && g.menuCamera != null && g.menuCamera.gameObject.activeInHierarchy;
            if (alpha > 0.01f && !menu)
            {
                VirtualGamepad.Hold(Button, PressSeconds);
                Plugin.L.LogInfo($"title: the game's \"press any key\" shows (alpha {alpha:0.00}); one press of Square passes it.");
                return;
            }
            Plugin.L.LogInfo($"title: no \"press any key\" showing ({(skippedAt >= 0f ? $"SkipTitleScreen passed it at {skippedAt:0.00} s" : "nothing skipped it")}; " +
                             $"main menu {(menu ? "up" : "not up")}); nothing pressed.");
        }
#endif
    }
}
