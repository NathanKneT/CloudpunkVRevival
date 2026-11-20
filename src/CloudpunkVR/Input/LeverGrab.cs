using System;
using System.Globalization;
using CloudpunkVR.UI;
using CloudpunkVR.VR;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// The HOVA's lift lever, taken with the strafe button and tilted under the hand. Its angle is the climb input
    /// (neutral middle sixth, full at the stops). Sticky while the button is held, springs back on release.
    /// </summary>
    internal static class LeverGrab
    {
        // Reach and grip.
        private const float GrabReach = 0.09f;        // m from the handle's bar
        private const float GrabWindow = 0.25f;       // s a press stays live
        private const float HoldReach = 0.35f;        // m: a holding hand farther than this...
        private const float SlipTime = 0.15f;         // ...for this long has let go
        private const float MinPivotDistance = 0.04f, PivotMargin = 0.01f; // m: angle means nothing nearer the hinge
        private const float StopPlay = 1f;            // deg past a stop before sliding (jitter)
        private const float BarSlack = 0.015f;        // m a drawn grip may trail its hand
        private const float StraightBar = 2f;         // m: a straight bar is a huge rim for VoxelHands
        // The lever.
        private const float DetentShare = 1f / 6f;    // neutral share of travel either side of centre
        private const float NotchStep = 5f;           // deg between faint ticks
        private const float MaxRate = 600f;           // deg/s
        private const float LostHoldTime = 0.5f, LostCentreRate = 40f; // no trusted hand: hold, then recentre
        private const float ReturnTime = 0.35f;       // s to spring back
        private const float GrabBlend = 0.09f, ReleaseBlend = 0.12f; // s to close on and leave the handle
        private const float ReleaseBlendPer = 0.10f;  // m off the handle per ReleaseBlend, max 3x
        private const float ElsewhereOff = 0.12f;     // m off and near something else: reaching for it
        private const float CatchUpTime = 0.06f;      // s for the shown lever to catch up after a grab
        private const float LearnInput = 0.6f, LearnTime = 0.15f; // firm input held this long shows the game's lean

        private sealed class Hand : TrackedHand
        {
            public Hand(bool left) : base(left) { }

            public float PressedAt = -10f;
            public bool Trusted;               // tracking good enough to move the lever
            public float Off, OffFor;          // distance from the bar, and time spent too far
            public float Palm, ThumbSign;      // palm turn about the bar, thumb side
            public float OnBar;                // m along the handle, as drawn
            public float Blend;
            public float ReleaseTime = ReleaseBlend; // s for the drawn hand to leave after release
            public GripPose Pose;

            /// <summary>The drawn hand goes back to the real one, slower from farther.</summary>
            public void LeaveHandle() => ReleaseTime = ReleaseBlend * Mathf.Clamp(Off / ReleaseBlendPer, 1f, 3f);
        }

        private static readonly Hand left = new Hand(true), right = new Hand(false);
        private static readonly Hand[] hands = { left, right };
        private static Hand holder;
        private static readonly OneEuroFilter filter = new OneEuroFilter(1.2f, 0.15f, 1f);

        private static LeverModel.Frame frozen, live;
        private static int frozenSpace;
        // deg about the hinge (the game's currentAngle): hand, shown, game
        private static float handAngle, visualAngle, gameAngle, catchUp, travel = 30f, detent = 5f;
        private static float psi, psiRef, angleRef; // hand angle about the hinge, and references at grab
        private static float climb;                 // positive climbs
        // sign of the game lever's angle for a climb: data invert x a learned settings sign
        private static float climbSign = -1f, settingSign = 1f;
        private static bool signFromGame;           // settings sign seen on the game's lever
        // cached per interior
        private static IntPtr interiorPtr, leverPtr;
        private static Transform cachedLever, leverParent;
        private static bool returning;
        private static float returnT, returnEase, returnFromAngle, returnFromClimb;
        private static float lostFor;
        private static int lastNotch;
        private static bool atStop, inDetent;
        private static float lastTick;
        private static float voteSign, voteFor;
        private static bool loggedData, loggedGrab;
        private static int trustLogs, handoverLogs, errors;
        // HOVA height while held, for the log
        private static float lastHeight = float.NaN, heightAtGrab, verticalSpeed;
        private static float riseMax, riseMin; // m, highest and lowest since the grab

        /// <summary>Climb input sent to the game this frame (right stick y).</summary>
        public static float Fed { get; private set; }

        public static GripPose Grip(bool isLeft) => isLeft ? left.Pose : right.Pose;

        /// <summary>Climb input for the game: the hand's, else a blend back to the stick.</summary>
        public static float Climb(float stick)
        {
            Fed = holder != null ? climb : returning ? Mathf.Lerp(returnFromClimb, stick, returnEase) : stick;
            return Fed;
        }

        private static bool InUse() =>
            VRConfig.GrabLever.Value && CameraDriver.Mode == ViewMode.Cockpit && !VRUI.Interactive
            && CameraDriver.CockpitCamera != null && !TransitionGuard.Active;

        // ---------------------------------------------------------------- every frame (game update)

        private static int sensedFrame = -1;
        private static bool sensed; // Update may take presses
        private static float frameDt;

        /// <summary>Reads the hands and reports distances to GrabArbiter, before the wheel takes any press.</summary>
        public static void Sense()
        {
            try { sensed = ReadFrame(); }
            catch (Exception e)
            {
                sensed = false;
                Stop();
                if (errors++ < 3) Plugin.L.LogError("Lever: " + e);
            }
            sensedFrame = Time.frameCount;
        }

        /// <summary>Before the game reads input, after the wheel. Errors only cost the lever.</summary>
        public static void Update()
        {
            try
            {
                if (sensedFrame != Time.frameCount) sensed = ReadFrame();
                if (sensed) Tick();
            }
            catch (Exception e)
            {
                Stop();
                if (errors++ < 3) Plugin.L.LogError("Lever: " + e);
            }
        }

        /// <summary>False when the lever isn't in use (it's let go then).</summary>
        private static bool ReadFrame()
        {
            float dt = frameDt = Mathf.Clamp(Time.unscaledDeltaTime, 1e-4f, 0.1f);
            if (!(simLeft.On ? simLeft.Down : SteamVRInput.StrafeLeft.Down)) GrabArbiter.Let(GrabArbiter.Lever, true);
            if (!(simRight.On ? simRight.Down : SteamVRInput.StrafeRight.Down)) GrabArbiter.Let(GrabArbiter.Lever, false);
            var lever = Lever(out var vi);
            if (lever == null)
            {
                Stop();
                return false;
            }
            var data = vi.ascensionLeverData;
            gameAngle = data.currentAngle;
            travel = LeverModel.Travel(data.maxAngle);
            detent = travel * DetentShare;
            climbSign = (data.invert ? -1f : 1f) * settingSign; // the game's rule: angle = (invert ? -1 : 1) x input x span
            if (!InUse())
            {
                // out of the cockpit, menu or load: let go and spring back
                GrabArbiter.Clear(GrabArbiter.Lever);
                simLeft.On = simRight.On = false;
                Fed = 0f;
                // measure out of sight under the boarding or load black
                if (VRConfig.GrabLever.Value && TransitionGuard.Settling && CameraDriver.Mode == ViewMode.Cockpit && CameraDriver.CockpitCamera != null
                    && !LeverModel.Measured(lever) && SetupSlot.Take())
                    LeverModel.Prepare(lever);
                if (!LeverModel.Usable) { Stop(); return false; }
                if (holder != null) Release(holder, false);
                if (returning) Return(dt);
                Blend(dt);
                return false;
            }
            if (!LeverModel.Measured(lever) && !SetupSlot.Take()) return false; // one heavy setup a frame
            if (!LeverModel.Prepare(lever))
            {
                Stop();
                return false;
            }
            if (!loggedData)
            {
                loggedData = true;
                Plugin.L.LogInfo($"Lever: the game's lever turns {data.maxAngle:0} degrees either way (extraAngleThreshold {data.extraAngleThreshold:0}, lerpSpeed {data.lerpSpeed:0.00}, invert {data.invert}): " +
                                 $"climbing tilts it {LeverModel.Way(lever, climbSign)}; in the hand it is neutral within {detent:0.0} degrees of the middle.");
            }
            live = LeverModel.TrackingFrame(lever, leverParent);
            if (holder == null && !returning) visualAngle = gameAngle; // free: the game's pose
            if (holder != null || Simulated) SampleHeight(Global.Instance.playerCar, dt);
            else lastHeight = float.NaN;
            UpdateSimulation(dt);
            Read(left, dt);
            Read(right, dt);
            foreach (var h in hands)
            {
                float d = h.Valid && h != holder ? LeverModel.Distance(holder != null ? frozen : live, holder != null ? handAngle : visualAngle, h.Pos) : float.PositiveInfinity;
                GrabArbiter.Report(GrabArbiter.Lever, h.Left, d <= GrabReach ? d : float.PositiveInfinity);
            }
            return true;
        }

        private static void Tick()
        {
            float dt = frameDt;
            // only the button lets go. A press stays live for GrabWindow.
            foreach (var h in hands)
            {
                if (h.Pressed) h.PressedAt = Time.unscaledTime;
                if (h == holder && !h.Down) Release(h, false);
                if (h == holder || !h.Down || Time.unscaledTime - h.PressedAt >= GrabWindow || !h.Valid) continue;
                var f = holder != null ? frozen : live;
                if (LeverModel.Distance(f, holder != null ? handAngle : visualAngle, h.Pos) > GrabReach) continue;
                // a claimed press doesn't strafe until released, grabbed or not
                if (!GrabArbiter.Claim(GrabArbiter.Lever, h.Left)) continue;
                if (h.Healthy) TryGrab(h);
            }

            if (holder != null) Hold(dt);
            else if (returning) Return(dt);
            else climb = 0f;
            Blend(dt);
        }

        /// <summary>The interior's lever, or null outside the HOVA. A new lever resets the learned sign.</summary>
        private static Transform Lever(out VehicleInterior vi)
        {
            var g = Global.Instance;
            vi = g != null ? g.vehicleInterior : null;
            if (vi == null) return null;
            if (vi.Pointer != interiorPtr || cachedLever == null)
            {
                interiorPtr = vi.Pointer;
                cachedLever = vi.ascensionLever;
                leverParent = cachedLever != null ? cachedLever.parent : null;
                IntPtr ptr = cachedLever != null ? cachedLever.Pointer : IntPtr.Zero;
                if (ptr != leverPtr)
                {
                    leverPtr = ptr;
                    Stop();
                    settingSign = 1f;
                    signFromGame = loggedData = false;
                    voteFor = 0f;
                }
            }
            return cachedLever;
        }

        private static void Blend(float dt)
        {
            foreach (var h in hands)
                h.Blend = Mathf.MoveTowards(h.Blend, h == holder ? 1f : 0f, dt / (h == holder ? GrabBlend : h.ReleaseTime));
        }

        /// <summary>Lets go at once and leaves the lever to the game.</summary>
        private static void Stop()
        {
            foreach (var h in hands)
            {
                h.Trusted = false;
                h.Blend = 0f;
                h.Pose.Weight = 0f;
            }
            holder = null;
            simLeft.On = simRight.On = false;
            returning = false;
            climb = 0f;
            GrabArbiter.Clear(GrabArbiter.Lever);
        }

        private static void Read(Hand h, float dt)
        {
            var sim = Sim(h);
            if (sim.On) ReadSimulated(h, sim, holder != null ? frozen : live);
            else if (WheelGrab.SimulatedHand(h.Left, out var grip, out var rotation, out _, out bool down)) h.ReadOther(grip, rotation, down);
            else h.ReadController(LeverModel.BarRadius);
            h.UpdateHealth(dt);
        }

        // ---------------------------------------------------------------- grabbing and letting go

        private static void TryGrab(Hand h)
        {
            if (holder != null)
            {
                // handover: no spring back, and the old press is spent so it can't grab straight back
                var old = holder;
                holder = null;
                old.Trusted = false;
                old.PressedAt = -10f;
                old.LeaveHandle();
                Haptics.Pulse(old.Left, 0.012f, 0.2f, 120f);
                if (handoverLogs++ < 10) Plugin.L.LogInfo($"Lever: the {Side(h)} hand takes it over from the {Side(old)} hand.");
            }
            else
            {
                // freeze the frame for the hold, start at the current climb so the HOVA doesn't jump
                frozen = live;
                frozenSpace = CameraDriver.SpaceVersion;
                handAngle = AngleFor(Fed);
                catchUp = visualAngle - handAngle;
                returning = false;
                filter.Reset(handAngle);
                lastNotch = Notch(handAngle);
                atStop = Mathf.Abs(handAngle) >= travel - 0.05f;
                inDetent = Mathf.Abs(handAngle) < detent;
                lostFor = 0f;
                verticalSpeed = 0f;
                lastHeight = float.NaN;
                var car = Global.Instance != null ? Global.Instance.playerCar : null;
                heightAtGrab = car != null ? car.transform.position.y : 0f;
                riseMax = riseMin = 0f;
            }
            holder = h;
            h.Trusted = true;
            h.OffFor = 0f;
            Anchor(h);
            h.Off = LeverModel.Distance(frozen, handAngle, h.Pos);
            // palm turn and thumb side, kept for the whole grip
            LeverModel.Handle(frozen, handAngle, out _, out var bar, out var across1, out var across2);
            Vector3 across = h.Forward - bar * Vector3.Dot(h.Forward, bar);
            h.Palm = across.sqrMagnitude > 1e-4f ? Mathf.Atan2(Vector3.Dot(across, across2), Vector3.Dot(across, across1)) * Mathf.Rad2Deg : 90f;
            h.ThumbSign = Vector3.Dot(h.ToIndex, bar) >= 0f ? 1f : -1f;
            Haptics.Pulse(h.Left, 0.022f, 0.45f, 170f);
            if (!loggedGrab)
            {
                loggedGrab = true;
                Plugin.L.LogInfo($"Lever grabbed: the HOVA climbs and dives with your hand ({LeverModel.Describe()}; travel {travel:0} degrees either way, neutral within {detent:0.0}).");
            }
        }

        /// <summary>Maps the hand's angle to the lever's current one, so it never jumps.</summary>
        private static void Anchor(Hand h)
        {
            psi = psiRef = LeverModel.Around(frozen, h.Pos, out _, out _);
            angleRef = handAngle;
            h.OnBar = LeverModel.Along(frozen, handAngle, h.Pos);
            if (pushed) // the simulated push is judged from here on
            {
                pushFromSim = simAngle;
                pushFromLever = handAngle;
            }
        }

        private static void Release(Hand h, bool slipped, string why = "left the handle")
        {
            if (h != holder) return;
            holder = null;
            h.Trusted = false;
            h.OffFor = 0f;
            h.LeaveHandle();
            if (slipped)
            {
                Haptics.Pulse(h.Left, 0.02f, 0.35f, 90f);
                Haptics.Pulse(h.Left, 0.02f, 0.35f, 90f, 0.07f);
                Plugin.L.LogInfo($"Lever: the {Side(h)} hand {why} and let go.");
            }
            else Haptics.Pulse(h.Left, 0.015f, 0.25f, 120f);
            // spring back to the game's lever, climb eases back to the stick
            returning = true;
            returnT = returnEase = 0f;
            returnFromAngle = visualAngle;
            returnFromClimb = climb;
            climb = 0f;
        }

        private static void Return(float dt)
        {
            returnT = Mathf.Min(1f, returnT + dt / ReturnTime);
            returnEase = 1f - (1f - returnT) * (1f - returnT);
            visualAngle = Mathf.Lerp(returnFromAngle, gameAngle, returnEase);
            if (returnT >= 1f) returning = false;
        }

        // ---------------------------------------------------------------- holding

        private static void Hold(float dt)
        {
            var h = holder;
            // recenter or camera cut: retake the frame
            if (CameraDriver.SpaceVersion != frozenSpace || CameraDriver.CutThisFrame)
            {
                frozen = live;
                frozenSpace = CameraDriver.SpaceVersion;
                if (h.Valid)
                {
                    if (LeverModel.Distance(frozen, handAngle, h.Pos) > HoldReach)
                    {
                        Release(h, true);
                        return;
                    }
                    Anchor(h);
                    Note(h, "still holds the handle after the cockpit moved (recenter or cut); the lever goes on from there");
                }
            }
            Track(h, dt);
            if (holder == null) return; // it slipped off: Release started the spring back
            Move(dt);
            catchUp *= Mathf.Exp(-dt / CatchUpTime);
            visualAngle = filter.Filter(handAngle, dt) + catchUp;
            climb = ClimbOf(handAngle);
            Feel();
        }

        /// <summary>Hand angle about the hinge, trust, and slipping off.</summary>
        private static void Track(Hand h, float dt)
        {
            bool wasTrusted = h.Trusted;
            if (!h.Valid)
            {
                h.Trusted = false;
                if (wasTrusted) Note(h, "lost its tracking; the lever holds, then goes back to the middle");
                return;
            }
            psi = LeverModel.Around(frozen, h.Pos, out float rho, out _);
            h.Off = LeverModel.Distance(frozen, handAngle, h.Pos);
            if (h.Healthy)
            {
                // clean tracking only: too far, or well off and in reach of something else
                bool elsewhere = h.Off > ElsewhereOff && GrabArbiter.NearOther(GrabArbiter.Lever, h.Left);
                h.OffFor = h.Off > HoldReach || elsewhere ? h.OffFor + dt : 0f;
                if (h.OffFor > SlipTime)
                {
                    Release(h, true, elsewhere && h.Off <= HoldReach ? "reached for something else" : "left the handle");
                    return;
                }
            }
            // at the hinge the angle means nothing (1 cm of hysteresis against flicker)
            h.Trusted = h.Healthy && rho >= MinPivotDistance + (wasTrusted ? 0f : PivotMargin);
            if (h.Trusted && wasTrusted)
            {
                // drawn grip trails the hand along the bar by up to 1.5 cm
                float trail = LeverModel.Along(frozen, handAngle, h.Pos) - h.OnBar;
                if (Mathf.Abs(trail) > BarSlack) h.OnBar += trail - Mathf.Sign(trail) * BarSlack;
            }
            if (h.Trusted == wasTrusted) return;
            if (h.Trusted)
            {
                // back from a tracking loss: re-anchor so the lever doesn't jump
                Anchor(h);
                Note(h, "moves the lever again, from where it is");
            }
            else Note(h, h.Healthy ? "is at the hinge, where its angle means nothing; the lever holds" : "has unsure tracking (frozen or jumping); the lever holds");
        }

        private static void Note(Hand h, string what)
        {
            if (trustLogs++ < 40) Plugin.L.LogInfo($"Lever: the {Side(h)} hand {what}.");
        }

        /// <summary>Clamped at the stops. Pushing past one slides the reference, so pulling back responds at once.</summary>
        private static void Move(float dt)
        {
            float target = handAngle;
            if (holder.Trusted)
            {
                lostFor = 0f;
                float raw = angleRef + Mathf.DeltaAngle(psiRef, psi);
                float stopped = Mathf.Clamp(raw, -travel, travel);
                float past = raw - stopped;
                if (Mathf.Abs(past) > StopPlay) angleRef -= past - Mathf.Sign(past) * StopPlay;
                target = stopped;
            }
            else
            {
                lostFor += dt;
                if (lostFor > LostHoldTime) target = Mathf.MoveTowards(handAngle, 0f, LostCentreRate * dt);
            }
            handAngle = Mathf.MoveTowards(handAngle, target, MaxRate * dt);
        }

        /// <summary>Zero in the neutral middle, full at the stop.</summary>
        private static float ClimbOf(float angle)
        {
            float a = Mathf.Abs(angle);
            return a <= detent ? 0f : Mathf.Sign(angle) * climbSign * Mathf.Clamp01((a - detent) / (travel - detent));
        }

        /// <summary>Inverse of ClimbOf. A faint input is the middle.</summary>
        private static float AngleFor(float input) =>
            Mathf.Abs(input) < 0.05f ? 0f : Mathf.Sign(input) * climbSign * (detent + Mathf.Min(Mathf.Abs(input), 1f) * (travel - detent));

        /// <summary>Bump at the stops, click in and out of neutral, faint ticks every 5 degrees.</summary>
        private static void Feel()
        {
            float a = Mathf.Abs(handAngle);
            if (a >= travel - 0.05f)
            {
                if (!atStop) Pulse(0.045f, 0.6f, 55f);
                atStop = true;
            }
            else if (a < travel - 2f) atStop = false;
            // 0.5 deg hysteresis so a resting hand doesn't buzz
            bool inside = inDetent ? a < detent + 0.5f : a < detent - 0.5f;
            if (inside != inDetent)
            {
                inDetent = inside;
                lastTick = Time.unscaledTime;
                if (inside) Pulse(0.016f, 0.32f, 160f);
                else Pulse(0.012f, 0.16f, 200f);
            }
            // 1 deg of play either side of each notch
            if (handAngle >= lastNotch * NotchStep - 1f && handAngle < (lastNotch + 1) * NotchStep + 1f) return;
            lastNotch = Notch(handAngle);
            if (atStop || inDetent || Time.unscaledTime - lastTick < 0.045f) return;
            lastTick = Time.unscaledTime;
            Pulse(0.01f, 0.06f, 230f);
        }

        private static int Notch(float angle) => Mathf.FloorToInt(angle / NotchStep);

        private static void Pulse(float seconds, float amplitude, float frequency)
        {
            if (holder != null) Haptics.Pulse(holder.Left, seconds, amplitude, frequency);
        }

        private static void SampleHeight(PlayerCar car, float dt)
        {
            if (car == null) return;
            float y = car.transform.position.y;
            if (!float.IsNaN(lastHeight))
            {
                float v = (y - lastHeight) / dt;
                if (Mathf.Abs(v) < 100f) verticalSpeed += (v - verticalSpeed) * (1f - Mathf.Exp(-dt / 0.25f)); // a teleport is not a climb
            }
            lastHeight = y;
            if (holder == null) return;
            riseMax = Mathf.Max(riseMax, y - heightAtGrab);
            riseMin = Mathf.Min(riseMin, y - heightAtGrab);
        }

        private static string Side(Hand h) => h.Left ? "left" : "right";

        // ---------------------------------------------------------------- before rendering

        /// <summary>After the game's update: pose the lever and place the grip.</summary>
        public static void OnBeforeRender()
        {
            try { Render(); }
            catch (Exception e)
            {
                Stop();
                if (errors++ < 3) Plugin.L.LogError("Lever: " + e);
            }
        }

        private static void Render()
        {
            var lever = Lever(out var vi);
            if (lever == null || !LeverModel.Ready)
            {
                left.Pose.Weight = right.Pose.Weight = 0f;
                return;
            }
            if (InUse())
            {
                var data = vi.ascensionLeverData;
                LeverModel.CheckGamePose(lever, data.currentAngle, Time.unscaledDeltaTime);
                if (holder == null && !returning) LearnSign(lever, data); // never while held, the climb would flip
                else voteFor = 0f;
            }
            if (holder != null || returning) LeverModel.Pose(lever, visualAngle);
            bool drawn = CameraDriver.Mode == ViewMode.Cockpit && CameraDriver.CockpitCamera != null;
            PlaceGrip(left, lever, drawn);
            PlaceGrip(right, lever, drawn);
        }

        /// <summary>Climb sign from the game's lever while the stick climbs. A game setting can flip the data's invert.</summary>
        private static void LearnSign(Transform lever, VehicleInterior.SteeringWheelData data)
        {
            // with no input the target stays within extraAngleThreshold
            if (Mathf.Abs(Fed) < LearnInput || Mathf.Abs(data.targetAngle) <= data.extraAngleThreshold + 1f)
            {
                voteFor = 0f;
                return;
            }
            float vote = Mathf.Sign(data.targetAngle * Fed);
            if (vote != voteSign)
            {
                voteSign = vote;
                voteFor = 0f;
            }
            voteFor += Time.unscaledDeltaTime;
            float dataSign = data.invert ? -1f : 1f;
            if (voteFor < LearnTime || (signFromGame && vote == dataSign * settingSign)) return;
            bool firstSeen = !signFromGame;
            settingSign = vote * dataSign;
            climbSign = vote;
            signFromGame = true;
            if (settingSign < 0f)
                Plugin.L.LogWarning($"Lever: the game's lever leans the other way for a climb than its data says (invert {data.invert}; one of its settings turns it round): " +
                                    $"the lever in the hand follows the game's, climbing tilts it {LeverModel.Way(lever, climbSign)}.");
            else
                Plugin.L.LogInfo($"Lever: the game's own lever leans {(firstSeen ? "as its data says" : "as its data says again")} for a climb " +
                                 $"(target {data.targetAngle:0.0} for input {Signed(Fed, 2)}): climbing tilts it {LeverModel.Way(lever, climbSign)}.");
        }

        private static void PlaceGrip(Hand h, Transform lever, bool drawn)
        {
            if (h.Blend <= 0f || !drawn)
            {
                h.Pose.Weight = 0f;
                return;
            }
            LeverModel.GripInSet(lever, h.OnBar, h.Palm, h.ThumbSign, out var point, out var forward, out var thumb, out var across);
            CameraDriver.CockpitToWorld(point, Quaternion.identity, out var p, out var toWorld);
            h.Pose = new GripPose
            {
                Weight = h.Blend,
                Point = p,
                Forward = toWorld * forward,
                ThumbSide = toWorld * thumb,
                Outward = toWorld * across,
                TubeRadius = LeverModel.BarRadius,
                RimRadius = StraightBar,
            };
        }

        // ---------------------------------------------------------------- state and dev tools

        /// <summary>State for the log. With a simulated hand it also checks lever, game lever and climb.</summary>
        public static string Describe()
        {
            var g = Global.Instance;
            var vi = g != null ? g.vehicleInterior : null;
            var lever = vi != null ? vi.ascensionLever : null;
            var car = g != null ? g.playerCar : null;
            if (lever == null) return "no lever (not in the HOVA)";
            var d = vi.ascensionLeverData;
            float span = (d.maxAngle + d.extraAngleThreshold) * (d.invert ? -1f : 1f);
            string reads = Mathf.Abs(d.targetAngle) > d.extraAngleThreshold + 1f && Mathf.Abs(span) > 1f ? $", reads input {Signed(d.targetAngle / span, 2)}" : "";
            string state = holder != null ? $"held by the {Side(holder)} hand{HandState(holder)}" : returning ? "springing back" : "free";
            string hova = "no HOVA";
            float sinceGrab = 0f;
            if (car != null)
            {
                float y = car.transform.position.y;
                sinceGrab = y - heightAtGrab;
                hova = $"HOVA height {y:0.00} m" + (holder != null ? $" ({Signed(y - heightAtGrab, 2)} m since the grab), {Signed(verticalSpeed, 1)} m/s" : "");
            }
            string sim = Simulated ? $", simulated hand at {simAngle:0.0}" : "";
            return $"{state}: lever {visualAngle:0.0} deg (hand {handAngle:0.0}{sim}, stops at {travel:0}, neutral within {detent:0.0}), climb input {Signed(Fed, 2)}; " +
                   $"climbing tilts it {LeverModel.Way(lever, climbSign)} ({(signFromGame ? "seen on the game's lever" : "from the game's data")}); " +
                   $"game lever {d.currentAngle:0.0} deg (target {d.targetAngle:0.0}{reads}, max {d.maxAngle:0}, invert {d.invert}); {hova}; presses: left {GrabArbiter.Describe(true)}, right {GrabArbiter.Describe(false)}; {LeverModel.Describe()}{Checks(sinceGrab)}";
        }

        private static string HandState(Hand h)
        {
            string trust = h.Trusted ? "" : !h.Valid ? " (tracking lost)" : !h.Healthy ? " (tracking unsure)" : " (at the hinge)";
            string gap = "";
            if (h.Valid && h.Pose.Weight > 0f)
            {
                CameraDriver.TrackingToWorld(h.Pos, Quaternion.identity, out var hand, out _);
                gap = $", drawn {(h.Pose.Point - hand).magnitude * 100f:0.0} cm from the hand";
            }
            string off = h.Valid ? $", {h.Off * 100f:0} cm off the handle" : "";
            return $"{trust}{gap}{off}, {Signed(h.OnBar * 100f, 1)} cm along the handle from its middle";
        }

        private static string Checks(float sinceGrab)
        {
            if (!Simulated || holder == null) return "";
            // since the last push the lever moved the hand's way, unless already at that stop
            string push = "";
            float wanted = simAngle - pushFromSim, moved = handAngle - pushFromLever;
            if (pushed && holder.Trusted && Mathf.Abs(wanted) > 3f)
            {
                bool alreadyThere = Mathf.Abs(pushFromLever) >= travel - 0.1f && Mathf.Sign(pushFromLever) == Mathf.Sign(wanted);
                push = alreadyThere || (Mathf.Abs(moved) > 1f && Mathf.Sign(moved) == Mathf.Sign(wanted)) ? " push ok"
                    : $" PUSH FAILED (the hand went {Signed(wanted, 0)} deg, the lever {Signed(moved, 0)})";
            }
            // the game's own lever should lean the same way
            string game = Mathf.Abs(Fed) < 0.3f || Mathf.Abs(gameAngle) < 3f ? "" : Mathf.Sign(gameAngle) == Mathf.Sign(handAngle) ? " game lever ok"
                : $" GAME LEVER FAILED (the hand holds it at {handAngle:0} deg, the game leans its own to {gameAngle:0})";
            // at the city's floor or ceiling the HOVA bounces back a bit: counts as held back
            float farthest = Fed > 0f ? riseMax : -riseMin, cameBack = Fed > 0f ? riseMax - sinceGrab : sinceGrab - riseMin;
            bool heldBack = farthest >= 1f && cameBack <= 3f;
            string hova = Mathf.Abs(Fed) < 0.5f ? "" : Mathf.Abs(verticalSpeed) < 0.3f ? " climb: the HOVA is not moving (not yet, or held back)"
                : Mathf.Sign(verticalSpeed) == Mathf.Sign(Fed) ? (Fed > 0f ? " climb ok (climbing)" : " climb ok (diving)")
                : heldBack ? $" climb ok ({(Fed > 0f ? "climbed" : "dived")} {farthest:0.0} m to a ceiling or floor, bouncing there: {cameBack:0.0} m back, {Signed(verticalSpeed, 1)} m/s)"
                : $" CLIMB FAILED (input {Signed(Fed, 2)}, the HOVA goes {Signed(verticalSpeed, 1)} m/s)";
            string result = $"; check:{push}{game}{hova}";
            if (result.Contains("FAILED")) Plugin.L.LogWarning("Lever" + result);
            return result;
        }

        /// <summary>Signed number, +0 for zero (a format's negative section would print "-+0.0").</summary>
        private static string Signed(float value, int decimals)
        {
            double r = Math.Round(value, decimals);
            return (r < 0.0 ? "-" : "+") + Math.Abs(r).ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        // dev command "lever": a simulated hand, moved as the head sees the lever
        private sealed class SimHand
        {
            public bool On, Down, PressNext, Frozen;
            public Vector3 Offset, OffsetTarget, FrozenPos; // tracking space, moved off the handle
            public float JumpUntil;
            public float Along;                             // m along the handle from its middle
        }

        private static readonly SimHand simLeft = new SimHand(), simRight = new SimHand();
        private static readonly SimHand[] sims = { simLeft, simRight };
        private static float simAngle, simTarget; // sim hand's lever angle, deg
        private static bool pushed;
        private static float pushFromSim, pushFromLever; // the hand's and the lever's angles at the last push
        private const float SimRate = 60f, SimDriftSpeed = 1f;

        public static bool Simulated => simLeft.On || simRight.On;
        private static SimHand Sim(Hand h) => h.Left ? simLeft : simRight;

        /// <summary>A simulated hand's grip, rotation and bar radius, tracking space.</summary>
        public static bool SimulatedHand(bool isLeft, out Vector3 grip, out Quaternion rotation, out float barRadius) =>
            SimulatedHand(isLeft, out grip, out rotation, out barRadius, out _);

        /// <summary>Same, plus the button (the wheel reads it as its own hand).</summary>
        public static bool SimulatedHand(bool isLeft, out Vector3 grip, out Quaternion rotation, out float barRadius, out bool down)
        {
            var h = isLeft ? left : right;
            var sim = isLeft ? simLeft : simRight;
            grip = h.Pos;
            rotation = h.Valid ? Quaternion.LookRotation(h.Forward, h.ToIndex) : Quaternion.identity;
            barRadius = LeverModel.BarRadius;
            down = sim.Down;
            return sim.On && h.Valid;
        }

        /// <summary>Handle middle now, tracking space (for the wheel's reach dev command).</summary>
        public static bool HandleMiddle(out Vector3 middle)
        {
            middle = Vector3.zero;
            if (!LeverModel.Ready || live.Axis.sqrMagnitude < 0.5f) return false; // no lever seen in the cockpit yet
            LeverModel.Handle(holder != null ? frozen : live, holder != null ? handAngle : visualAngle, out middle, out _, out _, out _);
            return true;
        }

#if DEV
        /// <summary>
        /// lever grab [left|right] [cm], push &lt;x&gt; (share of travel, + toward the windscreen), release [left|right],
        /// faults jump|freeze|slip|away|reach, thaw, stop.
        /// </summary>
        public static void Simulate(string[] a)
        {
            string action = a.Length > 1 ? a[1].ToLowerInvariant() : "";
            string which = a.Length > 2 ? a[2].ToLowerInvariant() : holder != null ? Side(holder) : "right";
            var sim = which == "left" ? simLeft : simRight;
            switch (action)
            {
                case "grab":
                    if (holder == null) simAngle = simTarget = visualAngle; // the hand closes where the lever is now
                    pushed = false;
                    sim.On = sim.Down = sim.PressNext = true;
                    sim.Along = a.Length > 3 ? float.Parse(a[3], CultureInfo.InvariantCulture) * 0.01f : 0f;
                    sim.Offset = sim.OffsetTarget = Vector3.zero;
                    sim.Frozen = false;
                    sim.JumpUntil = 0f;
                    break;
                case "push":
                    if (a.Length < 3) break;
                    simTarget = Mathf.Clamp(float.Parse(a[2], CultureInfo.InvariantCulture), -2f, 2f) * travel * WindscreenSign();
                    pushed = true;
                    pushFromSim = simAngle;
                    pushFromLever = handAngle;
                    break;
                case "release": sim.Down = false; break;
                case "slip": sim.OffsetTarget = AlongHinge(live) * 0.2f; break;
                case "away": sim.OffsetTarget = TowardHead(live) * 0.5f; break;
                case "reach":
                    LeverModel.Handle(holder != null ? frozen : live, simAngle, out var from, out var barDir, out _, out _);
                    from += barDir * sim.Along;
                    if (WheelGrab.NearestRimPoint(from, out var rim)) sim.OffsetTarget = rim - from;
                    break;
                case "jump": sim.JumpUntil = Time.unscaledTime + 0.12f; break;
                case "freeze": sim.Frozen = true; break;
                case "thaw":
                    sim.Frozen = false;
                    sim.OffsetTarget = Vector3.zero;
                    sim.JumpUntil = 0f;
                    break;
                case "stop":
                    simLeft.On = simLeft.Down = simRight.On = simRight.Down = false;
                    break;
            }
        }
#endif

        /// <summary>+1 if a positive angle tilts the handle toward the windscreen.</summary>
        private static float WindscreenSign()
        {
            LeverModel.Handle(live, 0f, out var middle, out _, out _, out _);
            Vector3 tan = Vector3.Cross(live.Axis, live.Up);
            return Vector3.Dot(tan, Vector3.ProjectOnPlane(middle - VRSession.HeadPosition, Vector3.up)) >= 0f ? 1f : -1f;
        }

        /// <summary>Along the hinge, away from the head (the angle doesn't change).</summary>
        private static Vector3 AlongHinge(LeverModel.Frame f) =>
            f.Axis * (Vector3.Dot(f.Axis, VRSession.HeadPosition - f.Pivot) > 0f ? -1f : 1f);

        private static Vector3 TowardHead(LeverModel.Frame f)
        {
            Vector3 d = VRSession.HeadPosition - f.Pivot;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.up;
        }

        private static void UpdateSimulation(float dt)
        {
            simAngle = Mathf.MoveTowards(simAngle, simTarget, SimRate * dt);
            foreach (var sim in sims)
            {
                Vector3 drift = sim.OffsetTarget - sim.Offset;
                float step = SimDriftSpeed * dt;
                sim.Offset = drift.magnitude <= step ? sim.OffsetTarget : sim.Offset + drift.normalized * step;
            }
        }

        private static void ReadSimulated(Hand h, SimHand sim, LeverModel.Frame f)
        {
            h.Down = sim.Down;
            h.Pressed = sim.PressNext;
            sim.PressNext = false;
            // from above, palm down, faint tremor (an exact repeat reads as frozen)
            LeverModel.Handle(f, simAngle, out var middle, out var bar, out _, out _);
            Vector3 ahead = Vector3.ProjectOnPlane(middle - VRSession.HeadPosition, Vector3.up);
            ahead = ahead.sqrMagnitude > 1e-6f ? ahead.normalized : Vector3.forward;
            float t = Time.unscaledTime * (h.Left ? 1f : 1.3f);
            Vector3 tremor = new Vector3(Mathf.Sin(t * 7.3f), Mathf.Sin(t * 9.1f + 1f), Mathf.Sin(t * 5.7f + 2f)) * 0.0002f;
            Vector3 pos = middle + bar * sim.Along + sim.Offset + tremor;
            if (sim.Frozen) pos = sim.FrozenPos;
            else sim.FrozenPos = pos;
            if (Time.unscaledTime < sim.JumpUntil) pos += TowardHead(f) * 0.35f;
            h.Valid = true;
            h.Pos = pos;
            h.Forward = (ahead * 0.9f - Vector3.up * 0.42f).normalized;
            Vector3 thumbSide = Vector3.Cross(h.Forward, Vector3.up).normalized; // left of the fingers, palm down
            h.ToIndex = h.Left ? -thumbSide : thumbSide;
        }
    }
}
