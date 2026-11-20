using UnityEngine;
using UnityEngine.XR;

namespace CloudpunkVR.VR
{
    /// <summary>
    /// How the view moved this frame, for the cut blink and the transition guard. Anchor vs its carrier (HOVA, Rania)
    /// is a camera change. Carrier beyond its velocity is a teleport.
    /// </summary>
    internal static class ViewMotion
    {
        /// <summary>Same view and carrier as last frame, so the steps mean something.</summary>
        public static bool Continuous { get; private set; }

        /// <summary>Anchor move vs its carrier (m, deg).</summary>
        public static float RelativeStep { get; private set; }
        public static float RelativeTurn { get; private set; }

        /// <summary>Carrier move beyond its velocity (m, deg).</summary>
        public static float CarrierError { get; private set; }
        public static float CarrierTurn { get; private set; }

        /// <summary>Carrier step predicted by its velocity (m, deg).</summary>
        public static float CarrierExpected { get; private set; }
        public static float CarrierExpectedTurn { get; private set; }

        /// <summary>Total carrier turn (deg).</summary>
        public static float CarrierYawStep { get; private set; }

        /// <summary>Menu camera step beyond its recent speed (m). It glides over a metre a frame at times.</summary>
        public static float MenuError { get; private set; }

        /// <summary>World moves (m), anchor turn (deg).</summary>
        public static float AnchorStep { get; private set; }
        public static float AnchorTurn { get; private set; }
        public static float HeadStep { get; private set; }

        /// <summary>Anchor to carrier, m. In the cockpit the eyes sit 1-2 m from the HOVA's centre.</summary>
        public static float CarrierDistance { get; private set; }
        public static Vector3 CarrierPosition { get; private set; }
        public static string CarrierName { get; private set; } = "nothing";
        public static int CarrierId { get; private set; }

        /// <summary>Real (unscaled) frame time, s.</summary>
        public static float Dt { get; private set; }

        /// <summary>1, or how many times longer than 2 headset frames. Turn limits scale with it.</summary>
        public static float LongFrame { get; private set; } = 1f;

        private static bool have, loggedCarInterpolation, loggedRaniaInterpolation;
        private static ViewMode lastMode;
        private static int lastCarrierId;
        private static Vector3 lastRel, lastCarrierPos, lastAnchor, lastHead, smoothedVelocity, menuVelocity;
        private static float lastRelYaw, lastCarrierYaw, lastAnchorYaw;

        /// <summary>Past frame, to tell what moved before a transition was noticed.</summary>
        private struct Sample
        {
            public float Time, RelativeStep, RelativeTurn, CarrierError, CarrierDistance;
            public bool Continuous;
        }

        private static readonly Sample[] history = new Sample[256];
        private static int historyNext, historyCount;

