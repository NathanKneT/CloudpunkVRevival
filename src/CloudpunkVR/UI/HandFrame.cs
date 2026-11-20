using UnityEngine;

namespace CloudpunkVR.UI
{
    /// <summary>A hand closed on a bar this frame: the wheel rim, or the lift lever (a rim of huge radius).</summary>
    internal struct GripPose
    {
        public float Weight;                        // 0 the hand's own pose .. 1 closed on the bar
        public Vector3 Point;                       // the bar's centreline under the palm
        public Vector3 Forward, ThumbSide, Outward; // across the bar, along it, away from rim centre
        public float TubeRadius, RimRadius;
    }

    /// <summary>Palm frame and size from SteamVR's hand skeleton, in whatever space the joints are given.</summary>
    internal struct HandFrame
    {
        public Vector3 Wrist, Forward, Back, ToIndex, Across;
        public float PalmLength, PalmWidth, PalmThickness, Scale;
        /// <summary>Skeleton palm faced the wrong way, controller axes used instead.</summary>
        public bool Flipped;

        /// <summary>x across the palm, y out of the back of the hand, z wrist to knuckles.</summary>
        public Quaternion Rotation => Quaternion.LookRotation(Forward, Back);

        // held bar sits under the palm a bit behind the knuckles, like in a fist
        private const float GripAlong = 0.7f, GripSqueeze = 0.002f;
        private static readonly int[] Knuckles = { 7, 12, 17, 22 };

        /// <summary>Held bar centre, in palm space.</summary>
        public static Vector3 GripLocal(float palmLength, float palmThickness, float barRadius) =>
            new Vector3(0f, -palmThickness * 0.5f - barRadius + GripSqueeze, palmLength * GripAlong);

        /// <summary>Held bar centre, in joint space.</summary>
        public Vector3 GripCentre(float barRadius) => Wrist + Rotation * GripLocal(PalmLength, PalmThickness, barRadius);

        /// <summary>
        /// From SteamVR's 31 joints (wrist 1, knuckles 7, 12, 17, 22). The back of the hand should face the
        /// controller's outer side. If the skeleton disagrees, the controller wins.
        /// </summary>
        public static bool From(Vector3[] j, Quaternion root, bool isLeft, out HandFrame f)
        {
            f = default;
            Vector3 wrist = j[1];
            Vector3 k1 = j[7], k4 = j[22];
            Vector3 knuckles = (j[7] + j[12] + j[17] + j[22]) * 0.25f;
            Vector3 fwd = knuckles - wrist;
            if (fwd.sqrMagnitude < 1e-8f) return false;
            fwd.Normalize();
            Vector3 toIndex = Vector3.ProjectOnPlane(k1 - k4, fwd);
            if (toIndex.sqrMagnitude < 1e-8f) return false;
            toIndex.Normalize();
            Vector3 back = isLeft ? Vector3.Cross(fwd, toIndex) : Vector3.Cross(toIndex, fwd);
            Vector3 outward = root * (isLeft ? Vector3.left : Vector3.right);
            bool flipped = Vector3.Dot(back, outward) < 0f;
            if (flipped) back = -back;

            float palmLength = 0f;
            foreach (int kj in Knuckles) palmLength = Mathf.Max(palmLength, Vector3.Dot(j[kj] - wrist, fwd));
            palmLength = Mathf.Max(palmLength + 0.004f, 0.05f);
            float scale = Mathf.Clamp(palmLength / 0.091f, 0.8f, 1.25f);
            f = new HandFrame
            {
                Wrist = wrist,
                Forward = fwd,
                Back = back,
                ToIndex = toIndex,
                Across = Vector3.Cross(back, fwd), // fingers curl about this axis, toward the palm
                PalmLength = palmLength,
                PalmWidth = Mathf.Abs(Vector3.Dot(k1 - k4, toIndex)) + 0.018f * scale,
                PalmThickness = 0.027f * scale,
                Scale = scale,
                Flipped = flipped,
            };
            return true;
        }
    }
}
