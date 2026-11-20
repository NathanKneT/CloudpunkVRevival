using System;
using System.Globalization;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// The HOVA's wheel, taken with the strafe button (one hand or two) and turned under the hands.
    /// Sticky while the button is held; a hand with bad tracking stops steering.
    /// </summary>
    internal static class WheelGrab
    {
        // Reach and grip.
        private const float GrabReach = 0.13f;       // m from the rim
        private const float GrabWindow = 0.25f;      // s a press stays live
        private const float HoldReach = 0.38f;       // m: a holding hand farther than this...
        private const float SlipTime = 0.15f;        // ...for this long has let go
        private const float MinHubDistance = 0.05f, HubMargin = 0.01f; // m: angle means nothing nearer the hub
        private const float GripSlack = 10f;         // deg a drawn grip may trail its hand
        private const float GlideRate = 540f;        // deg/s
        private const float ElsewhereOff = 0.12f;    // m off and near something else: reaching for it
        private const float RestOff = 0.12f, RestBack = 0.10f; // m off the rim: resting hand holds, doesn't steer
        // Two hands.
        private const float DisagreeStretch = 0.08f, AgreeStretch = 0.05f; // m change in the hands' distance
        private const float RegripTime = 0.5f;       // s of steady disagreement before regripping
        private const float LineMinChord = 0.08f;    // m: closer hands steer by their own turns
        private const float SplitRate = 6f;          // deg/s apart: above it one hand slides
        // The wheel.
        private const float MaxTurnRate = 1080f;     // deg/s
        private const float LostHoldTime = 0.6f, LostCentreRate = 60f; // no trusted hand: hold, then centre at this rate
        private const float ReturnTime = 0.35f;      // s to spring back
        private const float GrabBlend = 0.09f, ReleaseBlend = 0.12f; // s to close on and leave the rim
        private const float ReleaseBlendPer = 0.10f; // m off the rim per ReleaseBlend, max 3x
        private const float CatchUpTime = 0.06f;     // s for the shown wheel to catch up after a grab
        private const float FreeEase = 0.06f;        // s: free wheel eases to the stick's steering
        private const float DetentStep = 15f;        // deg between the faint ticks

        private sealed class Hand : TrackedHand
        {
            public Hand(bool left) : base(left) { }

            public float PressedAt = -10f;

            // grip angle from 12 o'clock, thumb side, palm turn, distance off, time too far
            public bool Holding, Trusted;
            public float Alpha, ThumbSign, Palm, Off, OffFor;
            public bool AtHub, Resting, Gliding; // at the hub (no steering), resting off the rim, drawn grip gliding
            public float ReleaseTime = ReleaseBlend; // s for the drawn hand to leave after release
            public float Clearance; // drawn further round to clear a spoke
            public float Psi, LastPsi, Turn; // angle around the hub now, last frame, and the change
            public float Blend;
            public GripPose Pose;
            public Vector3 DrawnInCockpit; // drawn grip, cockpit set
        }

        private static readonly Hand left = new Hand(true), right = new Hand(false);
        private static readonly Hand[] hands = { left, right };
        private static readonly OneEuroFilter filter = new OneEuroFilter(1.2f, 0.15f, 1f);

        private static WheelModel.Frame frozen, liveFrame;
        private static int frozenSpace;
        // wheel angle: hands, shown, game
        private static float handsAngle, visualAngle, gameAngle, catchUp, steer, lockAngle = 65f;
        // free wheel follows the stick's steering once learned
        private static bool freeOurs;
        private static float freeAngle;
        private static bool returning;
        private static float returnT, returnEase, returnFromAngle, returnFromSteer;
        private static bool pairDirty, pairWasBoth, agree = true;
        private static float chord0, lastLine, split, disagreeFor, lostFor;
        private static int lastNotch;
        private static bool atStop;
        private static float lastTick;
        private static bool loggedGrab;
        private static int disagreeLogs, trustLogs, errors;

        private static bool AnyHolding => left.Holding || right.Holding;

        public static GripPose Grip(bool isLeft) => isLeft ? left.Pose : right.Pose;

        /// <summary>Wheel angle (deg, positive right) and its steering, for the log.</summary>
        public static float HeldAngle => handsAngle;
        public static float HeldSteer => steer;

        /// <summary>Steering for the game: the hands', else a blend back to the stick.</summary>
        public static float Steering(float stick)
        {
            if (AnyHolding) return steer;
            return returning ? Mathf.Lerp(returnFromSteer, stick, returnEase) : stick;
        }

        private static bool InUse() =>
            VRConfig.GrabWheel.Value && CameraDriver.Mode == ViewMode.Cockpit && !UI.VRUI.Interactive
            && CameraDriver.CockpitCamera != null && !TransitionGuard.Active;

        // ---------------------------------------------------------------- every frame (game update)

        /// <summary>Every frame before the game reads input. Errors only cost the wheel.</summary>
        public static void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                Stop();
                if (errors++ < 3) Plugin.L.LogError("Wheel: " + e);
            }
        }

        private static void Tick()
        {
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f);
            if (!(simLeft.On ? simLeft.Down : SteamVRInput.StrafeLeft.Down)) GrabArbiter.Let(GrabArbiter.Wheel, true);
            if (!(simRight.On ? simRight.Down : SteamVRInput.StrafeRight.Down)) GrabArbiter.Let(GrabArbiter.Wheel, false);
            var g = Global.Instance;
            var vi = g != null ? g.vehicleInterior : null;
            var wheel = vi != null ? vi.steeringWheel : null;
            WheelSteering.Update(g != null ? g.playerCar : null);
            lockAngle = WheelSteering.Lock;
            if (!InUse())
            {
                // out of the cockpit, menu or load: let go and spring back
                GrabArbiter.Clear(GrabArbiter.Wheel);
                if (wheel == null || !WheelModel.Ready) { Stop(); return; }
                simLeft.On = simRight.On = false;
                foreach (var h in hands)
                    if (h.Holding) Release(h, false);
                gameAngle = freeAngle = WheelModel.GameAngle(vi.steeringWheelData.currentAngle);
                freeOurs = false;
                if (returning) Return(dt);
                else steer = 0f;
                Blend(dt);
                return;
            }
            if (wheel == null || !WheelModel.Prepare(wheel, vi.steeringWheelData.currentAngle))
            {
                GrabArbiter.Clear(GrabArbiter.Wheel);
                Stop();
                return;
            }
            gameAngle = WheelModel.GameAngle(vi.steeringWheelData.currentAngle);
            var car = g.playerCar;
            freeOurs = WheelModel.Learned && car != null;
            freeAngle = freeOurs ? WheelSteering.HandsAngle(Mathf.Clamp(car.steerInput, -1f, 1f)) : gameAngle;
            var live = WheelModel.TrackingFrame(wheel);
            if (!AnyHolding && !returning) // free: follow the stick's steering (the game's pose until learned)
                visualAngle = freeOurs ? Mathf.Lerp(visualAngle, freeAngle, 1f - Mathf.Exp(-dt / FreeEase)) : gameAngle;
            UpdateSimulation(dt);
            Read(left, live, dt);
            Read(right, live, dt);
            liveFrame = live;
            foreach (var h in hands)
            {
                float off = h.Valid && !h.Holding ? ToWheel(AnyHolding ? frozen : live, h.Pos) : float.PositiveInfinity;
                GrabArbiter.Report(GrabArbiter.Wheel, h.Left, off <= GrabReach ? off : float.PositiveInfinity);
            }

            // only the button lets go. A press stays live for GrabWindow.
            foreach (var h in hands)
            {
                if (h.Pressed) h.PressedAt = Time.unscaledTime;
                if (h.Holding && !h.Down) Release(h, false);
                if (h.Holding || !h.Down || Time.unscaledTime - h.PressedAt >= GrabWindow || !h.Valid) continue;
                var f = AnyHolding ? frozen : live;
                if (ToWheel(f, h.Pos) > GrabReach) continue;
                // a claimed press doesn't strafe until released, grabbed or not
                if (!GrabArbiter.Claim(GrabArbiter.Wheel, h.Left)) continue;
                if (h.Healthy) TryGrab(h, f, wheel, g.playerCar);
            }

            if (AnyHolding) Hold(live, dt);
            else if (returning) Return(dt);
            else
            {
                steer = 0f;
                WheelModel.ApplyLearned(wheel);
            }
            Blend(dt);
        }

        private static void Blend(float dt)
        {
            foreach (var h in hands)
                h.Blend = Mathf.MoveTowards(h.Blend, h.Holding ? 1f : 0f, dt / (h.Holding ? GrabBlend : h.ReleaseTime));
        }

        /// <summary>Lets go at once and leaves the wheel to the game.</summary>
        private static void Stop()
        {
            foreach (var h in hands)
            {
                h.Holding = h.Trusted = false;
                h.Blend = 0f;
                h.Pose.Weight = 0f;
            }
            simLeft.On = simRight.On = false;
            returning = false;
            steer = 0f;
            GrabArbiter.Clear(GrabArbiter.Wheel);
        }

        // ---------------------------------------------------------------- input and tracking health

        private static void Read(Hand h, WheelModel.Frame live, float dt)
        {
            var sim = h.Left ? simLeft : simRight;
            if (sim.On) ReadSimulated(h, sim, AnyHolding ? frozen : live);
            else if (LeverGrab.SimulatedHand(h.Left, out var grip, out var rotation, out _, out bool down)) h.ReadOther(grip, rotation, down);
            else h.ReadController(WheelModel.TubeRadius);
            h.UpdateHealth(dt);
        }

        // ---------------------------------------------------------------- grabbing and letting go

        /// <summary>Distance to the rim, or inside the ring to the wheel's face (spokes, hub).</summary>
        private static float ToWheel(WheelModel.Frame f, Vector3 p)
        {
            Around(f, p, out float rho, out float z, out _);
            return rho <= WheelModel.RimRadius ? Mathf.Abs(z) : RimDistance(rho, z);
        }

        private static float RimDistance(float rho, float z) =>
            Mathf.Sqrt((rho - WheelModel.RimRadius) * (rho - WheelModel.RimRadius) + z * z);

        private static void TryGrab(Hand h, WheelModel.Frame f, Transform wheel, PlayerCar car)
        {
            if (!AnyHolding)
            {
                // freeze the frame for the hold, start at the current steering so the HOVA doesn't jerk
                if (WheelModel.FaceDriver(wheel)) f = WheelModel.TrackingFrame(wheel);
                frozen = f;
                frozenSpace = CameraDriver.SpaceVersion;
                handsAngle = car != null ? WheelSteering.HandsAngle(Mathf.Clamp(car.steerInput, -1f, 1f)) : Mathf.Clamp(visualAngle, -lockAngle, lockAngle);
                catchUp = visualAngle - handsAngle;
                returning = false;
                filter.Reset(handsAngle);
                lastNotch = Mathf.FloorToInt(handsAngle / DetentStep);
                atStop = Mathf.Abs(handsAngle) >= lockAngle - 0.05f;
                lostFor = 0f;
                simWheel = handsAngle;
                simTurnSeen = simTurn;
            }
            float psi = Around(f, h.Pos, out float rho, out float z, out var outward);
            // thumb side and palm turn (0 = fingers into the dash), kept for the whole grip
            Vector3 clockwise = Vector3.Cross(f.Axis, outward);
            h.ThumbSign = Vector3.Dot(h.ToIndex, clockwise) >= 0f ? 1f : -1f;
            Vector3 across = h.Forward - clockwise * Vector3.Dot(h.Forward, clockwise);
            h.Palm = across.sqrMagnitude > 1e-4f ? Mathf.Clamp(Mathf.Atan2(Vector3.Dot(across, -outward), Vector3.Dot(across, -f.Axis)) * Mathf.Rad2Deg, -60f, 100f) : 0f;
            // taken at the hub: holds, steers once it moves out (Track)
            h.AtHub = rho < MinHubDistance + HubMargin;
            h.Holding = true;
            h.Trusted = h.Healthy && !h.AtHub;
            h.Gliding = h.Resting = false;
            Regrip(h, psi);
            h.Psi = h.LastPsi = psi;
            h.Turn = 0f;
            h.Off = RimDistance(rho, z);
            h.OffFor = 0f;
            if (h.AtHub) Note(h, "took the wheel at the hub; it holds it and steers once it moves out from the middle");
            pairDirty = true;
            Haptics.Pulse(h.Left, 0.022f, 0.45f, 170f);
            if (!loggedGrab)
            {
                loggedGrab = true;
                Plugin.L.LogInfo($"Wheel grabbed: the HOVA steers with your hands ({WheelModel.Describe()}, lock {lockAngle:0} degrees).");
            }
        }

        /// <summary>Grip where the hand is now (deg around the hub), drawn clear of the spokes.</summary>
        private static void Regrip(Hand h, float psi)
        {
            h.Alpha = psi - handsAngle;
            h.Clearance = WheelModel.SpokeClearance(h.Alpha);
        }

        private static void Release(Hand h, bool slipped, string why = "left the rim")
        {
            h.Holding = h.Trusted = false;
            h.OffFor = 0f;
            h.Gliding = h.Resting = false;
            h.ReleaseTime = ReleaseBlend * Mathf.Clamp(h.Off / ReleaseBlendPer, 1f, 3f);
            pairDirty = true;
            if (slipped)
            {
                Haptics.Pulse(h.Left, 0.02f, 0.35f, 90f);
                Haptics.Pulse(h.Left, 0.02f, 0.35f, 90f, 0.07f);
                Plugin.L.LogInfo($"Wheel: the {(h.Left ? "left" : "right")} hand {why} and let go.");
            }
            else Haptics.Pulse(h.Left, 0.015f, 0.25f, 120f);
            if (AnyHolding) return;
            // both hands off: spring back
            returning = true;
            returnT = returnEase = 0f;
            returnFromAngle = visualAngle;
            returnFromSteer = steer;
        }

        private static void Return(float dt)
        {
            returnT = Mathf.Min(1f, returnT + dt / ReturnTime);
            returnEase = 1f - (1f - returnT) * (1f - returnT);
            visualAngle = Mathf.Lerp(returnFromAngle, freeAngle, returnEase);
            if (returnT >= 1f) returning = false;
        }

        // ---------------------------------------------------------------- holding

        private static void Hold(WheelModel.Frame live, float dt)
        {
            // recenter or camera cut: retake the frame and regrip
            if (CameraDriver.SpaceVersion != frozenSpace || CameraDriver.CutThisFrame)
            {
                frozen = live;
                frozenSpace = CameraDriver.SpaceVersion;
                foreach (var h in hands)
                {
                    if (!h.Holding || !h.Valid) continue;
                    h.Psi = h.LastPsi = Around(frozen, h.Pos, out float rho, out float z, out _);
                    h.Off = RimDistance(rho, z);
                    if (h.Off > HoldReach)
                    {
                        Release(h, true);
                        continue;
                    }
                    Regrip(h, h.Psi);
                    h.Gliding = false;
                }
                pairDirty = true;
                if (!AnyHolding) return;
            }
            foreach (var h in hands)
                if (h.Holding) Track(h, dt);
            if (!AnyHolding) return; // last hand slipped off: Release started the return

            TurnWheel(HandsTurn(dt), dt);
            if (Simulated) FollowSimulation();
            // drawn grip trails up to GripSlack, then slides (glides when leaving the hub)
            foreach (var h in hands)
            {
                if (!h.Holding || !h.Trusted) continue;
                float trail = Mathf.DeltaAngle(h.Alpha + handsAngle, h.Psi);
                if (Mathf.Abs(trail) <= GripSlack) { h.Gliding = false; continue; }
                float move = trail - Mathf.Sign(trail) * GripSlack;
                if (h.Gliding) move = Mathf.Clamp(move, -GlideRate * dt, GlideRate * dt);
                h.Alpha += move;
            }
            catchUp *= Mathf.Exp(-dt / CatchUpTime);
            visualAngle = filter.Filter(handsAngle, dt) + catchUp;
            steer = WheelSteering.Steer(handsAngle);
            Feel();
        }

        /// <summary>Hand angle around the hub, trust, and slipping off.</summary>
        private static void Track(Hand h, float dt)
        {
            bool wasTrusted = h.Trusted;
            h.Turn = 0f;
            if (!h.Valid)
            {
                h.Trusted = false;
                if (wasTrusted) Note(h, "lost its tracking; it stops steering and stays drawn on the rim");
                return;
            }
            h.Psi = Around(frozen, h.Pos, out float rho, out float z, out _);
            h.Turn = Mathf.DeltaAngle(h.LastPsi, h.Psi);
            h.LastPsi = h.Psi;
            h.Off = RimDistance(rho, z);
            if (h.Healthy)
            {
                // clean tracking only: too far, or well off and in reach of something else
                bool elsewhere = h.Off > ElsewhereOff && GrabArbiter.NearOther(GrabArbiter.Wheel, h.Left);
                h.OffFor = h.Off > HoldReach || elsewhere ? h.OffFor + dt : 0f;
                if (h.OffFor > SlipTime)
                {
                    Release(h, true, elsewhere && h.Off <= HoldReach ? "reached for something else" : "left the rim");
                    return;
                }
            }
            // resting off the rim: holds, steers only if the other doesn't
            bool resting = h.Off > (h.Resting ? RestBack : RestOff);
            if (resting != h.Resting)
            {
                h.Resting = resting;
                pairDirty = true;
            }
            // at the hub the angle means nothing (1 cm of hysteresis against flicker)
            bool wasAtHub = h.AtHub;
            h.AtHub = rho < MinHubDistance + (h.AtHub || !wasTrusted ? HubMargin : 0f);
            h.Trusted = h.Healthy && !h.AtHub;
            if (h.Trusted == wasTrusted) return;
            if (h.Trusted)
            {
                h.Turn = 0f;
                pairDirty = true;
                if (wasAtHub)
                {
                    // out of the hub: steer from here, drawn grip glides over
                    h.Gliding = true;
                    Note(h, "moved out of the hub; it steers from where it is");
                }
                else
                {
                    // back from a tracking loss: regrip so the wheel doesn't jump
                    Regrip(h, h.Psi);
                    Note(h, "steers again, from where it is on the rim");
                }
            }
            else Note(h, h.Healthy ? "is at the hub, where its angle means nothing; it holds without steering" : "has unsure tracking (frozen or jumping); it stops steering");
        }

        private static void Note(Hand h, string what)
        {
            if (trustLogs++ < 40) Plugin.L.LogInfo($"Wheel: the {(h.Left ? "left" : "right")} hand {what}.");
        }

        /// <summary>A hand steers when trusted, unless it rests off the rim while the other is on it.</summary>
        private static bool Steers(Hand h, Hand other) => h.Trusted && !(h.Resting && other.Trusted && !other.Resting);

        /// <summary>This frame's turn from the steering hands, deg.</summary>
        private static float HandsTurn(float dt)
        {
            bool l = Steers(left, right), r = Steers(right, left);
            if (!(l && r))
            {
                pairWasBoth = false;
                return l ? left.Turn : r ? right.Turn : 0f;
            }
            Vector3 between = right.Pos - left.Pos;
            Vector3 across = Vector3.ProjectOnPlane(between, frozen.Axis);
            float chord = across.magnitude; // in the rim's plane
            float line = SignedAngle(frozen.Ref, across, frozen.Axis);
            if (pairDirty || !pairWasBoth)
            {
                chord0 = chord;
                lastLine = line;
                split = 0f;
                agree = true;
                disagreeFor = 0f;
            }
            pairDirty = false;
            pairWasBoth = true;
            float lineTurn = Mathf.DeltaAngle(lastLine, line);
            lastLine = line;
            // how fast the hands' own turns part, smoothed
            split += ((left.Turn - right.Turn) / dt - split) * (1f - Mathf.Exp(-dt / 0.08f));

            // a rigid wheel keeps the hands' distance: if it changes, tracking drifted
            float stretch = Mathf.Abs(chord - chord0);
            if (agree && stretch > DisagreeStretch)
            {
                agree = false;
                if (disagreeLogs++ < 10) Plugin.L.LogInfo($"Wheel: the hands no longer hold one wheel ({stretch * 100f:0} cm apart from their grip); the wheel holds.");
            }
            else if (!agree && stretch < AgreeStretch) agree = true;
            if (agree)
            {
                disagreeFor = 0f;
                if (chord < LineMinChord) return 0.5f * (left.Turn + right.Turn);
                if (Mathf.Abs(split) < SplitRate) return lineTurn; // turning together
                // one hand slides against the other: turn only as far as both agree
                float sign = Mathf.Sign(lineTurn);
                if (Mathf.Sign(left.Turn) != sign || Mathf.Sign(right.Turn) != sign) return 0f;
                return sign * Mathf.Min(Mathf.Abs(lineTurn), Mathf.Min(Mathf.Abs(left.Turn), Mathf.Abs(right.Turn)));
            }
            disagreeFor += dt;
            if (disagreeFor > RegripTime)
            {
                // steady disagreement with good tracking: a new grip
                foreach (var h in hands) Regrip(h, h.Psi);
                chord0 = chord;
                agree = true;
                disagreeFor = 0f;
                if (disagreeLogs < 10) Plugin.L.LogInfo("Wheel: the hands took a new grip.");
            }
            return 0f;
        }

        /// <summary>Rate-limited, clamped at the stops. What the wheel can't follow, the grips slide.</summary>
        private static void TurnWheel(float turn, float dt)
        {
            bool trusted = left.Trusted || right.Trusted;
            lostFor = trusted ? 0f : lostFor + dt;
            if (lostFor > LostHoldTime) turn = -Mathf.Sign(handsAngle) * Mathf.Min(Mathf.Abs(handsAngle), LostCentreRate * dt);
            float limited = Mathf.Clamp(turn, -MaxTurnRate * dt, MaxTurnRate * dt);
            float stopped = Mathf.Clamp(handsAngle + limited, -lockAngle, lockAngle);
            float slide = turn - (stopped - handsAngle);
            if (slide != 0f)
                foreach (var h in hands)
                    if (h.Holding && h.Trusted) h.Alpha += slide;
            handsAngle = stopped;
        }

        /// <summary>Bump at the stops, faint ticks every 15 degrees, a clearer one through the centre.</summary>
        private static void Feel()
        {
            if (Mathf.Abs(handsAngle) >= lockAngle - 0.05f)
            {
                if (!atStop) PulseHolding(0.045f, 0.6f, 55f);
                atStop = true;
            }
            else if (Mathf.Abs(handsAngle) < lockAngle - 3f) atStop = false;
            // 1 deg of play so a resting hand doesn't buzz
            if (handsAngle >= lastNotch * DetentStep - 1f && handsAngle < (lastNotch + 1) * DetentStep + 1f) return;
            int notch = Mathf.FloorToInt(handsAngle / DetentStep);
            bool centre = Mathf.Min(notch, lastNotch) == -1 && Mathf.Max(notch, lastNotch) == 0;
            lastNotch = notch;
            if (atStop || Time.unscaledTime - lastTick < 0.045f) return;
            lastTick = Time.unscaledTime;
            if (centre) PulseHolding(0.014f, 0.15f, 190f);
            else PulseHolding(0.01f, 0.06f, 230f);
        }

        private static void PulseHolding(float seconds, float amplitude, float frequency)
        {
            foreach (var h in hands)
                if (h.Holding) Haptics.Pulse(h.Left, seconds, amplitude, frequency);
        }

        /// <summary>Angle from 12 o'clock (clockwise for the driver), distance from the axis and along it.</summary>
        private static float Around(WheelModel.Frame f, Vector3 p, out float rho, out float z, out Vector3 outward)
        {
            Vector3 v = p - f.Centre;
            z = Vector3.Dot(v, f.Axis);
            Vector3 q = v - f.Axis * z;
            rho = q.magnitude;
            outward = rho > 1e-5f ? q * (1f / rho) : f.Ref;
            return SignedAngle(f.Ref, q, f.Axis);
        }

        /// <summary>Signed angle about an axis, the same way round as Quaternion.AngleAxis.</summary>
        private static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis) =>
            Mathf.Atan2(Vector3.Dot(Vector3.Cross(from, to), axis), Vector3.Dot(from, to)) * Mathf.Rad2Deg;

        // ---------------------------------------------------------------- before rendering

        /// <summary>After the game's update: learn from its pose, pose the wheel, place the grips.</summary>
        public static void OnBeforeRender()
        {
            try { Render(); }
            catch (Exception e)
            {
                Stop();
                left.Pose.Weight = right.Pose.Weight = 0f;
                if (errors++ < 3) Plugin.L.LogError("Wheel: " + e);
            }
        }

        private static void Render()
        {
            var g = Global.Instance;
            var vi = g != null ? g.vehicleInterior : null;
            var wheel = vi != null ? vi.steeringWheel : null;
            if (wheel == null || !WheelModel.Ready)
            {
                left.Pose.Weight = right.Pose.Weight = 0f;
                return;
            }
            bool turning = AnyHolding || returning;
            if (InUse()) WheelModel.Learn(wheel, vi.steeringWheelData.currentAngle, g.playerCar != null ? g.playerCar.steerInput : 0f, !turning);
            if (turning || freeOurs) WheelModel.Pose(wheel, visualAngle);
            bool drawn = CameraDriver.Mode == ViewMode.Cockpit && CameraDriver.CockpitCamera != null;
            PlaceGrip(left, wheel, drawn);
            PlaceGrip(right, wheel, drawn);
        }

        private static void PlaceGrip(Hand h, Transform wheel, bool drawn)
        {
            if (h.Blend <= 0f || !drawn)
            {
                h.Pose.Weight = 0f;
                return;
            }
            WheelModel.RimPoint(wheel, h.Alpha + h.Clearance, out var point, out var outward, out var axis);
            float palm = h.Palm * Mathf.Deg2Rad;
            Vector3 forward = -axis * Mathf.Cos(palm) - outward * Mathf.Sin(palm);
            Vector3 thumb = Vector3.Cross(axis, outward) * h.ThumbSign;
            h.DrawnInCockpit = point;
            CameraDriver.CockpitToWorld(point, Quaternion.identity, out var p, out var toWorld);
            h.Pose = new GripPose
            {
                Weight = h.Blend,
                Point = p,
                Forward = toWorld * forward,
                ThumbSide = toWorld * thumb,
                Outward = toWorld * outward,
                TubeRadius = WheelModel.TubeRadius,
                RimRadius = WheelModel.RimRadius,
            };
        }

        // ---------------------------------------------------------------- state and dev tools

        /// <summary>State for the log. With simulated hands it also checks sides and steering.</summary>
        public static string Describe()
        {
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            var vi = g != null ? g.vehicleInterior : null;
            string game = car != null ? $"game steer {car.steerInput:0.00}" : "no car";
            var data = vi != null ? vi.steeringWheelData : default;
            string model = vi != null ? $", game wheel {data.currentAngle:0.0} (target {data.targetAngle:0.0}, max {data.maxAngle:0})" : "";
            string state = AnyHolding ? "held" : returning ? "springing back" : "free";
            string pair = Steers(left, right) && Steers(right, left) ? (agree ? ", one wheel in both hands" : ", hands apart: the wheel holds") : "";
            return $"{state}: {HandState(left)}, {HandState(right)}{pair}; wheel {visualAngle:0.0} deg ({WheelSteering.Describe()}), " +
                   $"steer {steer:0.00}, {game}{model}; presses: left {GrabArbiter.Describe(true)}, right {GrabArbiter.Describe(false)}; " +
                   $"{WheelModel.Describe()}; {CameraDriver.DescribeSeat()}{Checks()}";
        }

        private static string HandState(Hand h)
        {
            string name = h.Left ? "left" : "right";
            if (!h.Holding) return $"{name} free";
            string trust = h.Trusted ? (h.Resting && !Steers(h, h.Left ? right : left) ? " (resting: the other hand steers)" : "")
                : !h.Valid ? " (tracking lost)" : !h.Healthy ? " (tracking unsure)" : " (at the hub: holds, does not steer)";
            string off = h.Valid ? $", {h.Off * 100f:0} cm off the rim" : "";
            string gap = "";
            if (h.Valid && h.Pose.Weight > 0f)
            {
                CameraDriver.TrackingToWorld(h.Pos, Quaternion.identity, out var hand, out _);
                gap = $", drawn {(h.Pose.Point - hand).magnitude * 100f:0.0} cm from the hand";
            }
            return $"{name} at {Mathf.DeltaAngle(0f, h.Alpha + handsAngle):0} deg{gap}{off}{trust}";
        }

        private static string Checks()
        {
            if (!Simulated || !AnyHolding) return "";
            var cam = CameraDriver.CockpitCamera;
            string sides = "";
            if (left.Holding && right.Holding && cam != null && Mathf.Abs(visualAngle) < 30f) // past 30 deg the sides can cross
            {
                Transform eye = cam.transform;
                float lx = Vector3.Dot(left.DrawnInCockpit - eye.position, eye.right), rx = Vector3.Dot(right.DrawnInCockpit - eye.position, eye.right);
                sides = lx < rx ? " sides ok" : $" SIDES FAILED (left {lx:0.00} m, right {rx:0.00} m across the eye)";
            }
            // against where the simulated hands should have put it
            string turn = Mathf.Abs(simWheel) < 5f || Mathf.Sign(steer) == Mathf.Sign(simWheel) ? " steer ok"
                : $" STEER FAILED (the hands turned the wheel to {simWheel:0} deg clockwise as the head sees it, steer {steer:0.00})";
            string follow = "";
            if (SimOnRim())
                follow = Mathf.Abs(handsAngle - simWheel) <= 3f ? " wheel ok"
                    : $" WHEEL FAILED (the hands put it at {simWheel:0.0} deg, it is at {handsAngle:0.0})";
            string result = $"; check:{sides}{turn}{follow}";
            if (result.Contains("FAILED")) Plugin.L.LogWarning("Wheel" + result);
            return result;
        }

        // dev command "wheel": simulated hands, placed as the head sees the wheel
        private sealed class SimHand
        {
            public bool On, Down, PressNext, Frozen;
            public float At;                     // deg from 12 o'clock, clockwise as the head sees it
            public Vector3 Offset, OffsetTarget; // tracking space, moved off the rim
            public float JumpUntil;
            public Vector3 FrozenPos;
        }

        private static readonly SimHand simLeft = new SimHand(), simRight = new SimHand();
        private static readonly SimHand[] sims = { simLeft, simRight };
        private static float simTurn, simTarget;
        private static float simWheel, simTurnSeen; // expected wheel angle, and the sim turn then
        private const float SimTurnRate = 120f, SimDriftSpeed = 1f;

        public static bool Simulated => simLeft.On || simRight.On;

        /// <summary>Where the simulated hands should have turned the wheel, clamped at the stops.</summary>
        private static void FollowSimulation()
        {
            float turn = simTurn - simTurnSeen;
            simTurnSeen = simTurn;
            if (Steers(left, right) || Steers(right, left)) simWheel = Mathf.Clamp(simWheel + turn, -lockAngle, lockAngle);
            else simWheel = handsAngle;
        }

        /// <summary>All simulated holding hands are on the rim and cleanly tracked.</summary>
        private static bool SimOnRim()
        {
            foreach (var sim in sims)
            {
                var h = sim == simLeft ? left : right;
                if (!sim.On || !h.Holding) continue;
                if (sim.Offset.sqrMagnitude > 1e-6f || sim.Frozen || Time.unscaledTime < sim.JumpUntil || !h.Trusted || h.Gliding) return false;
            }
            return agree || !(Steers(left, right) && Steers(right, left)); // two hands: only while they hold one wheel
        }

        /// <summary>Simulated hands' turn since the grab, deg clockwise.</summary>
        public static float SimTurnNow => simTurn;

        /// <summary>Simulated hands turn the wheel to this angle. False without them.</summary>
        public static bool SimTurnTo(float degrees)
        {
            if (!Simulated) return false;
            simTarget = degrees;
            return true;
        }

        /// <summary>A simulated hand's grip, rotation and rim tube radius, tracking space.</summary>
        public static bool SimulatedHand(bool isLeft, out Vector3 grip, out Quaternion rotation, out float barRadius) =>
            SimulatedHand(isLeft, out grip, out rotation, out barRadius, out _);

        /// <summary>Same, plus the button (the lever reads it as its own hand).</summary>
        public static bool SimulatedHand(bool isLeft, out Vector3 grip, out Quaternion rotation, out float barRadius, out bool down)
        {
            var h = isLeft ? left : right;
            var sim = isLeft ? simLeft : simRight;
            grip = h.Pos;
            rotation = h.Valid ? Quaternion.LookRotation(h.Forward, h.ToIndex) : Quaternion.identity;
            barRadius = WheelModel.TubeRadius;
            down = sim.Down;
            return sim.On && h.Valid;
        }

        /// <summary>Nearest rim point, tracking space (for the lever's reach dev command).</summary>
        public static bool NearestRimPoint(Vector3 p, out Vector3 rim)
        {
            var f = AnyHolding ? frozen : liveFrame;
            Around(f, p, out _, out _, out var outward);
            rim = f.Centre + outward * WheelModel.RimRadius;
            return WheelModel.Ready && f.Axis != Vector3.zero;
        }