        /// <summary>From the camera driver once the anchor and head are placed.</summary>
        public static void Measure(ViewMode mode)
        {
            var g = Global.Instance;
            bool vehicle = mode == ViewMode.Cockpit || mode == ViewMode.Chase;
            Transform carrier = null;
            Rigidbody body = null;
            if (g != null)
            {
                if (vehicle && g.playerCar != null) { carrier = g.playerCar.transform; body = g.playerCar.rBody; }
                else if (mode == ViewMode.OnFoot && g.player != null) { carrier = g.player.transform; body = g.player.rBody; }
            }
            CarrierName = carrier == null ? "nothing" : vehicle ? "HOVA" : "Rania";
            int carrierId = carrier != null ? carrier.GetInstanceID() : 0;
            CarrierId = carrierId;
            LogInterpolationOnce(vehicle, body);

            Vector3 anchor = CameraDriver.AnchorPosition;
            float anchorYaw = CameraDriver.Yaw(CameraDriver.AnchorRotation);
            Vector3 carrierPos = carrier != null ? carrier.position : Vector3.zero;
            Quaternion carrierRot = carrier != null ? carrier.rotation : Quaternion.identity;
            float carrierYaw = CameraDriver.Yaw(carrierRot);
            // HOVA: anchor in the car's frame so its turns aren't view moves. On foot only position counts.
            Vector3 rel = carrier == null ? anchor : vehicle ? Quaternion.Inverse(carrierRot) * (anchor - carrierPos) : anchor - carrierPos;
            float relYaw = carrier == null ? anchorYaw : vehicle ? Mathf.DeltaAngle(carrierYaw, anchorYaw) : 0f;

            Dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            float headsetFrame = 1f / Mathf.Max(XRDevice.refreshRate, 60f);
            LongFrame = Mathf.Max(1f, Dt / (2f * headsetFrame));
            Continuous = have && mode == lastMode && carrierId == lastCarrierId;
            if (Continuous)
            {
                RelativeStep = (rel - lastRel).magnitude;
                RelativeTurn = Mathf.Abs(Mathf.DeltaAngle(lastRelYaw, relYaw));
                // carrier moves in game time (paused, slowed, capped), not real time
                float gameDt = Time.deltaTime;
                Vector3 carrierStep = carrierPos - lastCarrierPos;
                float carrierYawStep = Mathf.DeltaAngle(lastCarrierYaw, carrierYaw);
                // physics HOVA: rigidbody velocity. Kinematic (passages) or Rania: recent speed.
                bool physics = vehicle && body != null && !body.isKinematic;
                Vector3 velocity = physics ? body.velocity : smoothedVelocity;
                float yawRate = physics ? body.angularVelocity.y * Mathf.Rad2Deg : 0f;
                Vector3 expected = velocity * gameDt;
                CarrierExpected = expected.magnitude;
                CarrierExpectedTurn = Mathf.Abs(yawRate * gameDt);
                CarrierError = (carrierStep - expected).magnitude;
                CarrierTurn = Mathf.Abs(Mathf.DeltaAngle(yawRate * gameDt, carrierYawStep));
                CarrierYawStep = Mathf.Abs(carrierYawStep);
                AnchorStep = (anchor - lastAnchor).magnitude;
                AnchorTurn = Mathf.Abs(Mathf.DeltaAngle(lastAnchorYaw, anchorYaw));
                HeadStep = (CameraDriver.HeadWorldPosition - lastHead).magnitude;
                if (mode == ViewMode.Menu)
                {
                    Vector3 step = anchor - lastAnchor;
                    MenuError = (step - menuVelocity * Dt).magnitude;
                    Vector3 v = step / Dt;
                    if (v.sqrMagnitude < 200f * 200f) menuVelocity = Vector3.Lerp(menuVelocity, v, 1f - Mathf.Exp(-Dt / 0.1f));
                }
                // smoothed over 0.1 s (physics steps and frames don't line up), teleports (>200 m/s) excluded
                if (gameDt > 1e-5f)
                {
                    Vector3 v = carrierStep / gameDt;
                    if (v.sqrMagnitude < 200f * 200f) smoothedVelocity = Vector3.Lerp(smoothedVelocity, v, 1f - Mathf.Exp(-gameDt / 0.1f));
                }
            }
            else
            {
                RelativeStep = RelativeTurn = CarrierError = CarrierTurn = CarrierExpected = CarrierExpectedTurn = CarrierYawStep = AnchorStep = AnchorTurn = HeadStep = MenuError = 0f;
                smoothedVelocity = menuVelocity = Vector3.zero;
            }
            CarrierDistance = carrier != null ? (anchor - carrierPos).magnitude : 0f;
            CarrierPosition = carrierPos;

            have = true;
            lastMode = mode;
            lastCarrierId = carrierId;
            lastRel = rel;
            lastRelYaw = relYaw;
            lastCarrierPos = carrierPos;
            lastCarrierYaw = carrierYaw;
            lastAnchor = anchor;
            lastAnchorYaw = anchorYaw;
            lastHead = CameraDriver.HeadWorldPosition;

            history[historyNext] = new Sample
            {
                Time = Time.unscaledTime, RelativeStep = RelativeStep, RelativeTurn = RelativeTurn, CarrierError = CarrierError,
                CarrierDistance = CarrierDistance, Continuous = Continuous,
            };
            historyNext = (historyNext + 1) % history.Length;
            if (historyCount < history.Length) historyCount++;
        }

