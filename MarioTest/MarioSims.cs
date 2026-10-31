using System;
using System.Collections.Generic;

namespace MarioTest
{
    // The live Mario sims on this machine: the player's own, and the shadow sims that play the
    // other players' Marios (MarioPuppet). Each sim only knows the ground its own FLUDD wetted,
    // so before every step a sim is told whether any other Mario's water is under it: Marios
    // glide over each other's wet spots, on every machine alike (the owner's sim and the shadow
    // sims of him make the same call, so they stay in step).
    internal static class MarioSims
    {
        static readonly HashSet<IntPtr> live = new HashSet<IntPtr>();
        // Whose Mario each sim plays (for whose water it is: MarioDeflect).
        static readonly Dictionary<IntPtr, RoR2.CharacterBody> owners = new Dictionary<IntPtr, RoR2.CharacterBody>();

        public static void Add(IntPtr sim, RoR2.CharacterBody body)
        {
            if (sim == IntPtr.Zero) return;
            live.Add(sim);
            owners[sim] = body;
        }
        public static void Remove(IntPtr sim) { live.Remove(sim); owners.Remove(sim); }
        public static void Clear() { live.Clear(); owners.Clear(); }
        public static int Count => live.Count;

        public static bool WaterTouches(UnityEngine.Vector3 a, UnityEngine.Vector3 b, float radius)
            => WaterTouches(a, b, radius, out _);

        /// Whether any Mario's drops touch the segment `a`-`b` (world), and whose (null for a sim
        /// whose body is gone).
        public static bool WaterTouches(UnityEngine.Vector3 a, UnityEngine.Vector3 b, float radius, out RoR2.CharacterBody mario)
        {
            mario = null;
            var from = UnityHost.ToSim(a);
            var to = UnityHost.ToSim(b);
            foreach (var sim in live)
                if (Native.sms_sim_water_touches_segment(sim, ref from, ref to, radius / UnityHost.Scale) != 0)
                {
                    owners.TryGetValue(sim, out mario);
                    return true;
                }
            return false;
        }

        /// Whether another Mario's water has wetted the ground where `sim`'s Mario stands.
        public static bool OthersWet(IntPtr sim)
        {
            if (live.Count < 2 || sim == IntPtr.Zero || Native.sms_sim_state(sim, out var state) == 0) return false;
            var at = state.pos;
            foreach (var other in live)
                if (other != sim && Native.sms_sim_wet_at(other, ref at) != 0) return true;
            return false;
        }

        /// Actual droplet/body contact, independent of the patches used by wet slides.
        public static bool OthersSoaking(IntPtr sim)
        {
            if (live.Count < 2 || sim == IntPtr.Zero || Native.sms_sim_state(sim, out var state) == 0) return false;
            var a = state.pos; a.y += 35f;
            var b = state.pos; b.y += 125f;
            foreach (var other in live)
                if (other != sim && Native.sms_sim_water_touches_segment(other, ref a, ref b, 35f) != 0)
                    return true;
            return false;
        }
    }
}
