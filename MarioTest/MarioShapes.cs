using System;
using Ror2Common;
using UnityEngine;
namespace MarioTest
{
    /// Asset-independent presentation of the existing Mario/water simulation.
    internal sealed class MarioShapes : IDisposable
    {
        // libsms's POSE_* bits: the native silhouette for hosts that draw a stand-in.
        const byte PoseDive = 1, PoseSlide = 2, PosePound = 4, PoseSpin = 8;
        const float Radius = 0.35f;
        readonly PrimitiveShapes shapes = new PrimitiveShapes("Mario built-in shapes");
        readonly Native.WaterParticle[] water = new Native.WaterParticle[512];
        Native.State state;
        public void Draw(IntPtr sim, Vector3 feet, Quaternion facing, uint skin)
        {
            Native.sms_sim_state(sim, out state);
            shapes.Begin();
            Pose(feet, facing, MarioSkin.StandInColor(skin), UnityHost.MarioHeight * UnityHost.Scale);
            int count = (int)Math.Min((uint)water.Length, Native.sms_sim_water_particles(sim, water, (uint)water.Length));
            for (int i = 0; i < count; i++)
            {
                var p = water[i];
                var at = UnityHost.ToUnity(p.pos);
                var velocity = new Vector3(p.velocity.x, p.velocity.y, p.velocity.z) * UnityHost.Scale;
                shapes.Segment(at, at - velocity * 0.3f, Mathf.Clamp(p.radius * UnityHost.Scale, 0.025f, 0.15f), Color.cyan);
            }
            shapes.End();
        }
        /// Follows the native silhouettes: horizontal along a dive or belly slide, squashed in
        /// a ground pound, and a wider core around a spin jump or spinning pound.
        void Pose(Vector3 feet, Quaternion facing, Color color, float height)
        {
            if ((state.pose & PosePound) != 0) height *= 0.55f;
            if ((state.pose & (PoseDive | PoseSlide)) != 0)
            {
                bool slide = (state.pose & PoseSlide) != 0;
                var rotation = facing * Quaternion.Euler(90f, 0f, 0f);
                var center = feet + Vector3.up * (Radius * (slide ? 0.75f : 0.95f))
                    + facing * Vector3.forward * (height * 0.25f);
                shapes.Show(PrimitiveType.Cylinder, center,
                    new Vector3(Radius * 2f, height * 0.5f, Radius * 2f), rotation, color);
                shapes.Show(PrimitiveType.Sphere,
                    center + facing * Vector3.forward * (height * 0.4f) + Vector3.up * (Radius * 0.3f),
                    Vector3.one * (Radius * 0.35f), facing, Color.white);
            }
            else
            {
                shapes.Body(feet, facing, color, height, Radius);
            }
            if ((state.pose & PoseSpin) != 0)
            {
                // A cylinder body can't show its own rotation; this wide core does.
                shapes.Show(PrimitiveType.Cylinder, feet + Vector3.up * (height * 0.5f),
                    new Vector3(Radius * 4f, height * 0.2f, Radius * 4f), facing,
                    Color.Lerp(color, Color.white, 0.35f));
            }
        }

        public void Hud()
        {
            string nozzle = state.nozzle == 0 ? "Off" : state.nozzle == 1 ? "Spray" : state.nozzle == 2 ? "Rocket"
                : state.nozzle == 5 ? "Hover" : state.nozzle == 6 ? "Turbo" : "FLUDD";
            GUI.Label(new Rect(24, Screen.height - 110, 300, 30), $"FLUDD: {nozzle}   Water: {state.water:P0}");
        }
        public void Hide() => shapes.Hide();
        public void Dispose() => shapes.Dispose();
    }
}