        /// <summary>Without interpolation, steps are uneven at speed.</summary>
        private static void LogInterpolationOnce(bool vehicle, Rigidbody body)
        {
            if (body == null || (vehicle ? loggedCarInterpolation : loggedRaniaInterpolation)) return;
            if (vehicle) loggedCarInterpolation = true; else loggedRaniaInterpolation = true;
            Plugin.L.LogInfo($"View motion: {(vehicle ? "the HOVA's" : "Rania's")} rigidbody interpolation is {body.interpolation}, " +
                             $"kinematic {body.isKinematic}, physics step {Time.fixedDeltaTime:0.000} s.");
        }

        /// <summary>View jumped without the player (cut, rig change, teleport, respawn). Mode switches aren't judged here.</summary>
        public static bool IsJump(ViewMode mode, bool playerTurned)
        {
            if (!Continuous) return false;
            // long frames let turning and damping go further
            float turnLimit = LongFrame, slideLimit = Mathf.Min(LongFrame, 3f);
            switch (mode)
            {
                case ViewMode.Cockpit:
                case ViewMode.Chase:
                    return RelativeStep > 3f * slideLimit || RelativeTurn > 30f * turnLimit || CarrierTurn > 30f * turnLimit
                           || CarrierError > 4f + 0.5f * CarrierExpected;
                case ViewMode.OnFoot:
                    return RelativeStep > 1.2f * slideLimit || CarrierError > 1.2f + 0.5f * CarrierExpected || (AnchorTurn > 45f * turnLimit && !playerTurned);
                case ViewMode.Menu:
                    return MenuError > 1.2f * slideLimit || (RelativeTurn > 45f * turnLimit && !playerTurned);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Only the head moved the view: anchor still on its carrier (under 1.5 m/s, 60°/s), carrier on its velocity.
        /// Transitions wait for a run of these.
        /// </summary>
        public static bool IsSteady =>
            Continuous && RelativeStep < 0.005f + 1.5f * Dt && RelativeTurn < 0.2f + 60f * Dt
            && CarrierError < 1f + 0.5f * CarrierExpected && CarrierTurn < 10f * LongFrame;

        public static string Describe() =>
            $"view {RelativeStep:0.00} m / {RelativeTurn:0.0}° relative to {CarrierName}, {CarrierName} {CarrierError:0.00} m / {CarrierTurn:0.0}° beyond its velocity " +
            $"(which gave {CarrierExpected:0.00} m / {CarrierExpectedTurn:0.0}°), head {HeadStep:0.00} m, eyes {CarrierDistance:0.0} m from {CarrierName}, " +
            $"frame {Dt * 1000f:0} ms";

        /// <summary>What moved over the last seconds, for the log.</summary>
        public static string DescribeBefore(float seconds)
        {
            float since = Time.unscaledTime - seconds;
            float slid = 0f, largest = 0f, turned = 0f, carrierWorst = 0f, firstDistance = -1f, lastDistance = 0f;
            int frames = 0, breaks = 0;
            for (int i = 0; i < historyCount; i++)
            {
                var s = history[(historyNext - historyCount + i + history.Length) % history.Length];
                if (s.Time < since) continue;
                frames++;
                if (!s.Continuous) { breaks++; continue; }
                slid += s.RelativeStep;
                turned += s.RelativeTurn;
                largest = Mathf.Max(largest, s.RelativeStep);
                carrierWorst = Mathf.Max(carrierWorst, s.CarrierError);
                if (firstDistance < 0f) firstDistance = s.CarrierDistance;
                lastDistance = s.CarrierDistance;
            }
            if (frames == 0) return "no frame recorded";
            return $"over {frames} frames the view slid {slid:0.00} m and turned {turned:0}° relative to {CarrierName} (largest step {largest:0.00} m), " +
                   $"{CarrierName} at most {carrierWorst:0.00} m beyond its velocity, eyes {Mathf.Max(firstDistance, 0f):0.0} -> {lastDistance:0.0} m from it" +
                   (breaks > 0 ? $", {breaks} change(s) of view" : "");
        }
    }
}
