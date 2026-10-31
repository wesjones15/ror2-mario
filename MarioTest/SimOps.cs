using System;

namespace MarioTest
{
    // The calls on a player's sim that change what it does for a step, each also recorded for the
    // other players' puppets while a recorder is attached (see MarioFrames). MarioDriver attaches
    // its recorder for the length of its physics step, so these must only be called inside it
    // (MarioItems.Begin and End are).
    internal static class SimOps
    {
        /// The recorder of the driver now stepping, or null (single player, or no step).
        public static FrameRecorder Rec;

        public static void Teleport(IntPtr sim, ref Native.Vec3 pos)
        {
            Native.sms_sim_teleport(sim, ref pos);
            Rec?.Teleport(pos);
        }

        public static void Tuning(IntPtr sim, ref Native.Tuning tuning)
        {
            Native.sms_sim_set_tuning(sim, ref tuning);
            Rec?.Tuning(tuning);
        }

        public static void Refill(IntPtr sim)
        {
            Native.sms_sim_refill(sim);
            Rec?.Refill();
        }

        public static void SelectNozzle(IntPtr sim, uint slot)
        {
            Native.sms_sim_select_nozzle(sim, slot);
            Rec?.SelectNozzle((byte)slot);
        }

        public static void AddWater(IntPtr sim, float share)
        {
            Native.sms_sim_add_water(sim, share);
            Rec?.AddWater(share);
        }

        public static void Limits(IntPtr sim, ref Native.Limits limits)
        {
            Native.sms_sim_set_limits(sim, ref limits);
            Rec?.Limits(limits);
        }

        public static void BoostJump(IntPtr sim, float factor)
        {
            Native.sms_sim_boost_jump(sim, factor);
            Rec?.BoostJump(factor);
        }

        public static void Launch(IntPtr sim, ref Native.Vec3 vel)
        {
            Native.sms_sim_launch(sim, ref vel);
            Rec?.Launch(vel);
        }

        public static void LaunchPad(IntPtr sim, ref Native.Vec3 vel, float gravity)
        {
            Native.sms_sim_launch_pad(sim, ref vel, gravity);
            Rec?.LaunchPad(vel, gravity);
        }

        public static byte KnockDown(IntPtr sim, ref Native.Vec3 from, byte kind)
        {
            byte result = Native.sms_sim_knock_down(sim, ref from, kind);
            Rec?.KnockDown(from, kind);
            return result;
        }

        public static byte Ignite(IntPtr sim)
        {
            byte result = Native.sms_sim_ignite(sim);
            Rec?.Ignite();
            return result;
        }

        /// Another Mario holds him (the silent fire-hop flail) or has let go.
        public static void SetCarried(IntPtr sim, bool on)
        {
            Native.sms_sim_set_carried(sim, (byte)(on ? 1 : 0));
            Rec?.Carried(on);
        }

        public static void SetAim(IntPtr sim, byte on, short yaw, float pitch)
        {
            Native.sms_sim_set_aim(sim, on, yaw, pitch);
            Rec?.SetAim(on != 0, yaw, pitch);
        }

        public static void FlightCamera(IntPtr sim, byte on, short yaw, float pitch)
        {
            Native.sms_sim_set_flight_camera(sim, on, yaw, pitch);
            Rec?.FlightCamera(on != 0, yaw, pitch);
        }

        public static void Down(IntPtr sim)
        {
            Native.sms_sim_down(sim);
            Rec?.Down();
        }

        /// He touches a Shine (the win pose); false if he was already in it or is down.
        public static bool ShineGet(IntPtr sim, ref Native.Vec3 pos, short yaw)
        {
            bool took = Native.sms_sim_shine_get(sim, ref pos, yaw) != 0;
            if (took) Rec?.ShineGet(pos, yaw);
            return took;
        }

        public static void ShineEnd(IntPtr sim)
        {
            Native.sms_sim_shine_end(sim);
            Rec?.ShineEnd();
        }

        /// A Melee A pose for the puppets: the lab sim stays on the owner, so the clip and frame
        /// travel with the step instead of being re-derived by every client.
        public static void Melee(byte move, float frame)
        {
            Rec?.Melee(move, frame);
        }

        public static uint Step(IntPtr sim, ref Native.Host host, ref Native.Pad pad, short cameraYaw, float dt, IntPtr view)
        {
            bool wet = MarioSims.OthersWet(sim);
            if (MarioSims.OthersSoaking(sim))
            {
                Rec?.Soaked();
                Native.sms_sim_wet(sim);
            }
            Rec?.ForeignWet(wet);
            Rec?.Step(pad, cameraYaw, dt);
            Native.sms_sim_set_foreign_wet(sim, wet ? 1u : 0u);
            return Native.sms_sim_step(sim, ref host, ref pad, cameraYaw, dt, view);
        }
    }
}
