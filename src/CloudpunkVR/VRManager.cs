using System;
using CloudpunkVR.Input;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using UnityEngine;
using UnityEngine.Events;

namespace CloudpunkVR
{
    /// <summary>Waits for Unity's OpenVR device, then runs every VR system each frame.</summary>
    public class VRManager : MonoBehaviour
    {
        public VRManager(IntPtr ptr) : base(ptr) { }

        private bool hooked;
        private float nextWaitLog;
        private int errors;
        private static float blinkUntil;

        private void Update()
        {
            try
            {
                HovaBody.Restore(); // game scripts see the HOVA body as they left it
                VirtualGamepad.Attach();
                if (!VRSession.Active)
                {
                    FootView.SessionStopped(); // restore the game's camera blend
                    if (Time.unscaledTime > nextWaitLog)
                    {
                        nextWaitLog = Time.unscaledTime + 10f;
                        Diagnostics.LogVrStatus();
                    }
                    return;
                }

                // per-system timings, the log names the slowest
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                VRSession.ConfigureOnce();
                if (!hooked)
                {
                    RenderHook.BeforeRender += OnBeforeRender;
                    RenderHook.PreCull += CockpitMirrors.OnPreCull; // before HovaBody's: the rear mirror's hide is undone first
                    RenderHook.PreCull += HovaBody.OnPreCull;
                    RenderHook.PostRender += CockpitMirrors.OnPostRender;
                    RenderHook.PreRender += CockpitMirrors.OnPreRender;
                    RenderHook.Install(gameObject);
                    hooked = true;
                }
                RenderHook.EnsureGlobalCallback();
                t = FrameTiming.Lap(t, "setup");

                SteamVRInput.TryInit();
                SteamVRInput.Update(); t = FrameTiming.Lap(t, "SteamVRInput");
                FootView.Update(); t = FrameTiming.Lap(t, "FootView"); // before the buttons: the camera tutorial's Square
                InputBridge.Update(); t = FrameTiming.Lap(t, "InputBridge");
                WindowFocus.Update(); // monitor focus means nothing in a headset
                TitleScreen.Update(); // SkipTitleScreen
                CockpitCharm.UpdateGrip(); t = FrameTiming.Lap(t, "CockpitCharm.Grip"); // after game buttons: holding the charm doesn't strafe
                CockpitCoffee.UpdateGrip(); t = FrameTiming.Lap(t, "CockpitCoffee.Grip"); // after wheel and charm took their presses
                ButtonPrompts.Update(); t = FrameTiming.Lap(t, "ButtonPrompts");
                Haptics.Update(); t = FrameTiming.Lap(t, "Haptics");
                AdaptiveTriggers.Update(); t = FrameTiming.Lap(t, "AdaptiveTriggers");

                VRUI.Create();
                VRUI.HookGameUI(); t = FrameTiming.Lap(t, "VRUI.HookGameUI");
                VrevivalMenu.Update(); t = FrameTiming.Lap(t, "VrevivalMenu"); // our settings in the game's menu
                MirrorBlackScreen(); t = FrameTiming.Lap(t, "MirrorBlackScreen");
                ComfortTweaks.Update(); t = FrameTiming.Lap(t, "ComfortTweaks");
                PerformanceTweaks.Update(); t = FrameTiming.Lap(t, "PerformanceTweaks");
                SceneComfort.Update(); t = FrameTiming.Lap(t, "SceneComfort");
                StereoFixes.Update(); t = FrameTiming.Lap(t, "StereoFixes");
                CockpitGlass.Update(); t = FrameTiming.Lap(t, "CockpitGlass");
                CockpitRenderer.Update(); t = FrameTiming.Lap(t, "CockpitRenderer");
                CockpitMirrors.Update(); t = FrameTiming.Lap(t, "CockpitMirrors");
                VRSession.KeepFramePacing(); FrameTiming.Lap(t, "FrameTiming");
            }
            catch (Exception e)
            {
                if (errors++ < 30) Plugin.L.LogError("VR update: " + e);
            }
        }

