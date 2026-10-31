using System;
using UnityEngine;

namespace MarioTest
{
    // A coin's pop and bounces, worked out once when it appears (one ray for the ground under where it
    // lands) and then read off by time: no physics query per coin per step. The bounces are
    // TMapObjGeneral::touchGround's for coin_physical_data (MapObjInit.cpp): each one keeps half the
    // speed it landed with (0.5) and half its sideways speed (0.5), and the coin lies still once a
    // landing is too slow to bounce. Gravity and the pop's speed are this host's, sized to RoR2's world.
    internal struct CoinBounce
    {
        internal const float Gravity = 25f, Restitution = 0.5f, SideKeep = 0.5f, StillBelow = 1f;
        const int MaxFlights = 6;

        public Vector3 start;
        // Sideways speed in the first flight (each bounce halves it), and where the coin comes to rest.
        public float vx, vz, rest;
        // Vertical speed leaving the ground in flight i (flight 0: the pop from `start`), and when each ends.
        float v0, v1, v2, v3, v4, v5;
        float e0, e1, e2, e3, e4, e5;
        public int flights;

        /// Seconds until it lies still.
        public float Duration => flights > 0 ? End(flights - 1) : 0f;

        float Speed(int i) => i switch { 0 => v0, 1 => v1, 2 => v2, 3 => v3, 4 => v4, _ => v5 };
        float End(int i) => i switch { 0 => e0, 1 => e1, 2 => e2, 3 => e3, 4 => e4, _ => e5 };
        void Set(int i, float v, float e)
        {
            switch (i)
            {
                case 0: v0 = v; e0 = e; break;
                case 1: v1 = v; e1 = e; break;
                case 2: v2 = v; e2 = e; break;
                case 3: v3 = v; e3 = e; break;
                case 4: v4 = v; e4 = e; break;
                default: v5 = v; e5 = e; break;
            }
        }

        /// The path of a coin popped from `start` at `velocity`, coming to rest at height `rest`
        /// (the ground under where it lands, plus how high a coin sits); false if `rest` is above `start`
        /// by more than the pop can reach.
        public static bool Make(Vector3 start, Vector3 velocity, float rest, out CoinBounce path)
        {
            path = new CoinBounce { start = start, vx = velocity.x, vz = velocity.z, rest = rest };
            float up = velocity.y, drop = start.y - rest;
            // The first flight: from `start` with `up` until it comes down to `rest`.
            float disc = up * up + 2f * Gravity * drop;
            if (disc < 0f) return false;
            float t = (up + (float)Math.Sqrt(disc)) / Gravity;
            float landing = Math.Abs(up - Gravity * t);
            path.Set(0, up, t);
            path.flights = 1;
            for (int i = 1; i < MaxFlights; i++)
            {
                float v = landing * Restitution;
                if (v < StillBelow) break;
                t += 2f * v / Gravity;
                path.Set(i, v, t);
                path.flights = i + 1;
                landing = v;
            }
            return true;
        }

        /// Where it ends up sideways, from `start` (for the ground ray under where it lands).
        public Vector3 Landing()
        {
            float side = 0f, keep = 1f, from = 0f;
            for (int i = 0; i < flights; i++)
            {
                float end = End(i);
                side += keep * (end - from);
                keep *= SideKeep;
                from = end;
            }
            return new Vector3(vx * side, 0f, vz * side);
        }

        /// Where the coin is `time` seconds after it appeared, and how many times it has touched the ground.
        public Vector3 At(float time, out int landings)
        {
            landings = 0;
            float from = 0f, side = 0f, keep = 1f;
            for (int i = 0; i < flights; i++)
            {
                float end = End(i);
                if (time < end)
                {
                    float t = time - from;
                    float baseY = i == 0 ? start.y : rest;
                    float y = baseY + Speed(i) * t - 0.5f * Gravity * t * t;
                    float s = side + keep * t;
                    return new Vector3(start.x + vx * s, Math.Max(y, rest), start.z + vz * s);
                }
                side += keep * (end - from);
                keep *= SideKeep;
                from = end;
                landings = i + 1;
            }
            return new Vector3(start.x + vx * side, rest, start.z + vz * side);
        }
    }
}