#if DEV
        /// <summary>
        /// wheel grab [left|right|both] [hub], turn &lt;deg&gt;, release [left|right|both],
        /// faults jump|freeze|drift|slip|away|hub|reach, thaw, stop.
        /// </summary>
        public static void Simulate(string[] a)
        {
            string action = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            string which = a.Length > 2 ? a[2].ToLowerInvariant() : "both";
            foreach (var sim in sims)
            {
                bool isLeft = sim == simLeft;
                if (which != "both" && which != (isLeft ? "left" : "right")) continue;
                switch (action)
                {
                    case "grab":
                        if (!simLeft.Down && !simRight.Down) simTurn = simTarget = 0f; // fresh grab
                        sim.On = sim.Down = sim.PressNext = true;
                        sim.At = isLeft ? -85f : 85f;
                        sim.Offset = sim.OffsetTarget = a.Length > 3 && a[3].ToLowerInvariant() == "hub" ? ToHub(sim) : Vector3.zero;
                        sim.Frozen = false;
                        sim.JumpUntil = 0f;
                        break;
                    case "hub": sim.OffsetTarget = ToHub(sim); break;
                    case "reach":
                        if (LeverGrab.HandleMiddle(out var handle)) sim.OffsetTarget = handle - SimRimPoint(sim, AnyHolding ? frozen : liveFrame);
                        break;
                    case "release": sim.Down = false; break;
                    case "slip": sim.OffsetTarget = SimFrame(frozen, out _) * 0.2f; break;
                    case "away": sim.OffsetTarget = SimFrame(frozen, out _) * 0.5f; break;
                    case "drift": sim.OffsetTarget = Quaternion.AngleAxis(sim.At + simTurn, SimFrame(frozen, out var up)) * up * 0.1f; break;
                    case "jump": sim.JumpUntil = Time.unscaledTime + 0.12f; break;
                    case "freeze": sim.Frozen = true; break;
                    case "thaw": sim.Frozen = false; sim.OffsetTarget = Vector3.zero; sim.JumpUntil = 0f; break;
                    case "stop": sim.On = sim.Down = false; break;
                }
            }
            if (action == "turn" && a.Length > 2) simTarget = float.Parse(a[2], CultureInfo.InvariantCulture);
        }
