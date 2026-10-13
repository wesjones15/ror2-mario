using System;

namespace MarioTest
{
    // Host policy, in Mario body heights. Keep independent of world/model scaling.
    internal static class MarioFallDamage
    {
        public static float Fraction(float heights, int flags, float safeHeight,
            float diveSafeHeight, float perHeight, float diveMultiplier)
        {
            if (!Finite(heights) || !Finite(safeHeight) || !Finite(diveSafeHeight)
                || !Finite(perHeight) || !Finite(diveMultiplier)) return 0f;
            // libsms FallImpact: bit 1 diving, bit 2 pounding, bit 4 cushioned.
            if ((flags & 6) != 0) return 0f;
            bool diving = (flags & 1) != 0;
            float safe = Math.Max(0f, safeHeight);
            if (diving) safe = Math.Max(safe, diveSafeHeight);
            float fraction = Math.Min(0.9f, Math.Max(0f, heights - safe) * Math.Max(0f, perHeight));
            return fraction * (diving ? Math.Max(0f, Math.Min(1f, diveMultiplier)) : 1f);
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
