using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace CloudpunkVR.Input
{
    /// <summary>SteamVR Input reader. Default bindings in bindings/*.json, rebindable in SteamVR.</summary>
    internal static class SteamVRInput
    {
        public struct Button
        {
            public bool Down, Pressed, Released;
        }

        public struct HandPose
        {
            public bool Valid;
            public Vector3 Position;    // tracking space, Unity coordinates
            public Quaternion Rotation;
        }

        /// <summary>31 joints: wrist, thumb 2-5, index 6-10, middle 11-15, ring 16-20, pinky 21-25.</summary>
        public sealed class HandSkeleton
        {
            public const int BoneCount = 31;
            public bool Valid;
            public Vector3 Position;      // root, tracking space
            public Quaternion Rotation;
            public readonly Vector3[] Joints = new Vector3[BoneCount]; // relative to the root
        }

        /// <summary>Finger touches sensed through buttons (PS VR2 Sense). Ring and pinky aren't sensed.</summary>
        public struct FingerTouch
        {
            public bool Active;       // the controller reports touches
            public bool Thumb;        // face buttons, stick, Options/Create
            public bool Index;        // trigger
            public bool Middle;       // grip button
            public bool MiddleBound;  // custom bindings may not give it
            public float Squeeze;     // grip pressed, 0-1
            public float IndexNear, MiddleNear; // proximity sensors 0-1, -1 when not bound
        }

        public static FingerTouch LeftTouch, RightTouch;

        public static bool Ready { get; private set; }

        public static Vector2 Move, Look;
        public static float Accelerate, Brake;
        public static Button Interact, Back, FreeLook, Inventory, StrafeLeft, StrafeRight, Menu, Map, SwitchView, ToggleHud, Recenter, ClickLeft, ClickRight;
        public static HandPose LeftHand, RightHand;
        public static readonly HandSkeleton LeftSkeleton = new HandSkeleton(), RightSkeleton = new HandSkeleton();

        private static ulong set, move, look, accelerate, brake, interact, back, freeLook, inventory, strafeL, strafeR, menu, map, switchView, toggleHud, recenter, clickL, clickR, poseL, poseR, skelL, skelR, thumbL, thumbR, indexL, indexR, hapticL, hapticR,
            middleL, middleR, squeezeL, squeezeR, indexNearL, indexNearR, middleNearL, middleNearR;
        private static readonly VRBoneTransform_t[] boneBuffer = new VRBoneTransform_t[HandSkeleton.BoneCount];
        private static VRActiveActionSet_t[] activeSets;
        private static float nextRetry;
        private static int failures;

        private static readonly uint DigitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
        private static readonly uint AnalogSize = (uint)Marshal.SizeOf<InputAnalogActionData_t>();
        private static readonly uint PoseSize = (uint)Marshal.SizeOf<InputPoseActionData_t>();
        private static readonly uint SetSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();

        public static void TryInit()
        {
            if (Ready || Time.unscaledTime < nextRetry) return;
            nextRetry = Time.unscaledTime + 2f;
            var input = OpenVR.Input;
            if (input == null) { Warn("SteamVR input interface not available yet"); return; }

            string manifest = Path.Combine(Plugin.PluginDir, "bindings", "actions.json");
            if (!File.Exists(manifest)) { Warn("Missing " + manifest); return; }
            var err = input.SetActionManifestPath(manifest);
            if (err != EVRInputError.None) { Warn("SetActionManifestPath: " + err); return; }

            set = Handle(input.GetActionSetHandle, "/actions/main");
            move = Act("in/move"); look = Act("in/look");
            accelerate = Act("in/accelerate"); brake = Act("in/brake");
            interact = Act("in/interact"); back = Act("in/back"); freeLook = Act("in/freelook"); inventory = Act("in/inventory");
            strafeL = Act("in/strafeleft"); strafeR = Act("in/straferight");
            menu = Act("in/menu"); map = Act("in/map"); switchView = Act("in/switchview"); toggleHud = Act("in/togglehud");
            recenter = Act("in/recenter"); clickL = Act("in/clickleft"); clickR = Act("in/clickright");
            poseL = Act("in/poseleft"); poseR = Act("in/poseright");
            skelL = Act("in/skeletonleft"); skelR = Act("in/skeletonright");
            thumbL = Act("in/thumbleft"); thumbR = Act("in/thumbright");
            indexL = Act("in/indexleft"); indexR = Act("in/indexright");
            middleL = Act("in/middleleft"); middleR = Act("in/middleright");
            squeezeL = Act("in/squeezeleft"); squeezeR = Act("in/squeezeright");
            indexNearL = Act("in/indexnearleft"); indexNearR = Act("in/indexnearright");
            middleNearL = Act("in/middlenearleft"); middleNearR = Act("in/middlenearright");
            hapticL = Act("out/hapticleft"); hapticR = Act("out/hapticright");

            activeSets = new[] { new VRActiveActionSet_t { ulActionSet = set, ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle, nPriority = 0 } };
            Ready = set != 0 && move != 0;
            Plugin.L.LogMessage(Ready ? "SteamVR Input ready (action manifest loaded)." : "SteamVR Input: action handles missing.");
        }

        private static ulong Act(string name) => Handle(OpenVR.Input.GetActionHandle, "/actions/main/" + name);

        private delegate EVRInputError HandleGetter(string name, ref ulong handle);

        private static ulong Handle(HandleGetter getter, string name)
        {
            ulong h = 0;
            var e = getter(name, ref h);
            if (e != EVRInputError.None) Warn($"{name}: {e}");
            return h;
        }

        private static void Warn(string msg)
        {
            if (failures++ < 20) Plugin.L.LogWarning("SteamVR Input: " + msg);
        }

        // dev command overrides
        private static Vector2 simMove, simLook;
        private static float simUntil;
        private static string simPress;

#if DEV
        public static void Simulate(Vector2 move, Vector2 look, float seconds) { simMove = move; simLook = look; simUntil = Time.unscaledTime + seconds; }
        public static void SimulatePress(string button) => simPress = button?.ToLowerInvariant();
#endif

        private static void ApplySimulation()
        {
            if (Time.unscaledTime < simUntil) { Move += simMove; Look += simLook; }
            if (simPress == null) return;
            var b = new Button { Down = true, Pressed = true };
            switch (simPress)
            {
                case "recenter": Recenter = b; break;
                case "switchview": SwitchView = b; break;
                case "clickright": ClickRight = b; break;
                case "clickleft": ClickLeft = b; break;
                case "menu": Menu = b; break;
                case "map": Map = b; break;
                case "freelook": FreeLook = b; break;
                case "back": Back = b; break;
            }
            simPress = null;
        }

        /// <summary>Call once per frame, before the game reads input.</summary>
        public static void Update()
        {
            if (!Ready) return;
            try { ReadActions(); }
            finally { ApplySimulation(); }
        }

        private static void ReadActions()
        {
            Move = Look = Vector2.zero; // no stale values over a failed read
            // a failed read keeps what's held but not the edges, or a stale press fires every frame
            Interact = Held(Interact); Back = Held(Back); FreeLook = Held(FreeLook); Inventory = Held(Inventory);
            StrafeLeft = Held(StrafeLeft); StrafeRight = Held(StrafeRight);
            Menu = Held(Menu); Map = Held(Map); SwitchView = Held(SwitchView); ToggleHud = Held(ToggleHud);
            Recenter = Held(Recenter); ClickLeft = Held(ClickLeft); ClickRight = Held(ClickRight);
            var input = OpenVR.Input;
            if (input == null) return;
            if (input.UpdateActionState(activeSets, SetSize) != EVRInputError.None) return;

            Move = Vec2(move); Look = Vec2(look);
            Accelerate = Vec1(accelerate); Brake = Vec1(brake);
            Interact = Digital(interact); Back = Digital(back); FreeLook = Digital(freeLook); Inventory = Digital(inventory);
            StrafeLeft = Digital(strafeL); StrafeRight = Digital(strafeR);
            Menu = Digital(menu); Map = Digital(map); SwitchView = Digital(switchView); ToggleHud = Digital(toggleHud);
            Recenter = Digital(recenter); ClickLeft = Digital(clickL); ClickRight = Digital(clickR);
            var universe = VR.VRSession.TrackingUniverse;
            LeftHand = Pose(poseL, universe);
            RightHand = Pose(poseR, universe);
            ReadSkeleton(skelL, universe, LeftSkeleton);
            ReadSkeleton(skelR, universe, RightSkeleton);
            LeftTouch = Touch(thumbL, indexL, middleL, squeezeL, indexNearL, middleNearL);
            RightTouch = Touch(thumbR, indexR, middleR, squeezeR, indexNearR, middleNearR);
        }

        private static Button Held(Button b) => new Button { Down = b.Down };

        private static FingerTouch Touch(ulong thumb, ulong index, ulong middle, ulong squeeze, ulong indexNear, ulong middleNear)
        {
            bool tOk = Touched(thumb, out bool t), iOk = Touched(index, out bool i), mOk = Touched(middle, out bool m);
            float s = Vec1(squeeze);
            return new FingerTouch
            {
                Active = tOk || iOk || mOk,
                Thumb = t, Index = i, Middle = m, MiddleBound = mOk,
                Squeeze = s,
                IndexNear = Near(indexNear), MiddleNear = Near(middleNear),
            };
        }

        /// <summary>False if this binding doesn't provide the touch action.</summary>
        private static bool Touched(ulong action, out bool state)
        {
            var d = new InputDigitalActionData_t();
            bool ok = action != 0 && OpenVR.Input.GetDigitalActionData(action, ref d, DigitalSize, OpenVR.k_ulInvalidInputValueHandle) == EVRInputError.None && d.bActive;
            state = ok && d.bState;
            return ok;
        }

        private static float Near(ulong action)
        {
            var d = new InputAnalogActionData_t();
            if (action == 0 || OpenVR.Input.GetAnalogActionData(action, ref d, AnalogSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None || !d.bActive)
                return -1f;
            return Mathf.Clamp01(d.x);
        }

        /// <summary>SteamVR's finger curls, thumb to pinky, 0 straight to 1 closed.</summary>
        public static bool DriverCurls(bool isLeft, float[] curls)
        {
            if (!Ready || OpenVR.Input == null) return false;
            var d = new VRSkeletalSummaryData_t();
            if (OpenVR.Input.GetSkeletalSummaryData(isLeft ? skelL : skelR, EVRSummaryType.FromDevice, ref d) != EVRInputError.None) return false;
            curls[0] = d.flFingerCurl0; curls[1] = d.flFingerCurl1; curls[2] = d.flFingerCurl2; curls[3] = d.flFingerCurl3; curls[4] = d.flFingerCurl4;
            return true;
        }

        private static void ReadSkeleton(ulong action, ETrackingUniverseOrigin universe, HandSkeleton s)
        {
            s.Valid = false;
            // joints are relative to the skeleton action's own pose
            var root = Pose(action, universe);
            if (!root.Valid) return;
            if (OpenVR.Input.GetSkeletalBoneData(action, EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange.WithController, boneBuffer) != EVRInputError.None)
                return;
            for (int i = 0; i < HandSkeleton.BoneCount; i++)
            {
                var p = boneBuffer[i].position;
                s.Joints[i] = new Vector3(p.v0, p.v1, -p.v2); // OpenVR is right-handed: the same flip as every pose
            }
            s.Position = root.Position;
            s.Rotation = root.Rotation;
            s.Valid = true;
            LogSkeleton(s, action == skelL);
        }

        private static float nextSkeletonLog;
        private static int skeletonLogs;

        /// <summary>Logs a few skeleton samples.</summary>
        private static void LogSkeleton(HandSkeleton s, bool isLeft)
        {
            if (skeletonLogs >= 6 || Time.unscaledTime < nextSkeletonLog) return;
            if (!isLeft) nextSkeletonLog = Time.unscaledTime + 20f;
            skeletonLogs++;
            string J(int i) => $"{s.Joints[i].x:0.000},{s.Joints[i].y:0.000},{s.Joints[i].z:0.000}";
            Plugin.L.LogInfo($"Skeleton {(isLeft ? "left" : "right")} ({ControllerType}): wrist {J(1)} thumb {J(2)}>{J(5)} index {J(7)}>{J(10)} middle {J(12)}>{J(15)} pinky {J(22)}>{J(25)}; touch thumb={(isLeft ? LeftTouch : RightTouch).Thumb} index={(isLeft ? LeftTouch : RightTouch).Index} middle={(isLeft ? LeftTouch : RightTouch).Middle}");
        }

        private static Button Digital(ulong action)
        {
            var d = new InputDigitalActionData_t();
            if (action == 0 || OpenVR.Input.GetDigitalActionData(action, ref d, DigitalSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None || !d.bActive)
                return default;
            return new Button { Down = d.bState, Pressed = d.bState && d.bChanged, Released = !d.bState && d.bChanged };
        }

        private static float Vec1(ulong action)
        {
            var d = new InputAnalogActionData_t();
            if (action == 0 || OpenVR.Input.GetAnalogActionData(action, ref d, AnalogSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None || !d.bActive)
                return 0f;
            return d.x;
        }

        private static Vector2 Vec2(ulong action)
        {
            var d = new InputAnalogActionData_t();
            if (action == 0 || OpenVR.Input.GetAnalogActionData(action, ref d, AnalogSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None || !d.bActive)
                return Vector2.zero;
            return new Vector2(d.x, d.y);
        }

        private static HandPose Pose(ulong action, ETrackingUniverseOrigin universe)
        {
            var d = new InputPoseActionData_t();
            if (action == 0 || OpenVR.Input.GetPoseActionDataForNextFrame(action, universe, ref d, PoseSize, OpenVR.k_ulInvalidInputValueHandle) != EVRInputError.None
                || !d.bActive || !d.pose.bPoseIsValid || !d.pose.bDeviceIsConnected
                || d.pose.eTrackingResult != ETrackingResult.Running_OK)
                return default; // not tracked: better no laser than a wild one
            VR.VRSession.ToUnity(d.pose.mDeviceToAbsoluteTracking, out var p, out var r);
            return new HandPose { Valid = true, Position = p, Rotation = r };
        }

        /// <summary>"playstation_vr2_sense", "oculus_touch", "knuckles"..., right hand first. Empty if none.</summary>
        public static string ControllerType { get; private set; } = "";
        private static float nextTypeCheck;

        public static void RefreshControllerType()
        {
            if (Time.unscaledTime < nextTypeCheck) return;
            nextTypeCheck = Time.unscaledTime + 2f;
            var sys = OpenVR.System;
            if (sys == null) return;
            string found = "";
            foreach (var role in new[] { ETrackedControllerRole.RightHand, ETrackedControllerRole.LeftHand })
            {
                uint index = sys.GetTrackedDeviceIndexForControllerRole(role);
                if (index == OpenVR.k_unTrackedDeviceIndexInvalid || !sys.IsTrackedDeviceConnected(index)) continue;
                var sb = new System.Text.StringBuilder(64);
                var err = ETrackedPropertyError.TrackedProp_Success;
                sys.GetStringTrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_ControllerType_String, sb, (uint)sb.Capacity, ref err);
                if (err == ETrackedPropertyError.TrackedProp_Success && sb.Length > 0) { found = sb.ToString(); break; }
            }
            if (found != ControllerType)
            {
                ControllerType = found;
                bindingTries = 0;
                Plugin.L.LogInfo("Controllers: " + (found.Length > 0 ? found : "none"));
            }
            if (Ready && found.Length > 0 && bindingTries < 10) LogBindings();
        }

        private static int bindingTries;
        private static readonly uint BindingInfoSize = (uint)Marshal.SizeOf<InputBindingInfo_t>();

        /// <summary>Logs where the menu and map are bound. Retried, as the binding loads after the controller.</summary>
        private static void LogBindings()
        {
            bindingTries++;
            string menuAt, mapAt;
            try { menuAt = BoundTo(menu); mapAt = BoundTo(map); }
            catch (Exception) { bindingTries = int.MaxValue; return; } // an older SteamVR without binding info
            if (menuAt.Length == 0 && mapAt.Length == 0 && bindingTries < 10) return; // last try logs anyway
            bindingTries = int.MaxValue;
            Plugin.L.LogInfo($"Bindings ({ControllerType}): pause menu on {(menuAt.Length > 0 ? menuAt : "nothing")}, map on {(mapAt.Length > 0 ? mapAt : "nothing")}.");
            if (ControllerType == "playstation_vr2_sense" && mapAt.Contains("/input/create"))
                Plugin.L.LogInfo("The map is on Create, which the PS VR2 driver's SteamVR bindings also use for a screenshot, unless it was taken off there (see the README).");
        }

        /// <summary>e.g. "/user/hand/right/input/options (button, long)", empty if unbound.</summary>
        private static string BoundTo(ulong action)
        {
            if (action == 0) return "";
            var info = new InputBindingInfo_t[4];
            uint count = 0;
            if (OpenVR.Input.GetActionBindingInfo(action, info, BindingInfoSize, ref count) != EVRInputError.None || count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count && i < info.Length; i++)
            {
                if (sb.Length > 0) sb.Append(" and ");
                sb.Append(Text(info[i].rchDevicePathName)).Append(Text(info[i].rchInputPathName)).Append(" (").Append(Text(info[i].rchModeName));
                string slot = Text(info[i].rchSlotName);
                if (slot.Length > 0) sb.Append(", ").Append(slot);
                sb.Append(')');
            }
            return sb.ToString();
        }

        private static string Text(string fixedChars)
        {
            int end = fixedChars.IndexOf('\0');
            return end < 0 ? fixedChars : fixedChars.Substring(0, end);
        }

        /// <summary>Raw vibration, delay s from now (scheduled by SteamVR). Use Haptics.Pulse instead.</summary>
        public static void HapticAt(bool left, float delay, float seconds, float amplitude, float frequency)
        {
            if (!Ready || amplitude <= 0.005f) return;
            try
            {
                OpenVR.Input.TriggerHapticVibrationAction(left ? hapticL : hapticR, Mathf.Max(0f, delay), Mathf.Clamp(seconds, 0.005f, 1f),
                    Mathf.Clamp(frequency, 1f, 320f), Mathf.Clamp01(amplitude), OpenVR.k_ulInvalidInputValueHandle);
            }
            catch (Exception) { /* haptics are best effort */ }
        }
    }
}
