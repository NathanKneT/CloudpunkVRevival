using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace CloudpunkVR.VR
{
    public enum ViewMode { Startup, Menu, OnFoot, Cockpit, Chase }

    /// <summary>
    /// Puts the tracked head into the game's own cameras: Cinemachine places them as usual, then just before
    /// rendering the pose becomes a VR anchor (level horizon, body yaw on foot) with the head added on top.
    /// </summary>
    internal static class CameraDriver
    {
        public static ViewMode Mode { get; private set; } = ViewMode.Startup;

        /// <summary>Where the calibrated head origin sits in the world this frame.</summary>
        public static Vector3 AnchorPosition { get; private set; }
        public static Quaternion AnchorRotation { get; private set; } = Quaternion.identity;

        public static Vector3 HeadWorldPosition { get; private set; }
        public static Quaternion HeadWorldRotation { get; private set; } = Quaternion.identity;

        public static Camera ViewCamera { get; private set; }

        /// <summary>Anchor turn rate (deg/s), for the comfort vignette.</summary>
        public static float AnchorYawRate { get; private set; }
        public static Vector3 AnchorVelocity { get; private set; }

        private static Vector3 calibPosition;
        private static Quaternion calibYawInverse = Quaternion.identity;
        private static bool calibrated;
        private static float footYaw;
        private static ViewMode lastMode = ViewMode.Startup, loggedMode = ViewMode.Startup;
        private static Quaternion lastAnchorRot = Quaternion.identity;
        private static Vector3 lastAnchorPos;
        private static float smoothedEyeY;
        private static bool haveEyeY;

        private struct CamState
        {
            public Vector3 BasePos, WrittenPos;
            public Quaternion BaseRot, WrittenRot;
            public bool HasWritten;
        }

        private static readonly Dictionary<int, CamState> states = new Dictionary<int, CamState>();
        private static readonly HashSet<int> trackingDisabled = new HashSet<int>();
        private static readonly List<Camera> driven = new List<Camera>();
        private static Camera[] allCameras = new Camera[0];
        private static float nextCameraScan;
        private static bool overlayClears;
        private static int clearLogs;

        public static Camera OverlayCamera; // our camera for the UI panel, laser and vignette
        public static Camera UiCamera; // game's UI camera, retargeted to a texture

        public static void Recenter()
        {
            calibrated = false;
            if (Mode == ViewMode.OnFoot && ViewCamera != null) footYaw = Yaw(ViewCamera.transform.rotation);
        }

        /// <summary>Bumped on calibration or recenter: tracking-space poses kept against the cockpit go stale.</summary>
        public static int SpaceVersion { get; private set; }

        public static void Turn(float degrees) { footYaw += degrees; turnedThisFrame = true; }

        /// <summary>View jumped without the player (cut, teleport, respawn): the headset blinks black.</summary>
        public static bool CutThisFrame { get; private set; }

        /// <summary>Main menu just came up: black until its camera settles.</summary>
        public static bool MenuSettling { get; private set; }
        private static float settleStart, settleSteadySince, settleJump;

        /// <summary>Blinks seen in the headset (not inside a transition).</summary>
        public static int VisibleCuts { get; private set; }
        private static bool turnedThisFrame;
        private static float nextCutLog;
        private static int cutLogs;

        /// <summary>Null outside the cockpit.</summary>
        public static Camera CockpitCamera { get; private set; }
        private static Vector3 cockpitBasePos, viewBasePos;
        private static Quaternion cockpitBaseRot = Quaternion.identity, viewBaseRotInverse = Quaternion.identity;

        /// <summary>World pose to the same spot in the interior set (near the origin, filmed by its own camera).</summary>
        public static void WorldToCockpit(Vector3 p, Quaternion r, out Vector3 cp, out Quaternion cr)
        {
            Quaternion toCockpit = cockpitBaseRot * viewBaseRotInverse;
            cp = cockpitBasePos + toCockpit * (p - viewBasePos);
            cr = toCockpit * r;
        }

        public static void CockpitToWorld(Vector3 cp, Quaternion cr, out Vector3 p, out Quaternion r)
        {
            Quaternion toWorld = Quaternion.Inverse(cockpitBaseRot * viewBaseRotInverse);
            p = viewBasePos + toWorld * (cp - cockpitBasePos);
            r = toWorld * cr;
        }

        /// <summary>Head drop below its calibrated height (crouching), m.</summary>
        public static float HeadDrop => calibrated ? calibPosition.y - VRSession.HeadPosition.y : 0f;

        /// <summary>Same calibration as the head.</summary>
        public static void TrackingToWorld(Vector3 p, Quaternion r, out Vector3 wp, out Quaternion wr)
        {
            wp = AnchorPosition + AnchorRotation * (calibYawInverse * (p - calibPosition)) + Vector3.up * VRConfig.HeightOffset.Value;
            wr = AnchorRotation * calibYawInverse * r;
        }

        public static void WorldToTracking(Vector3 wp, Quaternion wr, out Vector3 p, out Quaternion r)
        {
            Quaternion toTracking = Quaternion.Inverse(AnchorRotation * calibYawInverse);
            p = calibPosition + toTracking * (wp - AnchorPosition - Vector3.up * VRConfig.HeightOffset.Value);
            r = toTracking * wr;
        }

        /// <summary>From the render hook: after all LateUpdates, before any camera renders.</summary>
        public static void OnBeforeRender()
        {
            hullFixValid = gameFixValid = CockpitAligned = false; // set again once the cockpit is placed
            VRSession.SampleHead();
            if (!VRSession.HeadValid) return;

            if (!calibrated)
            {
                calibPosition = VRSession.HeadPosition;
                calibYawInverse = Quaternion.Inverse(Quaternion.Euler(0f, Yaw(VRSession.HeadRotation), 0f));
                calibrated = true;
                SpaceVersion++;
                Plugin.L.LogInfo($"View calibrated at head position {calibPosition}.");
            }

            ScanCameras();
            var view = PickViewCamera();
            // drive a camera the game just enabled (menu after "press any key") from its first frame, not the next scan
            if (view != null && !driven.Contains(view) && view != OverlayCamera && view != UiCamera && view.targetTexture == null && view.stereoTargetEye != StereoTargetEyeMask.None)
            {
                driven.Add(view);
                nextCameraScan = 0f;
            }
            ViewCamera = view;
            ClearWhenNoView(view == null);
            var previousMode = Mode;
            Mode = DetectMode();
            if (Mode != loggedMode)
            {
                Plugin.L.LogInfo($"View: {loggedMode} -> {Mode} at {Time.unscaledTime:0.00} s (camera {(view != null ? view.name : "none")}, loading {SceneLoader.IsLoading()}, black screen {Global.BlackScreenIsOn}).");
                loggedMode = Mode;
            }

            // no game camera (title, loading): keep the last anchor. While black the anchor follows the game unsmoothed.
            Vector3 basePos = AnchorPosition;
            Quaternion baseRot = AnchorRotation;
            if (view != null)
            {
                ReadBase(view, out basePos, out baseRot);
                ComputeAnchor(basePos, baseRot);
            }

            Vector3 rel = calibYawInverse * (VRSession.HeadPosition - calibPosition);
            HeadWorldPosition = AnchorPosition + AnchorRotation * rel + Vector3.up * VRConfig.HeightOffset.Value;
            HeadWorldRotation = AnchorRotation * calibYawInverse * VRSession.HeadRotation;

            // view camera gets the head pose, other cameras the same delta in their own frame (the interior set's camera too)
            if (view != null)
            {
                Quaternion invBaseRot = Quaternion.Inverse(baseRot);
                Vector3 deltaPos = invBaseRot * (HeadWorldPosition - basePos);
                Quaternion deltaRot = invBaseRot * HeadWorldRotation;
                CockpitCamera = null;
                HullAngle = HullShift = GamePairAngle = 0f;
                hullFixValid = gameFixValid = CockpitAligned = false;
                var cockpit = Global.Instance?.vehicleInterior?.cameraCockpit;
                foreach (var cam in driven)
                {
                    if (cam == null) continue;
                    DisableAutoTracking(cam);
                    if (cam == view)
                    {
                        Write(cam, HeadWorldPosition, HeadWorldRotation);
                        continue;
                    }
                    if (IsDescendantOf(cam.transform, view.transform)) continue; // children follow their parent
                    ReadBase(cam, out var p, out var r);
                    if (cam == cockpit && cam.enabled && cam.gameObject.activeInHierarchy)
                    {
                        CockpitCamera = cam;
                        PlaceInSeat(cam, p, r, basePos, baseRot);
                        continue;
                    }
                    Write(cam, p + r * deltaPos, r * deltaRot);
                }
            }

            if (OverlayCamera != null)
            {
                DisableAutoTracking(OverlayCamera);
                OverlayCamera.transform.SetPositionAndRotation(HeadWorldPosition, HeadWorldRotation);
            }

            ViewMotion.Measure(Mode);
            bool jump = ViewMotion.IsJump(Mode, turnedThisFrame);
            SettleMenu(previousMode, jump);
            CutThisFrame = VRConfig.StereoFixes.Value && jump && !MenuSettling; // no blink inside a settling menu's black
            turnedThisFrame = false;
            if (CutThisFrame)
            {
                haveEyeY = false; // eye height restarts instead of gliding
                LogCut();
            }
            TransitionGuard.Update(previousMode, Mode);

            // vignette has nothing to react to while black
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            bool hidden = CutThisFrame || TransitionGuard.Active || MenuSettling;
            AnchorYawRate = hidden ? 0f : Mathf.Abs(Mathf.DeltaAngle(Yaw(lastAnchorRot), Yaw(AnchorRotation))) / dt;
            AnchorVelocity = hidden ? Vector3.zero : (AnchorPosition - lastAnchorPos) / dt;
            if (AnchorVelocity.sqrMagnitude > 200f * 200f) AnchorVelocity = Vector3.zero; // teleport or scene change
            lastAnchorRot = AnchorRotation;
            lastAnchorPos = AnchorPosition;
            lastMode = Mode;
        }

        /// <summary>No game camera draws the eyes (title "press any key"): the overlay clears them, or old frames smear.</summary>
        private static void ClearWhenNoView(bool clear)
        {
            if (OverlayCamera == null || clear == overlayClears) return;
            overlayClears = clear;
            OverlayCamera.clearFlags = clear ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
            if (clearLogs++ < 6)
                Plugin.L.LogInfo(clear ? $"View: no game camera draws the eyes at {Time.unscaledTime:0.00} s; the overlay clears them to black."
                                       : $"View: a game camera draws the eyes again at {Time.unscaledTime:0.00} s.");
        }

        /// <summary>The menu camera shows a stale frame then jumps 43 m: black until it holds 0.3 s (2 s max).</summary>
        private static void SettleMenu(ViewMode previousMode, bool jump)
        {
            float now = Time.unscaledTime;
            if (previousMode == ViewMode.Startup && Mode == ViewMode.Menu)
            {
                MenuSettling = true;
                settleStart = settleSteadySince = now;
                settleJump = 0f;
                return;
            }
            if (!MenuSettling) return;
            if (Mode != ViewMode.Menu) { MenuSettling = false; return; }
            if (jump || !ViewMotion.Continuous) { settleSteadySince = now; settleJump = Mathf.Max(settleJump, ViewMotion.AnchorStep); }
            if (now - settleSteadySince < 0.3f && now - settleStart < 2f) return;
            MenuSettling = false;
            Plugin.L.LogInfo($"View: main menu in view at {now:0.00} s, black for its first {now - settleStart:0.00} s " +
                             (settleJump > 0f ? $"(its camera jumped {settleJump:0.0} m as it came up)." : "(its camera held its course)."));
        }

        /// <summary>One log line per blink (2/s max).</summary>
        private static void LogCut()
        {
            if (TransitionGuard.Active) return;
            VisibleCuts++;
            if (Time.unscaledTime < nextCutLog || cutLogs >= 200) return;
            nextCutLog = Time.unscaledTime + 0.5f;
            cutLogs++;
            Plugin.L.LogInfo($"View cut at {Time.unscaledTime:0.00} s ({Mode}): {ViewMotion.Describe()}; the headset blinked.");
        }

        // eye pose per HOVA interior, in the interior's frame, taken once at rest
        private struct Seat
        {
            public Vector3 Pos;
            public Quaternion Rot;
            // anchor in the HOVA's frame at seat time: its turn since is the body's turn vs the interior
            public Vector3 CarAnchorPos;
            public Quaternion CarAnchorRot;
        }

        /// <summary>HOVA body turn vs the seat's interior, degrees. 0 while following the game's cameras.</summary>
        public static float HullAngle { get; private set; }

        /// <summary>World eye shift in the HOVA's frame since seat time, m (the camera pulls back on boost).</summary>
        public static float HullShift { get; private set; }

        /// <summary>Degrees between the seat cockpit and the game's camera pair (its lean into turns).</summary>
        public static float GamePairAngle { get; private set; }

        private static bool hullFixValid;
        private static Vector3 hullFixPos;
        private static Quaternion hullFixRot = Quaternion.identity;

        /// <summary>
        /// Move (p -> pos + rot * p) that puts something rigid with the HOVA where the world camera saw it at seat time.
        /// False while following the game's cameras or outside the cockpit.
        /// </summary>
        public static bool HullFix(out Vector3 pos, out Quaternion rot)
        {
            pos = hullFixPos;
            rot = hullFixRot;
            return hullFixValid;
        }

        private static bool gameFixValid;
        private static Vector3 gameFixPos;
        private static Quaternion gameFixRot = Quaternion.identity;

        /// <summary>Move that lines a world-camera object up with the interior the way the game's camera pair does.</summary>
        public static bool GameFix(out Vector3 pos, out Quaternion rot)
        {
            pos = gameFixPos;
            rot = gameFixRot;
            return gameFixValid;
        }

        /// <summary>Cockpit drawn as the game composes it (following its cameras, or seat within 0.3° and 5 mm of seat time).</summary>
        public static bool CockpitAligned { get; private set; }

        public static Vector3 CockpitToAnchorSpace(Vector3 cp) => Quaternion.Inverse(cockpitBaseRot) * (cp - cockpitBasePos);

        public static Vector3 WorldToAnchorSpace(Vector3 p) => Quaternion.Inverse(AnchorRotation) * (p - AnchorPosition);

        private static readonly Dictionary<int, Seat> seats = new Dictionary<int, Seat>();
        private static Transform seatFrame;
        private static int seatFrameOf;
        private static Seat heldSeat;
        private static bool holdingSeat;
        private static Vector3 lastGuessPos;
        private static Quaternion lastGuessRot = Quaternion.identity;
        private static float seatStillFor, guessTurnRate, guessMoveRate, hovaClimb, lastHovaY = float.NaN;

        /// <summary>
        /// Films the interior from a fixed seat. The game's interior camera leans into turns, which in VR swings the cockpit
        /// around a still head. Follows the game's mapping until the HOVA rests 0.2 s, then keeps that pose.
        /// </summary>
        private static void PlaceInSeat(Camera cam, Vector3 camPos, Quaternion camRot, Vector3 basePos, Quaternion baseRot)
        {
            Quaternion gameToCockpit = camRot * Quaternion.Inverse(baseRot);
            Vector3 seatPos = camPos + gameToCockpit * (AnchorPosition - basePos);
            Quaternion seatRot = gameToCockpit * AnchorRotation;
            var frame = SeatFrame();
            var g = Global.Instance;
            var car = g != null && g.playerCar != null ? g.playerCar.transform : null;
            holdingSeat = false;
            if (frame != null && !TransitionGuard.Active)
            {
                Quaternion toFrame = Quaternion.Inverse(frame.rotation);
                var guess = new Seat { Pos = toFrame * (seatPos - frame.position), Rot = toFrame * seatRot };
                int id = frame.GetInstanceID();
                if (seats.TryGetValue(id, out heldSeat)) holdingSeat = true;
                else
                {
                    float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
                    guessTurnRate = Quaternion.Angle(guess.Rot, lastGuessRot) / dt;
                    guessMoveRate = (guess.Pos - lastGuessPos).magnitude / dt;
                    bool still = guessTurnRate < 1f && guessMoveRate < 0.01f;
                    seatStillFor = still && HovaAtRest() ? seatStillFor + dt : 0f;
                    lastGuessPos = guess.Pos;
                    lastGuessRot = guess.Rot;
                    if (seatStillFor >= 0.2f && car != null)
                    {
                        Quaternion toCar = Quaternion.Inverse(car.rotation);
                        guess.CarAnchorRot = toCar * AnchorRotation;
                        guess.CarAnchorPos = toCar * (AnchorPosition - car.position);
                        seats[id] = guess;
                        Plugin.L.LogInfo($"Cockpit: the seat taken with the HOVA at rest, eye at {guess.Pos} in the interior's frame; the interior now stays still around you.");
                    }
                    heldSeat = guess;
                }
                seatPos = frame.position + frame.rotation * heldSeat.Pos;
                seatRot = frame.rotation * heldSeat.Rot;
            }
            cockpitBasePos = seatPos;
            cockpitBaseRot = seatRot;
            viewBasePos = AnchorPosition;
            viewBaseRotInverse = Quaternion.Inverse(AnchorRotation);
            Quaternion toCockpit = cockpitBaseRot * viewBaseRotInverse;
            GamePairAngle = Quaternion.Angle(gameToCockpit, toCockpit);
            // world to interior by the game's cameras, back by the seat's
            Quaternion backFromCockpit = Quaternion.Inverse(toCockpit);
            gameFixRot = backFromCockpit * gameToCockpit;
            gameFixPos = viewBasePos + backFromCockpit * (camPos - cockpitBasePos) - gameFixRot * basePos;
            gameFixValid = true;
            if (holdingSeat && car != null)
            {
                Quaternion toCar = Quaternion.Inverse(car.rotation);
                HullAngle = Quaternion.Angle(toCar * AnchorRotation, heldSeat.CarAnchorRot);
                HullShift = (toCar * (AnchorPosition - car.position) - heldSeat.CarAnchorPos).magnitude;
                // HOVA as if the anchor sat where it did at seat time
                Quaternion fromSeat = AnchorRotation * Quaternion.Inverse(heldSeat.CarAnchorRot);
                hullFixRot = fromSeat * toCar;
                hullFixPos = AnchorPosition - hullFixRot * car.position - fromSeat * heldSeat.CarAnchorPos;
                hullFixValid = true;
            }
            // game's interior camera vs the seat
            float pairShift = (cockpitBasePos - (camPos + gameToCockpit * (AnchorPosition - basePos))).magnitude;
            CockpitAligned = !holdingSeat || (HullAngle < 0.3f && GamePairAngle < 0.3f && HullShift < 0.005f && pairShift < 0.005f);
            WorldToCockpit(HeadWorldPosition, HeadWorldRotation, out var p, out var r);
            Write(cam, p, r);
        }

        private static bool HovaAtRest()
        {
            var car = Global.Instance != null ? Global.Instance.playerCar : null;
            if (car == null) return false;
            // measured: the game's verticalSpeed is a setting (2), not the motion
            float y = car.transform.position.y, dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            hovaClimb = float.IsNaN(lastHovaY) ? 0f : (y - lastHovaY) / dt;
            lastHovaY = y;
            return Mathf.Abs(car.steerInput) < 0.05f && Mathf.Abs(car.strafeInput) < 0.05f && Mathf.Abs(hovaClimb) < 0.3f
                   && car.speedBoostTimer <= 0f && Mathf.Abs(car.currentSpeed) < 1.5f && AnchorYawRate < 2f;
        }

        /// <summary>Interior frame (charm's parent), else the wheel's mount.</summary>
        private static Transform SeatFrame()
        {
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            if (vi == null) return null;
            int id = vi.GetInstanceID();
            if (id != seatFrameOf || seatFrame == null)
            {
                seatFrameOf = id;
                seatFrame = vi.interiorLocal != null ? vi.interiorLocal : vi.steeringWheel != null ? vi.steeringWheel.parent : null;
                seatStillFor = 0f;
            }
            return seatFrame;
        }

        /// <summary>Via the interior frame as it is now (the stored mapping is a frame behind).</summary>
        public static void CockpitToTracking(Vector3 cp, Quaternion cr, out Vector3 p, out Quaternion r)
        {
            Vector3 basePos = cockpitBasePos;
            Quaternion baseRot = cockpitBaseRot;
            if (holdingSeat && seatFrame != null)
            {
                basePos = seatFrame.position + seatFrame.rotation * heldSeat.Pos;
                baseRot = seatFrame.rotation * heldSeat.Rot;
            }
            Quaternion toWorld = Quaternion.Inverse(baseRot * viewBaseRotInverse);
            WorldToTracking(viewBasePos + toWorld * (cp - basePos), toWorld * cr, out p, out r);
        }

        public static string DescribeSeat()
        {
            var cam = CockpitCamera;
            if (cam == null) return "seat: not in the cockpit";
            var frame = SeatFrame();
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            var wheel = vi != null ? vi.steeringWheel : null;
            string eye = wheel != null && frame != null ? $", eye from the wheel {Quaternion.Inverse(frame.rotation) * (cam.transform.position - wheel.position)} (interior axes)" : "";
            var car = Global.Instance != null ? Global.Instance.playerCar : null;
            string rest = car == null ? "no HOVA" :
                $"steer {car.steerInput:0.00}, strafe {car.strafeInput:0.00}, climb {hovaClimb:0.00} m/s, boost {car.speedBoostTimer:0.0}, " +
                $"speed {car.currentSpeed:0.00}, yaw {AnchorYawRate:0.0} deg/s, camera {guessTurnRate:0.0} deg/s {guessMoveRate * 100f:0.0} cm/s";
            return $"seat {(holdingSeat ? "kept" : frame == null ? "none (no interior frame)" : $"following the game's cameras (at rest for {seatStillFor:0.00} s: {rest})")}; " +
                   $"anchor {AnchorRotation.eulerAngles}, interior frame {(frame != null ? frame.name + " " + frame.rotation.eulerAngles : "-")}{eye}; " +
                   $"HOVA body against the cockpit {HullAngle:0.0} deg, {HullShift * 100f:0.0} cm ({HovaBody.Summary()}), game's camera pair {GamePairAngle:0.0} deg";
        }
        private static void ComputeAnchor(Vector3 basePos, Quaternion baseRot)
        {
            Quaternion rot;
            Vector3 pos = basePos;
            var g = Global.Instance;
            switch (Mode)
            {
                case ViewMode.OnFoot:
                    if (lastMode != ViewMode.OnFoot) footYaw = Yaw(baseRot);
                    rot = Quaternion.Euler(0f, footYaw, 0f);
                    pos = SmoothEyeHeight(basePos, g);
                    break;
                case ViewMode.Cockpit:
                    var car = g != null && g.playerCar != null ? g.playerCar.transform : null;
                    if (car != null)
                        rot = VRConfig.LevelHorizonInCockpit.Value ? Quaternion.Euler(0f, car.eulerAngles.y, 0f) : car.rotation;
                    else
                        rot = Quaternion.Euler(0f, Yaw(baseRot), 0f);
                    haveEyeY = false;
                    break;
                default:
                    rot = Quaternion.Euler(0f, Yaw(baseRot), 0f);
                    haveEyeY = false;
                    break;
            }
            AnchorPosition = pos;
            AnchorRotation = rot;
        }

        /// <summary>Rania's walk bobs the camera (VR sickness): eye height is low-pass filtered against her feet.</summary>
        private static Vector3 SmoothEyeHeight(Vector3 basePos, Global g)
        {
            var player = g != null ? g.player : null;
            if (player == null) return basePos;
            float feetY = player.transform.position.y;
            float eye = basePos.y - feetY;
            if (!haveEyeY || TransitionGuard.Active) { smoothedEyeY = eye; haveEyeY = true; }
            smoothedEyeY = Mathf.Lerp(smoothedEyeY, eye, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 2f));
            return new Vector3(basePos.x, feetY + smoothedEyeY, basePos.z);
        }

        public static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"mode={Mode} view={(ViewCamera ? ViewCamera.name : "-")} head={VRSession.HeadPosition}/{VRSession.HeadRotation.eulerAngles} " +
                          $"anchor={AnchorPosition}/{AnchorRotation.eulerAngles} headWorld={HeadWorldPosition}/{HeadWorldRotation.eulerAngles}");
            foreach (var c in driven)
            {
                if (c == null) continue;
                states.TryGetValue(c.GetInstanceID(), out var s);
                sb.AppendLine($"   {c.name} depth={c.depth} pos={c.transform.position} rot={c.transform.eulerAngles} base={s.BasePos}/{s.BaseRot.eulerAngles} near={c.nearClipPlane} far={c.farClipPlane} mask=0x{c.cullingMask:X8}");
            }
            if (OverlayCamera) sb.AppendLine($"   overlay pos={OverlayCamera.transform.position} layer mask=0x{OverlayCamera.cullingMask:X8}");
            if (UI.VRUI.Panel) sb.AppendLine($"   panel pos={UI.VRUI.Panel.position} rot={UI.VRUI.Panel.eulerAngles} scale={UI.VRUI.Panel.localScale} interactive={UI.VRUI.Interactive}");
            return sb.ToString();
        }

        private static ViewMode DetectMode()
        {
            var g = Global.Instance;
            if (g == null) return ViewMode.Startup;
            if (g.menuCamera != null && g.menuCamera.gameObject.activeInHierarchy && ViewCamera != null && ViewCamera.transform == g.menuCamera)
                return ViewMode.Menu;
            if (ViewCamera == null) return ViewMode.Startup;
            if (Global.IsInVehicle) return Global.IsInVehicleCockpit ? ViewMode.Cockpit : ViewMode.Chase;
            return ViewMode.OnFoot;
        }

        /// <summary>Menu camera on the title screen, else the main camera.</summary>
        private static Camera PickViewCamera()
        {
            var g = Global.Instance;
            if (g != null)
            {
                if (g.menuCamera != null && g.menuCamera.gameObject.activeInHierarchy)
                {
                    var mc = g.menuCamera.GetComponent<Camera>();
                    if (mc != null && mc.enabled) return mc;
                }
                if (g.mainCamera != null && g.mainCamera.isActiveAndEnabled) return g.mainCamera;
            }
            Camera best = null;
            foreach (var c in driven)
                if (c != null && (best == null || c.depth < best.depth)) best = c;
            return best;
        }

        private static void ScanCameras()
        {
            if (Time.unscaledTime >= nextCameraScan)
            {
                nextCameraScan = Time.unscaledTime + 0.5f;
                allCameras = Object.FindObjectsOfType<Camera>();
            }
            driven.Clear();
            foreach (var c in allCameras)
            {
                if (c == null || !c.isActiveAndEnabled) continue;
                if (c == OverlayCamera || c == UiCamera) continue;
                if (c.targetTexture != null || c.stereoTargetEye == StereoTargetEyeMask.None) continue;
                driven.Add(c);
            }
            // cockpit camera is disabled and rendered by the game by hand, but still needs the head pose
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            if (vi != null && vi.cameraCockpit != null && !vi.cameraCockpit.enabled && vi.cameraCockpit.gameObject.activeInHierarchy && !driven.Contains(vi.cameraCockpit))
                driven.Add(vi.cameraCockpit);
        }

        private static void ReadBase(Camera cam, out Vector3 pos, out Quaternion rot)
        {
            int id = cam.GetInstanceID();
            var t = cam.transform;
            pos = t.position;
            rot = t.rotation;
            if (states.TryGetValue(id, out var s) && s.HasWritten && (pos - s.WrittenPos).sqrMagnitude < 1e-10f && Quaternion.Angle(rot, s.WrittenRot) < 0.001f)
            {
                // unmoved since our write (paused, no Cinemachine update): reuse the game's pose
                pos = s.BasePos;
                rot = s.BaseRot;
                return;
            }
            s.BasePos = pos;
            s.BaseRot = rot;
            states[id] = s;
        }

        private static void Write(Camera cam, Vector3 pos, Quaternion rot)
        {
            cam.transform.SetPositionAndRotation(pos, rot);
            int id = cam.GetInstanceID();
            states.TryGetValue(id, out var s);
            s.WrittenPos = cam.transform.position;
            s.WrittenRot = cam.transform.rotation;
            s.HasWritten = true;
            states[id] = s;
        }

        private static void DisableAutoTracking(Camera cam)
        {
            int id = cam.GetInstanceID();
            if (trackingDisabled.Contains(id)) return;
            XRDevice.DisableAutoXRCameraTracking(cam, true);
            trackingDisabled.Add(id);
        }

        private static bool IsDescendantOf(Transform t, Transform ancestor)
        {
            for (var p = t.parent; p != null; p = p.parent)
                if (p == ancestor) return true;
            return false;
        }

        public static float Yaw(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = q * Vector3.up * -1f; // straight up/down: use the top of the head
            f.y = 0f;
            return f.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }
    }
}