#endif

        /// <summary>Offset from a simulated hand's rim spot to 3 cm from the hub.</summary>
        private static Vector3 ToHub(SimHand sim)
        {
            var f = AnyHolding ? frozen : liveFrame;
            return f.Centre + (SimRimPoint(sim, f) - f.Centre).normalized * 0.03f - SimRimPoint(sim, f);
        }

        private static Vector3 SimRimPoint(SimHand sim, WheelModel.Frame f)
        {
            Vector3 towardHead = SimFrame(f, out var up);
            return f.Centre + Quaternion.AngleAxis(sim.At + simTurn, towardHead) * up * WheelModel.RimRadius;
        }

        /// <summary>Wheel axis toward the head, and 12 o'clock as the head sees it.</summary>
        private static Vector3 SimFrame(WheelModel.Frame f, out Vector3 up)
        {
            Vector3 towardHead = f.Axis * (Vector3.Dot(f.Axis, VRSession.HeadPosition - f.Centre) >= 0f ? 1f : -1f);
            up = Vector3.ProjectOnPlane(Vector3.up, towardHead).normalized;
            return towardHead;
        }

        private static void UpdateSimulation(float dt)
        {
            simTurn = Mathf.MoveTowards(simTurn, simTarget, SimTurnRate * dt);
            foreach (var sim in sims)
            {
                Vector3 drift = sim.OffsetTarget - sim.Offset;
                float step = SimDriftSpeed * dt;
                sim.Offset = drift.magnitude <= step ? sim.OffsetTarget : sim.Offset + drift.normalized * step;
            }
        }

        private static void ReadSimulated(Hand h, SimHand sim, WheelModel.Frame f)
        {
            h.Down = sim.Down;
            h.Pressed = sim.PressNext;
            sim.PressNext = false;
            // quarter to three, thumbs up, with a faint tremor (an exact repeat reads as frozen)
            Vector3 towardHead = SimFrame(f, out var up);
            Vector3 outward = Quaternion.AngleAxis(sim.At + simTurn, towardHead) * up;
            Vector3 clockwise = Vector3.Cross(towardHead, outward);
            float t = Time.unscaledTime * (h.Left ? 1f : 1.3f);
            Vector3 tremor = new Vector3(Mathf.Sin(t * 7.3f), Mathf.Sin(t * 9.1f + 1f), Mathf.Sin(t * 5.7f + 2f)) * 0.0002f;
            Vector3 pos = f.Centre + outward * WheelModel.RimRadius + sim.Offset + tremor;
            if (sim.Frozen) pos = sim.FrozenPos;
            else sim.FrozenPos = pos;
            if (Time.unscaledTime < sim.JumpUntil) pos += towardHead * 0.35f;
            h.Valid = true;
            h.Pos = pos;
            h.Forward = (-towardHead * 0.985f + outward * 0.17f).normalized;
            h.ToIndex = clockwise * (sim.At < 0f ? 1f : -1f);
        }
    }
}
