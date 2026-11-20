using System.Globalization;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// One strafe-button press grabs one cockpit thing (wheel, lever, charm, cup): the nearest in reach of that hand.
    /// The press stays with it until release, and the game gets no strafe from it.
    /// </summary>
    internal static class GrabArbiter
    {
        public const int None = -1, Wheel = 0, Lever = 1, Charm = 2, Coffee = 3, Count = 4;
        private static readonly string[] Names = { "wheel", "lever", "charm", "coffee" };
        private static readonly float[] left = Far(), right = Far();
        private static int leftOwner = None, rightOwner = None;

        private static float[] Far()
        {
            var a = new float[Count];
            for (int i = 0; i < Count; i++) a[i] = float.PositiveInfinity;
            return a;
        }

        /// <summary>Hand distance in m, infinity when out of reach or not takeable.</summary>
        public static void Report(int thing, bool isLeft, float distance) =>
            (isLeft ? left : right)[thing] = float.IsNaN(distance) ? float.PositiveInfinity : distance;

        /// <summary>Neither hand can take this thing.</summary>
        public static void Clear(int thing) => left[thing] = right[thing] = float.PositiveInfinity;

        /// <summary>Takes the hand's press if it's free and nothing nearer is in reach. Ties go to the lower id.</summary>
        public static bool Claim(int thing, bool isLeft)
        {
            int owner = isLeft ? leftOwner : rightOwner;
            if (owner == thing) return true;
            if (owner != None) return false;
            var d = isLeft ? left : right;
            for (int i = 0; i < Count; i++)
            {
                if (i == thing || float.IsPositiveInfinity(d[i])) continue;
                if (d[i] < d[thing] || (d[i] == d[thing] && i < thing)) return false;
            }
            if (isLeft) leftOwner = thing;
            else rightOwner = thing;
            return true;
        }

        /// <summary>Button released: the press is free again.</summary>
        public static void Let(int thing, bool isLeft)
        {
            if (isLeft && leftOwner == thing) leftOwner = None;
            else if (!isLeft && rightOwner == thing) rightOwner = None;
        }

        /// <summary>The hand is in reach of some other thing (a holding hand that goes there is reaching for it).</summary>
        public static bool NearOther(int thing, bool isLeft)
        {
            var d = isLeft ? left : right;
            for (int i = 0; i < Count; i++)
                if (i != thing && !float.IsPositiveInfinity(d[i])) return true;
            return false;
        }

        public static bool Owns(int thing, bool isLeft) => (isLeft ? leftOwner : rightOwner) == thing;

        /// <summary>The press belongs to a cockpit thing, so the game gets no strafe.</summary>
        public static bool Taken(bool isLeft) => (isLeft ? leftOwner : rightOwner) != None;

        public static string Describe(bool isLeft)
        {
            var d = isLeft ? left : right;
            int owner = isLeft ? leftOwner : rightOwner;
            var sb = new System.Text.StringBuilder(owner == None ? "press free" : "press the " + Names[owner] + "'s");
            int near = 0;
            for (int i = 0; i < Count; i++)
            {
                if (float.IsPositiveInfinity(d[i])) continue;
                sb.Append(near++ == 0 ? ", in reach: " : ", ").Append(Names[i]).Append(' ').Append((d[i] * 100f).ToString("0.0", CultureInfo.InvariantCulture)).Append(" cm");
            }
            if (near == 0) sb.Append(", nothing in reach");
            return sb.ToString();
        }
    }
}
