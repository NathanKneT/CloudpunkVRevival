using CloudpunkVR.UI;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// A controller as a closed hand for cockpit grabs (wheel, lever): strafe button, grip pose in tracking space,
    /// and whether its tracking can be trusted this frame.
    /// </summary>
    internal class TrackedHand
    {
        private const float TipToGrip = 0.07f;   // m from tip pose back to the closed hand (no skeleton)
        private const float JumpSpeed = 4f;      // m/s: faster is a tracking jump
        private const float FrozenTime = 0.1f;   // s with zero motion: tracking froze
        private const float RecoverTime = 0.15f; // s of clean tracking before the hand is trusted again

        private static readonly Vector3[] joints = new Vector3[SteamVRInput.HandSkeleton.BoneCount];
        // palm frame per hand, computed once per frame for all readers
        private static readonly int[] frameAt = { -1, -1 };
        private static readonly bool[] frameValid = new bool[2];
        private static readonly HandFrame[] frames = new HandFrame[2];

        public readonly bool Left;
        public TrackedHand(bool left) { Left = left; }

        // tracking space: grip centre, finger direction, thumb side
        public bool Valid, Down, Pressed;
        public Vector3 Pos, Forward, ToIndex;
        public bool Healthy;

        private Vector3 lastPos;
        private bool haveLast;
        private float stillFor, cleanFor;

        /// <summary>Strafe button, and the closed hand around a bar of this radius (skeleton, else the tip pose).</summary>
        public void ReadController(float barRadius)
        {
            var grab = Left ? SteamVRInput.StrafeLeft : SteamVRInput.StrafeRight;
            Down = grab.Down;
            Pressed = grab.Pressed;
            Valid = false;
            if (SkeletonFrame(Left, out var f))
            {
                Valid = true;
                Pos = f.GripCentre(barRadius);
                Forward = f.Forward;
                ToIndex = f.ToIndex;
            }
            var pose = Left ? SteamVRInput.LeftHand : SteamVRInput.RightHand;
            if (!Valid && pose.Valid)
            {
                Valid = true;
                Forward = pose.Rotation * Vector3.forward;
                ToIndex = pose.Rotation * Vector3.up;
                Pos = pose.Position - Forward * TipToGrip;
            }
        }

        /// <summary>A simulated hand for dev commands. The button counts as held, never newly pressed.</summary>
        public void ReadOther(Vector3 grip, Quaternion rotation, bool down)
        {
            Valid = true;
            Pos = grip;
            Forward = rotation * Vector3.forward;
            ToIndex = rotation * Vector3.up;
            Down = down;
            Pressed = false;
        }

        /// <summary>Palm frame from the hand skeleton, tracking space. Cached per frame, shared by all readers.</summary>
        public static bool SkeletonFrame(bool left, out HandFrame f)
        {
            int i = left ? 0 : 1, now = Time.frameCount;
            if (frameAt[i] != now)
            {
                frameAt[i] = now;
                frameValid[i] = false;
                var s = left ? SteamVRInput.LeftSkeleton : SteamVRInput.RightSkeleton;
                if (s.Valid)
                {
                    for (int k = 1; k < joints.Length; k++) joints[k] = s.Position + s.Rotation * s.Joints[k];
                    frameValid[i] = HandFrame.From(joints, s.Rotation, left, out frames[i]);
                }
            }
            f = frames[i];
            return frameValid[i];
        }

        /// <summary>Healthy: tracked, not frozen (a pose repeated to the bit), no jumps, for RecoverTime.</summary>
        public void UpdateHealth(float dt)
        {
            if (!Valid)
            {
                Healthy = haveLast = false;
                cleanFor = 0f;
                return;
            }
            bool clean = true;
            if (haveLast)
            {
                float moved = (Pos - lastPos).magnitude;
                if (moved > JumpSpeed * Mathf.Max(dt, 1f / 120f)) clean = false;
                stillFor = moved < 1e-6f ? stillFor + dt : 0f;
                if (stillFor > FrozenTime) clean = false;
            }
            lastPos = Pos;
            haveLast = true;
            cleanFor = clean ? cleanFor + dt : 0f;
            Healthy = clean && cleanFor >= RecoverTime;
        }
    }
}