        // after game Updates, before canvases build: keep the cockpit dialogue/call boxes hidden
        private void LateUpdate()
        {
            if (!VRSession.Active) return;
            Patches.CockpitBoxes.Enforce();
#if DEV
            FootView.LateUpdate(); // dev command `footview fight`
#endif
        }

        private void OnBeforeRender()
        {
            try
            {
                if (!VRSession.Active) return;
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                CameraDriver.OnBeforeRender(); t = FrameTiming.Lap(t, "CameraDriver");
                HovaBody.Update(); t = FrameTiming.Lap(t, "HovaBody"); // after the view is placed
                CockpitMirrors.OnBeforeRender(); t = FrameTiming.Lap(t, "CockpitMirrors.Place");
                // camera cut, menu settling or a first-person switch still gliding: black before this frame, then fade back
                if (CameraDriver.CutThisFrame || TransitionGuard.Active || CameraDriver.MenuSettling || FootView.Hiding) { VRSession.SetFade(1f, 0f); blinkUntil = Time.unscaledTime + 0.1f; }
                WheelGrab.OnBeforeRender(); t = FrameTiming.Lap(t, "WheelGrab"); // wheel, lever and cup before the hands
                LeverGrab.OnBeforeRender(); t = FrameTiming.Lap(t, "LeverGrab");
                CockpitCoffee.Update(); t = FrameTiming.Lap(t, "CockpitCoffee");
                VoxelHands.Update(); t = FrameTiming.Lap(t, "VoxelHands");
                CockpitCharm.Update(); t = FrameTiming.Lap(t, "CockpitCharm"); // after the hands: they push it
                ComfortTweaks.ScaleScreenEffects(); t = FrameTiming.Lap(t, "ScreenEffects");
                WorldRain.OnBeforeRender(); t = FrameTiming.Lap(t, "WorldRain");
                LookHighlight.Update(); t = FrameTiming.Lap(t, "LookHighlight");
                VRUI.UpdatePanel(); t = FrameTiming.Lap(t, "VRUI.UpdatePanel");
                Wrist.Update(); t = FrameTiming.Lap(t, "Wrist");
#if DEV
                CallProbe.Watch(); // dev command `call`, idle unless watching
                StereoTest.Update(VRUI.Layer);
#endif
                LaserPointer.Update(); t = FrameTiming.Lap(t, "LaserPointer");
                Vignette.Update(); t = FrameTiming.Lap(t, "Vignette");
                SpectatorView.Update(); FrameTiming.Lap(t, "SpectatorView"); // last: view and fade are set
#if DEV
                Film.CleanFrame.Watch(); // dev command `clean watch`, after everything is placed
#endif
            }
            catch (Exception e)
            {
                if (errors++ < 30) Plugin.L.LogError("VR render setup: " + e);
            }
        }

        // free the triggers: the PSVR2 Toolkit keeps the last effect after the game closes
        private void OnApplicationQuit()
        {
            AdaptiveTriggers.FreeAtExit();
            PassageCard.DestroyAtExit();
            FrameTiming.LogSession();
        }

        /// <summary>The game fades with a UI panel. In VR the whole view should fade.</summary>
        private static void MirrorBlackScreen()
        {
            var g = Global.Instance;
            float alpha = 0f;
            if (g != null && g.m_BlackScreenCanvas != null && g.m_BlackScreenCanvas.gameObject.activeInHierarchy)
                alpha = g.m_BlackScreenCanvas.alpha;
            if (TransitionGuard.Active) { VRSession.SetFade(1f, 0f); return; } // district load: stay black
            if (Time.unscaledTime < blinkUntil) return; // camera-cut blink running
            VRSession.SetFade(alpha, 0.25f);
        }
    }
}
