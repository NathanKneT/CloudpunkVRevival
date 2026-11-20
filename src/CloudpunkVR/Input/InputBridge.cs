using System;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using InControl;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>SteamVR actions to the virtual gamepad, plus turning, recentering and the cockpit start.</summary>
    internal static class InputBridge
    {
        private static bool snapArmed = true;
        private static bool wasInVehicle;
        private static float vehicleEnteredAt = -1f;
        private static bool cockpitHandled;
        private static int cockpitAttempts;
        private static float transitionSince = -1f;
        private static bool tutorialSquare;

        /// <summary>A district load left the HOVA outside: ask for the cockpit again.</summary>
        public static void AskForCockpitAgain()
        {
            cockpitHandled = false;
            cockpitAttempts = 0;
            directSwitches = boardingTries = 0;
            vehicleEnteredAt = Time.unscaledTime;
        }
        private static float recenterHeldSince = -1f;
        private static bool hudToggledThisHold;
        private static float hudPulseUntil;

        /// <summary>Hold recenter (R3) for 0.8 s to toggle the HUD (d-pad right).</summary>
        private static bool HudHold()
        {
            if (SteamVRInput.Recenter.Down)
            {
                if (recenterHeldSince < 0f) { recenterHeldSince = Time.unscaledTime; hudToggledThisHold = false; }
                if (!hudToggledThisHold && Time.unscaledTime - recenterHeldSince > 0.8f)
                {
                    hudToggledThisHold = true;
                    hudPulseUntil = Time.unscaledTime + 0.1f;
                    Haptics.Pulse(false, 0.05f, 0.5f, 120f);
                }
            }
            else recenterHeldSince = -1f;
            return Time.unscaledTime < hudPulseUntil;
        }

        public static void Update()
        {
            if (!SteamVRInput.Ready) return;
            var mode = CameraDriver.Mode;
            bool ui = VRUI.Interactive;
            bool pointerOnPanel = ui && LaserPointer.PointingAtPanel;

            if (SteamVRInput.Recenter.Pressed)
            {
                CameraDriver.Recenter();
                VRUI.PlaceAgain();
                Haptics.Pulse(false, 0.03f, 0.4f, 150f);
            }

            LeverGrab.Sense(); // lever distances first, so the wheel sees this frame's
            WheelGrab.Update(); // every frame, so a grab never outlives the cockpit
            LeverGrab.Update();
            OnFoot.Update(mode, ui);
            // Sticks
            Vector2 move = SteamVRInput.Move;
            Vector2 look = SteamVRInput.Look;
            Vector2 right = look;
            if (!ui)
            {
                switch (mode)
                {
                    case ViewMode.OnFoot:
                        Turn(look.x);
                        right = Vector2.zero; // the head looks around, the game's look would fight it
                        move = ControllerRelative(move);
                        break;
                    case ViewMode.Cockpit:
                        right = new Vector2(0f, LeverGrab.Climb(look.y)); // climb/dive, or the lift lever
                        move.x = WheelGrab.Steering(move.x); // hands on the wheel steer, else the stick
                        break;
                }
            }
            if (ui)
            {
                if (move.sqrMagnitude > 0.25f || look.sqrMagnitude > 0.25f) LaserPointer.NoteStickNavigation();
                MenuValues.Update(Mathf.Abs(move.x) > Mathf.Abs(look.x) ? move.x : look.x);
                // on an option row left/right changes the value, not the selection
                if (MenuValues.Selected() != null) { move.x = 0f; right.x = 0f; }
            }
            VirtualGamepad.SetSticks(move, right);

            // Buttons, same layout as a DualSense.
            VirtualGamepad.Set(InputControlType.Action1, SteamVRInput.Interact.Down ? 1f : 0f);
            VirtualGamepad.Set(InputControlType.Action2, SteamVRInput.Back.Down ? 1f : 0f);
            // the game's free camera (CameraUnlock) swings the whole view: menus only (an on-foot trade offer isn't a
            // menu), plus the camera tutorial's press (FootView). With FirstPersonOnFoot off, Square on foot is the game's.
            bool menuSquare = ui && !(mode == ViewMode.OnFoot && SteamVRInput.FreeLook.Down && OnlyTradeOffer());
            bool footSquare = mode == ViewMode.OnFoot && !VRConfig.FirstPersonOnFoot.Value;
            if (SteamVRInput.FreeLook.Pressed && !menuSquare && !footSquare && FootView.TutorialTakesSquare) { tutorialSquare = true; FootView.NoteTutorialSquare(); }
            else if (!SteamVRInput.FreeLook.Down) tutorialSquare = false;
            VirtualGamepad.Set(InputControlType.Action3, ((menuSquare || footSquare) && SteamVRInput.FreeLook.Down) || tutorialSquare ? 1f : 0f);
            VirtualGamepad.Set(InputControlType.Action4, SteamVRInput.Inventory.Down ? 1f : 0f);
            // a press that grabbed something doesn't strafe (GrabArbiter). On foot the grips reach instead.
            VirtualGamepad.Set(InputControlType.LeftBumper, SteamVRInput.StrafeLeft.Down && !GrabArbiter.Taken(true) && !OnFoot.UsesGrips ? 1f : 0f);
            VirtualGamepad.Set(InputControlType.RightBumper, SteamVRInput.StrafeRight.Down && !GrabArbiter.Taken(false) && !OnFoot.UsesGrips ? 1f : 0f);
            VirtualGamepad.Set(InputControlType.Options, SteamVRInput.Menu.Down ? 1f : 0f);
            VirtualGamepad.Set(InputControlType.Share, SteamVRInput.Map.Down ? 1f : 0f);
            VirtualGamepad.Set(InputControlType.DPadUp, SteamVRInput.SwitchView.Down && !OnFoot.UsesViewButton ? 1f : 0f); // on foot: jog
            VirtualGamepad.Set(InputControlType.DPadRight, SteamVRInput.ToggleHud.Down || HudHold() ? 1f : 0f);
            // triggers click instead while the laser is on a menu
            VirtualGamepad.Set(InputControlType.RightTrigger, pointerOnPanel ? 0f : SteamVRInput.Accelerate);
            VirtualGamepad.Set(InputControlType.LeftTrigger, pointerOnPanel ? 0f : Mathf.Max(SteamVRInput.Brake, OnFoot.Crouching ? 1f : 0f)); // crouching sneaks

            if (SteamVRInput.SwitchView.Pressed) { cockpitHandled = true; TransitionGuard.NoteViewButton(); } // the player chose a view
            StartFlightsInCockpit();
        }

        /// <summary>Only a trade offer is up (IsInDialog(true)): no menu, big window or full map.</summary>
        private static bool OnlyTradeOffer()
        {
            var g = Global.Instance;
            if (g == null) return false;
            try { return g.IsInDialog(true) && !g.IsInMenu() && !g.HasBigWindowOpen() && !Global.FullMapOpen; }
            catch (Exception) { return false; }
        }

        /// <summary>With HeadRelativeWalking off, forward is where the left controller points.</summary>
        private static Vector2 ControllerRelative(Vector2 move)
        {
            if (VRConfig.HeadRelativeWalking.Value || move.sqrMagnitude < 1e-4f || !SteamVRInput.LeftHand.Valid) return move;
            CameraDriver.TrackingToWorld(SteamVRInput.LeftHand.Position, SteamVRInput.LeftHand.Rotation, out _, out var handRot);
            float delta = Mathf.DeltaAngle(CameraDriver.Yaw(CameraDriver.HeadWorldRotation), CameraDriver.Yaw(handRot));
            Vector3 v = Quaternion.Euler(0f, delta, 0f) * new Vector3(move.x, 0f, move.y);
            return new Vector2(v.x, v.z);
        }

        private static void Turn(float x)
        {
            switch (VRConfig.Turn.Value)
            {
                case TurnMode.Snap:
                    if (snapArmed && Mathf.Abs(x) > 0.7f)
                    {
                        CameraDriver.Turn(Mathf.Sign(x) * VRConfig.SnapTurnAngle.Value);
                        snapArmed = false;
                    }
                    else if (Mathf.Abs(x) < 0.3f) snapArmed = true;
                    break;
                case TurnMode.Smooth:
                    if (Mathf.Abs(x) > 0.15f)
                        CameraDriver.Turn(x * VRConfig.SmoothTurnSpeed.Value * Time.unscaledDeltaTime);
                    break;
            }
        }

        /// <summary>Asks for the cockpit view on every boarding, retrying while the game ignores its toggle.</summary>
        private static void StartFlightsInCockpit()
        {
            var g = Global.Instance;
            // an open menu just pauses the requests
            bool inVehicle = g != null && (CameraDriver.Mode == ViewMode.Chase || CameraDriver.Mode == ViewMode.Cockpit);
            if (inVehicle && !wasInVehicle) { vehicleEnteredAt = Time.unscaledTime; cockpitHandled = false; cockpitAttempts = 0; directSwitches = boardingTries = 0; }
            wasInVehicle = inVehicle;
            if (!inVehicle || cockpitHandled || !VRConfig.StartInCockpit.Value) { ForgetClimb(); return; }
            var car = g.playerCar;
            MeasureClimb(car);
            if (g.IsInMenu()) { vehicleEnteredAt += Time.unscaledDeltaTime; return; } // the wait stops while paused
            if (Global.IsInVehicleCockpit)
            {
                cockpitHandled = true;
                Plugin.L.LogInfo(cockpitAttempts > 0 || directSwitches > 0
                    ? $"Cockpit view on (after {cockpitAttempts} request(s){(directSwitches > 0 ? $" and {directSwitches} direct switch(es)" : "")})."
                    : "Cockpit view on (the game's own).");
                return;
            }
            if (SteamVRInput.SwitchView.Pressed) { cockpitHandled = true; return; }
            if (TransitionGuard.BoardingBlack) { BoardWhileBlack(g, car); return; }
            // some opening scenes lock the view for a minute or more
            float since = Time.unscaledTime - vehicleEnteredAt;
            if (since > 180f) { cockpitHandled = true; Plugin.L.LogWarning("The game did not switch to the cockpit view; staying outside (the view button switches it)."); return; }
            if (since < 1.5f + cockpitAttempts * 4f || Global.BlackScreenIsOn || VRUI.Interactive) return;
            if (!ClearStuckTransition(car)) return;
            cockpitAttempts++;
            // the game can ignore the button for minutes, and always while the passage flag (isInLift) is up:
            // switch directly from the 3rd try, or at once with the flag up
            bool inLift = car != null && car.isInLift;
            if ((cockpitAttempts >= 3 || inLift) && car != null)
            {
                if (cockpitAttempts == 3 || (inLift && cockpitAttempts == 1))
                    Plugin.L.LogInfo(inLift ? "The HOVA's passage flag is up, so the game ignores the view button; switching the HOVA to its cockpit view directly."
                                            : "The view button was ignored twice; switching the HOVA to its cockpit view directly.");
                if (HovaReady(car, out string busy)) SwitchToCockpitNow(inLift ? "the passage flag is up" : "the view button was ignored");
                else if (cockpitAttempts % 10 == 3) Plugin.L.LogInfo($"Cockpit switch waits: {busy}{ClimbNote(busy)}.");
                return;
            }
            PressViewButton(g, car);
        }

        // ---- Getting in while the headset is black.

        private const float BoardingRetry = 0.75f;   // s between tries while black
        private const float StillFor = 0.2f;         // s the height must hold before the switch
        private static int boardingTries, directSwitches;
        private static float nextBoardingTry, readySince = -1f;
        private static bool readyNoted;
        private static string lastBusy;

        /// <summary>
        /// Nobody sees the switch during the boarding black, so switch directly once take-off is over (no fade).
        /// After two failed tries, press the view button instead.
        /// </summary>
        private static void BoardWhileBlack(Global g, PlayerCar car)
        {
            float now = Time.unscaledTime;
            if (car == null || now < nextBoardingTry || Global.BlackScreenIsOn || VRUI.Interactive) return;
            if (!HovaReady(car, out string busy))
            {
                readySince = -1f;
                if (!ReferenceEquals(busy, lastBusy)) { lastBusy = busy; TransitionGuard.Signal("the cockpit switch waits: " + busy + ClimbNote(busy)); }
                return;
            }
            lastBusy = null;
            if (readySince < 0f) readySince = now;
            if (now - readySince < StillFor) return; // take-off settles with a dip
            nextBoardingTry = now + BoardingRetry;
            if (!readyNoted)
            {
                readyNoted = true;
                TransitionGuard.Signal(PlayerClimbs(car)
                    ? "the HOVA is ready for the cockpit (take-off over; it moves up or down on your own input)"
                    : $"the HOVA is ready for the cockpit (take-off over, height still for {StillFor:0.0} s)");
            }
            if (boardingTries < 2)
            {
                boardingTries++;
                if (SwitchToCockpitNow("getting in")) return;
            }
            if (car.isInLift || !ClearStuckTransition(car)) return;
            cockpitAttempts++;
            PressViewButton(g, car);
        }

        public static void BoardingStarts()
        {
            readyNoted = false;
            readySince = -1f;
            nextBoardingTry = 0f;
            lastBusy = null;
        }

        // ---- The game's view switch.

        /// <summary>The game's view button (d-pad up). Its own transition and fade take about 1.5 s.</summary>
        private static void PressViewButton(Global g, PlayerCar car)
        {
            TransitionGuard.NoteCockpitRequest(); // the switch that follows is the mod's, not the game's
            VirtualGamepad.Hold(InputControlType.DPadUp, 0.12f);
            lastPressAt = Time.unscaledTime;
            if (cockpitAttempts <= 2 || cockpitAttempts % 10 == 0)
                Plugin.L.LogInfo($"Asking the game for the cockpit view (attempt {cockpitAttempts}; transition flag {(car != null && car.inInteriorCamTransition)}, in menu {g.IsInMenu()}).");
        }

        /// <summary>The view button is ignored while the transition flag is up, and it can get stuck. Cleared after 5 s.</summary>
        private static bool ClearStuckTransition(PlayerCar car)
        {
            if (car == null || !car.inInteriorCamTransition) { transitionSince = -1f; return true; }
            if (transitionSince < 0f) transitionSince = Time.unscaledTime;
            if (Time.unscaledTime - transitionSince < 5f) return false;
            car.inInteriorCamTransition = false;
            transitionSince = -1f;
            Plugin.L.LogInfo("The HOVA's cockpit camera transition was stuck; cleared it.");
            return true;
        }

        // ---- Whether the HOVA is free for a switch.

        private const string BusyNoHova = "no HOVA", BusyLoading = "the game is loading",
            BusyRoutine = "the HOVA is in one of its routines (take-off, parking or passage)",
            BusyCamTransition = "the HOVA's cockpit camera transition runs", BusyPress = "the view button was just pressed",
            BusyClimb = "the HOVA is still moving up or down";

        private static float lastCarY = float.NaN, climb, stillSince = -1f, lastPressAt = -10f;
        private static IntPtr climbCar;
        private static Transform climbTransform;

        /// <summary>m/s, for the logs.</summary>
        public static float Climb => climb;

        /// <summary>Only measured while a cockpit switch may be wanted.</summary>
        public static bool ClimbMeasured => climbCar != IntPtr.Zero;

        /// <summary>From the height each frame: PlayerCar.verticalSpeed is a rate setting, not the motion.</summary>
        private static void MeasureClimb(PlayerCar car)
        {
            if (car == null) { ForgetClimb(); return; }
            float dt = Time.deltaTime;
            if (dt <= 1e-5f) return;
            if (car.Pointer != climbCar || climbTransform == null) { ForgetClimb(); climbCar = car.Pointer; climbTransform = car.transform; }
            float y = climbTransform.position.y;
            bool first = float.IsNaN(lastCarY);
            climb = first ? 0f : (y - lastCarY) / dt;
            lastCarY = y;
            if (first || Mathf.Abs(climb) >= 0.3f) stillSince = -1f;
            else if (stillSince < 0f) stillSince = Time.unscaledTime;
        }

        private static void ForgetClimb()
        {
            if (climbCar == IntPtr.Zero && float.IsNaN(lastCarY)) return;
            lastCarY = float.NaN;
            stillSince = -1f;
            climb = 0f;
            climbCar = IntPtr.Zero;
            climbTransform = null;
        }

        /// <summary>The player moves the HOVA up or down (stick, or the game's rise/sink flags).</summary>
        private static bool PlayerClimbs(PlayerCar car) => Mathf.Abs(SteamVRInput.Look.y) > 0.3f || car.isRising || car.isSinking;

        private static string ClimbNote(string busy) => ReferenceEquals(busy, BusyClimb) ? $" ({climb:0.0} m/s)" : "";

        /// <summary>
        /// Free for a view switch: not loading, no routine (its end could undo it), no transition, no unread press,
        /// height still. Else busy says why.
        /// </summary>
        public static bool HovaReady(PlayerCar car, out string busy)
        {
            busy = null;
            if (car == null) { busy = BusyNoHova; return false; }
            bool loading, routine;
            try { loading = SceneLoader.IsLoading(); }
            catch (Exception) { loading = false; }
            try { routine = car.IsInRoutine(); }
            catch (Exception) { routine = false; } // the climb check still covers take-off
            if (loading) busy = BusyLoading;
            else if (routine) busy = BusyRoutine;
            else if (car.inInteriorCamTransition) busy = BusyCamTransition;
            // an unread press would toggle back out right after a direct switch
            else if (Time.unscaledTime - lastPressAt < 0.5f) busy = BusyPress;
            // not the passage flag, the game can leave it up (see TransitionGuard)
            else if (stillSince < 0f && !PlayerClimbs(car)) busy = BusyClimb;
            return busy == null;
        }

        /// <summary>
        /// The game's cockpit switch done directly. SwitchToCockpit_Local alone leaves the game thinking it's outside.
        /// If the FPS camera stays off, the flag is put back.
        /// </summary>
        public static bool SwitchToCockpitNow(string why)
        {
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            if (car == null) return false;
            directSwitches++;
            bool was = car.isInVehicleCockpit;
            string failure = null;
            try
            {
                TransitionGuard.NoteCockpitRequest();
                car.isInVehicleCockpit = true;
                g.OnChangeVehicleState(0f);
                var fps = car.fpsCamera;
                if (fps == null || !fps.gameObject.activeSelf) failure = "the HOVA's first-person camera stayed off";
            }
            catch (Exception e) { failure = "the game's change of view failed (" + e.Message + ")"; }
            if (failure != null)
            {
                try
                {
                    car.isInVehicleCockpit = was;
                    g.OnChangeVehicleState(0f);
                }
                catch (Exception e) { failure += "; putting the outside view back failed too (" + e.Message + ")"; }
            }
            string line = "Cockpit switch (" + why + "): the game's own change of view, made directly: " +
                          (failure == null ? "cockpit view on." : failure + "; the flag is back as it was, the view button follows.");
            if (TransitionGuard.Active) TransitionGuard.Signal(line); // into the transition's trace
            if (failure != null) Plugin.L.LogWarning(line);
            else if (!TransitionGuard.Active) Plugin.L.LogInfo(line);
            return failure == null;
        }
    }
}
