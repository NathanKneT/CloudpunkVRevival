using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// Blacks out the headset for district passages ("lifts") and loads, whose camera moves are awful in VR.
    /// Black from the first signal until the game is done and the view is steady, then a 0.25 s fade-in.
    /// </summary>
    internal static class TransitionGuard
    {
        public static bool Active { get; private set; }

        private const float SteadyTime = 0.3f; // steady this long ends it
        // wait for the cockpit view: longer than InputBridge's last resort (9.5 s)
        private const float CockpitWait = 10f;
        private const float SettleWait = 12f; // max wait for a steady view once the game is done
        private const float MaxDuration = 45f; // hard cap on the black
        private const float AfterWindow = 3f; // trace keeps going this long after the fade-in
        private const float StepTimeout = 10f; // routine silent this long counts as stopped
        private const string FirstPersonCamera = "Vehicle FirstPersonCam"; // the HOVA's cockpit camera
        private const int ActiveBudget = 120, AfterBudget = 40, SignalBudget = 150;

        private static float startedAt, idleSince = -1f, steadySince = -1f, endedAt = -100f, lastViewButton = -10f, lastCockpitRequest = -10f;
        private static float nextCockpitSwitch, camTransitionSince = -1f, cockpitOnAt = -1f;
        private static bool boardedThroughChase; // boarding went through the outside view
        private static ViewMode modeBefore;
        private static bool wasLoader, wasScene, wasInLift, askedCockpit, flagNoted, camTransitionNoted, menuAtStart;
        private static string reason = "";

        // after a load or passage the cockpit comes back if you were in it. Boarding waits for InputBridge's cockpit request.
        private enum Cause { Load, Passage, ViewSwitch, Boarding }
        private static Cause cause;

        // passage count, EnterLift calls, running routines by pointer
        private static bool isPassage;
        private static int passages, passageNumber, enterCalls;
        private static readonly Dictionary<IntPtr, (int id, int state)> routines = new Dictionary<IntPtr, (int, int)>();
        private static int routineIds;
        private static float lastStepAt = -100f;

        // a menu shows the picture, the transition restarts when it closes
        private static bool suspended;
        private static Cause suspendedCause;

        // cockpit eye distance from the HOVA's centre, learned in flight: farther means another camera rig
        private static float cockpitEyeDistance = -1f, nextEyeSample;
        private static int cockpitEyeCarrier;

        // signals before a transition, printed when it starts
        private static readonly (float time, string what)[] early = new (float, string)[6];
        private static int earlyNext;

        private enum Kind { Signal, Line }
        private static int signalLines, activeLines, afterLines;
        private static float nextSummary, summaryAt, largestSlide, largestSlideTurn, largestError, largestErrorTurn, fastestTurn;
        private static int largeFrames;
        private static Vector3 summaryCarrierPos;
        private static string lastState = "";

        // state line parts, compared each frame: the line is only formatted on change
        private struct StateKey
        {
            public ViewMode Mode;
            public bool Loader, Scene, InLift;
            public bool? Blending;
            public int Routines;
            public IntPtr Camera;
            public string Black, Fade;

            public bool Same(StateKey o) =>
                Mode == o.Mode && Loader == o.Loader && Scene == o.Scene && InLift == o.InLift && Blending == o.Blending
                && Routines == o.Routines && Camera == o.Camera && ReferenceEquals(Black, o.Black) && ReferenceEquals(Fade, o.Fade);
        }
        private static StateKey lastKey;
        private static bool haveKey;

        /// <summary>A cockpit/outside switch right after this is the player's.</summary>
        public static void NoteViewButton() => lastViewButton = Time.unscaledTime;

        /// <summary>Our own cockpit request (InputBridge): not the game's switch.</summary>
        public static void NoteCockpitRequest() => lastCockpitRequest = Time.unscaledTime;

        /// <summary>Black for boarding: InputBridge switches to the cockpit as soon as the game allows.</summary>
        public static bool BoardingBlack => Active && cause == Cause.Boarding;

        /// <summary>Steady and about to end: one-time cockpit setup now won't show as a hitch.</summary>
        public static bool Settling => Active && steadySince >= 0f;

        public static bool PassageBusy => PassageRunning(Time.unscaledTime, CarInLift());

        private static bool Enabled => VRConfig.StereoFixes.Value && VRSession.Active;

        /// <summary>The game is starting a passage or load: black now, before anything moves.</summary>
        public static void Begin(string why, bool passage = false)
        {
            if (Active)
            {
                if (passage && !isPassage) { BecomePassage(); why = $"district passage {passageNumber}, {why}"; }
                if (cause == Cause.ViewSwitch || cause == Cause.Boarding) cause = passage ? Cause.Passage : Cause.Load;
                Signal(why);
                return;
            }
            // passage calls behind an open menu don't black it out, a load from a menu does
            if (!Enabled || CameraDriver.Mode == ViewMode.Startup || (passage && (suspended || MenuOpen()))) { Signal(why); return; }
            Start(why, CameraDriver.Mode, passage ? Cause.Passage : Cause.Load, false);
        }

        /// <summary>A trace line, held until a transition starts.</summary>
        public static void Signal(string what)
        {
            if (Active || Time.unscaledTime - endedAt < AfterWindow) { Trace(what, Kind.Signal); return; }
            early[earlyNext] = (Time.unscaledTime, what);
            earlyNext = (earlyNext + 1) % early.Length;
        }

        public static void PassageEnters(Lift lift)
        {
            string details = "";
            try
            {
                if (lift != null)
                    details = $": '{lift.name}' to scene {lift.destination}, exit '{lift.exitID}', direction {lift.direction}, " +
                              $"its camera '{(lift.camera != null ? lift.camera.name : "none")}', end point {(lift.endPoint != null ? lift.endPoint.position.ToString() : "none")}";
            }
            catch (Exception e) { details = " (" + e.Message + ")"; }
            if (Active && isPassage)
            {
                enterCalls++;
                Signal($"EnterLift called again ({enterCalls} calls in district passage {passageNumber}){details}");
                return;
            }
            Begin("the HOVA entered a district passage" + details, true);
        }

        public static void PassageStep(IntPtr routine, bool running, int state)
        {
            lastStepAt = Time.unscaledTime;
            if (!running)
            {
                if (routines.TryGetValue(routine, out var done))
                {
                    routines.Remove(routine);
                    Signal($"passage routine {done.id} is over ({routines.Count} still running)");
                }
                return;
            }
            if (!routines.TryGetValue(routine, out var known)) known = (++routineIds, -1);
            if (known.state != state) Signal($"passage routine {known.id} at step {state}");
            routines[routine] = (known.id, state);
        }

        /// <summary>From the camera driver each frame, once the view is placed and measured.</summary>
        public static void Update(ViewMode previous, ViewMode mode)
        {
            if (!VRConfig.StereoFixes.Value)
            {
                if (Active) { Active = false; PassageCard.End(); }
                return;
            }
            float now = Time.unscaledTime;
            // polled signals for what the hooks miss. Only their start counts, so a stuck loader can't hold the black.
            bool loader = LoaderBusy(), scene = SceneLoading(), inLift = CarInLift(), menu = MenuOpen();
            bool loadStarted = (loader && !wasLoader) || (scene && !wasScene);
            bool liftStarted = inLift && !wasInLift;
            wasLoader = loader;
            wasScene = scene;
            wasInLift = inLift;
            bool passage = PassageRunning(now, inLift);
            bool busy = loader || scene || passage || mode == ViewMode.Startup;

            // a cockpit/outside switch nobody asked for is the game's camera swing: hidden. Boarding is black from the start.
            bool inGame = previous != ViewMode.Startup;
            bool byButton = now - lastViewButton <= 1.5f;
            // our cockpit request made while black lands just after the end: no second black
            bool byMod = now - lastCockpitRequest <= 3f && now - endedAt <= 0.5f;
            bool leftCockpit = previous == ViewMode.Cockpit && mode == ViewMode.Chase && !byButton;
            bool enteredCockpit = previous == ViewMode.Chase && mode == ViewMode.Cockpit && !byButton && !byMod;
            bool boardedStraight = previous == ViewMode.OnFoot && mode == ViewMode.Cockpit;
            bool boarded = boardedStraight || (previous == ViewMode.OnFoot && mode == ViewMode.Chase && VRConfig.StartInCockpit.Value);

            // a menu must be seen, unless mid-load or it's the menu the load started from
            if (!menu) menuAtStart = false;
            if (Active && menu && !menuAtStart && !loader && !scene) { Suspend(); return; }
            if (suspended && !menu && !Active)
            {
                suspended = false;
                if (Enabled) { Start("the menu closed", modeBefore, mode == ViewMode.Menu ? Cause.Load : suspendedCause, true); return; }
            }
            // loads count even from a menu, the rest waits for it to close
            if (!Active && inGame && Enabled && (loadStarted || (!menu && (liftStarted || leftCockpit || enteredCockpit || boarded))))
            {
                if (loadStarted) Start("the game's loader started", previous, Cause.Load, false);
                else if (liftStarted) Start("the HOVA is in a district passage", previous, Cause.Passage, false);
                else if (boarded) Start(boardedStraight ? "getting into the HOVA straight into the cockpit view: black until it holds still"
                                                        : "getting into the HOVA through the outside view: black until the cockpit view is on", previous, Cause.Boarding, false);
                else Start(leftCockpit ? "the game left the cockpit" : "the game switched to the cockpit", previous, Cause.ViewSwitch, false);
            }

            if (Active || now - endedAt < AfterWindow) TraceFrame(mode, loader, scene, inLift, passage);
            else LearnCockpitEyes(mode, now);
            if (!Active) return;

            if (isPassage) PassageCard.Update(busy, now - startedAt);
            if (cause == Cause.Boarding && mode == ViewMode.Cockpit && cockpitOnAt < 0f) cockpitOnAt = now;
            if (busy)
            {
                idleSince = -1f;
                steadySince = -1f;
            }
            else
            {
                if (idleSince < 0f) { idleSince = now; Signal("the game is done; waiting for a steady view"); }
                bool forces = cause == Cause.Load || cause == Cause.Passage;
                bool wantsCockpit = cause == Cause.Boarding
                    || (forces && (modeBefore == ViewMode.Cockpit || (modeBefore == ViewMode.Menu && VRConfig.StartInCockpit.Value)));
                bool buttonDuringBlack = lastViewButton > startedAt;
                // boarding: a dialogue or menu must be seen. After loads the cockpit switch is ours.
                if (cause == Cause.Boarding && UI.VRUI.Interactive) { End("a dialogue or a menu came with getting in"); return; }
                if (forces && wantsCockpit && mode == ViewMode.Chase && !buttonDuringBlack)
                    SwitchToCockpit(now);
                // only the outside view is waited out. A view picked with the button stands.
                bool viewBack = !wantsCockpit || mode != ViewMode.Chase || buttonDuringBlack || now - idleSince > CockpitWait;
                bool steady = viewBack && ViewMotion.IsSteady && CameraSettled(mode, now);
                if (!steady) steadySince = -1f;
                else if (steadySince < 0f) steadySince = now;
                if (steadySince >= 0f && now - steadySince >= SteadyTime) { End($"steady from +{steadySince - startedAt:0.00} s"); return; }
                if (now - idleSince > SettleWait) { End($"the view never held still for {SteadyTime} s ({ViewMotion.Describe()}, camera {CameraState()})"); return; }
            }
            if (now - startedAt > MaxDuration)
                End($"gave up after {MaxDuration:0} s (loader {loader}, scene loading {scene}, passage {passage}, view {mode})");
        }

        private static void Start(string why, ViewMode before, Cause began, bool resumed)
        {
            float now = Time.unscaledTime;
            VRSession.SetFade(1f, 0f);
            Active = true;
            startedAt = now;
            idleSince = -1f;
            steadySince = -1f;
            askedCockpit = flagNoted = camTransitionNoted = suspended = false;
            menuAtStart = MenuOpen();
            nextCockpitSwitch = 0f;
            camTransitionSince = -1f;
            cockpitOnAt = -1f;
            boardedThroughChase = began == Cause.Boarding && CameraDriver.Mode == ViewMode.Chase;
            modeBefore = before;
            cause = began;
            if (began == Cause.Boarding) Input.InputBridge.BoardingStarts();
            reason = why;
            signalLines = activeLines = 0;
            ResetSummary(now);
            lastState = "";
            haveKey = false;
            isPassage = false;
            if (began == Cause.Passage)
            {
                if (!resumed) BecomePassage();
                else { isPassage = true; PassageCard.Begin(); } // same passage, same number
            }
            else ClearRoutines(); // leftover routine from an interrupted passage
            Plugin.L.LogInfo($"Transition at {now:0.00} s: {(isPassage ? $"district passage {passageNumber}, " : "")}{why}; " +
                             "the headset is black until the game is done and the view is steady.");
            for (int i = 0; i < early.Length; i++)
            {
                var (time, what) = early[(earlyNext + i) % early.Length];
                if (what != null && now - time < 3f) Trace($"(before) {what}", Kind.Signal, time);
            }
            Array.Clear(early, 0, early.Length);
            Trace("the second before: " + ViewMotion.DescribeBefore(1f), Kind.Signal);
        }

        private static void BecomePassage()
        {
            isPassage = true;
            passageNumber = ++passages;
            enterCalls = 1;
            PassageCard.Begin();
        }

        private static void End(string how)
        {
            float now = Time.unscaledTime;
            Active = false;
            endedAt = now;
            afterLines = 0;
            PassageCard.End();
            string what = isPassage ? $"district passage {passageNumber}" : reason;
            Plugin.L.LogInfo($"Transition over after {now - startedAt:0.0} s ({what}), view {CameraDriver.Mode}: {how}; " +
                             $"game done at +{(idleSince >= 0f ? idleSince - startedAt : now - startedAt):0.00} s; the picture fades back in over 0.25 s.");
            if (cause == Cause.Boarding)
                Plugin.L.LogInfo($"Boarding: the headset was black for {now - startedAt:0.00} s getting into the HOVA {(boardedThroughChase ? "through the outside view" : "straight into the cockpit view")}; " +
                                 (cockpitOnAt >= 0f ? $"cockpit view on at +{cockpitOnAt - startedAt:0.00} s, steady {now - cockpitOnAt:0.00} s later." : $"the view is {CameraDriver.Mode} ({how})."));
            ClearRoutines();
            // normally done while black, this covers a timeout
            if ((cause == Cause.Load || cause == Cause.Passage) && modeBefore == ViewMode.Cockpit && CameraDriver.Mode == ViewMode.Chase
                && !askedCockpit && lastViewButton <= startedAt)
                Input.InputBridge.AskForCockpitAgain();
        }

        /// <summary>A menu is open: show the picture for it.</summary>
        private static void Suspend()
        {
            float now = Time.unscaledTime;
            Active = false;
            endedAt = now;
            afterLines = 0;
            suspended = true;
            suspendedCause = cause;
            PassageCard.End();
            Plugin.L.LogInfo($"Transition suspended after {now - startedAt:0.0} s ({(isPassage ? $"district passage {passageNumber}" : reason)}): a menu is open, " +
                             "the picture comes back to show it; black again when it closes, until the view is steady.");
        }

        private static void ClearRoutines()
        {
            routines.Clear();
            lastStepAt = -100f;
        }

        /// <summary>Unity-stopped routines never report their end. Once seen, routines win over the HOVA's flag, which can stick.</summary>
        private static bool PassageRunning(float now, bool inLift)
        {
            if (routines.Count > 0 && now - lastStepAt < StepTimeout) return true;
            if (!Active) return inLift;
            if (lastStepAt >= startedAt)
            {
                if (inLift && routines.Count == 0 && !flagNoted) { flagNoted = true; Signal("the HOVA's passage flag is still up after its routine ended: ignored"); }
                return false;
            }
            return inLift && now - startedAt < 20f;
        }

        /// <summary>Back outside after a load or passage: make the game's cockpit switch while black, every 2 s.</summary>
        private static void SwitchToCockpit(float now)
        {
            // InputBridge first, so its switch count includes this one
            if (!askedCockpit)
            {
                askedCockpit = true;
                Input.InputBridge.AskForCockpitAgain();
                Signal("the view came back outside the HOVA: switching it to the cockpit while black");
            }
            if (now < nextCockpitSwitch) return;
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            if (!Input.InputBridge.HovaReady(car, out _)) return;
            nextCockpitSwitch = now + 2f;
            Input.InputBridge.SwitchToCockpitNow("back outside the HOVA after a " + (cause == Cause.Passage ? "passage" : "load"));
        }

        /// <summary>No blend, no cockpit camera transition, and the cockpit camera when in the cockpit.</summary>
        private static bool CameraSettled(ViewMode mode, float now)
        {
            var g = Global.Instance;
            if (g == null) return true;
            if (CameraBlending() == true) return false;
            var car = g.playerCar;
            // the cockpit cam transition takes ~1 s but its flag can stick, so ignore it after 3 s
            bool camTransition = (mode == ViewMode.Cockpit || mode == ViewMode.Chase) && car != null && car.inInteriorCamTransition;
            if (!camTransition) camTransitionSince = -1f;
            else if (camTransitionSince < 0f) camTransitionSince = now;
            if (camTransition && now - camTransitionSince < 3f) return false;
            if (camTransition && !camTransitionNoted) { camTransitionNoted = true; Signal("the cockpit camera transition flag has been up for 3 s: ignored"); }
            if (mode == ViewMode.Cockpit && cockpitEyeDistance >= 0f && cockpitEyeCarrier == ViewMotion.CarrierId
                && Mathf.Abs(ViewMotion.CarrierDistance - cockpitEyeDistance) > 1.5f) return false;
            return true;
        }

        /// <summary>Cockpit eye distance, from the cockpit camera only (by name, else within 3 m).</summary>
        private static void LearnCockpitEyes(ViewMode mode, float now)
        {
            if (mode != ViewMode.Cockpit) return;
            if (ViewMotion.CarrierId != cockpitEyeCarrier) { cockpitEyeCarrier = ViewMotion.CarrierId; cockpitEyeDistance = -1f; }
            if (now < nextEyeSample || !ViewMotion.IsSteady || CameraBlending() == true) return;
            nextEyeSample = now + 0.25f;
            string live = LiveCameraName();
            bool cockpitCamera = live != null ? live == FirstPersonCamera : ViewMotion.CarrierDistance < 3f;
            if (!cockpitCamera) return;
            float d = ViewMotion.CarrierDistance;
            cockpitEyeDistance = cockpitEyeDistance < 0f ? d : Mathf.Lerp(cockpitEyeDistance, d, 0.2f);
        }

        /// <summary>Null if the brain can't be read (then holds nothing back).</summary>
        private static bool? CameraBlending()
        {
            try
            {
                var brain = Global.Instance != null ? Global.Instance.cameraBrain : null;
                return brain != null ? brain.IsBlending : (bool?)null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Pointer, to see it change without reading its name.</summary>
        private static IntPtr LiveCameraPointer()
        {
            try
            {
                var brain = Global.Instance != null ? Global.Instance.cameraBrain : null;
                var live = brain != null ? brain.ActiveVirtualCamera : null;
                return live != null ? live.Pointer : IntPtr.Zero;
            }
            catch (Exception) { return new IntPtr(-1); }
        }

        private static string LiveCameraName()
        {
            try
            {
                var brain = Global.Instance != null ? Global.Instance.cameraBrain : null;
                var live = brain != null ? brain.ActiveVirtualCamera : null;
                return live != null ? live.Name : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>State changes, then while black cuts and a 2 Hz summary, after it large moves.</summary>
        private static void TraceFrame(ViewMode mode, bool loader, bool scene, bool inLift, bool passage)
        {
            float now = Time.unscaledTime;
            int shownRoutines = passage && routines.Count > 0 ? routines.Count : 0;
            var key = new StateKey
            {
                Mode = mode, Loader = loader, Scene = scene, InLift = inLift, Routines = shownRoutines,
                Camera = LiveCameraPointer(), Blending = CameraBlending(), Black = Level(BlackScreen()), Fade = Level(LoaderFade()),
            };
            if (!haveKey || !key.Same(lastKey))
            {
                haveKey = true;
                lastKey = key;
                string state = $"view {mode}, loader {(loader ? "busy" : "idle")}{(scene ? ", scene loading" : "")}{(inLift ? ", HOVA in passage" : "")}" +
                               $"{(shownRoutines > 0 ? $", {shownRoutines} passage routine(s)" : "")}, camera {CameraState()}, " +
                               $"game black screen {key.Black}, loader fade {key.Fade}";
                if (state != lastState) { lastState = state; Trace(state, Kind.Line); }
            }

            // large: the view slid on its carrier or the carrier jumped
            bool large = ViewMotion.RelativeStep > 0.25f || ViewMotion.RelativeTurn > 10f || ViewMotion.CarrierError > 1f || ViewMotion.CarrierTurn > 10f;
            if (!Active)
            {
                if (large) Trace("after the fade-in, " + (CameraDriver.CutThisFrame ? "jump, blinked: " : "large move: ") + Move(), Kind.Line);
                return;
            }
            if (CameraDriver.CutThisFrame) Trace("jump (hidden by the black): " + Move(), Kind.Line);
            if (large)
            {
                largeFrames++;
                largestSlide = Mathf.Max(largestSlide, ViewMotion.RelativeStep);
                largestSlideTurn = Mathf.Max(largestSlideTurn, ViewMotion.RelativeTurn);
                largestError = Mathf.Max(largestError, ViewMotion.CarrierError);
                largestErrorTurn = Mathf.Max(largestErrorTurn, ViewMotion.CarrierTurn);
            }
            if (ViewMotion.Continuous) fastestTurn = Mathf.Max(fastestTurn, ViewMotion.CarrierYawStep / ViewMotion.Dt);
            if (now < nextSummary) return;
            float span = Mathf.Max(now - summaryAt, 1e-3f);
            string carrier = ViewMotion.CarrierName;
            Trace($"{carrier} at {ViewMotion.CarrierPosition}, {(ViewMotion.CarrierPosition - summaryCarrierPos).magnitude / span:0} m/s, turning at most {fastestTurn:0}°/s; " +
                  $"eyes {ViewMotion.CarrierDistance:0.0} m from it" + (mode == ViewMode.Cockpit && cockpitEyeDistance >= 0f ? $" (usually {cockpitEyeDistance:0.0} m)" : "") +
                  (largeFrames > 0 ? $"; {largeFrames} large frame(s): the view slid up to {largestSlide:0.00} m / {largestSlideTurn:0.0}° on {carrier}, " +
                                     $"{carrier} up to {largestError:0.00} m / {largestErrorTurn:0.0}° beyond its velocity" : "") +
                  $"; {(ViewMotion.IsSteady ? "steady" : "moving")}" + (steadySince >= 0f ? $" for {now - steadySince:0.00} s" : ""), Kind.Line);
            ResetSummary(now);
        }

        private static string Move() => $"{ViewMotion.Describe()}, anchor {ViewMotion.AnchorStep:0.00} m / {ViewMotion.AnchorTurn:0.0}°";

        private static void ResetSummary(float now)
        {
            nextSummary = now + 0.5f;
            summaryAt = now;
            summaryCarrierPos = ViewMotion.CarrierPosition;
            largeFrames = 0;
            largestSlide = largestSlideTurn = largestError = largestErrorTurn = fastestTurn = 0f;
        }

        private static string Level(float alpha) => alpha < 0f ? "?" : alpha <= 0.01f ? "off" : alpha >= 0.99f ? "on" : "fading";

        private static void Trace(string what, Kind kind) => Trace(what, kind, Time.unscaledTime);

        /// <summary>Separate line budgets for signals, while black, and after.</summary>
        private static void Trace(string what, Kind kind, float at)
        {
            int budget = kind == Kind.Signal ? SignalBudget : Active ? ActiveBudget : AfterBudget;
            int used = kind == Kind.Signal ? signalLines++ : Active ? activeLines++ : afterLines++;
            if (used > budget) return;
            if (used == budget) { Plugin.L.LogInfo($"Transition: further {(kind == Kind.Signal ? "signal" : Active ? "trace" : "after-fade")} lines left out."); return; }
            Plugin.L.LogInfo($"Transition {at - startedAt:+0.00;-0.00} s: {what}");
        }

        private static string CameraState()
        {
            string live = LiveCameraName();
            return $"'{live ?? "?"}'{(CameraBlending() == true ? " (blending)" : "")}";
        }

        private static float BlackScreen()
        {
            try
            {
                var g = Global.Instance;
                if (g == null || g.m_BlackScreenCanvas == null || !g.m_BlackScreenCanvas.gameObject.activeInHierarchy) return 0f;
                return g.m_BlackScreenCanvas.alpha;
            }
            catch (Exception) { return -1f; }
        }

        /// <summary>The loader's fade on the flat screen (not in the headset, logged only).</summary>
        private static float LoaderFade()
        {
            try
            {
                var sl = SceneLoader.Instance;
                if (sl == null || sl.canvasGroup == null || !sl.canvasGroup.gameObject.activeInHierarchy) return 0f;
                return sl.canvasGroup.alpha;
            }
            catch (Exception) { return -1f; }
        }

        /// <summary>Pause, inventory, big windows or map, not the title menu.</summary>
        private static bool MenuOpen()
        {
            try
            {
                var g = Global.Instance;
                return g != null && CameraDriver.Mode != ViewMode.Menu && (g.IsInMenu() || g.HasBigWindowOpen() || Global.FullMapOpen);
            }
            catch (Exception) { return false; }
        }

        private static bool CarInLift()
        {
            try
            {
                var g = Global.Instance;
                return g != null && g.playerCar != null && g.playerCar.isInLift;
            }
            catch (Exception) { return false; }
        }

        private static bool LoaderBusy()
        {
            try { return SceneLoader.IsLoading(); }
            catch (Exception) { return false; }
        }

        private static bool SceneLoading()
        {
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (!SceneManager.GetSceneAt(i).isLoaded) return true;
            }
            catch (Exception) { }
            return false;
        }

        internal static bool IsPlayerCar(Collider other)
        {
            var g = Global.Instance;
            return other != null && g != null && g.playerCar != null && other.transform.IsChildOf(g.playerCar.transform);
        }
    }

    // Hooks on the passage and load steps, so the black comes first. Each is patched on its own and catches its own
    // errors: an escaping exception skips the game's method, and a load that never starts stays black.

    /// <summary>Logged only, the passage starts in EnterLift.</summary>
    [HarmonyPatch(typeof(Lift), nameof(Lift.OnTriggerEnter))]
    internal static class PassageTouched
    {
        private static void Prefix(Lift __instance, Collider other)
        {
            try { if (TransitionGuard.IsPlayerCar(other)) TransitionGuard.Signal($"the HOVA touched passage '{__instance.name}'"); }
            catch (Exception) { /* trace only */ }
        }
    }

    /// <summary>Earliest sure sign of a passage.</summary>
    [HarmonyPatch(typeof(PlayerCar), nameof(PlayerCar.EnterLift))]
    internal static class PassageEnter
    {
        private static void Prefix(Lift l)
        {
            try { TransitionGuard.PassageEnters(l); }
            catch (Exception e) { Plugin.L.LogWarning("Transition hook (EnterLift): " + e.Message); }
        }
    }

    /// <summary>Each LiftRoutine step. Found by name prefix, so a renumbered class in another build costs only this hook.</summary>
    [HarmonyPatch]
    internal static class PassageRoutineStep
    {
        private static PropertyInfo stateProperty;

        private static MethodBase TargetMethod()
        {
            foreach (var t in typeof(PlayerCar).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!t.Name.StartsWith("_LiftRoutine_d__", StringComparison.Ordinal)) continue;
                stateProperty = t.GetProperty("__1__state");
                return AccessTools.Method(t, "MoveNext");
            }
            throw new MissingMemberException("PlayerCar", "LiftRoutine state machine");
        }

        private static void Postfix(Il2CppObjectBase __instance, bool __result)
        {
            try
            {
                int state = __result && stateProperty != null ? (int)stateProperty.GetValue(__instance) : -1;
                TransitionGuard.PassageStep(__instance.Pointer, __result, state);
            }
            catch (Exception) { /* the passage flag shows the end too */ }
        }
    }

    /// <summary>Backup if EnterLift isn't hooked.</summary>
    [HarmonyPatch(typeof(Global), nameof(Global.EnteredLift))]
    internal static class PassageEntered
    {
        private static void Prefix()
        {
            try { TransitionGuard.Begin("the game's passage-entered event", true); }
            catch (Exception) { /* the passage flag is polled too */ }
        }
    }

    [HarmonyPatch(typeof(Global), nameof(Global.ExitedLift))]
    internal static class PassageExited
    {
        private static void Prefix()
        {
            try { TransitionGuard.Signal("the game's passage-exited event"); }
            catch (Exception) { /* trace only */ }
        }
    }

    [HarmonyPatch(typeof(Global), nameof(Global.FindLiftExit))]
    internal static class PassageExitFound
    {
        private static void Postfix(string id, LiftExit __result)
        {
            try
            {
                TransitionGuard.Signal(__result == null ? $"no exit '{id}' in the new district" :
                    $"exit '{id}' found at {(__result.exitPoint != null ? __result.exitPoint.position.ToString() : "?")}, its camera '{(__result.camera != null ? __result.camera.name : "none")}'");
            }
            catch (Exception) { /* trace only */ }
        }
    }

    /// <summary>Every scene load: passages, Rania's scene changes, Continue, New Game.</summary>
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.LoadNewScene))]
    internal static class SceneLoadStarts
    {
        private static void Prefix(int sceneID, bool showUI)
        {
            try { TransitionGuard.Begin($"scene {sceneID} starts loading (loading screen {showUI})"); }
            catch (Exception) { /* loader is polled too */ }
        }
    }

    /// <summary>Rania changes scene on foot (door, story trigger).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.ChangeScene))]
    internal static class PlayerChangesScene
    {
        private static void Prefix(int sceneID)
        {
            try { TransitionGuard.Begin($"Rania changes scene ({sceneID})"); }
            catch (Exception) { /* loader is polled too */ }
        }
    }

    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.BeginFadeIn))]
    internal static class LoaderFadeIn
    {
        private static void Prefix(float seconds)
        {
            try { TransitionGuard.Signal($"the loader's BeginFadeIn over {seconds:0.00} s"); }
            catch (Exception) { /* trace only */ }
        }
    }

    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.BeginFadeOut))]
    internal static class LoaderFadeOut
    {
        private static void Prefix(float seconds)
        {
            try { TransitionGuard.Signal($"the loader's BeginFadeOut over {seconds:0.00} s"); }
            catch (Exception) { /* trace only */ }
        }
    }
}
